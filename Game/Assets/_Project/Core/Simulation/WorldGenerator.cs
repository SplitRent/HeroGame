using System;
using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Config;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Property;
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
        public static World Create(string serverId, ServerConfig config, ContentSet content, ITransactionJournal journal)
        {
            var report = ServerConfigValidator.ValidateAndClamp(config);
            if (report.HasErrors) throw new InvalidOperationException("Invalid server config:\n" + report);

            var world = new World(serverId, config, content, journal);
            CreateInstitutions(world);
            var expanded = LayoutExpander.Expand(content.Layout, world.Seed, world.Ids, world.Geography);
            var propertyByPlace = CreateProperties(world, expanded);

            var generator = new PopulationGenerator(world.Seed, world.Ids, world.Occupations, content.Names, world.Geography);
            generator.Generate(world.Population, world.Today, config.Gameplay.NpcDensity);

            AssignResidentialOwnership(world, propertyByPlace);
            CreateBusinesses(world, expanded, propertyByPlace);

            world.Properties.Reassess(world.Geography, world.Macro, config.Economy.PropertyPriceMultiplier);
            world.Weather.AdvanceTo(world.Clock.Now);
            world.Cursor.SimulatedUpTo = world.Clock.Now;
            world.History.Record(world.Today, HistoryCategory.Server, 5,
                config.Identity.CityName + " opens its doors",
                "A new chapter begins for " + config.Identity.CityName + ". Population " + world.Population.Count + ".");
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

            Seed(world, a.Treasury, Money.FromDollars(50000000L), "Initial municipal budget");
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
                }
                world.Businesses[business.Id] = business;
            }
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
