using UnityEngine;
using UnityEngine.AI;

namespace HeroGame.Runtime.Population
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.People;

    /// <summary>
    /// The physical body of a persistent NPC while it is near a player. Pooled: when released it keeps
    /// no identity; when reused it is bound to a (possibly different) <see cref="NpcRecord"/>.
    /// Full tier uses NavMesh navigation when a NavMesh exists; Nearby tier uses cheap steering.
    /// </summary>
    public sealed class NpcAvatar : MonoBehaviour
    {
        public EntityId NpcId { get; private set; }
        public SimulationTier Tier { get; private set; }
        public ActivityKind Activity { get; private set; }
        public string DisplayName { get; private set; } = "";

        public float WalkSpeed = 1.35f;
        public Renderer BodyRenderer;

        /// <summary>Face, body, today's clothes and walk (from the character catalogs); null before binding.</summary>
        public NpcLooks Looks { get; private set; }
        private WalkStyle _walk;
        private HumanAvatar _human;
        private HumanGait _humanGait;
        private float _gaitPhase;

        private NavMeshAgent _agent;
        private Vector3 _target;
        private bool _hasTarget;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            if (BodyRenderer == null) BodyRenderer = GetComponentInChildren<Renderer>();
        }

        public void Bind(NpcRecord record, SimulationTier tier, Vector3 spawn)
        {
            NpcId = record.Id;
            DisplayName = record.FullName;
            name = "NPC " + record.FullName;
            transform.position = spawn;
            gameObject.SetActive(true);
            ApplyAppearance(record);
            SetTier(tier);
        }

        public void SetTier(SimulationTier tier)
        {
            Tier = tier;
            if (_agent != null) _agent.enabled = tier == SimulationTier.Full && _agent.isOnNavMesh;
        }

        public void SetGoal(Vector3 position, ActivityKind activity)
        {
            Activity = activity;
            _target = position;
            _hasTarget = true;
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh) _agent.SetDestination(position);
        }

        private float _flinch;

        /// <summary>Placeholder hit reaction (a stagger) until the animation set exists.</summary>
        public void Flinch() => _flinch = 0.35f;

        public void Release()
        {
            NpcId = EntityId.None;
            _hasTarget = false;
            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_flinch > 0f)
            {
                _flinch -= Time.deltaTime;
                var e = transform.eulerAngles;
                transform.rotation = Quaternion.Euler(Mathf.Sin(Mathf.Clamp01(_flinch / 0.35f) * Mathf.PI) * -18f, e.y, 0f);
            }
            if (!_hasTarget || (_agent != null && _agent.enabled)) return;
            var to = _target - transform.position;
            to.y = 0f;
            var dist = to.magnitude;
            if (dist < 0.5f) return;
            var speed = Activity == ActivityKind.Commuting ? WalkSpeed * 1.2f : WalkSpeed;
            var step = to / dist * Mathf.Min(dist, speed * Time.deltaTime);
            transform.position += step;
            var yaw = Quaternion.Slerp(Quaternion.Euler(0f, transform.eulerAngles.y, 0f), Quaternion.LookRotation(to), 1f - Mathf.Exp(-8f * Time.deltaTime));
            // A human body walks with its legs (HumanGait); the placeholder capsule leans and sways instead.
            transform.rotation = _human != null ? yaw : yaw * Gait(speed);
        }

        /// <summary>
        /// Procedural gait layer (until the locomotion clips exist): each walk style's lean and hip sway, stepped at a
        /// cadence set by its speed and stride, so the town does not move in lockstep.
        /// </summary>
        private Quaternion Gait(float speed)
        {
            if (_walk == null) return Quaternion.identity;
            var stepLength = 0.7f * Mathf.Max(0.3f, _walk.Stride) * Mathf.Clamp(transform.localScale.y, 0.4f, 1.2f);
            _gaitPhase += Time.deltaTime * speed / stepLength * Mathf.PI;
            var sway = Mathf.Sin(_gaitPhase) * (2f + 6f * _walk.HipSway);
            var pitch = _walk.Lean + _walk.HeadDown * 0.2f + Mathf.Abs(Mathf.Sin(_gaitPhase)) * _walk.Bounce * 2f;
            return Quaternion.Euler(pitch, Mathf.Sin(_gaitPhase) * _walk.ShoulderRoll * 6f, sway * 0.5f);
        }

        /// <summary>
        /// Looks from the character catalogs: height, build, clothes for today's weather and a personal walk. The body is
        /// still a placeholder capsule tinted with the main garment's colour until the human model is in
        /// (docs/ASSET_TRACKER.md).
        /// </summary>
        private void ApplyAppearance(NpcRecord record)
        {
            var height = Mathf.Clamp(record.HeightCm / 175f, 0.35f, 1.2f);
            Looks = null;
            _walk = null;
            Color color;
            if (ServiceRegistry.TryGet<GameSession>(out var session) && session.World.Content.Clothing.Count > 0)
            {
                var w = session.World;
                var weather = w.Weather.State.Current;
                var raining = weather.Kind >= Core.Weather.WeatherKind.LightRain;
                Looks = LooksGenerator.ForNpc(record, w.Today, w.Content, weather.TemperatureC, raining);
                _walk = w.Content.Animations.Walk(Looks.WalkStyle);
                if (_walk != null) WalkSpeed = _walk.Speed;
                _gaitPhase = (record.AppearanceSeed & 0xFFFF) / 65535f * Mathf.PI * 2f;
                if (_human == null) _human = GetComponentInChildren<HumanAvatar>();
                if (_humanGait == null) _humanGait = GetComponentInChildren<HumanGait>();
                if (_human != null)
                {
                    // The human body carries height, build, skin and clothes itself.
                    transform.localScale = Vector3.one;
                    var age = (int)System.Math.Max(0, (w.Today - record.BirthDay) / 365);
                    _human.Apply(Looks.Appearance, record.Presentation, age);
                    _human.Dress(Looks.Outfit, w.Content, record.Presentation);
                    if (_humanGait != null) _humanGait.Style = _walk;
                    if (_agent != null) _agent.speed = WalkSpeed;
                    return;
                }
                var build = Looks.Appearance.GetMorph("body_fat") * 0.5f + Looks.Appearance.GetMorph("muscle") * 0.3f;
                transform.localScale = new Vector3(height * (0.85f + build * 0.4f), height, height * (0.85f + build * 0.4f));
                color = MainColour(w.Content, Looks.Outfit);
            }
            else
            {
                transform.localScale = Vector3.one * height;
                var rng = new DeterministicRandom(record.AppearanceSeed);
                color = Color.HSVToRGB(rng.NextFloat(), 0.35f + rng.NextFloat() * 0.4f, 0.45f + rng.NextFloat() * 0.45f);
            }
            if (_agent != null) _agent.speed = WalkSpeed;
            if (BodyRenderer == null) return;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            BodyRenderer.SetPropertyBlock(block);
        }

        /// <summary>The colour most of the body shows: the jacket, else the dress, else the top.</summary>
        private static Color MainColour(Core.World.ContentSet content, Outfit outfit)
        {
            foreach (var slot in new[] { ClothingSlot.Outer, ClothingSlot.FullBody, ClothingSlot.Top })
                foreach (var p in outfit.Pieces)
                {
                    var item = content.FindClothing(p.ItemId);
                    if (item == null || item.Slot != slot) continue;
                    var v = item.Variant(p.VariantId);
                    if (v != null && ColorUtility.TryParseHtmlString(v.Hex, out var c)) return c;
                }
            return new Color(0.6f, 0.55f, 0.5f);
        }
    }
}
