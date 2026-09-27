using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.World
{
    public enum DistrictType
    {
        Downtown,
        InnerCity,
        Wealthy,
        Suburban,
        LowIncome,
        Industrial,
        Port,
        Coastal,
        Rural,
    }

    public enum PlaceKind
    {
        Residence,
        ApartmentBuilding,
        Shop,
        Restaurant,
        Office,
        Factory,
        Warehouse,
        School,
        University,
        Hospital,
        PoliceStation,
        FireStation,
        Park,
        Church,
        Gym,
        Nightlife,
        Stadium,
        TransitStop,
        GasStation,
        Garage,
        Government,
        Beach,
        Dock,
        Vacant,
    }

    /// <summary>Static + slowly-changing data for a district (GDD §9).</summary>
    [Serializable]
    public sealed class District
    {
        public EntityId Id;
        public string Key = "";
        public string Name = "";
        public DistrictType Type;
        /// <summary>0 (poorest) .. 1 (wealthiest). Drives rents, prices, NPC incomes.</summary>
        public float Wealth = 0.5f;
        /// <summary>Base daily crime pressure 0..1.</summary>
        public float CrimeBaseline = 0.2f;
        /// <summary>Relative pedestrian/customer traffic 0..2.</summary>
        public float FootTraffic = 1f;
        /// <summary>How willing residents are to report crime to police 0..1.</summary>
        public float PoliceTrust = 0.6f;
        /// <summary>Flood exposure 0..1 (low-lying bayou/coast).</summary>
        public float FloodRisk = 0.2f;
        public WorldPosition Center;
        public float Radius = 400f;
        public string Description = "";
    }

    /// <summary>A meaningful location NPCs and players can be at: a home, workplace, park, etc.</summary>
    [Serializable]
    public sealed class Place
    {
        public EntityId Id;
        public string Name = "";
        public PlaceKind Kind;
        public EntityId District;
        public WorldPosition Position;
        public int Capacity = 10;
        /// <summary>Opening hours for public places (minutes of day). Open==Close means always open.</summary>
        public int OpenMinute;
        public int CloseMinute;
        /// <summary>Property record that physically contains this place, if any.</summary>
        public EntityId Property;

        public bool IsOpenAt(int minuteOfDay)
        {
            if (OpenMinute == CloseMinute) return true;
            if (OpenMinute < CloseMinute) return minuteOfDay >= OpenMinute && minuteOfDay < CloseMinute;
            return minuteOfDay >= OpenMinute || minuteOfDay < CloseMinute; // overnight
        }
    }

    /// <summary>Lookup tables for districts and places.</summary>
    public sealed class Geography
    {
        private readonly Dictionary<EntityId, District> _districts = new Dictionary<EntityId, District>();
        private readonly Dictionary<EntityId, Place> _places = new Dictionary<EntityId, Place>();
        private readonly Dictionary<EntityId, List<Place>> _placesByDistrict = new Dictionary<EntityId, List<Place>>();

        public IEnumerable<District> Districts => _districts.Values;
        public IEnumerable<Place> Places => _places.Values;
        public int PlaceCount => _places.Count;

        public void Add(District d) => _districts[d.Id] = d;

        public void Add(Place p)
        {
            _places[p.Id] = p;
            if (!_placesByDistrict.TryGetValue(p.District, out var list))
            {
                list = new List<Place>();
                _placesByDistrict.Add(p.District, list);
            }
            list.Add(p);
        }

        public District GetDistrict(EntityId id) => _districts.TryGetValue(id, out var d) ? d : null;
        public Place GetPlace(EntityId id) => _places.TryGetValue(id, out var p) ? p : null;

        public District FindDistrict(string key)
        {
            foreach (var d in _districts.Values) if (d.Key == key) return d;
            return null;
        }

        public IReadOnlyList<Place> PlacesIn(EntityId district)
        {
            return _placesByDistrict.TryGetValue(district, out var list) ? (IReadOnlyList<Place>)list : Array.Empty<Place>();
        }

        public List<Place> PlacesOfKind(PlaceKind kind)
        {
            var result = new List<Place>();
            foreach (var p in _places.Values) if (p.Kind == kind) result.Add(p);
            return result;
        }

        /// <summary>District whose centre is nearest to a position.</summary>
        public District DistrictAt(WorldPosition position)
        {
            District best = null;
            var bestDist = float.MaxValue;
            foreach (var d in _districts.Values)
            {
                var dist = WorldPosition.DistanceSquaredXZ(position, d.Center);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            return best;
        }
    }
}
