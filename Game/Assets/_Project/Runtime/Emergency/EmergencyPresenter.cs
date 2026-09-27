using System.Collections.Generic;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Traffic;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.Emergency
{
    /// <summary>
    /// Shows emergency units near the player driving their dispatched routes (lights flashing while responding)
    /// and parked at scenes. Positions come from the core dispatcher's trip times, so what you see is where the
    /// simulation says the unit is; nothing is invented locally.
    /// </summary>
    public sealed class EmergencyPresenter : MonoBehaviour
    {
        public Transform Observer;
        public GameObject PoliceCar;
        public GameObject FireEngine;
        public GameObject Ambulance;
        public float Radius = 350f;
        public float LightFlashHz = 2.5f;

        private readonly Dictionary<EntityId, GameObject> _shown = new Dictionary<EntityId, GameObject>();
        private readonly List<EntityId> _stale = new List<EntityId>();

        private void Update()
        {
            if (Observer == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var w = session.World;
            var now = w.Clock.Now.TotalSeconds;
            var observer = Observer.position;
            _stale.Clear();
            _stale.AddRange(_shown.Keys);
            foreach (var u in w.Emergency.Units)
            {
                if (u.Status == UnitStatus.Available) continue; // parked in the station bay
                var pos = PositionOnRoute(w.Roads, u, now, out var heading);
                if ((pos - observer).sqrMagnitude > Radius * Radius) continue;
                _stale.Remove(u.Vehicle);
                if (!_shown.TryGetValue(u.Vehicle, out var go))
                {
                    var prefab = u.Service == EmergencyService.Police ? PoliceCar : u.Service == EmergencyService.Fire ? FireEngine : Ambulance;
                    if (prefab == null) continue;
                    go = Instantiate(prefab, transform);
                    go.name = u.CallSign;
                    _shown[u.Vehicle] = go;
                }
                go.transform.SetPositionAndRotation(Vector3.Lerp(go.transform.position, pos, Time.deltaTime * 6f), Quaternion.Slerp(go.transform.rotation, heading, Time.deltaTime * 6f));
                var responding = u.Status == UnitStatus.EnRoute || u.Status == UnitStatus.Transporting || u.Status == UnitStatus.OnScene;
                var lights = go.GetComponentInChildren<Light>();
                if (lights != null) lights.enabled = responding && Mathf.Repeat(Time.time * LightFlashHz, 1f) < 0.5f;
            }
            foreach (var id in _stale)
            {
                Destroy(_shown[id]);
                _shown.Remove(id);
            }
        }

        /// <summary>Walks the unit's road route by elapsed trip fraction (falls back to a straight line).</summary>
        private static Vector3 PositionOnRoute(RoadNetwork roads, EmergencyUnit u, long now, out Quaternion heading)
        {
            heading = Quaternion.identity;
            var p = u.PositionAt(now);
            var fallback = new Vector3(p.X, 0f, p.Z);
            if (u.Status == UnitStatus.OnScene || roads == null || u.Route == null || u.Route.Count < 2 || u.ArriveSecond <= u.DepartSecond) return fallback;
            var points = new List<Vector3> { new Vector3(u.From.X, 0f, u.From.Z) };
            foreach (var n in u.Route) points.Add(new Vector3(roads.Nodes[n].Position.X, 0f, roads.Nodes[n].Position.Z));
            points.Add(new Vector3(u.To.X, 0f, u.To.Z));
            var total = 0f;
            for (var i = 1; i < points.Count; i++) total += Vector3.Distance(points[i - 1], points[i]);
            var t = Mathf.Clamp01((now - u.DepartSecond) / (float)(u.ArriveSecond - u.DepartSecond)) * total;
            for (var i = 1; i < points.Count; i++)
            {
                var seg = Vector3.Distance(points[i - 1], points[i]);
                if (t <= seg && seg > 0.01f)
                {
                    var dir = (points[i] - points[i - 1]).normalized;
                    heading = Quaternion.LookRotation(dir, Vector3.up);
                    return points[i - 1] + dir * t + Quaternion.Euler(0, 90, 0) * dir * 2.2f; // keep right
                }
                t -= seg;
            }
            return points[points.Count - 1];
        }
    }
}
