using System;
using System.Collections.Generic;
using HeroGame.Core.Building;
using HeroGame.Core.Business;
using HeroGame.Core.Config;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Property;
using HeroGame.Core.Time;
using HeroGame.Core.Vehicles;
using HeroGame.Core.World;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// Builds a brand-new world from configuration and content: institutions and their accounts,
    /// geography, real estate, businesses and the initial population. Deterministic for a seed,
    /// so "same seed + same content" always yields the same starting city.
    /// </summary>
    public static class WorldGenerator
    {
        public static World Create(string serverId, ServerConfig config, ContentSet content, ITransactionJournal journal, GameDateTime? start = null)
        {
            var report = ServerConfigValidator.ValidateAndClamp(config);
            if (report.HasErrors) throw new InvalidOperationException("Invalid server config:\n" + report);

            var clock = start.HasValue ? new WorldClock(start.Value, WorldClock.TimeScaleForDayLength(config.Gameplay.RealMinutesPerGameDay)) : null;
            var world = new World(serverId, config, content, journal, clock);
            CreateInstitutions(world);
            var expanded = LayoutExpander.Expand(content.Layout, world.Seed, world.Ids, world.Geography);
            var propertyByPlace = CreateProperties(world, expanded);

            var generator = new PopulationGenerator(world.Seed, world.Ids, world.Occupations, content.Names, world.Geography);
            generator.Generate(world.Population, world.Today, config.Gameplay.NpcDensity);

            AssignResidentialOwnership(world, propertyByPlace);
            CreateLayoutsAndUnits(world, expanded, propertyByPlace);
            CreateBusinesses(world, expanded, propertyByPlace);
            CreateVehicles(world);

            world.Properties.Reassess(world.Geography, world.Macro, config.Economy.PropertyPriceMultiplier);
            world.Weather.AdvanceTo(world.Clock.Now);
            world.Cursor.SimulatedUpTo = world.Clock.Now;
            world.History.Record(world.Today, HistoryCategory.Server, 5,
                config.Identity.CityName + " opens its doors",
                "A new chapter begins for " + config.Identity.CityName + ". Population " + world.Population.Count + ".");
            world.EnsureInstitutions();
            world.Dirty.MarkAll(SaveChunks.WorldChunks);

            if (!world.Ledger.VerifyInvariant(out var sum)) throw new InvalidOperationException("Ledger invariant broken after generation: " + sum);
            return world;
        }

        private static void CreateInstitutions(World world)
        {
            var a = world.Accounts;
            a.Government = world.Ids.Next(EntityKind.Organization);
            a.BankOrganization = world.Ids.Next(EntityKind.Organization);
            a.PropertyManagement = world.Ids.Next(EntityKind.Organization);

            a.External = world.Ids.Next(EntityKind.LedgerAccount);
            world.Ledger.Open(a.External, EntityId.None, LedgerAccountKind.External, "Outside economy");
            a.Treasury = world.Ids.Next(EntityKind.LedgerAccount);
            world.Ledger.Open(a.Treasury, a.Government, LedgerAccountKind.Government, world.Config.Identity.GovernmentName + " treasury", 5000000000);
            a.BankReserves = world.Ids.Next(EntityKind.LedgerAccount);
            world.Ledger.Open(a.BankReserves, a.BankOrganization, LedgerAccountKind.Bank, "Gulf Tidewater Bank reserves");

            a.PropertyManagementAccount = world.Ids.Next(EntityKind.LedgerAccount);
            world.Ledger.Open(a.PropertyManagementAccount, a.PropertyManagement, LedgerAccountKind.Business, "Tidewater Property Management");
            a.Contractors = world.Ids.Next(EntityKind.LedgerAccount);
            world.Ledger.Open(a.Contractors, EntityId.None, LedgerAccountKind.External, "Contractors & suppliers");
            Seed(world, a.Treasury, Money.FromDollars(50000000L), "Initial municipal budget");
            Seed(world, a.PropertyManagementAccount, Money.FromDollars(2000000L), "Property management reserves");
            Seed(world, a.BankReserves, Money.FromDollars(750000000L), "Bank capitalisation");
        }

        private static Dictionary<EntityId, PropertyRecord> CreateProperties(World world, List<ExpandedPlace> expanded)
        {
            var map = new Dictionary<EntityId, PropertyRecord>();
            foreach (var e in expanded)
            {
                var spec = e.Property ?? DefaultSpec(e.Place.Kind);
                if (spec == null) continue;
                var rng = DeterministicRandom.For(world.Seed, e.Place.Id.Value, 0x9409);
                var variation = 1f + (rng.NextFloat() * 2f - 1f) * spec.Variation;
                var record = new PropertyRecord
                {
                    Id = world.Ids.Next(EntityKind.Property),
                    Address = e.Address,
                    Kind = spec.Kind,
                    Zoning = spec.Zoning,
                    District = e.Place.District,
                    Place = e.Place.Id,
                    FloorAreaSqm = spec.FloorAreaSqm * variation,
                    LotAreaSqm = spec.LotAreaSqm * variation,
                    Bedrooms = spec.Bedrooms,
                    Floors = spec.Floors,
                    BaseValueCents = (long)(spec.BaseValueCents * variation),
                    Condition = 0.55f + rng.NextFloat() * 0.45f,
                    HasAlarm = rng.Chance(0.25),
                };
                if (e.Place.Kind == PlaceKind.Vacant)
                {
                    record.Kind = PropertyKind.Land;
                    record.Condition = 0.3f;
                    record.BaseValueCents /= 4;
                }
                e.Place.Property = record.Id;
                world.Properties.Add(record);
                map[e.Place.Id] = record;
            }
            return map;
        }

        private static PropertySpec DefaultSpec(PlaceKind kind)
        {
            switch (kind)
            {
                case PlaceKind.Residence: return new PropertySpec();
                case PlaceKind.ApartmentBuilding: return new PropertySpec { Kind = PropertyKind.Apartment, FloorAreaSqm = 2400, LotAreaSqm = 1600, Bedrooms = 0, Floors = 4, BaseValueCents = 320000000 };
                case PlaceKind.Shop:
                case PlaceKind.Restaurant:
                case PlaceKind.Nightlife:
                case PlaceKind.Gym:
                case PlaceKind.GasStation:
                case PlaceKind.Garage:
                case PlaceKind.Office:
                    return new PropertySpec { Kind = PropertyKind.Commercial, Zoning = ZoningType.Commercial, FloorAreaSqm = 300, LotAreaSqm = 700, Bedrooms = 0, BaseValueCents = 65000000 };
                case PlaceKind.Warehouse:
                case PlaceKind.Factory:
                    return new PropertySpec { Kind = PropertyKind.Warehouse, Zoning = ZoningType.Industrial, FloorAreaSqm = 1800, LotAreaSqm = 3000, Bedrooms = 0, BaseValueCents = 95000000 };
                case PlaceKind.Vacant:
                    return new PropertySpec { Kind = PropertyKind.Land, Zoning = ZoningType.MixedUse, FloorAreaSqm = 0, LotAreaSqm = 450, Bedrooms = 0, BaseValueCents = 6000000 };
                default:
                    return null; // civic places (schools, hospitals, parks) are not on the market
            }
        }

        private static void AssignResidentialOwnership(World world, Dictionary<EntityId, PropertyRecord> propertyByPlace)
        {
            var occupied = new HashSet<EntityId>();
            foreach (var household in world.Population.Households)
            {
                if (!propertyByPlace.TryGetValue(household.Home, out var property)) continue;
                occupied.Add(household.Home);
                if (world.Ownership.OwnerOf(property.Id).IsValid) continue; // shared building already assigned
                if (property.Kind == PropertyKind.Apartment || !household.OwnsHome)
                {
                    world.Ownership.AssignInitial(property.Id, world.Accounts.PropertyManagement);
                }
                else
                {
                    // Head of household holds title.
                    world.Ownership.AssignInitial(property.Id, household.Members[0]);
                }
            }
            // Unoccupied homes: rentals owned by the management company, a few on the open market.
            foreach (var kv in propertyByPlace)
            {
                var p = kv.Value;
                if (occupied.Contains(kv.Key) || world.Ownership.OwnerOf(p.Id).IsValid) continue;
                if ((p.Kind == PropertyKind.House || p.Kind == PropertyKind.Apartment) && StableHash.Mix(p.Id.Value ^ world.Seed) % 3 == 0)
                    world.Ownership.AssignInitial(p.Id, world.Accounts.PropertyManagement);
                // Everything else remains unowned → listed for sale by the city at market value.
            }
        }

        private static void CreateBusinesses(World world, List<ExpandedPlace> expanded, Dictionary<EntityId, PropertyRecord> propertyByPlace)
        {
            var employeesByPlace = new Dictionary<EntityId, List<EntityId>>();
            foreach (var npc in world.Population.Ordered)
            {
                if (!npc.Workplace.IsValid) continue;
                if (!employeesByPlace.TryGetValue(npc.Workplace, out var list))
                {
                    list = new List<EntityId>();
                    employeesByPlace.Add(npc.Workplace, list);
                }
                list.Add(npc.Id);
            }

            foreach (var e in expanded)
            {
                if (e.Business == null || string.IsNullOrEmpty(e.Business.TemplateId)) continue;
                var template = world.BusinessSim.Template(e.Business.TemplateId);
                if (template == null) continue;
                var business = new BusinessRecord
                {
                    Id = world.Ids.Next(EntityKind.Business),
                    Name = string.IsNullOrEmpty(e.Business.Name) ? e.Place.Name : e.Business.Name,
                    TemplateId = template.Id,
                    Place = e.Place.Id,
                    Property = propertyByPlace.TryGetValue(e.Place.Id, out var prop) ? prop.Id : EntityId.None,
                    Staff = e.Business.Staff >= 0 ? e.Business.Staff : template.StaffRequired,
                    InventoryDays = template.InventoryDays,
                    Reputation = 0.4f + (float)(StableHash.Mix(e.Place.Id.Value) % 30) / 100f,
                };
                if (employeesByPlace.TryGetValue(e.Place.Id, out var staff)) business.Employees.AddRange(staff);
                business.Account = world.Ids.Next(EntityKind.LedgerAccount);
                world.Ledger.Open(business.Account, business.Id, LedgerAccountKind.Business, business.Name + " operating");
                var runway = (template.DailyFixedCostCents + template.HourlyWageCents * template.StaffRequired * 12) * 45;
                Seed(world, business.Account, new Money(runway), "Opening capital: " + business.Name);

                var owner = e.Business.Owner ?? "npc";
                if (owner == "market")
                {
                    // Unowned: for sale to players at template price; its property transfers with it.
                }
                else
                {
                    var ownerOrg = world.Ids.Next(EntityKind.Organization);
                    world.Ownership.AssignInitial(business.Id, ownerOrg);
                    if (business.Property.IsValid) world.Ownership.AssignInitial(business.Property, ownerOrg);
                    // Some owners are ready to retire: roughly one business in six is on the market from day one.
                    var h = StableHash.Mix(business.Id.Value ^ world.Seed ^ 0xB12);
                    if (h % 6 == 0)
                    {
                        business.ForSale = true;
                        business.AskingPriceCents = template.PurchasePriceCents / 100 * (100 + (long)(h / 6 % 30));
                    }
                }
                world.Businesses[business.Id] = business;
            }
        }

        private static void CreateLayoutsAndUnits(World world, List<ExpandedPlace> expanded, Dictionary<EntityId, PropertyRecord> propertyByPlace)
        {
            var householdsByHome = new Dictionary<EntityId, List<Household>>();
            foreach (var h in SortedHouseholds(world))
            {
                if (!householdsByHome.TryGetValue(h.Home, out var list)) householdsByHome[h.Home] = list = new List<Household>();
                list.Add(h);
            }
            foreach (var e in expanded)
            {
                if (!propertyByPlace.TryGetValue(e.Place.Id, out var property) || property.Kind == PropertyKind.Land) continue;
                property.Layout = BuildingLayout.Shell(property.Id.ToString(), e.Width, e.Depth, RoomFor(e.Place.Kind), Math.Max(1, property.Floors));
                if (property.Kind != PropertyKind.Apartment) continue;

                // Apartment units: one per resident household plus ~10% vacancy for renters (players included).
                householdsByHome.TryGetValue(e.Place.Id, out var households);
                var occupied = households != null ? households.Count : 0;
                var total = occupied + Math.Max(1, (int)Math.Ceiling(occupied * 0.1));
                var district = world.Geography.GetDistrict(e.Place.District);
                var wealth = district != null ? district.Wealth : 0.5f;
                for (var i = 0; i < total; i++)
                {
                    var bedrooms = 1 + (int)(StableHash.Combine(world.Seed, property.Id.Value, (ulong)i) % 3);
                    property.Units.Add(new RentalUnit
                    {
                        Id = i + 1,
                        Label = "Apt " + (1 + i / 6) + (char)('A' + i % 6),
                        Bedrooms = bedrooms,
                        AreaSqm = 45f + bedrooms * 22f,
                        MonthlyRentCents = (long)((65000 + bedrooms * 28000) * (0.6 + wealth * 0.9)) / 100 * 100,
                        NpcHousehold = i < occupied ? households[i].Id : EntityId.None,
                    });
                }
            }
        }

        private static RoomType RoomFor(PlaceKind kind)
        {
            switch (kind)
            {
                case PlaceKind.Shop:
                case PlaceKind.GasStation: return RoomType.Retail;
                case PlaceKind.Restaurant: return RoomType.Dining;
                case PlaceKind.Nightlife: return RoomType.Bar;
                case PlaceKind.Office: return RoomType.Office;
                case PlaceKind.Warehouse:
                case PlaceKind.Factory:
                case PlaceKind.Dock: return RoomType.Warehouse;
                case PlaceKind.Garage: return RoomType.Workshop;
                default: return RoomType.Living;
            }
        }

        /// <summary>
        /// Household cars parked at home (the most car-dependent metro in the country, so most working households
        /// drive) plus emergency-service fleets at their stations. NPC cars are real, persistent and stealable.
        /// </summary>
        private static void CreateVehicles(World world)
        {
            var civilian = new List<VehicleModel>();
            var weights = new List<double>();
            foreach (var m in world.Content.VehicleModels)
            {
                if (!m.Civilian || m.Class == VehicleClass.Boat || m.Class == VehicleClass.Helicopter) continue;
                civilian.Add(m);
                weights.Add(1.0 / Math.Max(0.3, m.BasePriceCents / 2500000.0)); // cheaper cars are more common
            }
            civilian.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            if (civilian.Count > 0)
            {
                foreach (var household in SortedHouseholds(world))
                {
                    var head = world.Population.Get(household.Members[0]);
                    if (head == null || head.AgeYears(world.Today) < 18) continue;
                    var rng = DeterministicRandom.For(world.Seed, household.Id.Value, 0xCA7);
                    var district = world.Geography.GetDistrict(world.Geography.GetPlace(household.Home)?.District ?? EntityId.None);
                    var wealth = district != null ? district.Wealth : 0.5f;
                    var ownsCar = head.Employment == EmploymentStatus.Employed ? 0.72 + wealth * 0.25 : 0.35;
                    if (!rng.Chance(ownsCar)) continue;
                    // Wealthier households skew toward pricier models.
                    var w = new List<double>(weights);
                    for (var i = 0; i < w.Count; i++) w[i] *= Math.Pow(civilian[i].BasePriceCents / 2500000.0, wealth * 1.6);
                    var model = civilian[Math.Max(0, rng.PickWeighted(w))];
                    var home = world.Geography.GetPlace(household.Home);
                    var pos = home != null ? new WorldPosition(home.Position.X + rng.Range(-4f, 4f), 0, home.Position.Z - 10f) : default;
                    var v = world.Vehicles.Spawn(model.Id, head.Id, pos, 90f, world.Clock.Now);
                    v.ColorHex = CarColors[rng.NextInt(0, CarColors.Length)];
                    v.FuelLitres = model.FuelCapacityLitres * (0.2f + rng.NextFloat() * 0.8f);
                    v.OdometerKm = rng.Range(2000f, 180000f);
                    v.BodyHealth = 0.7f + rng.NextFloat() * 0.3f;
                    head.Vehicle = v.Id;
                }
            }

            SpawnFleet(world, PlaceKind.PoliceStation, VehicleClass.Police, 6);
            SpawnFleet(world, PlaceKind.FireStation, VehicleClass.FireEngine, 2);
            SpawnFleet(world, PlaceKind.FireStation, VehicleClass.Ambulance, 2);
            SpawnFleet(world, PlaceKind.Hospital, VehicleClass.Ambulance, 3);
        }

        private static readonly string[] CarColors = { "#1C1C1E", "#E8E8E6", "#8A8D91", "#5B6770", "#7A1E1E", "#1E3A5F", "#2F4F3A", "#C0B283", "#B5651D", "#4A4E69" };

        private static void SpawnFleet(World world, PlaceKind station, VehicleClass cls, int perStation)
        {
            VehicleModel model = null;
            foreach (var m in world.Content.VehicleModels) if (m.Class == cls) { model = m; break; }
            if (model == null) return;
            foreach (var place in world.Geography.PlacesOfKind(station))
                for (var i = 0; i < perStation; i++)
                    world.Vehicles.Spawn(model.Id, world.Accounts.Government, new WorldPosition(place.Position.X - 12f + i * 4f, 0, place.Position.Z - 18f), 0f, world.Clock.Now);
        }

        private static List<Household> SortedHouseholds(World world)
        {
            var list = new List<Household>(world.Population.Households);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        private static void Seed(World world, EntityId account, Money amount, string memo)
        {
            var result = world.Transactions.Execute(new WorldTransaction
            {
                Source = TransactionSource.Simulation,
                Timestamp = world.Clock.Now,
                Description = memo,
                Money = LedgerTransaction.Transfer(world.Accounts.External, account, amount, TransactionReason.WorldGeneration, memo),
            });
            if (!result.Success) throw new InvalidOperationException("Seeding failed: " + result.Error);
        }
    }
}
