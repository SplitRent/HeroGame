using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Presentation;
using UnityEngine;

namespace HeroGame.Runtime.Population
{
    /// <summary>
    /// Materialises the persistent people who are inside a place (workers on shift, customers, residents
    /// at home) while the player is within the volume, and releases them when the player leaves
    /// (TDD §5.3). Who appears is decided by the core schedules, never invented locally.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class InteriorPresenter : MonoBehaviour
    {
        public PlaceMarker Place;
        public NpcAvatar AvatarPrefab;
        public Transform[] SpawnPoints;
        public int MaxOccupants = 12;
        public float RefreshInterval = 5f;

        private readonly List<NpcAvatar> _active = new List<NpcAvatar>();
        private readonly Stack<NpcAvatar> _pool = new Stack<NpcAvatar>();
        private bool _playerInside;
        private float _timer;

        private void Reset() => GetComponent<BoxCollider>().isTrigger = true;

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag("Player")) return;
            _playerInside = true;
            _timer = 0f;
        }

        private void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag("Player")) return;
            _playerInside = false;
            ReleaseAll();
        }

        private void Update()
        {
            if (!_playerInside) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = RefreshInterval;
            Refresh();
        }

        private void Refresh()
        {
            if (Place == null || AvatarPrefab == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var place = Place.Resolve(session.World);
            if (place == null) return;
            var people = session.World.Director.PeopleAt(place, session.World.Clock.Now);
            var wanted = new HashSet<EntityId>();
            for (var i = 0; i < people.Count && i < MaxOccupants; i++) wanted.Add(people[i]);

            for (var i = _active.Count - 1; i >= 0; i--)
            {
                if (wanted.Contains(_active[i].NpcId)) continue;
                _active[i].Release();
                _pool.Push(_active[i]);
                _active.RemoveAt(i);
            }
            var present = new HashSet<EntityId>();
            foreach (var a in _active) present.Add(a.NpcId);
            var slot = 0;
            foreach (var id in wanted)
            {
                if (present.Contains(id)) continue;
                var record = session.World.Population.Get(id);
                if (record == null) continue;
                var avatar = _pool.Count > 0 ? _pool.Pop() : Instantiate(AvatarPrefab, transform);
                var spawn = SpawnPoint(slot++);
                avatar.Bind(record, SimulationTier.Full, spawn);
                avatar.SetGoal(spawn, session.World.Schedules.Resolve(record, session.World.Clock.Now).Activity);
                _active.Add(avatar);
            }
        }

        private Vector3 SpawnPoint(int index)
        {
            if (SpawnPoints != null && SpawnPoints.Length > 0) return SpawnPoints[index % SpawnPoints.Length].position;
            var box = GetComponent<BoxCollider>();
            var size = Vector3.Scale(box.size, transform.lossyScale) * 0.4f;
            var h = StableHash.Mix((ulong)index + 1);
            return transform.TransformPoint(box.center) + new Vector3(((h & 0xFF) / 255f - 0.5f) * size.x * 2f, 0f, (((h >> 8) & 0xFF) / 255f - 0.5f) * size.z * 2f);
        }

        private void ReleaseAll()
        {
            foreach (var a in _active)
            {
                a.Release();
                _pool.Push(a);
            }
            _active.Clear();
        }
    }
}
