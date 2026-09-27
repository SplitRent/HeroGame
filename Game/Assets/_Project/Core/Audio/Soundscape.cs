using System;
using System.Collections.Generic;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Simulation;
using HeroGame.Core.World;

namespace HeroGame.Core.Audio
{
    /// <summary>An audible siren: which kind, where, how loud at the listener.</summary>
    public struct SirenSource
    {
        public Synth.SirenKind Kind;
        public WorldPosition Position;
        public float Volume;
    }

    /// <summary>Target volumes (0..1) for each sound layer at one listener position and moment.</summary>
    public sealed class Soundscape
    {
        public Synth.AmbienceKind Bed;
        public float BedVolume;
        /// <summary>Night insects/quiet bed, cross-faded with the main bed.</summary>
        public float NightVolume;
        public float RainVolume;
        public float WindVolume;
        /// <summary>Chance per second of a thunder clap.</summary>
        public float ThunderPerSecond;
        /// <summary>Traffic and crowd intensity from real foot traffic and traffic demand.</summary>
        public float CrowdVolume;
        public readonly List<SirenSource> Sirens = new List<SirenSource>();
    }

    /// <summary>
    /// Decides what the world sounds like where the listener is (GDD Phase 23): the ambience of the district, weather
    /// from the simulation, crowds from real foot traffic and time of day, and the sirens of emergency units actually
    /// on calls nearby. Presentation just plays the layers at these volumes.
    /// </summary>
    public static class SoundscapeMixer
    {
        public const float SirenRange = 450f;

        public static Soundscape Mix(Simulation.World w, WorldPosition listener, bool indoors)
        {
            var mix = new Soundscape();
            var now = w.Clock.Now;
            var district = NearestDistrict(w, listener);
            mix.Bed = BedFor(w, district, listener);
            var hour = now.HourFloat;
            var night = hour >= 22f || hour < 5f;
            var dusk = hour >= 20f && hour < 22f ? (hour - 20f) / 2f : hour >= 5f && hour < 6.5f ? 1f - (hour - 5f) / 1.5f : night ? 1f : 0f;
            var inside = indoors ? 0.3f : 1f;
            mix.BedVolume = (0.55f * (1f - 0.6f * dusk)) * inside;
            mix.NightVolume = 0.35f * dusk * inside;

            var weather = w.Weather.State.Current;
            mix.RainVolume = Math.Min(1f, weather.Precipitation / 8f) * (indoors ? 0.35f : 1f);
            mix.WindVolume = Math.Min(1f, weather.WindSpeedMs / 30f) * (indoors ? 0.25f : 1f);
            mix.ThunderPerSecond = weather.Kind == Weather.WeatherKind.Thunderstorm ? 0.04f : weather.Kind == Weather.WeatherKind.Hurricane || weather.Kind == Weather.WeatherKind.TropicalStorm ? 0.02f : 0f;

            var foot = district != null ? district.FootTraffic : 1f;
            var demand = Traffic.TrafficModel.DemandAt(now);
            var blackout = district != null && w.Calendar.BlackoutAt(district.Id, now.TotalSeconds);
            mix.CrowdVolume = Math.Min(1f, 0.25f * foot * (0.3f + demand)) * inside * (blackout ? 0.5f : 1f) * (w.Weather.Effects.EmergencyDeclared ? 0.2f : 1f);

            foreach (var u in w.Emergency.Units)
            {
                if (u.Status != UnitStatus.EnRoute && u.Status != UnitStatus.Transporting) continue;
                var pos = u.PositionAt(now.TotalSeconds);
                var d = WorldPosition.DistanceXZ(pos, listener);
                if (d > SirenRange) continue;
                var kind = u.Service == EmergencyService.Police ? Synth.SirenKind.Police : u.Service == EmergencyService.Fire ? Synth.SirenKind.Fire : Synth.SirenKind.Ambulance;
                mix.Sirens.Add(new SirenSource { Kind = kind, Position = pos, Volume = (1f - d / SirenRange) * (indoors ? 0.5f : 1f) });
            }
            mix.Sirens.Sort((a, b) => b.Volume.CompareTo(a.Volume));
            if (mix.Sirens.Count > 3) mix.Sirens.RemoveRange(3, mix.Sirens.Count - 3);
            return mix;
        }

        private static Synth.AmbienceKind BedFor(Simulation.World w, District d, WorldPosition listener)
        {
            var near = new List<Place>();
            w.Geography.QueryRadius(listener, 60f, near);
            foreach (var p in near)
            {
                if (p.Kind == PlaceKind.Park && WorldPosition.DistanceXZ(p.Position, listener) < 60f) return Synth.AmbienceKind.Park;
                if ((p.Kind == PlaceKind.Beach || p.Kind == PlaceKind.Dock) && WorldPosition.DistanceXZ(p.Position, listener) < 60f) return Synth.AmbienceKind.Coast;
            }
            if (d == null) return Synth.AmbienceKind.City;
            switch (d.Type)
            {
                case DistrictType.Industrial:
                case DistrictType.Port: return Synth.AmbienceKind.Industrial;
                case DistrictType.Coastal: return Synth.AmbienceKind.Coast;
                case DistrictType.Rural: return Synth.AmbienceKind.Park;
                default: return Synth.AmbienceKind.City;
            }
        }

        private static District NearestDistrict(Simulation.World w, WorldPosition p)
        {
            District best = null;
            var bestD = float.MaxValue;
            foreach (var d in w.Geography.Districts)
            {
                var dist = WorldPosition.DistanceXZ(d.Center, p) / Math.Max(1f, d.Radius);
                if (dist < bestD || dist == bestD && best != null && d.Id.CompareTo(best.Id) < 0) { bestD = dist; best = d; }
            }
            return best;
        }
    }
}
