using System.Collections.Generic;
using System.IO;
using HeroGame.Core.Business;
using HeroGame.Core.Config;
using HeroGame.Core.Crime;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.World;
using HeroGame.Persistence.Json;
using HeroGame.Persistence.Storage;

namespace HeroGame.Persistence.Content
{
    /// <summary>
    /// Loads data-driven content (StreamingAssets/Data/*.json) into a <see cref="ContentSet"/> and
    /// validates cross references. Used identically by the Unity client, the headless server and tests.
    /// </summary>
    public static class ContentLoader
    {
        public const string Occupations = "occupations.json";
        public const string BusinessTemplates = "business_templates.json";
        public const string Names = "names.json";
        public const string PowerArchetypes = "power_archetypes.json";
        public const string AnomalyCauses = "anomaly_causes.json";
        public const string InteractionRules = "interaction_rules.json";
        public const string CrimeTypes = "crime_types.json";
        public const string DefaultLayout = "layout_vertical_slice.json";
        public const string Barks = "barks.json";
        public const string VehicleCatalog = "vehicle_catalog.json";
        public const string VehicleMods = "vehicle_mods.json";
        public const string Furniture = "furniture_catalog.json";
        public const string BusinessRequirements = "business_requirements.json";
        public const string Items = "items.json";
        public const string DefaultServerConfig = "server_default.json";

        public static ContentSet Load(string dataDirectory, string layoutFile = DefaultLayout)
        {
            var set = new ContentSet
            {
                Occupations = Read<List<OccupationDefinition>>(dataDirectory, Occupations),
                BusinessTemplates = Read<List<BusinessTemplate>>(dataDirectory, BusinessTemplates),
                Names = Read<NameTables>(dataDirectory, Names),
                PowerArchetypes = Read<List<PowerArchetype>>(dataDirectory, PowerArchetypes),
                AnomalyCauses = Read<List<AnomalyCause>>(dataDirectory, AnomalyCauses),
                InteractionRules = Read<List<InteractionRule>>(dataDirectory, InteractionRules),
                CrimeTypes = Read<List<CrimeType>>(dataDirectory, CrimeTypes),
                Layout = Read<WorldLayout>(dataDirectory, layoutFile),
                Barks = Read<List<Core.Social.BarkLine>>(dataDirectory, Barks),
                VehicleModels = Read<List<Core.Vehicles.VehicleModel>>(dataDirectory, VehicleCatalog),
                VehicleMods = Read<List<Core.Vehicles.VehicleMod>>(dataDirectory, VehicleMods),
                Furniture = Read<List<Core.Building.FurnitureDefinition>>(dataDirectory, Furniture),
                BusinessRequirements = Read<List<Core.Building.BusinessRequirement>>(dataDirectory, BusinessRequirements),
                Items = Read<List<ItemDefinition>>(dataDirectory, Items),
            };
            return set;
        }

        public static ServerConfig LoadServerConfig(string path)
        {
            return JsonSetup.Deserialize<ServerConfig>(AtomicFile.ReadAllText(path));
        }

