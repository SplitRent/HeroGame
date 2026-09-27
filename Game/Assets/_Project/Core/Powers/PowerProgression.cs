using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Powers
{
    public struct PowerUseResult
    {
        public bool Success;
        /// <summary>Effective output after stage, strain and bonuses (0..~1.5).</summary>
        public float Output;
        public float StaminaCost;
        public float StrainAdded;
        /// <summary>Uncontrolled side effect (instability, low precision) the runtime should render.</summary>
        public bool Backfire;
        public string NewEvolution;
        public PowerStage? StageAdvancedTo;
    }

    /// <summary>
    /// Discovery, practice and evolution of abilities (GDD §43–44). The runtime calls
    /// <see cref="AdvanceDay"/> for incubation/recovery and <see cref="Use"/> whenever a power is
    /// attempted; this class decides whether it works and how the character grows.
    /// </summary>
    public static class PowerProgression
    {
        public const float AwareExperience = 0f;
        public const float PracticedExperience = 120f;
        public const float MasteredExperience = 600f;

        /// <summary>Daily incubation and recovery. Returns powers that just started manifesting.</summary>
        public static List<PowerInstance> AdvanceDay(CharacterPowers powers, long dayIndex)
        {
            var started = new List<PowerInstance>();
            foreach (var p in powers.Powers)
            {
                if (p.Stage == PowerStage.Latent && dayIndex >= p.ManifestDay)
                {
                    p.Stage = PowerStage.Manifesting;
                    p.StageChangedDay = dayIndex;
                    started.Add(p);
                }
            }
            powers.Strain = Math.Max(0f, powers.Strain - 0.35f);
            powers.Stamina = 1f;
            return started;
        }

        /// <summary>
        /// Picks today's involuntary symptom for a manifesting power (deterministic per day).
        /// Symptoms are how players first learn something is happening to them.
        /// </summary>
        public static string SymptomFor(PowerInstance power, PowerArchetype archetype, long dayIndex, ulong worldSeed)
        {
            if (power.Stage != PowerStage.Manifesting) return null;
            var rng = DeterministicRandom.For(worldSeed, power.Definition.Seed, (ulong)dayIndex, 0x5E);
            if (!rng.Chance(0.6)) return null;
            if (archetype != null && archetype.Symptoms.Count > 0) return rng.Pick(archetype.Symptoms);
            return "unexplained_" + power.Definition.Components[0].Domain.ToString().ToLowerInvariant();
        }

        /// <summary>Attempts a use at the requested intensity (0..1).</summary>
        public static PowerUseResult Use(CharacterPowers owner, PowerInstance power, PowerArchetype archetype, float intensity, DeterministicRandom rng)
        {
            var result = new PowerUseResult();
            if (power.Stage == PowerStage.Latent) return result;
            intensity = Math.Max(0.05f, Math.Min(1f, intensity));
            var primary = power.Definition.Components[0];

            var efficiency = Math.Min(0.95f, primary.Efficiency + power.Progress.EfficiencyBonus);
            result.StaminaCost = intensity * (1f - efficiency) * 0.5f;
            result.StrainAdded = intensity * intensity * (1f - efficiency) * 0.3f;
            foreach (var l in power.Definition.Limitations)
            {
                if (l.Kind == LimitationKind.Stamina) result.StaminaCost *= 1f + l.Severity;
                if (l.Kind == LimitationKind.PhysicalStrain) result.StrainAdded *= 1f + l.Severity * 1.5f;
            }

            // Control: manifesting powers are mostly involuntary; practice brings reliability.
            var control = StageControl(power.Stage) + power.Progress.PrecisionBonus + primary.Precision * 0.3f - owner.Strain * 0.5f;
            if (owner.Stamina < result.StaminaCost) control -= 0.4f;
            var instability = 0f;
            foreach (var l in power.Definition.Limitations) if (l.Kind == LimitationKind.Instability) instability = l.Severity;

            result.Success = rng.NextDouble() < Math.Max(0.05, Math.Min(0.98, control));
            result.Backfire = !result.Success && rng.NextDouble() < 0.25 + instability * 0.5;
            result.Output = result.Success ? intensity * Math.Min(1.5f, primary.Magnitude + power.Progress.StrengthBonus) : 0f;

            owner.Stamina = Math.Max(0f, owner.Stamina - result.StaminaCost);
            owner.Strain = Math.Min(1f, owner.Strain + result.StrainAdded);

            // Learning happens from attempts, faster from successes and near the edge of ability.
            var gained = (result.Success ? 4f : 1.5f) * (0.5f + intensity);
            power.Progress.Experience += gained;
            if (result.Success) power.Progress.SuccessfulUses++;
            else power.Progress.FailedUses++;
            var xp = power.Progress.Experience;
            power.Progress.StrengthBonus = Math.Min(0.5f, xp / 2400f);
            power.Progress.PrecisionBonus = Math.Min(0.4f, xp / 1500f);
            power.Progress.RangeBonus = Math.Min(0.8f, xp / 1000f);
            power.Progress.EfficiencyBonus = Math.Min(0.3f, xp / 2000f);

            var newStage = power.Stage;
            if (power.Stage == PowerStage.Manifesting && result.Success) newStage = PowerStage.Aware;
            else if (power.Stage == PowerStage.Aware && xp >= PracticedExperience) newStage = PowerStage.Practiced;
            else if (power.Stage == PowerStage.Practiced && xp >= MasteredExperience) newStage = PowerStage.Mastered;
            if (newStage != power.Stage)
            {
                power.Stage = newStage;
                result.StageAdvancedTo = newStage;
            }

            if (archetype != null)
            {
                foreach (var evo in archetype.Evolutions)
                {
                    if (power.Progress.UnlockedEvolutions.Contains(evo.Id) || xp < evo.RequiredExperience) continue;
                    if (!string.IsNullOrEmpty(evo.Requires) && !power.Progress.UnlockedEvolutions.Contains(evo.Requires)) continue;
                    power.Progress.UnlockedEvolutions.Add(evo.Id);
                    result.NewEvolution = evo.Id;
                    break; // one discovery at a time
                }
            }
            return result;
        }

        private static float StageControl(PowerStage stage)
        {
            switch (stage)
            {
                case PowerStage.Manifesting: return 0.15f;
                case PowerStage.Aware: return 0.5f;
                case PowerStage.Practiced: return 0.75f;
                case PowerStage.Mastered: return 0.9f;
                default: return 0f;
            }
        }
    }
}
