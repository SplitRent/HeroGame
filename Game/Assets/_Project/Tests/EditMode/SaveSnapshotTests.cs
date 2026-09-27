using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HeroGame.Core.Building;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Simulation;
using HeroGame.Persistence.Json;
using HeroGame.Persistence.Saves;
using NUnit.Framework;

namespace HeroGame.Tests
{
    /// <summary>
    /// Saves copy the population and reuse copy-on-write building layouts so the heavy serialization runs off the
    /// simulation thread. These tests keep those copies honest.
    /// </summary>
    public class SaveSnapshotTests
    {
        /// <summary>Types that are immutable once created and may be shared between a record and its snapshot.</summary>
        private static readonly HashSet<Type> Shared = new HashSet<Type> { typeof(string), typeof(LifeEvent), typeof(PowerDefinition), typeof(BuildingLayout) };

        /// <summary>Walks both object graphs in step and fails if any mutable reference object is shared.</summary>
        private static void AssertDetached(object original, object copy, string path)
        {
            if (original == null) return;
            var type = original.GetType();
            if (type.IsPrimitive || type.IsEnum || Shared.Contains(type)) return;
            if (!type.IsValueType) Assert.IsFalse(ReferenceEquals(original, copy), path + " is shared between the record and its snapshot");
            if (original is IList list)
            {
                var other = (IList)copy;
                Assert.AreEqual(list.Count, other.Count, path + ".Count");
                for (var i = 0; i < list.Count; i++) AssertDetached(list[i], other[i], path + "[" + i + "]");
                return;
            }
            if (original is IDictionary map)
            {
                // Compare entries, not the collection's internals (comparers are meant to be shared).
                var other = (IDictionary)copy;
                Assert.AreEqual(map.Count, other.Count, path + ".Count");
                foreach (DictionaryEntry e in map) AssertDetached(e.Value, other[e.Key], path + "[" + e.Key + "]");
                return;
            }
            if (original is IEnumerable sequence && type.Namespace != null && type.Namespace.StartsWith("System", StringComparison.Ordinal))
            {
                var a = sequence.Cast<object>().ToList();
                var b = ((IEnumerable)copy).Cast<object>().ToList();
                Assert.AreEqual(a.Count, b.Count, path + ".Count");
                for (var i = 0; i < a.Count; i++) AssertDetached(a[i], b[i], path + "[" + i + "]");
                return;
            }
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.IsInitOnly && f.FieldType.IsValueType) continue;
                AssertDetached(f.GetValue(original), f.GetValue(copy), path + "." + f.Name);
            }
        }

        private static NpcRecord FullyPopulated(World world)
        {
            var npc = world.Population.Ordered.First(n => n.Relationships.Count > 0);
            npc.Interests.Add("fishing");
            npc.FavoritePlaces.Add(world.Geography.Places.First().Id);
            npc.MemoryOf(EntityId.Create(EntityKind.Character, 5), true, world.Today).Trust = 0.4f;
            npc.AddHistory(world.Today, "test", "Something happened.");
            npc.Powers = new CharacterPowers();
            npc.Powers.Powers.Add(new PowerInstance { Stage = PowerStage.Mastered, Progress = new PowerProgress { Experience = 10f, UnlockedEvolutions = { "reach" } } });
            npc.Powers.Active.Add(new ActivePowerEffect { Kind = EffectKind.SpeedBoost, Amount = 2f, UntilSecond = 99 });
            return npc;
        }

        [Test]
        public void NpcAndHouseholdSnapshots_ShareNothingMutable_AndSerializeIdentically()
        {
            var world = WorldGenerator.Create("snapshot", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            var npc = FullyPopulated(world);
            var copy = npc.SnapshotCopy();
            AssertDetached(npc, copy, "NpcRecord");
            Assert.AreEqual(JsonSetup.Serialize(npc), JsonSetup.Serialize(copy));

            // Mutating the live record afterwards leaves the snapshot as it was.
            var before = JsonSetup.Serialize(copy);
            npc.Relationships[0].Affinity = -1f;
            npc.Powers.Powers[0].Progress.UnlockedEvolutions.Add("more");
            npc.Memories[0].Trust = 1f;
            npc.AddHistory(world.Today + 1, "later", "Later.");
            Assert.AreEqual(before, JsonSetup.Serialize(copy));

            var household = world.Population.Households.First();
            var h = household.SnapshotCopy();
            AssertDetached(household, h, "Household");
            household.Members.Add(npc.Id);
            Assert.AreNotEqual(household.Members.Count, h.Members.Count);
        }

        [Test]
        public void TheGenericCloner_CopiesEverySavedKindOfData_Faithfully_AndSharesNothingMutable()
        {
            var world = WorldGenerator.Create("cloner", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            var me = world.CreateCharacter(new Core.Characters.AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 3), Character = new Core.Characters.CharacterIdentity { FirstName = "Ines" } }, new WorldPosition());
            me.Inventory.Add(new Core.Characters.InventoryStack { ItemId = "laptop", Quantity = 2 });
            new WorldSimulation(world).AdvanceDays(5); // businesses report, emergencies happen, history is written
            world.Justice.Wanted = world.Wanted.Snapshot();

            var payloads = new List<(string name, object value)>
            {
                ("properties", world.Properties.All.ToList()),
                ("businesses", world.Businesses.Values.ToList()),
                ("places", world.Geography.Places.ToList()),
                ("districts", world.Geography.Districts.ToList()),
                ("accounts", world.Ledger.Accounts.ToList()),
                ("ownership", world.Ownership.Entries.ToList()),
                ("loans", world.Loans.Loans.ToList()),
                ("policies", world.Insurance.All.ToList()),
                ("civic", world.Civic),
                ("emergency", world.Emergency),
                ("justice", world.Justice),
                ("ripple", world.Ripple),
                ("destruction", world.Destruction),
                ("vehicles", world.Vehicles.All.ToList()),
                ("history", world.History.Recent.ToList()),
                ("character", me),
                ("macro", world.Macro),
                ("weather", world.Weather.State),
            };
            foreach (var (name, value) in payloads)
            {
                var copy = SnapshotCloner.Clone(value);
                Assert.AreEqual(JsonSetup.Serialize(value), JsonSetup.Serialize(copy), name + " serializes identically");
                AssertDetached(value, copy, name);
            }

            // And a mutation after the copy does not reach it.
            var business = world.Businesses.Values.First();
            var snapshot = SnapshotCloner.Clone(business);
            var json = JsonSetup.Serialize(snapshot);
            business.Name += " (renamed)";
            business.Reports.Clear();
            Assert.AreEqual(json, JsonSetup.Serialize(snapshot));
        }

        private static Dictionary<string, long> Generations(string dir)
        {
            var manifest = JsonSetup.Deserialize<SaveManifest>(File.ReadAllText(Path.Combine(dir, WorldSaveSystem.ManifestFile)));
            return manifest.ChunkGenerations;
        }

        [Test]
        public void EditingOneBuilding_RewritesOnlyItsLayoutShard_AndLayoutsSurviveTheRoundTrip()
        {
            var dir = TestContent.TempDirectory("layouts");
            var saves = new WorldSaveSystem(dir);
            EntityId edited;
            int furniture;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("layouts", TestContent.DefaultConfig(), TestContent.Load(), journal);
                saves.Save(world, full: true);
                var first = Generations(dir);
                for (var s = 0; s < SaveChunks.LayoutShards; s++) Assert.IsTrue(first.ContainsKey(SaveChunks.LayoutShard(s)));

                // A routine save with no building work leaves every layout shard alone.
                world.Dirty.Mark(SaveChunks.Properties);
                saves.Save(world);
                var second = Generations(dir);
                for (var s = 0; s < SaveChunks.LayoutShards; s++) Assert.AreEqual(first[SaveChunks.LayoutShard(s)], second[SaveChunks.LayoutShard(s)]);

                // A build replaces the layout (copy-on-write, as ConstructionService.Commit does).
                var property = world.Properties.All.OrderBy(p => p.Id).First(p => p.Layout != null);
                edited = property.Id;
                var layout = property.Layout.Clone();
                layout.Furniture.Add(new PlacedFurniture { Id = layout.NextId++, CatalogId = "structural_column", X = 3f, Z = 3f });
                furniture = layout.Furniture.Count;
                property.Layout = layout;
                var result = saves.Save(world);
                var third = Generations(dir);
                var rewritten = Enumerable.Range(0, SaveChunks.LayoutShards).Where(s => third[SaveChunks.LayoutShard(s)] == result.Generation).ToList();
                CollectionAssert.AreEqual(new[] { SaveChunks.LayoutShardOf(edited) }, rewritten, "only the edited building's shard");

                // A save that dies before committing must not lose the next edit: the following save still writes it.
                var again = property.Layout.Clone();
                again.Furniture.Add(new PlacedFurniture { Id = again.NextId++, CatalogId = "structural_column", X = 5f, Z = 5f });
                furniture = again.Furniture.Count;
                property.Layout = again;
                saves.BeforeCommit = _ => throw new IOException("power cut");
                Assert.Throws<IOException>(() => saves.Save(world));
                saves.BeforeCommit = null;
                var retried = saves.Save(world);
                Assert.AreEqual(retried.Generation, Generations(dir)[SaveChunks.LayoutShard(SaveChunks.LayoutShardOf(edited))]);
            }
            using (var journal = saves.OpenJournal())
            {
                var world = new WorldSaveSystem(dir).Load(TestContent.Load(), journal).World;
                Assert.AreEqual(furniture, world.Properties.Get(edited).Layout.Furniture.Count);
                Assert.IsTrue(world.Properties.All.Where(p => p.Kind != Core.Property.PropertyKind.Land).All(p => p.Layout != null), "every generated layout came back");
            }
        }

        [Test]
        public void SavesFromBeforeLayoutShards_StillLoadTheirInlineLayouts()
        {
            var dir = TestContent.TempDirectory("inline-layouts");
            var saves = new WorldSaveSystem(dir);
            EntityId id;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("inline", TestContent.DefaultConfig(), TestContent.Load(), journal);
                saves.Save(world, full: true);
                id = world.Properties.All.OrderBy(p => p.Id).First(p => p.Layout != null).Id;

                // Rewrite the save the old way: layouts inside the properties chunk, no layout shards in the manifest.
                var manifestPath = Path.Combine(dir, WorldSaveSystem.ManifestFile);
                var manifest = JsonSetup.Deserialize<SaveManifest>(File.ReadAllText(manifestPath));
                foreach (var key in manifest.ChunkGenerations.Keys.Where(k => k.StartsWith(SaveChunks.LayoutShardPrefix, StringComparison.Ordinal)).ToList())
                    manifest.ChunkGenerations.Remove(key);
                var chunkPath = Directory.GetFiles(Path.Combine(dir, "chunks"), "properties.g" + manifest.ChunkGenerations[SaveChunks.Properties] + ".json").Single();
                File.WriteAllText(chunkPath, JsonSetup.Serialize(new PropertiesChunk { Properties = world.Properties.All.OrderBy(p => p.Id).ToList() }));
                File.WriteAllText(manifestPath, JsonSetup.Serialize(manifest));
            }
            using (var journal = saves.OpenJournal())
            {
                var loaded = new WorldSaveSystem(dir).Load(TestContent.Load(), journal).World;
                Assert.IsNotNull(loaded.Properties.Get(id).Layout);
            }
        }
    }
}
