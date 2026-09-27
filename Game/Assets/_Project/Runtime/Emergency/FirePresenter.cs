using System.Collections.Generic;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.Emergency
{
    /// <summary>Flames and smoke on burning buildings near the player, sized by the core fire intensity.</summary>
    public sealed class FirePresenter : MonoBehaviour
    {
        public Transform Observer;
        public ParticleSystem FirePrefab;
        public float Radius = 500f;

        private readonly Dictionary<EntityId, ParticleSystem> _fires = new Dictionary<EntityId, ParticleSystem>();
        private readonly List<EntityId> _stale = new List<EntityId>();

        private void Update()
        {
            if (Observer == null || FirePrefab == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            _stale.Clear();
            _stale.AddRange(_fires.Keys);
            foreach (var i in session.World.Emergency.Incidents)
            {
                if (i.Kind != EmergencyKind.Fire || !i.Open || i.FireIntensity <= 0f) continue;
                var pos = new Vector3(i.Position.X, 0f, i.Position.Z);
                if ((pos - Observer.position).sqrMagnitude > Radius * Radius) continue;
                _stale.Remove(i.Id);
                if (!_fires.TryGetValue(i.Id, out var ps))
                {
                    ps = Instantiate(FirePrefab, pos + Vector3.up * 2f, Quaternion.identity, transform);
                    _fires[i.Id] = ps;
                }
                var emission = ps.emission;
                emission.rateOverTimeMultiplier = 20f + 180f * i.FireIntensity;
                ps.transform.localScale = Vector3.one * (1f + 3f * i.FireIntensity);
            }
            foreach (var id in _stale)
            {
                Destroy(_fires[id].gameObject);
                _fires.Remove(id);
            }
        }
    }
}
