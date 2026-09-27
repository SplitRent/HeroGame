using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;

namespace HeroGame.Core.Building
{
    /// <summary>
    /// Keeps player construction architecturally believable (GDD §21–22): walls on the grid and inside the lot,
    /// rooms enclosed by walls, every room reachable from an exterior door, egress for large commercial spaces,
    /// accessible entrances, maximum unsupported spans, upper floors supported, utilities where fixtures need
    /// them, furniture inside rooms without overlapping or blocking doors, and the target business's needs.
    /// Errors block a build; warnings are advice.
    /// </summary>
    public sealed class BuildingValidator
    {
        public const float Grid = 0.25f;
        public const float MaxSpan = 12f;
        public const float DoorClearance = 0.9f;
        public const float AccessibleDoorWidth = 0.9f;
        public const float SecondExitArea = 150f;
        private const float Tolerance = 0.05f;

        private readonly Dictionary<string, FurnitureDefinition> _catalog = new Dictionary<string, FurnitureDefinition>();

        public BuildingValidator(IEnumerable<FurnitureDefinition> catalog)
        {
            foreach (var f in catalog) _catalog[f.Id] = f;
        }

        public FurnitureDefinition Item(string id) => id != null && _catalog.TryGetValue(id, out var f) ? f : null;
        public IEnumerable<FurnitureDefinition> Catalog => _catalog.Values;

        public ValidationReport Validate(BuildingLayout layout, PropertyRecord property, bool commercial, BusinessRequirement business = null)
        {
            var r = new ValidationReport();
            ValidateWalls(layout, r);
            ValidateRooms(layout, r);
            ValidateAccess(layout, r, commercial);
            ValidateStructure(layout, r);
            ValidateFurniture(layout, property, r);
            if (business != null) ValidateBusiness(layout, property, business, r);
            return r;
        }

        private static bool OnGrid(float v) => Math.Abs(v / Grid - Math.Round(v / Grid)) < 1e-3;

        private static void ValidateWalls(BuildingLayout l, ValidationReport r)
        {
            foreach (var w in l.Walls)
            {
                var path = "wall " + w.Id;
                if (!OnGrid(w.X0) || !OnGrid(w.Z0) || !OnGrid(w.X1) || !OnGrid(w.Z1)) r.Error(path, "Wall endpoints must snap to the 0.25 m grid.");
                if (w.Length < 0.5f) r.Error(path, "Wall shorter than 0.5 m.");
                if (w.X0 != w.X1 && w.Z0 != w.Z1) r.Error(path, "Walls must be axis-aligned (diagonals are not supported by the kit).");
                if (Math.Min(w.X0, w.X1) < -Tolerance || Math.Max(w.X0, w.X1) > l.LotWidth + Tolerance ||
                    Math.Min(w.Z0, w.Z1) < -Tolerance || Math.Max(w.Z0, w.Z1) > l.LotDepth + Tolerance)
                    r.Error(path, "Wall extends beyond the lot boundary.");
                if (w.Floor < 0 || w.Floor >= l.Floors) r.Error(path, "Wall on a floor the building does not have.");
                if (w.Height < 2.4f || w.Height > 8f) r.Error(path, "Wall height must be 2.4–8 m.");
            }
            foreach (var o in l.Openings)
            {
                var w = l.FindWall(o.WallId);
                if (w == null)
                {
                    r.Error("opening " + o.Id, "Opening on a missing wall.");
                    continue;
                }
                if (o.Offset < 0.1f || o.Offset + o.Width > w.Length - 0.1f) r.Error("opening " + o.Id, "Opening does not fit on its wall (keep 10 cm from corners).");
                if (o.Height > w.Height - 0.2f) r.Error("opening " + o.Id, "Opening taller than its wall allows.");
            }
            for (var i = 0; i < l.Openings.Count; i++)
            for (var j = i + 1; j < l.Openings.Count; j++)
            {
                var a = l.Openings[i];
                var b = l.Openings[j];
                if (a.WallId == b.WallId && a.Offset < b.Offset + b.Width && b.Offset < a.Offset + a.Width)
                    r.Error("opening " + a.Id, "Overlaps opening " + b.Id + ".");
            }
        }

