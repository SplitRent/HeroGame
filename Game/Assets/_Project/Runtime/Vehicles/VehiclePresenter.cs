using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Vehicles;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.Vehicles
{
    /// <summary>
    /// Materialises persistent parked/street vehicles near the player (NPC household cars, the player's own
    /// cars, service fleets) and writes their final position back when they leave the bubble. Vehicles in
    /// garages or impound are never spawned on the street.
    /// </summary>
    public sealed class VehiclePresenter : MonoBehaviour
    {
        public Transform Observer;
        public VehicleController CarPrefab;
        public float Radius = 180f;
        public float ReleaseRadius = 230f;
        public float Interval = 2f;
        public int MaxVehicles = 60;

        private readonly Dictionary<EntityId, VehicleController> _active = new Dictionary<EntityId, VehicleController>();
        private readonly Stack<VehicleController> _pool = new Stack<VehicleController>();
        private readonly List<EntityId> _release = new List<EntityId>();
        private float _timer;

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f || Observer == null || CarPrefab == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            _timer = Interval;
            var service = session.World.Vehicles;
            var here = Observer.position;

            _release.Clear();
            foreach (var kv in _active)
            {
                var entry = kv.Value.GetComponent<VehicleEntry>();
                if (entry != null && entry.Occupied) continue; // never despawn a car someone is driving
                if ((kv.Value.transform.position - here).sqrMagnitude > ReleaseRadius * ReleaseRadius) _release.Add(kv.Key);
            }
            foreach (var id in _release)
            {
                var car = _active[id];
                var record = service.Get(id);
                if (record != null)
                {
                    record.Position = car.transform.position.ToWorld();
                    record.Heading = car.transform.eulerAngles.y;
                }
                _active.Remove(id);
                car.gameObject.SetActive(false);
                _pool.Push(car);
            }

            foreach (var v in service.All)
            {
                if (_active.Count >= MaxVehicles) break;
                if (_active.ContainsKey(v.Id) || v.LocationKind != VehicleLocationKind.Street) continue;
                var pos = v.Position.ToVector3();
                if ((pos - here).sqrMagnitude > Radius * Radius) continue;
                var car = _pool.Count > 0 ? _pool.Pop() : Instantiate(CarPrefab, transform);
                car.transform.SetPositionAndRotation(pos + Vector3.up * 0.6f, Quaternion.Euler(0f, v.Heading, 0f));
                car.gameObject.SetActive(true);
                car.name = v.Plate;
                car.Bind(v, service);
                Paint(car, v.ColorHex);
                _active[v.Id] = car;
            }
        }

        private static void Paint(VehicleController car, string hex)
        {
            var body = car.transform.Find("Body");
            var renderer = body != null ? body.GetComponent<Renderer>() : null;
            if (renderer == null) return;
            var block = new MaterialPropertyBlock();
            var color = UnityConversions.ParseHex(hex, Color.gray);
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }
    }
}
