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
        public const string Story = "story_port_arden.json";
        public const string Ordinances = "ordinances.json";
        public const string CalendarEvents = "calendar_events.json";
        public const string RippleTemplates = "ripple_templates.json";
        public const string RadioStations = "radio_stations.json";
        public const string Destructibles = "destructibles.json";
        public const string Weapons = "weapons.json";

        /// <summary>Story Mode content (not needed by player servers).</summary>
        public static Core.Story.StoryDefinition LoadStory(string dataDirectory, string file = Story) => Read<Core.Story.StoryDefinition>(dataDirectory, file);

        /// <summary>
        /// Checks every id, place, branch target and scripted effect/condition in a story, so a writer's typo fails
        /// the build instead of soft-locking a player mid-chapter.
        /// </summary>
        public static ValidationReport ValidateStory(Core.Story.StoryDefinition s, ContentSet content)
        {
            var r = new ValidationReport();
            _storyContent = content;
            var places = new HashSet<string>();
            foreach (var d in content.Layout.Districts) foreach (var p in d.Places) places.Add(p.Name);
            var cast = new HashSet<string>();
            foreach (var c in s.Cast)
            {
                if (!cast.Add(c.Id)) r.Error("story.cast." + c.Id, "Duplicate cast id.");
                if (!string.IsNullOrEmpty(c.Occupation) && !content.Occupations.Exists(o => o.Id == c.Occupation)) r.Error("story.cast." + c.Id, "Unknown occupation " + c.Occupation);
                if (!string.IsNullOrEmpty(c.Workplace) && !places.Contains(c.Workplace)) r.Error("story.cast." + c.Id, "Unknown place " + c.Workplace);
            }
            var missions = new HashSet<string>();
            foreach (var m in s.Missions) if (!missions.Add(m.Id)) r.Error("story.missions." + m.Id, "Duplicate mission id.");
            var dialogues = new HashSet<string>();
            foreach (var d in s.Dialogues) if (!dialogues.Add(d.Id)) r.Error("story.dialogues." + d.Id, "Duplicate dialogue id.");

            void Effects(string path, List<string> list)
            {
                foreach (var e in list) CheckOp(r, path, e, effect: true, s, missions, cast);
            }
            void Where(string path, string where)
            {
                if (string.IsNullOrEmpty(where) || where == "home") return;
                if (where.StartsWith("cast:", System.StringComparison.Ordinal)) { if (!cast.Contains(where.Substring(5))) r.Error(path, "Unknown cast " + where); }
                else if (!places.Contains(where)) r.Error(path, "Unknown place '" + where + "'.");
            }
            foreach (var m in s.Missions)
            {
                var path = "story.missions." + m.Id;
                foreach (var req in m.Requires) if (!missions.Contains(req)) r.Error(path, "Requires unknown mission " + req);
                if (m.Objectives.Count == 0) r.Error(path, "No objectives.");
                Effects(path + ".OnStart", m.OnStart);
                Effects(path + ".OnComplete", m.OnComplete);
                foreach (var o in m.Objectives)
                {
                    var op = path + "." + o.Id;
                    Effects(op, o.OnComplete);
                    if (!string.IsNullOrEmpty(o.Condition)) CheckOp(r, op, o.Condition, effect: false, s, missions, cast);
                    if (!string.IsNullOrEmpty(o.Dialogue) && !dialogues.Contains(o.Dialogue)) r.Error(op, "Unknown dialogue " + o.Dialogue);
                    switch (o.Kind)
                    {
                        case Core.Story.ObjectiveKind.TalkTo:
                            if (!cast.Contains(o.Target)) r.Error(op, "Talks to unknown cast member " + o.Target);
                            break;
                        case Core.Story.ObjectiveKind.GoTo:
                        case Core.Story.ObjectiveKind.Stay:
                            Where(op, o.Target);
                            break;
                        case Core.Story.ObjectiveKind.Interact:
                            Where(op, o.Location);
                            if (string.IsNullOrEmpty(o.Target)) r.Error(op, "Interact objectives need a tag.");
                            break;
                        case Core.Story.ObjectiveKind.Condition:
                            if (string.IsNullOrEmpty(o.Condition)) r.Error(op, "Condition objective without a condition.");
                            break;
                    }
                }
            }
            foreach (var d in s.Dialogues)
            {
                var path = "story.dialogues." + d.Id;
                if (d.Node(d.Start) == null) r.Error(path, "Start node missing.");
                foreach (var n in d.Nodes)
                {
                    if (n.Speaker != "player" && n.Speaker != "narrator" && !cast.Contains(n.Speaker)) r.Error(path + "." + n.Id, "Unknown speaker " + n.Speaker);
                    if (!string.IsNullOrEmpty(n.Next) && d.Node(n.Next) == null) r.Error(path + "." + n.Id, "Next node missing: " + n.Next);
                    Effects(path + "." + n.Id, n.Effects);
                    foreach (var c in n.Choices)
                    {
                        if (!string.IsNullOrEmpty(c.Next) && d.Node(c.Next) == null) r.Error(path + "." + n.Id, "Choice leads to missing node " + c.Next);
                        Effects(path + "." + n.Id, c.Effects);
                        foreach (var cond in c.Conditions) CheckOp(r, path + "." + n.Id, cond, effect: false, s, missions, cast);
                    }
                }
            }
            foreach (var c in s.Cutscenes)
                foreach (var shot in c.Shots)
                    if (shot.Camera.StartsWith("place:", System.StringComparison.Ordinal) && !places.Contains(shot.Camera.Substring(6))) r.Error("story.cutscenes." + c.Id, "Unknown place " + shot.Camera);
            foreach (var t in s.TimeJumps)
            {
                Effects("story.timejumps." + t.Id, t.Effects);
                foreach (var ch in t.CastChanges)
                {
                    if (!cast.Contains(ch.Cast)) r.Error("story.timejumps." + t.Id, "Unknown cast " + ch.Cast);
                    if (!string.IsNullOrEmpty(ch.Occupation) && !content.Occupations.Exists(o => o.Id == ch.Occupation)) r.Error("story.timejumps." + t.Id, "Unknown occupation " + ch.Occupation);
                    if (!string.IsNullOrEmpty(ch.Workplace) && !places.Contains(ch.Workplace)) r.Error("story.timejumps." + t.Id, "Unknown place " + ch.Workplace);
                    if (!string.IsNullOrEmpty(ch.Condition)) CheckOp(r, "story.timejumps." + t.Id, ch.Condition, effect: false, s, missions, cast);
                }
                if (!string.IsNullOrEmpty(t.Cutscene) && s.Cutscene(t.Cutscene) == null) r.Error("story.timejumps." + t.Id, "Unknown cutscene " + t.Cutscene);
            }
            return r;
        }

        [System.ThreadStatic] private static ContentSet _storyContent;

        private static bool PlaceExists(string name)
        {
            if (_storyContent == null) return true;
            foreach (var d in _storyContent.Layout.Districts) foreach (var p in d.Places) if (p.Name == name) return true;
            return false;
        }

        private static void CheckOp(ValidationReport r, string path, string text, bool effect, Core.Story.StoryDefinition s, HashSet<string> missions, HashSet<string> cast)
        {
            var op = Core.Story.StoryOp.Parse(text);
            var verbs = effect ? Core.Story.StoryOp.EffectVerbs : Core.Story.StoryOp.ConditionVerbs;
            if (System.Array.IndexOf(verbs, op.Verb) < 0)
            {
                r.Error(path, "Unknown " + (effect ? "effect" : "condition") + " '" + text + "'.");
                return;
            }
            bool Num(string v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            switch (op.Verb)
            {
                case "rel":
                    var name = op.Arg(0);
                    var i = name.IndexOfAny(new[] { '+', '-', '=', '>', '<' }, 1);
                    if (i < 0 || !cast.Contains(name.Substring(0, i))) r.Error(path, "Bad relationship in '" + text + "'.");
                    break;
                case "money":
                    if (effect && !Num(op.Arg(0))) r.Error(path, "Bad amount in '" + text + "'.");
                    break;
                case "pay":
                    if (!Num(op.Arg(1))) r.Error(path, "Bad amount in '" + text + "'.");
                    break;
                case "mission":
                    var id = effect ? op.Arg(1) : Core.Story.StoryOp.SplitComparison(op.Arg(0)).name;
                    if (!missions.Contains(id)) r.Error(path, "Unknown mission in '" + text + "'.");
                    break;
                case "cutscene":
                    if (s.Cutscene(op.Arg(0)) == null) r.Error(path, "Unknown cutscene in '" + text + "'.");
                    break;
                case "timejump":
                    if (s.TimeJump(op.Arg(0)) == null) r.Error(path, "Unknown time jump in '" + text + "'.");
                    break;
                case "storm":
                    if (!Num(op.Arg(1)) || !Num(op.Arg(2))) r.Error(path, "storm:NAME:CATEGORY:HOURS expected, got '" + text + "'.");
                    break;
                case "anomaly":
                    if (op.Arg(0) != "player" || !Num(op.Arg(1)) || !Num(op.Arg(4))) r.Error(path, "anomaly:player:DAYS:DOMAINS:ELEMENTS:INTENSITY expected, got '" + text + "'.");
                    foreach (var d in op.Arg(2).Split(',')) if (!System.Enum.TryParse(d, out Core.Powers.PowerDomain _)) r.Error(path, "Unknown power domain " + d);
                    foreach (var e in op.Arg(3).Split(',')) if (!System.Enum.TryParse(e, out Core.Powers.PowerElement _)) r.Error(path, "Unknown element " + e);
                    break;
                case "cast":
                case "say":
                    if (op.Verb == "cast" && !cast.Contains(op.Arg(0)) || op.Verb == "say" && op.Arg(0) != "narrator" && !cast.Contains(op.Arg(0))) r.Error(path, "Unknown cast in '" + text + "'.");
                    break;
                case "time":
                    if (op.Arg(0) != "advance" && op.Arg(0) != "to") r.Error(path, "time:advance:H or time:to:HH:MM expected.");
                    break;
                case "ordinance":
                {
                    var ordinanceId = effect ? op.Arg(0) : Core.Story.StoryOp.SplitComparison(op.Arg(0)).name;
                    if (_storyContent != null && !_storyContent.Ordinances.Exists(o => o.Id == ordinanceId)) r.Error(path, "Unknown ordinance in '" + text + "'.");
                    if (effect && System.Array.IndexOf(new[] { "propose", "vote", "enact", "repeal" }, op.Arg(1)) < 0) r.Error(path, "ordinance:ID:propose|vote|enact|repeal expected, got '" + text + "'.");
                    break;
                }
                case "election":
                    switch (op.Arg(0))
                    {
                        case "schedule":
                        case "hold":
                            break;
                        case "candidate":
                            if (!cast.Contains(op.Arg(1)) || string.IsNullOrEmpty(op.Arg(2))) r.Error(path, "election:candidate:CAST:SLATE expected, got '" + text + "'.");
                            break;
                        case "boost":
                            if (!cast.Contains(op.Arg(1)) || !Num(op.Arg(2))) r.Error(path, "election:boost:CAST:AMOUNT expected, got '" + text + "'.");
                            break;
                        default:
                            r.Error(path, "Unknown election step in '" + text + "'.");
                            break;
                    }
                    break;
                case "disaster":
                    if (!System.Enum.TryParse(op.Arg(0), out Core.Civic.DisasterKind _)) r.Error(path, "Unknown disaster in '" + text + "'.");
                    if (!PlaceExists(string.Join(":", op.Args, 1, System.Math.Max(0, op.Args.Length - 1)))) r.Error(path, "Unknown place in '" + text + "'.");
                    break;
                case "fire":
                    if (!PlaceExists(string.Join(":", op.Args))) r.Error(path, "Unknown place in '" + text + "'.");
                    break;
                case "opinion":
                    if (System.Array.IndexOf(Core.Civic.Issues.All, op.Arg(0)) < 0 || !Num(op.Arg(1))) r.Error(path, "opinion:ISSUE:DELTA expected, got '" + text + "'.");
                    break;
                case "rep":
                {
                    var dim = effect ? op.Arg(0) : Core.Story.StoryOp.SplitComparison(op.Arg(0)).name;
                    if (!System.Enum.TryParse(dim, out Core.Identity.ReputationDimension _) || effect && !Num(op.Arg(1))) r.Error(path, "rep:DIMENSION:DELTA expected, got '" + text + "'.");
                    break;
                }
                case "transfer":
                    if (_storyContent != null && !_storyContent.Layout.Districts.Exists(d => d.Key == op.Arg(0)) || !Num(op.Arg(1)) || string.IsNullOrEmpty(op.Arg(2)))
                        r.Error(path, "transfer:DISTRICT:SHARE:NEW_OWNER[:FROM_OWNER] expected, got '" + text + "'.");
                    break;
                case "ending":
                    if (string.IsNullOrEmpty(op.Arg(0))) r.Error(path, "ending:NAME expected.");
                    break;
                case "power":
                {
                    var what = Core.Story.StoryOp.SplitComparison(op.Arg(0)).name;
                    if (what != "stage" && what != "uses") r.Error(path, "power:stage… or power:uses… expected, got '" + text + "'.");
                    break;
                }
            }
        }
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
                LayoutFile = layoutFile,
                Barks = Read<List<Core.Social.BarkLine>>(dataDirectory, Barks),
                VehicleModels = Read<List<Core.Vehicles.VehicleModel>>(dataDirectory, VehicleCatalog),
                VehicleMods = Read<List<Core.Vehicles.VehicleMod>>(dataDirectory, VehicleMods),
                Furniture = Read<List<Core.Building.FurnitureDefinition>>(dataDirectory, Furniture),
                BusinessRequirements = Read<List<Core.Building.BusinessRequirement>>(dataDirectory, BusinessRequirements),
                Items = Read<List<ItemDefinition>>(dataDirectory, Items),
                Ordinances = Read<List<Core.Civic.OrdinanceDefinition>>(dataDirectory, Ordinances),
                CalendarEvents = Read<List<Core.Civic.CalendarEvent>>(dataDirectory, CalendarEvents),
                RippleTemplates = Read<List<Core.Social.RippleTemplate>>(dataDirectory, RippleTemplates),
                RadioStations = Read<List<Core.Audio.RadioStation>>(dataDirectory, RadioStations),
                Destructibles = Read<List<DestructibleKind>>(dataDirectory, Destructibles),
                Weapons = Read<List<Core.Combat.WeaponDefinition>>(dataDirectory, Weapons),
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
            "assault", "powered_assault", "vandalism", "evading_police", "assaulting_officer", "unregistered_anomalous_activity",
        };

        /// <summary>Ordinance effect keys the civic service understands; anything else is a typo.</summary>
        public static readonly string[] OrdinanceEffects =
        {
            "RegistrationRequired", "PublicWorksBonusPercent", "FloodRiskReductionPerYear", "PropertyTaxRate", "SalesTaxRate", "PoliceBonusPercent",
            "FireBonusPercent", "RentIncreaseCapPercent", "PoliceTrustDriftPerMonth", "PoliceResponseMultiplier", "CurfewStartHour", "CurfewEndHour",
            "PermitFeePercent", "ChannelsideFootTrafficBonus",
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

            // Civic content: ordinances must target a known issue with known effects; events must name real place kinds.
            var ordinanceIds = new HashSet<string>();
            foreach (var o in c.Ordinances)
            {
                var path = "ordinances." + o.Id;
                if (string.IsNullOrEmpty(o.Id) || !ordinanceIds.Add(o.Id)) r.Error(path, "Missing or duplicate id.");
                if (System.Array.IndexOf(Core.Civic.Issues.All, o.Issue) < 0) r.Error(path, "Unknown issue " + o.Issue);
                if (o.Stance != 1 && o.Stance != -1) r.Error(path, "Stance must be 1 or -1.");
                if (o.Effects.Count == 0) r.Error(path, "An ordinance must do something.");
                foreach (var kv in o.Effects)
                    if (System.Array.IndexOf(OrdinanceEffects, kv.Key) < 0) r.Error(path, "Unknown effect " + kv.Key);
                if (o.Effects.TryGetValue("PropertyTaxRate", out var ptr) && (ptr < 0 || ptr > 0.1)) r.Error(path, "PropertyTaxRate out of range.");
                if (o.Effects.TryGetValue("SalesTaxRate", out var str) && (str < 0 || str > 0.3)) r.Error(path, "SalesTaxRate out of range.");
                if (o.Effects.ContainsKey("CurfewStartHour") != o.Effects.ContainsKey("CurfewEndHour")) r.Error(path, "Curfews need a start and an end hour.");
            }
            if (!ordinanceIds.Contains("anomaly_registration")) r.Error("ordinances", "The registration ordinance (anomaly_registration) is required by the story and the power rules.");
            var eventIds = new HashSet<string>();
            foreach (var e in c.CalendarEvents)
            {
                var path = "calendar_events." + e.Id;
                if (string.IsNullOrEmpty(e.Id) || !eventIds.Add(e.Id)) r.Error(path, "Missing or duplicate id.");
                if (e.Month < 1 || e.Month > 12 || e.Day < 1 || e.Day > System.DateTime.DaysInMonth(2031, e.Month)) r.Error(path, "Invalid date.");
                if (e.Days < 1 || e.Days > 31) r.Error(path, "Days must be 1..31.");
                foreach (var kv in e.Demand)
                {
                    if (!System.Enum.TryParse(kv.Key, out Core.World.PlaceKind _)) r.Error(path, "Unknown place kind " + kv.Key);
                    if (kv.Value <= 0f || kv.Value > 3f) r.Error(path, "Demand multiplier out of range for " + kv.Key);
                }
            }
            var rippleTopics = new HashSet<string>();
            foreach (var t in c.RippleTemplates)
            {
                rippleTopics.Add(t.Topic);
                if (t.Lines.Count == 0) r.Error("ripple_templates." + t.Topic, "No lines.");
                if (!string.IsNullOrEmpty(t.Tag) && !t.Tag.StartsWith("#", System.StringComparison.Ordinal)) r.Error("ripple_templates." + t.Topic, "Tags start with #.");
                foreach (var line in t.Lines)
                    if (line.Length > 200) r.Error("ripple_templates." + t.Topic, "Line too long for a post: " + line);
            }
            foreach (Core.World.HistoryCategory cat in System.Enum.GetValues(typeof(Core.World.HistoryCategory)))
            {
                if (cat == Core.World.HistoryCategory.Server) continue;
                var topic = Core.Simulation.RippleService.TopicOf(cat);
                if (!rippleTopics.Contains(topic)) r.Error("ripple_templates", "No templates for topic '" + topic + "'.");
            }

            // Radio: unique stations and tracks, sane lengths, known tokens only.
            var stationIds = new HashSet<string>();
            var trackIds = new HashSet<string>();
            var tokens = new[] { "{time}", "{weather}", "{temp}", "{city}", "{district}", "{headline}" };
            foreach (var s in c.RadioStations)
            {
                var path = "radio_stations." + s.Id;
                if (string.IsNullOrEmpty(s.Id) || !stationIds.Add(s.Id)) r.Error(path, "Missing or duplicate id.");
                if (s.Format == Core.Audio.StationFormat.Music && s.Tracks.Count < 3) r.Error(path, "A music station needs at least three tracks.");
                if (s.AdsPerHour < 0 || s.AdsPerHour > 12 || s.TalkPerHour < 0 || s.TalkPerHour > 12) r.Error(path, "Breaks per hour out of range.");
                if (s.TalkPerHour > 0 && s.Talk.Count == 0) r.Error(path, "Talk breaks but no host lines.");
                foreach (var t in s.Tracks)
                {
                    if (!trackIds.Add(t.Id)) r.Error(path, "Duplicate track id " + t.Id);
                    if (t.Seconds < 30 || t.Seconds > 1200) r.Error(path, "Track " + t.Id + " length out of range.");
                }
                foreach (var line in s.Talk)
                {
                    var rest = line;
                    foreach (var token in tokens) rest = rest.Replace(token, "");
                    if (rest.IndexOf('{') >= 0) r.Error(path, "Unknown token in host line: " + line);
                }
            }

            var propIds = new HashSet<string>();
            foreach (var k in c.Destructibles)
            {
                var path = "destructibles." + k.Id;
                if (string.IsNullOrEmpty(k.Id) || !propIds.Add(k.Id)) r.Error(path, "Missing or duplicate id.");
                if (k.MaxHealth <= 0f || k.MaxHealth > 20f) r.Error(path, "MaxHealth out of range.");
                if (k.WindResistance < 0f || k.WindResistance > 1f) r.Error(path, "WindResistance must be 0..1.");
                if (k.RepairCostCents < 0) r.Error(path, "Negative repair cost.");
                if (k.Effect != "" && k.Effect != "Darkness" && k.Effect != "SignalOut" && k.Effect != "WaterMain") r.Error(path, "Unknown effect " + k.Effect);
            }
            foreach (var required in new[] { "street_light", "traffic_signal", "fire_hydrant" })
                if (!propIds.Contains(required)) r.Error("destructibles", "Missing required kind " + required + ".");

            var weaponIds = new HashSet<string>();
            foreach (var wpn in c.Weapons)
            {
                var path = "weapons." + wpn.Id;
                if (string.IsNullOrEmpty(wpn.Id) || !weaponIds.Add(wpn.Id)) r.Error(path, "Missing or duplicate id.");
                if (!string.IsNullOrEmpty(wpn.ItemId) && c.FindItem(wpn.ItemId) == null) r.Error(path, "Unknown item " + wpn.ItemId);
                if (!string.IsNullOrEmpty(wpn.AmmoItemId) && c.FindItem(wpn.AmmoItemId) == null) r.Error(path, "Unknown ammunition item " + wpn.AmmoItemId);
                if (wpn.UsesAmmo && (wpn.AmmoPackSize <= 0 || wpn.AmmoPackPriceCents <= 0)) r.Error(path, "Ammunition needs a pack size and price.");
                if (c.FindCrime(wpn.CrimeId) == null) r.Error(path, "Unknown crime " + wpn.CrimeId);
                if (wpn.Damage < 0f || wpn.Damage > 1f || wpn.Range <= 0f || wpn.Range > 200f || wpn.CooldownSeconds <= 0f || wpn.Accuracy <= 0f || wpn.Accuracy > 1f)
                    r.Error(path, "Damage 0..1, range 0..200, cooldown > 0, accuracy 0..1.");
                if (wpn.Damage == 0f && wpn.StunSeconds <= 0f && wpn.BlindSeconds <= 0f) r.Error(path, "A weapon must do something.");
                foreach (var t in wpn.SoldBy) if (!templateIds.Contains(t)) r.Error(path, "Unknown shop template " + t);
                if (wpn.SoldBy.Count > 0 && wpn.PriceCents <= 0) r.Error(path, "Sold weapons need a price.");
            }
            if (c.FindWeapon("fists") == null) r.Error("weapons", "Missing required weapon fists.");
            foreach (var required in new[] { "homicide", "unlawful_discharge", "unlicensed_firearm", "street_robbery" })
                if (c.FindCrime(required) == null) r.Error("crime_types", "Missing required crime " + required + ".");

            // Water: well-formed polygons; nothing but docks, beaches and bridges in it.
            foreach (var water in c.Layout.Water)
            {
                var path = "layout.water." + water.Name;
                if (water.Points.Count < 6 || water.Points.Count % 2 != 0) r.Error(path, "A water body needs at least three corners.");
                foreach (var v in water.Points) if (float.IsNaN(v) || float.IsInfinity(v)) r.Error(path, "Non-finite coordinate.");
            }
            if (c.Layout.Water.Count > 0)
            {
                foreach (var d in c.Layout.Districts)
                    foreach (var p in d.Places)
                        if (p.Kind != PlaceKind.Dock && p.Kind != PlaceKind.Beach && c.Layout.IsWater(p.X, p.Z))
                            r.Error("layout." + d.Key + "." + p.Key, p.Name + " is in the water.");
                foreach (var d in c.Layout.Districts)
                    foreach (var b in d.Blocks)
                        for (var row = 0; row < b.Rows; row += System.Math.Max(1, b.Rows - 1))
                            for (var col = 0; col < b.Columns; col += System.Math.Max(1, b.Columns - 1))
                                if (c.Layout.IsWater(b.OriginX + col * b.SpacingX, b.OriginZ + row * b.SpacingZ))
                                    r.Error("layout." + d.Key + "." + b.KeyPrefix, "Parcel block reaches into the water.");
                foreach (var road in c.Layout.Roads)
                {
                    if (road.Kind == "bridge") continue;
                    for (var i = 0; i + 3 < road.Points.Count; i += 2)
                        for (var t = 0f; t <= 1f; t += 0.125f)
                        {
                            var x = road.Points[i] + (road.Points[i + 2] - road.Points[i]) * t;
                            var z = road.Points[i + 1] + (road.Points[i + 3] - road.Points[i + 1]) * t;
                            if (!c.Layout.IsWater(x, z)) continue;
                            r.Error("layout.roads." + road.Name, "Crosses water without being a bridge (" + x.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ", " + z.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ").");
                            t = 2f;
                            i = road.Points.Count;
                        }
                }
            }

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
