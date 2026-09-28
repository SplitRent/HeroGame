using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Phone;
    using HeroGame.Core.Presentation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Simulation;
    using HeroGame.Persistence.Settings;

    /// <summary>Phase 24 player UI logic: settings, notifications, the property screen and the inventory screen.</summary>
    public class PlayerUiTests
    {
        // ---------------------------------------------------------------- settings

        [Test]
        public void Settings_RoundTrip_ClampNonsense_AndSurviveDamagedFiles()
        {
            var dir = TestContent.TempDirectory("settings");
            var store = new SettingsStore(dir);
            Assert.AreEqual(new GameSettings().FieldOfView, store.Load().FieldOfView, "no file: defaults");

            var s = new GameSettings { MusicVolume = 0.25f, InvertY = true, Quality = QualityPreset.Low, TargetFps = 144, Units = UnitSystem.Metric, Clock24h = true, FieldOfView = 85f };
            store.Save(s);
            var back = store.Load();
            Assert.AreEqual(0.25f, back.MusicVolume);
            Assert.IsTrue(back.InvertY);
            Assert.AreEqual(QualityPreset.Low, back.Quality);
            Assert.AreEqual(144, back.TargetFps);
            Assert.AreEqual(UnitSystem.Metric, back.Units);
            Assert.AreEqual(85f, back.FieldOfView);

            var wild = new GameSettings { MasterVolume = 7f, MouseSensitivity = float.NaN, FieldOfView = 10f, TargetFps = 5, UiScale = 9f, Quality = (QualityPreset)42 }.Clamp();
            Assert.AreEqual(1f, wild.MasterVolume);
            Assert.AreEqual(1f, wild.MouseSensitivity, "NaN falls back to the default");
            Assert.AreEqual(GameSettings.MinFov, wild.FieldOfView);
            Assert.AreEqual(GameSettings.MinFps, wild.TargetFps);
            Assert.AreEqual(GameSettings.MaxUiScale, wild.UiScale);
            Assert.AreEqual(QualityPreset.High, wild.Quality);
            Assert.AreEqual(0, new GameSettings { TargetFps = 0 }.Clamp().TargetFps, "0 means uncapped");

            // One bad value costs only that value; a corrupt file costs nothing but the file.
            var map = s.ToMap();
            map["display.fov"] = "banana";
            map["display.quality"] = "Cinematic";
            map["something.from.the.future"] = "1";
            var partial = GameSettings.FromMap(map);
            Assert.AreEqual(new GameSettings().FieldOfView, partial.FieldOfView);
            Assert.AreEqual(new GameSettings().Quality, partial.Quality);
            Assert.AreEqual(0.25f, partial.MusicVolume);
            File.WriteAllText(store.FilePath, "{ not json");
            Assert.AreEqual(new GameSettings().MusicVolume, store.Load().MusicVolume);
        }

        [Test]
        public void Settings_FormatUnitsAndClock()
        {
            var us = new GameSettings();
            Assert.AreEqual("0.6 mi", us.FormatDistance(1000f));
            Assert.AreEqual("164 ft", us.FormatDistance(50f));
            Assert.AreEqual("86°F", us.FormatTemperature(30f));
            Assert.AreEqual("9:05 PM", us.FormatTime(21, 5));
            Assert.AreEqual("12:00 AM", us.FormatTime(0, 0));
            var metric = new GameSettings { Units = UnitSystem.Metric, Clock24h = true };
            Assert.AreEqual("1.0 km", metric.FormatDistance(1000f));
            Assert.AreEqual("650 m", metric.FormatDistance(650f));
            Assert.AreEqual("30°C", metric.FormatTemperature(30f));
            Assert.AreEqual("21:05", metric.FormatTime(21, 5));
        }

        // ---------------------------------------------------------------- notifications

        [Test]
        public void Toasts_FoldRepeats_LimitTheScreen_AndLetDangerJumpTheQueue()
        {
            var q = new ToastQueue { DurationSeconds = 5 };
            q.Push(ToastKind.Money, "Bank", "Paycheck deposited", 0);
            q.Push(ToastKind.Money, "Bank", "Paycheck deposited", 1);
            q.Push(ToastKind.Money, "Bank", "Paycheck deposited", 2);
            Assert.AreEqual(1, q.Visible.Count, "repeats fold");
            Assert.AreEqual("Paycheck deposited  ×3", q.Visible[0].Text);

            for (var i = 0; i < 5; i++) q.Push(ToastKind.Info, "News", "Story " + i, 3);
            Assert.AreEqual(ToastQueue.MaxVisible, q.Visible.Count);
            Assert.AreEqual(2, q.WaitingCount);
            q.Push(ToastKind.Danger, "PAPD", "You are wanted", 3);
            q.Push(ToastKind.Info, "News", "Story 9", 3);

            q.Update(7.5); // the paycheck (kept alive until t=7) and the stories from t=3 (until t=8) → paycheck gone
            Assert.IsTrue(q.Visible.Any(t => t.Kind == ToastKind.Danger), "the danger is shown before older news");
            Assert.IsFalse(q.Visible.Any(t => t.Body == "Paycheck deposited"));
            q.Update(100);
            q.Update(200);
            q.Update(300);
            Assert.AreEqual(0, q.WaitingCount);
            Assert.IsEmpty(q.Visible);

            for (var i = 0; i < 100; i++) q.Push(ToastKind.Info, "Spam", "n" + i, 400);
            Assert.LessOrEqual(q.WaitingCount, ToastQueue.MaxWaiting, "a flood cannot grow without bound");

            Assert.AreEqual(ToastKind.Danger, ToastQueue.KindFor(MessageCategory.Emergency, "Evacuate"));
            Assert.AreEqual(ToastKind.Warning, ToastQueue.KindFor(MessageCategory.Bank, "Your loan payment is overdue."));
            Assert.AreEqual(ToastKind.Money, ToastQueue.KindFor(MessageCategory.Bank, "Deposit received."));
        }

        // ---------------------------------------------------------------- property screen and inventory

        private static (World world, ServerCharacter me) Owner()
        {
            var w = WorldGenerator.Create("ui", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            var me = w.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 91), Character = new CharacterIdentity { FirstName = "Rosa" } }, new WorldPosition());
            return (w, me);
        }

        [Test]
        public void Portfolio_TotalsValueRentAndDebt_AndFlagsWhatNeedsAttention()
        {
            var (w, me) = Owner();
            Assert.IsEmpty(PlayerViews.PortfolioOf(w, me).Entries);

            var house = w.Properties.All.OrderBy(p => p.Id).First(p => p.ForSale && p.Kind == PropertyKind.House);
            Assert.IsTrue(w.AdminGrant(me.CheckingAccount, new Money(house.ListingPriceCents * 2), "admin", "test").Success);
            var seller = w.AccountFor(w.Ownership.OwnerOf(house.Id));
            Assert.IsTrue(w.Properties.Purchase(house.Id, me.CharacterId, me.CheckingAccount, seller, w.Accounts.Treasury, w.Clock.Now, "buy-ui").Success);

            var p = PlayerViews.PortfolioOf(w, me);
            Assert.AreEqual(1, p.Entries.Count);
            var e = p.Entries[0];
            Assert.AreEqual(house.Address, e.Address);
            Assert.AreEqual(house.MarketValue, p.TotalValue);
            Assert.AreEqual(Money.Zero, e.MortgageOwed);
            Assert.IsTrue(e.Alerts.Contains("Uninsured"));

            // A tenant who has missed rent, unpaid tax and storm damage all show up.
            house.Tenancy = new Tenancy { Tenant = EntityId.Create(EntityKind.Character, 777), MonthlyRentCents = 150000, MissedPayments = 1 };
            house.TaxArrearsCents = 42000;
            w.Properties.ApplyDamage(house, 0.5f);
            e = PlayerViews.PortfolioOf(w, me).Entries[0];
            Assert.AreEqual(new Money(150000), e.MonthlyRentIncome);
            Assert.AreEqual(1, e.OccupiedUnits);
            Assert.AreEqual(1, e.TenantsBehindOnRent);
            Assert.IsTrue(e.Alerts.Any(a => a.StartsWith("Property tax overdue")));
            Assert.IsTrue(e.Alerts.Any(a => a.Contains("behind on rent")));
            Assert.IsTrue(e.Alerts.Any(a => a.StartsWith("Damaged") || a.StartsWith("Destroyed")));
            Assert.AreEqual(new Money(150000), PlayerViews.PortfolioOf(w, me).MonthlyNet, "no mortgage or insurance yet");
        }

        [Test]
        public void Inventory_StacksLikeItems_KeepsStolenGoodsApart_AndFlagsContraband()
        {
            var (w, me) = Owner();
            me.Inventory.Add(new InventoryStack { ItemId = "laptop", Quantity = 1 });
            me.Inventory.Add(new InventoryStack { ItemId = "laptop", Quantity = 2 });
            me.Inventory.Add(new InventoryStack { ItemId = "laptop", Quantity = 1, Stolen = true });
            me.Inventory.Add(new InventoryStack { ItemId = "lockpick_set", Quantity = 1 });
            me.Inventory.Add(new InventoryStack { ItemId = "mystery_item", Quantity = 1 });
            me.Inventory.Add(new InventoryStack { ItemId = "crowbar", Quantity = 0 });

            var lines = PlayerViews.InventoryOf(w.Content, me);
            Assert.AreEqual(4, lines.Count, "clean laptops stack, the stolen one is separate, empty stacks are hidden");
            var clean = lines.Single(l => l.ItemId == "laptop" && !l.Stolen);
            Assert.AreEqual(3, clean.Quantity);
            Assert.AreEqual("Laptop computer", clean.Name);
            Assert.AreEqual(new Money(3 * 65000), clean.TotalValue);
            Assert.IsTrue(lines.Single(l => l.ItemId == "laptop" && l.Stolen).Confiscatable);
            Assert.IsTrue(lines.Single(l => l.ItemId == "lockpick_set").Illegal);
            Assert.AreEqual("misc", lines.Single(l => l.ItemId == "mystery_item").Category, "unknown items still show");
            CollectionAssert.AreEqual(lines.Select(l => l.Category + l.Name).OrderBy(x => x, System.StringComparer.Ordinal).ToList(), lines.Select(l => l.Category + l.Name).ToList());
        }
    }
}
