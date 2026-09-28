using UnityEngine;

namespace HeroGame.Runtime.Audio
{
    public enum AudioChannel { Master, Music, Effects, Ambience, Voice, Interface }

    /// <summary>Per-player volume settings (PlayerPrefs; a convenience, safe to lose).</summary>
    public static class AudioVolumes
    {
        private static readonly float[] Values = { 1f, 0.7f, 0.9f, 0.8f, 0.9f, 0.7f };
        private static bool _loaded;

        public static float Get(AudioChannel c)
        {
            Load();
            return Values[(int)c];
        }

        /// <summary>Channel volume multiplied by master.</summary>
        public static float Effective(AudioChannel c) => c == AudioChannel.Master ? Get(c) : Get(c) * Get(AudioChannel.Master);

        public static void Set(AudioChannel c, float v)
        {
            Load();
            Values[(int)c] = Mathf.Clamp01(v);
            try { PlayerPrefs.SetFloat("audio." + c, Values[(int)c]); }
            catch (System.Exception) { /* preference only */ }
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            for (var i = 0; i < Values.Length; i++)
            {
                try { Values[i] = PlayerPrefs.GetFloat("audio." + (AudioChannel)i, Values[i]); }
                catch (System.Exception) { /* defaults */ }
            }
        }
    }
}
