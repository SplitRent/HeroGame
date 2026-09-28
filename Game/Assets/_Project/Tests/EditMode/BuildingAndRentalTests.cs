using System.Collections.Generic;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Building;
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Time;

    public class BuildingAndRentalTests
    {
        private World _world;
        private ServerCharacter _player;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("building", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _player = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 1), Character = new CharacterIdentity { FirstName = "Dee" } }, new WorldPosition());
        }

        private void Grant(long dollars) => Assert.IsTrue(_world.AdminGrant(_player.CheckingAccount, Money.FromDollars(dollars), "test", "funds").Success);

        private PropertyRecord FirstHomeForSale()
        {
            foreach (var p in _world.Properties.All) if (p.Kind == PropertyKind.House && p.ForSale) return p;
            return null;
        }

        private PropertyRecord Find(string address)
        {
            foreach (var p in _world.Properties.All) if (p.Address == address) return p;
            Assert.Fail("No property " + address);
            return null;
        }

        [Test]
        public void EveryGeneratedLayout_IsValid()
        {
            var checkedLayouts = 0;
            foreach (var p in _world.Properties.All)
            {
                if (p.Layout == null) continue;
                var report = _world.Construction.Validator.Validate(p.Layout, p, p.Zoning != ZoningType.Residential);
                Assert.IsFalse(report.HasErrors, p.Address + ":\n" + report);
                checkedLayouts++;
            }
            Assert.Greater(checkedLayouts, 90);
        }

        [Test]
        public void Validator_CatchesUnbelievableArchitecture()
        {
            var v = _world.Construction.Validator;
            var shell = BuildingLayout.Shell("t", 10f, 10f, RoomType.Living);

            var offGrid = shell.Clone();
            offGrid.Walls.Add(new Wall { Id = 99, X0 = 3.1f, Z0 = 3f, X1 = 3.1f, Z1 = 6f, Kind = WallKind.Interior });
            StringAssert.Contains("grid", v.Validate(offGrid, null, false).ToString());

            var outside = shell.Clone();
            outside.Walls.Add(new Wall { Id = 99, X0 = 3f, Z0 = 3f, X1 = 40f, Z1 = 3f, Kind = WallKind.Interior });
            StringAssert.Contains("lot boundary", v.Validate(outside, null, false).ToString());

            // A closet walled off with no door is unreachable.
            var sealedRoom = shell.Clone();
            sealedRoom.Walls.Add(new Wall { Id = 90, X0 = 8f, Z0 = 2f, X1 = 8f, Z1 = 5f, Kind = WallKind.Interior });
            sealedRoom.Walls.Add(new Wall { Id = 91, X0 = 8f, Z0 = 5f, X1 = 12f, Z1 = 5f, Kind = WallKind.Interior });
            sealedRoom.Rooms.Add(new Room { Id = 92, Type = RoomType.Storage, Name = "Closet", Polygon = new List<float> { 8, 2, 12, 2, 12, 5, 8, 5 } });
            var report = v.Validate(sealedRoom, null, false);
            StringAssert.Contains("Not reachable", report.ToString());
            sealedRoom.Openings.Add(new Opening { Id = 93, WallId = 91, Kind = OpeningKind.Door, Offset = 1f, Width = 0.9f });
            Assert.IsFalse(v.Validate(sealedRoom, null, false).ToString().Contains("Not reachable"), "adding a door fixes it");

            var wide = BuildingLayout.Shell("w", 30f, 30f, RoomType.Retail);
            wide.Furniture.RemoveAll(f => f.CatalogId == BuildingLayout.ColumnItem);
            StringAssert.Contains("span", v.Validate(wide, null, true).ToString());

            var oneExit = BuildingLayout.Shell("e", 20f, 20f, RoomType.Retail);
            oneExit.Openings.RemoveAt(1);
            StringAssert.Contains("second exit", v.Validate(oneExit, null, true).ToString());

            var furnished = shell.Clone();
            furnished.Furniture.Add(new PlacedFurniture { Id = 50, CatalogId = "sofa", X = 7f, Z = 7f });
            furnished.Furniture.Add(new PlacedFurniture { Id = 51, CatalogId = "armchair", X = 7.5f, Z = 7f });
            furnished.Furniture.Add(new PlacedFurniture { Id = 52, CatalogId = "stove", X = 4f, Z = 9f });
            furnished.Furniture.Add(new PlacedFurniture { Id = 53, CatalogId = "bookshelf", X = 7f, Z = 2.5f }); // in front of the door
            furnished.Furniture.Add(new PlacedFurniture { Id = 54, CatalogId = "rug", X = 30f, Z = 30f });
            var f = v.Validate(furnished, null, false).ToString();
            StringAssert.Contains("Overlaps", f);
            StringAssert.Contains("cannot go in a Living", f);
            StringAssert.Contains("Blocks door", f);
            StringAssert.Contains("inside a room", f);
        }

        [Test]
        public void BuildTools_TurnPointerInputIntoValidOps()
        {
            var shell = BuildingLayout.Shell("t", 10f, 8f, RoomType.Living);
            // Drags snap to the grid and stay axis-aligned.
            var wall = BuildTools.WallFromDrag(0, 7.1f, 2.05f, 7.3f, 9.9f);
            Assert.AreEqual(7f, wall.X0);
            Assert.AreEqual(7f, wall.X1);
            Assert.AreEqual(2f, wall.Z0);
            Assert.AreEqual(10f, wall.Z1);

            // Clicking near the front wall finds it and centres an opening on the pointer, clamped inside the wall.
            var front = BuildTools.NearestWall(shell, 0, 4.1f, 2.3f, 0.6f, out var offset);
            Assert.AreEqual(shell.Walls[0].Id, front.Id);
            Assert.AreEqual(2.1f, offset, 0.01f);
            var door = BuildTools.OpeningAt(front, 0.1f, OpeningKind.Door, 0.9f);
            Assert.AreEqual(0.25f, door.Offset);
            Assert.IsNull(BuildTools.NearestWall(shell, 0, 7f, 6f, 0.6f, out _), "nothing near the middle of the room");

            // Delete picks furniture before walls and never removes an exterior wall.
            shell.Furniture.Add(new PlacedFurniture { Id = 77, CatalogId = "armchair", X = 5f, Z = 5f });
            Assert.AreEqual(BuildOpKind.RemoveFurniture, BuildTools.RemovalAt(shell, _world.Construction.Validator, 0, 5.1f, 5.1f).Kind);
            var onExterior = BuildTools.RemovalAt(shell, _world.Construction.Validator, 0, 11.8f, 6f);
            Assert.AreEqual(BuildOpKind.RemoveRoom, onExterior.Kind);

            // A room drawn with the tool plus a partition and door yields a valid two-room home.
            var ops = new List<BuildOp>
            {
                new BuildOp { Kind = BuildOpKind.RemoveRoom, TargetId = shell.Rooms[0].Id },
                BuildTools.WallFromDrag(0, 8f, 2f, 8f, 10f),
                BuildTools.RoomFromDrag(0, 2f, 2f, 8f, 10f, RoomType.Living),
                BuildTools.RoomFromDrag(0, 12f, 10f, 8f, 2f, RoomType.Bedroom),
            };
            var preview = _world.Construction.Preview(shell, ops, null, false, 1.0);
            var partition = preview.Result.Walls[preview.Result.Walls.Count - 1];
            ops.Add(BuildTools.OpeningAt(partition, 4f, OpeningKind.Door, 0.9f));
            preview = _world.Construction.Preview(shell, ops, null, false, 1.0);
            Assert.IsFalse(preview.Report.HasErrors, preview.Report.ToString());
            Assert.Greater(preview.Cost.Cents, 0L);
        }

        [Test]
        public void WorldBuild_ChargesAtPriceLevel_AndMarksPropertiesDirty()
        {
            Grant(500000);
            var home = FirstHomeForSale();
            Assert.IsTrue(_world.Properties.Purchase(home.Id, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Accounts.Treasury, _world.Clock.Now, "buy-home-2").Success);
            var room = home.Layout.Rooms[0];
            room.Bounds(out var minX, out var minZ, out _, out _);
            var ops = new List<BuildOp> { new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "armchair", X0 = minX + 1.5f, Z0 = minZ + 1.5f } };
            _world.Dirty.Clear();
            var before = _world.Ledger.BalanceOf(_player.CheckingAccount);
            var price = _world.Construction.Validator.Item("armchair").PriceCents;
            Assert.IsTrue(_world.Build(home, ops, _player, "build-1").Success);
            Assert.AreEqual(before.Cents - (long)(price * _world.Macro.PriceLevel), _world.Ledger.BalanceOf(_player.CheckingAccount).Cents);
            Assert.IsTrue(home.Layout.Furniture.Exists(f => f.CatalogId == "armchair"));
            Assert.IsTrue(_world.Dirty.IsDirty(SaveChunks.Properties));
            // Replaying the same request is a no-op for money (idempotency key).
            var after = _world.Ledger.BalanceOf(_player.CheckingAccount);
            _world.Build(home, ops, _player, "build-1");
            Assert.AreEqual(after, _world.Ledger.BalanceOf(_player.CheckingAccount));
        }

        [Test]
        public void WarehouseToNightclub_ConversionEndToEnd()
        {
            Grant(2000000);
            var warehouse = Find("Former Gulfmark Cold Storage");
            Assert.IsTrue(warehouse.ForSale);
            Assert.IsTrue(_world.Properties.Purchase(warehouse.Id, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Accounts.Treasury, _world.Clock.Now, "buy-warehouse").Success);
            var layout = warehouse.Layout;
            var nightclub = _world.BusinessSim.Template("nightclub");

            // Before renovation: an empty warehouse cannot be a nightclub.
            Assert.IsFalse(_world.Construction.ChangeOfUse(warehouse, layout, nightclub, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Clock.Now, "permit-0").Success);

            var original = layout.Rooms[0];
            var ops = new List<BuildOp>
            {
                new BuildOp { Kind = BuildOpKind.RemoveRoom, TargetId = original.Id },
                new BuildOp { Kind = BuildOpKind.AddWall, X0 = 27f, Z0 = 2f, X1 = 27f, Z1 = 47f, WallKind = WallKind.Interior },
                new BuildOp { Kind = BuildOpKind.AddWall, X0 = 67f, Z0 = 2f, X1 = 67f, Z1 = 47f, WallKind = WallKind.Interior },
            };
            var preview = _world.Construction.Preview(layout, ops, warehouse, true, 1.0);
            var partitionA = preview.Result.Walls[preview.Result.Walls.Count - 2].Id;
            var partitionB = preview.Result.Walls[preview.Result.Walls.Count - 1].Id;
            ops.Add(new BuildOp { Kind = BuildOpKind.AddOpening, TargetId = partitionA, OpeningKind = OpeningKind.Archway, Offset = 20f, Width = 4f });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddOpening, TargetId = partitionB, OpeningKind = OpeningKind.Door, Offset = 10f, Width = 1f });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddRoom, RoomType = RoomType.Bar, Name = "Bar", Polygon = new List<float> { 2, 2, 27, 2, 27, 47, 2, 47 } });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddRoom, RoomType = RoomType.DanceFloor, Name = "Dance floor", Polygon = new List<float> { 27, 2, 67, 2, 67, 47, 27, 47 } });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddRoom, RoomType = RoomType.Restroom, Name = "Restrooms", Polygon = new List<float> { 67, 2, 72, 2, 72, 47, 67, 47 } });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "bar_counter", X0 = 14f, Z0 = 30f });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "sound_system", X0 = 45f, Z0 = 40f });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "toilet", X0 = 70f, Z0 = 30f });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "security_camera", X0 = 5f, Z0 = 5f });

            var check = _world.Construction.Preview(layout, ops, warehouse, true, 1.0, "nightclub");
            Assert.IsFalse(check.Report.HasErrors, check.Report.ToString());
            Assert.Greater(check.Cost.Cents, 2000000, "materials + equipment cost real money");

            var contractorBefore = _world.Ledger.BalanceOf(_world.Accounts.Contractors);
            var balanceBefore = _world.Ledger.BalanceOf(_player.CheckingAccount);
            var commit = _world.Construction.Commit(warehouse, layout, ops, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Contractors,
                _world.Clock.Now, 1.0, "reno-1", l => warehouse.Layout = l);
            Assert.IsTrue(commit.Success, commit.Error);
            Assert.AreEqual(balanceBefore - check.Net, _world.Ledger.BalanceOf(_player.CheckingAccount));
            Assert.AreEqual(contractorBefore + check.Net, _world.Ledger.BalanceOf(_world.Accounts.Contractors));
            Assert.AreEqual(3, warehouse.Layout.Rooms.Count);

            var permit = _world.Construction.ChangeOfUse(warehouse, warehouse.Layout, nightclub, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Clock.Now, "permit-1");
            Assert.IsTrue(permit.Success, permit.Error);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Construction_IsAtomic_AndRespectsZoningAndOwnership()
        {
            var house = Find("Former Gulfmark Cold Storage");
            var ops = new List<BuildOp> { new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "sofa", X0 = 10f, Z0 = 10f } };
            Assert.IsFalse(_world.Construction.Commit(house, house.Layout, ops, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Contractors, _world.Clock.Now, 1.0, "x", l => house.Layout = l).Success,
                "cannot build on someone else's property");

            // A residential home can't become a nightclub whatever is built inside.
            PropertyRecord home = null;
            foreach (var p in _world.Properties.All) if (p.Kind == PropertyKind.House && p.ForSale) { home = p; break; }
            Grant(600000);
            Assert.IsTrue(_world.Properties.Purchase(home.Id, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Accounts.Treasury, _world.Clock.Now, "home").Success);
            var result = _world.Construction.ChangeOfUse(home, home.Layout, _world.BusinessSim.Template("nightclub"), _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Clock.Now, "p");
            Assert.IsFalse(result.Success);

            // Drain funds: an unaffordable renovation changes nothing.
            var all = _world.Ledger.BalanceOf(_player.CheckingAccount);
            _world.Transactions.Execute(new WorldTransaction { Money = LedgerTransaction.Transfer(_player.CheckingAccount, _world.Accounts.External, all, TransactionReason.Purchase) });
            var before = home.Layout;
            var expensive = new List<BuildOp> { new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "bed_queen", X0 = 5f, Z0 = 8f } };
            Assert.IsFalse(_world.Construction.Commit(home, home.Layout, expensive, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Contractors, _world.Clock.Now, 1.0, "y", l => home.Layout = l).Success);
            Assert.AreSame(before, home.Layout);
        }

        [Test]
        public void Renting_CollectsRentAndEvictsNonPayers()
        {
            Grant(20000);
            var vacancies = _world.Rentals.Vacancies();
            Assert.Greater(vacancies.Count, 0, "apartment buildings keep some units vacant");
            var (building, unit) = vacancies[0];
            Assert.IsNotNull(unit);
            var landlord = _world.AccountFor(_world.Ownership.OwnerOf(building.Id));
            var landlordBefore = _world.Ledger.BalanceOf(landlord);
            Assert.IsTrue(_world.Rentals.Rent(building, unit.Id, _player.CharacterId, _player.CheckingAccount, landlord, _world.Clock.Now, "lease").Success);
            Assert.AreEqual(landlordBefore + new Money(unit.MonthlyRentCents * 2), _world.Ledger.BalanceOf(landlord));

            var sim = new WorldSimulation(_world);
            sim.AdvanceDays(31);
            Assert.AreEqual(landlordBefore + new Money(unit.MonthlyRentCents * 3), _world.Ledger.BalanceOf(landlord), "second month collected");

            var all = _world.Ledger.BalanceOf(_player.CheckingAccount);
            _world.Transactions.Execute(new WorldTransaction { Money = LedgerTransaction.Transfer(_player.CheckingAccount, _world.Accounts.External, all, TransactionReason.Purchase) });
            sim.AdvanceDays(62);
            Assert.IsNull(unit.Tenancy, "evicted after two missed payments");
            Assert.IsTrue(_player.Inbox.Exists(m => m.Body.Contains("Evicted")));
        }

        [Test]
        public void PropertyTax_ArrearsLeadToTaxSale()
        {
            Grant(600000);
            PropertyRecord home = null;
            foreach (var p in _world.Properties.All) if (p.Kind == PropertyKind.House && p.ForSale) { home = p; break; }
            Assert.IsTrue(_world.Properties.Purchase(home.Id, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Treasury, _world.Accounts.Treasury, _world.Clock.Now, "tax-home").Success);
            var treasuryBefore = _world.Ledger.BalanceOf(_world.Accounts.Treasury);
            var sim = new WorldSimulation(_world);
            sim.AdvanceDays(31);
            Assert.Greater(_world.Ledger.BalanceOf(_world.Accounts.Treasury).Cents, treasuryBefore.Cents, "monthly property tax collected");

            var all = _world.Ledger.BalanceOf(_player.CheckingAccount);
            _world.Transactions.Execute(new WorldTransaction { Money = LedgerTransaction.Transfer(_player.CheckingAccount, _world.Accounts.External, all, TransactionReason.Purchase) });
            sim.AdvanceDays(60);
            Assert.Greater(home.TaxArrearsCents, 0);
            Assert.AreEqual(_player.CharacterId, _world.Ownership.OwnerOf(home.Id), "arrears alone don't take the house");
            sim.AdvanceDays(200);
            Assert.AreNotEqual(_player.CharacterId, _world.Ownership.OwnerOf(home.Id), "six months of arrears → tax sale");
            Assert.IsTrue(home.ForSale);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }
    }
}
