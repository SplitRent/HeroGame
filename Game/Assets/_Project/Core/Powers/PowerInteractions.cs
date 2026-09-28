using System;
using System.Collections.Generic;

namespace HeroGame.Core.Powers
{
    /// <summary>Material/state tags carried by world objects, characters and effects.</summary>
    [Flags]
    public enum MaterialTag
    {
        None = 0,
        Wet = 1 << 0,
        Flammable = 1 << 1,
        Conductive = 1 << 2,
        Metallic = 1 << 3,
        Heavy = 1 << 4,
        Fragile = 1 << 5,
        Explosive = 1 << 6,
        Organic = 1 << 7,
        Frozen = 1 << 8,
        Burning = 1 << 9,
        Electrified = 1 << 10,
        Projectile = 1 << 11,
        Vehicle = 1 << 12,
        Shielded = 1 << 13,
        Portal = 1 << 14,
        HighMomentum = 1 << 15,
        Liquid = 1 << 16,
        Gas = 1 << 17,
    }

    public enum InteractionOutcome
    {
        Electrocute,
        ChainArc,
        Ignite,
        Detonate,
        Freeze,
        Melt,
        Extinguish,
        Steam,
        Lift,
        Crush,
        Shatter,
        Deflect,
        Redirect,
        Conduct,
        Slow,
        Accelerate,
    }

    /// <summary>One data-driven rule: when an effect of <see cref="Element"/> meets a target with <see cref="RequiredTags"/>.</summary>
    [Serializable]
    public sealed class InteractionRule
    {
        public string Id = "";
        public PowerElement Element;
        /// <summary>All of these tags must be present on the target.</summary>
        public MaterialTag RequiredTags;
        /// <summary>Rule does not fire if any of these are present.</summary>
        public MaterialTag BlockedByTags;
        public InteractionOutcome Outcome;
        /// <summary>Multiplier applied to effect magnitude.</summary>
        public float Scale = 1f;
        /// <summary>Tags added to the target after the interaction (e.g. Burning after Ignite).</summary>
        public MaterialTag AddsTags;
        public MaterialTag RemovesTags;
        public int Priority;
    }

    public struct InteractionResult
    {
        public InteractionRule Rule;
        public float Magnitude;
        public MaterialTag ResultingTags;
    }

    /// <summary>
    /// Resolves how powers affect materials and each other (GDD §109): electricity + water,
    /// fire + fuel, gravity + vehicles, portals + projectiles… The table is data; the resolver is
    /// generic, so new powers get interactions for free by declaring an element.
    /// </summary>
    public sealed class PowerInteractionResolver
    {
        private readonly List<InteractionRule> _rules;

        public PowerInteractionResolver(IEnumerable<InteractionRule> rules)
        {
            _rules = new List<InteractionRule>(rules);
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public int RuleCount => _rules.Count;

        public List<InteractionResult> Resolve(PowerElement element, float magnitude, MaterialTag targetTags)
        {
            var results = new List<InteractionResult>();
            var tags = targetTags;
            foreach (var rule in _rules)
            {
                if (rule.Element != element) continue;
                if ((tags & rule.RequiredTags) != rule.RequiredTags) continue;
                if ((tags & rule.BlockedByTags) != 0) continue;
                tags = (tags | rule.AddsTags) & ~rule.RemovesTags;
                results.Add(new InteractionResult { Rule = rule, Magnitude = magnitude * rule.Scale, ResultingTags = tags });
            }
            return results;
        }
    }
}
