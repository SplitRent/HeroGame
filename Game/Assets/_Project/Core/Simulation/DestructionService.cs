using System;
using System.Collections.Generic;

namespace HeroGame.Core.Simulation
{
    using HeroGame.Core.Economy;
    using HeroGame.Core.Emergency;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Time;
    using HeroGame.Core.World;
    using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

    /// <summary>
    /// Destruction and recovery (GDD Phase 20). Street furniture — lights, signals, hydrants, benches, shelters,
    /// dumpsters, bollards — is placed along the real road network and around real places, persists, and breaks
    /// from storms, powers and crashes with consequences: dark streets make witnesses worse at night, a sheared
    /// hydrant floods the street (wet surfaces conduct), a dead signal slows the intersection. Buildings whose damage
    /// reaches "destroyed" collapse: occupants are called in to EMS, leases end, debris hits nearby street furniture.
    /// Recovery is real work paid by the city: public-works crews (sized by the Public Works budget) repair broken
    /// furniture by priority, and collapsed buildings not owned by players are rebuilt with a reconstruction grant.
    /// </summary>
    public sealed class DestructionService
    {
        public const float CellSize = 32f;
        public const int RebuildDays = 60;
        public const float StreetLightSpacing = 38f;
        public const float HydrantSpacing = 130f;

        private readonly World _w;
        private readonly Dictionary<string, DestructibleKind> _kinds = new Dictionary<string, DestructibleKind>();
        private readonly Dictionary<long, List<PropInstance>> _grid = new Dictionary<long, List<PropInstance>>();
        private readonly Dictionary<int, PropInstance> _byId = new Dictionary<int, PropInstance>();
        private int _indexedCount = -1;

        /// <summary>Raised when a prop changes state (presentation, replication).</summary>
        public event Action<PropInstance> Changed;

        public DestructionService(World world)
        {
            _w = world;
            foreach (var k in world.Content.Destructibles) _kinds[k.Id] = k;
            world.Properties.Damaged += OnPropertyDamaged;
        }

        private DestructionState S => _w.Destruction;
        public DestructibleKind Kind(string id) => id != null && _kinds.TryGetValue(id, out var k) ? k : null;
        public IReadOnlyList<PropInstance> All => S.Props;

        public PropInstance Get(int id)
        {
            EnsureIndex();
            return _byId.TryGetValue(id, out var p) ? p : null;
        }

        // ------------------------------------------------------------------ placement

        /// <summary>Places street furniture from the road network and places (idempotent; older saves get it on load).</summary>
        public void EnsureGenerated()
        {
            if (S.Generated || _kinds.Count == 0) return;
            var next = 1;
            void Add(string kind, float x, float z, float yaw, int node = -1)
            {
                if (!_kinds.ContainsKey(kind)) return;
                S.Props.Add(new PropInstance { Id = next++, Kind = kind, X = x, Z = z, Yaw = yaw, Health = _kinds[kind].MaxHealth, RoadNode = node });
            }

            var roads = _w.Roads;
            if (roads != null)
            {
                foreach (var e in roads.Edges)
                {
                    var a = roads.Nodes[e.From].Position;
                    var b = roads.Nodes[e.To].Position;
                    var dx = b.X - a.X;
                    var dz = b.Z - a.Z;
                    var len = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (len < 1f) continue;
                    var ux = dx / len;
                    var uz = dz / len;
                    var yaw = (float)(Math.Atan2(ux, uz) * 180.0 / Math.PI);
                    var side = 5f + 3.5f * Math.Max(1, e.LanesPerDirection); // kerb offset grows with lanes
                    var lights = (int)(len / StreetLightSpacing);
                    for (var i = 1; i <= lights; i++)
                    {
                        var t = i * StreetLightSpacing;
                        var s = i % 2 == 0 ? 1f : -1f; // alternate sides
                        Add("street_light", a.X + ux * t - uz * side * s, a.Z + uz * t + ux * side * s, yaw);
                    }
                    var hydrants = (int)(len / HydrantSpacing);
                    for (var i = 1; i <= hydrants; i++)
                    {
                        var t = i * HydrantSpacing - 11f;
                        Add("fire_hydrant", a.X + ux * t + uz * side, a.Z + uz * t - ux * side, yaw);
                    }
                }
                foreach (var n in roads.Nodes)
                    if (n.IsIntersection) Add("traffic_signal", n.Position.X + 6f, n.Position.Z + 6f, 0f, n.Id);
            }

            var places = new List<Place>(_w.Geography.Places);
            places.Sort((x, y) => x.Id.CompareTo(y.Id));
            foreach (var p in places)
            {
                var x = p.Position.X;
                var z = p.Position.Z;
                switch (p.Kind)
                {
                    case PlaceKind.TransitStop:
                        Add("bus_shelter", x, z + 3f, 0f);
                        Add("bench", x + 2.5f, z + 3f, 0f);
                        break;
                    case PlaceKind.Park:
                    case PlaceKind.Beach:
                        Add("bench", x - 6f, z + 4f, 90f);
                        Add("bench", x + 6f, z - 4f, 270f);
                        break;
                    case PlaceKind.Shop:
                    case PlaceKind.Restaurant:
                    case PlaceKind.Nightlife:
                        Add("dumpster", x + 8f, z - 9f, 0f);
                        break;
                    case PlaceKind.Government:
                    case PlaceKind.PoliceStation:
                    case PlaceKind.Hospital:
                        Add("bollard", x - 3f, z + 7f, 0f);
                        Add("bollard", x, z + 7f, 0f);
                        Add("bollard", x + 3f, z + 7f, 0f);
                        break;
                }
            }
            S.Generated = true;
            _indexedCount = -1;
            _w.Dirty.Mark(SaveChunks.Destruction);
        }

