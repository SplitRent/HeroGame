using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HeroGame.Runtime.Presentation
{
    using HeroGame.Core.Presentation;

    /// <summary>
    /// Applies the player's graphics settings to the engine and the render pipeline:
    ///  • window mode and resolution, texture resolution, anisotropic filtering, level-of-detail bias;
    ///  • per camera: draw distance and (HDRP) anti-aliasing mode;
    ///  • per sun/moon light: shadow resolution tier (or no shadows);
    ///  • a global, high-priority volume (HDRP) overriding only the player-facing effects: shadow distance, ambient
    ///    occlusion, screen-space reflections, volumetric fog, contact shadows, bloom, motion blur, film grain,
    ///    vignette, chromatic aberration and exposure compensation (brightness). Scene volumes keep everything else.
    /// Re-applied after every scene load, since cameras and lights belong to scenes.
    /// </summary>
    public static class GraphicsApplier
    {
        public const float BaseViewDistance = 1500f;

        private static GameSettings _last;
        private static GameObject _volumeHost;
        private static ScriptableObject _profile;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Hook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_last != null) Apply(_last);
        }

        public static void Apply(GameSettings s)
        {
            _last = s;
            ApplyDisplay(s);
            ApplyQuality(s);
            foreach (var camera in Camera.allCameras) ApplyCamera(camera, s);
            foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
                if (light.gameObject.scene.IsValid()) ApplyLight(light, s); // scene lights only, not prefab assets
            ApplyVolume(s);
        }

        private static void ApplyDisplay(GameSettings s)
        {
            if (Application.isEditor) return; // the Game view owns its size
            var mode = s.Window == WindowMode.Fullscreen ? FullScreenMode.ExclusiveFullScreen : s.Window == WindowMode.Borderless ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            var native = Screen.currentResolution;
            var w = s.ResolutionWidth > 0 ? s.ResolutionWidth : native.width;
            var h = s.ResolutionHeight > 0 ? s.ResolutionHeight : native.height;
            if (Screen.width != w || Screen.height != h || Screen.fullScreenMode != mode) Screen.SetResolution(w, h, mode);
        }

        private static void ApplyQuality(GameSettings s)
        {
            QualitySettings.anisotropicFiltering = s.AnisotropicFiltering ? AnisotropicFiltering.ForceEnable : AnisotropicFiltering.Disable;
            QualitySettings.lodBias = s.DetailLevel;
            // Full = 0 (every mip), Half = 1, Quarter = 2. The property was renamed in Unity 2022.2.
            var limit = 2 - (int)s.Textures;
            var property = typeof(QualitySettings).GetProperty("globalTextureMipmapLimit", BindingFlags.Public | BindingFlags.Static)
                           ?? typeof(QualitySettings).GetProperty("masterTextureLimit", BindingFlags.Public | BindingFlags.Static);
            property?.SetValue(null, limit);
            // Built-in pipeline equivalents (HDRP reads its own settings from the volume and light data below).
            QualitySettings.shadowDistance = s.ShadowDistance;
            QualitySettings.shadows = s.Shadows == ShadowQuality.Off ? UnityEngine.ShadowQuality.Disable : UnityEngine.ShadowQuality.All;
            QualitySettings.antiAliasing = s.AntiAliasing == AntiAliasing.Off ? 0 : s.AntiAliasing == AntiAliasing.Taa ? 8 : 4;
        }

        private static void ApplyCamera(Camera camera, GameSettings s)
        {
            if (camera.targetTexture != null) return; // minimap/render-texture cameras keep their own setup
            camera.farClipPlane = BaseViewDistance * s.ViewDistance;
            var data = camera.GetComponent(PipelineReflection.FindType(PipelineReflection.Hd + "HDAdditionalCameraData") ?? typeof(Transform));
            if (data == null || data is Transform) return;
            var mode = s.AntiAliasing == AntiAliasing.Fxaa ? "FastApproximateAntialiasing" : s.AntiAliasing == AntiAliasing.Smaa ? "SubpixelMorphologicalAntiAliasing" :
                       s.AntiAliasing == AntiAliasing.Taa ? "TemporalAntialiasing" : "None";
            PipelineReflection.SetMember(data, "antialiasing", mode);
        }

        private static void ApplyLight(Light light, GameSettings s)
        {
            if (light.type != LightType.Directional) return;
            light.shadows = s.Shadows == ShadowQuality.Off ? LightShadows.None : LightShadows.Soft;
            light.shadowResolution = s.Shadows == ShadowQuality.Low ? UnityEngine.Rendering.LightShadowResolution.Low :
                                     s.Shadows == ShadowQuality.Medium ? UnityEngine.Rendering.LightShadowResolution.Medium :
                                     s.Shadows == ShadowQuality.High ? UnityEngine.Rendering.LightShadowResolution.High : UnityEngine.Rendering.LightShadowResolution.VeryHigh;
            var data = light.GetComponent(PipelineReflection.FindType(PipelineReflection.Hd + "HDAdditionalLightData") ?? typeof(Transform));
            if (data == null || data is Transform || s.Shadows == ShadowQuality.Off) return;
            PipelineReflection.Invoke(data, "SetShadowResolutionLevel", (int)s.Shadows - 1); // HDRP tiers 0..3
        }

        private static void ApplyVolume(GameSettings s)
        {
            if (_profile == null)
            {
                _profile = PipelineReflection.NewProfile();
                if (_profile == null) return;
                _profile.name = "Player Graphics Settings";
                _volumeHost = new GameObject("Player Graphics Settings");
                Object.DontDestroyOnLoad(_volumeHost);
                if (PipelineReflection.AddGlobalVolume(_volumeHost, _profile, 100f) == null) return;
            }
            var hd = PipelineReflection.Hd;
            var shadows = PipelineReflection.Override(_profile, hd + "HDShadowSettings");
            PipelineReflection.Set(shadows, "maxShadowDistance", s.ShadowDistance);

            var ao = PipelineReflection.Override(_profile, hd + "ScreenSpaceAmbientOcclusion");
            PipelineReflection.SetActive(ao, s.AmbientOcclusion);
            PipelineReflection.Set(ao, "intensity", s.AmbientOcclusion ? 1f : 0f);

            var ssr = PipelineReflection.Override(_profile, hd + "ScreenSpaceReflection");
            PipelineReflection.Set(ssr, "enabled", s.ScreenSpaceReflections);

            var fog = PipelineReflection.Override(_profile, hd + "Fog");
            PipelineReflection.Set(fog, "enableVolumetricFog", s.VolumetricFog);

            var contact = PipelineReflection.Override(_profile, hd + "ContactShadows");
            PipelineReflection.Set(contact, "enable", s.ContactShadows);

            PipelineReflection.Set(PipelineReflection.Override(_profile, hd + "Bloom"), "intensity", s.Bloom * 0.6f);
            PipelineReflection.Set(PipelineReflection.Override(_profile, hd + "MotionBlur"), "intensity", s.MotionBlur ? 0.5f : 0f);
            PipelineReflection.Set(PipelineReflection.Override(_profile, hd + "FilmGrain"), "intensity", s.FilmGrain);
            PipelineReflection.Set(PipelineReflection.Override(_profile, hd + "Vignette"), "intensity", s.Vignette * 0.5f);
            PipelineReflection.Set(PipelineReflection.Override(_profile, hd + "ChromaticAberration"), "intensity", s.ChromaticAberration ? 0.25f : 0f);
            PipelineReflection.Set(PipelineReflection.Override(_profile, hd + "Exposure"), "compensation", s.Brightness);
        }
    }
}
