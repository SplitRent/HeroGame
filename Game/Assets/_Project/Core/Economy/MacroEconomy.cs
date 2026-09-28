using System;

namespace HeroGame.Core.Economy
{
    using HeroGame.Core.Foundation;

    /// <summary>
    /// Aggregate city economy (GDD §23): a business-cycle index, inflation and unemployment.
    /// Individual transactions are not simulated; businesses and NPCs read these values as
    /// multipliers. Updated once per game day.
    /// </summary>
    [Serializable]
    public sealed class MacroEconomyState
    {
        /// <summary>1.0 = normal. ~0.8 = recession, ~1.2 = boom.</summary>
        public double CycleIndex = 1.0;
        /// <summary>Drift direction of the business cycle (per day).</summary>
        public double Momentum;
        public double AnnualInflation = 0.028;
        /// <summary>Cumulative price level since world creation (1.0 at start).</summary>
        public double PriceLevel = 1.0;
        public double UnemploymentRate = 0.045;
        public bool InRecession;
        public int DaysInCurrentPhase;
        /// <summary>Temporary shock (storms, disasters). Decays toward zero.</summary>
        public double Shock;
    }

    public static class MacroEconomySimulator
    {
        /// <summary>Advances the macro economy by one day using a mean-reverting random walk with rare regime changes.</summary>
        public static void StepDay(MacroEconomyState s, ulong worldSeed, long dayIndex)
        {
            var rng = DeterministicRandom.For(worldSeed, 0xEC0, (ulong)dayIndex);
            s.DaysInCurrentPhase++;

            // Rare regime changes: ~1 recession every few in-game years, lasting months.
            if (!s.InRecession && s.DaysInCurrentPhase > 180 && rng.Chance(1.0 / 900.0))
            {
                s.InRecession = true;
                s.DaysInCurrentPhase = 0;
            }
            else if (s.InRecession && s.DaysInCurrentPhase > 90 && rng.Chance(1.0 / 120.0))
            {
                s.InRecession = false;
                s.DaysInCurrentPhase = 0;
            }

            var target = s.InRecession ? 0.82 : 1.04;
            // AR(2)-style cycle. Parameters chosen so 30 simulated years stay within ~0.72–1.13
            // (recessions bottom out ~0.75) instead of drifting into the clamps.
            s.Momentum = s.Momentum * 0.85 + (target - s.CycleIndex) * 0.006 + rng.NextGaussian() * 0.0012;
            s.CycleIndex = Clamp(s.CycleIndex + s.Momentum + s.Shock * 0.01, 0.6, 1.4);
            s.Shock *= 0.93;

            var targetInflation = s.InRecession ? 0.012 : 0.026 + (s.CycleIndex - 1.0) * 0.05;
            s.AnnualInflation += (targetInflation - s.AnnualInflation) * 0.01;
            s.PriceLevel *= 1.0 + s.AnnualInflation / 365.0;

            var targetUnemployment = 0.045 + (1.0 - s.CycleIndex) * 0.25;
            s.UnemploymentRate = Clamp(s.UnemploymentRate + (targetUnemployment - s.UnemploymentRate) * 0.02, 0.02, 0.2);
        }

        /// <summary>Applies an external shock (e.g. hurricane: negative). Magnitude roughly −1..1.</summary>
        public static void ApplyShock(MacroEconomyState s, double magnitude)
        {
            s.Shock = Clamp(s.Shock + magnitude, -3, 3);
        }

        private static double Clamp(double v, double min, double max) => Math.Min(max, Math.Max(min, v));
    }
}
