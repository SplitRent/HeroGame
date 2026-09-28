using System;
using System.Collections.Generic;

namespace HeroGame.Core.Powers
{
    using HeroGame.Core.Foundation;

    [Serializable]
    public struct DomainAffinity
    {
        public PowerDomain Domain;
        public float Weight;
    }

    /// <summary>
    /// The "flavour" of an anomaly event: which domains and elements it resonates with. A lightning
    /// strike on the ship canal skews toward Energy/Electric; a refinery pressure-vessel failure toward
    /// Force/Matter/Fire. Different events therefore produce different powers (GDD §39).
    /// </summary>
    [Serializable]
    public sealed class AnomalySignature
    {
        public List<DomainAffinity> Domains = new List<DomainAffinity>();
        public List<PowerElement> Elements = new List<PowerElement>();
        /// <summary>0..1 raw strength; stronger events can push toward rarer compositions.</summary>
        public float Intensity = 0.5f;

        public float AffinityFor(PowerDomain domain)
        {
            foreach (var d in Domains) if (d.Domain == domain) return d.Weight;
            return 0.08f; // every event carries a little of everything
        }
    }

    /// <summary>
    /// Generates concrete abilities from archetypes and anomaly signatures. Rarity is emergent:
    /// weight = affinity × exp(−k × complexity) (GDD §41). Very occasionally a novel composition is
    /// assembled directly from components with no archetype at all.
    /// </summary>
    public sealed class PowerGenerator
    {
        /// <summary>Complexity falloff. Tuned so time/space effects are roughly 50–150× rarer than body enhancement.</summary>
        public float RarityFalloff = 1.55f;
        public float NovelCompositionChance = 0.03f;

        private readonly List<PowerArchetype> _archetypes;
        private readonly List<float> _archetypeComplexity = new List<float>();

        public PowerGenerator(IEnumerable<PowerArchetype> archetypes)
        {
            _archetypes = new List<PowerArchetype>(archetypes);
            _archetypes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (var a in _archetypes) _archetypeComplexity.Add(PowerComplexity.Of(a.Components));
        }

        public IReadOnlyList<PowerArchetype> Archetypes => _archetypes;

        public PowerArchetype Find(string id)
        {
            foreach (var a in _archetypes) if (a.Id == id) return a;
            return null;
        }

        /// <summary>Relative probability that this signature produces this archetype.</summary>
        public double Weight(PowerArchetype archetype, float complexity, AnomalySignature signature)
        {
            var affinity = 0f;
            foreach (var c in archetype.Components) affinity = Math.Max(affinity, signature.AffinityFor(c.Domain));
            // Intense events flatten the falloff a little: rare powers come from rare events.
            var falloff = RarityFalloff * (1.15f - 0.3f * signature.Intensity);
            return affinity * Math.Exp(-falloff * complexity);
        }

        /// <param name="exclude">Archetypes the character already has (secondary powers are always different).</param>
        public PowerDefinition Generate(AnomalySignature signature, DeterministicRandom rng, ICollection<string> exclude = null)
        {
            if (_archetypes.Count == 0 || rng.Chance(NovelCompositionChance * (0.5 + signature.Intensity)))
            {
                var novel = GenerateNovel(signature, rng);
                if (novel != null) return novel;
            }

            var weights = new List<double>(_archetypes.Count);
            for (var i = 0; i < _archetypes.Count; i++)
            {
                var excluded = exclude != null && exclude.Contains(_archetypes[i].Id);
                weights.Add(excluded ? 0 : Weight(_archetypes[i], _archetypeComplexity[i], signature));
            }
            var index = rng.PickWeighted(weights);
            if (index < 0) return GenerateNovel(signature, rng);
            return Instantiate(_archetypes[index], signature, rng);
        }

        public PowerDefinition Instantiate(PowerArchetype archetype, AnomalySignature signature, DeterministicRandom rng)
        {
            var def = new PowerDefinition { ArchetypeId = archetype.Id, Seed = rng.NextULong() };
            var element = PickElement(archetype.AllowedElements, signature, rng);
            foreach (var template in archetype.Components)
            {
                def.Components.Add(new PowerComponent
                {
                    Domain = template.Domain,
                    Verb = template.Verb,
                    Delivery = template.Delivery,
                    Element = template.Element == PowerElement.None && def.Components.Count == 0 ? element : template.Element,
                    Magnitude = Jitter(template.Magnitude, 0.18f, rng),
                    Range = template.Range * (0.7f + rng.NextFloat() * 0.6f),
                    Precision = Jitter(template.Precision, 0.2f, rng),
                    Efficiency = Jitter(template.Efficiency, 0.2f, rng),
                });
            }
            AddLimitations(def, archetype.TypicalLimitations, rng);
            def.Complexity = PowerComplexity.Of(def.Components);
            return def;
        }

