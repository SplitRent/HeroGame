using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.WorldProps
{
    using HeroGame.Core.World;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// Shows the street furniture near the camera and its damage state (GDD Phase 20). Uses the Blender kit prefab
    /// (Resources/Props/&lt;Mesh&gt;) when present, otherwise a greybox shape (ASSET_TRACKER). Broken props leave
    /// physics debris for a while; a sheared hydrant sprays water; repaired props come back.
    /// </summary>
    public sealed class DestructiblePresenter : MonoBehaviour
    {
        public float ShowRadius = 120f;
        public float HideRadius = 150f;
        public float DebrisLifetime = 25f;

        private readonly Dictionary<int, GameObject> _live = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, PropState> _shown = new Dictionary<int, PropState>();
        private readonly List<PropInstance> _query = new List<PropInstance>();
        private readonly List<int> _remove = new List<int>();
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            _next = Time.unscaledTime + 0.4f;
            var cam = Camera.main;
            if (cam == null) return;
            var c = cam.transform.position;
            var service = session.World.Destructibles;
            var replica = Online.NetworkSession.Replica; // online: the server decides what is broken
            service.Query(new Core.Foundation.WorldPosition(c.x, 0f, c.z), ShowRadius, _query);
            foreach (var p in _query)
            {
                if (!_live.TryGetValue(p.Id, out var go))
                {
                    go = Build(p, service.Kind(p.Kind));
                    _live[p.Id] = go;
                    _shown[p.Id] = PropState.Intact;
                }
                var state = replica != null ? (PropState)replica.PropState(p.Id) : p.State;
                if (_shown[p.Id] != state) Apply(go, p, state, _shown[p.Id]);
                _shown[p.Id] = state;
            }
            _remove.Clear();
            foreach (var kv in _live)
            {
                var pos = kv.Value.transform.position;
                if ((pos.x - c.x) * (pos.x - c.x) + (pos.z - c.z) * (pos.z - c.z) > HideRadius * HideRadius) _remove.Add(kv.Key);
            }
            foreach (var id in _remove)
            {
                Destroy(_live[id]);
                _live.Remove(id);
                _shown.Remove(id);
            }
        }

        private GameObject Build(PropInstance p, DestructibleKind kind)
        {
            GameObject go = null;
            if (kind != null && !string.IsNullOrEmpty(kind.Mesh))
            {
                var prefab = Resources.Load<GameObject>("Props/" + kind.Mesh);
                if (prefab != null) go = Instantiate(prefab);
            }
            if (go == null) go = Greybox(p.Kind);
            go.name = "Prop " + p.Kind + " #" + p.Id;
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(p.X, 0f, p.Z);
            go.transform.rotation = Quaternion.Euler(0f, p.Yaw, 0f);
            if (go.GetComponent<Collider>() == null) go.AddComponent<BoxCollider>();
            go.AddComponent<DestructibleProp>().PropId = p.Id;
            return go;
        }

        private static GameObject Greybox(string kind)
        {
            var root = new GameObject();
            GameObject Part(PrimitiveType type, Vector3 pos, Vector3 scale, Color color)
            {
                var part = GameObject.CreatePrimitive(type);
                Destroy(part.GetComponent<Collider>());
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = pos;
                part.transform.localScale = scale;
                var r = part.GetComponent<Renderer>();
                if (r != null) r.material.color = color;
                return part;
            }
            var grey = new Color(0.35f, 0.36f, 0.38f);
            switch (kind)
            {
                case "street_light":
                    Part(PrimitiveType.Cylinder, new Vector3(0f, 3f, 0f), new Vector3(0.18f, 3f, 0.18f), grey);
                    Part(PrimitiveType.Sphere, new Vector3(0f, 6.1f, 0.6f), new Vector3(0.5f, 0.25f, 0.5f), new Color(1f, 0.95f, 0.7f));
                    break;
                case "traffic_signal":
                    Part(PrimitiveType.Cylinder, new Vector3(0f, 2.5f, 0f), new Vector3(0.2f, 2.5f, 0.2f), grey);
                    Part(PrimitiveType.Cube, new Vector3(0f, 4.6f, 0f), new Vector3(0.4f, 1.1f, 0.4f), new Color(0.1f, 0.1f, 0.1f));
                    break;
                case "fire_hydrant":
                    Part(PrimitiveType.Cylinder, new Vector3(0f, 0.4f, 0f), new Vector3(0.3f, 0.4f, 0.3f), new Color(0.8f, 0.1f, 0.08f));
                    break;
                case "bench":
                    Part(PrimitiveType.Cube, new Vector3(0f, 0.45f, 0f), new Vector3(1.8f, 0.1f, 0.5f), new Color(0.45f, 0.3f, 0.18f));
                    break;
                case "bus_shelter":
                    Part(PrimitiveType.Cube, new Vector3(0f, 2.4f, 0f), new Vector3(3.5f, 0.1f, 1.6f), grey);
                    Part(PrimitiveType.Cube, new Vector3(0f, 1.2f, -0.75f), new Vector3(3.5f, 2.3f, 0.05f), new Color(0.6f, 0.75f, 0.85f, 0.5f));
                    break;
                case "dumpster":
                    Part(PrimitiveType.Cube, new Vector3(0f, 0.65f, 0f), new Vector3(1.9f, 1.3f, 1.1f), new Color(0.12f, 0.35f, 0.18f));
                    break;
                default:
                    Part(PrimitiveType.Cylinder, new Vector3(0f, 0.5f, 0f), new Vector3(0.3f, 0.5f, 0.3f), grey);
                    break;
            }
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, kind == "street_light" || kind == "traffic_signal" ? 2.5f : 0.6f, 0f);
            box.size = kind == "bus_shelter" ? new Vector3(3.5f, 2.5f, 1.6f) : kind == "bench" ? new Vector3(1.8f, 0.9f, 0.5f) : kind == "dumpster" ? new Vector3(1.9f, 1.3f, 1.1f)
                : kind == "street_light" || kind == "traffic_signal" ? new Vector3(0.4f, 5f, 0.4f) : new Vector3(0.4f, 1f, 0.4f);
            return root;
        }

        private void Apply(GameObject go, PropInstance p, PropState state, PropState was)
        {
            switch (state)
            {
                case PropState.Intact:
                    go.transform.rotation = Quaternion.Euler(0f, p.Yaw, 0f);
                    SetVisible(go, true);
                    var spray = go.transform.Find("Water");
                    if (spray != null) Destroy(spray.gameObject);
                    break;
                case PropState.Damaged:
                    go.transform.rotation = Quaternion.Euler(12f, p.Yaw, 0f);
                    break;
                case PropState.Destroyed:
                    SetVisible(go, false);
                    if (was != PropState.Destroyed) Debris(go.transform.position, p.Kind);
                    if (p.Kind == "fire_hydrant") Water(go.transform);
                    break;
            }
        }

        private static void SetVisible(GameObject go, bool visible)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = visible;
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = visible;
        }

        private void Debris(Vector3 at, string kind)
        {
            var pieces = kind == "bus_shelter" ? 7 : 4;
            for (var i = 0; i < pieces; i++)
            {
                var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
                piece.name = "Debris";
                piece.transform.position = at + new Vector3(Random.Range(-0.5f, 0.5f), 0.5f + i * 0.4f, Random.Range(-0.5f, 0.5f));
                piece.transform.localScale = new Vector3(Random.Range(0.15f, 0.5f), Random.Range(0.1f, 0.6f), Random.Range(0.15f, 0.5f));
                var body = piece.AddComponent<Rigidbody>();
                body.mass = 20f;
                body.AddForce(new Vector3(Random.Range(-2f, 2f), Random.Range(1f, 3f), Random.Range(-2f, 2f)), ForceMode.VelocityChange);
                Destroy(piece, DebrisLifetime);
            }
        }

        private static void Water(Transform parent)
        {
            if (parent.Find("Water") != null) return;
            var go = new GameObject("Water");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startSpeed = 7f;
            main.startLifetime = 1.2f;
            main.startSize = 0.15f;
            main.startColor = new Color(0.7f, 0.85f, 1f, 0.8f);
            main.gravityModifier = 1f;
            var emission = ps.emission;
            emission.rateOverTime = 120f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f;
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        }
    }
}
