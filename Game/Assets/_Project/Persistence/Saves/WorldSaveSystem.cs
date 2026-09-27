using System;
using System.Collections.Generic;
using System.IO;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.World;
using HeroGame.Persistence.Json;
using HeroGame.Persistence.Storage;

namespace HeroGame.Persistence.Saves
{
    public sealed class SaveResult
    {
        public int ChunksWritten;
        public int ChunksSkipped;
        public long Generation;
        public long Bytes;
        public double Milliseconds;
    }

    public sealed class LoadResult
    {
        public World World;
        public int JournalEntriesReplayed;
        public int CorruptJournalEntries;
        public ValidationReport Report = new ValidationReport();
    }

    /// <summary>
    /// Incremental, crash-consistent world persistence (TDD §9).
    ///
    /// Layout of a save directory:
    ///   manifest.json               commit record (written last, atomically)
    ///   chunks/{name}.g{N}.json     chunk files, immutable once written
    ///   journal.log                 write-ahead journal of player/admin transactions
    ///
    /// Only dirty chunks are rewritten. A crash at any point leaves the previous manifest (and all
    /// chunk files it references) intact; player transactions committed after that snapshot are
    /// recovered from the journal.
    /// </summary>
    public sealed class WorldSaveSystem
    {
        /// <summary>2: NPCs split into population shards.</summary>
        public const int CurrentSchema = 2;
        public const string ManifestFile = "manifest.json";
        public const string JournalFile = "journal.log";
        public string GameVersion = "0.1.0";

        private readonly string _directory;

        public WorldSaveSystem(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(Path.Combine(directory, "chunks"));
        }

        public string JournalPath => Path.Combine(_directory, JournalFile);
        public bool Exists => File.Exists(Path.Combine(_directory, ManifestFile));

        public FileTransactionJournal OpenJournal() => new FileTransactionJournal(JournalPath);

        public SaveResult Save(World world, bool full = false)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (!world.Ledger.VerifyInvariant(out var sum))
                throw new InvalidOperationException("Refusing to save: ledger invariant broken (sum " + sum + "). Investigate before persisting.");

            var previous = ReadManifest();
            var manifest = new SaveManifest
            {
                GameVersion = GameVersion,
                ServerId = world.ServerId,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Generation = (previous?.Generation ?? 0) + 1,
                ChunkGenerations = previous != null ? new Dictionary<string, long>(previous.ChunkGenerations) : new Dictionary<string, long>(),
            };
            var result = new SaveResult { Generation = manifest.Generation };

            // Meta and transactional state are always written together: they must agree on time and journal sequence.
            var dirty = new HashSet<string>(world.Dirty.Chunks) { SaveChunks.Meta, SaveChunks.Transactional };
            foreach (var c in SaveChunks.WorldChunks)
                if (full || previous == null || !previous.ChunkGenerations.ContainsKey(c)) dirty.Add(c); // new chunk kinds after an upgrade
            if (world.Story != null && (full || previous == null || !previous.ChunkGenerations.ContainsKey(SaveChunks.Story))) dirty.Add(SaveChunks.Story);
            foreach (var id in world.Characters.Keys)
            {
                var key = SaveChunks.CharacterPrefix + id;
                if (full || previous == null || !manifest.ChunkGenerations.ContainsKey(key)) dirty.Add(key);
            }

            if (dirty.Contains(SaveChunks.Population))
                foreach (var shard in ShardsOf(world)) dirty.Add(SaveChunks.PopulationShard(shard));

