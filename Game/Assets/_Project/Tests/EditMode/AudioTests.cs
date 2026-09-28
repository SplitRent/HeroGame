using System;
using System.Linq;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Audio;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Emergency;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Time;
    using HeroGame.Core.Weather;
    using HeroGame.Core.World;

    public class AudioTests
    {
        private static void AssertClean(float[] s, string what)
        {
            Assert.IsNotEmpty(s, what);
            Assert.IsFalse(s.Any(v => float.IsNaN(v) || float.IsInfinity(v)), what + ": finite");
            Assert.LessOrEqual(s.Max(v => Math.Abs(v)), 1f, what + ": no clipping");
            Assert.Greater(s.Max(v => Math.Abs(v)), 0.05f, what + ": audible");
        }

        private static int ZeroCrossings(float[] s, int from, int to)
        {
            var n = 0;
            for (var i = from + 1; i < to; i++) if (s[i - 1] < 0 != s[i] < 0) n++;
            return n;
        }

        [Test]
        public void EverySound_IsFinite_Audible_AndUnclipped()
        {
            AssertClean(Synth.MusicLoop("bg01", "Gulf soul and brass"), "music");
            foreach (Synth.SirenKind k in Enum.GetValues(typeof(Synth.SirenKind))) AssertClean(Synth.Siren(k), k.ToString());
            AssertClean(Synth.Engine(1400f), "engine");
            AssertClean(Synth.Rain(0.8f), "rain");
            AssertClean(Synth.Wind(), "wind");
            AssertClean(Synth.Thunder(3), "thunder");
            foreach (Synth.AmbienceKind k in Enum.GetValues(typeof(Synth.AmbienceKind))) AssertClean(Synth.Ambience(k), k.ToString());
            foreach (Synth.UiSound k in Enum.GetValues(typeof(Synth.UiSound))) AssertClean(Synth.Ui(k), k.ToString());
            AssertClean(Synth.PowerCharge("Fire"), "charge");
            AssertClean(Synth.PowerImpact("Electric"), "impact");
            AssertClean(Synth.Babble(42, "Lupe says the rent went up again."), "babble");
            foreach (Synth.CombatSound k in Enum.GetValues(typeof(Synth.CombatSound))) AssertClean(Synth.Combat(k), k.ToString());
        }

        [Test]
        public void Music_IsDeterministicPerTrack_DistinctBetweenTracks_AndLoopsWithoutAClick()
        {
            var a = Synth.MusicLoop("tc03", "Hip-hop and pop");
            CollectionAssert.AreEqual(a, Synth.MusicLoop("tc03", "Hip-hop and pop"));
            var other = Synth.MusicLoop("gc02", "Country");
            Assert.IsFalse(a.Length == other.Length && a.SequenceEqual(other), "different songs sound different");
            Assert.Less(Math.Abs(a[0]), 0.01f);
            Assert.Less(Math.Abs(a[a.Length - 1]), 0.01f, "the seam meets at silence");
            var seconds = a.Length / (double)Synth.SampleRate;
            Assert.That(seconds, Is.InRange(10.0, 40.0), "an eight-bar loop");
        }

        [Test]
        public void Sirens_SoundLikeTheirService()
        {
            var police = Synth.Siren(Synth.SirenKind.Police);
            var ambulance = Synth.Siren(Synth.SirenKind.Ambulance);
            // The police wail sweeps: more zero crossings at its peak than at its trough.
            var quarter = police.Length / 4;
            Assert.Greater(ZeroCrossings(police, 0, quarter), ZeroCrossings(police, 2 * quarter, 3 * quarter) * 1.2);
            // The ambulance hi-lo alternates between two pitches every half second.
            var half = Synth.SampleRate / 2;
            Assert.Greater(ZeroCrossings(ambulance, 0, half), ZeroCrossings(ambulance, half, 2 * half));
        }

        [Test]
        public void Babble_LengthFollowsTheLine_AndVoicesDiffer()
        {
            var shortLine = Synth.Babble(1, "Hey.");
            var longLine = Synth.Babble(1, "Hey, did you hear about the canal lights last night? Everybody on Magnolia saw them.");
            Assert.Greater(longLine.Length, shortLine.Length * 5);
            Assert.IsFalse(Synth.Babble(1, "Same words").SequenceEqual(Synth.Babble(2, "Same words")), "two speakers sound different");
        }

        [Test]
        public void Soundscape_FollowsTheWorld()
        {
            var world = WorldGenerator.Create("audio", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            var street = new WorldPosition(-200f, 0f, 40f);

            var dry = world.Weather.State.Current;
            dry.Kind = WeatherKind.Clear;
            dry.Precipitation = 0f;
            dry.WindSpeedMs = 2f;
            world.Weather.State.Current = dry;
            var calm = SoundscapeMixer.Mix(world, street, indoors: false);
            Assert.AreEqual(0f, calm.RainVolume);

            var storm = dry;
            storm.Kind = WeatherKind.Thunderstorm;
            storm.Precipitation = 12f;
            storm.WindSpeedMs = 20f;
            world.Weather.State.Current = storm;
            var wet = SoundscapeMixer.Mix(world, street, indoors: false);
            Assert.AreEqual(1f, wet.RainVolume);
            Assert.Greater(wet.WindVolume, calm.WindVolume);
            Assert.Greater(wet.ThunderPerSecond, 0f);
            Assert.Less(SoundscapeMixer.Mix(world, street, indoors: true).RainVolume, wet.RainVolume, "walls muffle the rain");

            // Night: the city bed fades and the night bed comes up.
            world.Clock.Now = GameDateTime.FromCalendar(2030, 5, 7, 23);
            var night = SoundscapeMixer.Mix(world, street, indoors: false);
            world.Clock.Now = GameDateTime.FromCalendar(2030, 5, 7, 13);
            var noon = SoundscapeMixer.Mix(world, street, indoors: false);
            Assert.Greater(night.NightVolume, noon.NightVolume);
            Assert.Less(night.BedVolume, noon.BedVolume);

            // A real ambulance on a real call nearby is heard; the same unit parked at the station is not.
            Assert.IsEmpty(noon.Sirens);
            var incident = world.Dispatch.Report(EmergencyKind.Medical, street, 1, "test", severity: 0.5f);
            var unit = world.Emergency.Units.First(u => u.Incident == incident.Id);
            world.Clock.Now = new GameDateTime((unit.DepartSecond + unit.ArriveSecond) / 2);
            var heard = SoundscapeMixer.Mix(world, unit.PositionAt(world.Clock.Now.TotalSeconds), indoors: false);
            Assert.IsNotEmpty(heard.Sirens);
            Assert.AreEqual(Synth.SirenKind.Ambulance, heard.Sirens[0].Kind);
        }
    }
}
