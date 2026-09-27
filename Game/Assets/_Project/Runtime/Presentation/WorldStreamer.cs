using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HeroGame.Runtime.Presentation
{
    /// <summary>
    /// Grid-cell world streaming (TDD §11). The city is split into square cells, each an additive scene
    /// named <c>{Prefix}_{x}_{z}</c> (registered in Build Settings or, later, Addressables). Cells within
    /// <see cref="LoadRadius"/> load asynchronously; cells beyond <see cref="UnloadRadius"/> unload.
    /// The gap between radii is hysteresis so the player never thrashes at a boundary. Loads are
    /// prioritised by distance and throttled so a frame never starts more than a few operations.
    /// </summary>
    public sealed class WorldStreamer : MonoBehaviour
    {
        public Transform Focus;
        public string ScenePrefix = "Cell";
        public float CellSize = 256f;
        public float LoadRadius = 600f;
        public float UnloadRadius = 800f;
        public int MaxConcurrentOperations = 2;
        [Tooltip("Speed-based look-ahead: cells ahead of a fast-moving player load early.")]
        public float PredictSeconds = 3f;

        private readonly Dictionary<Vector2Int, AsyncOperation> _pending = new Dictionary<Vector2Int, AsyncOperation>();
        private readonly HashSet<Vector2Int> _loaded = new HashSet<Vector2Int>();
        private readonly List<Vector2Int> _scratch = new List<Vector2Int>();
        private Vector3 _lastFocus;
        private Vector3 _velocity;

        public IReadOnlyCollection<Vector2Int> LoadedCells => _loaded;
        public int PendingCount => _pending.Count;

        private void Update()
        {
            if (Focus == null) return;
            var focus = Focus.position;
            if (Time.deltaTime > 0f) _velocity = Vector3.Lerp(_velocity, (focus - _lastFocus) / Time.deltaTime, 0.1f);
            _lastFocus = focus;
            var predicted = focus + _velocity * PredictSeconds;

            CompleteFinished();
            RequestLoads(focus, predicted);
            RequestUnloads(focus, predicted);
        }

        public static Vector2Int CellOf(Vector3 position, float cellSize)
        {
            return new Vector2Int(Mathf.FloorToInt(position.x / cellSize), Mathf.FloorToInt(position.z / cellSize));
        }

        public string SceneName(Vector2Int cell) => ScenePrefix + "_" + cell.x + "_" + cell.y;

        private void RequestLoads(Vector3 focus, Vector3 predicted)
        {
            _scratch.Clear();
            var r = Mathf.CeilToInt(LoadRadius / CellSize);
            foreach (var origin in new[] { focus, predicted })
            {
                var c = CellOf(origin, CellSize);
                for (var dx = -r; dx <= r; dx++)
                for (var dz = -r; dz <= r; dz++)
                {
                    var cell = new Vector2Int(c.x + dx, c.y + dz);
                    if (_loaded.Contains(cell) || _pending.ContainsKey(cell) || _scratch.Contains(cell)) continue;
                    if (DistanceToCell(origin, cell) <= LoadRadius) _scratch.Add(cell);
                }
            }
            _scratch.Sort((a, b) => DistanceToCell(focus, a).CompareTo(DistanceToCell(focus, b)));
            foreach (var cell in _scratch)
            {
                if (_pending.Count >= MaxConcurrentOperations) break;
                var name = SceneName(cell);
                if (!Application.CanStreamedLevelBeLoaded(name))
                {
                    _loaded.Add(cell); // no content authored for this cell yet: treat as loaded (empty)
                    continue;
                }
                var op = SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive);
                if (op == null) continue;
                op.priority = 0;
                _pending[cell] = op;
            }
        }

        private void RequestUnloads(Vector3 focus, Vector3 predicted)
        {
            _scratch.Clear();
            foreach (var cell in _loaded)
                if (DistanceToCell(focus, cell) > UnloadRadius && DistanceToCell(predicted, cell) > UnloadRadius) _scratch.Add(cell);
            foreach (var cell in _scratch)
            {
                _loaded.Remove(cell);
                var scene = SceneManager.GetSceneByName(SceneName(cell));
                if (scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        private void CompleteFinished()
        {
            _scratch.Clear();
            foreach (var kv in _pending) if (kv.Value.isDone) _scratch.Add(kv.Key);
            foreach (var cell in _scratch)
            {
                _pending.Remove(cell);
                _loaded.Add(cell);
            }
        }

        private float DistanceToCell(Vector3 p, Vector2Int cell)
        {
            var min = new Vector2(cell.x * CellSize, cell.y * CellSize);
            var dx = Mathf.Max(min.x - p.x, 0f, p.x - (min.x + CellSize));
            var dz = Mathf.Max(min.y - p.z, 0f, p.z - (min.y + CellSize));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
