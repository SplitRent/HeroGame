using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Combat;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using NUnit.Framework;

namespace HeroGame.Tests
{
    /// <summary>Street encounters: the city pushes back at players who are out at night.</summary>
    public class StreetCrimeTests
    {
        private World _world;
        private ServerCharacter _me;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("street", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _me = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 21), Character = new CharacterIdentity { FirstName = "Ari" } }, new WorldPosition(-200f, 0f, 40f));
            Assert.IsTrue(_world.AdminGrant(_me.CheckingAccount, Money.FromDollars(2000), "admin", "test").Success);
        }

        private StreetEncounter Encounter() => _world.StreetCrime.Start(_me, _me.LastPosition);

        [Test]
        public void OnlyPlayersWhoAreOut_AndMostlyAtNight_InRougherDistricts()
        {
            var night = GameDateTime.FromCalendar(2030, 5, 7, 23);
            var noon = GameDateTime.FromCalendar(2030, 5, 7, 12);
            Assert.Greater(_world.StreetCrime.Chance(_me, night), _world.StreetCrime.Chance(_me, noon) * 3, "night is riskier");

            var sim = new WorldSimulation(_world);
            sim.AdvanceDays(20);
            Assert.IsNull(_world.StreetCrime.ActiveFor(_me.CharacterId), "nobody is mugged while not present");

            var started = 0;
            _world.StreetCrime.Started += _ => started++;
            _world.StreetCrime.IsPresent = c => c == _me;
            for (var d = 0; d < 60 && started == 0; d++)
            {
                sim.AdvanceDays(1);
                var e = _world.StreetCrime.ActiveFor(_me.CharacterId);
                if (e != null) _world.StreetCrime.Update(_me, new WorldPosition(e.Position.X + 100f, 0f, e.Position.Z)); // walk away
            }
            Assert.Greater(started, 0, "out every night in Eastwater, sooner or later someone tries it");
            // Servers can switch it off: a month of nights out, nothing happens.
            _world.Config.Gameplay.StreetCrimeAgainstPlayers = false;
            var before = started;
            sim.AdvanceDays(30);
            Assert.AreEqual(before, started, "switched off");
        }

        [Test]
        public void HandingItOver_MovesMoneyThroughTheLedger_AndReportsARealSuspect()
        {
            var e = Encounter();
            Assert.IsNotNull(e);
            var mugger = _world.Population.Get(e.Mugger);
            Assert.IsNotNull(mugger);
            Assert.IsTrue(_me.Inbox.Any(m => m.Body.Contains("Now.")));
            var cash = _world.Ledger.BalanceOf(_me.CheckingAccount);
            Assert.IsTrue(_world.StreetCrime.Comply(_me, "comply-1").Success);
            Assert.AreEqual(cash.Cents - e.DemandCents, _world.Ledger.BalanceOf(_me.CheckingAccount).Cents);
            Assert.AreEqual(EncounterOutcome.Complied, e.Outcome);
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.CrimeTypeId == "street_robbery" && i.Perpetrator == mugger.Id && i.Victim == _me.CharacterId && i.ReportedToPolice));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
            Assert.IsFalse(_world.StreetCrime.Comply(_me, "comply-2").Success, "it's over");
        }

        [Test]
        public void RefusingStartsAFight_HittingBackIsSelfDefence_AndKnockingThemDownEndsIt()
        {
            var e = Encounter();
            var mugger = _world.Population.Get(e.Mugger);
            Assert.IsTrue(_world.StreetCrime.Refuse(_me).Success);
            var crimes = _world.Justice.Incidents.Count(i => i.Perpetrator == _me.CharacterId);
            AttackOutcome hit = null;
            for (var i = 0; i < 30 && _world.StreetCrime.ActiveFor(_me.CharacterId) != null; i++)
            {
                _world.Clock.AdvanceGame(2);
                hit = _world.Combat.Attack(_me, new AttackRequest { WeaponId = "fists", TargetKind = AttackTargetKind.Npc, Target = mugger.Id, Origin = e.Position, TargetPosition = e.Position });
                if (hit.Attempted) Assert.IsTrue(hit.Justified, "defending yourself: " + hit.Message);
                _world.StreetCrime.Update(_me, e.Position);
            }
            Assert.AreEqual(EncounterOutcome.FoughtOff, e.Outcome);
            Assert.AreEqual(crimes, _world.Justice.Incidents.Count(i => i.Perpetrator == _me.CharacterId), "self-defence is not a crime");
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.CrimeTypeId == "street_robbery" && i.Perpetrator == mugger.Id), "the attempt is still reported");
        }

        [Test]
        public void RunningAway_Escapes_AndIgnoringThemGetsYouHurt()
        {
            var e = Encounter();
            _world.StreetCrime.Update(_me, new WorldPosition(e.Position.X + StreetCrimeService.EscapeRadius + 5f, 0f, e.Position.Z));
            Assert.AreEqual(EncounterOutcome.Escaped, e.Outcome);

            _world.Clock.AdvanceGame(StreetCrimeService.CooldownSeconds + 60);
            var again = Encounter();
            var health = _me.Health;
            for (var s = 0; s < 120 && again.Open; s += 5)
            {
                _world.Clock.AdvanceGame(5);
                _world.StreetCrime.Update(_me, again.Position); // standing there, doing nothing
            }
            Assert.Less(_me.Health, health, "past the deadline they attack");
        }

        [Test]
        public void PoliceCanArrestTheMugger()
        {
            var e = Encounter();
            var mugger = _world.Population.Get(e.Mugger);
            Assert.IsTrue(_world.StreetCrime.Comply(_me, "c").Success);
            // Report enough cases that the police find some suspects (30% are located on scene).
            var arrested = mugger.Arrests > 0;
            for (var tries = 0; tries < 40 && !arrested; tries++)
            {
                _world.Clock.AdvanceGame(StreetCrimeService.CooldownSeconds + 60);
                var next = _world.StreetCrime.Start(_me, _me.LastPosition);
                if (next == null) continue;
                _world.StreetCrime.Comply(_me, "c" + tries);
                new WorldSimulation(_world).AdvanceDays(1);
                arrested = _world.Population.Ordered.Any(n => n.Arrests > 0 && n.OverrideActivity == ActivityKind.InCustody && n.OverrideUntilDay > _world.Today);
            }
            Assert.IsTrue(arrested, "the police locate and arrest some muggers");
            Assert.IsTrue(_world.Population.Ordered.Any(n => n.HasCriminalRecord && n.History.Any(h => h.Kind == "arrested")));
        }
    }
}
