using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.Population
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// Bridges the core <see cref="PopulationDirector"/> to pooled <see cref="NpcAvatar"/>s (TDD §7.3).
    /// Every <see cref="EvaluateInterval"/> seconds it asks who should be present near the observers,
    /// keeps avatars that are still wanted (so people never pop while visible), binds new ones out of
    /// view where possible, and releases the rest. NPC identity always comes from the persistent record.
    /// </summary>
    public sealed class NpcPopulationPresenter : MonoBehaviour
    {
        public NpcAvatar AvatarPrefab;
        public Transform Observer;
        public float EvaluateInterval = 1.5f;
        public int PoolSize = 120;
        [Tooltip("New NPCs spawn at least this far from the camera when possible, so they never visibly appear.")]
        public float MinSpawnDistance = 35f;

        private readonly Dictionary<EntityId, NpcAvatar> _active = new Dictionary<EntityId, NpcAvatar>();
        private readonly Stack<NpcAvatar> _pool = new Stack<NpcAvatar>();
        private readonly List<WorldPosition> _observers = new List<WorldPosition>(1);
        private readonly HashSet<EntityId> _wanted = new HashSet<EntityId>();
        private readonly List<EntityId> _release = new List<EntityId>();
        private float _timer;

        public int ActiveCount => _active.Count;
        public IEnumerable<NpcAvatar> Active => _active.Values;

        private void Start()
        {
            if (AvatarPrefab == null) return;
            for (var i = 0; i < PoolSize; i++)
            {
                var a = Instantiate(AvatarPrefab, transform);
                a.Release();
                _pool.Push(a);
            }
        }

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f || Observer == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            _timer = EvaluateInterval;
            Evaluate(session);
        }

        private void Evaluate(GameSession session)
        {
            var world = session.World;
            _observers.Clear();
            _observers.Add(Observer.position.ToWorld());
            var requests = world.Director.Evaluate(_observers, world.Clock.Now);

            _wanted.Clear();
            foreach (var r in requests)
            {
                var indoors = r.Activity.Activity == ActivityKind.Sleeping || r.Activity.Activity == ActivityKind.AtHome || r.Activity.Activity == ActivityKind.Hospitalized
                              || (r.Activity.Activity != ActivityKind.Commuting && !IsPublic(world, r.Activity.Place));
                if (indoors)
                    continue; // indoors: materialised by the interior when the player enters (not on the street)
                _wanted.Add(r.Npc);
                var target = r.Position.ToVector3() + Jitter(r.Npc);
                if (_active.TryGetValue(r.Npc, out var avatar))
                {
                    avatar.SetTier(r.Tier);
                    avatar.SetGoal(target, r.Activity.Activity);
                    continue;
                }
                if (_pool.Count == 0) continue;
                var record = world.Population.Get(r.Npc);
                if (record == null) continue;
                avatar = _pool.Pop();
                avatar.Bind(record, r.Tier, SpawnPoint(target));
                avatar.SetGoal(target, r.Activity.Activity);
                _active[r.Npc] = avatar;
            }

            _release.Clear();
            foreach (var kv in _active) if (!_wanted.Contains(kv.Key)) _release.Add(kv.Key);
            foreach (var id in _release)
            {
                var avatar = _active[id];
                _active.Remove(id);
                avatar.Release();
                _pool.Push(avatar);
            }
        }

        private static bool IsPublic(Core.Simulation.World world, EntityId placeId)
        {
            var place = world.Geography.GetPlace(placeId);
            return place != null && (place.Kind == Core.World.PlaceKind.Park || place.Kind == Core.World.PlaceKind.Beach || place.Kind == Core.World.PlaceKind.Dock);
        }

        /// <summary>Stable per-NPC offset so people at the same place spread out consistently.</summary>
        private static Vector3 Jitter(EntityId id)
        {
            var h = StableHash.Mix(id.Value);
            return new Vector3(((h & 0xFF) / 255f - 0.5f) * 8f, 0f, (((h >> 8) & 0xFF) / 255f - 0.5f) * 8f);
        }

        private Vector3 SpawnPoint(Vector3 target)
        {
            var cam = Camera.main;
            if (cam == null) return target;
            var toTarget = target - cam.transform.position;
            var visible = Vector3.Dot(cam.transform.forward, toTarget.normalized) > 0.3f && toTarget.magnitude < MinSpawnDistance;
            if (!visible) return target;
            // Visible and close: start behind the nearest corner relative to the camera instead of popping in.
            var away = Vector3.ProjectOnPlane(toTarget, Vector3.up).normalized;
            return target + away * MinSpawnDistance * 0.5f;
        }
    }
}
