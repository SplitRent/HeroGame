using UnityEngine;
using UnityEngine.AI;

namespace HeroGame.Runtime.Population
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;

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
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), 1f - Mathf.Exp(-8f * Time.deltaTime));
        }

        /// <summary>Placeholder appearance from the NPC's persistent appearance seed (replaced by the character system).</summary>
        private void ApplyAppearance(NpcRecord record)
        {
            transform.localScale = Vector3.one * Mathf.Clamp(record.HeightCm / 175f, 0.35f, 1.2f);
            if (BodyRenderer == null) return;
            var rng = new DeterministicRandom(record.AppearanceSeed);
            var block = new MaterialPropertyBlock();
            var color = Color.HSVToRGB(rng.NextFloat(), 0.35f + rng.NextFloat() * 0.4f, 0.45f + rng.NextFloat() * 0.45f);
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            BodyRenderer.SetPropertyBlock(block);
        }
    }
}
