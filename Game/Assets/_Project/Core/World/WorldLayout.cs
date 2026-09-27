using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;

namespace HeroGame.Core.World
{
    /// <summary>
    /// Authoring format for a region of the city (districts_*.json). Mixes hand-placed landmarks
    /// with procedural parcel blocks so large areas can be described compactly and regenerated
    /// deterministically. The same data drives the simulation and the Unity greybox builder.
    /// </summary>
    [Serializable]
    public sealed class WorldLayout
    {
        public string Id = "";
        public string DisplayName = "";
        public List<DistrictLayout> Districts = new List<DistrictLayout>();
        public List<RoadLayout> Roads = new List<RoadLayout>();
        /// <summary>Sea, sounds, canals, rivers and bayous (convex polygons); only docks, beaches and bridges meet them.</summary>
        public List<WaterLayout> Water = new List<WaterLayout>();

        /// <summary>Is this point in any body of water?</summary>
        public bool IsWater(float x, float z)
        {
            foreach (var w in Water) if (w.Contains(x, z)) return true;
            return false;
        }
    }

    [Serializable]
    public sealed class WaterLayout
    {
        public string Name = "";
        /// <summary>Gulf, Sound, ShipCanal, Channel, River, Bayou, Lake.</summary>
        public string Kind = "";
        /// <summary>Polygon as x0, z0, x1, z1, … (at least three corners).</summary>
        public List<float> Points = new List<float>();

        /// <summary>Point in polygon (even-odd rule), with a bounding-box shortcut.</summary>
        public bool Contains(float x, float z)
        {
            var n = Points.Count / 2;
            if (n < 3) return false;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (var i = 0; i < n; i++)
            {
                var px = Points[2 * i];
                var pz = Points[2 * i + 1];
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (pz < minZ) minZ = pz;
                if (pz > maxZ) maxZ = pz;
            }
            if (x < minX || x > maxX || z < minZ || z > maxZ) return false;
            var inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = Points[2 * i], zi = Points[2 * i + 1], xj = Points[2 * j], zj = Points[2 * j + 1];
                if (zi > z != zj > z && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }
    }

    [Serializable]
    public sealed class DistrictLayout
    {
        public string Key = "";
        public string Name = "";
        public DistrictType Type;
        public float Wealth = 0.5f;
        public float CrimeBaseline = 0.2f;
        public float FootTraffic = 1f;
        public float PoliceTrust = 0.6f;
        public float FloodRisk = 0.2f;
        public float CenterX;
        public float CenterZ;
        public float Radius = 300f;
        public string Description = "";
        public List<PlaceLayout> Places = new List<PlaceLayout>();
        public List<ParcelBlock> Blocks = new List<ParcelBlock>();
    }

    [Serializable]
    public sealed class PlaceLayout
    {
        public string Key = "";
        public string Name = "";
        public PlaceKind Kind;
        public float X;
        public float Z;
        public float Width = 20f;
        public float Depth = 20f;
        public float Height = 8f;
        public float RotationY;
        public int Capacity = 10;
        public int OpenMinute;
        public int CloseMinute;
        public PropertySpec Property;
        public BusinessSpec Business;
        public List<string> Tags = new List<string>();
    }

    /// <summary>A grid of similar parcels (e.g. a street of houses) expanded procedurally.</summary>
    [Serializable]
    public sealed class ParcelBlock
    {
        public string KeyPrefix = "";
        public PlaceKind Kind = PlaceKind.Residence;
        public float OriginX;
        public float OriginZ;
        public int Rows = 1;
        public int Columns = 1;
        public float SpacingX = 25f;
        public float SpacingZ = 30f;
        public float RotationY;
        public float Width = 12f;
        public float Depth = 14f;
        public float MinHeight = 5f;
        public float MaxHeight = 8f;
        public int MinCapacity = 3;
        public int MaxCapacity = 5;
        /// <summary>Probability a parcel is left vacant (empty lot / abandoned building).</summary>
        public float VacancyRate;
        public List<string> StreetNames = new List<string>();
        public PropertySpec Property;
    }

    [Serializable]
    public sealed class PropertySpec
    {
        public PropertyKind Kind = PropertyKind.House;
        public ZoningType Zoning = ZoningType.Residential;
        public float FloorAreaSqm = 110f;
        public float LotAreaSqm = 400f;
        public int Bedrooms = 3;
        public int Floors = 1;
        public long BaseValueCents = 18000000;
        /// <summary>± fraction of random variation applied to area and value.</summary>
        public float Variation = 0.15f;
        public string Owner = "npc";
    }

