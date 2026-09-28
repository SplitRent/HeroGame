using UnityEngine;

namespace HeroGame.Runtime.Audio
{
    using HeroGame.Core.Audio;
    using HeroGame.Runtime.Vehicles;

    /// <summary>Engine sound for a vehicle: an idle loop whose pitch and volume follow speed.</summary>
    [RequireComponent(typeof(VehicleController))]
    public sealed class VehicleAudio : MonoBehaviour
    {
        private VehicleController _vehicle;
        private AudioSource _engine;

        private void Start()
        {
            _vehicle = GetComponent<VehicleController>();
            var mass = _vehicle.Model != null ? _vehicle.Model.MassKg : 1400f;
            var key = "engine:" + Mathf.RoundToInt(mass / 250f);
            _engine = gameObject.AddComponent<AudioSource>();
            _engine.clip = ProceduralClips.Prefer("engine_" + Mathf.RoundToInt(mass / 250f), key, () => Synth.Engine(mass));
            _engine.loop = true;
            _engine.spatialBlend = 1f;
            _engine.maxDistance = 80f;
            _engine.rolloffMode = AudioRolloffMode.Linear;
            _engine.Play();
        }

        private void Update()
        {
            if (_engine == null) return;
            var top = _vehicle.Model != null ? Mathf.Max(60f, _vehicle.Model.TopSpeedKph) : 160f;
            var speed = Mathf.Clamp01(_vehicle.SpeedKph / top);
            // Crude gears: pitch climbs through each gear and drops at the shift.
            var gear = Mathf.Min(4, (int)(speed * 5f));
            var inGear = speed * 5f - gear;
            _engine.pitch = 0.8f + inGear * 0.9f + gear * 0.08f;
            _engine.volume = (0.35f + 0.65f * speed) * AudioVolumes.Effective(AudioChannel.Effects);
        }
    }
}
