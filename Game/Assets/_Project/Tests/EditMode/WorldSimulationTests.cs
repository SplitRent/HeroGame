using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Business;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Time;
    using HeroGame.Core.Weather;
    using HeroGame.Core.World;

    public class WorldSimulationTests
    {
        private static World NewWorld(string id = "sim")
        {
            return WorldGenerator.Create(id, TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
        }

        [Test]
        public void Content_PassesValidation()
        {
            var report = HeroGame.Persistence.Content.ContentLoader.Validate(TestContent.Load());
            Assert.IsFalse(report.HasErrors, report.ToString());
        }

        [Test]
        public void GeneratedWorld_HasInstitutionsBusinessesAndMarket()
        {
            var world = NewWorld();
            Assert.Greater(world.Businesses.Count, 15);
            Assert.Greater(world.Properties.Count, 90);
            Assert.IsTrue(world.Ledger.VerifyInvariant(out _));
            var forSale = 0;
            foreach (var p in world.Properties.All) if (p.ForSale) forSale++;
            Assert.Greater(forSale, 0, "players need something to buy");
            var warehouseListed = false;
            foreach (var p in world.Properties.All) if (p.Address == "Former Gulfmark Cold Storage" && p.ForSale) warehouseListed = true;
            Assert.IsTrue(warehouseListed, "the conversion-candidate warehouse should be on the market");
        }

        [Test]
        public void ThirtyDays_KeepTheLedgerBalancedAndTheWorldAlive()
        {
            var world = NewWorld();
            var sim = new WorldSimulation(world);
            var startDay = world.Today;
            sim.AdvanceDays(30);
            Assert.AreEqual(startDay + 30, world.Today);
            Assert.IsTrue(world.Ledger.VerifyInvariant(out var sum), "sum=" + sum);
            foreach (var b in world.Businesses.Values)
            {
                Assert.GreaterOrEqual(b.Reports.Count, 29, b.Name);
                Assert.AreEqual(world.Today - 1, b.LastSimulatedDay);
            }
            foreach (var npc in world.Population.Ordered)
                if (npc.Alive) Assert.AreEqual(world.Today - 1, npc.LastSimulatedDay);
            Assert.Greater(sim.Stats.DaysSimulated, 29);
        }

        [Test]
        public void OfflineCatchUp_MatchesLiveSimulation()
        {
            var live = NewWorld("offline");
            var offline = NewWorld("offline");
            var liveSim = new WorldSimulation(live);
            var offlineSim = new WorldSimulation(offline);
            for (var i = 0; i < 20 * 24; i++)
            {
                live.Clock.AdvanceGame(GameDateTime.SecondsPerHour);
                liveSim.Update();
            }
            offline.Clock.AdvanceGame(20 * GameDateTime.SecondsPerDay); // server was down for 20 days
            offlineSim.Update();

            Assert.AreEqual(live.Clock.Now, offline.Clock.Now);
            Assert.AreEqual(live.Population.Count, offline.Population.Count);
            foreach (var kv in live.Businesses)
            {
                var other = offline.Businesses[kv.Key];
                Assert.AreEqual(kv.Value.LifetimeRevenueCents, other.LifetimeRevenueCents, kv.Value.Name);
                Assert.AreEqual(live.Ledger.BalanceOf(kv.Value.Account), offline.Ledger.BalanceOf(other.Account));
            }
            for (var i = 0; i < live.Population.Ordered.Count; i++)
            {
                Assert.AreEqual(live.Population.Ordered[i].SavingsCents, offline.Population.Ordered[i].SavingsCents);
                Assert.AreEqual(live.Population.Ordered[i].Employment, offline.Population.Ordered[i].Employment);
            }
            Assert.AreEqual(live.Weather.State.Current.Kind, offline.Weather.State.Current.Kind);
        }

        [Test]
        public void Storms_HurtWeatherSensitiveBusinesses()
        {
            var world = NewWorld();
            var biz = FindBusiness(world, "Low Tide Club");
            var template = world.BusinessSim.Template(biz.TemplateId);
            var clear = SimulateOnce(world, biz, 0f);
            biz.InventoryDays = template.InventoryDays;
            var storm = SimulateOnce(world, biz, 1f);
            Assert.Less(storm.Customers, clear.Customers * 0.8);
            Assert.IsTrue(world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void UnderstaffedBusinesses_ServeFewerCustomers()
        {
            var world = NewWorld();
            var biz = FindBusiness(world, "Gulfside Diner");
            var full = SimulateOnce(world, biz, 0f);
            biz.Staff = 1;
            var thin = SimulateOnce(world, biz, 0f);
            Assert.Less(thin.Customers, full.Customers);
        }

        [Test]
        public void BrokeBusinesses_LoseStaffInsteadOfCreatingMoney()
        {
            var world = NewWorld();
            var biz = FindBusiness(world, "Salt & Thread Boutique");
            // Drain the account.
            var balance = world.Ledger.BalanceOf(biz.Account);
            world.Transactions.Execute(new WorldTransaction { Source = TransactionSource.Simulation, Money = LedgerTransaction.Transfer(biz.Account, world.Accounts.External, balance, TransactionReason.Purchase) });
            var staffBefore = biz.Staff;
            var report = SimulateOnce(world, biz, 0f, footTraffic: 0f); // nobody comes in; wages are still due
            Assert.IsTrue(report.Note.Contains("payroll"), report.Note);
            Assert.Less(biz.Staff, staffBefore);
            Assert.GreaterOrEqual(world.Ledger.BalanceOf(biz.Account).Cents, 0);
            Assert.IsTrue(world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Weather_IsDeterministicAndHurricanesOnlyInSeason()
        {
            var a = new WeatherSystem(99);
            var b = new WeatherSystem(99);
            var t = GameDateTime.FromCalendar(2030, 1, 1);
            var hurricaneHoursOutOfSeason = 0;
            var tropical = 0;
            a.TropicalSystemAnnounced += s => tropical++;
            for (var h = 0; h < 24 * 365 * 3; h++)
            {
                t = t.AddHours(1);
                a.AdvanceTo(t);
                b.AdvanceTo(t);
                Assert.AreEqual(a.State.Current.Kind, b.State.Current.Kind);
                if (a.State.Current.Kind == WeatherKind.Hurricane || a.State.Current.Kind == WeatherKind.TropicalStorm)
                {
                    var announced = a.State.ActiveSystem;
                    var announcedMonth = announced != null ? new GameDateTime(announced.AnnouncedHour * 3600).Month : t.Month;
                    if (announcedMonth < 6 || announcedMonth > 11) hurricaneHoursOutOfSeason++;
                }
            }
            Assert.AreEqual(0, hurricaneHoursOutOfSeason);
            Assert.Greater(tropical, 0, "three seasons should produce at least one tropical system");
            Assert.Less(tropical, 12);
        }

        [Test]
        public void News_IsBuiltFromRealHistory()
        {
            var world = NewWorld();
            world.History.Record(world.Today, HistoryCategory.Crime, 4, "Armed robbery at Tidewater Mercado");
            var edition = NewsDesk.Edition(world.History, world.Today, world.Config.Identity.NewsOrganizations);
            Assert.IsTrue(edition.Exists(a => a.Headline == "Armed robbery at Tidewater Mercado"));
        }

        private static BusinessRecord FindBusiness(World world, string name)
        {
            foreach (var b in world.Businesses.Values) if (b.Name == name) return b;
            Assert.Fail("Business not found: " + name);
            return null;
        }

        private static BusinessDayReport SimulateOnce(World world, BusinessRecord b, float badWeather, float footTraffic = 1f)
        {
            b.LastSimulatedDay = long.MinValue;
            return world.BusinessSim.SimulateDay(b, new BusinessDayContext
            {
                Day = world.Today,
                DayOfWeek = System.DayOfWeek.Saturday,
                FootTraffic = footTraffic,
                BadWeather = badWeather,
                CycleIndex = 1.0,
                PriceLevel = 1.0,
                RevenueMultiplier = 1f,
                WageMultiplier = 1f,
                ExternalAccount = world.Accounts.External,
                TreasuryAccount = world.Accounts.Treasury,
                Timestamp = world.Clock.Now,
            });
        }
    }
}