        private void EnsureIndex()
        {
            if (_indexedCount == S.Props.Count) return;
            _grid.Clear();
            _byId.Clear();
            foreach (var p in S.Props)
            {
                _byId[p.Id] = p;
                var key = Cell(p.X, p.Z);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<PropInstance>();
                list.Add(p);
            }
            _indexedCount = S.Props.Count;
        }

        private static long Cell(float x, float z) => ((long)Math.Floor(x / CellSize) << 32) ^ (uint)(int)Math.Floor(z / CellSize);

        /// <summary>Props within <paramref name="radius"/>, in id order.</summary>
        public void Query(WorldPosition center, float radius, List<PropInstance> result)
        {
            EnsureIndex();
            result.Clear();
            var r2 = radius * radius;
            var x0 = (int)Math.Floor((center.X - radius) / CellSize);
            var x1 = (int)Math.Floor((center.X + radius) / CellSize);
            var z0 = (int)Math.Floor((center.Z - radius) / CellSize);
            var z1 = (int)Math.Floor((center.Z + radius) / CellSize);
            for (var cx = x0; cx <= x1; cx++)
                for (var cz = z0; cz <= z1; cz++)
                {
                    if (!_grid.TryGetValue(((long)cx << 32) ^ (uint)cz, out var list)) continue;
                    foreach (var p in list)
                    {
                        var dx = p.X - center.X;
                        var dz = p.Z - center.Z;
                        if (dx * dx + dz * dz <= r2) result.Add(p);
                    }
                }
            result.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        // ------------------------------------------------------------------ damage

        /// <summary>Applies damage; returns true if this broke it.</summary>
        public bool Damage(PropInstance p, float amount, string cause)
        {
            if (p == null || amount <= 0f || p.State == PropState.Destroyed || float.IsNaN(amount)) return false;
            var kind = Kind(p.Kind);
            var max = kind != null ? kind.MaxHealth : 1f;
            p.Health = Math.Max(0f, p.Health - amount);
            var before = p.State;
            p.State = p.Health <= 0.001f ? PropState.Destroyed : p.Health < max * 0.6f ? PropState.Damaged : PropState.Intact;
            if (p.State == before) return false;
            _w.Dirty.Mark(SaveChunks.Destruction);
            if (p.State == PropState.Destroyed)
            {
                p.BrokenDay = _w.Today;
                S.Destroyed++;
                if (kind != null && kind.Effect == "SignalOut") ApplyTrafficEffects();
                if (kind != null && kind.Effect == "WaterMain")
                    _w.History.Record(_w.Today, HistoryCategory.Community, 1, "Water gushing from a broken hydrant (" + cause + ")", "", DistrictAt(p.Position));
            }
            Changed?.Invoke(p);
            return p.State == PropState.Destroyed;
        }

        public int DamageInRadius(WorldPosition center, float radius, float amount, string cause)
        {
            var list = new List<PropInstance>();
            Query(center, radius, list);
            var broken = 0;
            foreach (var p in list)
            {
                var d = WorldPosition.DistanceXZ(p.Position, center);
                if (Damage(p, amount * (1f - 0.6f * d / Math.Max(1f, radius)), cause)) broken++;
            }
            return broken;
        }

        /// <summary>
        /// A vehicle hits a prop. Damage scales with kinetic energy; the vehicle takes some back. Speed and mass come
        /// from the server's own state for network players.
        /// </summary>
        public bool Impact(PropInstance p, float speedMs, float massKg)
        {
            if (p == null || speedMs < 3f) return false;
            var energy = 0.5 * massKg * speedMs * speedMs; // joules
            var damage = (float)Math.Min(3.0, energy / 150000.0);
            var kind = Kind(p.Kind);
            if (kind != null && kind.Material == "Concrete") damage *= 0.3f; // bollards stop cars
            return Damage(p, damage, "vehicle collision");
        }

        // ------------------------------------------------------------------ consequences

        /// <summary>
        /// 1 in daylight or where lighting works; at night, 0.5 where broken street lights leave a stretch dark
        /// (multiplies witness visibility).
        /// </summary>
        public float LightingAt(WorldPosition pos, GameDateTime t)
        {
            var h = t.Hour;
            if (h >= 6 && h < 20) return 1f;
            var list = new List<PropInstance>();
            Query(pos, 30f, list);
            var broken = false;
            foreach (var p in list)
            {
                if (p.Kind != "street_light") continue;
                if (p.State != PropState.Destroyed) return 1f;
                broken = true;
            }
            return broken ? 0.5f : 1f;
        }

        /// <summary>A sheared hydrant floods the street around it until it is repaired.</summary>
        public bool WetAt(WorldPosition pos)
        {
            var list = new List<PropInstance>();
            Query(pos, 25f, list);
            foreach (var p in list)
                if (p.Kind == "fire_hydrant" && p.State == PropState.Destroyed) return true;
            return false;
        }

        /// <summary>Dead signals turn intersections into four-way stops: roads meeting there lose capacity.</summary>
        public void ApplyTrafficEffects()
        {
            var roads = _w.Roads;
            if (roads == null) return;
            foreach (var e in roads.Edges) e.CapacityFactor = 1f;
            foreach (var p in S.Props)
            {
                if (p.RoadNode < 0 || p.RoadNode >= roads.Nodes.Count || p.State != PropState.Destroyed) continue;
                foreach (var ei in roads.Nodes[p.RoadNode].Edges) roads.Edges[ei].CapacityFactor = Math.Min(roads.Edges[ei].CapacityFactor, 0.55f);
            }
        }

        // ------------------------------------------------------------------ structural events

        private void OnPropertyDamaged(PropertyRecord p)
        {
            if (p.Damage != DamageState.Destroyed || S.Collapsed.ContainsKey(p.Id.ToString())) return;
            S.Collapsed[p.Id.ToString()] = _w.Today;
            _w.Dirty.Mark(SaveChunks.Destruction);
            var place = _w.Geography.GetPlace(p.Place);
            var pos = place != null ? place.Position : default;
            _w.History.Record(_w.Today, HistoryCategory.Disaster, 4, "Building collapses at " + p.Address, "Crews search the rubble; the structure is a total loss.", p.District, p.Id);
            if (place != null)
            {
                // People inside: an emergency call with severity from how busy the place was.
                var inside = 0;
                foreach (var id in _w.Population.AssociatedWith(place.Id))
                {
                    var npc = _w.Population.Get(id);
                    if (npc == null || !npc.Alive) continue;
                    var activity = _w.Schedules.Resolve(npc, _w.Clock.Now);
                    if (activity.Place == place.Id) inside++;
                }
                _w.Dispatch.Report(EmergencyKind.Medical, pos, 1, "Structural collapse at " + p.Address + (inside > 0 ? ": " + inside + " people inside" : ""), severity: Math.Min(1f, 0.4f + inside * 0.1f));
                DamageInRadius(pos, 15f, 1.2f, "falling debris");
            }
            foreach (var tenant in _w.Rentals.EndLeasesAfterDisaster(p, _w.Clock.Now))
                if (_w.Characters.TryGetValue(tenant, out var c))
                    _w.Phone.Send(c, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Emergency,
                        p.Address + " has collapsed. Your lease has ended and your deposit was returned. Emergency shelter is available at City Hall.");
            if (_w.Characters.TryGetValue(_w.Ownership.OwnerOf(p.Id), out var owner))
                _w.Phone.Send(owner, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Emergency,
                    "Your building at " + p.Address + " has collapsed. Rebuilding will cost about " + _w.Properties.RepairQuote(p, _w.Macro.PriceLevel) + " (insurance claims apply).");
            _w.Dirty.Mark(SaveChunks.Properties);
        }

        public bool IsCollapsed(EntityId property) => S.Collapsed.ContainsKey(property.ToString());

        // ------------------------------------------------------------------ time

        /// <summary>Hurricane winds bring down lights, signals and shelters.</summary>
        public void ProcessHour(GameDateTime t)
        {
            var storm = _w.Weather.State.ActiveSystem;
            if (storm == null || _w.Weather.State.Current.Kind != Weather.WeatherKind.Hurricane || storm.Category < 1f) return;
            foreach (var p in S.Props)
            {
                if (p.State == PropState.Destroyed) continue;
                var kind = Kind(p.Kind);
                var resistance = kind != null ? kind.WindResistance : 0.8f;
                var rng = DeterministicRandom.For(_w.Seed, 0x51DE, (ulong)p.Id, (ulong)t.HourIndex);
                if (!rng.Chance(0.006 * storm.Category * (1.0 - resistance))) continue;
                Damage(p, (float)(0.4 + rng.NextDouble() * 0.9) * (kind != null ? kind.MaxHealth : 1f), "hurricane winds");
            }
        }

        /// <summary>Public-works crews repair broken furniture by priority; the city rebuilds collapsed buildings it is responsible for.</summary>
        public void ProcessDay(long day)
        {
            var broken = new List<PropInstance>();
            foreach (var p in S.Props) if (p.State != PropState.Intact) broken.Add(p);
            if (broken.Count > 0)
            {
                broken.Sort((a, b) =>
                {
                    var pa = Kind(a.Kind)?.RepairPriority ?? 0;
                    var pb = Kind(b.Kind)?.RepairPriority ?? 0;
                    if (pa != pb) return pb.CompareTo(pa);
                    if (a.State != b.State) return b.State.CompareTo(a.State); // destroyed before merely damaged
                    return a.BrokenDay != b.BrokenDay ? a.BrokenDay.CompareTo(b.BrokenDay) : a.Id.CompareTo(b.Id);
                });
                var crews = Math.Max(1, (int)Math.Round(10 * _w.Government.ServiceLevel(Civic.Department.PublicWorks)));
                var repairedSignal = false;
                for (var i = 0; i < broken.Count && i < crews; i++)
                {
                    var p = broken[i];
                    var kind = Kind(p.Kind);
                    var cost = new Money((long)Math.Round((kind != null ? kind.RepairCostCents : 100000) * (p.State == PropState.Destroyed ? 1.0 : 0.35) * _w.Macro.PriceLevel));
                    var paid = _w.Transactions.Execute(new WorldTransaction
                    {
                        Source = TransactionSource.Simulation, Timestamp = new GameDateTime(day * GameDateTime.SecondsPerDay + 12 * GameDateTime.SecondsPerHour),
                        Description = "Public works repair",
                        Money = LedgerTransaction.Transfer(_w.Accounts.Treasury, _w.Accounts.Contractors, cost, TransactionReason.Maintenance, "Repair " + (kind?.DisplayName ?? p.Kind)),
                    });
                    if (!paid.Success) break; // the city is out of money: repairs wait
                    repairedSignal |= p.RoadNode >= 0 && p.State == PropState.Destroyed;
                    p.State = PropState.Intact;
                    p.Health = kind != null ? kind.MaxHealth : 1f;
                    p.BrokenDay = -1;
                    S.Repaired++;
                    Changed?.Invoke(p);
                }
                if (repairedSignal) ApplyTrafficEffects();
                _w.Dirty.Mark(SaveChunks.Destruction);
            }
            Rebuild(day);
        }

        private void Rebuild(long day)
        {
            if (S.Collapsed.Count == 0) return;
            var keys = new List<string>(S.Collapsed.Keys);
            keys.Sort(string.CompareOrdinal);
            foreach (var key in keys)
            {
                if (!EntityId.TryParse(key, out var id)) { S.Collapsed.Remove(key); continue; }
                var p = _w.Properties.Get(id);
                if (p == null || p.Damage != DamageState.Destroyed)
                {
                    S.Collapsed.Remove(key); // repaired by its owner
                    continue;
                }
                var owner = _w.Ownership.OwnerOf(id);
                if (_w.Characters.ContainsKey(owner)) continue; // a player's building waits for its owner
                if (day - S.Collapsed[key] < RebuildDays) continue;
                var cost = _w.Properties.RepairQuote(p, _w.Macro.PriceLevel);
                var paid = _w.Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation, Timestamp = new GameDateTime(day * GameDateTime.SecondsPerDay + 12 * GameDateTime.SecondsPerHour),
                    Description = "Reconstruction grant",
                    Money = LedgerTransaction.Transfer(_w.Accounts.Treasury, _w.Accounts.Contractors, cost, TransactionReason.Maintenance, "Rebuild " + p.Address),
                });
                if (!paid.Success) continue;
                _w.Properties.MarkRepaired(p);
                S.Collapsed.Remove(key);
                _w.History.Record(day, HistoryCategory.Community, 3, p.Address + " rebuilt after collapse", "The city's reconstruction grant covered " + cost + ".", p.District, p.Id);
                _w.Dirty.Mark(SaveChunks.Properties);
            }
            _w.Dirty.Mark(SaveChunks.Destruction);
        }

        private EntityId DistrictAt(WorldPosition p)
        {
            var best = EntityId.None;
            var bestD = float.MaxValue;
            foreach (var d in _w.Geography.Districts)
            {
                var dist = WorldPosition.DistanceXZ(d.Center, p) / Math.Max(1f, d.Radius);
                if (dist < bestD || dist == bestD && d.Id.CompareTo(best) < 0) { bestD = dist; best = d.Id; }
            }
            return best;
        }
    }
}
