using System;
using System.Collections.Generic;

namespace HeroGame.Core.Building
{
    public enum WallKind
    {
        Exterior,
        Interior,
        LoadBearing,
    }

    public enum OpeningKind
    {
        Door,
        Window,
        GarageDoor,
        Archway,
    }

    public enum RoomType
    {
        Living,
        Kitchen,
        Bathroom,
        Bedroom,
        Dining,
        Office,
        Retail,
        Storage,
        Bar,
        DanceFloor,
        Restroom,
        Workshop,
        Garage,
        Hallway,
        Showroom,
        Warehouse,
    }

    /// <summary>A straight wall on the build grid (lot-local metres, X right / Z forward from the lot's min corner).</summary>
    [Serializable]
    public sealed class Wall
    {
        public int Id;
        public int Floor;
        public float X0, Z0, X1, Z1;
        public WallKind Kind;
        public float Height = 3.2f;
        public string Finish = "";

        public float Length => (float)Math.Sqrt((X1 - X0) * (X1 - X0) + (Z1 - Z0) * (Z1 - Z0));
    }

    /// <summary>A door or window placed along a wall at <see cref="Offset"/> metres from its start.</summary>
    [Serializable]
    public sealed class Opening
    {
        public int Id;
        public int WallId;
        public OpeningKind Kind;
        public float Offset;
        public float Width = 0.9f;
        public float Height = 2.1f;
        public bool Locked;
        /// <summary>Security tier 0 (hollow) .. 3 (steel); affects burglary.</summary>
        public int Security;
    }

    /// <summary>A room as a closed polygon of lot-local points (XZ pairs).</summary>
    [Serializable]
    public sealed class Room
    {
        public int Id;
        public int Floor;
        public RoomType Type;
        public string Name = "";
        public List<float> Polygon = new List<float>();

        public int VertexCount => Polygon.Count / 2;
        public float X(int i) => Polygon[i * 2];
        public float Z(int i) => Polygon[i * 2 + 1];

        public float Area
        {
            get
            {
                double a = 0;
                for (var i = 0; i < VertexCount; i++)
                {
                    var j = (i + 1) % VertexCount;
                    a += X(i) * Z(j) - X(j) * Z(i);
                }
                return (float)Math.Abs(a / 2);
            }
        }

        public bool Contains(float x, float z)
        {
            var inside = false;
            for (int i = 0, j = VertexCount - 1; i < VertexCount; j = i++)
            {
                if ((Z(i) > z) != (Z(j) > z) && x < (X(j) - X(i)) * (z - Z(i)) / (Z(j) - Z(i)) + X(i)) inside = !inside;
            }
            return inside;
        }

        public void Bounds(out float minX, out float minZ, out float maxX, out float maxZ)
        {
            minX = minZ = float.MaxValue;
            maxX = maxZ = float.MinValue;
            for (var i = 0; i < VertexCount; i++)
            {
                minX = Math.Min(minX, X(i));
                maxX = Math.Max(maxX, X(i));
                minZ = Math.Min(minZ, Z(i));
                maxZ = Math.Max(maxZ, Z(i));
            }
        }
    }

    [Serializable]
    public sealed class PlacedFurniture
    {
        public int Id;
        public string CatalogId = "";
        public int Floor;
        public float X, Z;
        /// <summary>Degrees, snapped to 90° steps by the build tool.</summary>
        public float Rotation;
    }

    /// <summary>The editable interior/structure of one property (TDD §7). Stored with the property record.</summary>
    [Serializable]
    public sealed class BuildingLayout
    {
        public string Id = "";
        public float LotWidth = 20f;
        public float LotDepth = 20f;
        public int Floors = 1;
        public List<Wall> Walls = new List<Wall>();
        public List<Opening> Openings = new List<Opening>();
        public List<Room> Rooms = new List<Room>();
        public List<PlacedFurniture> Furniture = new List<PlacedFurniture>();
        public int NextId = 1;

        public Wall FindWall(int id)
        {
            foreach (var w in Walls) if (w.Id == id) return w;
            return null;
        }

        public BuildingLayout Clone()
        {
            var c = new BuildingLayout { Id = Id, LotWidth = LotWidth, LotDepth = LotDepth, Floors = Floors, NextId = NextId };
            foreach (var w in Walls) c.Walls.Add(new Wall { Id = w.Id, Floor = w.Floor, X0 = w.X0, Z0 = w.Z0, X1 = w.X1, Z1 = w.Z1, Kind = w.Kind, Height = w.Height, Finish = w.Finish });
            foreach (var o in Openings) c.Openings.Add(new Opening { Id = o.Id, WallId = o.WallId, Kind = o.Kind, Offset = o.Offset, Width = o.Width, Height = o.Height, Locked = o.Locked, Security = o.Security });
            foreach (var r in Rooms) c.Rooms.Add(new Room { Id = r.Id, Floor = r.Floor, Type = r.Type, Name = r.Name, Polygon = new List<float>(r.Polygon) });
            foreach (var f in Furniture) c.Furniture.Add(new PlacedFurniture { Id = f.Id, CatalogId = f.CatalogId, Floor = f.Floor, X = f.X, Z = f.Z, Rotation = f.Rotation });
            return c;
        }

