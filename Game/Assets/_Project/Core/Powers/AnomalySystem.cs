using System;
using System.Collections.Generic;

namespace HeroGame.Core.Powers
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Time;

    /// <summary>An in-world anomalous incident that can expose people (GDD §39, §130).</summary>
    [Serializable]
    public sealed class AnomalyEvent
    {
        public EntityId Id;
        public GameDateTime OccurredAt;
        public EntityId District;
        public WorldPosition Position;
        public float Radius = 60f;
        public string CauseId = "";
        public string Description = "";
        public AnomalySignature Signature = new AnomalySignature();
        /// <summary>Visible phenomena (keys for VFX/news): "aurora_column", "pressure_wave", "static_storm"…</summary>
        public List<string> Phenomena = new List<string>();
        public List<EntityId> Exposed = new List<EntityId>();
        public bool Investigated;
    }

    /// <summary>Data-driven template for what can cause an anomaly (anomaly_causes.json).</summary>
    [Serializable]
    public sealed class AnomalyCause
    {
        public string Id = "";
        public string Description = "";
        public List<DomainAffinity> Domains = new List<DomainAffinity>();
        public List<PowerElement> Elements = new List<PowerElement>();
        public List<string> Phenomena = new List<string>();
        /// <summary>District types where this cause can occur (empty = anywhere).</summary>
        public List<string> DistrictTypes = new List<string>();
        public float Weight = 1f;
        public float MinIntensity = 0.2f;
        public float MaxIntensity = 0.9f;
    }

    public struct ExposureCandidate
    {
        public EntityId Character;
        public WorldPosition Position;
        /// <summary>0..1: 1 = in the open, 0.3 = inside a building, 0.1 = underground/in a vault.</summary>
        public float Openness;
        public CharacterPowers Powers;
    }

    public enum ExposureResult
    {
        None,
        ExposedNoEffect,
        GainedFirstPower,
        GainedAdditionalPower,
    }

    public sealed class AnomalySettings
    {
        /// <summary>Base chance per game day that an anomaly occurs somewhere in the city.</summary>
        public double DailyChance = 1.0 / 45.0;
        public float FrequencyMultiplier = 1f;
        public float ManifestationMultiplier = 1f;
        /// <summary>Manifestation probability for an unshielded person at the epicentre of an intensity-1 event.</summary>
        public double BaseManifestation = 0.45;
        /// <summary>
        /// Chance that an already-powered person develops another ability, relative to first manifestation.
        /// 0.002 × 0.45 ≈ 1 in 1,100 at the epicentre of a maximal event — and most exposures are far weaker.
        /// </summary>
        public double SecondPowerFactor = 0.002;
        /// <summary>Additional multiplier for a third power (another ~1 in 100 on top).</summary>
        public double ThirdPowerFactor = 0.01;
        public float MultiplePowerMultiplier = 1f;
        public int MaxPowers = 3;
        public int MinIncubationDays = 1;
        public int MaxIncubationDays = 12;
    }

    /// <summary>
    /// Generates anomaly events and resolves exposure → latent power. Powers are never picked from
    /// a menu; they arrive through the world (GDD §39–45).
    /// </summary>
    public sealed class AnomalySystem
    {
        private readonly ulong _seed;
        private readonly PowerGenerator _generator;
        private readonly List<AnomalyCause> _causes;
        public readonly AnomalySettings Settings;

        public event Action<AnomalyEvent> EventOccurred;
        public event Action<EntityId, PowerInstance, bool> PowerAcquired;

        public AnomalySystem(ulong seed, PowerGenerator generator, IEnumerable<AnomalyCause> causes, AnomalySettings settings = null)
        {
            _seed = seed;
            _generator = generator;
            _causes = new List<AnomalyCause>(causes);
            _causes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            Settings = settings ?? new AnomalySettings();
        }

        /// <summary>Daily roll. Returns the event (not yet applied to anyone) or null.</summary>
        public AnomalyEvent RollDaily(long dayIndex, IReadOnlyList<(EntityId id, string type, WorldPosition center, float radius)> districts, IdAllocator ids)
        {
            var rng = DeterministicRandom.For(_seed, 0xA40A1, (ulong)dayIndex);
            if (districts.Count == 0 || _causes.Count == 0) return null;
            if (!rng.Chance(Settings.DailyChance * Settings.FrequencyMultiplier)) return null;
            var district = districts[rng.NextInt(0, districts.Count)];
            var hour = rng.NextInt(0, 24);
            var minute = rng.NextInt(0, 60);
            return Create(district.id, district.type, district.center, district.radius, new GameDateTime(dayIndex * GameDateTime.SecondsPerDay + hour * 3600 + minute * 60), rng, ids, null);
        }

        /// <summary>Creates an event at a specific place (debug tools, story scripting).</summary>
        public AnomalyEvent Create(EntityId district, string districtType, WorldPosition center, float spread, GameDateTime when,
            DeterministicRandom rng, IdAllocator ids, string forcedCauseId)
        {
            var eligible = new List<AnomalyCause>();
            var weights = new List<double>();
            foreach (var c in _causes)
            {
                if (forcedCauseId != null && c.Id != forcedCauseId) continue;
                if (forcedCauseId == null && c.DistrictTypes.Count > 0 && !c.DistrictTypes.Contains(districtType)) continue;
                eligible.Add(c);
                weights.Add(c.Weight);
            }
            var index = rng.PickWeighted(weights);
            if (index < 0) return null;
            var cause = eligible[index];
            var intensity = cause.MinIntensity + (cause.MaxIntensity - cause.MinIntensity) * (float)Math.Pow(rng.NextDouble(), 2.2);
            var angle = rng.NextDouble() * Math.PI * 2;
            var dist = rng.NextDouble() * spread * 0.7;
            var evt = new AnomalyEvent
            {
                Id = ids.Next(EntityKind.AnomalyEvent),
                OccurredAt = when,
                District = district,
                Position = new WorldPosition(center.X + (float)(Math.Cos(angle) * dist), center.Y, center.Z + (float)(Math.Sin(angle) * dist)),
                Radius = 25f + intensity * 90f,
                CauseId = cause.Id,
                Description = cause.Description,
                Signature = new AnomalySignature { Domains = new List<DomainAffinity>(cause.Domains), Elements = new List<PowerElement>(cause.Elements), Intensity = intensity },
                Phenomena = new List<string>(cause.Phenomena),
            };
            EventOccurred?.Invoke(evt);
            return evt;
        }

        /// <summary>Exposure dose at a position: falls off with distance squared, reduced by shelter.</summary>
        public static float Dose(AnomalyEvent evt, WorldPosition position, float openness)
        {
            var d = WorldPosition.DistanceXZ(evt.Position, position);
            if (d >= evt.Radius) return 0f;
            var falloff = 1f - d / evt.Radius;
            return evt.Signature.Intensity * falloff * falloff * Math.Max(0f, Math.Min(1f, openness));
        }

        public ExposureResult Expose(AnomalyEvent evt, ExposureCandidate candidate, long dayIndex)
        {
            var dose = Dose(evt, candidate.Position, candidate.Openness);
            if (dose <= 0f) return ExposureResult.None;
            evt.Exposed.Add(candidate.Character);
            var powers = candidate.Powers;
            powers.Exposures++;

            var rng = DeterministicRandom.For(_seed, evt.Id.Value, candidate.Character.Value, 0xE0);
            var existing = powers.Powers.Count;
            if (existing >= Settings.MaxPowers) return ExposureResult.ExposedNoEffect;

            var chance = Settings.BaseManifestation * dose * Settings.ManifestationMultiplier;
            if (existing >= 1) chance *= Settings.SecondPowerFactor * Settings.MultiplePowerMultiplier;
            if (existing >= 2) chance *= Settings.ThirdPowerFactor;
            if (!rng.Chance(Math.Min(0.95, chance))) return ExposureResult.ExposedNoEffect;

            var exclude = new List<string>();
            foreach (var p in powers.Powers) exclude.Add(p.Definition.ArchetypeId);
            var def = _generator.Generate(evt.Signature, rng, exclude);
            if (def == null) return ExposureResult.ExposedNoEffect;
            var instance = new PowerInstance
            {
                Definition = def,
                Stage = PowerStage.Latent,
                AcquiredDay = dayIndex,
                StageChangedDay = dayIndex,
                ManifestDay = dayIndex + rng.NextInt(Settings.MinIncubationDays, Settings.MaxIncubationDays + 1),
                SourceEvent = evt.Id,
            };
            powers.Powers.Add(instance);
            PowerAcquired?.Invoke(candidate.Character, instance, existing > 0);
            return existing > 0 ? ExposureResult.GainedAdditionalPower : ExposureResult.GainedFirstPower;
        }
    }
}
