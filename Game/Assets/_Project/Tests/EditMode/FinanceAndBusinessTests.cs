using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Building;
    using HeroGame.Core.Business;
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.Property;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Time;
    using HeroGame.Core.World;
    using HeroGame.Persistence.Saves;

    public class FinanceAndBusinessTests
    {
        private World _world;
        private ServerCharacter _player;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("finance", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _player = _world.CreateCharacter(Account("Rae"), new WorldPosition());
        }

        private static AccountProfile Account(string name) =>
            new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, (ulong)name.GetHashCode() & 0xFFFF), Character = new CharacterIdentity { FirstName = name } };

        private static void Grant(World world, ServerCharacter c, long cents) =>
            Assert.IsTrue(world.AdminGrant(c.CheckingAccount, new Money(cents), "test", "funds").Success);

        private void Grant(long cents) => Grant(_world, _player, cents);

        private void Drain(World world, ServerCharacter c)
        {
            var all = world.Ledger.BalanceOf(c.CheckingAccount);
            if (all.Cents > 0)
                world.Transactions.Execute(new WorldTransaction { Money = LedgerTransaction.Transfer(c.CheckingAccount, world.Accounts.External, all, TransactionReason.Purchase) });
        }

        private static PropertyRecord HomeForSale(World world)
        {
            PropertyRecord best = null;
            foreach (var p in world.Properties.All)
                if (p.Kind == PropertyKind.House && p.ForSale && (best == null || p.Id.CompareTo(best.Id) < 0)) best = p;
            return best;
        }

        private static PropertyRecord BuyHome(World world, ServerCharacter c)
        {
            var home = HomeForSale(world);
            Grant(world, c, home.ListingPriceCents * 2);
            Assert.IsTrue(world.Properties.Purchase(home.Id, c.CharacterId, c.CheckingAccount, world.Accounts.Treasury, world.Accounts.Treasury, world.Clock.Now, "home-" + c.CharacterId).Success);
            return home;
        }

        /// <summary>Advances in month steps (the simulation caps a single catch-up at <see cref="WorldSimulation.MaxCatchUpDays"/>).</summary>
        private static void AdvanceDays(World world, int days)
        {
            var sim = new WorldSimulation(world);
            while (days > 0)
            {
                var step = System.Math.Min(30, days);
                sim.AdvanceDays(step);
                days -= step;
            }
        }

        // ------------------------------------------------------------------ underwriting

        [Test]
        public void Underwriter_ScoresHistory_AndEnforcesLoanToValueAndAffordability()
        {
            var borrower = EntityId.Create(EntityKind.Character, 9);
            var book = new LoanBook();
            var fresh = Underwriter.Profile(borrower, book, 0);
            book.Restore(new Loan { Id = EntityId.Create(EntityKind.Loan, 1), Borrower = borrower, Status = LoanStatus.PaidOff });
            book.Restore(new Loan { Id = EntityId.Create(EntityKind.Loan, 2), Borrower = borrower, Status = LoanStatus.PaidOff });
            var good = Underwriter.Profile(borrower, book, 400);
            book.Restore(new Loan { Id = EntityId.Create(EntityKind.Loan, 3), Borrower = borrower, Status = LoanStatus.Defaulted });
            var burned = Underwriter.Profile(borrower, book, 400);
            Assert.Greater(good.Score, fresh.Score);
            Assert.Less(burned.Score, good.Score - 100);

            var macro = new MacroEconomyState();
            var app = new LoanApplication { Kind = LoanKind.Mortgage, PrincipalCents = 24000000, TermMonths = 360, CollateralValueCents = 30000000, MonthlyIncomeCents = 900000 };
            var offer = Underwriter.Quote(app, good, macro);
            Assert.IsTrue(offer.Approved, offer.Reason);
            Assert.AreEqual(Loan.ComputeMonthlyPayment(24000000, offer.AnnualRate, 360), offer.MonthlyPaymentCents);

            app.PrincipalCents = 28000000; // 93 % LTV
            Assert.IsFalse(Underwriter.Quote(app, good, macro).Approved, "LTV above the limit");
            app.PrincipalCents = 24000000;
            app.MonthlyIncomeCents = 200000;
            Assert.IsFalse(Underwriter.Quote(app, good, macro).Approved, "payment above 43 % of income");
            app.LiquidAssetsCents = offer.MonthlyPaymentCents * 40;
            Assert.IsTrue(Underwriter.Quote(app, good, macro).Approved, "asset-depletion underwriting");

            var worse = Underwriter.Quote(new LoanApplication { Kind = LoanKind.Mortgage, PrincipalCents = 10000000, TermMonths = 360, CollateralValueCents = 30000000, MonthlyIncomeCents = 900000 },
                new CreditProfile { Score = 640 }, macro);
            Assert.Greater(worse.AnnualRate, offer.AnnualRate, "weaker credit pays more");
            Assert.IsFalse(Underwriter.Quote(app, burned, macro).Approved, "defaulters are declined");
        }

        // ------------------------------------------------------------------ banking

        [Test]
        public void Mortgage_PurchaseIsAtomic_AndRecordsTheLoanAgainstTheHome()
        {
            var home = HomeForSale(_world);
            var price = home.ListingPriceCents;
            var down = new Money(price / 5);
            var tax = _world.Taxes.TransferTax(new Money(price));
            Grant(down.Cents + tax.Cents + price); // enough savings to pass asset-based underwriting
            var quote = _world.Finance.QuoteMortgage(_player, home, down, 360);
            Assert.IsTrue(quote.Approved, quote.Reason);

            var bankBefore = _world.Ledger.BalanceOf(_world.Accounts.BankReserves);
            var cashBefore = _world.Ledger.BalanceOf(_player.CheckingAccount);
            var result = _world.Finance.BuyWithMortgage(_player, home, down, 360, "mortgage-1");
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(_player.CharacterId, _world.Ownership.OwnerOf(home.Id));
            Assert.AreEqual(cashBefore - down - tax, _world.Ledger.BalanceOf(_player.CheckingAccount));
            Assert.AreEqual(bankBefore.Cents - (price - down.Cents), _world.Ledger.BalanceOf(_world.Accounts.BankReserves).Cents);
            var loan = _world.Loans.Loans.Single(l => l.Borrower == _player.CharacterId);
            Assert.AreEqual(LoanKind.Mortgage, loan.Kind);
            Assert.AreEqual(home.Id, loan.Collateral);
            Assert.AreEqual(price - down.Cents, loan.OutstandingCents);
            Assert.IsFalse(home.ForSale);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
            Assert.IsFalse(_world.Finance.BuyWithMortgage(_player, home, down, 360, "mortgage-1").Success, "retries are idempotent");
            Assert.IsTrue(_player.Inbox.Exists(m => m.Body.Contains("mortgage")));
        }

        [Test]
        public void Mortgage_Default_RepossessesAndBankRelistsTheHome()
        {
            var home = HomeForSale(_world);
            var price = home.ListingPriceCents;
            Grant(price / 5 + price / 10 + price);
            Assert.IsTrue(_world.Finance.BuyWithMortgage(_player, home, new Money(price / 5), 360, "m").Success);
            Drain(_world, _player);
            AdvanceDays(_world, 100);
            var loan = _world.Loans.Loans.Single(l => l.Borrower == _player.CharacterId);
            Assert.AreEqual(LoanStatus.Defaulted, loan.Status);
            Assert.AreEqual(_world.Accounts.BankOrganization, _world.Ownership.OwnerOf(home.Id));
            Assert.IsTrue(home.ForSale);
            Assert.Less(home.ListingPriceCents, home.MarketValueCents);
            Assert.IsTrue(_player.Inbox.Exists(m => m.Body.Contains("default")));
            Assert.Less(_world.Finance.Credit(_player).Score, 600, "a default wrecks the credit score");
        }

        [Test]
        public void PersonalLoan_EarlyRepayment_ClosesItAndBuildsCredit()
        {
            Grant(2000000);
            var before = _world.Finance.Credit(_player).Score;
            var take = _world.Finance.TakeLoan(_player, LoanKind.Personal, Money.FromDollars(5000L), 24, EntityId.None, "p1");
            Assert.IsTrue(take.Success, take.Error);
            var loan = _world.Loans.Loans.Single(l => l.Borrower == _player.CharacterId);
            Assert.IsFalse(_world.Finance.TakeLoan(_player, LoanKind.Vehicle, Money.FromDollars(5000L), 24, EntityId.None, "p2").Success, "vehicle loans need collateral");

            Assert.IsTrue(_world.Finance.Repay(_player, loan, Money.FromDollars(2000L), "r1").Success);
            Assert.AreEqual(300000, loan.OutstandingCents);
            Assert.IsTrue(_world.Finance.Repay(_player, loan, Money.FromDollars(99999L), "r2").Success, "overpayment is capped at the balance");
            Assert.AreEqual(LoanStatus.PaidOff, loan.Status);
            Assert.AreEqual(0, loan.OutstandingCents);
            Assert.Greater(_world.Finance.Credit(_player).Score, before);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Savings_OpenOnce_TransferAndEarnMonthlyInterest()
        {
            Grant(1000000);
            var savings = _world.Finance.SavingsOf(_player);
            if (!savings.IsValid)
            {
                var open = _world.Finance.OpenSavings(_player, "s");
                Assert.IsTrue(open.Success, open.Error);
                savings = _world.Finance.SavingsOf(_player);
            }
            Assert.IsFalse(_world.Finance.OpenSavings(_player, "s2").Success, "one savings account per character");
            var start = _world.Ledger.BalanceOf(savings).Cents;
            Assert.IsTrue(_world.Finance.TransferOwn(_player, _player.CheckingAccount, savings, Money.FromDollars(8000L), "t").Success);
            Assert.IsFalse(_world.Finance.TransferOwn(_player, _player.CheckingAccount, _world.Accounts.Treasury, Money.FromDollars(1L), "t2").Success, "only own accounts");
            AdvanceDays(_world, 31);
            var principal = start + 800000;
            Assert.Greater(_world.Ledger.BalanceOf(savings).Cents, principal, "interest credited");
            Assert.Less(_world.Ledger.BalanceOf(savings).Cents, principal + principal / 100, "about one month of a few percent a year");
        }

        // ------------------------------------------------------------------ insurance

        [Test]
        public void PropertyInsurance_PaysRecordedDamageOnce_AfterWaitingPeriod_MinusDeductible()
        {
            var home = BuyHome(_world, _player);
            var quote = _world.Finance.QuoteInsurance(_player, InsuranceKind.Property, home.Id, Money.FromDollars(1000L));
            Assert.IsTrue(quote.Available, quote.Reason);
            var cheaper = _world.Finance.QuoteInsurance(_player, InsuranceKind.Property, home.Id, Money.FromDollars(10000L));
            Assert.Less(cheaper.MonthlyPremiumCents, quote.MonthlyPremiumCents, "a higher deductible is cheaper");
            Assert.IsTrue(_world.Finance.BuyInsurance(_player, InsuranceKind.Property, home.Id, Money.FromDollars(1000L), "ins").Success);
            var policy = _world.Insurance.ForHolder(_player.CharacterId).Single();
            Assert.IsFalse(_world.Finance.QuoteInsurance(_player, InsuranceKind.Property, home.Id, Money.Zero).Available, "no double cover");

            _world.Properties.ApplyDamage(home, 0.2f);
            Assert.IsFalse(_world.Finance.Claim(_player, policy, "c0").Success, "waiting period");
            AdvanceDays(_world, 15);
            var loss = _world.Finance.AssessedLoss(policy);
            Assert.AreEqual(home.DamageRepairCents, loss.Cents);
            var before = _world.Ledger.BalanceOf(_player.CheckingAccount);
            var claim = _world.Finance.Claim(_player, policy, "c1");
            Assert.IsTrue(claim.Success, claim.Error);
            Assert.AreEqual(before.Cents + loss.Cents - 100000, _world.Ledger.BalanceOf(_player.CheckingAccount).Cents);
            Assert.IsFalse(_world.Finance.Claim(_player, policy, "c2").Success, "the same damage cannot be claimed twice");

            // New damage after the claim is claimable on its own; repairing resets the slate.
            Assert.IsTrue(_world.Properties.Repair(home, _player.CharacterId, _player.CheckingAccount, _world.Accounts.Contractors, _world.Clock.Now, _world.Macro.PriceLevel, "fix").Success);
            Assert.AreEqual(0, home.DamageRepairCents);
            Assert.AreEqual(Money.Zero, _world.Finance.AssessedLoss(policy));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Premiums_AreCollectedMonthly_AndCoverLapsesWhenUnpaid()
        {
            Grant(50000);
            Assert.IsTrue(_world.Finance.BuyInsurance(_player, InsuranceKind.Health, EntityId.None, Money.Zero, "h").Success);
            var policy = _world.Insurance.ActiveHealth(_player.CharacterId);
            var insurerBefore = _world.Ledger.BalanceOf(_world.Accounts.InsurerAccount);
            AdvanceDays(_world, 31);
            Assert.AreEqual(insurerBefore.Cents + policy.MonthlyPremiumCents, _world.Ledger.BalanceOf(_world.Accounts.InsurerAccount).Cents);
            Drain(_world, _player);
            AdvanceDays(_world, 61);
            Assert.AreEqual(PolicyStatus.Lapsed, policy.Status);
            Assert.IsTrue(_player.Inbox.Exists(m => m.Body.Contains("lapsed")));
        }

        [Test]
        public void HealthInsurance_SplitsMedicalBills_WithAnnualDeductible()
        {
            Grant(1000000);
            var provider = _world.Accounts.Contractors;
            var uninsured = _world.Finance.PayMedicalBill(_player, Money.FromDollars(1000L), provider, "ER visit", "b0", out var share0);
            Assert.IsTrue(uninsured.Success);
            Assert.AreEqual(Money.Zero, share0);

            Assert.IsTrue(_world.Finance.BuyInsurance(_player, InsuranceKind.Health, EntityId.None, Money.Zero, "h").Success);
            var before = _world.Ledger.BalanceOf(_player.CheckingAccount);
            Assert.IsTrue(_world.Finance.PayMedicalBill(_player, Money.FromDollars(10000L), provider, "Surgery", "b1", out var share).Success);
            Assert.AreEqual(Money.FromDollars(6800L), share, "80 % above the $1,500 deductible");
            Assert.AreEqual(before - Money.FromDollars(3200L), _world.Ledger.BalanceOf(_player.CheckingAccount));
            Assert.IsTrue(_world.Finance.PayMedicalBill(_player, Money.FromDollars(1000L), provider, "Follow-up", "b2", out var share2).Success);
            Assert.AreEqual(Money.FromDollars(800L), share2, "deductible already met this year");
        }

        [Test]
        public void StormDamage_IsDeterministic_AndHitsFloodProneDistrictsHarder()
        {
            var twin = WorldGenerator.Create("finance", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            int a = 0, b = 0;
            for (var h = 0; h < 24; h++)
            {
                a += _world.Finance.ApplyStormDamage(1000 + h, 4f);
                b += twin.Finance.ApplyStormDamage(1000 + h, 4f);
            }
            Assert.Greater(a, 0);
            Assert.AreEqual(a, b);
            double Rate(bool flood)
            {
                int total = 0, hit = 0;
                foreach (var p in _world.Properties.All)
                {
                    var d = _world.Geography.GetDistrict(p.District);
                    if (d == null || (d.FloodRisk >= 0.4f) != flood) continue;
                    total++;
                    if (p.DamageRepairCents > 0) hit++;
                }
                return total == 0 ? 0 : hit / (double)total;
            }
            Assert.Greater(Rate(true), Rate(false));
        }

        // ------------------------------------------------------------------ businesses

        private BusinessRecord ListedBusinessWithBuilding()
        {
            foreach (var b in _world.BusinessOps.ForSale())
                if (b.Property.IsValid && _world.Ownership.OwnerOf(b.Property) == _world.Ownership.OwnerOf(b.Id)) return b;
            Assert.Fail("No listed business with its own building.");
            return null;
        }

        [Test]
        public void BuyingABusiness_TransfersBusinessBuildingAndStaff_Atomically()
        {
            var b = ListedBusinessWithBuilding();
            var price = _world.BusinessOps.PriceOf(b);
            Assert.Greater(price.Property.Cents, 0);
            Drain(_world, _player);
            Grant(price.Total.Cents - 1);
            Assert.IsFalse(_world.BusinessOps.Buy(b, _player, "b0").Success, "a cent short buys nothing");
            Assert.AreNotEqual(_player.CharacterId, _world.Ownership.OwnerOf(b.Id));
            Grant(1);
            var externalBefore = _world.Ledger.BalanceOf(_world.Accounts.External);
            var result = _world.BusinessOps.Buy(b, _player, "b1");
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(Money.Zero, _world.Ledger.BalanceOf(_player.CheckingAccount));
            Assert.AreEqual(_player.CharacterId, _world.Ownership.OwnerOf(b.Id));
            Assert.AreEqual(_player.CharacterId, _world.Ownership.OwnerOf(b.Property));
            Assert.AreEqual(externalBefore + price.Business + price.Cash + price.Property, _world.Ledger.BalanceOf(_world.Accounts.External), "NPC owner cashes out");
            Assert.IsTrue(_world.Geography.GetPlace(b.Place).PlayerStaffed);
            Assert.AreEqual(b.Staff, b.Employees.Count);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));

            // Over two months the NPC labour market never slips people into the player's shop behind their back.
            AdvanceDays(_world, 60);
            foreach (var npc in _world.Population.Ordered)
                if (npc.Workplace == b.Place) CollectionAssert.Contains(b.Employees, npc.Id);
            Assert.IsTrue(_player.Inbox.Exists(m => m.Body.StartsWith("Weekly report")));
        }

        [Test]
        public void Management_HireFirePricesAndCash_AreOwnerControlled()
        {
            var b = ListedBusinessWithBuilding();
            Grant(_world.BusinessOps.PriceOf(b).Total.Cents + 5000000);
            Assert.IsTrue(_world.BusinessOps.Buy(b, _player, "buy").Success);
            var stranger = _world.CreateCharacter(Account("Oz"), new WorldPosition());
            Assert.IsFalse(_world.BusinessOps.SetPrice(b, stranger.CharacterId, 2f).Success);
            Assert.IsTrue(_world.BusinessOps.AddManager(b, _player.CharacterId, stranger.CharacterId).Success);
            Assert.IsTrue(_world.BusinessOps.SetPrice(b, stranger.CharacterId, 9f).Success);
            Assert.AreEqual(BusinessOperations.MaxPriceLevel, b.PriceLevel, "clamped");
            Assert.IsFalse(_world.BusinessOps.Withdraw(b, stranger, Money.FromDollars(1L), "w0").Success, "managers cannot take cash");

            var candidate = _world.BusinessOps.Candidates(b, 5).First();
            var hired = _world.BusinessOps.Hire(b, _player.CharacterId, candidate.Id);
            Assert.IsTrue(hired.Success, hired.Error);
            Assert.AreEqual(b.Place, candidate.Workplace);
            Assert.AreEqual(EmploymentStatus.Employed, candidate.Employment);
            CollectionAssert.Contains(b.Employees, candidate.Id);
            Assert.AreEqual(b.Employees.Count, b.Staff);
            Assert.IsTrue(_world.BusinessOps.Fire(b, _player.CharacterId, candidate.Id).Success);
            Assert.AreEqual(EmploymentStatus.Unemployed, candidate.Employment);
            Assert.IsFalse(candidate.Workplace.IsValid);

            var till = _world.Ledger.BalanceOf(b.Account);
            Assert.IsFalse(_world.BusinessOps.Withdraw(b, _player, till + Money.FromDollars(1L), "w1").Success);
            Assert.IsTrue(_world.BusinessOps.Invest(b, _player, Money.FromDollars(10000L), "i1").Success);
            Assert.IsTrue(_world.BusinessOps.Withdraw(b, _player, Money.FromDollars(10000L), "w2").Success);
            Assert.AreEqual(till, _world.Ledger.BalanceOf(b.Account));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void UnderpaidStaff_QuitMoreOften_ThanWellPaidStaff()
        {
            // Two identical worlds; only the wage level differs.
            int Remaining(float wage)
            {
                var world = WorldGenerator.Create("wages", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
                var owner = world.CreateCharacter(Account("Lu"), new WorldPosition());
                var list = world.BusinessOps.ForSale();
                var b = list.First(x => x.Property.IsValid && world.Ownership.OwnerOf(x.Property) == world.Ownership.OwnerOf(x.Id));
                Grant(world, owner, world.BusinessOps.PriceOf(b).Total.Cents + 100000000);
                Assert.IsTrue(world.BusinessOps.Buy(b, owner, "buy").Success);
                foreach (var npc in world.BusinessOps.Candidates(b, 8)) world.BusinessOps.Hire(b, owner.CharacterId, npc.Id);
                world.BusinessOps.Invest(b, owner, Money.FromDollars(900000L), "cash");
                world.BusinessOps.SetWageLevel(b, owner.CharacterId, wage);
                var start = b.Employees.Count;
                AdvanceDays(world, 365);
                return b.Employees.Count - start;
            }
            Assert.Less(Remaining(0.8f), Remaining(2f));
        }

        private static List<BuildOp> NightclubRenovation(World world, PropertyRecord warehouse)
        {
            var ops = new List<BuildOp>
            {
                new BuildOp { Kind = BuildOpKind.RemoveRoom, TargetId = warehouse.Layout.Rooms[0].Id },
                new BuildOp { Kind = BuildOpKind.AddWall, X0 = 27f, Z0 = 2f, X1 = 27f, Z1 = 47f, WallKind = WallKind.Interior },
                new BuildOp { Kind = BuildOpKind.AddWall, X0 = 67f, Z0 = 2f, X1 = 67f, Z1 = 47f, WallKind = WallKind.Interior },
            };
            var preview = world.Construction.Preview(warehouse.Layout, ops, warehouse, true, 1.0);
            var a = preview.Result.Walls[preview.Result.Walls.Count - 2].Id;
            var b = preview.Result.Walls[preview.Result.Walls.Count - 1].Id;
            ops.Add(new BuildOp { Kind = BuildOpKind.AddOpening, TargetId = a, OpeningKind = OpeningKind.Archway, Offset = 20f, Width = 4f });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddOpening, TargetId = b, OpeningKind = OpeningKind.Door, Offset = 10f, Width = 1f });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddRoom, RoomType = RoomType.Bar, Name = "Bar", Polygon = new List<float> { 2, 2, 27, 2, 27, 47, 2, 47 } });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddRoom, RoomType = RoomType.DanceFloor, Name = "Dance floor", Polygon = new List<float> { 27, 2, 67, 2, 67, 47, 27, 47 } });
            ops.Add(new BuildOp { Kind = BuildOpKind.AddRoom, RoomType = RoomType.Restroom, Name = "Restrooms", Polygon = new List<float> { 67, 2, 72, 2, 72, 47, 67, 47 } });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "bar_counter", X0 = 14f, Z0 = 30f });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "sound_system", X0 = 45f, Z0 = 40f });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "toilet", X0 = 70f, Z0 = 30f });
            ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, CatalogId = "security_camera", X0 = 5f, Z0 = 5f });
            return ops;
        }

        private static PropertyRecord RenovatedWarehouse(World world, ServerCharacter c)
        {
            PropertyRecord warehouse = null;
            foreach (var p in world.Properties.All) if (p.Address.Contains("Gulfmark Cold Storage")) warehouse = p;
            Grant(world, c, warehouse.ListingPriceCents * 2 + 50000000);
            Assert.IsTrue(world.Properties.Purchase(warehouse.Id, c.CharacterId, c.CheckingAccount, world.Accounts.Treasury, world.Accounts.Treasury, world.Clock.Now, "wh").Success);
            var build = world.Build(warehouse, NightclubRenovation(world, warehouse), c, "reno");
            Assert.IsTrue(build.Success, build.Error);
            return warehouse;
        }

        [Test]
        public void FoundingANightclub_OpensItOnTheMap_HiresRealPeople_AndTrades()
        {
            var warehouse = RenovatedWarehouse(_world, _player);
            Assert.IsFalse(_world.BusinessOps.Start(warehouse, "nightclub", "", _player, Money.Zero, "n0").Success, "needs a name");
            Assert.IsFalse(_world.BusinessOps.Start(warehouse, "supermarket", "Tide Mart", _player, Money.Zero, "n1").Success, "layout lacks a supermarket's fixtures");
            var result = _world.BusinessOps.Start(warehouse, "nightclub", "Low Tide", _player, Money.FromDollars(150000L), "n2");
            Assert.IsTrue(result.Success, result.Error);
            var club = _world.BusinessOps.OwnedBy(_player.CharacterId).Single();
            Assert.AreEqual("Low Tide", club.Name);
            Assert.AreEqual(Money.FromDollars(150000L), _world.Ledger.BalanceOf(club.Account));
            var place = _world.Geography.GetPlace(club.Place);
            Assert.AreEqual(PlaceKind.Nightlife, place.Kind);
            Assert.IsTrue(place.PlayerStaffed);
            Assert.IsFalse(_world.BusinessOps.Start(warehouse, "nightclub", "Second Club", _player, Money.Zero, "n3").Success, "one business per building");

            foreach (var npc in _world.BusinessOps.Candidates(club, 8)) Assert.IsTrue(_world.BusinessOps.Hire(club, _player.CharacterId, npc.Id).Success);
            Assert.AreEqual(8, club.Staff);
            AdvanceDays(_world, 8);
            Assert.GreaterOrEqual(club.Reports.Count, 7);
            Assert.Greater(club.LifetimeRevenueCents, 0);
            Assert.IsTrue(_world.History.Recent.Exists(r => r.Headline.Contains("Low Tide")));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void BusinessInterruption_PaysForEmergencyClosures()
        {
            var b = ListedBusinessWithBuilding();
            Grant(_world.BusinessOps.PriceOf(b).Total.Cents + 1000000);
            Assert.IsTrue(_world.BusinessOps.Buy(b, _player, "buy").Success);
            AdvanceDays(_world, 10); // trading history for the valuation
            var cover = _world.Finance.BuyInsurance(_player, InsuranceKind.BusinessInterruption, b.Id, Money.FromDollars(20L), "bi");
            Assert.IsTrue(cover.Success, cover.Error);
            AdvanceDays(_world, 15);

            var day = _world.Today;
            var report = _world.BusinessSim.SimulateDay(b, new BusinessDayContext
            {
                Day = day, ForcedClosure = true, PriceLevel = _world.Macro.PriceLevel, CycleIndex = 1, RevenueMultiplier = 1, WageMultiplier = 1,
                ExternalAccount = _world.Accounts.External, TreasuryAccount = _world.Accounts.Treasury, Timestamp = _world.Clock.Now,
            });
            Assert.IsTrue(report.ForcedClosure);
            var before = _world.Ledger.BalanceOf(b.Account);
            _world.Finance.ProcessDay(day, _world.Clock.Now);
            var expected = System.Math.Max(0, _world.BusinessOps.AverageDailyProfit(b, 30)) + report.FixedCostsCents - 2000;
            Assert.Greater(expected, 0);
            Assert.AreEqual(before.Cents + expected, _world.Ledger.BalanceOf(b.Account).Cents);
            // The next consecutive closed day is the same event: no second deductible.
            _world.BusinessSim.SimulateDay(b, new BusinessDayContext
            {
                Day = day + 1, ForcedClosure = true, PriceLevel = _world.Macro.PriceLevel, CycleIndex = 1, RevenueMultiplier = 1, WageMultiplier = 1,
                ExternalAccount = _world.Accounts.External, TreasuryAccount = _world.Accounts.Treasury, Timestamp = _world.Clock.Now,
            });
            var mid = _world.Ledger.BalanceOf(b.Account);
            _world.Finance.ProcessDay(day + 1, _world.Clock.Now);
            Assert.AreEqual(mid.Cents + expected + 2000, _world.Ledger.BalanceOf(b.Account).Cents);
        }

        // ------------------------------------------------------------------ crash safety

        [Test]
        public void Crash_AfterMortgageAndFounding_RecoversLoanBusinessAndAccount()
        {
            var dir = TestContent.TempDirectory("finance-crash");
            var saves = new WorldSaveSystem(dir);
            EntityId playerId, homeId, clubId;
            long clubCash, checking;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("fcrash", TestContent.DefaultConfig(), TestContent.Load(), journal);
                var c = world.CreateCharacter(Account("Kit"), new WorldPosition());
                playerId = c.CharacterId;
                var warehouse = RenovatedWarehouse(world, c);
                saves.Save(world);

                // After the snapshot: a mortgage purchase and a new business, then the process dies.
                var home = HomeForSale(world);
                homeId = home.Id;
                var m = world.Finance.BuyWithMortgage(c, home, new Money(home.ListingPriceCents / 4), 360, "mortgage");
                Assert.IsTrue(m.Success, m.Error);
                Assert.IsTrue(world.BusinessOps.Start(warehouse, "nightclub", "Crash Pad", c, Money.FromDollars(40000L), "club").Success);
                clubId = world.BusinessOps.OwnedBy(playerId).Single().Id;
                clubCash = world.Ledger.BalanceOf(world.Businesses[clubId].Account).Cents;
                checking = world.Ledger.BalanceOf(c.CheckingAccount).Cents;
            }

            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(TestContent.Load(), journal);
                Assert.IsFalse(load.Report.HasErrors, load.Report.ToString());
                var world = load.World;
                Assert.AreEqual(playerId, world.Ownership.OwnerOf(homeId));
                Assert.IsTrue(world.Loans.Loans.Any(l => l.Borrower == playerId && l.Collateral == homeId), "the loan came back with the money");
                Assert.IsTrue(world.Businesses.ContainsKey(clubId), "the business came back with its capital");
                Assert.AreEqual(clubCash, world.Ledger.BalanceOf(world.Businesses[clubId].Account).Cents);
                Assert.AreEqual(checking, world.Ledger.BalanceOf(world.Characters[playerId].CheckingAccount).Cents);
                Assert.IsTrue(world.Geography.GetPlace(world.Businesses[clubId].Place).PlayerStaffed);
                Assert.IsTrue(world.Ledger.VerifyInvariant(out _));
            }
        }
    }
}