            // Build payloads on the calling thread (they reference live objects, which nothing mutates during a
            // save), then serialize and write chunks in parallel: chunk files are independent until the manifest.
            var jobs = new List<KeyValuePair<string, object>>();
            foreach (var chunk in dirty)
            {
                var payload = BuildChunk(world, chunk);
                if (payload != null) jobs.Add(new KeyValuePair<string, object>(chunk, payload));
            }
            var sizes = new long[jobs.Count];
            var generation = manifest.Generation;
            System.Threading.Tasks.Parallel.For(0, jobs.Count, i =>
            {
                var json = JsonSetup.Serialize(jobs[i].Value);
                AtomicFile.WriteAllText(ChunkPath(jobs[i].Key, generation), json);
                sizes[i] = json.Length;
            });
            for (var i = 0; i < jobs.Count; i++)
            {
                manifest.ChunkGenerations[jobs[i].Key] = generation;
                result.ChunksWritten++;
                result.Bytes += sizes[i];
            }
            // Shards that no longer exist must not linger in the manifest.
            var liveShards = new HashSet<string>();
            foreach (var shard in ShardsOf(world)) liveShards.Add(SaveChunks.PopulationShard(shard));
            var stale = new List<string>();
            foreach (var key in manifest.ChunkGenerations.Keys)
                if (key.StartsWith(SaveChunks.PopulationShardPrefix, StringComparison.Ordinal) && !liveShards.Contains(key)) stale.Add(key);
            foreach (var key in stale) manifest.ChunkGenerations.Remove(key);
            result.ChunksSkipped = manifest.ChunkGenerations.Count - result.ChunksWritten;

            AtomicFile.WriteAllText(Path.Combine(_directory, ManifestFile), JsonSetup.Serialize(manifest, true));
            world.Dirty.Clear();
            CollectGarbage(manifest);
            result.Milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>Loads the last committed snapshot and replays the journal on top of it.</summary>
        public LoadResult Load(ContentSet content, FileTransactionJournal journal)
        {
            var manifest = ReadManifest() ?? throw new FileNotFoundException("No save manifest in " + _directory);
            if (manifest.SchemaVersion > CurrentSchema)
                throw new InvalidDataException("Save schema " + manifest.SchemaVersion + " is newer than this build supports (" + CurrentSchema + ").");

            var meta = ReadChunk<MetaChunk>(manifest, SaveChunks.Meta);
            var tx = ReadChunk<TransactionalChunk>(manifest, SaveChunks.Transactional);
            var ids = new IdAllocator { LastIssued = meta.IdsLastIssued };
            var clock = new WorldClock(meta.Now, meta.TimeScale);
            var env = ReadChunk<EnvironmentChunk>(manifest, SaveChunks.Environment);

            var world = new World(manifest.ServerId, meta.Config, content, journal, clock, ids, meta.Macro, env.Weather, tx.LastSequence)
            {
                Accounts = meta.Accounts,
                Cursor = meta.Cursor ?? new SimulationCursor { SimulatedUpTo = meta.Now },
            };

            foreach (var d in env.Districts) world.Geography.Add(d);
            foreach (var p in env.Places) world.Geography.Add(p);
            world.AnomalyLog.AddRange(env.AnomalyLog);

            foreach (var a in tx.Accounts) world.Ledger.Restore(a);
            foreach (var o in tx.Ownership) world.Ownership.AssignInitial(o.Asset, o.Owner);
            foreach (var l in tx.Loans) world.Loans.Restore(l);
            if (tx.Policies != null) foreach (var p in tx.Policies) world.Insurance.Restore(p);

            var pop = ReadChunk<PopulationChunk>(manifest, SaveChunks.Population);
            foreach (var h in pop.Households) world.Population.Add(h);
            foreach (var n in pop.Npcs) world.Population.Add(n); // schema 1
            var shards = new PopulationShardChunk[pop.Shards.Count];
            System.Threading.Tasks.Parallel.For(0, shards.Length, i => shards[i] = ReadChunk<PopulationShardChunk>(manifest, SaveChunks.PopulationShard(pop.Shards[i])));
            foreach (var shard in shards)
                foreach (var n in shard.Npcs) world.Population.Add(n);

            foreach (var p in ReadChunk<PropertiesChunk>(manifest, SaveChunks.Properties).Properties) world.Properties.Add(p);
            foreach (var b in ReadChunk<BusinessesChunk>(manifest, SaveChunks.Businesses).Businesses) world.Businesses[b.Id] = b;

            if (manifest.ChunkGenerations.ContainsKey(SaveChunks.Story))
                world.Story = ReadChunk<Core.Story.StoryState>(manifest, SaveChunks.Story);
            if (manifest.ChunkGenerations.ContainsKey(SaveChunks.Emergency))
                world.Emergency = ReadChunk<Core.Emergency.EmergencyState>(manifest, SaveChunks.Emergency);
            if (manifest.ChunkGenerations.ContainsKey(SaveChunks.Civic))
            {
                var civic = ReadChunk<CivicChunk>(manifest, SaveChunks.Civic);
                world.Civic = civic.Civic ?? new Core.Civic.CivicState();
                world.Disasters = civic.Disasters ?? new Core.Civic.DisasterState();
            }
            if (manifest.ChunkGenerations.ContainsKey(SaveChunks.Social))
                world.Ripple = ReadChunk<Core.Social.RippleState>(manifest, SaveChunks.Social) ?? new Core.Social.RippleState();
            if (manifest.ChunkGenerations.ContainsKey(SaveChunks.Justice))
                world.RestoreJustice(ReadChunk<Core.Crime.JusticeState>(manifest, SaveChunks.Justice));

            if (manifest.ChunkGenerations.ContainsKey(SaveChunks.Vehicles))
                foreach (var v in ReadChunk<VehiclesChunk>(manifest, SaveChunks.Vehicles).Vehicles) world.Vehicles.Restore(v);

            var history = ReadChunk<HistoryChunk>(manifest, SaveChunks.History);
            world.History.Major.AddRange(history.Major);
            world.History.Recent.AddRange(history.Recent);

            foreach (var idText in meta.CharacterIds)
            {
                var key = SaveChunks.CharacterPrefix + idText;
                if (!manifest.ChunkGenerations.ContainsKey(key)) continue;
                var ch = ReadChunk<ServerCharacter>(manifest, key);
                world.Characters[ch.CharacterId] = ch;
            }

            var result = new LoadResult { World = world };
            var entries = journal.ReadAfter(tx.LastSequence);
            result.CorruptJournalEntries = journal.CorruptEntries;
            result.JournalEntriesReplayed = world.Transactions.Replay(entries);
            if (result.JournalEntriesReplayed > 0) world.Dirty.Mark(SaveChunks.Transactional);
            world.EnsureInstitutions();
            if (result.CorruptJournalEntries > 0) result.Report.Warn("journal", result.CorruptJournalEntries + " unreadable journal entries skipped.");

            if (!world.Ledger.VerifyInvariant(out var sum)) result.Report.Error("ledger", "Invariant broken after load: sum " + sum);
            return result;
        }

