using UnityEngine;

namespace HeroGame.Runtime.UI
{
    using HeroGame.Core.Presentation;
    using HeroGame.Runtime.Player;

    /// <summary>
    /// Pause/settings menu (Esc / gamepad Start): audio, controls, display, accessibility. Changes apply live and are
    /// saved when the menu closes. Placeholder IMGUI (docs/ASSET_TRACKER.md) until the UI Toolkit skin.
    /// </summary>
    [DefaultExecutionOrder(1000)] // after panels that may consume Esc this frame
    public sealed class SettingsPanel : MonoBehaviour
    {
        private enum Tab { Audio, Controls, Display, Accessibility }

        private IPlayerInputSource _input;
        private bool _open;
        private Tab _tab;
        private Vector2 _scroll;

        public bool IsOpen => _open;

        private void Update()
        {
            if (_input == null) _input = PlayerInputRegistry.Create();
            if (GameUi.Active) return; // the UI Toolkit pause screen handles Esc
            if (!_input.Read().PausePressed) return;
            // Another panel (phone, build mode, console) owns Esc while it is open.
            if (!_open && (UiFocus.Active || UiFocus.EscapeConsumedThisFrame)) return;
            Toggle();
        }

        private void Toggle()
        {
            _open = !_open;
            if (_open) UiFocus.Acquire();
            else
            {
                UiFocus.Release();
                SettingsService.Commit();
            }
        }

        private void OnGUI()
        {
            if (!_open) return;
            var s = SettingsService.Current;
            var w = Mathf.Min(560f, Screen.width - 40f);
            GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, 60f, w, Screen.height - 120f), GUI.skin.box);
            GUILayout.Label("Paused · Settings");
            GUILayout.BeginHorizontal();
            foreach (Tab t in System.Enum.GetValues(typeof(Tab)))
                if (GUILayout.Toggle(_tab == t, t.ToString(), GUI.skin.button)) _tab = t;
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll);
            var before = JsonLike(s);
            switch (_tab)
            {
                case Tab.Audio:
                    s.MasterVolume = Slider("Master", s.MasterVolume, 0f, 1f, true);
                    s.MusicVolume = Slider("Music & radio", s.MusicVolume, 0f, 1f, true);
                    s.EffectsVolume = Slider("Effects", s.EffectsVolume, 0f, 1f, true);
                    s.AmbienceVolume = Slider("Ambience", s.AmbienceVolume, 0f, 1f, true);
                    s.VoiceVolume = Slider("Voices", s.VoiceVolume, 0f, 1f, true);
                    s.InterfaceVolume = Slider("Interface", s.InterfaceVolume, 0f, 1f, true);
                    break;
                case Tab.Controls:
                    s.MouseSensitivity = Slider("Mouse sensitivity", s.MouseSensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity, false);
                    s.GamepadSensitivity = Slider("Gamepad sensitivity", s.GamepadSensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity, false);
                    s.InvertY = GUILayout.Toggle(s.InvertY, " Invert vertical look");
                    GUILayout.Space(8);
                    GUILayout.Label("Move WASD · Look mouse · Interact E · Phone ↑ · Vehicle F · Build B · Powers 1–4 + hold Q · Console `");
                    break;
                case Tab.Display:
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Quality", GUILayout.Width(170));
                    foreach (QualityPreset q in System.Enum.GetValues(typeof(QualityPreset)))
                        if (GUILayout.Toggle(s.Quality == q, q.ToString(), GUI.skin.button) && s.Quality != q) s.ApplyPreset(q);
                    GUILayout.EndHorizontal();
                    s.VSync = GUILayout.Toggle(s.VSync, " VSync");
                    if (!s.VSync) s.TargetFps = Mathf.RoundToInt(Slider("Frame cap", s.TargetFps, GameSettings.MinFps, GameSettings.MaxFps, false));
                    s.FieldOfView = Slider("Field of view", s.FieldOfView, GameSettings.MinFov, GameSettings.MaxFov, false);
                    s.UiScale = Slider("Interface scale", s.UiScale, GameSettings.MinUiScale, GameSettings.MaxUiScale, false);
                    s.ShowFps = GUILayout.Toggle(s.ShowFps, " Show frame rate");
                    break;
                case Tab.Accessibility:
                    s.Subtitles = GUILayout.Toggle(s.Subtitles, " Subtitles");
                    s.SubtitleScale = Slider("Subtitle size", s.SubtitleScale, 0.75f, 2.5f, false);
                    s.CameraShake = Slider("Camera shake", s.CameraShake, 0f, 1f, true);
                    s.MotionBlur = GUILayout.Toggle(s.MotionBlur, " Motion blur");
                    s.ShapeCodedMarkers = GUILayout.Toggle(s.ShapeCodedMarkers, " Shape-coded markers (colour-vision friendly)");
                    s.Clock24h = GUILayout.Toggle(s.Clock24h, " 24-hour clock");
                    s.Units = GUILayout.Toggle(s.Units == UnitSystem.Metric, " Metric units") ? UnitSystem.Metric : UnitSystem.Imperial;
                    s.NotificationSeconds = Slider("Notifications stay for (s)", s.NotificationSeconds, 2f, 30f, false);
                    break;
            }
            GUILayout.EndScrollView();
            if (JsonLike(s) != before) SettingsService.Commit();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to defaults")) SettingsService.ResetToDefaults();
            if (GUILayout.Button("Resume")) Toggle();
            if (GUILayout.Button("Quit to desktop")) Application.Quit();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static string JsonLike(GameSettings s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in s.ToMap()) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            return sb.ToString();
        }

        private static float Slider(string label, float value, float min, float max, bool percent)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(170));
            value = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.Label(percent ? Mathf.RoundToInt(value * 100f) + "%" : value.ToString("0.##"), GUILayout.Width(50));
            GUILayout.EndHorizontal();
            return value;
        }
    }
}
