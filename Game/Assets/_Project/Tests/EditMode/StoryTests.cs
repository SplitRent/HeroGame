using System;
using System.Collections.Generic;
using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Simulation;
using HeroGame.Core.Story;
using HeroGame.Core.Time;
using HeroGame.Persistence.Content;
using HeroGame.Persistence.Saves;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class StoryTests
    {
        private static StoryDefinition _story;
        private World _world;
        private StoryService _s;

        private static StoryDefinition Definition() => _story ?? (_story = ContentLoader.LoadStory(TestContent.DataDirectory));

        private static GameDateTime StartOf(StoryDefinition d)
        {
            var parts = d.Start.Split(' ', '-', ':');
            return GameDateTime.FromCalendar(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]));
        }

        private AccountProfile _account;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("story", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal(), StartOf(Definition()));
            _account = new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 1) };
            _s = StoryService.Begin(_world, Definition(), _account);
            _s.Simulation = new WorldSimulation(_world);
        }

        // ------------------------------------------------------------------ helpers that play like a player

        private WorldPosition PlayerAt()
        {
            var o = _s.CurrentObjective;
            return o != null && _s.TryObjectivePosition(o, out var p) ? p : _s.Player.LastPosition;
        }

        private void Hours(double h)
        {
            _world.Clock.AdvanceGame((long)(h * 3600));
            _s.Simulation.Update();
        }

        /// <summary>Completes the current objective the way a player would; dialogue picks <paramref name="choose"/> (default the first option).</summary>
        private void Do(Func<string, int> choose = null)
        {
            var o = _s.CurrentObjective;
            Assert.IsNotNull(o, "no active objective");
            switch (o.Kind)
            {
                case ObjectiveKind.GoTo:
                    Assert.IsTrue(_s.TryObjectivePosition(o, out var at), "objective '" + o.Id + "' has a location");
                    _s.Update(at);
                    break;
                case ObjectiveKind.TalkTo:
                    Assert.IsTrue(_s.TalkToCast(o.Target), "can talk to " + o.Target);
                    for (var guard = 0; _s.InDialogue && guard < 30; guard++)
                    {
                        var choices = _s.AvailableChoices();
                        if (choices.Count == 0) _s.Continue();
                        else _s.Choose(choose != null ? Math.Min(choose(_s.State.ActiveDialogue), choices.Count - 1) : 0);
                    }
                    Assert.IsFalse(_s.InDialogue, "dialogue ends");
                    _s.Update(PlayerAt());
                    break;
                case ObjectiveKind.Stay:
                    Assert.IsTrue(_s.TryObjectivePosition(o, out var place));
                    for (var i = 0; i < 12 && _s.CurrentObjective == o; i++)
                    {
                        _s.Update(place);
                        Hours(1);
                        _s.Update(place);
                    }
                    break;
                case ObjectiveKind.Wait:
                case ObjectiveKind.Survive:
                    for (var i = 0; i < 72 && _s.CurrentObjective == o; i++)
                    {
                        Hours(1);
                        _s.Update(PlayerAt());
                    }
                    break;
                case ObjectiveKind.Interact:
                    _s.Interact(o.Target);
                    break;
                case ObjectiveKind.Condition:
                    for (var i = 0; i < 400 && _s.CurrentObjective == o; i++)
                    {
                        if (o.Condition.StartsWith("power:uses", StringComparison.Ordinal)) UsePowerOnce();
                        else _s.Simulation.AdvanceDays(2);
                        _s.Update(PlayerAt());
                    }
                    break;
                case ObjectiveKind.Earn:
                    _world.AdminGrant(_s.Player.CheckingAccount, new Money((long)o.Amount), "test", "savings");
                    _s.Update(PlayerAt());
                    break;
            }
            Assert.AreNotSame(o, _s.CurrentObjective, "objective '" + o.Id + "' completed");
        }

        /// <summary>One deliberate use of the protagonist's ability, somewhere quiet (practice).</summary>
        private void UsePowerOnce()
        {
            var power = _s.Player.Powers.Powers[0];
            power.CooldownUntilSecond = 0;
            _s.Player.Powers.Stamina = 1f;
            _s.Player.Powers.Strain = 0f;
            Hours(0.25);
            var here = _s.Player.LastPosition;
            _world.PowerUse.Use(_s.Player, new PowerUseRequest { PowerIndex = 0, Intensity = 0.6f, Target = TargetKind.Point, Origin = here, Point = new WorldPosition(here.X + 5f, 0f, here.Z) });
        }

        private void PlayPartOne(Func<string, int> choose = null)
        {
            foreach (var m in new[] { "mornings", "eastwater_high", "friday_lights", "first_paycheck", "rafas_favor", "canal_lights", "isadora", "back_on_magnolia" }) FinishMission(m, choose);
        }

        private void FinishMission(string id, Func<string, int> choose = null)
        {
            Assert.AreEqual(id, _s.ActiveMission?.Id, "expected mission " + id);
            for (var guard = 0; guard < 12 && _s.ActiveMission != null && _s.ActiveMission.Id == id; guard++) Do(choose);
            Assert.AreEqual(MissionStatus.Completed, _s.State.Mission(id).Status);
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void StoryContent_PassesValidation()
        {
            var report = ContentLoader.ValidateStory(Definition(), TestContent.Load());
            Assert.IsFalse(report.HasErrors, report.ToString());
            Assert.GreaterOrEqual(Definition().Missions.Count(m => m.Part == 1), 7, "the seven teen missions");
        }

        [Test]
        public void Begin_CastsRealPeople_InARealWorld_In2026()
        {
            Assert.AreEqual(2026, _world.Clock.Now.Year);
            var rosa = _s.Npc("rosa");
            var lupe = _s.Npc("lupe");
            Assert.IsNotNull(rosa);
            Assert.AreEqual("Rosa Navarro", rosa.FullName);
            Assert.IsTrue(rosa.IsImportant);
            Assert.AreEqual(_s.Npc("pilar").Home, rosa.Home, "Rosa and Pilar share the Navarro home");
            Assert.AreEqual(rosa.Home.ToString(), _s.State.Home);
            Assert.AreEqual(_world.Geography.FindPlaceByName("Lupe's Corner Market").Id, lupe.Workplace);
            Assert.AreEqual("Eli", _account.Character.FirstName);
            Assert.AreEqual("Navarro", _account.Character.LastName);
            Assert.AreEqual("mornings", _s.ActiveMission.Id);
            Assert.IsFalse(_s.Allows("property.buy"), "a sixteen-year-old cannot buy property");
            Assert.AreEqual(ObjectiveKind.TalkTo, _s.CurrentObjective.Kind);
        }

        [Test]
        public void Dialogue_ChoicesSetFlagsAndRelationships_AndConditionsFilterChoices()
        {
            Do(_ => 0); // promise Rosa to look for work
            Assert.AreEqual(1, _s.State.Flag("promised_work"));
            Assert.Greater(_s.State.Relationship("rosa"), 0.1f);
            Assert.Greater(_s.Npc("rosa").MemoryOf(_s.Player.CharacterId, false, 0).Affinity, 0f, "the NPC's own memory follows the story relationship");

            // d_lupe_after only offers the lines that match what actually happened.
            _s.State.Flags["refused_rafa"] = 1;
            _s.StartDialogue("d_lupe_after");
            var choices = _s.AvailableChoices();
            Assert.AreEqual(1, choices.Count);
            StringAssert.Contains("stayed out", choices[0].Text);
        }

        [Test]
        public void RafasFavor_RefusingSkipsTheDeliveryBranch()
        {
            foreach (var m in new[] { "mornings", "eastwater_high", "friday_lights", "first_paycheck" }) FinishMission(m);
            Assert.AreEqual("rafas_favor", _s.ActiveMission.Id);
            Do(d => d == "d_rafa_favor" ? 1 : 0); // refuse
            Assert.AreEqual(1, _s.State.Flag("refused_rafa"));
            Assert.AreEqual("lupe_after", _s.CurrentObjective.Id, "no delivery, no police stop");
            Do();
            Assert.AreEqual(MissionStatus.Completed, _s.State.Mission("rafas_favor").Status, "Rosa never finds out because there is nothing to find");
            Assert.AreEqual(0, _s.Npc("rafa").Arrests);
        }

        [Test]
        public void PartOne_PlaysThrough_Isadora_AndTheFourYearJump()
        {
            var playerCash = _world.Ledger.BalanceOf(_s.Player.CheckingAccount);
            foreach (var m in new[] { "mornings", "eastwater_high", "friday_lights" }) FinishMission(m);
            var lupe = _world.Businesses.Values.First(b => b.Name == "Lupe's Corner Market");
            var wages = new List<WorldTransaction>();
            _world.Transactions.Committed += tx =>
            {
                if (tx.Money != null && tx.Money.Reason == TransactionReason.Payroll) wages.Add(tx);
            };
            FinishMission("first_paycheck");
            Assert.IsTrue(wages.Any(tx => tx.Money.Postings.Any(p => p.Account == lupe.Account && p.AmountCents == -6000)
                                          && tx.Money.Postings.Any(p => p.Account == _s.Player.CheckingAccount && p.AmountCents == 6000)),
                "Lupe paid $60 out of her own till");

            // Take the delivery and tell the detective the truth: Rafa is arrested, Rosa finds out.
            FinishMission("rafas_favor", d => 0);
            Assert.AreEqual(1, _s.State.Flag("rafa_arrested"));
            Assert.AreEqual(1, _s.Npc("rafa").Arrests);
            Assert.Less(_s.State.Relationship("rafa"), 0f);

            FinishMission("canal_lights");
            Assert.AreEqual("isadora", _s.ActiveMission.Id);
            Assert.IsNotNull(_world.Weather.State.ActiveSystem);
            Assert.AreEqual("Isadora", _world.Weather.State.ActiveSystem.Name);

            var eastwater = _world.Geography.Districts.First(d => d.Key == "eastwater");
            var year = _world.Clock.Now.Year;
            FinishMission("isadora");

            Assert.AreEqual(year + 4, _world.Clock.Now.Year, "four years pass");
            Assert.AreEqual(2, _s.State.Part);
            Assert.IsTrue(_s.Allows("property.buy"));
            Assert.AreEqual("back_on_magnolia", _s.ActiveMission.Id);
            var power = _s.Player.Powers.Powers.Single();
            Assert.AreEqual(PowerStage.Latent, power.Stage, "the ability waits");
            Assert.AreEqual(_world.Geography.FindPlaceByName("The Arden Ledger Newsroom").Id, _s.Npc("mai").Workplace, "Mai works at the Ledger");
            Assert.AreEqual("charge_nurse", _s.Npc("rosa").OccupationId);
            Assert.IsTrue(_s.Npc("rafa").History.Any(h => h.Summary.Contains("parole")));
            Assert.IsTrue(_s.Npc("lupe").Alive, "important characters don't die off-screen");
            Assert.Greater(_s.Npc("pilar").AgeYears(_world.Today), 12);
            Assert.IsTrue(_world.History.Recent.Concat(_world.History.Major).Any(h => h.Headline.Contains("Meridian")));
            var meridianHomes = _world.Properties.All.Count(p => p.District == eastwater.Id && _world.Ledger.Accounts.Any(a => a.Label == "Meridian Holdings" && a.Owner == _world.Ownership.OwnerOf(p.Id)));
            Assert.Greater(meridianHomes, 0, "Meridian bought flood-damaged lots");
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));

            // Act II begins on its own: the latent ability manifests about two weeks after coming home.
            for (var i = 0; i < 3 && power.Stage == PowerStage.Latent; i++) _s.Simulation.AdvanceDays(10);
            Assert.AreEqual(PowerStage.Manifesting, power.Stage);

            FinishMission("back_on_magnolia");
            Assert.Greater(_world.Ledger.BalanceOf(_s.Player.CheckingAccount).Cents, playerCash.Cents);
        }

        [Test]
        public void PartTwo_StandingUpToCalloway_EndsWithEastwaterSavedOrSplit_AndTheSandboxContinues()
        {
            var picks = new Dictionary<string, int>
            {
                ["d_lupe_fire"] = 0, ["d_rafa_syndicate"] = 0, ["d_calloway_confront"] = 1, ["d_hollins_ordinance"] = 1, ["d_hollins_campaign"] = 0,
                ["d_abernathy_confront"] = 0, ["d_landfall"] = 0,
            };
            Func<string, int> choose = dlg => picks.TryGetValue(dlg, out var i) ? i : 0;
            PlayPartOne(choose);
            var eastwater = _world.Geography.Districts.First(d => d.Key == "eastwater");
            int OwnedBy(string label) => _world.Properties.All.Count(p => p.District == eastwater.Id && _world.Ledger.Accounts.Any(a => a.Label == label && a.Owner == _world.Ownership.OwnerOf(p.Id)));
            var meridianBefore = OwnedBy("Meridian Holdings");
            Assert.Greater(meridianBefore, 0);

            // Act II: the ability wakes, Pilar notices, Dee measures, Lupe's burns.
            FinishMission("static", choose);
            Assert.GreaterOrEqual((int)_s.Player.Powers.Powers[0].Stage, (int)PowerStage.Manifesting);
            var calls = _world.Emergency.Stats.Calls;
            FinishMission("tests", choose); // finishing it sets Lupe's on fire as the next mission starts
            Assert.GreaterOrEqual(_s.Player.Powers.Powers.Sum(p => p.Progress.SuccessfulUses), 3);
            var lupes = _world.Geography.FindPlaceByName("Lupe's Corner Market");
            Assert.IsTrue(_world.Emergency.Incidents.Any(i => i.Kind == Core.Emergency.EmergencyKind.Fire && i.Property == lupes.Property), "the fire at Lupe's is real: it was called in");
            Assert.Greater(_world.Emergency.Stats.Calls, calls);
            FinishMission("witness", choose);
            Assert.AreEqual(1, _s.State.Flag("public_power"));
            Assert.Greater(_s.Player.Reputation.Get(ReputationDimension.Notoriety), 0f);

            // Act III: the money, the shells, the canal logs.
            FinishMission("follow_the_money", choose);
            FinishMission("shells", choose);
            Assert.AreEqual(1, _s.State.Flag("rafa_testifies"));
            FinishMission("calloways_canal", choose);
            Assert.AreEqual(1, _s.State.Flag("expose_published"));
            Assert.IsTrue(_world.History.Recent.Concat(_world.History.Major).Any(h => h.Headline.StartsWith("Ledger exposé")));

            // Act IV: the registry vote, the campaign, the detective.
            FinishMission("the_ordinance", choose);
            Assert.IsTrue(_world.Civic.Proposals.Any(p => p.OrdinanceId == "anomaly_registration" && p.Decided), "the council really voted");
            FinishMission("campaign_trail", choose);
            var race = _world.Civic.Elections.Single(e => e.Office == Core.Civic.Office.Mayor);
            Assert.IsTrue(race.Candidates.Any(c => c.Person == _s.Npc("hollins").Id), "Hollins is on the ballot");
            FinishMission("abernathy", choose);
            Assert.AreEqual(1, _s.State.Flag("abernathy_ally"));

            // Finale.
            FinishMission("landfall", choose);
            Assert.AreEqual("Nadia", _world.Weather.State.ActiveSystem?.Name ?? "Nadia");
            FinishMission("who_he_is", choose);
            Assert.IsTrue(race.Held, "the city voted");
            Assert.That(_s.State.Ending, Is.EqualTo("saved").Or.EqualTo("split"));
            if (_s.State.Ending == "saved")
            {
                Assert.AreEqual(1, _s.State.Flag("mayor_hollins"));
                Assert.AreEqual(0, OwnedBy("Meridian Holdings"), "the land trust bought every Meridian lot back");
                Assert.IsFalse(_world.Civic.IsActive("anomaly_registration"));
            }
            else Assert.Less(OwnedBy("Meridian Holdings"), meridianBefore, "half the street came back");
            Assert.Greater(OwnedBy("Eastwater Community Land Trust"), 0);

            // After the credits the sandbox goes on, and the world remembers.
            Assert.IsNull(_s.ActiveMission);
            foreach (var action in new[] { "property.buy", "business.start", "finance.loan" }) Assert.IsTrue(_s.Allows(action));
            _s.Simulation.AdvanceDays(3);
            Assert.IsTrue(_world.History.Major.Any(h => h.Importance == 5 && (h.Headline.Contains("land trust") || h.Headline.Contains("divided") || h.Headline.Contains("Land Trust"))));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void PartTwo_TakingCallowaysDeal_PaysTheFamily_AndMeridianKeepsBuilding()
        {
            var picks = new Dictionary<string, int> { ["d_lupe_fire"] = 1, ["d_rafa_syndicate"] = 1, ["d_calloway_confront"] = 0, ["d_hollins_ordinance"] = 2 };
            Func<string, int> choose = dlg => picks.TryGetValue(dlg, out var i) ? i : 0;
            PlayPartOne(choose);
            var eastwater = _world.Geography.Districts.First(d => d.Key == "eastwater");
            int Meridian() => _world.Properties.All.Count(p => p.District == eastwater.Id && _world.Ledger.Accounts.Any(a => a.Label == "Meridian Holdings" && a.Owner == _world.Ownership.OwnerOf(p.Id)));
            foreach (var m in new[] { "static", "tests", "witness", "follow_the_money", "shells" }) FinishMission(m, choose);
            Assert.AreEqual(1, _s.State.Flag("hid_power"), "Lupe was pulled out the hard way");
            var cash = _world.Ledger.BalanceOf(_s.Player.CheckingAccount);
            FinishMission("calloways_canal", choose);
            Assert.AreEqual(1, _s.State.Flag("deal_with_calloway"));
            Assert.GreaterOrEqual((_world.Ledger.BalanceOf(_s.Player.CheckingAccount) - cash).Cents, 500000, "the settlement, paid");
            Assert.AreEqual(0, _s.State.Flag("expose_published"));
            var before = Meridian();
            foreach (var m in new[] { "the_ordinance", "campaign_trail", "abernathy", "landfall", "who_he_is" }) FinishMission(m, choose);
            Assert.AreEqual("sold", _s.State.Ending);
            Assert.Greater(Meridian(), before, "Meridian keeps building");
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void StoryState_SurvivesSaveAndLoad_MidMission()
        {
            FinishMission("mornings");
            Do(); // first period
            var dir = TestContent.TempDirectory("story-save");
            var saves = new WorldSaveSystem(dir);
            saves.Save(_world, full: true);
            using (var journal = saves.OpenJournal())
            {
                var loaded = saves.Load(TestContent.Load(), journal).World;
                Assert.IsNotNull(loaded.Story);
                var player = loaded.Characters[_s.Player.CharacterId];
                var resumed = StoryService.Attach(loaded, Definition(), loaded.Story, player);
                Assert.AreEqual("eastwater_high", resumed.ActiveMission.Id);
                Assert.AreEqual("lunch_dee", resumed.CurrentObjective.Id);
                Assert.AreEqual(_s.State.Flag("promised_work"), resumed.State.Flag("promised_work"));
                Assert.IsNotNull(resumed.Npc("dee"));
                Assert.IsTrue(resumed.TalkToCast("dee"));
            }
        }

        [Test]
        public void Validator_CatchesWriterMistakes()
        {
            var broken = ContentLoader.LoadStory(TestContent.DataDirectory);
            broken.Missions[0].Objectives[0].Dialogue = "d_missing";
            broken.Missions[1].Objectives.Add(new ObjectiveDefinition { Id = "x", Kind = ObjectiveKind.GoTo, Target = "Nowhere Plaza" });
            broken.Dialogues[0].Nodes[0].Effects.Add("rel:nobody+1");
            broken.Dialogues[0].Nodes[0].Effects.Add("teleport:mars");
            broken.Missions[2].OnStart.Add("storm:Bad:three:8");
            var text = ContentLoader.ValidateStory(broken, TestContent.Load()).ToString();
            StringAssert.Contains("d_missing", text);
            StringAssert.Contains("Nowhere Plaza", text);
            StringAssert.Contains("rel:nobody", text);
            StringAssert.Contains("teleport:mars", text);
            StringAssert.Contains("storm:Bad", text);
        }
    }
}
