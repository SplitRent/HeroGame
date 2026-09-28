using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Business;
    using HeroGame.Core.Characters;
    using HeroGame.Core.Crime;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Time;
    using HeroGame.Core.Vehicles;
    using HeroGame.Persistence.Saves;

    public class CrimeActionsAndJusticeTests
    {
        private World _world;
        private ServerCharacter _player;

        private static readonly ConcealmentState Bare = new ConcealmentState();
        private static readonly ConcealmentState Masked = new ConcealmentState { FaceConcealment = 0.95f };

        [SetUp]
        public void SetUp()
        {
            _world = NewWorld("crime");
            _player = _world.Characters.Values.First();
        }

        private static World NewWorld(string id)
        {
            var w = WorldGenerator.Create(id, TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            w.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 7), Character = new CharacterIdentity { FirstName = "Vic" } }, new WorldPosition());
            return w;
        }

        /// <summary>Moves the clock to the next occurrence of <paramref name="hour"/>:00 (optionally a few minutes later for variety).</summary>
        private static void At(World w, int hour, int extraMinutes = 0)
        {
            var now = w.Clock.Now;
            var target = now.StartOfDay.AddHours(hour);
            if (target <= now) target = target.AddDays(1);
            w.Clock.AdvanceGame(target.TotalSeconds - now.TotalSeconds + extraMinutes * 60);
        }

        private BusinessRecord Store(string template = "corner_store") =>
            _world.Businesses.Values.OrderBy(b => b.Id).First(b => b.TemplateId == template);

        private static void Give(ServerCharacter c, string item) => c.Inventory.Add(new InventoryStack { ItemId = item });

        // ------------------------------------------------------------------ sentencing rules

        [Test]
        public void Sentencing_FirstOffendersGetProbation_RepeatOffendersJail_PleaMitigates_HomicideDeniedBail()
        {
            var content = TestContent.Load();
            var burglary = new List<CrimeType> { content.FindCrime("burglary_residential") };
            var clean = new CriminalRecord();
            var first = SentencingGuidelines.For(burglary, clean, plea: false, onProbation: false, scale: 0.1f);
            Assert.AreEqual(0, first.JailDays, "first minor offence → probation");
            Assert.Greater(first.ProbationDays, 0);
            var repeat = SentencingGuidelines.For(burglary, new CriminalRecord { Convictions = 3 }, false, false, 0.1f);
            Assert.Greater(repeat.JailDays, 0);
            Assert.Greater(repeat.FineCents, first.FineCents);
            var plea = SentencingGuidelines.For(burglary, new CriminalRecord { Convictions = 3 }, true, false, 0.1f);
            Assert.Less(plea.JailDays, repeat.JailDays);
            Assert.Less(SentencingGuidelines.Bail(burglary, clean, false), SentencingGuidelines.Bail(burglary, new CriminalRecord { Convictions = 2 }, true));
            Assert.AreEqual(-1, SentencingGuidelines.Bail(new List<CrimeType> { content.FindCrime("homicide") }, clean, false));
            Assert.Greater(SentencingGuidelines.ConvictionChance(0.9f, Counsel.PublicDefender), SentencingGuidelines.ConvictionChance(0.3f, Counsel.PublicDefender));
            Assert.Greater(SentencingGuidelines.ConvictionChance(0.9f, Counsel.PublicDefender), SentencingGuidelines.ConvictionChance(0.9f, Counsel.PrivateAttorney));
        }

        // ------------------------------------------------------------------ actions

        [Test]
        public void Shoplifting_OnlyWhenOpen_AndEitherYieldsStolenGoodsOrGetsYouNoticed()
        {
            var store = Store();
            At(_world, 3);
            var place = _world.Geography.GetPlace(store.Place);
            if (place.OpenMinute != place.CloseMinute) Assert.IsFalse(_world.Crimes.Shoplift(_player, store, Bare).Attempted, "closed at 3 am");

            int succeeded = 0, caught = 0;
            for (var i = 0; i < 30; i++)
            {
                At(_world, 14, i);
                var r = _world.Crimes.Shoplift(_player, store, Masked);
                Assert.IsTrue(r.Attempted, r.Message);
                Assert.IsNotNull(r.Incident);
                if (r.Succeeded)
                {
                    succeeded++;
                    Assert.IsTrue(r.Loot.All(s => s.Stolen));
                }
                else caught++;
            }
            Assert.Greater(succeeded, 0);
            Assert.Greater(caught, 0);
            Assert.AreEqual(30, _world.Justice.Incidents.Count(x => x.CrimeTypeId == "shoplifting"));
            Assert.IsTrue(_player.Inventory.Any(s => s.Stolen));
        }

        [Test]
        public void Burglary_NeedsTools_DamagesTheDoor_AndAlarmsAlwaysReport()
        {
            var house = _world.Properties.All.OrderBy(p => p.Id).First(p => p.Kind == PropertyKind.House && p.ForSale);
            house.HasAlarm = true;
            At(_world, 11);
            Assert.IsFalse(_world.Crimes.Burglary(_player, house, Masked).Attempted, "no tools");
            Give(_player, "crowbar");
            var r = _world.Crimes.Burglary(_player, house, Masked);
            Assert.IsTrue(r.Attempted, r.Message);
            Assert.IsTrue(r.Reported, "the alarm calls it in");
            Assert.Greater(house.DamageRepairCents, 0, "forced entry is recorded damage (insurable)");
            Assert.IsNotNull(_world.Wanted.Get(_player.CharacterId));
        }

        [Test]
        public void Masks_ReduceIdentification_FromTheSameRobbery()
        {
            float Identification(ConcealmentState concealment)
            {
                var w = NewWorld("mask");
                var c = w.Characters.Values.First();
                At(w, 13);
                var store = w.Businesses.Values.OrderBy(b => b.Id).First(b => b.TemplateId == "corner_store");
                var r = w.Crimes.RobStore(c, store, concealment);
                Assert.IsTrue(r.Attempted, r.Message);
                Assert.IsTrue(r.Reported);
                return w.Justice.StrengthFor(r.Incident.Id, c.CharacterId, false);
            }
            Assert.Greater(Identification(Bare), Identification(Masked) + 0.2f);
        }

        [Test]
        public void StoreRobbery_TakesFromTheTill_AndTheFenceBuysLoot()
        {
            var store = Store();
            At(_world, 13);
            var till = _world.Ledger.BalanceOf(store.Account);
            var cash = _world.Ledger.BalanceOf(_player.CheckingAccount);
            var r = _world.Crimes.RobStore(_player, store, Masked);
            Assert.Greater(r.Cash.Cents, 0);
            Assert.AreEqual(till - r.Cash, _world.Ledger.BalanceOf(store.Account));
            Assert.AreEqual(cash + r.Cash, _world.Ledger.BalanceOf(_player.CheckingAccount));

            _player.Inventory.Add(new InventoryStack { ItemId = "laptop", Stolen = true });
            _player.Inventory.Add(new InventoryStack { ItemId = "crowbar" });
            var offer = _world.Crimes.FenceOffer(_player);
            var laptop = _world.Content.FindItem("laptop").ValueCents;
            Assert.Greater(offer.Cents, laptop / 5);
            Assert.Less(offer.Cents, laptop / 2);
            Assert.IsTrue(_world.Crimes.SellToFence(_player, "fence-1").Success);
            Assert.IsFalse(_player.Inventory.Any(s => s.Stolen));
            Assert.IsTrue(_player.Inventory.Any(s => s.ItemId == "crowbar"), "the fence doesn't take your tools");
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void VehicleTheft_ImmobilisersWork_AndChopShopsDestroyTheCar()
        {
            int Successes(bool immobiliser)
            {
                var w = NewWorld(immobiliser ? "imm" : "plain");
                var c = w.Characters.Values.First();
                var cars = w.Vehicles.All.Where(v => v.LocationKind == VehicleLocationKind.Street && v.Drivable).OrderBy(v => v.Id).Take(40).ToList();
                var n = 0;
                foreach (var v in cars)
                {
                    if (immobiliser) v.Mods.Add(new InstalledMod { Slot = ModSlot.Security, ModId = "security_immobilizer" });
                    w.Clock.AdvanceGame(300);
                    if (w.Crimes.StealVehicle(c, v, Masked).Succeeded) n++;
                }
                return n;
            }
            Assert.Greater(Successes(false), Successes(true) + 5);

            var car = _world.Vehicles.All.OrderBy(v => v.Id).First(v => v.LocationKind == VehicleLocationKind.Street && v.Drivable);
            ServerCharacter thief = _player;
            CrimeResult r = null;
            for (var i = 0; i < 20 && (r == null || !r.Succeeded); i++)
            {
                _world.Clock.AdvanceGame(120);
                r = _world.Crimes.StealVehicle(thief, car, Masked);
            }
            Assert.IsTrue(r.Succeeded);
            Assert.IsTrue(car.ReportedStolen);
            Assert.AreEqual(thief.CharacterId, car.StolenBy);
            Assert.AreNotEqual(thief.CharacterId, _world.Ownership.OwnerOf(car.Id), "stealing doesn't transfer title");
            var before = _world.Ledger.BalanceOf(thief.CheckingAccount);
            Assert.IsTrue(_world.Crimes.ChopVehicle(thief, car, "chop").Success);
            Assert.Greater(_world.Ledger.BalanceOf(thief.CheckingAccount).Cents, before.Cents);
            Assert.AreEqual(VehicleLocationKind.Destroyed, car.LocationKind);
            Assert.IsFalse(_world.Crimes.ChopVehicle(thief, car, "chop2").Success);
        }

        // ------------------------------------------------------------------ justice

        [Test]
        public void Arrest_WithoutEvidence_ReleasesTheSuspect()
        {
            var r = _world.Courts.Arrest(_player, caughtInPursuit: false, resisted: false);
            Assert.IsFalse(r.Success);
            Assert.IsFalse(_player.Record.InCustody);
            Assert.AreEqual(0, _player.Record.Arrests);
        }

        [Test]
        public void Robbery_Arrest_Bail_Hearing_Sentence_EndToEnd()
        {
            var store = Store();
            At(_world, 13);
            var robbery = _world.Crimes.RobStore(_player, store, Bare);
            Assert.IsTrue(robbery.Reported);
            var eyewitness = _world.Justice.StrengthFor(robbery.Incident.Id, _player.CharacterId, false);
            Assert.Greater(eyewitness, 0.3f, "the clerk got a good look");
            Assert.Less(eyewitness, SentencingGuidelines.ChargeThreshold, "one eyewitness alone is not enough to charge");
            Assert.AreEqual(0, _world.Courts.ChargeableIncidents(_player).Count);

            var arrest = _world.Courts.Arrest(_player, caughtInPursuit: true, resisted: true);
            Assert.IsTrue(arrest.Success, arrest.Error);
            Assert.IsTrue(_player.Record.InCustody);
            Assert.AreEqual(1, _player.Record.Arrests);
            Assert.IsNull(_world.Wanted.Get(_player.CharacterId));
            var court = _world.Justice.OpenCaseFor(_player.CharacterId);
            Assert.AreEqual(2, court.Charges.Count, "robbery + evading police");
            Assert.IsTrue(_world.Crimes.RobStore(_player, store, Bare).Message.Contains("custody"), "no crimes from a cell");

            Assert.Greater(court.BailCents, 0);
            _world.AdminGrant(_player.CheckingAccount, new Money(court.BailCents), "test", "bail money");
            var treasury = _world.Ledger.BalanceOf(_world.Accounts.Treasury);
            Assert.IsTrue(_world.Courts.PostBail(_player, "bail").Success);
            Assert.IsFalse(_player.Record.InCustody);
            Assert.AreEqual(treasury + new Money(court.BailCents), _world.Ledger.BalanceOf(_world.Accounts.Treasury));

            new WorldSimulation(_world).AdvanceDays(SentencingGuidelines.HearingDelayDays + 1);
            Assert.AreEqual(CaseStage.Closed, court.Stage);
            Assert.AreNotEqual(Verdict.Pending, court.Verdict);
            if (court.Verdict == Verdict.Guilty)
            {
                Assert.AreEqual(1, _player.Record.Convictions);
                Assert.IsTrue(court.Charges.All(c => c.Convicted));
                Assert.IsTrue(court.Sentence.JailDays > 0 == _player.Record.InCustody);
            }
            else
            {
                Assert.IsTrue(court.Charges.All(c => c.Dismissed));
                Assert.IsFalse(_player.Record.InCustody);
            }
            Assert.IsTrue(_player.Inbox.Any(m => m.FromName == JusticeService.CourtName && (m.Body.Contains("guilty") || m.Body.Contains("Acquitted"))));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void CustodyScreen_ShowsTheCase_AndWaitingRunsToTheHearingThenRelease()
        {
            _player.Record.Convictions = 2; // a repeat offender goes to jail
            At(_world, 13);
            _world.Crimes.RobStore(_player, Store(), Bare);
            Assert.IsTrue(_world.Courts.Arrest(_player, caughtInPursuit: true, resisted: true).Success);
            var court = _world.Justice.OpenCaseFor(_player.CharacterId);

            var view = _world.Courts.Custody(_player);
            Assert.IsTrue(view.InCustody);
            Assert.IsFalse(view.ServingSentence);
            Assert.AreEqual(2, view.Charges.Count);
            Assert.AreEqual(court.HearingDay, view.HearingDay);
            Assert.AreEqual((court.HearingDay + 1) * GameDateTime.SecondsPerDay, view.NextEventSecond, "hearings are decided as their day ends");
            Assert.AreEqual(court.BailCents, view.BailCents);
            Assert.AreEqual(court.BailCents > 0, view.CanPostBail);
            Assert.IsTrue(view.CanHireAttorney);
            Assert.AreEqual(court.PleaOffered, view.PleaOffered);
            StringAssert.Contains("hearing", view.NextStep);

            Assert.IsTrue(_world.Courts.AcceptPlea(_player).Success || !court.PleaOffered);
            var sim = new WorldSimulation(_world);
            var waited = sim.WaitInCustody(_player);
            Assert.AreEqual(CaseStage.Closed, court.Stage, "waiting stops once the hearing has been held");
            Assert.AreEqual(court.HearingDay + 1, _world.Today);
            Assert.Greater(waited, 0);
            if (_player.Record.InCustody)
            {
                view = _world.Courts.Custody(_player);
                Assert.IsTrue(view.ServingSentence);
                Assert.AreEqual(_player.Record.CustodyUntilDay, view.ReleaseDay);
                StringAssert.Contains("Release", view.NextStep);
                sim.WaitInCustody(_player);
                Assert.IsFalse(_player.Record.InCustody, "waiting again serves the sentence");
                Assert.AreEqual(view.ReleaseDay + 1, _world.Today);
            }
            Assert.AreEqual(0, sim.WaitInCustody(_player), "nothing to wait for once free");
            Assert.IsFalse(_world.Courts.Custody(_player).InCustody);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void GuiltyPlea_IsCertain_Mitigated_AndJailEndsOnTime()
        {
            _player.Record.Convictions = 2; // a repeat offender goes to jail
            var store = Store();
            At(_world, 13);
            _world.Crimes.RobStore(_player, store, Bare);
            Assert.IsTrue(_world.Courts.Arrest(_player, true, false).Success);
            Assert.IsTrue(_world.Courts.AcceptPlea(_player).Success);
            var court = _world.Justice.OpenCaseFor(_player.CharacterId);
            var sim = new WorldSimulation(_world);
            sim.AdvanceDays(SentencingGuidelines.HearingDelayDays + 1);
            Assert.AreEqual(Verdict.GuiltyPlea, court.Verdict);
            var expected = SentencingGuidelines.For(new List<CrimeType> { _world.Content.FindCrime("store_robbery") }, new CriminalRecord { Convictions = 2 }, true, false,
                _world.Config.Gameplay.SentenceScale);
            Assert.AreEqual(expected.JailDays, court.Sentence.JailDays);
            Assert.IsTrue(_player.Record.InCustody);
            Assert.IsTrue(_player.Record.OnProbation(_world.Clock.Now));
            sim.AdvanceDays(court.Sentence.JailDays + 1);
            Assert.IsFalse(_player.Record.InCustody, "released when the sentence is served");
            Assert.IsTrue(_player.Inbox.Any(m => m.Body.StartsWith("Released")));
        }

        [Test]
        public void UnpaidFines_BecomeAWarrant_PayingClearsIt()
        {
            _player.Record.FinesOwedCents = 50000;
            _player.Record.FinesDueDay = _world.Today + 2;
            new WorldSimulation(_world).AdvanceDays(3);
            Assert.IsTrue(_player.Record.ActiveWarrant);
            Assert.IsTrue(_player.Record.WarrantForFines);
            _world.AdminGrant(_player.CheckingAccount, Money.FromDollars(500L), "t", "t");
            Assert.IsTrue(_world.Courts.PayFines(_player, Money.FromDollars(5000L), "fines").Success);
            Assert.AreEqual(0, _player.Record.FinesOwedCents);
            Assert.IsFalse(_player.Record.ActiveWarrant);
        }

        [Test]
        public void EvidenceAndCases_SurviveSaveAndLoad()
        {
            var dir = TestContent.TempDirectory("justice");
            var saves = new WorldSaveSystem(dir);
            EntityId incident, playerId;
            float identification;
            using (var journal = saves.OpenJournal())
            {
                var w = WorldGenerator.Create("jsave", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var c = w.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 3), Character = new CharacterIdentity { FirstName = "Jo" } }, new WorldPosition());
                playerId = c.CharacterId;
                At(w, 13);
                var r = w.Crimes.RobStore(c, w.Businesses.Values.OrderBy(b => b.Id).First(b => b.TemplateId == "corner_store"), Bare);
                incident = r.Incident.Id;
                identification = w.Wanted.IdentificationOf(playerId);
                Assert.Greater(identification, 0f);
                Assert.IsTrue(w.Courts.Arrest(c, true, false).Success);
                saves.Save(w);
            }
            using (var journal = saves.OpenJournal())
            {
                var w = saves.Load(TestContent.Load(), journal).World;
                Assert.IsNotNull(w.Justice.Incident(incident));
                Assert.IsNotNull(w.Justice.OpenCaseFor(playerId));
                Assert.GreaterOrEqual(w.Wanted.IdentificationOf(playerId), identification, "evidence re-filed with police");
                Assert.IsTrue(w.Characters[playerId].Record.InCustody);
            }
        }
    }
}