        /// <summary>Removes journal entries that are now inside the snapshot.</summary>
        public void CompactJournal(World world, ITransactionJournal journal) => journal.Compact(world.Transactions.LastSequence);

        private object BuildChunk(World world, string chunk)
        {
            switch (chunk)
            {
                case SaveChunks.Meta:
                    var meta = new MetaChunk
                    {
                        Config = world.Config,
                        Now = world.Clock.Now,
                        TimeScale = world.Clock.TimeScale,
                        IdsLastIssued = world.Ids.LastIssued,
                        Accounts = world.Accounts,
                        Cursor = world.Cursor,
                        Macro = world.Macro,
                    };
                    foreach (var id in world.Characters.Keys) meta.CharacterIds.Add(id.ToString());
                    return meta;
                case SaveChunks.Transactional:
                    var t = new TransactionalChunk { LastSequence = world.Transactions.LastSequence };
                    t.Accounts.AddRange(world.Ledger.Accounts);
                    t.Ownership.AddRange(world.Ownership.Entries);
                    t.Loans.AddRange(world.Loans.Loans);
                    t.Policies.AddRange(world.Insurance.All);
                    t.Accounts.Sort((a, b) => a.Id.CompareTo(b.Id));
                    t.Ownership.Sort((a, b) => a.Asset.CompareTo(b.Asset));
                    return t;
                case SaveChunks.Population:
                    var pop = new PopulationChunk();
                    pop.Households.AddRange(world.Population.Households);
                    pop.Households.Sort((a, b) => a.Id.CompareTo(b.Id));
                    pop.Shards.AddRange(ShardsOf(world));
                    return pop;
                case SaveChunks.Properties:
                    var props = new PropertiesChunk();
                    props.Properties.AddRange(world.Properties.All);
                    props.Properties.Sort((a, b) => a.Id.CompareTo(b.Id));
                    return props;
                case SaveChunks.Businesses:
                    var biz = new BusinessesChunk();
                    biz.Businesses.AddRange(world.Businesses.Values);
                    biz.Businesses.Sort((a, b) => a.Id.CompareTo(b.Id));
                    return biz;
                case SaveChunks.Environment:
                    var env = new EnvironmentChunk { Weather = world.Weather.State };
                    env.Districts.AddRange(world.Geography.Districts);
                    env.Places.AddRange(world.Geography.Places);
                    env.Places.Sort((a, b) => a.Id.CompareTo(b.Id));
                    env.AnomalyLog.AddRange(world.AnomalyLog);
                    return env;
                case SaveChunks.Justice:
                    return world.Justice;
                case SaveChunks.Emergency:
                    return world.Emergency;
                case SaveChunks.Story:
                    return world.Story ?? new Core.Story.StoryState();
                case SaveChunks.Civic:
                    return new CivicChunk { Civic = world.Civic, Disasters = world.Disasters };
                case SaveChunks.Social:
                    return world.Ripple;
                case SaveChunks.Vehicles:
                    var vehicles = new VehiclesChunk();
                    vehicles.Vehicles.AddRange(world.Vehicles.All);
                    vehicles.Vehicles.Sort((a, b) => a.Id.CompareTo(b.Id));
                    return vehicles;
                case SaveChunks.History:
                    var h = new HistoryChunk();
                    h.Major.AddRange(world.History.Major);
                    h.Recent.AddRange(world.History.Recent);
                    return h;
                default:
                    if (chunk.StartsWith(SaveChunks.PopulationShardPrefix, StringComparison.Ordinal))
                    {
                        var index = int.Parse(chunk.Substring(SaveChunks.PopulationShardPrefix.Length), System.Globalization.CultureInfo.InvariantCulture);
                        var shard = new PopulationShardChunk { Shard = index };
                        foreach (var n in world.Population.Ordered) if (SaveChunks.ShardOf(n.Id) == index) shard.Npcs.Add(n);
                        return shard;
                    }
                    if (chunk.StartsWith(SaveChunks.CharacterPrefix, StringComparison.Ordinal)
                        && EntityId.TryParse(chunk.Substring(SaveChunks.CharacterPrefix.Length), out var cid)
                        && world.Characters.TryGetValue(cid, out var character))
                        return character;
                    return null;
            }
        }

