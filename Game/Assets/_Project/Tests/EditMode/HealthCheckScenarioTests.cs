using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Simulation;
    using HeroGame.Persistence.Saves;

    /// <summary>The editor Health Check's save-and-load step, run headlessly with the same inputs.</summary>
    public class HealthCheckScenarioTests
    {
        [Test]
        public void HealthCheckSaveAndLoad_RoundTripsExactly()
        {
            var content = TestContent.Load();
            var saves = new WorldSaveSystem(TestContent.TempDirectory("healthcheck"));
            long money;
            int npcs;
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create("healthcheck", TestContent.DefaultConfig(), content, journal);
                new WorldSimulation(world).AdvanceDays(3);
                // Counted after simulating: with this seed a resident arrives within three days (311 -> 312), which the
                // Health Check once mistook for a save failure by counting before the simulation.
                npcs = world.Population.Count;
                saves.Save(world);
                money = world.Ledger.BalanceOf(world.Accounts.Treasury).Cents;
            }
            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(content, journal);
                Assert.IsFalse(load.Report.HasErrors, load.Report.ToString());
                Assert.AreEqual(npcs, load.World.Population.Count, "residents");
                Assert.AreEqual(money, load.World.Ledger.BalanceOf(load.World.Accounts.Treasury).Cents, "treasury");
                Assert.IsTrue(load.World.Ledger.VerifyInvariant(out var imbalance), "books off by " + imbalance);
            }
        }
    }
}