        /// <summary>Assembles an archetype-free ability from a valid domain/verb/delivery triple.</summary>
        public PowerDefinition GenerateNovel(AnomalySignature signature, DeterministicRandom rng)
        {
            var domains = (PowerDomain[])Enum.GetValues(typeof(PowerDomain));
            var candidates = new List<PowerComponent>();
            var weights = new List<double>();
            foreach (var d in domains)
            {
                foreach (var v in CompositionRules.VerbsFor(d))
                {
                    foreach (var m in CompositionRules.DeliveriesFor(v))
                    {
                        var c = new PowerComponent { Domain = d, Verb = v, Delivery = m, Magnitude = 0.5f };
                        candidates.Add(c);
                        weights.Add(signature.AffinityFor(d) * Math.Exp(-RarityFalloff * PowerComplexity.Of(c)));
                    }
                }
            }
            var index = rng.PickWeighted(weights);
            if (index < 0) return null;
            var chosen = candidates[index];
            chosen.Element = PickElement(CompositionRules.ElementsFor(chosen.Domain), signature, rng);
            chosen.Magnitude = 0.3f + rng.NextFloat() * 0.5f;
            chosen.Range = chosen.Delivery == DeliveryMode.Self ? 0f : 5f + rng.NextFloat() * 40f;
            chosen.Precision = 0.2f + rng.NextFloat() * 0.5f;
            chosen.Efficiency = 0.2f + rng.NextFloat() * 0.5f;
            var def = new PowerDefinition { Novel = true, Seed = rng.NextULong() };
            def.Components.Add(chosen);
            AddLimitations(def, new List<LimitationKind> { LimitationKind.Instability, LimitationKind.Stamina, LimitationKind.Concentration }, rng);
            def.Complexity = PowerComplexity.Of(def.Components);
            return def;
        }

        private static void AddLimitations(PowerDefinition def, List<LimitationKind> typical, DeterministicRandom rng)
        {
            // Stronger abilities carry heavier constraints — power budget, not arbitrary weakness (GDD §47).
            var budget = 0f;
            foreach (var c in def.Components) budget += c.Magnitude;
            var count = Math.Max(1, Math.Min(3, (int)Math.Round(budget * 1.5f)));
            var pool = new List<LimitationKind>(typical.Count > 0 ? typical : new List<LimitationKind> { LimitationKind.Stamina });
            for (var i = 0; i < count && pool.Count > 0; i++)
            {
                var kind = pool[rng.NextInt(0, pool.Count)];
                pool.Remove(kind);
                var limit = new PowerLimitation { Kind = kind, Severity = Math.Min(1f, 0.2f + budget * 0.3f + rng.NextFloat() * 0.3f) };
                if (kind == LimitationKind.EnvironmentRequirement) limit.Requirement = CompositionRules.RequirementFor(def.PrimaryElement, rng);
                def.Limitations.Add(limit);
            }
        }

        private static PowerElement PickElement(List<PowerElement> allowed, AnomalySignature signature, DeterministicRandom rng)
        {
            if (allowed == null || allowed.Count == 0) return PowerElement.None;
            var weights = new List<double>();
            foreach (var e in allowed) weights.Add(signature.Elements.Contains(e) ? 5.0 : 1.0);
            return allowed[Math.Max(0, rng.PickWeighted(weights))];
        }

        private static float Jitter(float value, float amount, DeterministicRandom rng)
        {
            return Math.Max(0.05f, Math.Min(1f, value + (rng.NextFloat() * 2f - 1f) * amount));
        }
    }

