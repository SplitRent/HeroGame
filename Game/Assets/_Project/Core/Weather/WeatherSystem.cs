using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Weather
{
    public enum WeatherKind
    {
        Clear,
        PartlyCloudy,
        Overcast,
        Fog,
        LightRain,
        HeavyRain,
        Thunderstorm,
        TropicalStorm,
        Hurricane,
    }

    [Serializable]
    public struct WeatherState
    {
        public WeatherKind Kind;
        public float TemperatureC;
        public float Humidity;
        public float WindSpeedMs;
        /// <summary>mm/h.</summary>
        public float Precipitation;
        /// <summary>Metres.</summary>
        public float Visibility;
        /// <summary>0..1 accumulated standing water (drains over time).</summary>
        public float FloodLevel;
    }

    /// <summary>Gameplay-facing effects derived from weather (GDD §37). Read by traffic, NPCs, businesses, emergency services.</summary>
    public struct WeatherEffects
    {
        /// <summary>Tyre grip multiplier (1 = dry).</summary>
        public float Traction;
        public float VisibilityMetres;
        /// <summary>Multiplier on pedestrians outdoors.</summary>
        public float PedestrianDensity;
        public float TrafficSpeedMultiplier;
        /// <summary>0..1 severity used by businesses' weather sensitivity.</summary>
        public float BadWeather;
        /// <summary>Chance per hour of a local power outage.</summary>
        public float PowerOutageRiskPerHour;
        public bool EmergencyDeclared;
    }

    /// <summary>
    /// A tropical system tracked by the weather simulation (GDD §38). Announced days in advance so
    /// news, radio and the phone can warn players, and NPCs/businesses can prepare.
    /// </summary>
    [Serializable]
    public sealed class TropicalSystem
    {
        public string Name = "";
        public long AnnouncedHour;
        public long LandfallHour;
        public int DurationHours;
        /// <summary>1..5 intensity (below 1 means tropical storm).</summary>
        public float Category;
    }

    [Serializable]
    public sealed class WeatherSimState
    {
        public WeatherState Current;
        public long LastHourIndex = long.MinValue;
        public TropicalSystem ActiveSystem;
        public int StormsThisYear;
        public int Year;
        public string FixedWeather = "";
    }

    /// <summary>
    /// Hour-by-hour Gulf Coast weather: a seasonal Markov chain plus rare scripted-quality tropical
    /// systems in hurricane season. Deterministic per (seed, hour).
    /// </summary>
    public sealed class WeatherSystem
    {
        private static readonly string[] StormNames =
        {
            "Adaira", "Bexley", "Corvin", "Delphine", "Emory", "Fenwick", "Galena", "Hollis", "Isadora", "Jericho",
            "Kestrel", "Linnea", "Merrick", "Noelle", "Orrin", "Perpetua", "Quillon", "Rosalind", "Soren", "Tamsin",
        };

        private readonly ulong _seed;
        public readonly WeatherSimState State;

        public event Action<TropicalSystem> TropicalSystemAnnounced;
        public event Action<TropicalSystem> TropicalSystemLandfall;
        public event Action<WeatherKind, WeatherKind> WeatherChanged;

        public WeatherSystem(ulong seed, WeatherSimState state = null)
        {
            _seed = seed;
            State = state ?? new WeatherSimState();
        }

        /// <summary>Advances hour by hour up to <paramref name="now"/>.</summary>
        public void AdvanceTo(GameDateTime now)
        {
            var target = now.HourIndex;
            if (State.LastHourIndex == long.MinValue)
            {
                State.LastHourIndex = target - 1;
                State.Current = new WeatherState { Kind = WeatherKind.PartlyCloudy, TemperatureC = 24, Humidity = 0.7f, Visibility = 10000 };
            }
            // Very long gaps (server offline for months) only need the last few days simulated in detail.
            if (target - State.LastHourIndex > 24 * 14) State.LastHourIndex = target - 24 * 14;
            while (State.LastHourIndex < target)
            {
                State.LastHourIndex++;
                StepHour(new GameDateTime(State.LastHourIndex * GameDateTime.SecondsPerHour));
            }
        }

        private void StepHour(GameDateTime t)
        {
            var rng = DeterministicRandom.For(_seed, 0x3EA7, (ulong)t.HourIndex);
            if (t.Year != State.Year)
            {
                State.Year = t.Year;
                State.StormsThisYear = 0;
            }

            var previous = State.Current.Kind;
            UpdateTropical(t, rng);
            if (State.ActiveSystem != null && t.HourIndex == State.ActiveSystem.LandfallHour) TropicalSystemLandfall?.Invoke(State.ActiveSystem);

            WeatherKind next;
            if (!string.IsNullOrEmpty(State.FixedWeather) && Enum.TryParse(State.FixedWeather, out WeatherKind fixedKind))
            {
                next = fixedKind;
            }
            else if (State.ActiveSystem != null && t.HourIndex >= State.ActiveSystem.LandfallHour
                     && t.HourIndex < State.ActiveSystem.LandfallHour + State.ActiveSystem.DurationHours)
            {
                next = State.ActiveSystem.Category >= 1 ? WeatherKind.Hurricane : WeatherKind.TropicalStorm;
            }
            else if (t.HourIndex % 3 == 0 || previous == WeatherKind.Hurricane || previous == WeatherKind.TropicalStorm)
            {
                next = Transition(previous, t, rng);
            }
            else
            {
                next = previous;
            }

            ApplyKind(next, t, rng);
            if (next != previous) WeatherChanged?.Invoke(previous, next);
        }

        private void UpdateTropical(GameDateTime t, DeterministicRandom rng)
        {
            var sys = State.ActiveSystem;
            if (sys != null && t.HourIndex >= sys.LandfallHour + sys.DurationHours + 24) State.ActiveSystem = sys = null;
            if (sys != null || !t.IsHurricaneSeason) return;

            // Peak in Aug–Sep. Expected ~1.2 systems/season reaching this coast.
            var peak = t.Month == 8 || t.Month == 9 ? 2.2 : 0.6;
            var perHour = 1.2 / (183.0 * 24.0) * peak;
            if (!rng.Chance(perHour)) return;

            var category = (float)Math.Max(0, Math.Min(5, rng.NextGaussian() * 1.2 + 0.8));
            sys = new TropicalSystem
            {
                Name = StormNames[(State.StormsThisYear + (int)(StableHash.Mix((ulong)t.Year) % (ulong)StormNames.Length)) % StormNames.Length],
                AnnouncedHour = t.HourIndex,
                LandfallHour = t.HourIndex + rng.NextInt(48, 96),
                DurationHours = rng.NextInt(10, 30),
                Category = category,
            };
            State.StormsThisYear++;
            State.ActiveSystem = sys;
            TropicalSystemAnnounced?.Invoke(sys);
        }

        private WeatherKind Transition(WeatherKind from, GameDateTime t, DeterministicRandom rng)
        {
            var summer = t.Season == Season.Summer;
            var winter = t.Season == Season.Winter;
            var afternoon = t.Hour >= 13 && t.Hour <= 18;
            var morning = t.Hour >= 4 && t.Hour <= 9;
            // Weights: Clear, PartlyCloudy, Overcast, Fog, LightRain, HeavyRain, Thunderstorm
            var w = new List<double> { 3, 3, 1.5, 0, 0.6, 0.15, 0 };
            if (summer && afternoon) { w[6] = 1.2; w[5] = 0.4; }
            if (morning && (winter || t.Season == Season.Spring)) w[3] = 0.9;
            if (winter) { w[2] += 1.5; w[4] += 0.6; }
            switch (from)
            {
                case WeatherKind.Clear: w[0] += 6; break;
                case WeatherKind.PartlyCloudy: w[1] += 5; break;
                case WeatherKind.Overcast: w[2] += 4; w[4] += 1; break;
                case WeatherKind.Fog: w[3] += 2; w[1] += 1; break;
                case WeatherKind.LightRain: w[4] += 3; w[2] += 2; w[5] += 0.5; break;
                case WeatherKind.HeavyRain: w[5] += 1.5; w[4] += 2; w[6] += 0.5; break;
                case WeatherKind.Thunderstorm: w[6] += 1; w[5] += 1.5; w[4] += 1; break;
                default: w[2] += 4; w[4] += 3; break; // tail of a tropical system
            }
            return (WeatherKind)rng.PickWeighted(w);
        }

        private void ApplyKind(WeatherKind kind, GameDateTime t, DeterministicRandom rng)
        {
            var s = State.Current;
            s.Kind = kind;
            var monthlyMean = new[] { 12.5f, 14.5f, 18f, 21.5f, 25.5f, 28f, 29f, 29.2f, 27.3f, 23f, 17.5f, 13.5f }[t.Month - 1];
            var diurnal = (float)Math.Sin((t.HourFloat - 9f) / 24f * 2 * Math.PI) * 5f;
            var cloudCooling = kind >= WeatherKind.Overcast ? -2.5f : 0f;
            var target = monthlyMean + diurnal + cloudCooling + (float)rng.NextGaussian() * 0.6f;
            s.TemperatureC += (target - s.TemperatureC) * 0.5f;
            switch (kind)
            {
                case WeatherKind.Clear: s.Precipitation = 0; s.Visibility = 12000; s.WindSpeedMs = 3; s.Humidity = 0.65f; break;
                case WeatherKind.PartlyCloudy: s.Precipitation = 0; s.Visibility = 10000; s.WindSpeedMs = 4; s.Humidity = 0.7f; break;
                case WeatherKind.Overcast: s.Precipitation = 0; s.Visibility = 8000; s.WindSpeedMs = 5; s.Humidity = 0.8f; break;
                case WeatherKind.Fog: s.Precipitation = 0; s.Visibility = 150; s.WindSpeedMs = 1; s.Humidity = 0.98f; break;
                case WeatherKind.LightRain: s.Precipitation = 2; s.Visibility = 4000; s.WindSpeedMs = 5; s.Humidity = 0.92f; break;
                case WeatherKind.HeavyRain: s.Precipitation = 18; s.Visibility = 900; s.WindSpeedMs = 8; s.Humidity = 0.97f; break;
                case WeatherKind.Thunderstorm: s.Precipitation = 35; s.Visibility = 600; s.WindSpeedMs = 14; s.Humidity = 0.97f; break;
                case WeatherKind.TropicalStorm: s.Precipitation = 45; s.Visibility = 400; s.WindSpeedMs = 26; s.Humidity = 1f; break;
                case WeatherKind.Hurricane:
                    var cat = State.ActiveSystem != null ? State.ActiveSystem.Category : 1f;
                    s.Precipitation = 60 + cat * 10; s.Visibility = 200; s.WindSpeedMs = 33 + cat * 8; s.Humidity = 1f; break;
            }
            // Flooding: rain accumulates faster than the bayous can drain it.
            s.FloodLevel = Math.Max(0f, Math.Min(1f, s.FloodLevel + s.Precipitation / 400f - 0.015f));
            State.Current = s;
        }

        public WeatherEffects Effects
        {
            get
            {
                var s = State.Current;
                var wet = Math.Min(1f, s.Precipitation / 30f);
                var e = new WeatherEffects
                {
                    Traction = 1f - wet * 0.35f - s.FloodLevel * 0.3f,
                    VisibilityMetres = s.Visibility,
                    PedestrianDensity = Math.Max(0.05f, 1f - wet * 0.75f - (s.Kind == WeatherKind.Hurricane ? 0.9f : 0f)),
                    TrafficSpeedMultiplier = Math.Max(0.3f, 1f - wet * 0.3f - s.FloodLevel * 0.5f - (s.Visibility < 300 ? 0.25f : 0f)),
                    BadWeather = Math.Min(1f, wet * 0.7f + s.FloodLevel * 0.5f + (s.TemperatureC > 35 ? 0.2f : 0f)),
                    PowerOutageRiskPerHour = s.Kind == WeatherKind.Hurricane ? 0.25f : s.Kind == WeatherKind.TropicalStorm ? 0.08f : s.Kind == WeatherKind.Thunderstorm ? 0.01f : 0.0005f,
                    EmergencyDeclared = s.Kind == WeatherKind.Hurricane || s.FloodLevel > 0.6f,
                };
                return e;
            }
        }
    }
}
