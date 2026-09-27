using System;
using System.Collections.Generic;

namespace HeroGame.Core.Powers
{
    /// <summary>
    /// Scores how "reality-bending" a composition is. Rarity is never a label: it emerges because the
    /// generator's sampling weight falls off exponentially with complexity (GDD §41). Enhancing your
    /// own body is common; bending time or space is vanishingly rare.
    /// </summary>
    public static class PowerComplexity
    {
        private static readonly Dictionary<PowerDomain, float> DomainCost = new Dictionary<PowerDomain, float>
        {
            { PowerDomain.Force, 1.0f },
            { PowerDomain.Defense, 1.0f },
            { PowerDomain.Movement, 1.2f },
            { PowerDomain.Biology, 1.3f },
            { PowerDomain.Perception, 1.3f },
            { PowerDomain.Energy, 1.5f },
            { PowerDomain.Environment, 1.7f },
            { PowerDomain.Manipulation, 1.8f },
            { PowerDomain.Transformation, 2.0f },
            { PowerDomain.Matter, 2.2f },
            { PowerDomain.Creation, 2.4f },
            { PowerDomain.Gravity, 2.6f },
            { PowerDomain.Space, 3.2f },
            { PowerDomain.Time, 3.8f },
        };

        private static readonly Dictionary<EffectVerb, float> VerbCost = new Dictionary<EffectVerb, float>
        {
            { EffectVerb.Enhance, 0f },
            { EffectVerb.Shield, 0.1f },
            { EffectVerb.Sense, 0.1f },
            { EffectVerb.Project, 0.3f },
            { EffectVerb.Generate, 0.4f },
            { EffectVerb.Traverse, 0.4f },
            { EffectVerb.Manipulate, 0.5f },
            { EffectVerb.Absorb, 0.6f },
            { EffectVerb.Transform, 0.7f },
            { EffectVerb.Duplicate, 1.2f },
            { EffectVerb.Warp, 1.4f },
            { EffectVerb.Copy, 1.6f },
        };

        private static readonly Dictionary<DeliveryMode, float> DeliveryCost = new Dictionary<DeliveryMode, float>
        {
            { DeliveryMode.Self, 0f },
            { DeliveryMode.Touch, 0.1f },
            { DeliveryMode.Projectile, 0.2f },
            { DeliveryMode.Beam, 0.3f },
            { DeliveryMode.Area, 0.5f },
            { DeliveryMode.Field, 0.5f },
            { DeliveryMode.Remote, 0.6f },
        };

        public static float Of(PowerComponent c)
        {
            return DomainCost[c.Domain] + VerbCost[c.Verb] + DeliveryCost[c.Delivery] + c.Magnitude * 0.4f;
        }

        public static float Of(IReadOnlyList<PowerComponent> components)
        {
            if (components.Count == 0) return 0f;
            var total = 0f;
            var max = 0f;
            foreach (var c in components)
            {
                var v = Of(c);
                total += v;
                max = Math.Max(max, v);
            }
            // The hardest component dominates; extra components add a smaller increment.
            return max + (total - max) * 0.45f + (components.Count - 1) * 0.3f;
        }

        public static float DomainBaseCost(PowerDomain domain) => DomainCost[domain];
    }
}
