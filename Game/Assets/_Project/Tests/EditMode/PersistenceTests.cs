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
        public void CharacterSlots_ThreeCharacters_EachWithTheirOwnWorlds()
        {
            var root = TestContent.TempDirectory("slots");
            var accounts = Path.Combine(root, "accounts");
            // Before slots there was one "local" profile: it becomes character 1 and keeps its worlds.
            new AccountStore(accounts).Save(CharacterSlots.LegacyKey, Account("Legacy"));
            var slots = new CharacterSlots(accounts);
            Assert.AreEqual("Legacy Tester", slots.Get(1).Character.FullName);
            Assert.AreEqual(CharacterSlots.AccountIdFor(1), slots.Get(1).AccountId);
            Assert.IsTrue(slots.IsEmpty(2));
            Assert.AreEqual(2, slots.FirstEmpty);
            Assert.AreEqual(1, slots.Active);

            var profile = CharacterSlots.NewProfile(3);
            profile.Character.FirstName = "  Nia ";
            profile.Character.LastName = "Brooks#1";
            profile.Character.Age = 9;
            slots.Save(3, profile);
            var nia = new CharacterSlots(accounts).Get(3);
            Assert.AreEqual("Nia Brooks", nia.Character.FullName, "names are cleaned when saved");
            Assert.AreEqual(IdentityRules.MinAge, nia.Character.Age);
            Assert.AreEqual(CharacterSlots.AccountIdFor(3), nia.AccountId);
            Assert.AreEqual(3, slots.Active, "a new character becomes the active one");
            Assert.Throws<System.ArgumentException>(() => slots.Save(2, CharacterSlots.NewProfile(2)), "no nameless characters");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => slots.Get(4));

            // Worlds: slot 1 keeps the plain folder names, the others get their own copies.
            Assert.AreEqual("local-dev", CharacterSlots.WorldId("local-dev", 1));
            Assert.AreEqual("local-dev-c3", CharacterSlots.WorldId("local-dev", 3));
            Assert.AreEqual("slot-1-c2", CharacterSlots.WorldId("slot-1", 2));
            var saves = Path.Combine(root, "saves");
            foreach (var folder in new[] { "servers/local-dev", "servers/local-dev-c3", "story/slot-1", "story/slot-1-c3", "servers/srv-9-c2" })
                Directory.CreateDirectory(Path.Combine(saves, folder));
            var three = CharacterSlots.WorldFolders(saves, 3);
            CollectionAssert.AreEquivalent(new[] { Path.Combine(saves, "servers", "local-dev-c3"), Path.Combine(saves, "story", "slot-1-c3") }, three);
            Assert.AreEqual(2, CharacterSlots.WorldFolders(saves, 1).Count);

            slots.Delete(1);
            Assert.IsTrue(slots.IsEmpty(1));
            Assert.IsTrue(new CharacterSlots(accounts).IsEmpty(1), "a deleted character does not come back from the old profile");
            Assert.AreEqual(3, slots.Active);
            slots.Delete(3);
            Assert.IsFalse(slots.Any);
        }

        [Test]
        public void IdentityRules_CleanNamesAndClampAppearance()
        {
            Assert.AreEqual("Anne-Marie O'Neil", IdentityRules.CleanName("  Anne-Marie  O'Neil!! "));
            Assert.AreEqual("José", IdentityRules.CleanName("José<{9}>"));
            Assert.AreEqual("", IdentityRules.CleanName("1234 --"));
            Assert.AreEqual(IdentityRules.MaxNameLength, IdentityRules.CleanName(new string('a', 200)).Length);
            var hostile = new CharacterIdentity { FirstName = "Kai", LastName = "Lee", Age = -5, Presentation = (Core.Population.GenderPresentation)42, VoicePresetId = "../../x" };
            hostile.Appearance.HeightCm = float.NaN;
            hostile.Appearance.SkinTone = 99;
            hostile.Appearance.HairColorHex = "red; drop";
            for (var i = 0; i < 100; i++) hostile.Appearance.FaceMorphs.Add(new NamedValue { Name = "m" + i, Value = 7f });
            var clean = IdentityRules.Sanitize(hostile);
            Assert.AreNotSame(hostile, clean);
            Assert.AreEqual(IdentityRules.MinAge, clean.Age);
            Assert.AreEqual(Core.Population.GenderPresentation.Androgynous, clean.Presentation);
            Assert.AreEqual("x", clean.VoicePresetId);
            Assert.AreEqual(175f, clean.Appearance.HeightCm);
            Assert.AreEqual(IdentityRules.SkinTones - 1, clean.Appearance.SkinTone);
            Assert.AreEqual("#2B1B10", clean.Appearance.HairColorHex);
            Assert.AreEqual(IdentityRules.MaxMorphs, clean.Appearance.FaceMorphs.Count);
            Assert.AreEqual(1f, clean.Appearance.GetMorph("m0"));
            Assert.IsTrue(IdentityRules.IsValid(clean));
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
