using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HeroGame.Runtime.Presentation
{
    using HeroGame.Core.Presentation;
    using HeroGame.Runtime.UI;

    /// <summary>
    /// Individual grass blades on every lawn the <see cref="GrassMask"/> allows, drawn with GPU instancing around the
    /// camera. The world is split into 16 m chunks; each chunk's clumps are generated once, deterministically, in a
    /// shuffled order, so a distance ring simply draws a shorter prefix (full density near, thinning out to the grass
    /// distance setting). Clumps within <see cref="SwayRadius"/> lean with the wind every frame by shearing their
    /// instance matrices (no custom shader needed); the rest lean with the average wind.
    /// </summary>
    public sealed class GrassRenderer : MonoBehaviour
    {
        public GrassMask Mask;
        public Material Material;
        public Transform Observer;
        /// <summary>Clumps per square metre at density 1 (each clump is <see cref="BladesPerClump"/> blades).</summary>
        public float ClumpsPerSquareMetre = 5f;
        public float SwayRadius = 22f;

        public const float ChunkSize = 16f;
        public const int BladesPerClump = 9;
        private const int Batch = 1000;

        private sealed class Chunk
        {
            public Vector3[] Positions;
            public float[] Yaw;
            public float[] Scale;
            public Matrix4x4[] Matrices;
            public int Count;
        }

        private readonly Dictionary<long, Chunk> _chunks = new Dictionary<long, Chunk>();
        private readonly List<long> _stale = new List<long>();
        private Mesh _clump;
        private Matrix4x4[] _scratch = new Matrix4x4[Batch];
        private float _density = -1f;

        private void OnEnable()
        {
            _clump = BuildClump();
            SettingsService.Changed += OnSettings;
        }

        private void OnDisable()
        {
            SettingsService.Changed -= OnSettings;
            _chunks.Clear();
        }

        private void OnSettings(GameSettings s)
        {
            if (!Mathf.Approximately(s.GrassDensity, _density)) _chunks.Clear(); // regenerate at the new density
        }

        private void Update()
        {
            if (Mask == null || Material == null || _clump == null) return;
            var observer = Observer != null ? Observer : Camera.main != null ? Camera.main.transform : null;
            if (observer == null) return;
            var s = SettingsService.Current;
            _density = s.GrassDensity;
            if (s.GrassDensity <= 0.01f) return;
            var radius = s.GrassDistance;
            var eye = observer.position;
            var wind = Wind(Time.time);
            var rp = new RenderParams(Material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true, layer = gameObject.layer };

            var c0x = Mathf.FloorToInt((eye.x - radius) / ChunkSize);
            var c1x = Mathf.FloorToInt((eye.x + radius) / ChunkSize);
            var c0z = Mathf.FloorToInt((eye.z - radius) / ChunkSize);
            var c1z = Mathf.FloorToInt((eye.z + radius) / ChunkSize);
            for (var cx = c0x; cx <= c1x; cx++)
            for (var cz = c0z; cz <= c1z; cz++)
            {
                var centre = new Vector3((cx + 0.5f) * ChunkSize, 0f, (cz + 0.5f) * ChunkSize);
                var d = Vector2.Distance(new Vector2(centre.x, centre.z), new Vector2(eye.x, eye.z)) - ChunkSize * 0.7f;
                if (d > radius) continue;
                var chunk = Get(cx, cz, s.GrassDensity);
                if (chunk.Count == 0) continue;
                // Near: everything; then thinning to a quarter at the edge of the grass distance.
                var t = Mathf.Clamp01((d - 12f) / Mathf.Max(1f, radius - 12f));
                var count = Mathf.Max(1, Mathf.RoundToInt(chunk.Count * Mathf.Lerp(1f, 0.25f, t)));
                if (d < SwayRadius) DrawSwaying(rp, chunk, count, wind, Time.time);
                else
                    for (var start = 0; start < count; start += Batch)
                        Graphics.RenderMeshInstanced(rp, _clump, 0, chunk.Matrices, Mathf.Min(Batch, count - start), start);
            }

            // Forget chunks far behind (memory stays bounded while travelling).
            if (_chunks.Count > 600)
            {
                _stale.Clear();
                foreach (var kv in _chunks)
                {
                    var x = (int)(kv.Key >> 32);
                    var z = (int)(kv.Key & 0xFFFFFFFF);
                    if (Mathf.Abs(x * ChunkSize - eye.x) > radius * 2f || Mathf.Abs(z * ChunkSize - eye.z) > radius * 2f) _stale.Add(kv.Key);
                }
                foreach (var k in _stale) _chunks.Remove(k);
            }
        }

        private static Vector2 Wind(float time)
        {
            // A steady Gulf breeze with slow gusts.
            var gust = 0.6f + 0.4f * Mathf.Sin(time * 0.37f) * Mathf.Sin(time * 0.11f + 1.3f);
            return new Vector2(0.8f, 0.45f).normalized * (0.1f + 0.14f * gust);
        }

        private void DrawSwaying(RenderParams rp, Chunk chunk, int count, Vector2 wind, float time)
        {
            for (var start = 0; start < count; start += Batch)
            {
                var n = Mathf.Min(Batch, count - start);
                for (var i = 0; i < n; i++)
                {
                    var k = start + i;
                    var p = chunk.Positions[k];
                    var wave = Mathf.Sin(time * 2.1f + p.x * 0.35f + p.z * 0.27f) * 0.5f + Mathf.Sin(time * 3.3f + p.x * 0.9f) * 0.2f;
                    _scratch[i] = Blade(p, chunk.Yaw[k], chunk.Scale[k], wind * (1f + wave));
                }
                Graphics.RenderMeshInstanced(rp, _clump, 0, _scratch, n);
            }
        }

        /// <summary>Instance matrix: yaw and scale, with the blade tips sheared along the wind (height-proportional bend).</summary>
        private static Matrix4x4 Blade(Vector3 p, float yaw, float scale, Vector2 lean)
        {
            var c = Mathf.Cos(yaw) * scale;
            var s = Mathf.Sin(yaw) * scale;
            var m = new Matrix4x4();
            m.m00 = c; m.m01 = lean.x * scale; m.m02 = s; m.m03 = p.x;
            m.m10 = 0; m.m11 = scale; m.m12 = 0; m.m13 = p.y;
            m.m20 = -s; m.m21 = lean.y * scale; m.m22 = c; m.m23 = p.z;
            m.m33 = 1f;
            return m;
        }

        private Chunk Get(int cx, int cz, float density)
        {
            var key = ((long)cx << 32) | (uint)cz;
            if (_chunks.TryGetValue(key, out var chunk)) return chunk;
            chunk = Generate(cx, cz, density);
            _chunks[key] = chunk;
            return chunk;
        }

        private Chunk Generate(int cx, int cz, float density)
        {
            var target = Mathf.RoundToInt(ChunkSize * ChunkSize * ClumpsPerSquareMetre * density);
            var positions = new List<Vector3>(target);
            var yaw = new List<float>(target);
            var scale = new List<float>(target);
            // Deterministic per chunk (the same lawn every visit, on every machine).
            var rng = new System.Random(unchecked(cx * 73856093 ^ cz * 19349663 ^ 0x6A11));
            for (var i = 0; i < target; i++)
            {
                var x = (cx + (float)rng.NextDouble()) * ChunkSize;
                var z = (cz + (float)rng.NextDouble()) * ChunkSize;
                var cell = Mask.At(x, z);
                var y = (float)rng.NextDouble();
                var r = (float)rng.NextDouble();
                var sc = (float)rng.NextDouble();
                if (cell == 0) continue;
                positions.Add(new Vector3(x, cell == 2 ? GrassMask.ParkHeight : 0f, z));
                yaw.Add(y * Mathf.PI * 2f);
                scale.Add(0.75f + 0.55f * sc * sc + (r < 0.08f ? 0.4f : 0f)); // the odd tall tuft
            }
            var chunk = new Chunk { Positions = positions.ToArray(), Yaw = yaw.ToArray(), Scale = scale.ToArray(), Count = positions.Count };
            chunk.Matrices = new Matrix4x4[chunk.Count];
            var still = Wind(0f);
            for (var i = 0; i < chunk.Count; i++) chunk.Matrices[i] = Blade(chunk.Positions[i], chunk.Yaw[i], chunk.Scale[i], still);
            return chunk;
        }

        /// <summary>
        /// One clump of curved, tapering blades (root at y = 0). UV.x picks a colour band per blade (the blade texture
        /// varies hue across U), UV.y runs root to tip for the darker-root gradient. Normals lean up for soft lighting.
        /// </summary>
        public static Mesh BuildClump()
        {
            var rng = new System.Random(4242);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (var b = 0; b < BladesPerClump; b++)
            {
                var angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (float)rng.NextDouble() * 0.12f;
                var facing = (float)rng.NextDouble() * Mathf.PI * 2f;
                var side = new Vector3(Mathf.Cos(facing), 0f, Mathf.Sin(facing));
                var forward = Vector3.Cross(side, Vector3.up);
                var height = 0.28f + (float)rng.NextDouble() * 0.3f;
                var width = 0.022f + (float)rng.NextDouble() * 0.014f;
                var curl = 0.05f + (float)rng.NextDouble() * 0.12f;
                var band = (b % 8 + 0.5f) / 8f;
                var start = vertices.Count;
                const int segments = 3;
                for (var k = 0; k <= segments; k++)
                {
                    var t = k / (float)segments;
                    var centre = offset + Vector3.up * (height * t) + forward * (curl * t * t);
                    var w = width * (1f - t * 0.85f);
                    var normal = (forward * -0.4f + Vector3.up * 0.9f).normalized;
                    vertices.Add(centre - side * w);
                    vertices.Add(centre + side * w);
                    normals.Add(normal);
                    normals.Add(normal);
                    uvs.Add(new Vector2(band - 0.04f, t));
                    uvs.Add(new Vector2(band + 0.04f, t));
                }
                var tip = offset + Vector3.up * (height * 1.12f) + forward * (curl * 1.3f);
                vertices.Add(tip);
                normals.Add(Vector3.up);
                uvs.Add(new Vector2(band, 1f));
                for (var k = 0; k < segments; k++)
                {
                    var i0 = start + k * 2;
                    triangles.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3 });
                }
                var last = start + segments * 2;
                triangles.AddRange(new[] { last, last + 2, last + 1 });
            }
            var mesh = new Mesh { name = "Grass Clump" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            mesh.bounds = new Bounds(new Vector3(0f, 0.4f, 0f), new Vector3(1.2f, 1.2f, 1.2f)); // room for the wind lean
            return mesh;
        }
    }
}