    /// <summary>Which domain/verb/delivery combinations are physically coherent.</summary>
    public static class CompositionRules
    {
        public static IEnumerable<EffectVerb> VerbsFor(PowerDomain d)
        {
            switch (d)
            {
                case PowerDomain.Force: return new[] { EffectVerb.Enhance, EffectVerb.Project, EffectVerb.Manipulate, EffectVerb.Absorb };
                case PowerDomain.Movement: return new[] { EffectVerb.Enhance, EffectVerb.Traverse };
                case PowerDomain.Energy: return new[] { EffectVerb.Project, EffectVerb.Generate, EffectVerb.Absorb, EffectVerb.Manipulate, EffectVerb.Shield };
                case PowerDomain.Matter: return new[] { EffectVerb.Transform, EffectVerb.Manipulate, EffectVerb.Enhance };
                case PowerDomain.Gravity: return new[] { EffectVerb.Manipulate, EffectVerb.Project, EffectVerb.Traverse };
                case PowerDomain.Time: return new[] { EffectVerb.Sense, EffectVerb.Warp, EffectVerb.Enhance };
                case PowerDomain.Space: return new[] { EffectVerb.Traverse, EffectVerb.Warp };
                case PowerDomain.Biology: return new[] { EffectVerb.Enhance, EffectVerb.Transform, EffectVerb.Duplicate };
                case PowerDomain.Perception: return new[] { EffectVerb.Sense, EffectVerb.Enhance, EffectVerb.Copy };
                case PowerDomain.Transformation: return new[] { EffectVerb.Transform };
                case PowerDomain.Creation: return new[] { EffectVerb.Generate };
                case PowerDomain.Manipulation: return new[] { EffectVerb.Manipulate, EffectVerb.Copy };
                case PowerDomain.Defense: return new[] { EffectVerb.Shield, EffectVerb.Enhance, EffectVerb.Absorb };
                case PowerDomain.Environment: return new[] { EffectVerb.Manipulate, EffectVerb.Generate };
                default: return Array.Empty<EffectVerb>();
            }
        }

        public static IEnumerable<DeliveryMode> DeliveriesFor(EffectVerb v)
        {
            switch (v)
            {
                case EffectVerb.Enhance:
                case EffectVerb.Sense:
                case EffectVerb.Traverse:
                case EffectVerb.Transform:
                case EffectVerb.Duplicate:
                    return new[] { DeliveryMode.Self };
                case EffectVerb.Project: return new[] { DeliveryMode.Projectile, DeliveryMode.Beam, DeliveryMode.Area };
                case EffectVerb.Manipulate: return new[] { DeliveryMode.Touch, DeliveryMode.Remote, DeliveryMode.Area };
                case EffectVerb.Generate: return new[] { DeliveryMode.Touch, DeliveryMode.Area, DeliveryMode.Remote };
                case EffectVerb.Absorb: return new[] { DeliveryMode.Self, DeliveryMode.Touch };
                case EffectVerb.Shield: return new[] { DeliveryMode.Self, DeliveryMode.Field };
                case EffectVerb.Copy: return new[] { DeliveryMode.Touch };
                case EffectVerb.Warp: return new[] { DeliveryMode.Field, DeliveryMode.Area };
                default: return new[] { DeliveryMode.Self };
            }
        }

        public static List<PowerElement> ElementsFor(PowerDomain d)
        {
            switch (d)
            {
                case PowerDomain.Energy: return new List<PowerElement> { PowerElement.Electric, PowerElement.Fire, PowerElement.Cold, PowerElement.Light, PowerElement.Sound };
                case PowerDomain.Force: return new List<PowerElement> { PowerElement.Kinetic };
                case PowerDomain.Gravity: return new List<PowerElement> { PowerElement.Gravitic };
                case PowerDomain.Time: return new List<PowerElement> { PowerElement.Temporal };
                case PowerDomain.Space: return new List<PowerElement> { PowerElement.Spatial };
                case PowerDomain.Biology: return new List<PowerElement> { PowerElement.Biological };
                case PowerDomain.Perception: return new List<PowerElement> { PowerElement.Psychic };
                case PowerDomain.Environment: return new List<PowerElement> { PowerElement.Fire, PowerElement.Cold, PowerElement.Electric };
                default: return new List<PowerElement> { PowerElement.None };
            }
        }

        public static string RequirementFor(PowerElement element, DeterministicRandom rng)
        {
            switch (element)
            {
                case PowerElement.Electric: return rng.Chance(0.5) ? "power_source" : "dry_conditions";
                case PowerElement.Fire: return "heat";
                case PowerElement.Cold: return "moisture";
                case PowerElement.Light: return "daylight";
                case PowerElement.Kinetic: return "solid_footing";
                default: return rng.Chance(0.5) ? "calm_mind" : "line_of_sight";
            }
        }
    }
}
