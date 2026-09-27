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
