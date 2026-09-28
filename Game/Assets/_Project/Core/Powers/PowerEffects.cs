using System;

namespace HeroGame.Core.Powers
{
    using HeroGame.Core.Foundation;

    /// <summary>What a use of a power does, in engine terms (GDD §41). The runtime renders it; the server applies it.</summary>
    public enum EffectKind
    {
        None,
        /// <summary>Hit something: damage and knockback (projectile, beam, touch).</summary>
        Strike,
        /// <summary>Everything in a radius around a point.</summary>
        Blast,
        /// <summary>Faster movement for a while.</summary>
        SpeedBoost,
        /// <summary>A single huge jump.</summary>
        Leap,
        /// <summary>Sustained flight or hovering.</summary>
        Flight,
        /// <summary>Instant relocation within range.</summary>
        Teleport,
        /// <summary>Absorbs incoming damage for a while.</summary>
        Shield,
        /// <summary>Restores health (self or touched person).</summary>
        Heal,
        /// <summary>Lift/move an object by mass limit.</summary>
        Telekinesis,
        /// <summary>Reveal people, evidence and hidden things in a radius.</summary>
        Sense,
        /// <summary>Change appearance (concealment).</summary>
        Disguise,
        /// <summary>Local time distortion (the user acts faster).</summary>
        TimeDilation,
        /// <summary>Create a barrier/object.</summary>
        Construct,
    }

    public enum TargetKind
    {
        None,
        Self,
        Point,
        Npc,
        Character,
        Vehicle,
        Property,
    }

    /// <summary>A resolved effect with concrete numbers, before hit/miss and consequences.</summary>
    public struct EffectPlan
    {
        public EffectKind Kind;
        public PowerElement Element;
        /// <summary>Health fraction removed from a person hit at full effect (0..1).</summary>
        public float Damage;
        /// <summary>Knockback impulse (N·s) for the runtime.</summary>
        public float Impulse;
        /// <summary>Reach in metres (targets beyond it are out of range).</summary>
        public float Range;
        /// <summary>Area radius (Blast/Sense) in metres.</summary>
        public float Radius;
        /// <summary>Seconds the effect lasts (boosts, shields, flight).</summary>
        public float Duration;
        /// <summary>Multiplier for SpeedBoost/TimeDilation, jump height for Leap, absorb amount for Shield.</summary>
        public float Amount;
        /// <summary>Mass limit in kg (Telekinesis).</summary>
        public float MassKg;
        /// <summary>0..1 chance to hit the intended target at range (precision and distance).</summary>
        public float Accuracy;
        /// <summary>Real seconds before this power can be used again.</summary>
        public float CooldownSeconds;
        /// <summary>Whether this use is visible to bystanders (glows, noise, flying people).</summary>
        public bool Conspicuous;
    }

    /// <summary>
    /// Maps a power's composition to an <see cref="EffectPlan"/> (GDD §40–41). Pure and data-driven: new
    /// archetypes need no code because effects are derived from domain × verb × delivery, not from ids.
    /// </summary>
    public static class PowerEffects
    {
        public static EffectKind KindOf(PowerComponent c)
        {
            switch (c.Verb)
            {
                case EffectVerb.Shield: return EffectKind.Shield;
                case EffectVerb.Sense: return EffectKind.Sense;
                case EffectVerb.Transform: return c.Domain == PowerDomain.Biology ? EffectKind.Heal : EffectKind.Disguise;
                case EffectVerb.Generate: return c.Domain == PowerDomain.Matter || c.Domain == PowerDomain.Creation ? EffectKind.Construct : Offensive(c);
                case EffectVerb.Traverse:
                    if (c.Domain == PowerDomain.Space) return EffectKind.Teleport;
                    if (c.Domain == PowerDomain.Gravity) return EffectKind.Flight;
                    return EffectKind.SpeedBoost;
                case EffectVerb.Warp: return c.Domain == PowerDomain.Time ? EffectKind.TimeDilation : c.Domain == PowerDomain.Space ? EffectKind.Teleport : Offensive(c);
                case EffectVerb.Enhance:
                    if (c.Domain == PowerDomain.Movement) return c.Magnitude > 0.7f ? EffectKind.Leap : EffectKind.SpeedBoost;
                    if (c.Domain == PowerDomain.Biology) return EffectKind.Heal;
                    if (c.Domain == PowerDomain.Time) return EffectKind.TimeDilation;
                    if (c.Domain == PowerDomain.Perception) return EffectKind.Sense;
                    if (c.Domain == PowerDomain.Gravity) return EffectKind.Flight;
                    return EffectKind.Strike; // enhanced strength: stronger touch attacks
                case EffectVerb.Manipulate:
                    if (c.Domain == PowerDomain.Matter || c.Domain == PowerDomain.Force || c.Domain == PowerDomain.Gravity) return EffectKind.Telekinesis;
                    if (c.Domain == PowerDomain.Biology) return EffectKind.Heal;
                    return Offensive(c);
                default:
                    return Offensive(c);
            }
        }

