using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Editor
{
    /// <summary>
    /// Meshes for ground surfaces (roads, sidewalks, curbs, lots, the ground plane) whose UVs are in real metres
    /// divided by <see cref="Tile"/>, the tiling of every kit texture. A scaled Unity cube stretches its texture over
    /// the whole object; these keep asphalt, slabs and lawn at true scale and continuous with the buildings. Box UVs
    /// follow the object's own axes, so sidewalk joints run along the street.
    /// </summary>
    public static class SurfaceMeshes
    {
        /// <summary>Metres covered by one texture tile (matches Tools/Blender/hg_pipeline/textures.py TILE_METRES).</summary>
        public const float Tile = 2f;

        private static readonly Dictionary<string, Mesh> Boxes = new Dictionary<string, Mesh>();

        /// <summary>Drops cached meshes (call at the start of each scene build).</summary>
        public static void Reset() => Boxes.Clear();

        /// <summary>A box of <paramref name="size"/> metres centred on the origin, box-projected UVs at true scale (shared per size).</summary>
        public static Mesh Box(Vector3 size)
        {
            var key = Mathf.RoundToInt(size.x * 100) + "x" + Mathf.RoundToInt(size.y * 100) + "x" + Mathf.RoundToInt(size.z * 100);
            if (Boxes.TryGetValue(key, out var cached)) return cached;
            var h = size * 0.5f;
            var vertices = new List<Vector3>(24);
            var normals = new List<Vector3>(24);
            var uvs = new List<Vector2>(24);
            var triangles = new List<int>(36);
            void Face(Vector3 n, Vector3 u, Vector3 v)
            {
                // Corners c ± u ± v, where c is the face centre; UVs are the corner's metres along u and v.
                var c = Vector3.Scale(n, h);
                var hu = Vector3.Scale(u, h);
                var hv = Vector3.Scale(v, h);
                var start = vertices.Count;
                foreach (var (su, sv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    var p = c + hu * su + hv * sv;
                    vertices.Add(p);
                    normals.Add(n);
                    uvs.Add(new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)) / Tile);
                }
                // Wind so the face points along n (Unity: clockwise seen from the front). The corners run
                // counter-clockwise seen from n when u × v = -n (e.g. the top face: right × forward = down).
                if (Vector3.Dot(Vector3.Cross(u, v), n) < 0) triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
                else triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            Face(Vector3.up, Vector3.right, Vector3.forward);
            Face(Vector3.down, Vector3.right, Vector3.forward);
            Face(Vector3.right, Vector3.forward, Vector3.up);
            Face(Vector3.left, Vector3.forward, Vector3.up);
            Face(Vector3.forward, Vector3.right, Vector3.up);
            Face(Vector3.back, Vector3.right, Vector3.up);
            var mesh = new Mesh { name = "Surface " + key };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            Boxes[key] = mesh;
            return mesh;
        }

        /// <summary>A flat rectangle on XZ in world coordinates with UVs from world position (so neighbouring pieces continue).</summary>
        public static Mesh WorldQuad(float minX, float minZ, float maxX, float maxZ, float y)
        {
            var mesh = new Mesh { name = "Ground" };
            mesh.SetVertices(new List<Vector3> { new Vector3(minX, y, minZ), new Vector3(minX, y, maxZ), new Vector3(maxX, y, maxZ), new Vector3(maxX, y, minZ) });
            mesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(minX, minZ) / Tile, new Vector2(minX, maxZ) / Tile, new Vector2(maxX, maxZ) / Tile, new Vector2(maxX, minZ) / Tile });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>World-position UVs for an existing flat mesh (water polygons).</summary>
        public static void WorldUvs(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var uvs = new Vector2[vertices.Length];
            for (var i = 0; i < vertices.Length; i++) uvs[i] = new Vector2(vertices[i].x, vertices[i].z) / Tile;
            mesh.uv = uvs;
            mesh.RecalculateTangents();
        }

        /// <summary>Road paint for one straight segment of <paramref name="length"/> × <paramref name="width"/> (local, centred, +Z along the road).</summary>
        public static Mesh Markings(float width, float length, bool avenue, float y, out Mesh yellow)
        {
            var white = new List<Vector3>();
            var gold = new List<Vector3>();
            void Strip(List<Vector3> into, float x, float z0, float z1, float w)
            {
                into.Add(new Vector3(x - w / 2, y, z0));
                into.Add(new Vector3(x - w / 2, y, z1));
                into.Add(new Vector3(x + w / 2, y, z1));
                into.Add(new Vector3(x + w / 2, y, z0));
            }
            var half = length / 2f - width / 2f; // stop short of the intersection overlap
            if (half > 1f)
            {
                // Edge lines.
                foreach (var side in new[] { -1f, 1f }) Strip(white, side * (width / 2f - 0.35f), -half, half, 0.12f);
                if (avenue)
                {
                    // Double solid yellow centre line.
                    Strip(gold, -0.12f, -half, half, 0.1f);
                    Strip(gold, 0.12f, -half, half, 0.1f);
                }
                else
                {
                    // Dashed yellow centre line: 3 m dashes every 9 m.
                    for (var z = -half; z + 3f <= half; z += 9f) Strip(gold, 0f, z, z + 3f, 0.12f);
                }
            }
            yellow = Build("Centre line", gold);
            return Build("Edge lines", white);
        }

        private static Mesh Build(string name, List<Vector3> quads)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(quads);
            var normals = new List<Vector3>(quads.Count);
            var uvs = new List<Vector2>(quads.Count);
            var triangles = new List<int>(quads.Count / 4 * 6);
            for (var i = 0; i < quads.Count; i += 4)
            {
                for (var k = 0; k < 4; k++)
                {
                    normals.Add(Vector3.up);
                    uvs.Add(new Vector2(quads[i + k].x, quads[i + k].z) / Tile);
                }
                triangles.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
