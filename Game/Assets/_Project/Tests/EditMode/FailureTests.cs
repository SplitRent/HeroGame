using System.IO;
using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Simulation;
using HeroGame.Persistence.Saves;
using HeroGame.Persistence.Storage;
using NUnit.Framework;

namespace HeroGame.Tests
{
    /// <summary>
    /// Phase 26 failure testing: power cuts mid-save, damaged snapshots, garbage in the journal, and a failing save
    /// that must not lose the changes it was trying to write.
    /// </summary>
    public class FailureTests
    {
        private static AccountProfile Account(string name) =>
            new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, (ulong)(StableHash.Of(name) % 100000 + 1)), Character = new CharacterIdentity { FirstName = name } };

        private static string Chunk(string dir, string prefix) =>
            Directory.GetFiles(Path.Combine(dir, "chunks"), prefix + ".g*.json").OrderByDescending(f => f).First();

        [Test]
        public void PowerCut_BetweenChunksAndManifest_LeavesTheLastSnapshot_AndTheJournalRestoresMoney()
        {
            var dir = TestContent.TempDirectory("powercut");
            var saves = new WorldSaveSystem(dir);
            EntityId account;
            long balance, generation;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("powercut", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var c = world.CreateCharacter(Account("Ivy"), new WorldPosition());
                account = c.CheckingAccount;
                generation = saves.Save(world).Generation;

                Assert.IsTrue(world.AdminGrant(account, Money.FromDollars(1234), "admin", "prize").Success);
                new WorldSimulation(world).AdvanceDays(1);
                saves.BeforeCommit = _ => throw new IOException("power cut");
                Assert.Throws<IOException>(() => saves.Save(world));
                balance = world.Ledger.BalanceOf(account).Cents;
            }
            saves.BeforeCommit = null;
            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(TestContent.Load(), journal);
                Assert.IsFalse(load.RecoveredFromPreviousSnapshot, "the committed snapshot is intact");
                Assert.AreEqual(1, load.JournalEntriesReplayed);
                Assert.AreEqual(balance, load.World.Ledger.BalanceOf(account).Cents);
                Assert.IsTrue(load.World.Ledger.VerifyInvariant(out _));
                Assert.AreEqual(generation + 1, saves.Save(load.World).Generation, "orphaned chunk files from the failed save are harmless");
            }
        }

        [Test]
        public void FailedSave_KeepsItsChangesDirty_SoTheNextSaveWritesThem()
        {
            var dir = TestContent.TempDirectory("retry");
            var saves = new WorldSaveSystem(dir);
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("retry", TestContent.DefaultConfig(), TestContent.Load(), journal);
                saves.Save(world);
                var c = world.CreateCharacter(Account("Oz"), new WorldPosition());
                saves.BeforeCommit = _ => throw new IOException("disk full");
                Assert.Throws<IOException>(() => saves.Save(world));
                Assert.IsTrue(world.Dirty.IsDirty(SaveChunks.CharacterPrefix + c.CharacterId), "nothing was lost from the dirty set");
                saves.BeforeCommit = null;
                saves.Save(world);
            }
            using (var journal = saves.OpenJournal())
                Assert.AreEqual(1, saves.Load(TestContent.Load(), journal).World.Characters.Count);
        }

        [Test]
        public void DamagedSnapshot_FallsBackToThePreviousOne_AndReplaysEveryTransactionSince()
        {
            var dir = TestContent.TempDirectory("damaged");
            var saves = new WorldSaveSystem(dir);
            EntityId account;
            long balance;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("damaged", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var c = world.CreateCharacter(Account("Rue"), new WorldPosition());
                account = c.CheckingAccount;
                saves.Save(world);
                saves.CompactJournal(world, journal);
                Assert.IsTrue(world.AdminGrant(account, Money.FromDollars(500), "admin", "a").Success);
                saves.Save(world);
                saves.CompactJournal(world, journal); // must keep the grant: it is newer than the fallback snapshot
                Assert.IsTrue(world.AdminGrant(account, Money.FromDollars(700), "admin", "b").Success);
                balance = world.Ledger.BalanceOf(account).Cents;
            }

            // A bad sector truncates the newest transactional chunk.
            var damaged = Chunk(dir, "transactional");
            File.WriteAllText(damaged, File.ReadAllText(damaged).Substring(0, 200));

            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(TestContent.Load(), journal);
                Assert.IsTrue(load.RecoveredFromPreviousSnapshot);
                StringAssert.Contains("transactional", load.Report.ToString());
                Assert.AreEqual(2, load.JournalEntriesReplayed, "both grants since the fallback snapshot");
                Assert.AreEqual(balance, load.World.Ledger.BalanceOf(account).Cents);
                Assert.IsTrue(load.World.Ledger.VerifyInvariant(out _));
                saves.Save(load.World);
            }
            using (var journal = saves.OpenJournal())
            {
                var again = saves.Load(TestContent.Load(), journal);
                Assert.IsFalse(again.RecoveredFromPreviousSnapshot, "the recovery save wrote a clean, complete snapshot");
                Assert.AreEqual(balance, again.World.Ledger.BalanceOf(account).Cents);
            }
        }

        [Test]
        public void CorruptManifest_FallsBack_AndWithNoFallbackLoadFailsLoudly()
        {
            var dir = TestContent.TempDirectory("manifest");
            var saves = new WorldSaveSystem(dir);
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("manifest", TestContent.DefaultConfig(), TestContent.Load(), journal);
                saves.Save(world);
            }
            var manifest = Path.Combine(dir, WorldSaveSystem.ManifestFile);
            File.WriteAllText(manifest, "{ \"Generation\": 1, \"ChunkGen");
            using (var journal = saves.OpenJournal())
            {
                var ex = Assert.Throws<InvalidDataException>(() => saves.Load(TestContent.Load(), journal), "one snapshot only: nothing to fall back to");
                StringAssert.Contains("corrupt", ex.Message);
            }

            var dir2 = TestContent.TempDirectory("manifest2");
            var saves2 = new WorldSaveSystem(dir2);
            using (var journal = saves2.OpenJournal())
            {
                var world = WorldGenerator.Create("manifest2", TestContent.DefaultConfig(), TestContent.Load(), journal);
                saves2.Save(world);
                new WorldSimulation(world).AdvanceDays(1);
                saves2.Save(world);
            }
            File.WriteAllText(Path.Combine(dir2, WorldSaveSystem.ManifestFile), "");
            using (var journal = saves2.OpenJournal())
                Assert.IsTrue(saves2.Load(TestContent.Load(), journal).RecoveredFromPreviousSnapshot);
        }

        [Test]
        public void GarbageInTheMiddleOfTheJournal_IsSkipped_AndTheRestReplays()
        {
            var dir = TestContent.TempDirectory("garbage");
            var saves = new WorldSaveSystem(dir);
            EntityId account;
            long expected;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("garbage", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var c = world.CreateCharacter(Account("Nell"), new WorldPosition());
                account = c.CheckingAccount;
                saves.Save(world);
                world.AdminGrant(account, Money.FromDollars(100), "admin", "one");
                world.AdminGrant(account, Money.FromDollars(200), "admin", "two");
                world.AdminGrant(account, Money.FromDollars(400), "admin", "three");
                expected = world.Ledger.BalanceOf(account).Cents - Money.FromDollars(200).Cents;
            }
            var lines = File.ReadAllLines(saves.JournalPath).ToList();
            var two = lines.FindIndex(l => l.Contains("admin: two"));
            Assert.Greater(two, 0);
            lines[two] = "{\"this is not\": a transaction";
            File.WriteAllLines(saves.JournalPath, lines);
            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(TestContent.Load(), journal);
                Assert.AreEqual(1, load.CorruptJournalEntries);
                Assert.AreEqual(2, load.JournalEntriesReplayed);
                Assert.AreEqual(expected, load.World.Ledger.BalanceOf(account).Cents);
                Assert.IsTrue(load.World.Ledger.VerifyInvariant(out _), "a skipped entry never breaks the books: each is balanced on its own");
                StringAssert.Contains("unreadable journal", load.Report.ToString());
            }
        }
    }
}