        private static EffectKind Offensive(PowerComponent c) => c.Delivery == DeliveryMode.Area || c.Delivery == DeliveryMode.Field ? EffectKind.Blast : EffectKind.Strike;

        /// <summary>Plans a use at <paramref name="output"/> (from <see cref="PowerProgression.Use"/>).</summary>
        public static EffectPlan Plan(PowerInstance power, float output, float distance)
        {
            var c = power.Definition.Components[0];
            var p = power.Progress;
            var kind = KindOf(c);
            var range = c.Delivery == DeliveryMode.Self ? 0f : c.Delivery == DeliveryMode.Touch ? 2f : Math.Max(2f, c.Range * (1f + p.RangeBonus));
            var precision = Math.Min(1f, c.Precision + p.PrecisionBonus);
            var plan = new EffectPlan
            {
                Kind = kind,
                Element = c.Element,
                Range = range,
                Accuracy = range <= 0f ? 1f : Clamp01(0.35f + 0.65f * precision - Math.Max(0f, distance / Math.Max(1f, range) - 0.5f) * 0.6f),
                Conspicuous = c.Element != PowerElement.None && c.Element != PowerElement.Psychic || kind == EffectKind.Flight || kind == EffectKind.Blast || kind == EffectKind.Teleport,
            };
            switch (kind)
            {
                case EffectKind.Strike:
                    plan.Damage = 0.15f + 0.55f * output;
                    plan.Impulse = 400f + 5200f * output;
                    break;
                case EffectKind.Blast:
                    plan.Damage = 0.1f + 0.4f * output;
                    plan.Impulse = 300f + 3500f * output;
                    plan.Radius = 3f + 9f * output * (1f + p.RangeBonus * 0.5f);
                    break;
                case EffectKind.SpeedBoost:
                    plan.Amount = 1.3f + 1.7f * output;
                    plan.Duration = 4f + 8f * output;
                    break;
                case EffectKind.Leap:
                    plan.Amount = 3f + 17f * output;
                    break;
                case EffectKind.Flight:
                    plan.Duration = 3f + 27f * output;
                    plan.Amount = 6f + 24f * output;
                    break;
                case EffectKind.Teleport:
                    plan.Range = Math.Max(5f, c.Range * (1f + p.RangeBonus)) * (0.3f + 0.7f * output);
                    break;
                case EffectKind.Shield:
                    plan.Amount = 0.2f + 0.8f * output;
                    plan.Duration = 3f + 9f * output;
                    break;
                case EffectKind.Heal:
                    plan.Amount = 0.1f + 0.5f * output;
                    break;
                case EffectKind.Telekinesis:
                    plan.MassKg = 20f + 2500f * output * output;
                    plan.Impulse = 200f + 3000f * output;
                    break;
                case EffectKind.Sense:
                    plan.Radius = 15f + 85f * output;
                    plan.Duration = 5f + 15f * output;
                    break;
                case EffectKind.Disguise:
                    plan.Amount = Clamp01(0.5f + 0.5f * output);
                    plan.Duration = 60f + 540f * output;
                    break;
                case EffectKind.TimeDilation:
                    plan.Amount = 1.2f + 1.3f * output;
                    plan.Duration = 2f + 6f * output;
                    break;
                case EffectKind.Construct:
                    plan.Amount = 1f + 4f * output;
                    plan.Duration = 10f + 50f * output;
                    break;
            }
            var cooldown = 1.5f + 10f * c.Magnitude * (1f - Math.Min(0.9f, c.Efficiency + p.EfficiencyBonus));
            foreach (var l in power.Definition.Limitations)
                if (l.Kind == LimitationKind.Cooldown) cooldown *= 1f + 2f * l.Severity;
            if (kind == EffectKind.Teleport || kind == EffectKind.TimeDilation) cooldown += 6f;
            plan.CooldownSeconds = cooldown;
            return plan;
        }

        /// <summary>Material tags of a world target, for the element interaction rules.</summary>
        public static MaterialTag TagsFor(TargetKind target, bool wet, bool burning)
        {
            var tags = MaterialTag.None;
            switch (target)
            {
                case TargetKind.Npc:
                case TargetKind.Character:
                    tags = MaterialTag.Organic;
                    break;
                case TargetKind.Vehicle:
                    tags = MaterialTag.Vehicle | MaterialTag.Metallic | MaterialTag.Conductive | MaterialTag.Heavy | MaterialTag.Flammable;
                    break;
                case TargetKind.Property:
                    tags = MaterialTag.Heavy | MaterialTag.Fragile | MaterialTag.Flammable;
                    break;
            }
            if (wet) tags |= MaterialTag.Wet;
            if (burning) tags |= MaterialTag.Burning;
            return tags;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