        public static T Read<T>(string dir, string file)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) throw new FileNotFoundException("Missing content file " + path, path);
            try
            {
                return JsonSetup.Deserialize<T>(AtomicFile.ReadAllText(path));
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new InvalidDataException("Content file " + file + " is invalid: " + ex.Message, ex);
            }
        }

        /// <summary>Loot tables, items and crime ids the crime and justice code refers to by name.</summary>
        public static readonly string[] RequiredLootTags = { "residence_low", "residence_high", "pickpocket", "vehicle", "office", "warehouse" };
        public static readonly string[] RequiredItems = { "crowbar", "lockpick_set", "ski_mask" };
        public static readonly string[] RequiredCrimes =
        {
            "shoplifting", "pickpocketing", "burglary_residential", "burglary_commercial", "store_robbery", "vehicle_theft",
            "assault", "powered_assault", "vandalism", "evading_police", "assaulting_officer",
        };

        /// <summary>Cross-reference validation: catches broken ids before they reach a live server.</summary>
        public static ValidationReport Validate(ContentSet c)
        {
            var r = new ValidationReport();
            var occIds = new HashSet<string>();
            foreach (var o in c.Occupations)
            {
                if (string.IsNullOrEmpty(o.Id)) r.Error("occupations", "Occupation without id.");
                else if (!occIds.Add(o.Id)) r.Error("occupations." + o.Id, "Duplicate id.");
                if (o.MaxSalaryCents < o.MinSalaryCents) r.Error("occupations." + o.Id, "MaxSalary < MinSalary.");
                if (o.WorkplaceKinds.Count == 0) r.Error("occupations." + o.Id, "No workplace kinds.");
                if (o.ShiftLengthMinutes <= 0 || o.ShiftLengthMinutes > 16 * 60) r.Error("occupations." + o.Id, "Unreasonable shift length.");
            }
            foreach (var o in c.Occupations)
                if (!string.IsNullOrEmpty(o.PromotesTo) && !occIds.Contains(o.PromotesTo)) r.Error("occupations." + o.Id, "PromotesTo unknown occupation " + o.PromotesTo);

            var templateIds = new HashSet<string>();
            foreach (var t in c.BusinessTemplates)
            {
                if (!templateIds.Add(t.Id)) r.Error("business_templates." + t.Id, "Duplicate id.");
                if (t.CostOfGoodsRatio < 0 || t.CostOfGoodsRatio > 0.95f) r.Error("business_templates." + t.Id, "CostOfGoodsRatio out of range.");
                if (t.AverageTicketCents <= 0) r.Error("business_templates." + t.Id, "AverageTicket must be positive.");
            }

            var archetypeIds = new HashSet<string>();
            foreach (var a in c.PowerArchetypes)
            {
                if (!archetypeIds.Add(a.Id)) r.Error("power_archetypes." + a.Id, "Duplicate id.");
                if (a.Components.Count == 0) r.Error("power_archetypes." + a.Id, "Archetype has no components.");
                var evoIds = new HashSet<string>();
                foreach (var e in a.Evolutions) evoIds.Add(e.Id);
                foreach (var e in a.Evolutions)
                    if (!string.IsNullOrEmpty(e.Requires) && !evoIds.Contains(e.Requires)) r.Error("power_archetypes." + a.Id, "Evolution " + e.Id + " requires unknown " + e.Requires);
            }
            if (c.AnomalyCauses.Count == 0) r.Warn("anomaly_causes", "No anomaly causes: powers can never appear.");
            if (!c.Names.IsUsable) r.Error("names", "Name tables are empty.");

            var barkIds = new HashSet<string>();
            var topics = new HashSet<string>();
            foreach (var b in c.Barks)
            {
                if (string.IsNullOrEmpty(b.Id) || !barkIds.Add(b.Id)) r.Error("barks." + b.Id, "Missing or duplicate id.");
                if (string.IsNullOrWhiteSpace(b.Text)) r.Error("barks." + b.Id, "Empty text.");
                if (b.MinAffinity > b.MaxAffinity) r.Error("barks." + b.Id, "MinAffinity > MaxAffinity.");
                topics.Add(b.Topic);
            }
            foreach (var required in new[] { "greet", "idle", "weather", "work", "economy", "sports", "gossip", "customer", "gift", "thanks", "insulted", "threatened",
                                             "compliment_accepted", "compliment_rejected", "flirt_accepted", "flirt_rejected", "number_shared", "number_refused" })
                if (!topics.Contains(required)) r.Error("barks", "No lines for topic '" + required + "'.");

            var modelIds = new HashSet<string>();
            foreach (var v in c.VehicleModels)
            {
                if (!modelIds.Add(v.Id)) r.Error("vehicle_catalog." + v.Id, "Duplicate id.");
                if (v.Civilian && v.BasePriceCents <= 0) r.Error("vehicle_catalog." + v.Id, "Civilian model needs a price.");
                if (v.MassKg <= 0 || v.FuelCapacityLitres <= 0) r.Error("vehicle_catalog." + v.Id, "Mass and fuel capacity must be positive.");
            }
            foreach (var required in new[] { "Police", "Ambulance", "FireEngine" })
                if (!c.VehicleModels.Exists(v => v.Class.ToString() == required)) r.Error("vehicle_catalog", "Emergency services need a " + required + " model.");
            var modIds = new HashSet<string>();
            foreach (var m in c.VehicleMods)
            {
                if (!modIds.Add(m.Id)) r.Error("vehicle_mods." + m.Id, "Duplicate id.");
                foreach (var cls in m.AllowedClasses)
                    if (!System.Enum.TryParse(cls, out Core.Vehicles.VehicleClass _)) r.Error("vehicle_mods." + m.Id, "Unknown vehicle class " + cls);
            }

            var furnitureIds = new HashSet<string>();
            var providedTags = new HashSet<string>();
            foreach (var f in c.Furniture)
            {
                if (!furnitureIds.Add(f.Id)) r.Error("furniture." + f.Id, "Duplicate id.");
                if (f.Width <= 0 || f.Depth <= 0) r.Error("furniture." + f.Id, "Footprint must be positive.");
                foreach (var t in f.Tags) providedTags.Add(t);
            }
            foreach (var required in new[] { Core.Building.BuildingLayout.ColumnItem, Core.Building.BuildingLayout.StairsItem })
                if (!furnitureIds.Contains(required)) r.Error("furniture", "Generated layouts need catalog item " + required + ".");
            foreach (var req in c.BusinessRequirements)
            {
                if (!templateIds.Contains(req.TemplateId)) r.Error("business_requirements." + req.TemplateId, "Unknown business template.");
                foreach (var t in req.RequiredTags) if (!providedTags.Contains(t)) r.Error("business_requirements." + req.TemplateId, "No furniture provides tag " + t + ".");
                foreach (var z in req.AllowedZoning) if (!System.Enum.TryParse(z, out Core.Property.ZoningType _)) r.Error("business_requirements." + req.TemplateId, "Unknown zoning " + z);
            }
            foreach (var t in c.BusinessTemplates)
                if (!c.BusinessRequirements.Exists(x => x.TemplateId == t.Id)) r.Warn("business_requirements", "No permit rules for " + t.Id + " (it cannot be opened by players).");

            // Items: unique ids, and every loot table a business or the crime system uses has something in it.
            var itemIds = new HashSet<string>();
            var lootTags = new HashSet<string>();
            foreach (var i in c.Items)
            {
                if (!itemIds.Add(i.Id)) r.Error("items." + i.Id, "Duplicate item id.");
                if (i.ValueCents < 0) r.Error("items." + i.Id, "Negative value.");
                foreach (var t in i.LootTags) lootTags.Add(t);
            }
            foreach (var t in c.BusinessTemplates)
                if (!lootTags.Contains(t.LootTag)) r.Error("business_templates." + t.Id, "No items carry loot tag " + t.LootTag + ".");
            foreach (var required in RequiredLootTags)
                if (!lootTags.Contains(required)) r.Error("items", "No items carry required loot tag " + required + ".");
            foreach (var required in RequiredItems)
                if (!itemIds.Contains(required)) r.Error("items", "Missing required item " + required + ".");
            foreach (var crime in RequiredCrimes)
                if (c.FindCrime(crime) == null) r.Error("crime_types", "Missing required crime type " + crime + ".");

            var placeKeys = new HashSet<string>();
            foreach (var d in c.Layout.Districts)
            {
                foreach (var p in d.Places)
                {
                    if (!placeKeys.Add(p.Key)) r.Error("layout." + d.Key + "." + p.Key, "Duplicate place key.");
                    if (p.Business != null && !templateIds.Contains(p.Business.TemplateId))
                        r.Error("layout." + d.Key + "." + p.Key, "Unknown business template " + p.Business.TemplateId);
                }
            }
            return r;
        }
    }
}
