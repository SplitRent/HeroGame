using System.Collections.Generic;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Powers;

    public class PowerTests
    {
        private static AnomalySignature Neutral()
        {
            var s = new AnomalySignature { Intensity = 0.5f };
            foreach (PowerDomain d in System.Enum.GetValues(typeof(PowerDomain))) s.Domains.Add(new DomainAffinity { Domain = d, Weight = 1f });
            return s;
        }

        [Test]
        public void Rarity_EmergesFromComplexity()
        {
            var gen = new PowerGenerator(TestContent.Load().PowerArchetypes);
            var rng = new DeterministicRandom(123);
            int bodyEnhancement = 0, timeOrSpace = 0;
            const int samples = 20000;
            for (var i = 0; i < samples; i++)
            {
                var p = gen.Generate(Neutral(), rng);
                var c = p.Components[0];
                if ((c.Domain == PowerDomain.Force || c.Domain == PowerDomain.Defense || c.Domain == PowerDomain.Movement) && c.Verb == EffectVerb.Enhance) bodyEnhancement++;
                if (p.HasDomain(PowerDomain.Time) || p.HasDomain(PowerDomain.Space)) timeOrSpace++;
            }
            Assert.Greater(timeOrSpace, 0, "rare is not impossible");
            Assert.Greater(bodyEnhancement, timeOrSpace * 15, "enhancement=" + bodyEnhancement + " time/space=" + timeOrSpace);
        }

        [Test]
        public void Signatures_ShapeWhichPowersAppear()
        {
            var gen = new PowerGenerator(TestContent.Load().PowerArchetypes);
            var rng = new DeterministicRandom(5);
            var electric = new AnomalySignature { Intensity = 0.6f, Elements = { PowerElement.Electric } };
            electric.Domains.Add(new DomainAffinity { Domain = PowerDomain.Energy, Weight = 1f });
            var energy = 0;
            for (var i = 0; i < 2000; i++) if (gen.Generate(electric, rng).HasDomain(PowerDomain.Energy)) energy++;
            Assert.Greater(energy, 1000);
        }

        [Test]
        public void GeneratedPowers_AlwaysHaveLimitations()
        {
            var gen = new PowerGenerator(TestContent.Load().PowerArchetypes);
            var rng = new DeterministicRandom(9);
            for (var i = 0; i < 500; i++)
            {
                var p = gen.Generate(Neutral(), rng);
                Assert.Greater(p.Limitations.Count, 0);
                Assert.Greater(p.Complexity, 0f);
            }
        }

        [Test]
        public void MultiplePowers_AreExtraordinarilyRare()
        {
            var content = TestContent.Load();
            var anomalies = new AnomalySystem(77, new PowerGenerator(content.PowerArchetypes), content.AnomalyCauses);
            var ids = new IdAllocator();
            var evt = new AnomalyEvent { Id = ids.Next(EntityKind.AnomalyEvent), Radius = 50, Signature = Neutral() };
            evt.Signature.Intensity = 1f;
            int first = 0, second = 0;
            const int people = 20000;
            for (var i = 0; i < people; i++)
            {
                var unpowered = new CharacterPowers();
                if (anomalies.Expose(evt, new ExposureCandidate { Character = ids.Next(EntityKind.Character), Openness = 1f, Powers = unpowered }, 0) == ExposureResult.GainedFirstPower) first++;

                var powered = new CharacterPowers();
                powered.Powers.Add(new PowerInstance { Definition = new PowerDefinition { ArchetypeId = "kinetic_strength" } });
                if (anomalies.Expose(evt, new ExposureCandidate { Character = ids.Next(EntityKind.Character), Openness = 1f, Powers = powered }, 0) == ExposureResult.GainedAdditionalPower) second++;
            }
            // Epicentre of a maximal event: first manifestation is likely; a second power ~1 in 1,100.
            Assert.Greater(first, people / 4);
            Assert.Less(second, people / 300, "second powers must stay an anomaly: " + second);
        }

        [Test]
        public void Exposure_FallsOffWithDistanceAndShelter()
        {
            var evt = new AnomalyEvent { Radius = 100, Position = new WorldPosition(0, 0, 0), Signature = new AnomalySignature { Intensity = 1f } };
            var near = AnomalySystem.Dose(evt, new WorldPosition(5, 0, 0), 1f);
            var far = AnomalySystem.Dose(evt, new WorldPosition(80, 0, 0), 1f);
            var sheltered = AnomalySystem.Dose(evt, new WorldPosition(5, 0, 0), 0.3f);
            Assert.Greater(near, far);
            Assert.Greater(near, sheltered);
            Assert.AreEqual(0f, AnomalySystem.Dose(evt, new WorldPosition(150, 0, 0), 1f));
        }

        [Test]
        public void Discovery_ProgressesFromLatentThroughPractice()
        {
            var content = TestContent.Load();
            var gen = new PowerGenerator(content.PowerArchetypes);
            var archetype = gen.Find("telekinesis");
            var owner = new CharacterPowers();
            var power = new PowerInstance { Definition = gen.Instantiate(archetype, Neutral(), new DeterministicRandom(1)), ManifestDay = 5 };
            owner.Powers.Add(power);

            var rng = new DeterministicRandom(2);
            Assert.IsFalse(PowerProgression.Use(owner, power, archetype, 0.5f, rng).Success, "latent powers cannot be used");
            Assert.AreEqual(0, PowerProgression.AdvanceDay(owner, 4).Count);
            Assert.AreEqual(1, PowerProgression.AdvanceDay(owner, 5).Count);
            Assert.AreEqual(PowerStage.Manifesting, power.Stage);

            var day = 5L;
            for (var i = 0; i < 400 && power.Stage != PowerStage.Mastered; i++)
            {
                PowerProgression.Use(owner, power, archetype, 0.6f, rng);
                if (owner.Stamina < 0.2f) PowerProgression.AdvanceDay(owner, ++day);
            }
            Assert.AreEqual(PowerStage.Mastered, power.Stage);
            Assert.Greater(power.Progress.UnlockedEvolutions.Count, 0);
            Assert.IsTrue(power.Progress.UnlockedEvolutions.Contains("multi_object"));
        }

        [Test]
        public void Interactions_AreDataDriven()
        {
            var resolver = new PowerInteractionResolver(TestContent.Load().InteractionRules);
            var wet = resolver.Resolve(PowerElement.Electric, 1f, MaterialTag.Wet | MaterialTag.Organic);
            Assert.IsTrue(wet.Exists(r => r.Rule.Outcome == InteractionOutcome.Electrocute));

            var wetWood = resolver.Resolve(PowerElement.Fire, 1f, MaterialTag.Flammable | MaterialTag.Wet);
            Assert.IsFalse(wetWood.Exists(r => r.Rule.Outcome == InteractionOutcome.Ignite), "wet things do not ignite");

            var fuel = resolver.Resolve(PowerElement.Fire, 1f, MaterialTag.Flammable | MaterialTag.Explosive);
            Assert.AreEqual(InteractionOutcome.Detonate, fuel[0].Rule.Outcome, "highest priority first");

            var car = resolver.Resolve(PowerElement.Gravitic, 1f, MaterialTag.Vehicle | MaterialTag.Heavy | MaterialTag.Metallic);
            Assert.IsTrue(car.Exists(r => r.Rule.Outcome == InteractionOutcome.Lift));

            var burning = resolver.Resolve(PowerElement.Cold, 1f, MaterialTag.Burning);
            Assert.AreEqual(InteractionOutcome.Extinguish, burning[0].Rule.Outcome);
            Assert.AreEqual(MaterialTag.None, burning[0].ResultingTags & MaterialTag.Burning);
        }

        [Test]
        public void Anomalies_AreRareAndDeterministic()
        {
            var content = TestContent.Load();
            var a = new AnomalySystem(1, new PowerGenerator(content.PowerArchetypes), content.AnomalyCauses);
            var b = new AnomalySystem(1, new PowerGenerator(content.PowerArchetypes), content.AnomalyCauses);
            var districts = new List<(EntityId id, string type, WorldPosition center, float radius)>
            {
                (EntityId.Create(EntityKind.District, 1), "Industrial", new WorldPosition(), 300f),
                (EntityId.Create(EntityKind.District, 2), "InnerCity", new WorldPosition(500, 0, 0), 300f),
            };
            var idsA = new IdAllocator();
            var idsB = new IdAllocator();
            var events = 0;
            for (var day = 0; day < 3650; day++)
            {
                var ea = a.RollDaily(day, districts, idsA);
                var eb = b.RollDaily(day, districts, idsB);
                Assert.AreEqual(ea == null, eb == null);
                if (ea == null) continue;
                events++;
                Assert.AreEqual(ea.CauseId, eb.CauseId);
            }
            // ~1 per 45 days on average → roughly 80 in ten years.
            Assert.Greater(events, 40);
            Assert.Less(events, 140);
        }
    }
}
