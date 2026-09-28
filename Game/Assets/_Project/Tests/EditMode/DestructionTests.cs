using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Civic;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Time;
    using HeroGame.Core.Weather;
    using HeroGame.Core.World;
    using HeroGame.Persistence.Saves;

    public class DestructionTests
    {
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("destruction", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
        }

        private DestructionService D => _world.Destructibles;

        private PropInstance First(string kind) => D.All.First(p => p.Kind == kind);

        [Test]
        public void StreetFurniture_IsPlacedFromRoadsAndPlaces_Deterministically()
        {
            Assert.Greater(D.All.Count(p => p.Kind == "street_light"), 20);
            Assert.Greater(D.All.Count(p => p.Kind == "fire_hydrant"), 3);
            var signals = D.All.Where(p => p.Kind == "traffic_signal").ToList();
            Assert.IsNotEmpty(signals);
            Assert.IsTrue(signals.All(s => _world.Roads.Nodes[s.RoadNode].IsIntersection));
            Assert.IsTrue(D.All.Any(p => p.Kind == "bus_shelter") == _world.Geography.Places.Any(p => p.Kind == PlaceKind.TransitStop));
            Assert.IsTrue(D.All.All(p => p.State == PropState.Intact));

            var again = WorldGenerator.Create("destruction", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            Assert.AreEqual(D.All.Count, again.Destructibles.All.Count);
            Assert.AreEqual(D.All.Last().X, again.Destructibles.All.Last().X);

            var nearby = new List<PropInstance>();
            var light = First("street_light");
            D.Query(light.Position, 1f, nearby);
            Assert.Contains(light, nearby);
        }

        [Test]
        public void DamageStates_AndTheirConsequences()
        {
            var light = First("street_light");
            Assert.IsFalse(D.Damage(light, 0.3f, "test"));
            Assert.AreEqual(PropState.Intact, light.State);
            Assert.IsFalse(D.Damage(light, 0.2f, "test"));
            Assert.AreEqual(PropState.Damaged, light.State);
            Assert.IsTrue(D.Damage(light, 5f, "test"));
            Assert.AreEqual(PropState.Destroyed, light.State);
            Assert.IsFalse(D.Damage(light, 5f, "test"), "already down");

            // Darkness: every light near a spot knocked out makes the street dark at night, not by day.
            var spot = light.Position;
            var around = new List<PropInstance>();
            D.Query(spot, 30f, around);
            foreach (var p in around.Where(p => p.Kind == "street_light")) D.Damage(p, 10f, "test");
            var night = GameDateTime.FromCalendar(2030, 5, 7, 23);
            var day = GameDateTime.FromCalendar(2030, 5, 7, 12);
            Assert.AreEqual(0.5f, D.LightingAt(spot, night));
            Assert.AreEqual(1f, D.LightingAt(spot, day));

            // A sheared hydrant floods the street.
            var hydrant = First("fire_hydrant");
            Assert.IsFalse(D.WetAt(hydrant.Position));
            D.Damage(hydrant, 10f, "test");
            Assert.IsTrue(D.WetAt(hydrant.Position));

            // A dead signal slows the roads that meet there.
            var signal = First("traffic_signal");
            var edge = _world.Roads.Edges[_world.Roads.Nodes[signal.RoadNode].Edges[0]];
            var rush = GameDateTime.FromCalendar(2030, 5, 7, 8);
            var before = _world.Traffic.TravelSeconds(edge, rush, _world.Weather.Effects);
            D.Damage(signal, 10f, "test");
            Assert.AreEqual(0.55f, edge.CapacityFactor, 1e-4f);
            Assert.Greater(_world.Traffic.TravelSeconds(edge, rush, _world.Weather.Effects), before);
        }

        [Test]
        public void VehicleImpacts_ScaleWithSpeed_AndBollardsHoldUp()
        {
            var light = First("street_light");
            Assert.IsFalse(D.Impact(light, 2f, 1400f), "a nudge");
            Assert.AreEqual(PropState.Intact, light.State);
            Assert.IsTrue(D.Impact(light, 20f, 1400f), "45 mph takes a pole down");
            var bollard = D.All.FirstOrDefault(p => p.Kind == "bollard");
            if (bollard != null) Assert.IsFalse(D.Impact(bollard, 15f, 1400f), "concrete stops cars");
        }

        [Test]
        public void PublicWorks_RepairsByPriority_PaidFromTheTreasury_AndBudgetCutsSlowIt()
        {
            var lights = D.All.Where(p => p.Kind == "street_light").Take(12).ToList();
            var signal = First("traffic_signal");
            foreach (var l in lights) D.Damage(l, 10f, "test");
            D.Damage(signal, 10f, "test");
            var treasury = _world.Ledger.BalanceOf(_world.Accounts.Treasury);
            D.ProcessDay(_world.Today);
            Assert.AreEqual(PropState.Intact, signal.State, "signals first");
            Assert.AreEqual(1f, _world.Roads.Edges[_world.Roads.Nodes[signal.RoadNode].Edges[0]].CapacityFactor, "traffic flows again");
            var fixedToday = lights.Count(l => l.State == PropState.Intact);
            Assert.AreEqual(9, fixedToday, "ten crews at full funding: the signal and nine lights");
            Assert.Less(_world.Ledger.BalanceOf(_world.Accounts.Treasury).Cents, treasury.Cents);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));

            foreach (var l in lights) D.Damage(l, 10f, "test");
            _world.Civic.Budget.Of(Department.PublicWorks).ServiceLevel = 0.3f;
            D.ProcessDay(_world.Today + 1);
            Assert.AreEqual(3, lights.Count(l => l.State == PropState.Intact), "a gutted public works budget fixes three a day");
        }

        [Test]
        public void HurricaneWinds_BringDownStreetFurniture_Deterministically()
        {
            int Storm(World w)
            {
                w.Weather.State.ActiveSystem = new TropicalSystem { Name = "Test", Category = 4f };
                var current = w.Weather.State.Current;
                current.Kind = WeatherKind.Hurricane;
                w.Weather.State.Current = current;
                var t = w.Clock.Now;
                for (var h = 0; h < 24; h++) w.Destructibles.ProcessHour(t.AddHours(h));
                return w.Destructibles.All.Count(p => p.State == PropState.Destroyed);
            }
            var down = Storm(_world);
            Assert.Greater(down, 0);
            Assert.Greater(_world.Destructibles.All.Count(p => p.Kind == "bus_shelter" && p.State == PropState.Destroyed) +
                           _world.Destructibles.All.Count(p => p.Kind == "street_light" && p.State == PropState.Destroyed), 0);
            Assert.AreEqual(down, Storm(WorldGenerator.Create("destruction", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal())));
        }

        [Test]
        public void BuildingCollapse_DisplacesTenants_ClosesBusinesses_AndTheCityRebuildsWhatPlayersDontOwn()
        {
            var tenant = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 71), Character = new CharacterIdentity { FirstName = "Tia" } }, new WorldPosition());
            var home = _world.Properties.All.OrderBy(p => p.Id).First(p => p.Kind != PropertyKind.Land && p.Units.Count == 0 && !_world.Characters.ContainsKey(_world.Ownership.OwnerOf(p.Id)));
            var landlord = _world.AccountFor(_world.Ownership.OwnerOf(home.Id));
            Assert.IsTrue(_world.AdminGrant(landlord, Money.FromDollars(10000), "admin", "deposit float").Success);
            home.Tenancy = new Tenancy { Tenant = tenant.CharacterId, TenantAccount = tenant.CheckingAccount, LandlordAccount = landlord, MonthlyRentCents = 120000, DepositCents = 120000, StartDay = _world.Today };
            var cash = _world.Ledger.BalanceOf(tenant.CheckingAccount);
            var calls = _world.Emergency.Stats.Calls;

            _world.Properties.ApplyDamage(home, 1f);
            Assert.AreEqual(DamageState.Destroyed, home.Damage);
            Assert.IsTrue(D.IsCollapsed(home.Id));
            Assert.IsNull(home.Tenancy, "the lease ends");
            Assert.AreEqual(cash + new Money(120000), _world.Ledger.BalanceOf(tenant.CheckingAccount), "deposit returned in full");
            Assert.IsTrue(tenant.Inbox.Any(m => m.Body.Contains("collapsed")));
            Assert.Greater(_world.Emergency.Stats.Calls, calls);
            Assert.IsTrue(_world.History.Recent.Any(r => r.Headline.StartsWith("Building collapses")));

            var business = _world.Businesses.Values.OrderBy(b => b.Id).FirstOrDefault(b => b.Property.IsValid && !_world.Characters.ContainsKey(_world.Ownership.OwnerOf(b.Property)));
            if (business != null)
            {
                _world.Properties.ApplyDamage(_world.Properties.Get(business.Property), 1f);
                var sim = new WorldSimulation(_world);
                var day = _world.Today;
                sim.AdvanceDays(1);
                Assert.IsTrue(business.Reports.Any(r => r.Day == day && r.WasClosed), "no trading in a collapsed building");
            }

            for (var d = 1; d <= DestructionService.RebuildDays; d++) D.ProcessDay(_world.Today + d);
            Assert.AreNotEqual(DamageState.Destroyed, home.Damage, "the city's reconstruction grant rebuilt it");
            Assert.IsFalse(D.IsCollapsed(home.Id));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void PropStates_SurviveSaveAndLoad_AndTrafficEffectsAreReapplied()
        {
            var dir = TestContent.TempDirectory("props");
            var saves = new WorldSaveSystem(dir);
            int signalId;
            using (var journal = saves.OpenJournal())
            {
                var w = WorldGenerator.Create("props", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var signal = w.Destructibles.All.First(p => p.Kind == "traffic_signal");
                signalId = signal.Id;
                w.Destructibles.Damage(signal, 10f, "test");
                saves.Save(w);
            }
            using (var journal = saves.OpenJournal())
            {
                var w = saves.Load(TestContent.Load(), journal).World;
                var signal = w.Destructibles.Get(signalId);
                Assert.AreEqual(PropState.Destroyed, signal.State);
                Assert.AreEqual(0.55f, w.Roads.Edges[w.Roads.Nodes[signal.RoadNode].Edges[0]].CapacityFactor, 1e-4f);
            }
        }
    }
}