        public const string ColumnItem = "structural_column";
        public const string StairsItem = "stairs_straight";

        /// <summary>
        /// A rectangular shell on the build grid — the default layout of every generated property: front door
        /// (plus a rear exit when large), stairs between floors, and structural columns where the clear span
        /// would exceed the validator's limit, so every generated building is valid out of the box.
        /// </summary>
        public static BuildingLayout Shell(string id, float width, float depth, RoomType roomType, int floors = 1)
        {
            width = Snap(Math.Max(3f, width));
            depth = Snap(Math.Max(3f, depth));
            var l = new BuildingLayout { Id = id, LotWidth = width + 4f, LotDepth = depth + 4f, Floors = Math.Max(1, floors) };
            const float m = 2f; // setback inside the lot
            var corners = new[] { m, m, m + width, m, m + width, m + depth, m, m + depth };
            for (var f = 0; f < l.Floors; f++)
            {
                for (var i = 0; i < 4; i++)
                {
                    var j = (i + 1) % 4;
                    l.Walls.Add(new Wall { Id = l.NextId++, Floor = f, X0 = corners[i * 2], Z0 = corners[i * 2 + 1], X1 = corners[j * 2], Z1 = corners[j * 2 + 1], Kind = WallKind.Exterior });
                }
                var room = new Room { Id = l.NextId++, Floor = f, Type = roomType, Name = roomType.ToString() + (l.Floors > 1 ? " L" + f : "") };
                room.Polygon.AddRange(corners);
                l.Rooms.Add(room);

                // Columns on a 10 m grid when the short side exceeds 12 m.
                if (Math.Min(width, depth) > 12f)
                    for (var cx = m + 10f; cx < m + width - 2f; cx += 10f)
                    for (var cz = m + 10f; cz < m + depth - 2f; cz += 10f)
                        l.Furniture.Add(new PlacedFurniture { Id = l.NextId++, CatalogId = ColumnItem, Floor = f, X = cx, Z = cz });
                if (f + 1 < l.Floors)
                    l.Furniture.Add(new PlacedFurniture { Id = l.NextId++, CatalogId = StairsItem, Floor = f, X = m + 1.0f, Z = m + depth - 2.5f });
            }
            // Front door centred on the street-facing (-Z) wall; a rear exit for large footprints (egress).
            l.Openings.Add(new Opening { Id = l.NextId++, WallId = l.Walls[0].Id, Kind = OpeningKind.Door, Offset = Snap(width / 2f - 0.6f), Width = 1.2f });
            if (width * depth > 150f)
                l.Openings.Add(new Opening { Id = l.NextId++, WallId = l.Walls[2].Id, Kind = OpeningKind.Door, Offset = Snap(width / 2f - 0.5f), Width = 1.0f });
            return l;
        }

        public static float Snap(float v) => (float)Math.Round(v / 0.25f) * 0.25f;
    }

    /// <summary>Furniture/fixture/equipment catalog entry (furniture_catalog.json).</summary>
    [Serializable]
    public sealed class FurnitureDefinition
    {
        public string Id = "";
        public string DisplayName = "";
        public string Category = "";
        public long PriceCents;
        public float Width = 1f;
        public float Depth = 1f;
        public float Height = 1f;
        public bool NeedsWater;
        public bool NeedsPower;
        public bool NeedsGas;
        /// <summary>Functional tags: bed, seating, stove, fridge, toilet, sink, counter, bar_counter, shelving, register,
        /// sound_system, lift, security_camera, alarm, safe, reinforced_door…</summary>
        public List<string> Tags = new List<string>();
        /// <summary>Customer capacity added (seats, standing room).</summary>
        public int Capacity;
        /// <summary>Stock capacity added for retail (days of sales).</summary>
        public float StockDays;
        /// <summary>Room types this item may be placed in (empty = any).</summary>
        public List<RoomType> AllowedRooms = new List<RoomType>();
    }

    /// <summary>What a business needs before it may open in a building (per business template).</summary>
    [Serializable]
    public sealed class BusinessRequirement
    {
        public string TemplateId = "";
        public List<string> RequiredTags = new List<string>();
        public List<RoomType> RequiredRooms = new List<RoomType>();
        public List<string> AllowedZoning = new List<string>();
        public float MinFloorArea;
        public long PermitFeeCents = 50000;
    }
}
