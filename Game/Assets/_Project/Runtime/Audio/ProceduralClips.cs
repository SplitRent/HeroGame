using System;
using System.Collections.Generic;
using HeroGame.Core.Audio;
using UnityEngine;

namespace HeroGame.Runtime.Audio
{
    /// <summary>Turns <see cref="Synth"/> sample arrays into cached AudioClips (placeholder audio, ASSET_TRACKER).</summary>
    public static class ProceduralClips
    {
        private static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>();

        public static AudioClip Get(string key, Func<float[]> make)
        {
            if (Cache.TryGetValue(key, out var clip) && clip != null) return clip;
            var samples = make();
            clip = AudioClip.Create(key, Math.Max(1, samples.Length), 1, Synth.SampleRate, false);
            clip.SetData(samples, 0);
            Cache[key] = clip;
            return clip;
        }

        /// <summary>A recorded clip from Resources/Audio/&lt;name&gt; when one exists, otherwise the synthesized placeholder.</summary>
        public static AudioClip Prefer(string resourceName, string key, Func<float[]> make)
        {
            var real = Resources.Load<AudioClip>("Audio/" + resourceName);
            return real != null ? real : Get(key, make);
        }
    }
}
