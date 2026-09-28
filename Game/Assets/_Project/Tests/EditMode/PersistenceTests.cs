using System.IO;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.World;
    using HeroGame.Persistence.Json;
    using HeroGame.Persistence.Saves;
    using HeroGame.Persistence.Storage;

    public class PersistenceTests
    {
        private static AccountProfile Account(string name)
        {
            return new AccountProfile
            {
                AccountId = EntityId.Create(EntityKind.UserAccount, 1),
                DisplayName = name,
                Character = new CharacterIdentity { FirstName = name, LastName = "Tester" },
                CharacterCreated = true,
            };
        }

        [Test]
        public void Json_RoundTripsCoreTypes()
        {
            var id = EntityId.Create(EntityKind.Business, 77);
            var json = JsonSetup.Serialize(new OwnershipEntry { Asset = id, Owner = EntityId.None });
            StringAssert.Contains("Business:77", json);
            var back = JsonSetup.Deserialize<OwnershipEntry>(json);
            Assert.AreEqual(id, back.Asset);
        }

        [Test]
        public void World_SavesAndLoadsFaithfully()
        {
            var dir = TestContent.TempDirectory("roundtrip");
            var saves = new WorldSaveSystem(dir);
            World original;
            using (var journal = saves.OpenJournal())
            {
                original = WorldGenerator.Create("persist", TestContent.DefaultConfig(), TestContent.Load(), journal);
                original.CreateCharacter(Account("Riley"), new WorldPosition(1, 0, 2));
                new WorldSimulation(original).AdvanceDays(3);
                var result = saves.Save(original);
                Assert.Greater(result.ChunksWritten, 5);
            }

            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(TestContent.Load(), journal);
                Assert.IsFalse(load.Report.HasErrors, load.Report.ToString());
                var loaded = load.World;
                Assert.AreEqual(original.Clock.Now, loaded.Clock.Now);
                Assert.AreEqual(original.Population.Count, loaded.Population.Count);
                Assert.AreEqual(original.Businesses.Count, loaded.Businesses.Count);
                Assert.AreEqual(original.Characters.Count, loaded.Characters.Count);
                foreach (var a in original.Ledger.Accounts) Assert.AreEqual(a.BalanceCents, loaded.Ledger.Get(a.Id).BalanceCents);
                foreach (var e in original.Ownership.Entries) Assert.AreEqual(e.Owner, loaded.Ownership.OwnerOf(e.Asset));
                var npcA = original.Population.Ordered[10];
                var npcB = loaded.Population.Get(npcA.Id);
                Assert.AreEqual(npcA.FullName, npcB.FullName);
                Assert.AreEqual(npcA.Relationships.Count, npcB.Relationships.Count);
                Assert.AreEqual(original.Ids.Next(EntityKind.Npc), loaded.Ids.Next(EntityKind.Npc), "id allocator continues where it left off");
            }
        }

        [Test]
        public void Crash_AfterPurchase_IsRecoveredFromJournal()
        {
            var dir = TestContent.TempDirectory("crash");
            var saves = new WorldSaveSystem(dir);
            EntityId propertyId, buyer;
            long buyerBalanceAfter;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("crash", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var character = world.CreateCharacter(Account("Sam"), new WorldPosition());
                buyer = character.CharacterId;
                saves.Save(world);

                // After the snapshot: an admin grant and a land purchase, then the process dies without saving.
                Assert.IsTrue(world.AdminGrant(character.CheckingAccount, Money.FromDollars(200000L), "test-admin", "test").Success);
                propertyId = CheapestListing(world);
                var purchase = world.Properties.Purchase(propertyId, buyer, character.CheckingAccount, world.Accounts.Treasury, world.Accounts.Treasury, world.Clock.Now, "buy-land-1");
                Assert.IsTrue(purchase.Success, purchase.Error);
                buyerBalanceAfter = world.Ledger.Get(character.CheckingAccount).BalanceCents;
            }

            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(TestContent.Load(), journal);
                Assert.AreEqual(2, load.JournalEntriesReplayed);
                var world = load.World;
                Assert.AreEqual(buyer, world.Ownership.OwnerOf(propertyId));
                Assert.AreEqual(buyerBalanceAfter, world.Ledger.Get(world.Characters[buyer].CheckingAccount).BalanceCents);
                Assert.IsTrue(world.Ledger.VerifyInvariant(out _));
                // Retrying the same request after recovery must not double-charge.
                Assert.IsFalse(world.Properties.Purchase(propertyId, buyer, world.Characters[buyer].CheckingAccount, world.Accounts.Treasury, world.Accounts.Treasury, world.Clock.Now, "buy-land-1").Success);
            }
        }

        [Test]
        public void TornJournalTail_IsDiscarded()
        {
            var dir = TestContent.TempDirectory("torn");
            var path = Path.Combine(dir, "journal.log");
            using (var journal = new FileTransactionJournal(path))
            {
                journal.Append(new WorldTransaction { Sequence = 1, Description = "a" });
                journal.Append(new WorldTransaction { Sequence = 2, Description = "b" });
            }
            File.AppendAllText(path, "{\"Sequence\":3,\"Descr"); // power loss mid-append
            using (var journal = new FileTransactionJournal(path))
            {
                journal.Append(new WorldTransaction { Sequence = 3, Description = "c" });
                var entries = new System.Collections.Generic.List<WorldTransaction>(journal.ReadAfter(0));
                Assert.AreEqual(3, entries.Count);
                Assert.AreEqual("c", entries[2].Description);
                Assert.AreEqual(0, journal.CorruptEntries);
            }
        }

        [Test]
        public void IncrementalSave_RewritesOnlyDirtyChunks()
        {
            var dir = TestContent.TempDirectory("incremental");
            var saves = new WorldSaveSystem(dir);
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("inc", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var first = saves.Save(world);
                var second = saves.Save(world); // nothing changed except the always-written meta/transactional pair
                Assert.AreEqual(2, second.ChunksWritten);
                Assert.Greater(first.ChunksWritten, second.ChunksWritten);
                Assert.Greater(second.ChunksSkipped, 0);
            }
        }

        [Test]
        public void Save_RefusesToPersistBrokenLedger()
        {
            var dir = TestContent.TempDirectory("broken");
            var saves = new WorldSaveSystem(dir);
            var world = WorldGenerator.Create("broken", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            foreach (var a in world.Ledger.Accounts) { a.BalanceCents += 1; break; } // simulated duplication bug
            Assert.Throws<System.InvalidOperationException>(() => saves.Save(world));
        }

        [Test]
        public void StorySlots_RotateAutosaves()
        {
            var slots = new StorySlotManager(TestContent.TempDirectory("slots"));
            for (var i = 0; i < 5; i++)
            {
                var id = slots.NextAutosaveSlot();
                slots.WriteInfo(new SlotInfo { SlotId = id, Kind = SlotKind.Autosave, SavedAtUnix = 1000 + i });
            }
            Assert.AreEqual(StorySlotManager.AutosaveRotation, slots.List().Count);
            Assert.AreEqual("auto_2", slots.NextAutosaveSlot());
        }

        private static EntityId CheapestListing(World world)
        {
            var best = EntityId.None;
            var bestPrice = long.MaxValue;
            foreach (var p in world.Properties.All)
            {
                if (!p.ForSale || p.ListingPriceCents <= 0 || p.ListingPriceCents >= bestPrice) continue;
                best = p.Id;
                bestPrice = p.ListingPriceCents;
            }
            Assert.IsTrue(best.IsValid);
            return best;
        }
    }
}
