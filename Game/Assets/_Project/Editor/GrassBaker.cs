using UnityEditor;
using UnityEngine;

namespace HeroGame.Editor
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.World;
    using HeroGame.Runtime.Presentation;

    /// <summary>
    /// Bakes what the <see cref="GrassRenderer"/> needs for a layout: the <see cref="GrassMask"/> (lawn cells, minus
    /// roads with their sidewalks, building footprints, dirt lots and water), a blade colour texture and an instanced,
    /// double-sided blade material for the active pipeline.
    /// </summary>
    public static class GrassBaker
    {
        /// <summary>Upper bound on mask resolution; large maps get coarser cells.</summary>
        public const int MaxCells = 2048;

        public static GrassMask BakeMask(WorldLayout layout, float minX, float minZ, float maxX, float maxZ, string path)
        {
            var cell = Mathf.Max(1f, Mathf.Max(maxX - minX, maxZ - minZ) / MaxCells);
            var w = Mathf.CeilToInt((maxX - minX) / cell);
            var h = Mathf.CeilToInt((maxZ - minZ) / cell);
            var cells = new byte[w * h];
            for (var i = 0; i < cells.Length; i++) cells[i] = 1;

            // Places: buildings and dirt lots clear the lawn; parks become raised lawn.
            var expanded = LayoutExpander.Expand(layout, 0, new IdAllocator(), new Geography());
            foreach (var e in expanded)
            {
                var kind = e.Place.Kind;
                var value = kind == PlaceKind.Park ? (byte)2 : (byte)0;
                var inset = kind == PlaceKind.Park ? 0.4f : -0.6f; // parks keep a mown edge; buildings get a margin
                FillRect(cells, w, h, minX, minZ, cell, e.Place.Position.X, e.Place.Position.Z, e.Width / 2f - inset, e.Depth / 2f - inset, e.RotationY, value);
            }
            // Roads, including their 3 m sidewalks (bridges have none).
            foreach (var road in layout.Roads)
            {
                var half = road.Width / 2f + (road.Kind == "bridge" ? 0.5f : 3.4f);
                for (var i = 0; i + 3 < road.Points.Count; i += 2)
                    FillSegment(cells, w, h, minX, minZ, cell, road.Points[i], road.Points[i + 1], road.Points[i + 2], road.Points[i + 3], half);
            }
            // Water.
            for (var z = 0; z < h; z++)
            for (var x = 0; x < w; x++)
            {
                var i = z * w + x;
                if (cells[i] == 0) continue;
                if (layout.IsWater(minX + (x + 0.5f) * cell, minZ + (z + 0.5f) * cell)) cells[i] = 0;
            }

            var mask = AssetDatabase.LoadAssetAtPath<GrassMask>(path);
            if (mask == null)
            {
                mask = ScriptableObject.CreateInstance<GrassMask>();
                AssetDatabase.CreateAsset(mask, path);
            }
            mask.MinX = minX;
            mask.MinZ = minZ;
            mask.Cell = cell;
            mask.Width = w;
            mask.Height = h;
            mask.Cells = cells;
            EditorUtility.SetDirty(mask);
            return mask;
        }

        private static void FillRect(byte[] cells, int w, int h, float minX, float minZ, float cell, float cx, float cz, float hx, float hz, float rotationY, byte value)
        {
            if (hx <= 0 || hz <= 0) return;
            var rot = Quaternion.Euler(0f, rotationY, 0f);
            var inv = Quaternion.Inverse(rot);
            var r = Mathf.Sqrt(hx * hx + hz * hz);
            var x0 = Mathf.Max(0, Mathf.FloorToInt((cx - r - minX) / cell));
            var x1 = Mathf.Min(w - 1, Mathf.CeilToInt((cx + r - minX) / cell));
            var z0 = Mathf.Max(0, Mathf.FloorToInt((cz - r - minZ) / cell));
            var z1 = Mathf.Min(h - 1, Mathf.CeilToInt((cz + r - minZ) / cell));
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
            {
                var local = inv * new Vector3(minX + (x + 0.5f) * cell - cx, 0f, minZ + (z + 0.5f) * cell - cz);
                if (Mathf.Abs(local.x) <= hx && Mathf.Abs(local.z) <= hz)
                {
                    var i = z * w + x;
                    cells[i] = value == 0 ? (byte)0 : cells[i] == 0 ? (byte)0 : value;
                }
            }
        }

        private static void FillSegment(byte[] cells, int w, int h, float minX, float minZ, float cell, float ax, float az, float bx, float bz, float half)
        {
            var x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(ax, bx) - half - minX) / cell));
            var x1 = Mathf.Min(w - 1, Mathf.CeilToInt((Mathf.Max(ax, bx) + half - minX) / cell));
            var z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(az, bz) - half - minZ) / cell));
            var z1 = Mathf.Min(h - 1, Mathf.CeilToInt((Mathf.Max(az, bz) + half - minZ) / cell));
            var d = new Vector2(bx - ax, bz - az);
            var len2 = Mathf.Max(1e-4f, d.sqrMagnitude);
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
            {
                var p = new Vector2(minX + (x + 0.5f) * cell - ax, minZ + (z + 0.5f) * cell - az);
                var t = Mathf.Clamp01(Vector2.Dot(p, d) / len2);
                if ((p - d * t).sqrMagnitude <= half * half) cells[z * w + x] = 0;
            }
        }

        /// <summary>Blade colours: eight hue bands across U (one per blade in a clump), darker roots to sunlit tips along V.</summary>
        public static Texture2D BladeTexture(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;
            const int w = 64, h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "T_GrassBlade_BC", wrapMode = TextureWrapMode.Clamp };
            var bands = new[]
            {
                new Color(0.20f, 0.34f, 0.10f), new Color(0.25f, 0.38f, 0.12f), new Color(0.18f, 0.30f, 0.09f), new Color(0.30f, 0.40f, 0.14f),
                new Color(0.22f, 0.36f, 0.13f), new Color(0.36f, 0.40f, 0.18f), new Color(0.16f, 0.29f, 0.10f), new Color(0.27f, 0.35f, 0.12f),
            };
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var t = y / (h - 1f);
                var baseColour = bands[Mathf.Min(bands.Length - 1, x * bands.Length / w)];
                var tipColour = Color.Lerp(baseColour, new Color(0.62f, 0.62f, 0.32f), 0.22f);
                var c = Color.Lerp(baseColour * 0.55f, tipColour, Mathf.SmoothStep(0f, 1f, t));
                c.a = 1f;
                tex.SetPixel(x, y, c);
            }
            tex.Apply(true);
            AssetDatabase.CreateAsset(tex, path);
            return tex;
        }

        /// <summary>Double-sided, instanced blade material (HDRP/Lit, URP/Lit or Standard).</summary>
        public static Material BladeMaterial(string path, Texture2D blade)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (m == null)
            {
                m = new Material(shader) { name = "M_Grass_Blades" };
                AssetDatabase.CreateAsset(m, path);
            }
            m.enableInstancing = true;
            foreach (var p in new[] { "_BaseColorMap", "_BaseMap", "_MainTex" })
                if (m.HasProperty(p)) m.SetTexture(p, blade);
            foreach (var p in new[] { "_BaseColor", "_Color" })
                if (m.HasProperty(p)) m.SetColor(p, Color.white);
            foreach (var (p, v) in new[] { ("_Smoothness", 0.28f), ("_Glossiness", 0.28f), ("_Metallic", 0f), ("_DoubleSidedEnable", 1f), ("_CullMode", 0f), ("_CullModeForward", 0f), ("_Cull", 0f) })
                if (m.HasProperty(p)) m.SetFloat(p, v);
            m.doubleSidedGI = true;
            var hdMaterial = PipelineReflection.FindType(PipelineReflection.Hd + "HDMaterial");
            hdMaterial?.GetMethod("ValidateMaterial", new[] { typeof(Material) })?.Invoke(null, new object[] { m });
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
