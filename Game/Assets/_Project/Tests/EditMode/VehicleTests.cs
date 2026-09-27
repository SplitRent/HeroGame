using System.Collections.Generic;
using System.Text.RegularExpressions;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.Traffic;
using HeroGame.Core.Vehicles;
using HeroGame.Core.Weather;
using HeroGame.Persistence.Saves;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class VehicleTests
    {
        private World _world;
        private ServerCharacter _player;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("vehicles", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _player = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 1), Character = new CharacterIdentity { FirstName = "Sam" } }, new WorldPosition());
            _world.AdminGrant(_player.CheckingAccount, Money.FromDollars(200000L), "test", "vehicle tests");
        }

        private EntityId BusinessAccount(string name)
        {
            foreach (var b in _world.Businesses.Values) if (b.Name == name) return b.Account;
            Assert.Fail("no business " + name);
            return EntityId.None;
        }

        private VehicleRecord Buy(string model = "halden_mira")
        {
            var result = _world.Vehicles.BuyNew(model, _player.CharacterId, _player.CheckingAccount, BusinessAccount("Gulfline Motors"), _world.Accounts.Treasury,
                _world.Clock.Now, "buy-" + model + "-" + _world.Vehicles.Count, 1f, "#7A1E1E", out var v);
            Assert.IsTrue(result.Success, result.Error);
            return v;
        }

        [Test]
        public void Dealership_SaleIsAtomicAndPaysDealerAndCity()
        {
            var before = _world.Ledger.BalanceOf(_player.CheckingAccount);
            var dealerBefore = _world.Ledger.BalanceOf(BusinessAccount("Gulfline Motors"));
            var v = Buy();
            var model = _world.Vehicles.Model("halden_mira");
            var price = new Money(model.BasePriceCents);
            var expected = price + _world.Taxes.SalesTax(price) + _world.Taxes.VehicleRegistrationFee;
            Assert.AreEqual(before - expected, _world.Ledger.BalanceOf(_player.CheckingAccount));
            Assert.AreEqual(dealerBefore + price, _world.Ledger.BalanceOf(BusinessAccount("Gulfline Motors")));
            Assert.AreEqual(_player.CharacterId, _world.Ownership.OwnerOf(v.Id));
            Assert.IsTrue(Regex.IsMatch(v.Plate, "^[A-Z]{3}-[0-9]{4}$"), v.Plate);
            Assert.IsTrue(_world.Vehicles.IsRegistered(v, _world.Today + 300));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Dealership_RefusesServiceVehiclesAndInsufficientFunds()
        {
            Assert.IsFalse(_world.Vehicles.BuyNew("vantor_patrol", _player.CharacterId, _player.CheckingAccount, BusinessAccount("Gulfline Motors"), _world.Accounts.Treasury, _world.Clock.Now, "cop", 1f, "", out _).Success);
            var countBefore = _world.Vehicles.Count;
            Assert.IsFalse(_world.Vehicles.BuyNew("aerion_h4", _player.CharacterId, _player.CheckingAccount, BusinessAccount("Gulfline Motors"), _world.Accounts.Treasury, _world.Clock.Now, "heli", 1f, "", out var none).Success);
            Assert.IsNull(none);
            Assert.AreEqual(countBefore, _world.Vehicles.Count, "failed purchase leaves no orphan vehicle");
        }

        [Test]
        public void Fuel_Wear_Damage_AndRepairFlowThroughLocalBusinesses()
        {
            var v = Buy();
            var station = BusinessAccount("Tidewater Fuel Stop");
            var stationBefore = _world.Ledger.BalanceOf(station);
            v.FuelLitres = 5f;
            Assert.IsTrue(_world.Vehicles.Refuel(v, 100f, _player.CheckingAccount, station, _world.Accounts.Treasury, _world.Clock.Now, 1.0, _player.CharacterId).Success);
            Assert.AreEqual(_world.Vehicles.Model(v.ModelId).FuelCapacityLitres, v.FuelLitres, 0.01f, "fills to capacity, not beyond");
            Assert.Greater(_world.Ledger.BalanceOf(station).Cents, stationBefore.Cents);

            Assert.IsTrue(_world.Vehicles.Drive(v, 100f, 60f));
            Assert.Less(v.FuelLitres, 45f);
            Assert.AreEqual(100f, v.OdometerKm, 0.01f);
            v.FuelLitres = 1f;
            Assert.IsFalse(_world.Vehicles.Drive(v, 100f, 60f), "runs out of fuel");

            _world.Vehicles.ApplyCollision(v, 1180f * 20f, frontal: true); // 20 m/s head-on
            Assert.Less(v.BodyHealth, 0.6f);
            Assert.Less(v.EngineHealth, 0.8f);
            var garage = BusinessAccount("Delacroix Auto & Tire");
            var quote = _world.Vehicles.RepairQuote(v, 1.0);
            Assert.Greater(quote.Cents, 0);
            var garageBefore = _world.Ledger.BalanceOf(garage);
            Assert.IsTrue(_world.Vehicles.Repair(v, _player.CharacterId, _player.CheckingAccount, garage, _world.Accounts.Treasury, _world.Clock.Now, 1.0).Success);
            Assert.AreEqual(garageBefore + quote, _world.Ledger.BalanceOf(garage));
            Assert.AreEqual(1f, v.BodyHealth);
        }

        [Test]
        public void Mods_RespectLicencesClassesAndChangePerformance()
        {
            var v = Buy();
            var shop = BusinessAccount("Delacroix Auto & Tire");
            Assert.IsFalse(_world.Vehicles.InstallMod(v, "armor_light", "", _player.CharacterId, _player.CheckingAccount, shop, _world.Accounts.Treasury, _world.Clock.Now, false).Success);
            Assert.IsFalse(_world.Vehicles.InstallMod(v, "susp_lift", "", _player.CharacterId, _player.CheckingAccount, shop, _world.Accounts.Treasury, _world.Clock.Now, false).Success, "lift kits are for trucks");
            Assert.IsTrue(_world.Vehicles.InstallMod(v, "engine_s2", "", _player.CharacterId, _player.CheckingAccount, shop, _world.Accounts.Treasury, _world.Clock.Now, false).Success);
            Assert.IsTrue(_world.Vehicles.InstallMod(v, "paint_matte", "#101010", _player.CharacterId, _player.CheckingAccount, shop, _world.Accounts.Treasury, _world.Clock.Now, false).Success);
            _world.Vehicles.Performance(v, out var power, out _, out _);
            Assert.AreEqual(1.16f, power, 0.001f);
            Assert.AreEqual("#101010", v.ColorHex);
            Assert.IsTrue(_world.Vehicles.InstallMod(v, "engine_s3", "", _player.CharacterId, _player.CheckingAccount, shop, _world.Accounts.Treasury, _world.Clock.Now, false).Success);
            Assert.AreEqual(1, v.Mods.FindAll(m => m.Slot == ModSlot.Engine).Count, "one part per slot");
        }

        [Test]
        public void Garages_HaveCapacityAndRequireOwnership()
        {
            var house = default(PropertyRecord);
            foreach (var p in _world.Properties.All) if (p.Kind == PropertyKind.House && p.ForSale) { house = p; break; }
            Assert.IsNotNull(house);
            var v1 = Buy();
            Assert.IsFalse(_world.Vehicles.StoreInGarage(v1, _player.CharacterId, house).Success, "not our house yet");
            Assert.IsTrue(_world.Properties.Purchase(house.Id, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Accounts.Treasury, _world.Clock.Now, "house").Success);
            var v2 = Buy("brava_solano");
            var v3 = Buy("halden_aster");
            Assert.IsTrue(_world.Vehicles.StoreInGarage(v1, _player.CharacterId, house).Success);
            Assert.IsTrue(_world.Vehicles.StoreInGarage(v2, _player.CharacterId, house).Success);
            Assert.IsFalse(_world.Vehicles.StoreInGarage(v3, _player.CharacterId, house).Success, "two-car garage");
            Assert.AreEqual(3, _world.Vehicles.OwnedBy(_player.CharacterId).Count);
        }

        [Test]
        public void Impound_RequiresOwnerToPayFee()
        {
            var v = Buy();
            _world.Vehicles.Impound(v, 25000);
            Assert.IsFalse(v.Drivable);
            Assert.IsTrue(_world.Vehicles.ReleaseFromImpound(v, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Clock.Now).Success);
            Assert.IsTrue(v.Drivable);
        }

        [Test]
        public void World_HasHouseholdCarsAndServiceFleets_ThatPersist()
        {
            var npcCars = 0;
            foreach (var n in _world.Population.Ordered) if (n.Vehicle.IsValid) npcCars++;
            Assert.Greater(npcCars, 60);
            var police = 0;
            foreach (var v in _world.Vehicles.All) if (_world.Vehicles.Model(v.ModelId).Class == VehicleClass.Police) police++;
            Assert.AreEqual(6, police);

            var dir = TestContent.TempDirectory("vehicles");
            var saves = new WorldSaveSystem(dir);
            saves.Save(_world);
            using (var journal = saves.OpenJournal())
            {
                var loaded = saves.Load(TestContent.Load(), journal).World;
                Assert.AreEqual(_world.Vehicles.Count, loaded.Vehicles.Count);
                foreach (var v in _world.Vehicles.All)
                {
                    Assert.AreEqual(v.Plate, loaded.Vehicles.Get(v.Id).Plate);
                    Assert.AreEqual(_world.Ownership.OwnerOf(v.Id), loaded.Ownership.OwnerOf(v.Id));
                }
            }
        }

        [Test]
        public void RoadNetwork_FindsIntersectionsAndRoutesAcrossDistricts()
        {
            var net = _world.Roads;
            var intersections = 0;
            foreach (var n in net.Nodes) if (n.IsIntersection) intersections++;
            Assert.Greater(intersections, 8, "crossing streets are split into intersections");
            var from = net.NearestNode(_world.Geography.FindPlaceByName("Lupe's Corner Market").Position);
            var to = net.NearestNode(_world.Geography.FindPlaceByName("Calloway Chemical Works").Position);
            var route = net.Route(from, to);
            Assert.IsNotNull(route, "Eastwater and Channelside are connected");
            var usesRefineryRoad = false;
            for (var i = 0; i + 1 < route.Count; i++) if (net.EdgeBetween(route[i], route[i + 1]).RoadName == "Refinery Road") usesRefineryRoad = true;
            Assert.IsTrue(usesRefineryRoad);
            var direct = WorldPosition.DistanceXZ(net.Nodes[from].Position, net.Nodes[to].Position);
            Assert.Less(net.RouteLength(route), direct * 2f);
        }

        [Test]
        public void Traffic_PeaksAtRushHourAndFallsInStorms()
        {
            var model = _world.Traffic;
            RoadEdge avenue = null;
            foreach (var e in _world.Roads.Edges) if (e.RoadName == "Tidewater Avenue") { avenue = e; break; }
            var clear = new WeatherEffects { PedestrianDensity = 1f, TrafficSpeedMultiplier = 1f };
            var storm = new WeatherEffects { PedestrianDensity = 0.1f, TrafficSpeedMultiplier = 0.5f, EmergencyDeclared = true };
            var rush = GameDateTime.FromCalendar(2030, 5, 7, 8, 0);
            var night = GameDateTime.FromCalendar(2030, 5, 7, 3, 0);
            Assert.Greater(model.Flow(avenue, rush, clear), model.Flow(avenue, night, clear) * 5f);
            Assert.Greater(model.TravelSeconds(avenue, rush, clear), model.TravelSeconds(avenue, night, clear));
            Assert.Less(model.Flow(avenue, rush, storm), model.Flow(avenue, rush, clear) * 0.3f);

            var observer = _world.Geography.FindPlaceByName("Gulfside Diner").Position;
            var a = model.Materialize(observer, 250f, rush, clear, 40);
            var b = model.Materialize(observer, 250f, rush, clear, 40);
            Assert.Greater(a.Count, 5);
            Assert.LessOrEqual(a.Count, 40);
            for (var i = 0; i < a.Count; i++) Assert.AreEqual(a[i].Seed, b[i].Seed);
        }
    }
}