    [Serializable]
    public sealed class BusinessSpec
    {
        public string TemplateId = "";
        public string Name = "";
        public int Staff = -1;
        /// <summary>"npc" (default) or "city" or "market" (for sale to players).</summary>
        public string Owner = "npc";
    }

    [Serializable]
    public sealed class RoadLayout
    {
        public string Name = "";
        public float Width = 12f;
        public List<float> Points = new List<float>();
        public string Kind = "street";
    }

    /// <summary>A place expanded from the layout, with its physical footprint (for the greybox builder).</summary>
    public sealed class ExpandedPlace
    {
        public Place Place;
        public PlaceLayout Source;
        public float Width;
        public float Depth;
        public float Height;
        public float RotationY;
        public PropertySpec Property;
        public BusinessSpec Business;
        public string Address = "";
    }

    public static class LayoutExpander
    {
        /// <summary>Expands a layout into districts and places. Deterministic for a seed.</summary>
        public static List<ExpandedPlace> Expand(WorldLayout layout, ulong seed, IdAllocator ids, Geography geography)
        {
            var result = new List<ExpandedPlace>();
            foreach (var dl in layout.Districts)
            {
                var district = new District
                {
                    Id = ids.Next(EntityKind.District),
                    Key = dl.Key,
                    Name = dl.Name,
                    Type = dl.Type,
                    Wealth = dl.Wealth,
                    CrimeBaseline = dl.CrimeBaseline,
                    FootTraffic = dl.FootTraffic,
                    PoliceTrust = dl.PoliceTrust,
                    FloodRisk = dl.FloodRisk,
                    Center = new WorldPosition(dl.CenterX, 0, dl.CenterZ),
                    Radius = dl.Radius,
                    Description = dl.Description,
                };
                geography.Add(district);

                foreach (var pl in dl.Places)
                {
                    var place = new Place
                    {
                        Id = ids.Next(EntityKind.Place),
                        Name = pl.Name,
                        Kind = pl.Kind,
                        District = district.Id,
                        Position = new WorldPosition(pl.X, 0, pl.Z),
                        Capacity = pl.Capacity,
                        OpenMinute = pl.OpenMinute,
                        CloseMinute = pl.CloseMinute,
                    };
                    geography.Add(place);
                    result.Add(new ExpandedPlace
                    {
                        Place = place, Source = pl, Width = pl.Width, Depth = pl.Depth, Height = pl.Height, RotationY = pl.RotationY,
                        Property = pl.Property, Business = pl.Business, Address = pl.Name,
                    });
                }

                foreach (var block in dl.Blocks)
                {
                    var rng = new DeterministicRandom(StableHash.Combine(seed, StableHash.Of(dl.Key), StableHash.Of(block.KeyPrefix)));
                    var n = 0;
                    for (var r = 0; r < block.Rows; r++)
                    {
                        for (var c = 0; c < block.Columns; c++)
                        {
                            n++;
                            var vacant = rng.Chance(block.VacancyRate);
                            var street = block.StreetNames.Count > 0 ? block.StreetNames[r % block.StreetNames.Count] : dl.Name + " St";
                            var number = 100 + c * 4 + (r % 2) * 2 + r * 100;
                            var address = number + " " + street;
                            var place = new Place
                            {
                                Id = ids.Next(EntityKind.Place),
                                Name = address,
                                Kind = vacant ? PlaceKind.Vacant : block.Kind,
                                District = district.Id,
                                Position = new WorldPosition(block.OriginX + c * block.SpacingX, 0, block.OriginZ + r * block.SpacingZ),
                                Capacity = rng.NextInt(block.MinCapacity, block.MaxCapacity + 1),
                            };
                            geography.Add(place);
                            result.Add(new ExpandedPlace
                            {
                                Place = place,
                                Width = block.Width,
                                Depth = block.Depth,
                                Height = block.MinHeight + (block.MaxHeight - block.MinHeight) * rng.NextFloat(),
                                RotationY = block.RotationY,
                                Property = block.Property,
                                Address = address,
                            });
                        }
                    }
                }
            }
            return result;
        }
    }
}
