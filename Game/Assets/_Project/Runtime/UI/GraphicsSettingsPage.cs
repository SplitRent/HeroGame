using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeroGame.Runtime.UI
{
    using HeroGame.Core.Presentation;

    /// <summary>
    /// The Display and Graphics settings pages, shared by the pause screen and the main menu (same settings file,
    /// same controls). Picking a preset rewrites every graphics option; changing any single option turns the preset
    /// into Custom (or back into a preset when the values match one again). Changes apply live.
    /// </summary>
    public static class GraphicsSettingsPage
    {
        public static void Display(VisualElement into, string fieldClass, Action rebuild)
        {
            var s = SettingsService.Current;
            into.Add(Enum(fieldClass, "Window mode", s.Window, v => s.Window = v, new[] { "Fullscreen", "Borderless window", "Windowed" }));

            var sizes = new List<(int w, int h)> { (0, 0) };
            foreach (var r in UnityEngine.Screen.resolutions)
            {
                if (sizes.Contains((r.width, r.height))) continue;
                sizes.Add((r.width, r.height));
            }
            sizes.Sort((a, b) => a.w == 0 ? -1 : b.w == 0 ? 1 : (b.w * b.h).CompareTo(a.w * a.h));
            var options = new List<string>();
            foreach (var (w, h) in sizes) options.Add(w == 0 ? "Native" : w + " × " + h);
            var current = Math.Max(0, sizes.IndexOf((s.ResolutionWidth, s.ResolutionHeight)));
            var resolution = new DropdownField("Resolution", options, current);
            resolution.AddToClassList(fieldClass);
            resolution.RegisterValueChangedCallback(_ =>
            {
                var (w, h) = sizes[Math.Max(0, resolution.index)];
                s.ResolutionWidth = w;
                s.ResolutionHeight = h;
                SettingsService.Apply();
            });
            into.Add(resolution);

            into.Add(Check(fieldClass, "VSync", () => s.VSync, v => s.VSync = v, false));
            into.Add(Range(fieldClass, "Frame cap (VSync off)", GameSettings.MinFps, GameSettings.MaxFps, () => s.TargetFps, v => s.TargetFps = Mathf.RoundToInt(v), false));
            into.Add(Range(fieldClass, "Field of view", GameSettings.MinFov, GameSettings.MaxFov, () => s.FieldOfView, v => s.FieldOfView = v, false));
            into.Add(Range(fieldClass, "Brightness", -2f, 2f, () => s.Brightness, v => s.Brightness = v, false));
            into.Add(Range(fieldClass, "Interface scale", GameSettings.MinUiScale, GameSettings.MaxUiScale, () => s.UiScale, v => s.UiScale = v, false));
            into.Add(Check(fieldClass, "Show frame rate", () => s.ShowFps, v => s.ShowFps = v, false));
        }

        public static void Graphics(VisualElement into, string fieldClass, Action rebuild)
        {
            var s = SettingsService.Current;
            var presets = new List<string>(System.Enum.GetNames(typeof(QualityPreset)));
            var preset = new DropdownField("Preset", presets, (int)s.Quality);
            preset.AddToClassList(fieldClass);
            preset.RegisterValueChangedCallback(_ =>
            {
                var chosen = (QualityPreset)Math.Max(0, preset.index);
                if (chosen == s.Quality) return;
                s.ApplyPreset(chosen);
                SettingsService.Apply();
                rebuild?.Invoke(); // every option below changed
            });
            into.Add(preset);
            Note(into, "Presets set everything below. Change any option to make your own (Custom).");

            Section(into, "Image");
            into.Add(Enum(fieldClass, "Anti-aliasing", s.AntiAliasing, v => s.AntiAliasing = v, new[] { "Off", "FXAA (fast)", "SMAA (sharp)", "TAA (smoothest)" }, preset));
            into.Add(Enum(fieldClass, "Texture quality", s.Textures, v => s.Textures = v, new[] { "Low (quarter)", "Medium (half)", "High (full)" }, preset));
            into.Add(Check(fieldClass, "Anisotropic filtering", () => s.AnisotropicFiltering, v => s.AnisotropicFiltering = v, true, preset));
            into.Add(Range(fieldClass, "Level of detail", GameSettings.MinDetail, GameSettings.MaxDetail, () => s.DetailLevel, v => s.DetailLevel = v, true, preset));
            into.Add(Range(fieldClass, "View distance", GameSettings.MinViewDistance, GameSettings.MaxViewDistance, () => s.ViewDistance, v => s.ViewDistance = v, true, preset));

            Section(into, "Lighting and shadows");
            into.Add(Enum(fieldClass, "Shadow quality", s.Shadows, v => s.Shadows = v, new[] { "Off", "Low", "Medium", "High", "Ultra" }, preset));
            into.Add(Range(fieldClass, "Shadow distance (m)", GameSettings.MinShadowDistance, GameSettings.MaxShadowDistance, () => s.ShadowDistance, v => s.ShadowDistance = v, true, preset));
            into.Add(Check(fieldClass, "Contact shadows", () => s.ContactShadows, v => s.ContactShadows = v, true, preset));
            into.Add(Check(fieldClass, "Ambient occlusion", () => s.AmbientOcclusion, v => s.AmbientOcclusion = v, true, preset));
            into.Add(Check(fieldClass, "Screen-space reflections", () => s.ScreenSpaceReflections, v => s.ScreenSpaceReflections = v, true, preset));
            into.Add(Check(fieldClass, "Volumetric fog and light shafts", () => s.VolumetricFog, v => s.VolumetricFog = v, true, preset));

            Section(into, "Vegetation");
            into.Add(Range(fieldClass, "Grass density", 0f, GameSettings.MaxGrassDensity, () => s.GrassDensity, v => s.GrassDensity = v, true, preset));
            into.Add(Range(fieldClass, "Grass distance (m)", GameSettings.MinGrassDistance, GameSettings.MaxGrassDistance, () => s.GrassDistance, v => s.GrassDistance = v, true, preset));

            Section(into, "Camera effects");
            into.Add(Check(fieldClass, "Motion blur", () => s.MotionBlur, v => s.MotionBlur = v, false));
            into.Add(Range(fieldClass, "Bloom", 0f, 1f, () => s.Bloom, v => s.Bloom = v, false));
            into.Add(Range(fieldClass, "Film grain", 0f, 1f, () => s.FilmGrain, v => s.FilmGrain = v, false));
            into.Add(Range(fieldClass, "Vignette", 0f, 1f, () => s.Vignette, v => s.Vignette = v, false));
            into.Add(Check(fieldClass, "Chromatic aberration", () => s.ChromaticAberration, v => s.ChromaticAberration = v, false));
        }

        // ------------------------------------------------------------------ controls

        private static void Section(VisualElement into, string title)
        {
            var l = new Label(title);
            l.AddToClassList("g-subsection");
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginTop = 14;
            l.style.marginBottom = 4;
            l.style.color = new Color(0.94f, 0.71f, 0.16f);
            into.Add(l);
        }

        private static void Note(VisualElement into, string text)
        {
            var l = new Label(text);
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.fontSize = 12;
            l.style.color = new Color(1f, 1f, 1f, 0.55f);
            l.style.marginBottom = 6;
            into.Add(l);
        }

        /// <summary>After a graphics option changes: the preset follows the values (Custom unless they match one).</summary>
        private static void Changed(bool graphics, DropdownField preset)
        {
            var s = SettingsService.Current;
            if (graphics)
            {
                s.Quality = s.MatchingPreset();
                preset?.SetValueWithoutNotify(s.Quality.ToString());
            }
            SettingsService.Apply();
        }

        private static VisualElement Range(string cls, string label, float min, float max, Func<float> get, Action<float> set, bool graphics, DropdownField preset = null)
        {
            var slider = new Slider(label, min, max) { value = get(), showInputField = true };
            slider.AddToClassList(cls);
            slider.RegisterValueChangedCallback(e => { set(e.newValue); Changed(graphics, preset); });
            return slider;
        }

        private static VisualElement Check(string cls, string label, Func<bool> get, Action<bool> set, bool graphics, DropdownField preset = null)
        {
            var toggle = new Toggle(label) { value = get() };
            toggle.AddToClassList(cls);
            toggle.RegisterValueChangedCallback(e => { set(e.newValue); Changed(graphics, preset); });
            return toggle;
        }

        private static VisualElement Enum<T>(string cls, string label, T value, Action<T> set, string[] names, DropdownField preset = null) where T : struct, System.Enum
        {
            var index = Convert.ToInt32(value);
            var dropdown = new DropdownField(label, new List<string>(names), Mathf.Clamp(index, 0, names.Length - 1));
            dropdown.AddToClassList(cls);
            dropdown.RegisterValueChangedCallback(_ =>
            {
                set((T)System.Enum.ToObject(typeof(T), Math.Max(0, dropdown.index)));
                Changed(preset != null, preset);
            });
            return dropdown;
        }
    }
}
