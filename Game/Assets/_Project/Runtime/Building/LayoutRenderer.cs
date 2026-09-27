using System.Collections.Generic;
using HeroGame.Core.Building;
using UnityEngine;

namespace HeroGame.Runtime.Building
{
    /// <summary>
    /// Builds greybox geometry for a <see cref="BuildingLayout"/>: wall segments split around openings, a
    /// floor slab per room and a sized placeholder box per furniture item. The transform's origin is the
    /// lot's min corner (lot-local X/Z map directly to local X/Z). Final art replaces the materials and the
    /// furniture boxes with catalog prefabs; the layout data stays the same.
    /// </summary>
    public sealed class LayoutRenderer : MonoBehaviour
    {
        public const float StoreyHeight = 3.4f;
        public const float WallThickness = 0.2f;

        public Material WallMaterial;
        public Material ExteriorWallMaterial;
        public Material FloorMaterial;
        public Material FurnitureMaterial;
        public Material PreviewMaterial;
        /// <summary>Only floors up to this index are drawn (build mode hides upper storeys).</summary>
        public int VisibleFloors = int.MaxValue;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private BuildingValidator _catalog;

        public BuildingLayout Current { get; private set; }

        public void Render(BuildingLayout layout, BuildingValidator catalog, bool preview = false)
        {
            Clear();
            Current = layout;
            _catalog = catalog;
            if (layout == null) return;
            foreach (var room in layout.Rooms) if (room.Floor < VisibleFloors) BuildFloor(room, preview);
            foreach (var wall in layout.Walls) if (wall.Floor < VisibleFloors) BuildWall(layout, wall, preview);
            foreach (var f in layout.Furniture) if (f.Floor < VisibleFloors) BuildFurniture(f, preview);
        }

