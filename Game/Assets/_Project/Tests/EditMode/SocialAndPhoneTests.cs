using System.Collections.Generic;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Phone;
    using HeroGame.Core.Population;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Social;
    using HeroGame.Core.World;

    public class SocialAndPhoneTests
    {
        private World _world;
        private ServerCharacter _player;
        private AccountProfile _account;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("social", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _account = new AccountProfile
            {
                AccountId = EntityId.Create(EntityKind.UserAccount, 1),
                Character = new CharacterIdentity { FirstName = "Riley", LastName = "Tester", Age = 24 },
            };
            _player = _world.CreateCharacter(_account, new WorldPosition());
        }

        private NpcRecord Adult()
        {
            foreach (var n in _world.Population.Ordered)
                if (n.Alive && n.AgeYears(_world.Today) >= 25 && n.Employment == EmploymentStatus.Employed) return n;
            Assert.Fail("no adult");
            return null;
        }

        [Test]
        public void Barks_PreferSpecificLinesAndAvoidRepetition()
        {
            var selector = new BarkSelector(TestContent.Load().Barks);
            var ctx = new BarkContext { Topic = "greet", Familiarity = Familiarity.Friend, Flags = MemoryFlags.Customer, PlaceKind = "Shop", Affinity = 0.5f };
            ctx.Tokens["player_first"] = "Riley";
            Assert.IsTrue(selector.TrySelect(ctx, new DeterministicRandom(1), null, out var first));
            Assert.AreEqual("greet_customer_regular", first.Line.Id, "the most specific matching line wins");

            var recent = new List<string>();
            var seen = new HashSet<string>();
            var generic = new BarkContext { Topic = "idle" };
            for (var i = 0; i < 4; i++) // four idle lines apply at night in clear weather
            {
                Assert.IsTrue(selector.TrySelect(generic, new DeterministicRandom((ulong)i), recent, out var r));
                Assert.IsFalse(recent.Contains(r.Line.Id), "recently used lines are skipped while alternatives exist");
                recent.Add(r.Line.Id);
                seen.Add(r.Line.Id);
            }
            Assert.AreEqual(4, seen.Count);
            Assert.IsTrue(selector.TrySelect(generic, new DeterministicRandom(9), recent, out _), "never silent: repeats once everything was used");
        }

        [Test]
        public void Barks_FillTokensAndExposeUnknownOnes()
        {
            var tokens = new Dictionary<string, string> { { "player_first", "Riley" } };
            Assert.AreEqual("Hi Riley, {missing}!", BarkSelector.Fill("Hi {player_first}, {missing}!", tokens));
        }

        [Test]
        public void RepeatedFriendlyContact_BuildsARelationship()
        {
            var npc = Adult();
            var outcome = _world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.Greet);
            Assert.IsFalse(string.IsNullOrEmpty(outcome.Line));
            Assert.IsFalse(_world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.AskForNumber).NumberShared,
                "strangers do not hand out their number");

            for (var day = 0; day < 20; day++)
            {
                _world.Clock.AdvanceGame(GameDateTimeSeconds.Day);
                _world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.Purchase);
                _world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.Favor);
            }
            var memory = npc.MemoryOf(_player.CharacterId, false, 0);
            Assert.GreaterOrEqual(BarkSelector.FamiliarityOf(memory), Familiarity.Friend);
            Assert.IsTrue((memory.Flags & MemoryFlags.Customer) != 0);
            var ask = _world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.AskForNumber);
            Assert.IsTrue(ask.NumberShared);
            Assert.AreEqual(1, _player.PhoneContacts.Count);
            Assert.Greater(_player.Reputation.GetNeighborhood(_world.Geography.GetPlace(npc.Home).District), 0f);
        }

        [Test]
        public void Threats_AndWitnessedCrimes_AreRemembered()
        {
            var npc = Adult();
            _world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.Threaten);
            _world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.Threaten);
            var memory = npc.MemoryOf(_player.CharacterId, false, 0);
            Assert.GreaterOrEqual(memory.Fear, 0.5f);
            var familiarity = BarkSelector.FamiliarityOf(memory);
            Assert.IsTrue(familiarity == Familiarity.Wary || familiarity == Familiarity.Hostile, familiarity.ToString());

            var witness = _world.Population.Ordered[_world.Population.Ordered.Count - 5];
            _world.Conversations.RecordWitnessedCrime(witness, _player.CharacterId, 4, victim: false);
            var ctx = _world.Conversations.BuildContext(witness, _player, _account.Character, witness.MemoryOf(_player.CharacterId, false, 0), "greet");
            var selector = new BarkSelector(TestContent.Load().Barks);
            Assert.IsTrue(selector.TrySelect(ctx, new DeterministicRandom(3), null, out var line));
            Assert.AreEqual("greet_crime_witness", line.Line.Id);
        }

        [Test]
        public void Gossip_UsesRealServerHistory()
        {
            var npc = Adult();
            _world.History.Record(_world.Today, HistoryCategory.Crime, 3, "Someone robbed the Harbor Row pharmacy", "", _world.Geography.GetPlace(npc.Home).District);
            for (var i = 0; i < 25; i++)
            {
                _world.Clock.AdvanceGame(60);
                var outcome = _world.Conversations.Interact(npc, _player, _account.Character, InteractionKind.AskAboutRumors);
                if (!outcome.Accepted) continue;
                StringAssert.Contains("Harbor Row pharmacy", outcome.Line);
                return;
            }
            Assert.Fail("NPC never shared gossip");
        }

        [Test]
        public void Phone_RecordsStatementAndBankAlerts()
        {
            Assert.Greater(_player.Statement.Count, 0, "starting funds appear on the statement");
            var before = _player.Inbox.Count;
            _world.AdminGrant(_player.CheckingAccount, Money.FromDollars(5000L), "test", "bonus");
            Assert.AreEqual(before + 1, _player.Inbox.Count);
            var last = _player.Inbox[_player.Inbox.Count - 1];
            Assert.AreEqual(MessageCategory.Bank, last.Category);
            StringAssert.Contains("$5,000.00", last.Body);
            Assert.AreEqual(_world.Ledger.BalanceOf(_player.CheckingAccount).Cents, _player.Statement[_player.Statement.Count - 1].BalanceAfterCents);
            Assert.AreEqual(_player.Inbox.Count, _world.Phone.UnreadCount(_player));
            _world.Phone.MarkRead(_player, last.Id);
            Assert.AreEqual(_player.Inbox.Count - 1, _world.Phone.UnreadCount(_player));
        }

        [Test]
        public void Phone_ReceivesBreakingNewsAndContactInvitations()
        {
            _world.History.Record(_world.Today, HistoryCategory.Disaster, 5, "Chemical fire at Calloway Works");
            Assert.IsTrue(_player.Inbox.Exists(m => m.Category == MessageCategory.News && m.Body.Contains("Calloway")));

            var npc = Adult();
            var memory = npc.MemoryOf(_player.CharacterId, true, _world.Today);
            memory.Affinity = 0.9f;
            memory.Interactions = 20;
            _player.PhoneContacts.Add(new PhoneContact { Npc = npc.Id, Name = npc.FullName });
            var sim = new WorldSimulation(_world);
            sim.AdvanceDays(60);
            Assert.IsTrue(_player.Inbox.Exists(m => m.Category == MessageCategory.Personal && m.From == npc.Id), "a close contact reaches out within two months");
        }

        [Test]
        public void Phone_MapSearchFindsRealPlaces()
        {
            var results = _world.Phone.SearchMap("taqueria");
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("Taqueria La Marea", results[0].Name);
            Assert.Greater(_world.Phone.SearchMap("Hospital").Count, 0);
        }

        private static class GameDateTimeSeconds
        {
            public const long Day = HeroGame.Core.Time.GameDateTime.SecondsPerDay;
        }
    }
}
