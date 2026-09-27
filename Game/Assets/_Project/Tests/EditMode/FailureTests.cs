using System.IO;
using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Crime;
using HeroGame.Core.Powers;
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

        [Test]
        public void ActiveManhunt_SurvivesARestart()
        {
            var dir = TestContent.TempDirectory("manhunt");
            var saves = new WorldSaveSystem(dir);
            EntityId suspect;
            int level;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("manhunt", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var c = world.CreateCharacter(Account("Vic"), new WorldPosition());
                suspect = c.CharacterId;
                var incident = new CrimeIncident { Id = world.Ids.Next(EntityKind.CrimeIncident), CrimeTypeId = "store_robbery", Perpetrator = suspect, OccurredAt = world.Clock.Now };
                world.Justice.Incidents.Add(incident);
                world.Wanted.ReportCrime(incident, world.Content.FindCrime("store_robbery"), world.Clock.Now, policeWitnessed: true);
                level = world.Wanted.Get(suspect).Level;
                Assert.Greater(level, 0);
                saves.Save(world);
            }
            using (var journal = saves.OpenJournal())
            {
                var w = saves.Load(TestContent.Load(), journal).World;
                var s = w.Wanted.Get(suspect);
                Assert.IsNotNull(s, "a restart (or reloading a save) does not end a manhunt");
                Assert.AreEqual(WantedPhase.Pursuit, s.Phase);
                Assert.AreEqual(level, s.Level);
            }
        }

        [Test]
        public void LoadingTwiceWithoutSaving_NeverReplaysMoneyTwice()
        {
            var dir = TestContent.TempDirectory("twice");
            var saves = new WorldSaveSystem(dir);
            EntityId account;
            long balance;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("twice", TestContent.DefaultConfig(), TestContent.Load(), journal);
                account = world.CreateCharacter(Account("Lu"), new WorldPosition()).CheckingAccount;
                saves.Save(world);
                world.AdminGrant(account, Money.FromDollars(999), "admin", "once");
                balance = world.Ledger.BalanceOf(account).Cents;
            }
            for (var i = 0; i < 3; i++)
                using (var journal = saves.OpenJournal())
                    Assert.AreEqual(balance, saves.Load(TestContent.Load(), journal).World.Ledger.BalanceOf(account).Cents, "load " + (i + 1));
        }

        [Test]
        public void FencingTheSameLootTwice_PaysOnce()
        {
            var world = WorldGenerator.Create("fence", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            var c = world.CreateCharacter(Account("Kit"), new WorldPosition());
            var item = world.Content.Items.First(i => i.ValueCents > 1000);
            c.Inventory.Add(new InventoryStack { ItemId = item.Id, Quantity = 2, Stolen = true });
            var before = world.Ledger.BalanceOf(c.CheckingAccount);
            Assert.IsTrue(world.Crimes.SellToFence(c, "fence-1").Success);
            var paid = world.Ledger.BalanceOf(c.CheckingAccount) - before;
            Assert.Greater(paid.Cents, 0);
            Assert.IsFalse(world.Crimes.SellToFence(c, "fence-1").Success, "retry of the same request");
            Assert.IsFalse(world.Crimes.SellToFence(c, "fence-2").Success, "the goods are gone");
            Assert.AreEqual(before + paid, world.Ledger.BalanceOf(c.CheckingAccount));
            Assert.IsFalse(c.Inventory.Any(s => s.Stolen));
        }

        [Test]
        public void EveryPowerCombination_IsSafe()
        {
            var world = WorldGenerator.Create("combos", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            var c = world.CreateCharacter(Account("Max"), new WorldPosition(-200f, 0f, 40f));
            var other = world.CreateCharacter(Account("Min"), new WorldPosition(-195f, 0f, 40f));
            var npc = world.Population.Ordered.First(n => n.Alive);
            var vehicle = world.Vehicles.All.First();
            var property = world.Properties.All.OrderBy(p => p.Id).First();
            var domains = (PowerDomain[])System.Enum.GetValues(typeof(PowerDomain));
            var verbs = (EffectVerb[])System.Enum.GetValues(typeof(EffectVerb));
            var deliveries = (DeliveryMode[])System.Enum.GetValues(typeof(DeliveryMode));
            var elements = (PowerElement[])System.Enum.GetValues(typeof(PowerElement));
            var targets = (TargetKind[])System.Enum.GetValues(typeof(TargetKind));
            var rng = DeterministicRandom.For(42, 7);
            var attempts = 0;
            foreach (var d in domains)
            foreach (var v in verbs)
            foreach (var m in deliveries)
            {
                var e = elements[rng.NextInt(0, elements.Length)];
                var power = new PowerInstance { Stage = PowerStage.Mastered, Progress = new PowerProgress { Experience = 700f } };
                power.Definition.Components.Add(new PowerComponent { Domain = d, Verb = v, Delivery = m, Element = e, Magnitude = (float)rng.NextDouble() * 1.5f, Range = (float)rng.NextDouble() * 200f, Precision = (float)rng.NextDouble(), Efficiency = (float)rng.NextDouble() });
                c.Powers.Powers.Clear();
                c.Powers.Powers.Add(power);
                var t = targets[rng.NextInt(0, targets.Length)];
                var id = t == TargetKind.Npc ? npc.Id : t == TargetKind.Character ? other.CharacterId : t == TargetKind.Vehicle ? vehicle.Id : t == TargetKind.Property ? property.Id : EntityId.None;
                c.Powers.Stamina = 1f;
                c.Powers.Strain = 0f;
                c.Injury = InjuryState.Healthy;
                c.Health = 1f;
                c.Record.InCustody = false;
                world.Clock.AdvanceGame(120);
                var outcome = world.PowerUse.Use(c, new PowerUseRequest
                {
                    PowerIndex = 0, Intensity = (float)rng.NextDouble() * 1.2f, Target = t, TargetId = id, Origin = c.LastPosition,
                    Point = new WorldPosition(c.LastPosition.X + (float)(rng.NextDouble() * 400 - 200), 0f, c.LastPosition.Z + (float)(rng.NextDouble() * 400 - 200)),
                });
                attempts++;
                Assert.IsNotNull(outcome);
                if (outcome.Attempted)
                {
                    Assert.IsFalse(float.IsNaN(outcome.Use.Output) || float.IsInfinity(outcome.Use.Output), d + "/" + v + "/" + m);
                    Assert.IsFalse(float.IsNaN(outcome.Plan.Amount) || float.IsInfinity(outcome.Plan.Amount), d + "/" + v + "/" + m);
                }
                foreach (var ch in new[] { c, other }) Assert.That(ch.Health, Is.InRange(0f, 1f), d + "/" + v + "/" + m);
                Assert.IsFalse(float.IsNaN(c.LastPosition.X) || float.IsNaN(c.LastPosition.Z));
            }
            Assert.Greater(attempts, 100);
            Assert.IsTrue(world.Ledger.VerifyInvariant(out _));
            c.Powers.Powers[0].CooldownUntilSecond = 0;
            Assert.IsFalse(world.PowerUse.Use(c, new PowerUseRequest { PowerIndex = 0, Intensity = 1f, Target = TargetKind.Point, Origin = c.LastPosition, Point = new WorldPosition(float.NaN, 0f, 0f) }).Attempted,
                "non-finite aim points are refused");
        }

        [Test]
        public void BackgroundSave_Commits_ChangesDuringTheWriteGoToTheNextSave_AndFailuresRetry()
        {
            var dir = TestContent.TempDirectory("background");
            var saves = new WorldSaveSystem(dir);
            EntityId first, second;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("background", TestContent.DefaultConfig(), TestContent.Load(), journal);
                first = world.CreateCharacter(Account("Bea"), new WorldPosition()).CharacterId;
                var task = saves.SaveInBackground(world);
                // The simulation keeps going while the snapshot is written.
                second = world.CreateCharacter(Account("Cal"), new WorldPosition()).CharacterId;
                Assert.Greater(task.Result.ChunksWritten, 5);
                Assert.IsTrue(world.Dirty.IsDirty(SaveChunks.CharacterPrefix + second), "changes after the snapshot wait for the next save");

                saves.BeforeCommit = _ => throw new IOException("disk full");
                var failing = saves.SaveInBackground(world);
                Assert.Throws<System.AggregateException>(() => failing.Wait());
                saves.BeforeCommit = null;
                Assert.IsFalse(world.Dirty.IsDirty(SaveChunks.CharacterPrefix + second), "cleared when the snapshot was taken…");
                saves.Save(world); // …but the failed chunks are retried here
            }
            using (var journal = saves.OpenJournal())
            {
                var w = saves.Load(TestContent.Load(), journal).World;
                Assert.IsTrue(w.Characters.ContainsKey(first));
                Assert.IsTrue(w.Characters.ContainsKey(second), "the character saved only by the failed background write was retried");
            }
        }
    }
}
