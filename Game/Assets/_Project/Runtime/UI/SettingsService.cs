using System;
using HeroGame.Core.Presentation;
using HeroGame.Persistence.Settings;
using HeroGame.Runtime.Audio;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// The player's settings for this machine (settings.json in the persistent data folder) and applying them to the
    /// engine: audio channels, quality, frame pacing. Camera, subtitles and notifications read <see cref="Current"/>.
    /// </summary>
    public static class SettingsService
    {
        private static GameSettings _current;
        private static SettingsStore _store;

        public static event Action<GameSettings> Changed;

        public static GameSettings Current
        {
            get
            {
                if (_current == null)
                {
                    _store = new SettingsStore(Application.persistentDataPath);
                    _current = _store.Load();
                    ApplyToEngine(_current);
                }
                return _current;
            }
        }

        /// <summary>Validates, applies and saves the current settings.</summary>
        public static void Commit()
        {
            var s = Current.Clamp();
            ApplyToEngine(s);
            try { _store.Save(s); }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { Debug.LogWarning("[Settings] Could not save: " + e.Message); }
            Changed?.Invoke(s);
        }

        /// <summary>Applies the current settings without writing the file (live slider changes); call Commit when done.</summary>
        public static void Apply()
        {
            var s = Current.Clamp();
            ApplyToEngine(s);
            Changed?.Invoke(s);
        }

        public static void ResetToDefaults()
        {
            _current = new GameSettings();
            Commit();
        }

        private static void ApplyToEngine(GameSettings s)
        {
            AudioVolumes.Set(AudioChannel.Master, s.MasterVolume);
            AudioVolumes.Set(AudioChannel.Music, s.MusicVolume);
            AudioVolumes.Set(AudioChannel.Effects, s.EffectsVolume);
            AudioVolumes.Set(AudioChannel.Ambience, s.AmbienceVolume);
            AudioVolumes.Set(AudioChannel.Voice, s.VoiceVolume);
            AudioVolumes.Set(AudioChannel.Interface, s.InterfaceVolume);
            var levels = QualitySettings.names.Length;
            if (levels > 0) QualitySettings.SetQualityLevel(Mathf.Clamp(Mathf.RoundToInt((int)s.Quality / 3f * (levels - 1)), 0, levels - 1), true);
            QualitySettings.vSyncCount = s.VSync ? 1 : 0;
            Application.targetFrameRate = s.VSync ? -1 : s.TargetFps == 0 ? -1 : s.TargetFps;
        }
    }
}
