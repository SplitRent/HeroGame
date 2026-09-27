using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Config;
using HeroGame.Core.Crime;
using HeroGame.Core.Economy;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.World;
using HeroGame.Persistence.Saves;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class EmergencyServicesTests
    {
        private World _world;
        private ServerCharacter _player;

        [SetUp]
        public void SetUp()
        {
            _world = Create("ems", TestContent.DefaultConfig());
            _player = _world.Characters.Values.First();
        }

        private static World Create(string id, ServerConfig config)
        {
            var w = WorldGenerator.Create(id, config, TestContent.Load(), new MemoryTransactionJournal());
            w.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 5), Character = new CharacterIdentity { FirstName = "Ana" } }, new WorldPosition());
            return w;
        }

        /// <summary>Advances the clock and processes only emergency events (no daily simulation noise).</summary>
        private static void Minutes(World w, int minutes)
        {
            w.Clock.AdvanceGame(minutes * 60L);
            w.Dispatch.AdvanceTo(w.Clock.Now);
        }

        private static Place PlaceOf(World w, PlaceKind kind) => w.Geography.PlacesOfKind(kind).OrderBy(p => p.Id).First();

        [Test]
        public void Units_AreCrewedFromServiceFleets()
        {
            var s = _world.Emergency;
            Assert.Greater(s.Units.Count(u => u.Service == EmergencyService.Police), 0);
            Assert.Greater(s.Units.Count(u => u.Service == EmergencyService.Fire), 0);
            Assert.Greater(s.Units.Count(u => u.Service == EmergencyService.Medical), 0);
            Assert.IsTrue(s.Units.All(u => u.Status == UnitStatus.Available && u.Station.IsValid));
            Assert.AreEqual(s.Units.Count, s.Units.Select(u => u.CallSign).Distinct().Count());
        }

        [Test]
        public void MedicalCall_NearestAmbulanceDrivesThere_TreatsAndTransports()
        {
            var park = PlaceOf(_world, PlaceKind.Park);
            _world.Dispatch.AdvanceTo(_world.Clock.Now);
            var call = _world.Dispatch.Report(EmergencyKind.Medical, park.Position, 2, "Fall in the park", severity: 0.4f);
            Assert.AreEqual(IncidentStatus.Dispatched, call.Status);
            var unit = _world.Emergency.Unit(call.Units.Single());
            Assert.AreEqual(EmergencyService.Medical, unit.Service);
            var eta = unit.ArriveSecond - unit.DepartSecond;
            Assert.Greater(eta, 60);
            Assert.Less(eta, 20 * 60, "an ambulance reaches anywhere in the slice in under 20 minutes");
            // The chosen unit had the best road ETA among free ambulances.
            foreach (var other in _world.Emergency.Units.Where(u => u.Service == EmergencyService.Medical && u.Free))
                Assert.GreaterOrEqual(_world.Dispatch.TravelSeconds(other.StationPosition, park.Position, _world.Clock.Now.TotalSeconds, out _), eta - 1);

            Minutes(_world, (int)((unit.ArriveSecond - _world.Clock.Now.TotalSeconds) / 60) + 1);
            Assert.AreEqual(IncidentStatus.OnScene, call.Status);
            Assert.AreEqual(UnitStatus.OnScene, unit.Status);
            Minutes(_world, 20);
            Assert.AreEqual(IncidentStatus.Resolved, call.Status);
            Assert.IsTrue(unit.Status == UnitStatus.Transporting || unit.Status == UnitStatus.Returning || unit.Status == UnitStatus.Available);
            Minutes(_world, 90);
            Assert.AreEqual(UnitStatus.Available, unit.Status, "back at the station");
            Assert.AreEqual(1, _world.Emergency.Stats.PatientsTransported);
        }

        [Test]
        public void Police_ArrestASuspectStillAtTheScene_ButNotOneWhoLeft()
        {
            bool Arrested(bool stay)
            {
                var w = Create(stay ? "stay" : "run", TestContent.DefaultConfig());
                var c = w.Characters.Values.First();
                var clock = w.Clock.Now;
                w.Clock.AdvanceGame(clock.StartOfDay.AddHours(13).TotalSeconds - clock.TotalSeconds + (clock.Hour >= 13 ? GameDateTime.SecondsPerDay : 0));
                w.Dispatch.AdvanceTo(w.Clock.Now);
                var store = w.Businesses.Values.OrderBy(b => b.Id).First(b => b.TemplateId == "corner_store");
                var place = w.Geography.GetPlace(store.Place);
                c.LastPosition = place.Position;
                var r = w.Crimes.RobStore(c, store, new ConcealmentState { FaceConcealment = 0.95f });
                Assert.IsTrue(r.Reported);
                Assert.IsTrue(w.Emergency.Incidents.Any(i => i.Kind == EmergencyKind.Crime && i.CrimeIncident == r.Incident.Id && i.Priority == 1), "a robbery is a priority-1 call");
                if (!stay) c.LastPosition = new WorldPosition(place.Position.X + 400f, 0f, place.Position.Z + 400f);
                Minutes(w, 30);
                return c.Record.InCustody;
            }
            Assert.IsTrue(Arrested(true));
            Assert.IsFalse(Arrested(false));
        }

        private PropertyRecord House(World w) => w.Properties.All.OrderBy(p => p.Id).First(p => p.Kind == PropertyKind.House);

        [Test]
        public void Fire_IsFoughtAndPutOut_Deterministically()
        {
            float Damage(World w, out EmergencyIncident fire)
            {
                var house = House(w);
                w.Dispatch.AdvanceTo(w.Clock.Now);
                fire = w.Dispatch.ReportFire(house, 0.3f, "kitchen");
                Assert.AreEqual(2, fire.Units.Count(id => w.Emergency.Unit(id).Service == EmergencyService.Fire));
                Minutes(w, 180);
                return 1f - house.Condition;
            }
            var a = Damage(_world, out var fireA);
            var b = Damage(Create("ems", TestContent.DefaultConfig()), out _);
            Assert.AreEqual(IncidentStatus.Resolved, fireA.Status);
            Assert.AreEqual("Fire extinguished.", fireA.Outcome);
            Assert.Greater(a, 0f);
            Assert.AreEqual(a, b, 1e-6, "same seed, same fire");
            Assert.Greater(House(_world).DamageRepairCents, 0, "fire damage is insurable");
        }

        [Test]
        public void Fire_WithNoEnginesAvailable_BurnsTheBuildingDown()
        {
            _world.Emergency.Units.RemoveAll(u => u.Service == EmergencyService.Fire);
            var house = House(_world);
            _world.Dispatch.AdvanceTo(_world.Clock.Now);
            var fire = _world.Dispatch.ReportFire(house, 0.5f, "unattended candle");
            Minutes(_world, 24 * 60);
            Assert.AreEqual(DamageState.Destroyed, house.Damage);
            Assert.AreEqual("Building lost.", fire.Outcome);
            Assert.GreaterOrEqual(_world.Emergency.Stats.BuildingsLost, 1);
            Assert.IsTrue(_world.History.Recent.Any(r => r.Headline.StartsWith("Fire destroys")));
        }

        [Test]
        public void DownedPlayer_IsTransported_BilledThroughHealthCover_AndDischarged()
        {
            _world.AdminGrant(_player.CheckingAccount, Money.FromDollars(20000L), "t", "t");
            Assert.IsTrue(_world.Finance.BuyInsurance(_player, InsuranceKind.Health, EntityId.None, Money.Zero, "h").Success);
            var insurer = _world.Ledger.BalanceOf(_world.Accounts.InsurerAccount);
            var hospital = _world.Ledger.BalanceOf(_world.Accounts.HospitalAccount);
            _world.Dispatch.AdvanceTo(_world.Clock.Now);
            _world.Dispatch.CharacterDowned(_player, PlaceOf(_world, PlaceKind.Park).Position, 0.6f, "fell from a roof");
            Assert.AreEqual(InjuryState.Incapacitated, _player.Injury);
            Minutes(_world, 120);
            Assert.AreEqual(InjuryState.Hospitalized, _player.Injury);
            var bill = MedicalBilling.Bill(0.6f, true, _world.Macro.PriceLevel);
            Assert.AreEqual(hospital.Cents + bill, _world.Ledger.BalanceOf(_world.Accounts.HospitalAccount).Cents);
            Assert.Less(_world.Ledger.BalanceOf(_world.Accounts.InsurerAccount).Cents, insurer.Cents, "the insurer paid its share");
            Assert.AreEqual(0, _player.MedicalDebtCents);
            Minutes(_world, (MedicalBilling.StayDays(0.6f) + 1) * 24 * 60);
            Assert.AreEqual(InjuryState.Healthy, _player.Injury);
            Assert.AreEqual(1f, _player.Health);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void UninsuredBrokePatient_GetsMedicalDebt_ThatHurtsCredit()
        {
            var all = _world.Ledger.BalanceOf(_player.CheckingAccount);
            _world.Transactions.Execute(new WorldTransaction { Money = LedgerTransaction.Transfer(_player.CheckingAccount, _world.Accounts.External, all, TransactionReason.Purchase) });
            var before = _world.Finance.Credit(_player).Score;
            _world.Dispatch.AdvanceTo(_world.Clock.Now);
            _world.Dispatch.CharacterDowned(_player, PlaceOf(_world, PlaceKind.Park).Position, 0.5f, "collision");
            Minutes(_world, 120);
            Assert.Greater(_player.MedicalDebtCents, 0);
            Assert.Less(_world.Finance.Credit(_player).Score, before);
            _world.AdminGrant(_player.CheckingAccount, new Money(_player.MedicalDebtCents), "t", "t");
            Assert.IsTrue(_world.Finance.PayMedicalDebt(_player, new Money(_player.MedicalDebtCents), "debt").Success);
            Assert.AreEqual(before, _world.Finance.Credit(_player).Score);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Permadeath_EndsTheCharactersLife()
        {
            var config = TestContent.DefaultConfig();
            config.Gameplay.DeathRule = DeathRule.Permadeath;
            var w = Create("hardcore", config);
            var c = w.Characters.Values.First();
            Assert.IsNull(w.Dispatch.CharacterDowned(c, new WorldPosition(), 1f, "shot"));
            Assert.AreEqual(InjuryState.Dead, c.Injury);
        }

        [Test]
        public void SurgeOfCalls_IsQueuedByPriority_AndEveryoneIsServed()
        {
            var park = PlaceOf(_world, PlaceKind.Park);
            _world.Dispatch.AdvanceTo(_world.Clock.Now);
            var ambulances = _world.Emergency.Units.Count(u => u.Service == EmergencyService.Medical);
            var calls = Enumerable.Range(0, ambulances + 6)
                .Select(i => _world.Dispatch.Report(EmergencyKind.Medical, park.Position, i % 2 == 0 ? 3 : 1, "Mass casualty " + i, severity: 0.3f)).ToList();
            Assert.IsTrue(calls.Any(c => c.Status == IncidentStatus.Queued), "more calls than ambulances");
            Minutes(_world, 6 * 60);
            Assert.IsTrue(calls.All(c => c.Status == IncidentStatus.Resolved));
            var firstServedLate = calls.Where(c => c.Priority == 3).Max(c => c.ClosedSecond);
            var urgentServed = calls.Where(c => c.Priority == 1).Max(c => c.ClosedSecond);
            Assert.LessOrEqual(urgentServed, firstServedLate, "priority-1 calls are cleared first");
        }

        [Test]
        public void BackgroundCalls_ComeInAtRealisticRates_AndAreAnswered()
        {
            var sim = new WorldSimulation(_world);
            for (var i = 0; i < 2; i++) sim.AdvanceDays(30);
            var stats = _world.Emergency.Stats;
            Assert.Greater(stats.Calls, 5);
            Assert.Less(stats.Calls, 400);
            Assert.Greater(stats.Responded, stats.Calls / 2);
            Assert.Less(stats.AverageResponseMinutes, 15f);
            var stale = _world.Emergency.Incidents.Count(i => i.Open && _world.Clock.Now.TotalSeconds - i.ReportSecond > GameDateTime.SecondsPerDay);
            Assert.AreEqual(0, stale, "nothing is left waiting for a day");
        }

        [Test]
        public void EmergencyState_SurvivesSaveAndLoad()
        {
            var dir = TestContent.TempDirectory("ems-save");
            var saves = new WorldSaveSystem(dir);
            EntityId callId;
            int units;
            using (var journal = saves.OpenJournal())
            {
                var w = WorldGenerator.Create("emsave", TestContent.DefaultConfig(), TestContent.Load(), journal);
                w.Dispatch.AdvanceTo(w.Clock.Now);
                var call = w.Dispatch.Report(EmergencyKind.Medical, PlaceOf(w, PlaceKind.Park).Position, 2, "test", severity: 0.4f);
                callId = call.Id;
                units = w.Emergency.Units.Count;
                saves.Save(w);
            }
            using (var journal = saves.OpenJournal())
            {
                var w = saves.Load(TestContent.Load(), journal).World;
                Assert.AreEqual(units, w.Emergency.Units.Count);
                var call = w.Emergency.Get(callId);
                Assert.IsNotNull(call);
                Assert.AreEqual(IncidentStatus.Dispatched, call.Status);
                Minutes(w, 60);
                Assert.AreEqual(IncidentStatus.Resolved, call.Status);
            }
        }
    }
}
