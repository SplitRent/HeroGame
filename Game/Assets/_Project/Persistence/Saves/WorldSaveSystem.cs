using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
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
        /// <summary>Total time from snapshot to commit.</summary>
        public double Milliseconds;
        /// <summary>Time on the calling thread to take a consistent snapshot (payloads + serialization).</summary>
        public double SnapshotMilliseconds;
        /// <summary>Time to write, fsync and commit (a background thread with <see cref="WorldSaveSystem.SaveInBackground"/>).</summary>
        public double WriteMilliseconds;
    }

    public sealed class LoadResult
    {
        public World World;
        public int JournalEntriesReplayed;
        public int CorruptJournalEntries;
        /// <summary>The latest snapshot was damaged and the one before it was loaded instead (plus the journal).</summary>
        public bool RecoveredFromPreviousSnapshot;
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
    ///
    /// One generation back is also kept (manifest.prev.json, its chunks, and the journal since it), so a
    /// snapshot damaged after it was committed — a bad disk sector, a truncated copy — falls back to the one
    /// before it and replays the journal instead of losing the world.
    /// </summary>
    public sealed class WorldSaveSystem
    {
        /// <summary>2: NPCs split into population shards.</summary>
        public const int CurrentSchema = 2;
        public const string ManifestFile = "manifest.json";
        public const string PreviousManifestFile = "manifest.prev.json";
        public const string JournalFile = "journal.log";
        public string GameVersion = "0.1.0";

        private readonly string _directory;

        /// <summary>Fault injection for tests and tools: runs after chunks are written, before the manifest commits.</summary>
        public Action<SaveManifest> BeforeCommit;

        public WorldSaveSystem(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(Path.Combine(directory, "chunks"));
        }

        public string JournalPath => Path.Combine(_directory, JournalFile);
        public bool Exists => File.Exists(Path.Combine(_directory, ManifestFile));

        public FileTransactionJournal OpenJournal() => new FileTransactionJournal(JournalPath);

        /// <summary>
        /// The layout file the saved world was generated from, or null for a new world or an older save. Hosts use it so a
        /// world always reopens on its own map without the operator repeating --layout.
        /// </summary>
        public string RecordedLayoutFile()
        {
            foreach (var file in new[] { ManifestFile, PreviousManifestFile })
            {
                try
                {
                    var m = ReadManifest(file);
                    if (m != null) return string.IsNullOrEmpty(m.LayoutFile) ? null : m.LayoutFile;
                }
                catch (InvalidDataException) { }
            }
            return null;
        }

        /// <summary>Saves synchronously: snapshot, write, commit. The world's dirty set is cleared only on success.</summary>
        public SaveResult Save(World world, bool full = false)
        {
            WaitForBackgroundSave();
            var pending = Prepare(world, full);
            try
            {
                Commit(pending);
            }
            catch
            {
                _forceFullSave |= pending.Recovering;
                lock (_retry) _retry.UnionWith(pending.Dirty); // e.g. layout shards, whose change tracking already moved on
                throw;
            }
            world.Dirty.Clear();
            return pending.Result;
        }

        /// <summary>
        /// Takes a consistent snapshot on the calling thread (serialization only), then writes, fsyncs and commits it on
        /// a background thread, so the simulation thread pays only for serialization. Changes made while the write runs
        /// are tracked for the next save; if the write fails, its chunks are retried by the next save. Journal
        /// compaction must wait until the task completes (it touches the journal, which the simulation thread owns).
        /// </summary>
        public Task<SaveResult> SaveInBackground(World world, bool full = false)
        {
            WaitForBackgroundSave();
            var pending = Prepare(world, full);
            world.Dirty.Clear();
            _background = Task.Run(() =>
            {
                try
                {
                    Commit(pending);
                    return pending.Result;
                }
                catch
                {
                    lock (_retry) _retry.UnionWith(pending.Dirty);
                    if (pending.Recovering) _forceFullSave = true;
                    throw;
                }
            });
            return _background;
        }

        /// <summary>Blocks until a background save (if any) has committed or failed.</summary>
        public void WaitForBackgroundSave()
        {
            var t = _background;
            if (t == null) return;
            try { t.Wait(); }
            catch (AggregateException) { /* reported through the task; its chunks are retried */ }
            _background = null;
        }

        private Task<SaveResult> _background;
        private readonly HashSet<string> _retry = new HashSet<string>();

        private sealed class PendingSave
        {
            public SaveManifest Manifest;
            public SaveManifest Previous;
            public bool Recovering;
            public HashSet<string> Dirty;
            public List<KeyValuePair<string, string>> Chunks;
            /// <summary>Detached payloads (population shards of NPC snapshot copies) serialized by Commit, off the simulation thread.</summary>
            public List<KeyValuePair<string, object>> Deferred;
            public SaveResult Result;
            public System.Diagnostics.Stopwatch Watch;
        }

        private PendingSave Prepare(World world, bool full)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (!world.Ledger.VerifyInvariant(out var sum))
                throw new InvalidOperationException("Refusing to save: ledger invariant broken (sum " + sum + "). Investigate before persisting.");

            SaveManifest previous;
            try { previous = ReadManifest(ManifestFile); }
            catch (InvalidDataException) { previous = null; } // damaged: start a fresh full snapshot
            // After recovering from a damaged snapshot, write everything and keep the good previous one as the fallback.
            var recovering = _forceFullSave;
            if (previous == null || recovering) full = true;
            _forceFullSave = false;
            var manifest = new SaveManifest
            {
                GameVersion = GameVersion,
                ServerId = world.ServerId,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Generation = (previous?.Generation ?? 0) + 1,
                ChunkGenerations = previous != null ? new Dictionary<string, long>(previous.ChunkGenerations) : new Dictionary<string, long>(),
                LayoutId = world.Content.Layout.Id,
                LayoutFile = world.Content.LayoutFile,
            };
            var result = new SaveResult { Generation = manifest.Generation };

            // Meta and transactional state are always written together: they must agree on time and journal sequence.
            var dirty = new HashSet<string>(world.Dirty.Chunks) { SaveChunks.Meta, SaveChunks.Transactional };
            lock (_retry)
            {
                dirty.UnionWith(_retry);
                _retry.Clear();
            }
            foreach (var c in SaveChunks.WorldChunks)
                if (full || previous == null || !previous.ChunkGenerations.ContainsKey(c)) dirty.Add(c); // new chunk kinds after an upgrade
            if (world.Story != null && (full || previous == null || !previous.ChunkGenerations.ContainsKey(SaveChunks.Story))) dirty.Add(SaveChunks.Story);
            foreach (var id in world.Characters.Keys)
            {
                var key = SaveChunks.CharacterPrefix + id;
                if (full || previous == null || !manifest.ChunkGenerations.ContainsKey(key)) dirty.Add(key);
            }

            _shards = ShardsOf(world); // once per save: it walks the whole population
            if (dirty.Contains(SaveChunks.Population))
                foreach (var shard in _shards) dirty.Add(SaveChunks.PopulationShard(shard));

            // Building layouts are copy-on-write, so a shard needs rewriting only when one of its layouts was replaced
            // since the last save (or it has never been written).
            for (var s = 0; s < SaveChunks.LayoutShards; s++)
                if (full || previous == null || !previous.ChunkGenerations.ContainsKey(SaveChunks.LayoutShard(s))) dirty.Add(SaveChunks.LayoutShard(s));
            foreach (var p in world.Properties.All)
            {
                _savedLayouts.TryGetValue(p.Id, out var saved);
                if (!ReferenceEquals(saved, p.Layout)) dirty.Add(SaveChunks.LayoutShard(SaveChunks.LayoutShardOf(p.Id)));
            }

            // Build payloads on the calling thread (they reference live objects, which nothing mutates while this runs),
            // and serialize them in parallel. The population, by far the largest part, is instead copied into detached
            // snapshot records (a fraction of the cost of serializing it) and serialized by Commit, which a background
            // save runs on another thread. After this point nothing refers to live world objects.
            var jobs = new List<KeyValuePair<string, object>>();
            var deferred = new List<KeyValuePair<string, object>>();
            var shards = new Dictionary<int, PopulationShardChunk>();
            var layoutShards = new Dictionary<int, LayoutShardChunk>();
            foreach (var chunk in dirty)
            {
                if (chunk.StartsWith(SaveChunks.LayoutShardPrefix, StringComparison.Ordinal))
                {
                    var index = int.Parse(chunk.Substring(SaveChunks.LayoutShardPrefix.Length), System.Globalization.CultureInfo.InvariantCulture);
                    var shard = new LayoutShardChunk { Shard = index };
                    layoutShards[index] = shard;
                    deferred.Add(new KeyValuePair<string, object>(chunk, shard));
                    continue;
                }
                if (chunk.StartsWith(SaveChunks.PopulationShardPrefix, StringComparison.Ordinal))
                {
                    var index = int.Parse(chunk.Substring(SaveChunks.PopulationShardPrefix.Length), System.Globalization.CultureInfo.InvariantCulture);
                    var shard = new PopulationShardChunk { Shard = index };
                    shards[index] = shard;
                    deferred.Add(new KeyValuePair<string, object>(chunk, shard));
                    continue;
                }
                var payload = BuildChunk(world, chunk);
                if (payload == null) continue;
                // Detach the payload from the live world (households are already copies) so Commit can serialize it
                // on the save thread while the simulation carries on.
                deferred.Add(new KeyValuePair<string, object>(chunk, payload));
            }
            // Nothing mutates the world while this runs, so the copies can be taken on every core: payloads are
            // detached from the live world (households and NPCs are copied by hand, everything else by the cloner)
            // so Commit can serialize them on the save thread while the simulation carries on.
            var copies = new object[deferred.Count];
            System.Threading.Tasks.Parallel.For(0, deferred.Count, i =>
            {
                var d = deferred[i];
                copies[i] = d.Key == SaveChunks.Population || d.Key.StartsWith(SaveChunks.PopulationShardPrefix, StringComparison.Ordinal)
                            || d.Key.StartsWith(SaveChunks.LayoutShardPrefix, StringComparison.Ordinal)
                    ? d.Value
                    : SnapshotCloner.Clone(d.Value);
            });
            for (var i = 0; i < deferred.Count; i++) deferred[i] = new KeyValuePair<string, object>(deferred[i].Key, copies[i]);
            if (shards.Count > 0)
            {
                var ordered = world.Population.Ordered;
                var shardList = new List<PopulationShardChunk>(shards.Values);
                var byShard = new Dictionary<int, List<NpcRecord>>();
                foreach (var n in ordered)
                    if (shards.ContainsKey(SaveChunks.ShardOf(n.Id)))
                    {
                        var key = SaveChunks.ShardOf(n.Id);
                        if (!byShard.TryGetValue(key, out var list)) byShard[key] = list = new List<NpcRecord>();
                        list.Add(n);
                    }
                System.Threading.Tasks.Parallel.For(0, shardList.Count, i =>
                {
                    var target = shardList[i];
                    if (!byShard.TryGetValue(target.Shard, out var members)) return;
                    target.Npcs.Capacity = members.Count;
                    foreach (var n in members) target.Npcs.Add(n.SnapshotCopy());
                });
            }
            if (layoutShards.Count > 0)
            {
                var ordered = new List<Core.Property.PropertyRecord>(world.Properties.All);
                ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
                foreach (var p in ordered)
                {
                    if (!layoutShards.TryGetValue(SaveChunks.LayoutShardOf(p.Id), out var target)) continue;
                    // Immutable once assigned (copy-on-write), so Commit may serialize it on another thread.
                    if (p.Layout != null) target.Layouts.Add(new PropertyLayout { Property = p.Id, Layout = p.Layout });
                    _savedLayouts[p.Id] = p.Layout;
                }
            }
            foreach (var d in deferred)
            {
                manifest.ChunkGenerations[d.Key] = manifest.Generation;
                result.ChunksWritten++;
            }
            var json = new string[jobs.Count];
            System.Threading.Tasks.Parallel.For(0, jobs.Count, i => json[i] = JsonSetup.Serialize(jobs[i].Value));
            var chunks = new List<KeyValuePair<string, string>>(jobs.Count);
            for (var i = 0; i < jobs.Count; i++)
            {
                chunks.Add(new KeyValuePair<string, string>(jobs[i].Key, json[i]));
                manifest.ChunkGenerations[jobs[i].Key] = manifest.Generation;
                result.ChunksWritten++;
                result.Bytes += json[i].Length;
            }
            // Shards that no longer exist must not linger in the manifest.
            var liveShards = new HashSet<string>();
            foreach (var shard in _shards) liveShards.Add(SaveChunks.PopulationShard(shard));
            var stale = new List<string>();
            foreach (var key in manifest.ChunkGenerations.Keys)
                if (key.StartsWith(SaveChunks.PopulationShardPrefix, StringComparison.Ordinal) && !liveShards.Contains(key)) stale.Add(key);
            foreach (var key in stale) manifest.ChunkGenerations.Remove(key);
            result.ChunksSkipped = manifest.ChunkGenerations.Count - result.ChunksWritten;
            manifest.JournalSequence = world.Transactions.LastSequence;
            result.SnapshotMilliseconds = watch.Elapsed.TotalMilliseconds;
            return new PendingSave { Manifest = manifest, Previous = previous, Recovering = recovering, Dirty = dirty, Chunks = chunks, Deferred = deferred, Result = result, Watch = watch };
        }

        private void Commit(PendingSave p)
        {
            var write = System.Diagnostics.Stopwatch.StartNew();
            var generation = p.Manifest.Generation;
            if (p.Deferred.Count > 0)
            {
                foreach (var d in p.Deferred)
                    if (d.Value is TransactionalChunk tx)
                    {
                        tx.Accounts.Sort((a, b) => a.Id.CompareTo(b.Id));
                        tx.Ownership.Sort((a, b) => a.Asset.CompareTo(b.Asset));
                    }
                var json = new string[p.Deferred.Count];
                System.Threading.Tasks.Parallel.For(0, json.Length, i => json[i] = JsonSetup.Serialize(p.Deferred[i].Value));
                for (var i = 0; i < json.Length; i++)
                {
                    p.Chunks.Add(new KeyValuePair<string, string>(p.Deferred[i].Key, json[i]));
                    p.Result.Bytes += json[i].Length;
                }
                p.Deferred.Clear();
            }
            System.Threading.Tasks.Parallel.For(0, p.Chunks.Count, i => AtomicFile.WriteAllText(ChunkPath(p.Chunks[i].Key, generation), p.Chunks[i].Value));
            BeforeCommit?.Invoke(p.Manifest);
            if (p.Previous != null && !p.Recovering) AtomicFile.WriteAllText(Path.Combine(_directory, PreviousManifestFile), JsonSetup.Serialize(p.Previous, true));
            AtomicFile.WriteAllText(Path.Combine(_directory, ManifestFile), JsonSetup.Serialize(p.Manifest, true));
            SaveManifest fallback;
            try { fallback = ReadManifest(PreviousManifestFile); }
            catch (InvalidDataException) { fallback = null; }
            CollectGarbage(p.Manifest, fallback);
            p.Result.WriteMilliseconds = write.Elapsed.TotalMilliseconds;
            p.Result.Milliseconds = p.Watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Loads the last committed snapshot and replays the journal on top of it. If that snapshot is damaged, the
        /// previous one is loaded instead (the journal still holds every transaction since it) and the next save is
        /// a full one.
        /// </summary>
        public LoadResult Load(ContentSet content, FileTransactionJournal journal)
        {
            SaveManifest manifest = null;
            InvalidDataException damage = null;
            try
            {
                manifest = ReadManifest(ManifestFile);
                if (manifest == null && !File.Exists(Path.Combine(_directory, PreviousManifestFile))) throw new FileNotFoundException("No save manifest in " + _directory);
                if (manifest != null) return LoadFrom(manifest, content, journal);
            }
            catch (InvalidDataException ex)
            {
                if (ex.Message.StartsWith("Save schema", StringComparison.Ordinal)) throw;
                damage = ex;
            }
            var previous = ReadManifest(PreviousManifestFile);
            if (previous == null || manifest != null && previous.Generation >= manifest.Generation)
                throw damage ?? new InvalidDataException("The save manifest is missing and there is no earlier snapshot.");
            var result = LoadFrom(previous, content, journal);
            result.RecoveredFromPreviousSnapshot = true;
            result.Report.Warn("save", "Snapshot " + (manifest != null ? manifest.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?") + " is damaged (" +
                                       (damage != null ? damage.Message : "manifest missing") + "); recovered snapshot " + previous.Generation + " and replayed the journal.");
            var w = result.World;
            w.Dirty.MarkAll(SaveChunks.WorldChunks);
            foreach (var id in w.Characters.Keys) w.Dirty.Mark(SaveChunks.CharacterPrefix + id);
            _forceFullSave = true;
            return result;
        }

        private volatile bool _forceFullSave;
        /// <summary>The layout object each property had when its shard was last written (owning thread only).</summary>
        private readonly Dictionary<EntityId, Core.Building.BuildingLayout> _savedLayouts = new Dictionary<EntityId, Core.Building.BuildingLayout>();

        private LoadResult LoadFrom(SaveManifest manifest, ContentSet content, FileTransactionJournal journal)
        {
            if (manifest.SchemaVersion > CurrentSchema)
                throw new InvalidDataException("Save schema " + manifest.SchemaVersion + " is newer than this build supports (" + CurrentSchema + ").");

            if (!string.IsNullOrEmpty(manifest.LayoutId) && manifest.LayoutId != content.Layout.Id)
                throw new LayoutMismatchException("This world was built on the '" + manifest.LayoutId + "' city layout (" + manifest.LayoutFile + ") but '" + content.Layout.Id +
                                               "' was loaded; open it with that layout.");
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
            // Layout shards (saves before them kept layouts inline in the properties chunk, which still loads).
            var layoutKeys = new List<string>();
            foreach (var key in manifest.ChunkGenerations.Keys)
                if (key.StartsWith(SaveChunks.LayoutShardPrefix, StringComparison.Ordinal)) layoutKeys.Add(key);
            var layouts = new LayoutShardChunk[layoutKeys.Count];
            System.Threading.Tasks.Parallel.For(0, layouts.Length, i => layouts[i] = ReadChunk<LayoutShardChunk>(manifest, layoutKeys[i]));
            foreach (var shard in layouts)
                foreach (var l in shard.Layouts)
                {
                    var property = world.Properties.Get(l.Property);
                    if (property != null) property.Layout = l.Layout;
                }
            _savedLayouts.Clear();
            foreach (var p in world.Properties.All) _savedLayouts[p.Id] = p.Layout;
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
            if (manifest.ChunkGenerations.ContainsKey(SaveChunks.Destruction))
                world.Destruction = ReadChunk<DestructionState>(manifest, SaveChunks.Destruction) ?? new DestructionState();
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

        /// <summary>
        /// Removes journal entries already inside the <em>previous</em> snapshot: entries since then are kept so that
        /// snapshot stays recoverable if the latest one is ever damaged.
        /// </summary>
        public void CompactJournal(World world, ITransactionJournal journal)
        {
            SaveManifest previous;
            try { previous = ReadManifest(PreviousManifestFile); }
            catch (InvalidDataException) { previous = null; }
            var upTo = previous != null ? Math.Min(previous.JournalSequence, world.Transactions.LastSequence) : world.Transactions.LastSequence;
            if (upTo > 0) journal.Compact(upTo);
        }

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
                    return t; // sorted on the save thread (Commit)
                case SaveChunks.Population:
                    var pop = new PopulationChunk();
                    foreach (var household in world.Population.Households) pop.Households.Add(household.SnapshotCopy());
                    pop.Households.Sort((a, b) => a.Id.CompareTo(b.Id));
                    pop.Shards.AddRange(_shards ?? ShardsOf(world));
                    return pop;
                case SaveChunks.Properties:
                    var props = new PropertiesChunk();
                    foreach (var p in world.Properties.All)
                    {
                        var copy = p.ShallowCopy();
                        copy.Layout = null; // saved in the layout shards
                        props.Properties.Add(copy);
                    }
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
                    world.Justice.Wanted = world.Wanted.Snapshot();
                    return world.Justice;
                case SaveChunks.Emergency:
                    return world.Emergency;
                case SaveChunks.Story:
                    return world.Story ?? new Core.Story.StoryState();
                case SaveChunks.Civic:
                    return new CivicChunk { Civic = world.Civic, Disasters = world.Disasters };
                case SaveChunks.Social:
                    return world.Ripple;
                case SaveChunks.Destruction:
                    return world.Destruction;
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
                    if (chunk.StartsWith(SaveChunks.CharacterPrefix, StringComparison.Ordinal)
                        && EntityId.TryParse(chunk.Substring(SaveChunks.CharacterPrefix.Length), out var cid)
                        && world.Characters.TryGetValue(cid, out var character))
                        return character;
                    return null;
            }
        }

        /// <summary>Population shards of the save being prepared (owning thread only).</summary>
        private List<int> _shards;

        private static List<int> ShardsOf(World world)
        {
            var set = new SortedSet<int>();
            foreach (var n in world.Population.Ordered) set.Add(SaveChunks.ShardOf(n.Id));
            return new List<int>(set);
        }

        private SaveManifest ReadManifest(string file = ManifestFile)
        {
            var path = Path.Combine(_directory, file);
            if (!File.Exists(path)) return null;
            SaveManifest m;
            try { m = JsonSetup.Deserialize<SaveManifest>(AtomicFile.ReadAllText(path)); }
            catch (Newtonsoft.Json.JsonException ex) { throw new InvalidDataException("Manifest " + file + " is corrupt: " + ex.Message, ex); }
            if (m == null || m.ChunkGenerations == null) throw new InvalidDataException("Manifest " + file + " is empty.");
            return m;
        }

        private T ReadChunk<T>(SaveManifest manifest, string chunk) where T : class
        {
            if (!manifest.ChunkGenerations.TryGetValue(chunk, out var gen)) throw new InvalidDataException("Manifest has no chunk " + chunk);
            var path = ChunkPath(chunk, gen);
            if (!File.Exists(path)) throw new InvalidDataException("Missing chunk file " + path);
            T value;
            try { value = JsonSetup.Deserialize<T>(AtomicFile.ReadAllText(path)); }
            catch (Newtonsoft.Json.JsonException ex) { throw new InvalidDataException("Chunk " + chunk + " is corrupt: " + ex.Message, ex); }
            return value ?? throw new InvalidDataException("Chunk " + chunk + " is empty.");
        }

        private string ChunkPath(string chunk, long generation)
        {
            var safe = chunk.Replace('/', '_').Replace(':', '-');
            return Path.Combine(_directory, "chunks", safe + ".g" + generation + ".json");
        }

        private void CollectGarbage(SaveManifest manifest, SaveManifest previous)
        {
            var keep = new HashSet<string>();
            foreach (var kv in manifest.ChunkGenerations) keep.Add(Path.GetFileName(ChunkPath(kv.Key, kv.Value)));
            if (previous != null) foreach (var kv in previous.ChunkGenerations) keep.Add(Path.GetFileName(ChunkPath(kv.Key, kv.Value)));
            foreach (var file in Directory.GetFiles(Path.Combine(_directory, "chunks")))
            {
                var name = Path.GetFileName(file);
                if (!keep.Contains(name) && !name.EndsWith(".tmp", StringComparison.Ordinal)) File.Delete(file);
            }
        }
    }
}
