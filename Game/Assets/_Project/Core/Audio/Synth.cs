using System;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Audio
{
    /// <summary>
    /// Deterministic procedural audio (placeholder until recorded/commissioned audio exists, docs/ASSET_TRACKER.md).
    /// Everything returns mono samples in [-1, 1]; loops are built to repeat seamlessly (whole cycles, faded seams).
    /// Pure math: no engine types, so it is tested headlessly and identical on every machine.
    /// </summary>
    public static class Synth
    {
        public const int SampleRate = 22050;
        private const double Tau = Math.PI * 2.0;

        // ------------------------------------------------------------------ music

        /// <summary>
        /// A looping 8-bar arrangement for a radio track: tempo, key, progression, bass, drums and a pentatonic lead
        /// all derive from the track id and the station's genre, so each song is distinct but always the same.
        /// </summary>
        public static float[] MusicLoop(string trackId, string genre)
        {
            var rng = DeterministicRandom.For(StableHash.Of(trackId ?? ""), 0x5A1);
            var g = (genre ?? "").ToLowerInvariant();
            var hip = g.Contains("hip") || g.Contains("pop");
            var country = g.Contains("country");
            var classical = g.Contains("news") || g.Contains("chamber");
            var bpm = hip ? 84 + rng.NextInt(0, 16) : country ? 100 + rng.NextInt(0, 24) : classical ? 66 + rng.NextInt(0, 12) : 88 + rng.NextInt(0, 30);
            var beat = 60.0 / bpm;
            var bars = 8;
            var total = (int)(bars * 4 * beat * SampleRate);
            var s = new float[total];
            var root = 45 + rng.NextInt(0, 10); // MIDI note of the key's bass root (A2..F#3)
            int[][] progressions =
            {
                new[] { 0, 7, 9, 5 },  // I V vi IV
                new[] { 0, 5, 7, 5 },  // I IV V IV
                new[] { 9, 5, 0, 7 },  // vi IV I V
                new[] { 0, 9, 5, 7 },  // I vi IV V
            };
            var prog = progressions[rng.NextInt(0, progressions.Length)];
            var scale = new[] { 0, 2, 4, 7, 9, 12, 14, 16 }; // major pentatonic over two octaves
            var leadNotes = new int[bars * 8];
            var step = rng.NextInt(0, 5);
            for (var i = 0; i < leadNotes.Length; i++)
            {
                step = Math.Max(0, Math.Min(scale.Length - 1, step + rng.NextInt(-2, 3)));
                leadNotes[i] = rng.Chance(classical ? 0.1 : 0.25) ? -1 : scale[step];
            }
            for (var i = 0; i < total; i++)
            {
                var t = i / (double)SampleRate;
                var beatPos = t / beat;
                var bar = (int)(beatPos / 4) % bars;
                var chordRoot = root + prog[bar % prog.Length];
                var inBeat = beatPos - Math.Floor(beatPos);
                double v = 0;

                // Pad: triad on the chord, soft.
                foreach (var interval in new[] { 12, 16, 19 })
                    v += 0.06 * Math.Sin(Tau * Hz(chordRoot + interval) * t);
                // Bass: root on beats, octave bounce for pop.
                var bassNote = chordRoot + (hip && (int)beatPos % 2 == 1 ? 12 : 0);
                v += 0.18 * Envelope(inBeat, 0.01, 0.8) * Saw(Hz(bassNote) * t);
                // Lead: eighth notes.
                var eighth = (int)(beatPos * 2) % leadNotes.Length;
                var n = leadNotes[eighth];
                if (n >= 0) v += 0.10 * Envelope(beatPos * 2 - Math.Floor(beatPos * 2), 0.02, 0.7) * Square(Hz(root + 24 + n) * t, 0.3);
                // Drums (not for the chamber ensemble).
                if (!classical)
                {
                    var beatInBar = (int)beatPos % 4;
                    if (beatInBar == 0 || beatInBar == 2 || hip && inBeat > 0.5 && beatInBar == 3) v += 0.35 * Kick(inBeat * beat);
                    if (beatInBar == 1 || beatInBar == 3) v += 0.18 * Noise(i) * Envelope(inBeat, 0.001, 0.15);
                    var half = beatPos * 2 - Math.Floor(beatPos * 2);
                    v += (country ? 0.03 : 0.05) * Noise(i * 7 + 3) * Envelope(half, 0.001, 0.05);
                }
                s[i] = (float)v;
            }
            Normalize(s, 0.8f);
            FadeSeam(s, 256);
            return s;
        }

        // ------------------------------------------------------------------ emergency services

        public enum SirenKind { Police, Ambulance, Fire }

        /// <summary>A two-second siren loop: police wail, ambulance hi-lo, fire-engine yelp with horn.</summary>
        public static float[] Siren(SirenKind kind)
        {
            var n = 2 * SampleRate;
            var s = new float[n];
            double phase = 0;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)SampleRate;
                double f;
                switch (kind)
                {
                    case SirenKind.Police: f = 950 + 450 * Math.Sin(Tau * t / 2.0); break;          // one slow wail per loop
                    case SirenKind.Ambulance: f = (int)(t * 2) % 2 == 0 ? 960 : 770; break;         // hi-lo
                    default: f = 700 + 500 * Math.Abs(Math.Sin(Tau * t * 2.0)); break;              // yelp
                }
                phase += Tau * f / SampleRate;
                var v = 0.6 * Square(phase / Tau, 0.5) * 0.5 + 0.4 * Math.Sin(phase);
                if (kind == SirenKind.Fire && t < 0.35) v += 0.4 * Saw(220 * t);
                s[i] = (float)v;
            }
            Normalize(s, 0.7f);
            FadeSeam(s, 128);
            return s;
        }

        // ------------------------------------------------------------------ vehicles

        /// <summary>
        /// A one-second engine loop at idle; the runtime raises AudioSource pitch with RPM. Harmonics of the firing
        /// frequency plus a little combustion noise; heavier vehicles are lower.
        /// </summary>
        public static float[] Engine(float massKg, int cylinders = 4)
        {
            var n = SampleRate;
            var s = new float[n];
            var firing = Math.Round(Math.Max(18.0, 38.0 - (massKg - 1000) / 400.0) * cylinders / 4.0); // whole Hz → seamless loop
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)SampleRate;
                var v = 0.5 * Saw(firing * t) + 0.25 * Math.Sin(Tau * firing * 2 * t) + 0.12 * Math.Sin(Tau * firing * 3 * t);
                v *= 0.75 + 0.25 * Math.Sin(Tau * firing / 2 * t);
                v += 0.08 * Noise(i);
                s[i] = (float)v;
            }
            Normalize(s, 0.6f);
            return s;
        }

        // ------------------------------------------------------------------ weather and ambience

        /// <summary>Rain: filtered noise with random drops; intensity 0..1 changes density, not length.</summary>
        public static float[] Rain(float intensity)
        {
            var n = 3 * SampleRate;
            var s = new float[n];
            var rng = DeterministicRandom.For(0xA1, (ulong)(intensity * 100));
            double lp = 0;
            for (var i = 0; i < n; i++)
            {
                lp += (Noise(i) - lp) * (0.25 + 0.5 * intensity);
                var v = lp * (0.3 + 0.5 * intensity);
                if (rng.Chance(0.0008 + 0.004 * intensity)) v += (rng.NextDouble() - 0.5) * 0.8;
                s[i] = (float)v;
            }
            Normalize(s, 0.6f);
            FadeSeam(s, 512);
            return s;
        }

        /// <summary>Wind: slowly swelling low-passed noise.</summary>
        public static float[] Wind()
        {
            var n = 4 * SampleRate;
            var s = new float[n];
            double lp = 0;
            for (var i = 0; i < n; i++)
            {
                lp += (Noise(i) - lp) * 0.02;
                var swell = 0.6 + 0.4 * Math.Sin(Tau * i / n); // one swell per loop
                s[i] = (float)(lp * swell);
            }
            Normalize(s, 0.6f);
            FadeSeam(s, 1024);
            return s;
        }

        /// <summary>A single thunder clap with a long rumble (one-shot).</summary>
        public static float[] Thunder(ulong seed)
        {
            var n = 4 * SampleRate;
            var s = new float[n];
            var rng = DeterministicRandom.For(0x7408, seed);
            double lp = 0;
            var crack = rng.NextDouble() * 0.2;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)SampleRate;
                lp += (Noise(i + (int)(seed % 1000)) - lp) * (t < crack + 0.1 ? 0.6 : 0.03);
                var env = t < crack ? t / Math.Max(0.01, crack) : Math.Exp(-(t - crack) * 1.3) * (0.8 + 0.2 * Math.Sin(t * 9));
                s[i] = (float)(lp * env);
            }
            Normalize(s, 0.9f);
            return s;
        }

        public enum AmbienceKind { City, Park, Industrial, Coast, Night }

        /// <summary>Background bed per kind of place: traffic hum, birds, machinery, surf, crickets.</summary>
        public static float[] Ambience(AmbienceKind kind)
        {
            var n = 6 * SampleRate;
            var s = new float[n];
            var rng = DeterministicRandom.For(0xA3B, (ulong)kind);
            double lp = 0;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)SampleRate;
                double v;
                switch (kind)
                {
                    case AmbienceKind.City:
                        lp += (Noise(i) - lp) * 0.05;
                        v = lp * 0.6 + 0.08 * Math.Sin(Tau * 55 * t) * (0.5 + 0.5 * Math.Sin(Tau * t / 3));
                        break;
                    case AmbienceKind.Park:
                        lp += (Noise(i) - lp) * 0.02;
                        v = lp * 0.25;
                        var chirp = (t * 1.7) % 1.0;
                        if (chirp < 0.12 && (int)(t * 1.7) % 3 != 1) v += 0.25 * Math.Sin(Tau * (3200 + 1800 * chirp / 0.12) * t) * Math.Sin(Math.PI * chirp / 0.12);
                        break;
                    case AmbienceKind.Industrial:
                        lp += (Noise(i) - lp) * 0.1;
                        v = lp * 0.4 + 0.2 * Square(60 * t, 0.5) * 0.3 + ((t % 1.5) < 0.05 ? 0.4 * Noise(i * 3) : 0);
                        break;
                    case AmbienceKind.Coast:
                        lp += (Noise(i) - lp) * 0.04;
                        v = lp * (0.3 + 0.7 * Math.Pow(Math.Sin(Math.PI * t / 6.0), 2)); // one wave per loop
                        break;
                    default:
                        lp += (Noise(i) - lp) * 0.02;
                        var cricket = (t * 3.0) % 1.0 < 0.2 ? Math.Sin(Tau * 4400 * t) * Math.Sin(Tau * 40 * t) : 0;
                        v = lp * 0.15 + 0.12 * cricket;
                        break;
                }
                s[i] = (float)v;
            }
            Normalize(s, 0.5f);
            FadeSeam(s, 1024);
            return s;
        }

        // ------------------------------------------------------------------ UI, phone, powers, voice

        public enum UiSound { Click, Confirm, Error, Notification, Cash }

        public static float[] Ui(UiSound kind)
        {
            switch (kind)
            {
                case UiSound.Click: return Tones(new[] { 1800.0 }, 0.03);
                case UiSound.Confirm: return Tones(new[] { 880.0, 1320.0 }, 0.07);
                case UiSound.Error: return Tones(new[] { 330.0, 247.0 }, 0.1);
                case UiSound.Notification: return Tones(new[] { 1175.0, 1568.0, 1175.0 }, 0.08);
                default: return Tones(new[] { 2093.0, 2637.0, 3136.0 }, 0.05);
            }
        }

        /// <summary>Rising charge tone for a power, coloured by its element name (hash-based timbre).</summary>
        public static float[] PowerCharge(string element)
        {
            var n = SampleRate; // 1 s
            var s = new float[n];
            var h = StableHash.Of(element ?? "") % 5;
            double phase = 0;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)n;
                var f = 180 + 700 * t * t + h * 40;
                phase += Tau * f / SampleRate;
                var v = h % 2 == 0 ? Math.Sin(phase) : Square(phase / Tau, 0.4);
                v += 0.3 * Noise(i) * t * (h == 3 ? 1.5 : 0.5);
                s[i] = (float)(v * t);
            }
            Normalize(s, 0.7f);
            return s;
        }

        public enum CombatSound { Punch, Blunt, Blade, Gunshot, Spray, Zap, Whoosh }

        /// <summary>Placeholder combat sounds (one-shots): a thud, a crack, a hiss, a buzz.</summary>
        public static float[] Combat(CombatSound kind)
        {
            var seconds = kind == CombatSound.Gunshot ? 1.2 : kind == CombatSound.Spray ? 0.9 : kind == CombatSound.Zap ? 0.7 : 0.35;
            var n = (int)(SampleRate * seconds);
            var s = new float[n];
            double lp = 0, hp = 0;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)SampleRate;
                var noise = Noise(i + (int)kind * 7919);
                double v;
                switch (kind)
                {
                    case CombatSound.Punch:
                        lp += (noise - lp) * 0.08;
                        v = (lp * 1.2 + Math.Sin(Tau * 85 * t) * 0.9) * Math.Exp(-t * 28);
                        break;
                    case CombatSound.Blunt:
                        lp += (noise - lp) * 0.12;
                        v = (lp + Math.Sin(Tau * 140 * t) * Math.Exp(-t * 12) * 0.8 + Math.Sin(Tau * 1100 * t) * Math.Exp(-t * 60) * 0.3) * Math.Exp(-t * 16);
                        break;
                    case CombatSound.Blade:
                        hp = noise - lp;
                        lp += (noise - lp) * 0.3;
                        v = hp * Math.Exp(-t * 22) * 0.8 + Math.Sin(Tau * (2400 - 1800 * t) * t) * Math.Exp(-t * 18) * 0.3;
                        break;
                    case CombatSound.Gunshot:
                        // A sharp crack, then the report rolling off the buildings.
                        lp += (noise - lp) * (t < 0.01 ? 0.9 : 0.05);
                        v = t < 0.004 ? noise : lp * (Math.Exp(-t * 9) + 0.25 * Math.Exp(-t * 2.2) * (0.6 + 0.4 * Math.Sin(t * 40)));
                        break;
                    case CombatSound.Spray:
                        hp = noise - lp;
                        lp += (noise - lp) * 0.5;
                        v = hp * (t < 0.05 ? t / 0.05 : 1.0) * Math.Exp(-Math.Max(0, t - 0.6) * 12) * 0.7;
                        break;
                    case CombatSound.Zap:
                        v = Math.Sign(Math.Sin(Tau * 55 * t)) * 0.35 * (0.6 + 0.4 * noise) * Math.Exp(-t * 3) + noise * 0.15 * Math.Exp(-t * 6);
                        break;
                    default:
                        lp += (noise - lp) * 0.04;
                        v = lp * Math.Sin(Math.PI * t / seconds);
                        break;
                }
                s[i] = (float)v;
            }
            Normalize(s, kind == CombatSound.Gunshot ? 0.95f : 0.8f);
            return s;
        }

        /// <summary>Impact burst: noise with an element-dependent colour and a pitched body.</summary>
        public static float[] PowerImpact(string element)
        {
            var n = SampleRate * 3 / 4;
            var s = new float[n];
            var h = StableHash.Of(element ?? "") % 5;
            double lp = 0;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)SampleRate;
                lp += (Noise(i) - lp) * (0.1 + h * 0.15);
                var body = Math.Sin(Tau * (90 + h * 60) * t) * Math.Exp(-t * 8);
                s[i] = (float)((lp * 0.8 + body * 0.6) * Math.Exp(-t * (4 + h)));
            }
            Normalize(s, 0.9f);
            return s;
        }

        /// <summary>
        /// Placeholder dialogue "voice": syllable blips whose pitch and rhythm are the speaker's own (seeded by who is
        /// speaking), roughly as long as the line of text. Subtitles carry the words.
        /// </summary>
        public static float[] Babble(ulong speakerSeed, string text)
        {
            var syllables = Math.Max(1, Math.Min(60, (text ?? "").Length / 3));
            var rng = DeterministicRandom.For(0xBAB1, speakerSeed);
            var basePitch = 110 + rng.NextDouble() * 160;
            var syllableSeconds = 0.09 + rng.NextDouble() * 0.05;
            var n = (int)(syllables * syllableSeconds * SampleRate);
            var s = new float[n];
            var textRng = DeterministicRandom.For(0xBAB2, speakerSeed, StableHash.Of(text ?? ""));
            var pitches = new double[syllables];
            for (var k = 0; k < syllables; k++) pitches[k] = basePitch * (0.85 + 0.35 * textRng.NextDouble());
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)SampleRate;
                var k = Math.Min(syllables - 1, (int)(t / syllableSeconds));
                var local = t / syllableSeconds - k;
                var f = pitches[k];
                var formant = Math.Sin(Tau * f * t) + 0.5 * Math.Sin(Tau * f * 2.7 * t) + 0.25 * Math.Sin(Tau * f * 4.1 * t);
                s[i] = (float)(formant * Math.Sin(Math.PI * local));
            }
            Normalize(s, 0.6f);
            return s;
        }

        // ------------------------------------------------------------------ helpers

        public static double Hz(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);

        private static double Saw(double cycles) => 2.0 * (cycles - Math.Floor(cycles + 0.5));

        private static double Square(double cycles, double duty) => cycles - Math.Floor(cycles) < duty ? 1.0 : -1.0;

        /// <summary>Stateless white noise from the sample index (repeatable, allocation free).</summary>
        private static double Noise(int i)
        {
            unchecked
            {
                var x = (uint)i * 747796405u + 2891336453u;
                x = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
                x = (x >> 22) ^ x;
                return x / (double)uint.MaxValue * 2.0 - 1.0;
            }
        }

        private static double Envelope(double position, double attack, double release)
        {
            if (position < attack) return position / attack;
            var decay = (position - attack) / Math.Max(1e-6, release);
            return Math.Max(0, 1 - decay);
        }

        private static double Kick(double secondsIntoBeat)
        {
            if (secondsIntoBeat > 0.25) return 0;
            var f = 50 + 90 * Math.Exp(-secondsIntoBeat * 30);
            return Math.Sin(Tau * f * secondsIntoBeat) * Math.Exp(-secondsIntoBeat * 14);
        }

        private static float[] Tones(double[] freqs, double each)
        {
            var per = (int)(each * SampleRate);
            var s = new float[per * freqs.Length];
            for (var k = 0; k < freqs.Length; k++)
                for (var i = 0; i < per; i++)
                {
                    var t = i / (double)SampleRate;
                    s[k * per + i] = (float)(Math.Sin(Tau * freqs[k] * t) * Math.Sin(Math.PI * i / per));
                }
            Normalize(s, 0.5f);
            return s;
        }

        public static void Normalize(float[] s, float peak)
        {
            var max = 0f;
            foreach (var v in s) max = Math.Max(max, Math.Abs(v));
            if (max <= 1e-9f) return;
            var k = peak / max;
            for (var i = 0; i < s.Length; i++) s[i] *= k;
        }

        /// <summary>
        /// Removes the click at a loop seam: a short fade out at the end and in at the start, so both sides meet at
        /// silence for a few milliseconds.
        /// </summary>
        public static void FadeSeam(float[] s, int samples)
        {
            samples = Math.Min(samples, s.Length / 4);
            for (var i = 0; i < samples; i++)
            {
                var w = i / (float)samples;
                s[i] *= w;
                s[s.Length - 1 - i] *= w;
            }
        }
    }
}
