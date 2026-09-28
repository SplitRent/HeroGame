using System;
using System.Collections.Generic;

namespace HeroGame.Core.Identity
{
    using HeroGame.Core.Foundation;

    /// <summary>Independent reputation axes (GDD §97). Never collapsed into one number.</summary>
    public enum ReputationDimension
    {
        Public,
        Criminal,
        Business,
        Heroic,
        Professional,
        Political,
        Notoriety,
    }

    [Serializable]
    public struct ReputationValue
    {
        public ReputationDimension Dimension;
        /// <summary>-100..100.</summary>
        public float Value;
    }

    [Serializable]
    public struct NeighborhoodStanding
    {
        public EntityId District;
        public float Value;
    }

    [Serializable]
    public sealed class ReputationProfile
    {
        public List<ReputationValue> Values = new List<ReputationValue>();
        public List<NeighborhoodStanding> Neighborhoods = new List<NeighborhoodStanding>();

        public float Get(ReputationDimension d)
        {
            foreach (var v in Values) if (v.Dimension == d) return v.Value;
            return 0f;
        }

        /// <summary>Adds with diminishing returns near the extremes.</summary>
        public float Add(ReputationDimension d, float delta)
        {
            for (var i = 0; i < Values.Count; i++)
            {
                if (Values[i].Dimension != d) continue;
                var v = Values[i];
                v.Value = Apply(v.Value, delta);
                Values[i] = v;
                return v.Value;
            }
            var created = new ReputationValue { Dimension = d, Value = Apply(0f, delta) };
            Values.Add(created);
            return created.Value;
        }

        public float GetNeighborhood(EntityId district)
        {
            foreach (var n in Neighborhoods) if (n.District == district) return n.Value;
            return 0f;
        }

        public float AddNeighborhood(EntityId district, float delta)
        {
            for (var i = 0; i < Neighborhoods.Count; i++)
            {
                if (Neighborhoods[i].District != district) continue;
                var n = Neighborhoods[i];
                n.Value = Apply(n.Value, delta);
                Neighborhoods[i] = n;
                return n.Value;
            }
            var created = new NeighborhoodStanding { District = district, Value = Apply(0f, delta) };
            Neighborhoods.Add(created);
            return created.Value;
        }

        private static float Apply(float current, float delta)
        {
            var headroom = delta >= 0 ? (100f - current) / 100f : (100f + current) / 100f;
            return Math.Max(-100f, Math.Min(100f, current + delta * Math.Max(0.05f, headroom)));
        }
    }
}
