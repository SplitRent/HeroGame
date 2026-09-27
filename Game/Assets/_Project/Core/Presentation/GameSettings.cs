using System;
using System.Collections.Generic;
using System.Globalization;

namespace HeroGame.Core.Presentation
{
    public enum QualityPreset { Low, Medium, High, Ultra }
    public enum UnitSystem { Imperial, Metric }

    /// <summary>
    /// Player settings (GDD Phase 24): audio, controls, display and accessibility. Plain data with validation; the
    /// persistence layer stores it as a flat key/value map so unknown or damaged entries fall back to defaults instead
    /// of losing the whole file. Presentation-only: nothing here affects the simulation.
    /// </summary>
    public sealed class GameSettings
    {
        // Audio (0..1)
        public float MasterVolume = 1f;
        public float MusicVolume = 0.7f;
        public float EffectsVolume = 0.9f;
        public float AmbienceVolume = 0.8f;
        public float VoiceVolume = 0.9f;
        public float InterfaceVolume = 0.7f;

        // Controls
        public float MouseSensitivity = 1f;
        public bool InvertY;
        public float GamepadSensitivity = 1f;

        // Display
        public QualityPreset Quality = QualityPreset.High;
        public bool VSync = true;
        /// <summary>Frame cap when VSync is off (0 = uncapped).</summary>
        public int TargetFps = 60;
        public float FieldOfView = 70f;
        public float UiScale = 1f;
        public bool ShowFps;

        // Accessibility and comfort
        public bool Subtitles = true;
        public float SubtitleScale = 1f;
        public float CameraShake = 1f;
        public bool MotionBlur = true;
        /// <summary>Adds shapes to colour-coded markers (wanted level, map pins) for colour-vision deficiency.</summary>
        public bool ShapeCodedMarkers;
        public bool Clock24h;
        public UnitSystem Units = UnitSystem.Imperial;
        /// <summary>How long notifications stay on screen, in seconds.</summary>
        public float NotificationSeconds = 5f;

        public const float MinSensitivity = 0.1f, MaxSensitivity = 5f;
        public const float MinFov = 55f, MaxFov = 100f;
        public const float MinUiScale = 0.75f, MaxUiScale = 2f;
        public const int MinFps = 30, MaxFps = 360;

        /// <summary>Forces every value into its valid range (NaN becomes the default).</summary>
        public GameSettings Clamp()
        {
            var d = new GameSettings();
            MasterVolume = C(MasterVolume, 0f, 1f, d.MasterVolume);
            MusicVolume = C(MusicVolume, 0f, 1f, d.MusicVolume);
            EffectsVolume = C(EffectsVolume, 0f, 1f, d.EffectsVolume);
            AmbienceVolume = C(AmbienceVolume, 0f, 1f, d.AmbienceVolume);
            VoiceVolume = C(VoiceVolume, 0f, 1f, d.VoiceVolume);
            InterfaceVolume = C(InterfaceVolume, 0f, 1f, d.InterfaceVolume);
            MouseSensitivity = C(MouseSensitivity, MinSensitivity, MaxSensitivity, d.MouseSensitivity);
            GamepadSensitivity = C(GamepadSensitivity, MinSensitivity, MaxSensitivity, d.GamepadSensitivity);
            if (!Enum.IsDefined(typeof(QualityPreset), Quality)) Quality = d.Quality;
            if (!Enum.IsDefined(typeof(UnitSystem), Units)) Units = d.Units;
            TargetFps = TargetFps == 0 ? 0 : Math.Max(MinFps, Math.Min(MaxFps, TargetFps));
            FieldOfView = C(FieldOfView, MinFov, MaxFov, d.FieldOfView);
            UiScale = C(UiScale, MinUiScale, MaxUiScale, d.UiScale);
            SubtitleScale = C(SubtitleScale, 0.75f, 2.5f, d.SubtitleScale);
            CameraShake = C(CameraShake, 0f, 1f, d.CameraShake);
            NotificationSeconds = C(NotificationSeconds, 2f, 30f, d.NotificationSeconds);
            return this;
        }

        private static float C(float v, float min, float max, float fallback) => float.IsNaN(v) || float.IsInfinity(v) ? fallback : Math.Max(min, Math.Min(max, v));

