using System;
using System.Collections.Generic;

namespace HeroGame.Core.Foundation
{
    /// <summary>
    /// Small, fast, fully deterministic PRNG (xoshiro256** seeded through SplitMix64).
    /// Identical sequences on every platform, so simulation results are reproducible
    /// between the Unity client, the headless server and the test suite.
    /// Never use UnityEngine.Random or System.Random inside simulation code.
    /// </summary>
    [Serializable]
    public sealed class DeterministicRandom
    {
        public ulong S0, S1, S2, S3;

        public DeterministicRandom() : this(0x9E3779B97F4A7C15UL) { }

        public DeterministicRandom(ulong seed)
        {
            var sm = seed;
            S0 = StableHash.SplitMix64(ref sm);
            S1 = StableHash.SplitMix64(ref sm);
            S2 = StableHash.SplitMix64(ref sm);
            S3 = StableHash.SplitMix64(ref sm);
        }

        /// <summary>Creates an independent stream for (seed, a, b...), e.g. (worldSeed, npcId, dayIndex).</summary>
        public static DeterministicRandom For(ulong seed, ulong a, ulong b = 0, ulong c = 0)
        {
            return new DeterministicRandom(StableHash.Combine(seed, a, b, c));
        }

        public ulong NextULong()
        {
            var result = RotL(S1 * 5, 7) * 9;
            var t = S1 << 17;
            S2 ^= S0;
            S3 ^= S1;
            S1 ^= S2;
            S0 ^= S3;
            S2 ^= t;
            S3 = RotL(S3, 45);
            return result;
        }

        /// <summary>Uniform double in [0, 1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public float NextFloat() => (float)NextDouble();

        /// <summary>Uniform int in [minInclusive, maxExclusive).</summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            var range = (ulong)((long)maxExclusive - minInclusive);
            return (int)(minInclusive + (long)(NextULong() % range));
        }

        public float Range(float min, float max) => min + (max - min) * NextFloat();

        public bool Chance(double probability) => probability > 0 && NextDouble() < probability;

        /// <summary>Standard normal sample (Box-Muller).</summary>
        public double NextGaussian()
        {
            var u1 = 1.0 - NextDouble();
            var u2 = NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        public T Pick<T>(IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0) throw new ArgumentException("Cannot pick from an empty list.");
            return items[NextInt(0, items.Count)];
        }

        /// <summary>Weighted index selection. Returns -1 when all weights are zero.</summary>
        public int PickWeighted(IReadOnlyList<double> weights)
        {
            double total = 0;
            for (var i = 0; i < weights.Count; i++) total += Math.Max(0, weights[i]);
            if (total <= 0) return -1;
            var roll = NextDouble() * total;
            for (var i = 0; i < weights.Count; i++)
            {
                roll -= Math.Max(0, weights[i]);
                if (roll < 0) return i;
            }
            return weights.Count - 1;
        }

        private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));
    }

    /// <summary>Stable (platform independent) hashing helpers. Never use string.GetHashCode for persistence.</summary>
    public static class StableHash
    {
        public static ulong SplitMix64(ref ulong state)
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public static ulong Mix(ulong x)
        {
            var s = x;
            return SplitMix64(ref s);
        }

        public static ulong Combine(ulong a, ulong b, ulong c = 0, ulong d = 0)
        {
            var h = Mix(a);
            h = Mix(h ^ (b + 0x632BE59BD9B4E019UL));
            h = Mix(h ^ (c + 0x8CB92BA72F3D8DD7UL));
            h = Mix(h ^ (d + 0xAEF17502108EF2D9UL));
            return h;
        }

        /// <summary>FNV-1a 64-bit over UTF-16 code units.</summary>
        public static ulong Of(string text)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            if (text == null) return hash;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= prime;
            }
            return hash;
        }
    }
}
