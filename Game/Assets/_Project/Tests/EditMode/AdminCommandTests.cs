using System.Collections.Generic;
using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Simulation;
using HeroGame.Core.World;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class AdminCommandTests
    {
        private World _world;
        private AdminCommands _admin;
        private ServerCharacter _me;
        private readonly WorldPosition _at = new WorldPosition(-200f, 0f, 40f);

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("admin", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _admin = new AdminCommands(_world, new WorldSimulation(_world));
            _me = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 90), Character = new CharacterIdentity { FirstName = "Dev" } }, _at);
        }

        private string Run(string line) => _admin.Execute(line, _me, _at, "tester");

        [Test]
        public void EveryListedCommand_HasHelp_AndUnknownCommandsAreRefused()
        {
            var help = Run("help");
            foreach (var name in new[] { "spawnnpc", "spawncar", "own", "disaster", "fire", "wanted", "police", "election", "ordinance", "business", "props", "storm", "money", "time", "weather", "npc", "status" })
                StringAssert.Contains(name, help);
            StringAssert.StartsWith("Unknown command", Run("rm -rf"));
            StringAssert.Contains("Ledger balanced", Run("status"));
        }

        [Test]
        public void MoneyAndOwnership_GoThroughAuditedTransactions()
        {
            var before = _world.Ledger.BalanceOf(_me.CheckingAccount);
            var journal = _world.Transactions.LastSequence;
            StringAssert.StartsWith("Balance", Run("money 5000"));
            Assert.AreEqual(before + Money.FromDollars(5000), _world.Ledger.BalanceOf(_me.CheckingAccount));
            Assert.Greater(_world.Transactions.LastSequence, journal, "journaled like any admin grant");
            StringAssert.Contains("between", Run("money 99999999999"));

            StringAssert.StartsWith("You now own", Run("own nearest"));
            Assert.AreEqual(1, _world.Ownership.AssetsOf(_me.CharacterId).Count(a => a.Kind == EntityKind.Property));
            StringAssert.StartsWith("Spawned", Run("spawncar " + _world.Vehicles.Models.First(m => m.Civilian).Id));
            Assert.AreEqual(1, _world.Ownership.AssetsOf(_me.CharacterId).Count(a => a.Kind == EntityKind.Vehicle));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void SpawnNpc_AddsRealResidents()
        {
            var count = _world.Population.Count;
            StringAssert.StartsWith("Moved in", Run("spawnnpc 3"));
            Assert.AreEqual(count + 3, _world.Population.Count);
            var newest = _world.Population.Ordered.Last();
            Assert.IsTrue(newest.Home.IsValid);
            StringAssert.Contains(newest.FullName, Run("npc " + newest.FullName));
        }

        [Test]
        public void Events_Police_Elections_Businesses_Props()
        {
            StringAssert.Contains("started", Run("disaster Blackout"));
            Assert.AreEqual(1, _world.Disasters.Active.Count);
            StringAssert.StartsWith("Fire at", Run("fire"));
            StringAssert.StartsWith("Wanted level", Run("wanted 6"));
            Assert.IsNotNull(_world.Wanted.Get(_me.CharacterId));
            Run("clearwanted");
            Assert.IsNull(_world.Wanted.Get(_me.CharacterId));
            StringAssert.StartsWith("Call", Run("police"));
            StringAssert.Contains("landfall", Run("storm Delia 3 12"));
            Assert.AreEqual("Delia", _world.Weather.State.ActiveSystem.Name);

            StringAssert.Contains("Mayor:", Run("election run"));
            Assert.IsNotEmpty(_world.Civic.PastElections);
            Run("election mayor");
            Assert.IsTrue(_world.Government.Holds(_me.CharacterId, Core.Civic.Office.Mayor));
            StringAssert.Contains("is in force", Run("ordinance sales_tax_increase enact"));
            StringAssert.Contains("is not in force", Run("ordinance sales_tax_increase repeal"));

            StringAssert.Contains("staff", Run("business nearest"));
            StringAssert.Contains("destroyed", Run("props break"));
            StringAssert.Contains("Destroyed", Run("props 60"));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }
    }
}