        /// <summary>Distance for display in the player's units ("0.4 mi" / "650 m").</summary>
        public string FormatDistance(float metres)
        {
            if (Units == UnitSystem.Metric) return metres < 1000f ? metres.ToString("0", CultureInfo.InvariantCulture) + " m" : (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
            var miles = metres / 1609.344f;
            return miles < 0.1f ? (metres * 3.28084f).ToString("0", CultureInfo.InvariantCulture) + " ft" : miles.ToString("0.0", CultureInfo.InvariantCulture) + " mi";
        }

        /// <summary>Temperature in the player's units.</summary>
        public string FormatTemperature(float celsius) =>
            Units == UnitSystem.Metric ? celsius.ToString("0", CultureInfo.InvariantCulture) + "°C" : (celsius * 9f / 5f + 32f).ToString("0", CultureInfo.InvariantCulture) + "°F";

        public string FormatTime(int hour, int minute)
        {
            if (Clock24h) return hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
            var h = hour % 12 == 0 ? 12 : hour % 12;
            return h.ToString(CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture) + (hour < 12 ? " AM" : " PM");
        }

        // ---------------------------------------------------------------- flat key/value form

        public Dictionary<string, string> ToMap()
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in Fields) m[f.Key] = f.Get(this);
            return m;
        }

        /// <summary>Reads what it can; missing, unknown or malformed entries keep their defaults. Always returns clamped settings.</summary>
        public static GameSettings FromMap(IReadOnlyDictionary<string, string> map)
        {
            var s = new GameSettings();
            if (map == null) return s;
            foreach (var f in Fields)
                if (map.TryGetValue(f.Key, out var v) && v != null) f.Set(s, v);
            return s.Clamp();
        }

        private sealed class Field
        {
            public string Key;
            public Func<GameSettings, string> Get;
            public Action<GameSettings, string> Set;
        }

        private static Field F(string key, Func<GameSettings, float> get, Action<GameSettings, float> set) => new Field
        {
            Key = key, Get = s => get(s).ToString("R", CultureInfo.InvariantCulture),
            Set = (s, v) => { if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) set(s, f); },
        };

        private static Field B(string key, Func<GameSettings, bool> get, Action<GameSettings, bool> set) => new Field
        {
            Key = key, Get = s => get(s) ? "true" : "false",
            Set = (s, v) => { if (bool.TryParse(v, out var b)) set(s, b); },
        };

        private static Field I(string key, Func<GameSettings, int> get, Action<GameSettings, int> set) => new Field
        {
            Key = key, Get = s => get(s).ToString(CultureInfo.InvariantCulture),
            Set = (s, v) => { if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) set(s, i); },
        };

        private static Field E<T>(string key, Func<GameSettings, T> get, Action<GameSettings, T> set) where T : struct => new Field
        {
            Key = key, Get = s => get(s).ToString(),
            Set = (s, v) => { if (Enum.TryParse<T>(v, true, out var e) && Enum.IsDefined(typeof(T), e)) set(s, e); },
        };

        private static readonly Field[] Fields =
        {
            F("audio.master", s => s.MasterVolume, (s, v) => s.MasterVolume = v),
            F("audio.music", s => s.MusicVolume, (s, v) => s.MusicVolume = v),
            F("audio.effects", s => s.EffectsVolume, (s, v) => s.EffectsVolume = v),
            F("audio.ambience", s => s.AmbienceVolume, (s, v) => s.AmbienceVolume = v),
            F("audio.voice", s => s.VoiceVolume, (s, v) => s.VoiceVolume = v),
            F("audio.interface", s => s.InterfaceVolume, (s, v) => s.InterfaceVolume = v),
            F("controls.mouse_sensitivity", s => s.MouseSensitivity, (s, v) => s.MouseSensitivity = v),
            B("controls.invert_y", s => s.InvertY, (s, v) => s.InvertY = v),
            F("controls.gamepad_sensitivity", s => s.GamepadSensitivity, (s, v) => s.GamepadSensitivity = v),
            E<QualityPreset>("display.quality", s => s.Quality, (s, v) => s.Quality = v),
            B("display.vsync", s => s.VSync, (s, v) => s.VSync = v),
            I("display.target_fps", s => s.TargetFps, (s, v) => s.TargetFps = v),
            F("display.fov", s => s.FieldOfView, (s, v) => s.FieldOfView = v),
            F("display.ui_scale", s => s.UiScale, (s, v) => s.UiScale = v),
            B("display.show_fps", s => s.ShowFps, (s, v) => s.ShowFps = v),
            B("access.subtitles", s => s.Subtitles, (s, v) => s.Subtitles = v),
            F("access.subtitle_scale", s => s.SubtitleScale, (s, v) => s.SubtitleScale = v),
            F("access.camera_shake", s => s.CameraShake, (s, v) => s.CameraShake = v),
            B("access.motion_blur", s => s.MotionBlur, (s, v) => s.MotionBlur = v),
            B("access.shape_markers", s => s.ShapeCodedMarkers, (s, v) => s.ShapeCodedMarkers = v),
            B("access.clock_24h", s => s.Clock24h, (s, v) => s.Clock24h = v),
            E<UnitSystem>("access.units", s => s.Units, (s, v) => s.Units = v),
            F("access.notification_seconds", s => s.NotificationSeconds, (s, v) => s.NotificationSeconds = v),
        };
    }
}