        public void Clear()
        {
            foreach (var go in _spawned)
            {
                if (go == null) continue;
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
            _spawned.Clear();
        }

        /// <summary>Lot-local grid point under a world-space point, snapped to the build grid.</summary>
        public Vector2 ToLot(Vector3 world)
        {
            var local = transform.InverseTransformPoint(world);
            return new Vector2(BuildingLayout.Snap(local.x), BuildingLayout.Snap(local.z));
        }

        public Vector3 ToWorld(float x, float z, int floor) => transform.TransformPoint(new Vector3(x, floor * StoreyHeight, z));

        private void BuildWall(BuildingLayout layout, Wall wall, bool preview)
        {
            var length = wall.Length;
            if (length < 0.01f) return;
            // Split the wall into solid runs between openings; doors cut full height, windows leave sill and lintel.
            var cuts = new List<Opening>();
            foreach (var o in layout.Openings) if (o.WallId == wall.Id) cuts.Add(o);
            cuts.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var cursor = 0f;
            foreach (var o in cuts)
            {
                var start = Mathf.Clamp(o.Offset, 0f, length);
                var end = Mathf.Clamp(o.Offset + o.Width, 0f, length);
                if (start > cursor) Segment(wall, cursor, start, 0f, wall.Height, preview);
                if (o.Kind == OpeningKind.Window)
                {
                    Segment(wall, start, end, 0f, 0.9f, preview);
                    Segment(wall, start, end, 0.9f + o.Height, wall.Height, preview);
                }
                else if (o.Height < wall.Height)
                {
                    Segment(wall, start, end, o.Height, wall.Height, preview);
                }
                cursor = Mathf.Max(cursor, end);
            }
            if (cursor < length) Segment(wall, cursor, length, 0f, wall.Height, preview);
        }

        private void Segment(Wall wall, float from, float to, float bottom, float top, bool preview)
        {
            if (to - from < 0.01f || top - bottom < 0.01f) return;
            var dir = new Vector3(wall.X1 - wall.X0, 0f, wall.Z1 - wall.Z0).normalized;
            var start = new Vector3(wall.X0, 0f, wall.Z0) + dir * from;
            var centre = start + dir * ((to - from) / 2f) + Vector3.up * (wall.Floor * StoreyHeight + (bottom + top) / 2f);
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Wall_" + wall.Id;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = centre;
            go.transform.localRotation = Quaternion.LookRotation(dir, Vector3.up);
            go.transform.localScale = new Vector3(WallThickness, top - bottom, to - from);
            Apply(go, preview ? PreviewMaterial : wall.Kind == WallKind.Exterior ? ExteriorWallMaterial : WallMaterial);
            _spawned.Add(go);
        }

        private void BuildFloor(Room room, bool preview)
        {
            if (room.VertexCount < 3) return;
            var vertices = new Vector3[room.VertexCount];
            for (var i = 0; i < room.VertexCount; i++) vertices[i] = new Vector3(room.X(i), room.Floor * StoreyHeight + 0.01f, room.Z(i));
            var triangles = Triangulate(room);
            var mesh = new Mesh { name = "Floor_" + room.Id };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("Room_" + room.Id + "_" + room.Name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            Apply(go.AddComponent<MeshRenderer>(), preview ? PreviewMaterial : FloorMaterial);
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            _spawned.Add(go);
        }

        private void BuildFurniture(PlacedFurniture f, bool preview)
        {
            var def = _catalog?.Item(f.CatalogId);
            var size = def != null ? new Vector3(def.Width, def.Height, def.Depth) : Vector3.one * 0.5f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Item_" + f.Id + "_" + f.CatalogId;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(f.X, f.Floor * StoreyHeight + size.y / 2f, f.Z);
            go.transform.localRotation = Quaternion.Euler(0f, f.Rotation, 0f);
            go.transform.localScale = size;
            Apply(go, preview ? PreviewMaterial : FurnitureMaterial);
            _spawned.Add(go);
        }

        /// <summary>Ear clipping for simple (possibly concave) room polygons in either winding.</summary>
        public static int[] Triangulate(Room room)
        {
            var n = room.VertexCount;
            var result = new List<int>();
            var indices = new List<int>();
            for (var i = 0; i < n; i++) indices.Add(i);
            var area = 0f;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                area += room.X(i) * room.Z(j) - room.X(j) * room.Z(i);
            }
            var ccw = area > 0f;
            var guard = n * n;
            while (indices.Count > 3 && guard-- > 0)
            {
                var clipped = false;
                for (var k = 0; k < indices.Count; k++)
                {
                    int a = indices[(k + indices.Count - 1) % indices.Count], b = indices[k], c = indices[(k + 1) % indices.Count];
                    var cross = (room.X(b) - room.X(a)) * (room.Z(c) - room.Z(a)) - (room.Z(b) - room.Z(a)) * (room.X(c) - room.X(a));
                    if (ccw ? cross <= 0f : cross >= 0f) continue;
                    var containsOther = false;
                    foreach (var p in indices)
                    {
                        if (p == a || p == b || p == c) continue;
                        if (InTriangle(room.X(p), room.Z(p), room.X(a), room.Z(a), room.X(b), room.Z(b), room.X(c), room.Z(c))) { containsOther = true; break; }
                    }
                    if (containsOther) continue;
                    AddTriangle(result, a, b, c, ccw);
                    indices.RemoveAt(k);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (indices.Count == 3) AddTriangle(result, indices[0], indices[1], indices[2], ccw);
            return result.ToArray();
        }

        // Unity is left-handed with Y up: a floor facing +Y needs clockwise winding seen from above.
        private static void AddTriangle(List<int> list, int a, int b, int c, bool ccw)
        {
            list.Add(a);
            list.Add(ccw ? c : b);
            list.Add(ccw ? b : c);
        }

        private static bool InTriangle(float px, float pz, float ax, float az, float bx, float bz, float cx, float cz)
        {
            var d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
            var d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
            var d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
            var neg = d1 < 0 || d2 < 0 || d3 < 0;
            var pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static void Apply(GameObject go, Material material) => Apply(go.GetComponent<Renderer>(), material);

        private static void Apply(Renderer renderer, Material material)
        {
            if (renderer != null && material != null) renderer.sharedMaterial = material;
        }

        private void OnDestroy() => Clear();
    }
}
