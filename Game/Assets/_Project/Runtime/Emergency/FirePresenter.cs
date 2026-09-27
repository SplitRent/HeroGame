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
            var replica = Online.NetworkSession.Replica;
            if (replica != null)
            {
                // Online: the server's fires, not the local presentation world's.
                foreach (var f in replica.Fires) Show(f.Incident, f.Position, f.Intensity);
            }
            else
            {
                foreach (var i in session.World.Emergency.Incidents)
                    if (i.Kind == EmergencyKind.Fire && i.Open && i.FireIntensity > 0f) Show(i.Id, i.Position, i.FireIntensity);
            }
            foreach (var id in _stale)
            {
                Destroy(_fires[id].gameObject);
                _fires.Remove(id);
            }
        }

        private void Show(EntityId id, WorldPosition at, float intensity)
        {
            var pos = new Vector3(at.X, 0f, at.Z);
            if ((pos - Observer.position).sqrMagnitude > Radius * Radius) return;
            _stale.Remove(id);
            if (!_fires.TryGetValue(id, out var ps))
            {
                ps = Instantiate(FirePrefab, pos + Vector3.up * 2f, Quaternion.identity, transform);
                _fires[id] = ps;
            }
            var emission = ps.emission;
            emission.rateOverTimeMultiplier = 20f + 180f * intensity;
            ps.transform.localScale = Vector3.one * (1f + 3f * intensity);
        }
    }
}
