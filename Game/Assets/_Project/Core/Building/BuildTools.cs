using System;
using System.Collections.Generic;

namespace HeroGame.Core.Building
{
    /// <summary>
    /// Engine-independent helpers the build UI uses to turn pointer positions into <see cref="BuildOp"/>s.
    /// Kept in the core so the same maths runs on the server when it re-validates a client's edits.
    /// </summary>
    public static class BuildTools
    {
        /// <summary>Nearest wall on <paramref name="floor"/> within <paramref name="maxDistance"/>, with the snapped offset along it.</summary>
        public static Wall NearestWall(BuildingLayout layout, int floor, float x, float z, float maxDistance, out float offset)
        {
            offset = 0f;
            Wall best = null;
            var bestDistance = maxDistance;
            foreach (var w in layout.Walls)
            {
                if (w.Floor != floor) continue;
                var len = w.Length;
                if (len < 0.01f) continue;
                var dx = (w.X1 - w.X0) / len;
                var dz = (w.Z1 - w.Z0) / len;
                var t = Math.Max(0f, Math.Min(len, (x - w.X0) * dx + (z - w.Z0) * dz));
                var px = w.X0 + dx * t;
                var pz = w.Z0 + dz * t;
                var d = (float)Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                if (d > bestDistance) continue;
                bestDistance = d;
                best = w;
                offset = t;
            }
            return best;
        }

        /// <summary>An opening op centred on the pointer, clamped to fit inside the wall.</summary>
        public static BuildOp OpeningAt(Wall wall, float pointerOffset, OpeningKind kind, float width)
        {
            var offset = BuildingLayout.Snap(Math.Max(0.25f, Math.Min(wall.Length - width - 0.25f, pointerOffset - width / 2f)));
            return new BuildOp { Kind = BuildOpKind.AddOpening, TargetId = wall.Id, OpeningKind = kind, Offset = offset, Width = width };
        }

        /// <summary>Axis-aligned wall from a drag; the longer axis wins so walls stay orthogonal.</summary>
        public static BuildOp WallFromDrag(int floor, float x0, float z0, float x1, float z1, WallKind kind = WallKind.Interior)
        {
            x0 = BuildingLayout.Snap(x0);
            z0 = BuildingLayout.Snap(z0);
            x1 = BuildingLayout.Snap(x1);
            z1 = BuildingLayout.Snap(z1);
            if (Math.Abs(x1 - x0) >= Math.Abs(z1 - z0)) z1 = z0;
            else x1 = x0;
            return new BuildOp { Kind = BuildOpKind.AddWall, Floor = floor, X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, WallKind = kind };
        }

        /// <summary>Rectangular room from a drag, corners snapped to the grid.</summary>
        public static BuildOp RoomFromDrag(int floor, float x0, float z0, float x1, float z1, RoomType type, string name = "")
        {
            var minX = BuildingLayout.Snap(Math.Min(x0, x1));
            var maxX = BuildingLayout.Snap(Math.Max(x0, x1));
            var minZ = BuildingLayout.Snap(Math.Min(z0, z1));
            var maxZ = BuildingLayout.Snap(Math.Max(z0, z1));
            return new BuildOp
            {
                Kind = BuildOpKind.AddRoom, Floor = floor, RoomType = type, Name = name,
                Polygon = new List<float> { minX, minZ, maxX, minZ, maxX, maxZ, minX, maxZ },
            };
        }

        /// <summary>Topmost thing under the pointer for the delete tool: furniture first, then openings, walls, rooms.</summary>
        public static BuildOp RemovalAt(BuildingLayout layout, BuildingValidator catalog, int floor, float x, float z)
        {
            foreach (var f in layout.Furniture)
            {
                if (f.Floor != floor) continue;
                var def = catalog?.Item(f.CatalogId);
                var hw = (def?.Width ?? 0.5f) / 2f;
                var hd = (def?.Depth ?? 0.5f) / 2f;
                if (((int)Math.Round(f.Rotation / 90f) & 1) == 1) { var t = hw; hw = hd; hd = t; }
                if (Math.Abs(x - f.X) <= hw && Math.Abs(z - f.Z) <= hd) return new BuildOp { Kind = BuildOpKind.RemoveFurniture, TargetId = f.Id };
            }
            var wall = NearestWall(layout, floor, x, z, 0.4f, out var offset);
            if (wall != null)
            {
                foreach (var o in layout.Openings)
                    if (o.WallId == wall.Id && offset >= o.Offset && offset <= o.Offset + o.Width) return new BuildOp { Kind = BuildOpKind.RemoveOpening, TargetId = o.Id };
                if (wall.Kind != WallKind.Exterior) return new BuildOp { Kind = BuildOpKind.RemoveWall, TargetId = wall.Id };
            }
            Room smallest = null;
            foreach (var r in layout.Rooms)
                if (r.Floor == floor && r.Contains(x, z) && (smallest == null || r.Area < smallest.Area)) smallest = r;
            return smallest != null ? new BuildOp { Kind = BuildOpKind.RemoveRoom, TargetId = smallest.Id } : null;
        }
    }
}
