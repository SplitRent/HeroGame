using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Crime;
using HeroGame.Core.Economy;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.World;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class PowerExecutionTests
    {
        private World _world;
        private ServerCharacter _hero;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("powers-exec", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _hero = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 11), Character = new CharacterIdentity { FirstName = "Nova" } }, new WorldPosition());
        }

        private static PowerInstance Power(PowerDomain domain, EffectVerb verb, DeliveryMode delivery, PowerElement element, float magnitude = 0.8f, float range = 40f,
            PowerStage stage = PowerStage.Mastered, params PowerLimitation[] limits)
        {
            var p = new PowerInstance { Stage = stage, Progress = new PowerProgress { Experience = 700f, PrecisionBonus = 0.4f } };
            p.Definition.Components.Add(new PowerComponent { Domain = domain, Verb = verb, Delivery = delivery, Element = element, Magnitude = magnitude, Range = range, Precision = 0.9f, Efficiency = 0.7f });
            p.Definition.Limitations.AddRange(limits);
            return p;
        }

        private int Give(PowerInstance p)
        {
            _hero.Powers.Powers.Add(p);
            return _hero.Powers.Powers.Count - 1;
        }

        private void Later(int seconds = 600) => _world.Clock.AdvanceGame(seconds);

        private void Refresh()
        {
            _hero.Powers.Stamina = 1f;
            _hero.Powers.Strain = 0f;
            foreach (var p in _hero.Powers.Powers) p.CooldownUntilSecond = 0;
        }

        private PowerOutcome UseUntil(PowerUseRequest request, System.Func<PowerOutcome, bool> done, int tries = 30)
        {
            PowerOutcome last = null;
            for (var i = 0; i < tries; i++)
            {
                Refresh();
                Later(97);
                last = _world.PowerUse.Use(_hero, request);
                if (done(last)) return last;
            }
            return last;
        }

        private (NpcRecord npc, WorldPosition pos) SomeoneOutdoors()
        {
            for (var h = 0; h < 48; h++)
            {
                _world.Director.Index.Update(_world.Clock.Now);
                foreach (var npc in _world.Population.Ordered)
                {
                    if (!npc.Alive || !_world.Director.Index.TryGet(npc.Id, _world.Clock.Now, out var activity, out var pos)) continue;
                    var place = _world.Geography.GetPlace(activity.Place);
                    if (activity.Activity == ActivityKind.Commuting || place != null && (place.Kind == PlaceKind.Park || place.Kind == PlaceKind.Beach)) return (npc, pos);
                }
                _world.Clock.AdvanceGame(3600);
            }
            Assert.Fail("Nobody outdoors in two days.");
            return default;
        }

        [Test]
        public void EffectPlanner_DerivesEffectsFromComposition()
        {
            Assert.AreEqual(EffectKind.Strike, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Energy, Verb = EffectVerb.Project, Delivery = DeliveryMode.Projectile }));
            Assert.AreEqual(EffectKind.Blast, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Force, Verb = EffectVerb.Project, Delivery = DeliveryMode.Area }));
            Assert.AreEqual(EffectKind.Teleport, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Space, Verb = EffectVerb.Traverse }));
            Assert.AreEqual(EffectKind.Flight, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Gravity, Verb = EffectVerb.Traverse }));
            Assert.AreEqual(EffectKind.Shield, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Defense, Verb = EffectVerb.Shield }));
            Assert.AreEqual(EffectKind.Heal, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Biology, Verb = EffectVerb.Transform }));
            Assert.AreEqual(EffectKind.Telekinesis, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Matter, Verb = EffectVerb.Manipulate }));
            Assert.AreEqual(EffectKind.TimeDilation, PowerEffects.KindOf(new PowerComponent { Domain = PowerDomain.Time, Verb = EffectVerb.Warp }));

            var bolt = Power(PowerDomain.Energy, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Fire);
            var weak = PowerEffects.Plan(bolt, 0.2f, 10f);
            var strong = PowerEffects.Plan(bolt, 1f, 10f);
            Assert.Greater(strong.Damage, weak.Damage);
            Assert.Greater(PowerEffects.Plan(bolt, 1f, 5f).Accuracy, PowerEffects.Plan(bolt, 1f, 55f).Accuracy, "harder to hit far away");
            var slow = Power(PowerDomain.Energy, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Fire, limits: new PowerLimitation { Kind = LimitationKind.Cooldown, Severity = 1f });
            Assert.Greater(PowerEffects.Plan(slow, 1f, 5f).CooldownSeconds, PowerEffects.Plan(bolt, 1f, 5f).CooldownSeconds * 2f);
        }

        [Test]
        public void Use_RespectsStage_Cooldown_Custody_Range_AndServerSwitches()
        {
            var bolt = Give(Power(PowerDomain.Energy, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Electric));
            var latent = Give(Power(PowerDomain.Energy, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Fire, stage: PowerStage.Latent));
            var point = new PowerUseRequest { PowerIndex = bolt, Target = TargetKind.Point, Origin = new WorldPosition(), Point = new WorldPosition(10, 0, 0), Intensity = 0.5f };
            Assert.IsFalse(_world.PowerUse.Use(_hero, new PowerUseRequest { PowerIndex = latent, Target = TargetKind.Self }).Attempted);
            Assert.IsFalse(_world.PowerUse.Use(_hero, new PowerUseRequest { PowerIndex = 9 }).Attempted);
            Assert.IsFalse(_world.PowerUse.Use(_hero, new PowerUseRequest { PowerIndex = bolt, Target = TargetKind.Point, Point = new WorldPosition(5000, 0, 0) }).Attempted, "out of range");

            Assert.IsTrue(_world.PowerUse.Use(_hero, point).Attempted);
            StringAssert.Contains("recovering", _world.PowerUse.Use(_hero, point).Message, "cooldown");
            Later(3600);
            _hero.Record.InCustody = true;
            Assert.IsFalse(_world.PowerUse.Use(_hero, point).Attempted);
            _hero.Record.InCustody = false;
            _world.Config.Powers.PowersEnabled = false;
            Assert.IsFalse(_world.PowerUse.Use(_hero, point).Attempted);
            _world.Config.Powers.PowersEnabled = true;

            _world.Config.Powers.OutputCap = 0.3f;
            var capped = UseUntil(point, o => o.Use.Success);
            Assert.LessOrEqual(capped.Plan.Damage, 0.15f + 0.55f * 0.3f + 1e-4, "server cap limits output");
        }

        [Test]
        public void Firebolt_AtSomeoneElsesBuilding_DamagesIt_AndIsACrimeWithAnEnergySignature()
        {
            var bolt = Give(Power(PowerDomain.Energy, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Fire, magnitude: 1f));
            var house = _world.Properties.All.OrderBy(p => p.Id).First(p => p.Kind == PropertyKind.House);
            var place = _world.Geography.GetPlace(house.Place);
            var origin = new WorldPosition(place.Position.X + 10f, 0f, place.Position.Z);
            var hit = UseUntil(new PowerUseRequest { PowerIndex = bolt, Target = TargetKind.Property, TargetId = house.Id, Origin = origin, Intensity = 1f }, o => o.Hit);
            Assert.IsTrue(hit.Hit, hit.Message);
            Assert.Greater(house.DamageRepairCents, 0);
            Assert.IsNotNull(hit.Crime);
            Assert.AreEqual("anomalous_property_destruction", hit.Crime.Incident.CrimeTypeId);
            Assert.IsTrue(_world.Justice.Evidence.Any(e => e.Kind == EvidenceKind.EnergySignature && e.Incident == hit.Crime.Incident.Id));
        }

        [Test]
        public void Strike_OnAPerson_HurtsThem_IsPoweredAssault_AndSeriousInjuryCallsEms()
        {
            var (npc, pos) = SomeoneOutdoors();
            var punch = Give(Power(PowerDomain.Force, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Kinetic, magnitude: 1f));
            var origin = new WorldPosition(pos.X + 5f, 0f, pos.Z);
            PowerOutcome last = null;
            for (var i = 0; i < 40 && npc.Health > 0.3f; i++)
            {
                Refresh();
                _world.Clock.AdvanceGame(5);
                _world.Director.Index.TryGet(npc.Id, _world.Clock.Now, out _, out var now);
                last = _world.PowerUse.Use(_hero, new PowerUseRequest { PowerIndex = punch, Target = TargetKind.Npc, TargetId = npc.Id, Origin = new WorldPosition(now.X + 5f, 0f, now.Z), Intensity = 1f });
            }
            Assert.LessOrEqual(npc.Health, 0.3f);
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.CrimeTypeId == "powered_assault" && i.Victim == npc.Id));
            Assert.IsTrue(_world.Emergency.Incidents.Any(i => i.Kind == EmergencyKind.Medical && i.Subject == npc.Id));
            Assert.IsNotNull(npc.MemoryOf(_hero.CharacterId, false, 0), "the victim remembers who did it");
        }

        [Test]
        public void Shield_AbsorbsDamage_AndHealingRestoresHealth()
        {
            var shield = Give(Power(PowerDomain.Defense, EffectVerb.Shield, DeliveryMode.Self, PowerElement.None));
            var heal = Give(Power(PowerDomain.Biology, EffectVerb.Transform, DeliveryMode.Self, PowerElement.Biological));
            var s = UseUntil(new PowerUseRequest { PowerIndex = shield, Target = TargetKind.Self, Intensity = 1f }, o => o.Use.Success);
            Assert.IsNotNull(_hero.Powers.ActiveOf(EffectKind.Shield, _world.Clock.Now.TotalSeconds));
            _world.PowerUse.Hurt(_hero, s.Plan.Amount * 0.5f, "test");
            Assert.AreEqual(1f, _hero.Health, 1e-4, "fully absorbed");
            _world.PowerUse.Hurt(_hero, s.Plan.Amount + 0.3f, "test");
            Assert.Less(_hero.Health, 1f, "overflow goes through");
            var before = _hero.Health;
            UseUntil(new PowerUseRequest { PowerIndex = heal, Target = TargetKind.Self, Intensity = 1f }, o => o.Use.Success);
            Assert.Greater(_hero.Health, before);
        }

        [Test]
        public void Teleport_MovesWithinRange_ButNotBeyond()
        {
            var blink = Give(Power(PowerDomain.Space, EffectVerb.Traverse, DeliveryMode.Self, PowerElement.Spatial, range: 50f));
            var near = new WorldPosition(20f, 0f, 0f);
            var ok = UseUntil(new PowerUseRequest { PowerIndex = blink, Target = TargetKind.Point, Point = near, Intensity = 1f }, o => o.Teleported);
            Assert.IsTrue(ok.Teleported, ok.Message);
            Assert.AreEqual(20f, _hero.LastPosition.X, 1e-3);
            var far = UseUntil(new PowerUseRequest { PowerIndex = blink, Origin = _hero.LastPosition, Target = TargetKind.Point, Point = new WorldPosition(500f, 0f, 0f), Intensity = 1f },
                o => o.Use.Success);
            Assert.IsFalse(far.Teleported);
            StringAssert.Contains("far", far.Message);
        }

        [Test]
        public void EnvironmentRequirements_Apply()
        {
            var shadow = Give(Power(PowerDomain.Energy, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Psychic,
                limits: new PowerLimitation { Kind = LimitationKind.EnvironmentRequirement, Requirement = "darkness", Severity = 1f }));
            var now = _world.Clock.Now;
            _world.Clock.AdvanceGame(now.StartOfDay.AddHours(36).TotalSeconds - now.TotalSeconds); // noon tomorrow
            var req = new PowerUseRequest { PowerIndex = shadow, Target = TargetKind.Point, Point = new WorldPosition(5, 0, 0) };
            StringAssert.Contains("dark", _world.PowerUse.Use(_hero, req).Message);
            _world.Clock.AdvanceGame(11 * 3600); // 23:00
            Assert.IsTrue(_world.PowerUse.Use(_hero, req).Attempted);
        }

        [Test]
        public void PublicUse_OutOfCostume_GivesWitnessesClues_InCostumeItDoesNot()
        {
            _hero.Alias = new AliasIdentity { Alias = "Static", CostumeOutfitId = "static_suit" };
            var flare = Give(Power(PowerDomain.Energy, EffectVerb.Generate, DeliveryMode.Area, PowerElement.Light, magnitude: 1f));
            _world.Config.Powers.CollateralDamage = false;
            _world.Config.Gameplay.PvpEnabled = false;
            PowerOutcome InCrowd()
            {
                PowerOutcome last = null;
                for (var i = 0; i < 30; i++)
                {
                    var (_, here) = SomeoneOutdoors();
                    Refresh();
                    last = _world.PowerUse.Use(_hero, new PowerUseRequest { PowerIndex = flare, Target = TargetKind.Point, Origin = here, Point = new WorldPosition(here.X, 0f, here.Z + 200f), Intensity = 1f });
                    if (last.Use.Success && last.Witnesses > 0) return last;
                    Later(97);
                }
                return last;
            }
            var bare = InCrowd();
            Assert.Greater(bare.Witnesses, 0);
            Assert.Greater(_hero.Alias.Knowledge.Count, 0, "witnesses who saw Nova's face now suspect she is Static");
            Assert.Greater(_hero.Reputation.Get(ReputationDimension.Notoriety), 0f);

            var clues = _hero.Alias.Knowledge.Sum(k => k.Clues.Count);
            _hero.CurrentOutfit = "static_suit";
            var costumed = InCrowd();
            Assert.Greater(costumed.Witnesses, 0);
            Assert.AreEqual(clues, _hero.Alias.Knowledge.Sum(k => k.Clues.Count), "in costume the alias is seen, not the civilian");
        }

        [Test]
        public void StaminaAndStrain_RecoverOverTime()
        {
            var bolt = Give(Power(PowerDomain.Energy, EffectVerb.Project, DeliveryMode.Projectile, PowerElement.Electric));
            for (var i = 0; i < 6; i++)
            {
                _world.Clock.AdvanceGame(3600);
                _hero.Powers.Powers[bolt].CooldownUntilSecond = 0;
                _world.PowerUse.Use(_hero, new PowerUseRequest { PowerIndex = bolt, Target = TargetKind.Point, Point = new WorldPosition(5, 0, 0), Intensity = 1f });
            }
            _hero.Powers.Stamina = 0.1f;
            _hero.Powers.Strain = 0.8f;
            new WorldSimulation(_world).AdvanceDays(0);
            var sim = new WorldSimulation(_world);
            _world.Clock.AdvanceGame(4 * 3600);
            sim.Update();
            Assert.AreEqual(1f, _hero.Powers.Stamina, 1e-4);
            Assert.Less(_hero.Powers.Strain, 0.6f);
        }
    }
}
