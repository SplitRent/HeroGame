using HeroGame.Core.Config;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class FoundationTests
    {
        [Test]
        public void EntityId_EncodesKindAndRoundTrips()
        {
            var id = EntityId.Create(EntityKind.Npc, 42);
            Assert.AreEqual(EntityKind.Npc, id.Kind);
            Assert.AreEqual(42UL, id.Sequence);
            Assert.AreEqual("Npc:42", id.ToString());
            Assert.AreEqual(id, EntityId.Parse("Npc:42"));
            Assert.IsFalse(EntityId.TryParse("Npc:0", out _));
            Assert.IsFalse(EntityId.TryParse("Bogus:1", out _));
            Assert.IsFalse(EntityId.None.IsValid);
        }

        [Test]
        public void IdAllocator_NeverReusesIds()
        {
            var ids = new IdAllocator();
            var a = ids.Next(EntityKind.Npc);
            var b = ids.Next(EntityKind.Npc);
            var c = ids.Next(EntityKind.Property);
            Assert.AreNotEqual(a, b);
            Assert.AreEqual(1UL, c.Sequence);
            ids.Reserve(EntityId.Create(EntityKind.Npc, 100));
            Assert.AreEqual(101UL, ids.Next(EntityKind.Npc).Sequence);
        }

        [Test]
        public void DeterministicRandom_IsReproducibleAndStreamsAreIndependent()
        {
            var a = DeterministicRandom.For(1, 2, 3);
            var b = DeterministicRandom.For(1, 2, 3);
            var c = DeterministicRandom.For(1, 2, 4);
            var differs = false;
            for (var i = 0; i < 100; i++)
            {
                var va = a.NextULong();
                Assert.AreEqual(va, b.NextULong());
                if (va != c.NextULong()) differs = true;
            }
            Assert.IsTrue(differs);
        }

        [Test]
        public void DeterministicRandom_NextIntStaysInRange()
        {
            var r = new DeterministicRandom(7);
            for (var i = 0; i < 10000; i++)
            {
                var v = r.NextInt(-3, 5);
                Assert.IsTrue(v >= -3 && v < 5);
            }
        }

        [Test]
        public void Money_IsExactAndFormats()
        {
            var m = Money.FromDollars(19.99) + Money.FromDollars(0.01);
            Assert.AreEqual(2000L, m.Cents);
            Assert.AreEqual("$1,234.50", new Money(123450).ToString());
            Assert.AreEqual("-$5.00", new Money(-500).ToString());
            Assert.AreEqual(1050L, new Money(1000).Scale(1.05).Cents);
        }

        [Test]
        public void GameDateTime_CalendarAndSeasons()
        {
            var t = GameDateTime.FromCalendar(2030, 8, 17, 14, 30); // a Saturday
            Assert.AreEqual(2030, t.Year);
            Assert.AreEqual(8, t.Month);
            Assert.AreEqual(14, t.Hour);
            Assert.AreEqual(30, t.Minute);
            Assert.IsTrue(t.IsWeekend);
            Assert.AreEqual(Season.Summer, t.Season);
            Assert.IsTrue(t.IsHurricaneSeason);
            Assert.AreEqual(t.DayIndex + 1, t.AddHours(10).DayIndex);
            Assert.AreEqual(DayPart.Afternoon, t.DayPart);
        }

        [Test]
        public void WorldClock_ScalesRealTime()
        {
            var clock = new WorldClock(GameDateTime.FromCalendar(2030, 1, 1), WorldClock.TimeScaleForDayLength(48));
            Assert.AreEqual(30.0, clock.TimeScale, 1e-9);
            for (var i = 0; i < 60; i++) clock.AdvanceReal(1.0 / 60.0); // one real second at 60 fps
            Assert.AreEqual(30L, clock.Now.TotalSeconds - GameDateTime.FromCalendar(2030, 1, 1).TotalSeconds);
            clock.Paused = true;
            Assert.AreEqual(0L, clock.AdvanceReal(10));
        }

        [Test]
        public void EventBus_DeliversAndUnsubscribes()
        {
            var bus = new EventBus();
            var count = 0;
            var sub = bus.Subscribe<int>(v => count += v);
            bus.Publish(2);
            bus.Enqueue(3);
            Assert.AreEqual(2, count);
            bus.Flush();
            Assert.AreEqual(5, count);
            sub.Dispose();
            bus.Publish(10);
            Assert.AreEqual(5, count);
        }

        [Test]
        public void ServerConfig_ClampsValuesThatWouldBreakIntegrity()
        {
            var config = new ServerConfig();
            config.Powers.MultiplePowerMultiplier = 1000f;
            config.Economy.PropertyPriceMultiplier = 0f;
            config.Economy.IncomeTaxRate = float.NaN;
            config.Identity.PrimaryColorHex = "blue";
            config.Gameplay.MaxPlayers = 100000;
            var report = ServerConfigValidator.ValidateAndClamp(config);
            Assert.IsFalse(report.HasErrors);
            Assert.AreEqual(ServerConfigValidator.MaxMultiplePowerMultiplier, config.Powers.MultiplePowerMultiplier);
            Assert.AreEqual(0.25f, config.Economy.PropertyPriceMultiplier);
            Assert.AreEqual(0f, config.Economy.IncomeTaxRate);
            Assert.AreEqual("#1B4F72", config.Identity.PrimaryColorHex);
            Assert.AreEqual(ServerConfigValidator.AbsoluteMaxPlayers, config.Gameplay.MaxPlayers);
        }

        [Test]
        public void ServerConfig_RequiresName()
        {
            var config = new ServerConfig();
            config.Identity.ServerName = " ";
            Assert.IsTrue(ServerConfigValidator.ValidateAndClamp(config).HasErrors);
        }
    }
}