        private static List<int> ShardsOf(World world)
        {
            var set = new SortedSet<int>();
            foreach (var n in world.Population.Ordered) set.Add(SaveChunks.ShardOf(n.Id));
            return new List<int>(set);
        }

        private SaveManifest ReadManifest()
        {
            var path = Path.Combine(_directory, ManifestFile);
            return File.Exists(path) ? JsonSetup.Deserialize<SaveManifest>(AtomicFile.ReadAllText(path)) : null;
        }

        private T ReadChunk<T>(SaveManifest manifest, string chunk) where T : class
        {
            if (!manifest.ChunkGenerations.TryGetValue(chunk, out var gen)) throw new InvalidDataException("Manifest has no chunk " + chunk);
            var path = ChunkPath(chunk, gen);
            if (!File.Exists(path)) throw new InvalidDataException("Missing chunk file " + path);
            return JsonSetup.Deserialize<T>(AtomicFile.ReadAllText(path));
        }

        private string ChunkPath(string chunk, long generation)
        {
            var safe = chunk.Replace('/', '_').Replace(':', '-');
            return Path.Combine(_directory, "chunks", safe + ".g" + generation + ".json");
        }

        private void CollectGarbage(SaveManifest manifest)
        {
            var keep = new HashSet<string>();
            foreach (var kv in manifest.ChunkGenerations) keep.Add(Path.GetFileName(ChunkPath(kv.Key, kv.Value)));
            foreach (var file in Directory.GetFiles(Path.Combine(_directory, "chunks")))
            {
                var name = Path.GetFileName(file);
                if (!keep.Contains(name) && !name.EndsWith(".tmp", StringComparison.Ordinal)) File.Delete(file);
            }
        }
    }
}
