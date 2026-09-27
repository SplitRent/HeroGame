using System.Collections.Generic;
using HeroGame.Core.Traffic;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.Vehicles
{
    /// <summary>
    /// Ambient traffic (GDD §35): kinematic cars placed and moved along road edges from the statistical
    /// <see cref="TrafficModel"/>. Counts follow the hour, weather and server density; positions are
    /// deterministic, so cars advance smoothly between refreshes instead of spawning in front of the player.
    /// Cars keep right, stop for the car ahead and slow for the player.
    /// </summary>
    public sealed class TrafficPresenter : MonoBehaviour
    {
        public Transform Observer;
        public GameObject CarPrefab;
        public float Radius = 250f;
        public int Budget = 40;
        public float Refresh = 3f;
        public float LaneOffset = 2.2f;

        private sealed class Car
        {
            public GameObject Go;
            public TrafficSpawn Spawn;
        }

        private readonly Dictionary<ulong, Car> _cars = new Dictionary<ulong, Car>();
        private readonly Stack<GameObject> _pool = new Stack<GameObject>();
        private float _timer;

        private void Update()
        {
            if (Observer == null || CarPrefab == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var world = session.World;
            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                _timer = Refresh;
                Sync(world.Traffic.Materialize(Observer.position.ToWorld(), Radius, world.Clock.Now, world.Weather.Effects, Budget));
            }
            foreach (var car in _cars.Values) Advance(world.Roads, car, Time.deltaTime);
        }

        private void Sync(List<TrafficSpawn> spawns)
        {
            var wanted = new HashSet<ulong>();
            foreach (var s in spawns)
            {
                wanted.Add(s.Seed);
                if (_cars.ContainsKey(s.Seed)) continue;
                var go = _pool.Count > 0 ? _pool.Pop() : Instantiate(CarPrefab, transform);
                go.SetActive(true);
                _cars[s.Seed] = new Car { Go = go, Spawn = s };
            }
            var remove = new List<ulong>();
            foreach (var kv in _cars) if (!wanted.Contains(kv.Key)) remove.Add(kv.Key);
            foreach (var key in remove)
            {
                _cars[key].Go.SetActive(false);
                _pool.Push(_cars[key].Go);
                _cars.Remove(key);
            }
        }

        private void Advance(RoadNetwork roads, Car car, float dt)
        {
            var e = roads.Edges[car.Spawn.Edge];
            var a = roads.Nodes[car.Spawn.Reverse ? e.To : e.From].Position.ToVector3();
            var b = roads.Nodes[car.Spawn.Reverse ? e.From : e.To].Position.ToVector3();
            var dir = (b - a).normalized;
            var right = Vector3.Cross(Vector3.up, dir);
            var along = car.Spawn.Reverse ? 1f - car.Spawn.T : car.Spawn.T;
            var pos = Vector3.Lerp(a, b, along) + right * (LaneOffset + car.Spawn.Lane * 3.2f);

            // Yield to the player standing in the lane.
            var toPlayer = Observer.position - pos;
            var blocked = Vector3.Dot(toPlayer, dir) > 0f && Vector3.Dot(toPlayer, dir) < 12f && Vector3.Cross(dir, toPlayer).magnitude < 2f;
            if (!blocked)
            {
                // Cars move at real road speed in real time (the clock's time scale only compresses the day).
                along += car.Spawn.SpeedMs * dt / Mathf.Max(1f, e.Length);
                if (along >= 1f) along -= 1f; // loop until the next refresh re-evaluates the edge
                car.Spawn.T = car.Spawn.Reverse ? 1f - along : along;
            }
            car.Go.transform.SetPositionAndRotation(pos + Vector3.up * 0.5f, Quaternion.LookRotation(dir));
        }
    }
}
