using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Powers;
using HeroGame.Core.Simulation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Online;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.Population;
using HeroGame.Runtime.Presentation;
using HeroGame.Runtime.UI;
using HeroGame.Runtime.Vehicles;
using UnityEngine;

namespace HeroGame.Runtime.Powers
{
    /// <summary>
    /// Player ability input and presentation (GDD §41). 1–4 selects a power; hold Q to charge, release to use.
    /// The target is whatever the camera is aimed at (person, vehicle, building or ground). The core
    /// <see cref="PowerService"/> — or the server, when online — decides what actually happens; this component only
    /// renders the result: motion effects on the player, knockback on physics objects and element-coloured VFX.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class PowerController : MonoBehaviour
    {
        public Camera AimCamera;
        public float MaxAimDistance = 150f;
        public float FullChargeSeconds = 1.5f;
        public ParticleSystem ImpactVfx;
        public LayerMask AimMask = ~0;

        private PlayerMotor _motor;
        private IPlayerInputSource _input;
        private int _selected;
        private float _charge;
        private bool _charging;
        private readonly List<(float until, System.Action end)> _timed = new List<(float, System.Action)>();

        public int Selected => _selected;
        public float Charge01 => Mathf.Clamp01(_charge / FullChargeSeconds);

        private void Awake() => _motor = GetComponent<PlayerMotor>();

        private void Update()
        {
            if (_input == null) _input = PlayerInputRegistry.Create();
            ExpireTimedEffects();
            if (!_input.GameplayEnabled || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var input = _input.Read();
            var count = session.LocalCharacter.Powers.Powers.Count;
            if (input.PowerSlotPressed > 0 && input.PowerSlotPressed <= count)
            {
                _selected = input.PowerSlotPressed - 1;
                SubtitleFeed.Say("", "Ready: " + Label(session.LocalCharacter.Powers.Powers[_selected]), 1.5f);
            }
            if (count == 0) return;
            if (input.PowerHeld)
            {
                _charging = true;
                _charge += Time.deltaTime;
            }
            else if (_charging)
            {
                _charging = false;
                Fire(session, Mathf.Clamp(_charge / FullChargeSeconds, 0.1f, 1f));
                _charge = 0f;
            }
        }

        private static string Label(PowerInstance p) => string.IsNullOrEmpty(p.PlayerLabel) ? p.Definition.Describe() : p.PlayerLabel;

        private void Fire(GameSession session, float intensity)
        {
            var request = Aim(session, intensity);
            if (NetworkSession.Current != null && NetworkSession.Current.State == Networking.Client.ClientState.Connected)
            {
                var args = new Dictionary<string, string>
                {
                    ["power"] = _selected.ToString(), ["intensity"] = intensity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["target"] = request.Target.ToString(),
                    ["x"] = request.Point.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    ["z"] = request.Point.Z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                };
                if (request.TargetId.IsValid) args["id"] = request.TargetId.ToString();
                var pending = NetworkSession.Current.Request("power.use", args);
                pending.ContinueWith(t =>
                {
                    var r = t.Result;
                    SubtitleFeed.Say("", r.Success ? r.Data["message"] : r.Error, 2.5f);
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }
            var outcome = session.World.PowerUse.Use(session.LocalCharacter, request);
            SubtitleFeed.Say("", outcome.Message, 2.5f);
            if (outcome.Attempted) Present(outcome, request);
        }

        /// <summary>Raycast from the camera; the first thing hit decides the target kind.</summary>
        private PowerUseRequest Aim(GameSession session, float intensity)
        {
            var origin = transform.position;
            var request = new PowerUseRequest
            {
                PowerIndex = _selected,
                Intensity = intensity,
                Origin = origin.ToWorld(),
                Target = TargetKind.Self,
                Point = origin.ToWorld(),
            };
            var cam = AimCamera != null ? AimCamera : Camera.main;
            if (cam == null) return request;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!Physics.Raycast(ray, out var hit, MaxAimDistance, AimMask, QueryTriggerInteraction.Ignore))
            {
                request.Target = TargetKind.Point;
                request.Point = ray.GetPoint(MaxAimDistance * 0.5f).ToWorld();
                return request;
            }
            request.Point = hit.point.ToWorld();
            request.Target = TargetKind.Point;
            var npc = hit.collider.GetComponentInParent<NpcAvatar>();
            var vehicle = hit.collider.GetComponentInParent<VehicleController>();
            var place = hit.collider.GetComponentInParent<PlaceMarker>();
            if (npc != null && npc.NpcId.IsValid)
            {
                request.Target = TargetKind.Npc;
                request.TargetId = npc.NpcId;
            }
            else if (vehicle != null && vehicle.Record != null)
            {
                request.Target = TargetKind.Vehicle;
                request.TargetId = vehicle.Record.Id;
            }
            else if (place != null)
            {
                var resolved = place.Resolve(session.World);
                if (resolved != null && resolved.Property.IsValid)
                {
                    request.Target = TargetKind.Property;
                    request.TargetId = resolved.Property;
                }
            }
            return request;
        }

        private void Present(PowerOutcome outcome, PowerUseRequest request)
        {
            var plan = outcome.Plan;
            var target = request.Point.ToVector3();
            if (!outcome.Use.Success)
            {
                if (outcome.Use.Backfire) Burst(transform.position + Vector3.up, plan.Element, 0.6f);
                return;
            }
            switch (plan.Kind)
            {
                case EffectKind.Strike:
                    Burst(target, plan.Element, 1f);
                    if (outcome.Hit) Knock(target, plan.Impulse, 2f);
                    break;
                case EffectKind.Blast:
                    Burst(target, plan.Element, 1f + plan.Radius / 4f);
                    Knock(target, plan.Impulse, plan.Radius);
                    break;
                case EffectKind.Telekinesis:
                    Knock(target, plan.Impulse, 3f);
                    break;
                case EffectKind.Leap:
                    _motor.Launch(Vector3.up * Mathf.Sqrt(2f * plan.Amount * -_motor.Gravity) + transform.forward * 4f);
                    break;
                case EffectKind.SpeedBoost:
                    _motor.SpeedMultiplier = plan.Amount;
                    For(plan.Duration, () => _motor.SpeedMultiplier = 1f);
                    break;
                case EffectKind.TimeDilation:
                    _motor.SpeedMultiplier = plan.Amount;
                    For(plan.Duration, () => _motor.SpeedMultiplier = 1f);
                    break;
                case EffectKind.Flight:
                    _motor.Flying = true;
                    _motor.SpeedMultiplier = Mathf.Max(1f, plan.Amount / _motor.JogSpeed);
                    For(plan.Duration, () =>
                    {
                        _motor.Flying = false;
                        _motor.SpeedMultiplier = 1f;
                    });
                    break;
                case EffectKind.Teleport:
                    if (outcome.Teleported)
                    {
                        Burst(transform.position + Vector3.up, plan.Element, 0.8f);
                        _motor.Warp(outcome.TeleportTo.ToVector3() + Vector3.up * 0.2f, transform.rotation);
                        Burst(transform.position + Vector3.up, plan.Element, 0.8f);
                    }
                    break;
                default:
                    Burst(transform.position + Vector3.up, plan.Element, 0.5f);
                    break;
            }
        }

        private void For(float seconds, System.Action end) => _timed.Add((Time.time + seconds, end));

        private void ExpireTimedEffects()
        {
            for (var i = _timed.Count - 1; i >= 0; i--)
            {
                if (Time.time < _timed[i].until) continue;
                _timed[i].end();
                _timed.RemoveAt(i);
            }
        }

        private static void Knock(Vector3 at, float impulse, float radius)
        {
            foreach (var c in Physics.OverlapSphere(at, radius))
            {
                var body = c.attachedRigidbody;
                if (body != null && !body.isKinematic) body.AddExplosionForce(impulse, at, radius, 0.4f, ForceMode.Impulse);
            }
        }

        private void Burst(Vector3 at, PowerElement element, float scale)
        {
            if (ImpactVfx == null) return;
            var fx = Instantiate(ImpactVfx, at, Quaternion.identity);
            var main = fx.main;
            main.startColor = ElementColor(element);
            fx.transform.localScale = Vector3.one * scale;
            Destroy(fx.gameObject, 3f);
        }

        public static Color ElementColor(PowerElement e)
        {
            switch (e)
            {
                case PowerElement.Fire: return new Color(1f, 0.45f, 0.1f);
                case PowerElement.Electric: return new Color(0.5f, 0.75f, 1f);
                case PowerElement.Cold: return new Color(0.7f, 0.95f, 1f);
                case PowerElement.Light: return new Color(1f, 1f, 0.8f);
                case PowerElement.Gravitic: return new Color(0.45f, 0.2f, 0.7f);
                case PowerElement.Biological: return new Color(0.4f, 1f, 0.5f);
                case PowerElement.Psychic: return new Color(0.9f, 0.4f, 0.9f);
                case PowerElement.Temporal: return new Color(0.9f, 0.85f, 0.4f);
                case PowerElement.Spatial: return new Color(0.3f, 0.3f, 1f);
                default: return new Color(0.85f, 0.85f, 0.85f);
            }
        }
    }
}