        private static void ValidateRooms(BuildingLayout l, ValidationReport r)
        {
            foreach (var room in l.Rooms)
            {
                var path = "room " + room.Id + " (" + room.Name + ")";
                if (room.VertexCount < 3 || room.Area < 1.5f)
                {
                    r.Error(path, "Room is degenerate (minimum 1.5 m²).");
                    continue;
                }
                // Every polygon edge must lie along a wall on the same floor (rooms are enclosed).
                for (var i = 0; i < room.VertexCount; i++)
                {
                    var j = (i + 1) % room.VertexCount;
                    if (!EdgeCovered(l, room.Floor, room.X(i), room.Z(i), room.X(j), room.Z(j)))
                    {
                        r.Error(path, "Edge (" + room.X(i) + "," + room.Z(i) + ")–(" + room.X(j) + "," + room.Z(j) + ") is not closed by a wall.");
                        break;
                    }
                }
                if (room.Type == RoomType.Bathroom && room.Area < 2.5f) r.Warn(path, "Bathroom smaller than 2.5 m².");
                if (room.Type == RoomType.Bedroom && room.Area < 7f) r.Error(path, "Bedrooms need at least 7 m².");
            }
        }

        private void ValidateAccess(BuildingLayout l, ValidationReport r, bool commercial)
        {
            // Exterior doors = doors on exterior walls. Rooms touching those doors are entry rooms.
            var exteriorDoors = new List<Opening>();
            foreach (var o in l.Openings)
            {
                var w = l.FindWall(o.WallId);
                if (w != null && w.Floor == 0 && w.Kind == WallKind.Exterior && (o.Kind == OpeningKind.Door || o.Kind == OpeningKind.GarageDoor || o.Kind == OpeningKind.Archway))
                    exteriorDoors.Add(o);
            }
            if (exteriorDoors.Count == 0)
            {
                r.Error("access", "The building has no exterior door.");
                return;
            }
            if (commercial)
            {
                var accessible = false;
                foreach (var d in exteriorDoors) if (d.Kind != OpeningKind.GarageDoor && d.Width >= AccessibleDoorWidth) accessible = true;
                if (!accessible) r.Error("access", "Commercial buildings need an accessible entrance at least " + AccessibleDoorWidth + " m wide.");
                var groundArea = 0f;
                foreach (var room in l.Rooms) if (room.Floor == 0) groundArea += room.Area;
                if (groundArea > SecondExitArea && exteriorDoors.Count < 2)
                    r.Error("access", "Commercial floor area over " + SecondExitArea + " m² needs a second exit (fire code).");
            }

            // Reachability: rooms are connected when a door/archway sits on a wall that forms part of both.
            var reached = new HashSet<int>();
            var queue = new Queue<Room>();
            foreach (var room in l.Rooms)
            {
                if (room.Floor != 0) continue;
                foreach (var d in exteriorDoors)
                    if (DoorTouchesRoom(l, d, room))
                    {
                        if (reached.Add(room.Id)) queue.Enqueue(room);
                        break;
                    }
            }
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var other in l.Rooms)
                {
                    if (reached.Contains(other.Id)) continue;
                    var connected = false;
                    if (other.Floor == current.Floor)
                    {
                        foreach (var o in l.Openings)
                        {
                            if (o.Kind == OpeningKind.Window) continue;
                            if (DoorTouchesRoom(l, o, current) && DoorTouchesRoom(l, o, other)) { connected = true; break; }
                        }
                    }
                    else if (Math.Abs(other.Floor - current.Floor) == 1 && HasStairs(l, current, other))
                    {
                        connected = true;
                    }
                    if (connected && reached.Add(other.Id)) queue.Enqueue(other);
                }
            }
            foreach (var room in l.Rooms)
                if (!reached.Contains(room.Id)) r.Error("room " + room.Id + " (" + room.Name + ")", "Not reachable from an exterior door.");
        }

        private void ValidateStructure(BuildingLayout l, ValidationReport r)
        {
            foreach (var room in l.Rooms)
            {
                room.Bounds(out var minX, out var minZ, out var maxX, out var maxZ);
                var shortSide = Math.Min(maxX - minX, maxZ - minZ);
                if (shortSide <= MaxSpan) continue;
                // A load-bearing wall or column crossing the room breaks the span.
                var supported = false;
                foreach (var w in l.Walls)
                    if (w.Floor == room.Floor && w.Kind == WallKind.LoadBearing && room.Contains((w.X0 + w.X1) / 2, (w.Z0 + w.Z1) / 2)) supported = true;
                foreach (var f in l.Furniture)
                    if (f.Floor == room.Floor && Item(f.CatalogId) != null && Item(f.CatalogId).Tags.Contains("column") && room.Contains(f.X, f.Z)) supported = true;
                if (!supported) r.Error("room " + room.Id + " (" + room.Name + ")", "Clear span of " + shortSide.ToString("0.0") + " m exceeds " + MaxSpan + " m without a load-bearing wall or column.");
            }
            for (var floor = 1; floor < l.Floors; floor++)
            foreach (var room in l.Rooms)
            {
                if (room.Floor != floor) continue;
                for (var i = 0; i < room.VertexCount; i++)
                {
                    var supported = false;
                    foreach (var below in l.Rooms)
                        if (below.Floor == floor - 1 && (below.Contains(room.X(i), room.Z(i)) || OnPolygonEdge(below, room.X(i), room.Z(i)))) supported = true;
                    if (supported) continue;
                    r.Error("room " + room.Id + " (" + room.Name + ")", "Upper-floor room overhangs the floor below.");
                    break;
                }
            }
        }

        private void ValidateFurniture(BuildingLayout l, PropertyRecord property, ValidationReport r)
        {
            for (var i = 0; i < l.Furniture.Count; i++)
            {
                var f = l.Furniture[i];
                var def = Item(f.CatalogId);
                var path = "furniture " + f.Id + " (" + f.CatalogId + ")";
                if (def == null)
                {
                    r.Error(path, "Unknown catalog item.");
                    continue;
                }
                Footprint(f, def, out var fx0, out var fz0, out var fx1, out var fz1);
                Room room = null;
                foreach (var candidate in l.Rooms)
                    if (candidate.Floor == f.Floor && candidate.Contains(fx0 + 0.01f, fz0 + 0.01f) && candidate.Contains(fx1 - 0.01f, fz1 - 0.01f) &&
                        candidate.Contains(fx0 + 0.01f, fz1 - 0.01f) && candidate.Contains(fx1 - 0.01f, fz0 + 0.01f)) room = candidate;
                if (room == null)
                {
                    r.Error(path, "Must be placed entirely inside a room.");
                    continue;
                }
                if (def.AllowedRooms.Count > 0 && !def.AllowedRooms.Contains(room.Type)) r.Error(path, def.DisplayName + " cannot go in a " + room.Type + ".");
                if ((def.NeedsWater || def.NeedsGas || def.NeedsPower) && property != null && !property.UtilitiesConnected) r.Error(path, "Utilities are disconnected at this property.");
                for (var j = i + 1; j < l.Furniture.Count; j++)
                {
                    var g = l.Furniture[j];
                    var gdef = Item(g.CatalogId);
                    if (gdef == null || g.Floor != f.Floor) continue;
                    Footprint(g, gdef, out var gx0, out var gz0, out var gx1, out var gz1);
                    if (fx0 < gx1 - 0.01f && gx0 < fx1 - 0.01f && fz0 < gz1 - 0.01f && gz0 < fz1 - 0.01f) r.Error(path, "Overlaps furniture " + g.Id + ".");
                }
                foreach (var o in l.Openings)
                {
                    if (o.Kind == OpeningKind.Window) continue;
                    var w = l.FindWall(o.WallId);
                    if (w == null || w.Floor != f.Floor) continue;
                    DoorZone(w, o, out var dx0, out var dz0, out var dx1, out var dz1);
                    if (fx0 < dx1 && dx0 < fx1 && fz0 < dz1 && dz0 < fz1) r.Error(path, "Blocks door " + o.Id + ".");
                }
            }
        }

        private void ValidateBusiness(BuildingLayout l, PropertyRecord property, BusinessRequirement b, ValidationReport r)
        {
            if (property != null && b.AllowedZoning.Count > 0 && !b.AllowedZoning.Contains(property.Zoning.ToString()))
                r.Error("zoning", b.TemplateId + " is not permitted in a " + property.Zoning + " zone.");
            var tags = new HashSet<string>();
            foreach (var f in l.Furniture)
            {
                var def = Item(f.CatalogId);
                if (def != null) foreach (var t in def.Tags) tags.Add(t);
            }
            foreach (var t in b.RequiredTags) if (!tags.Contains(t)) r.Error("business", b.TemplateId + " requires: " + t.Replace('_', ' ') + ".");
            foreach (var rt in b.RequiredRooms)
                if (!l.Rooms.Exists(x => x.Type == rt)) r.Error("business", b.TemplateId + " requires a " + rt + " room.");
            var area = 0f;
            foreach (var room in l.Rooms) area += room.Area;
            if (area < b.MinFloorArea) r.Error("business", b.TemplateId + " needs at least " + b.MinFloorArea + " m² (has " + area.ToString("0") + ").");
        }

        public void Footprint(PlacedFurniture f, FurnitureDefinition def, out float x0, out float z0, out float x1, out float z1)
        {
            var rotated = Math.Abs(((int)Math.Round(f.Rotation / 90f)) % 2) == 1;
            var w = rotated ? def.Depth : def.Width;
            var d = rotated ? def.Width : def.Depth;
            x0 = f.X - w / 2;
            x1 = f.X + w / 2;
            z0 = f.Z - d / 2;
            z1 = f.Z + d / 2;
        }

        private static void DoorZone(Wall w, Opening o, out float x0, out float z0, out float x1, out float z1)
        {
            // The opening's segment, grown by the clearance on both sides of the wall (axis-aligned walls).
            var len = Math.Max(0.001f, w.Length);
            var dx = (w.X1 - w.X0) / len;
            var dz = (w.Z1 - w.Z0) / len;
            var ax = w.X0 + dx * o.Offset;
            var az = w.Z0 + dz * o.Offset;
            var bx = ax + dx * o.Width;
            var bz = az + dz * o.Width;
            var nx = Math.Abs(dz) * DoorClearance; // wall along Z → clearance along X
            var nz = Math.Abs(dx) * DoorClearance; // wall along X → clearance along Z
            x0 = Math.Min(ax, bx) - nx;
            x1 = Math.Max(ax, bx) + nx;
            z0 = Math.Min(az, bz) - nz;
            z1 = Math.Max(az, bz) + nz;
        }

        private static bool EdgeCovered(BuildingLayout l, int floor, float ax, float az, float bx, float bz)
        {
            // Sample points along the edge; each must lie on some wall of that floor.
            var len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            var steps = Math.Max(2, (int)(len / Grid));
            for (var s = 0; s <= steps; s++)
            {
                var t = s / (float)steps;
                var px = ax + (bx - ax) * t;
                var pz = az + (bz - az) * t;
                var on = false;
                foreach (var w in l.Walls)
                    if (w.Floor == floor && OnSegment(px, pz, w.X0, w.Z0, w.X1, w.Z1)) { on = true; break; }
                if (!on) return false;
            }
            return true;
        }

        private static bool DoorTouchesRoom(BuildingLayout l, Opening o, Room room)
        {
            var w = l.FindWall(o.WallId);
            if (w == null || w.Floor != room.Floor) return false;
            var len = Math.Max(0.001f, w.Length);
            var mx = w.X0 + (w.X1 - w.X0) / len * (o.Offset + o.Width / 2);
            var mz = w.Z0 + (w.Z1 - w.Z0) / len * (o.Offset + o.Width / 2);
            if (OnPolygonEdge(room, mx, mz)) return true;
            // Also connect whatever room lies directly on either side of the opening (walls inside a larger room).
            var nx = -(w.Z1 - w.Z0) / len * 0.3f;
            var nz = (w.X1 - w.X0) / len * 0.3f;
            return room.Contains(mx + nx, mz + nz) || room.Contains(mx - nx, mz - nz);
        }

        private bool HasStairs(BuildingLayout l, Room a, Room b)
        {
            var lower = a.Floor < b.Floor ? a : b;
            var upper = a.Floor < b.Floor ? b : a;
            foreach (var f in l.Furniture)
            {
                var def = Item(f.CatalogId);
                if (def == null || !def.Tags.Contains("stairs") || f.Floor != lower.Floor) continue;
                if (lower.Contains(f.X, f.Z) && upper.Contains(f.X, f.Z)) return true;
            }
            return false;
        }

        private static bool OnPolygonEdge(Room room, float x, float z)
        {
            for (var i = 0; i < room.VertexCount; i++)
            {
                var j = (i + 1) % room.VertexCount;
                if (OnSegment(x, z, room.X(i), room.Z(i), room.X(j), room.Z(j))) return true;
            }
            return false;
        }

        private static bool OnSegment(float px, float pz, float ax, float az, float bx, float bz)
        {
            var abx = bx - ax;
            var abz = bz - az;
            var len2 = abx * abx + abz * abz;
            if (len2 < 1e-8f) return Math.Abs(px - ax) < Tolerance && Math.Abs(pz - az) < Tolerance;
            var t = ((px - ax) * abx + (pz - az) * abz) / len2;
            if (t < -1e-3f || t > 1 + 1e-3f) return false;
            var cx = ax + abx * t - px;
            var cz = az + abz * t - pz;
            return cx * cx + cz * cz <= Tolerance * Tolerance;
        }
    }
}
