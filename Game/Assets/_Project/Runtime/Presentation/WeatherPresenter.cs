using UnityEngine;

namespace HeroGame.Runtime.Presentation
{
    using HeroGame.Core.Weather;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// Presents authoritative weather: rain/debris particles, wind, fog and a wet-surface global shader
    /// value. Pipeline-specific volumes (HDRP fog, clouds) subscribe through <see cref="Applied"/>
    /// so this class stays pipeline agnostic.
    /// </summary>
    public sealed class WeatherPresenter : MonoBehaviour
    {
        public ParticleSystem Rain;
        public ParticleSystem Debris;
        public WindZone Wind;
        public float MaxRainEmission = 6000f;
        public float Blend = 0.5f;

        public static readonly int WetnessId = Shader.PropertyToID("_HG_GlobalWetness");

        public event System.Action<WeatherState, WeatherEffects> Applied;

        private float _rain;
        private float _wetness;

        private void Update()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var state = session.World.Weather.State.Current;
            var effects = session.World.Weather.Effects;
            var k = 1f - Mathf.Exp(-Blend * Time.deltaTime);

            _rain = Mathf.Lerp(_rain, Mathf.Clamp01(state.Precipitation / 40f), k);
            _wetness = Mathf.Lerp(_wetness, state.Precipitation > 0.5f ? 1f : 0f, k * (state.Precipitation > 0.5f ? 1f : 0.1f));
            Shader.SetGlobalFloat(WetnessId, _wetness);

            if (Rain != null)
            {
                var emission = Rain.emission;
                emission.rateOverTime = _rain * MaxRainEmission;
                if (_rain > 0.01f && !Rain.isPlaying) Rain.Play();
                else if (_rain <= 0.01f && Rain.isPlaying) Rain.Stop();
            }
            if (Debris != null)
            {
                var storm = state.Kind == WeatherKind.Hurricane || state.Kind == WeatherKind.TropicalStorm;
                if (storm && !Debris.isPlaying) Debris.Play();
                else if (!storm && Debris.isPlaying) Debris.Stop();
            }
            if (Wind != null)
            {
                Wind.windMain = state.WindSpeedMs * 0.1f;
                Wind.windTurbulence = Mathf.Clamp01(state.WindSpeedMs / 30f);
            }

            RenderSettings.fog = effects.VisibilityMetres < 8000f;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = Mathf.Lerp(RenderSettings.fogDensity, 1.7f / Mathf.Max(50f, effects.VisibilityMetres), k);
            Applied?.Invoke(state, effects);
        }
    }
}
