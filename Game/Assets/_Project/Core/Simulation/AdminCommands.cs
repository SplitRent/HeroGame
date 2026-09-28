using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HeroGame.Core.Simulation
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Civic;
    using HeroGame.Core.Crime;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Emergency;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.Property;
    using HeroGame.Core.Time;
    using HeroGame.Core.Weather;
    using HeroGame.Core.World;

    /// <summary>
    /// World debug/admin commands (GDD §150), engine-free so the Unity dev console, the dedicated server console, a
    /// permission-checked network request and tests all run the same code. Money and ownership changes go through the
    /// transaction processor like everything else (audited, journaled); nothing here edits balances directly.
    /// Callers decide who may run them: dev builds, the server owner's console, or accounts with WorldAdmin.
    /// </summary>
    public sealed class AdminCommands
    {
        private readonly World _w;
        private readonly WorldSimulation _sim;
        private readonly Dictionary<string, (string help, Func<Context, string> run)> _commands =
            new Dictionary<string, (string, Func<Context, string>)>(StringComparer.OrdinalIgnoreCase);

        public sealed class Context
        {
            public ServerCharacter Actor;
            public WorldPosition At;
            public string[] Args = Array.Empty<string>();
            public string AdminName = "admin";

            public string Arg(int i, string fallback = "") => i < Args.Length ? Args[i] : fallback;
            public int Int(int i, int fallback) => i < Args.Length && int.TryParse(Args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            public double Num(int i, double fallback) => i < Args.Length && double.TryParse(Args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public AdminCommands(World world, WorldSimulation simulation)
        {
            _w = world;
            _sim = simulation;
            Add("help", "List commands", c => Help());
            Add("status", "World state: time, weather, economy, events, emergencies, politics", c => Status());
            Add("time", "time +H | time HH:MM — advance the clock; the simulation catches up", Time);
            Add("days", "days N — advance N days (max 60)", c => { _sim.AdvanceDays(Math.Max(1, Math.Min(60, c.Int(0, 1)))); return "Now " + _w.Clock.Now; });
            Add("weather", "weather KIND | weather dynamic", Weather);
            Add("storm", "storm NAME CATEGORY HOURS — a tropical system making landfall in HOURS", Storm);
            Add("money", "money DOLLARS — audited admin grant to your checking account", Money);
            Add("spawnnpc", "spawnnpc [N] — N new adult residents move into the city", SpawnNpcs);
            Add("npcs", "npcs [RADIUS] — residents near you right now", NpcsNear);
            Add("npc", "npc NAME — inspect an NPC", Npc);
            Add("spawncar", "spawncar MODEL — a new vehicle registered to you, next to you ('spawncar' lists models)", SpawnCar);
            Add("own", "own nearest|PROPERTY_ID — transfer a property to you (audited)", Own);
            Add("disaster", "disaster FlashFlood|ChemicalIncident|Blackout|HeatWave [DISTRICT_KEY]", Disaster);
            Add("fire", "fire — start a fire in the nearest building", Fire);
            Add("wanted", "wanted SEVERITY — police are chasing you for a crime of that severity (1–10)", Wanted);
            Add("clearwanted", "clearwanted — end your current police episode", c => { if (c.Actor == null) return "No character."; _w.Wanted.Arrested(c.Actor.CharacterId); return "Police stood down."; });
            Add("police", "police — dispatch a patrol to your position", c => { var i = _w.Dispatch.Report(EmergencyKind.Crime, c.At, 3, "Admin test call"); return "Call " + i.Id + " queued; units assigned " + i.Units.Count; });
            Add("election", "election schedule | election run | election mayor — test elections", Election);
            Add("ordinance", "ordinance ID enact|repeal ('ordinance' lists them)", Ordinance);
            Add("business", "business nearest|NAME [days N] — inspect or fast-forward a business", Business);
            Add("props", "props [RADIUS] | props break — street furniture near you", Props);
            Add("mugging", "mugging — someone tries to rob you right here (street encounter test)", c =>
            {
                if (c.Actor == null) return "No character.";
                var e = _w.StreetCrime.Start(c.Actor, c.At);
                return e == null ? "Nobody around to try it." : e.MuggerName + " demands " + new Money(e.DemandCents) + (e.WeaponId == "fists" ? "." : " with a knife.");
            });
            Add("give", "give ITEM [N] — put items in your inventory ('give' lists items; weapons come with ammunition)", Give);
            Add("permit", "permit — issue yourself a firearm permit (skips the fee and background check)", c =>
            {
                if (c.Actor == null) return "No character.";
                c.Actor.Licenses.RemoveAll(l => l.Kind == CombatService.FirearmPermit);
                c.Actor.Licenses.Add(new Characters.License { Kind = CombatService.FirearmPermit, IssuedDay = _w.Today, ExpiresDay = _w.Today + 365 * CombatService.PermitYears });
                _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.Actor.CharacterId);
                return "Firearm permit issued.";
            });
        }

        public IEnumerable<string> Names => _commands.Keys;

        private void Add(string name, string help, Func<Context, string> run) => _commands[name] = (help, run);

        public string Execute(string line, ServerCharacter actor, WorldPosition at, string adminName = "admin")
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (!_commands.TryGetValue(parts[0], out var cmd)) return "Unknown command '" + parts[0] + "'. Try help.";
            var args = new string[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
            try
            {
                return cmd.run(new Context { Actor = actor, At = at, Args = args, AdminName = adminName ?? "admin" });
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is InvalidOperationException)
            {
                return "Error: " + ex.Message;
            }
        }

        public bool Has(string name) => _commands.ContainsKey(name ?? "");

        private string Help()
        {
            var sb = new StringBuilder();
            var names = new List<string>(_commands.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (var n in names) sb.AppendLine(n.PadRight(12) + _commands[n].help);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ inspection

        public string Status()
        {
            var w = _w;
            var sb = new StringBuilder();
            sb.AppendLine(w.Config.Identity.CityName + " · " + w.Clock.Now + " " + w.Clock.Now.DayOfWeek + " · " + w.Weather.State.Current.Kind + " " + w.Weather.State.Current.TemperatureC.ToString("0", CultureInfo.InvariantCulture) + "°C");
            sb.AppendLine("Population " + w.Population.Count + " · businesses " + w.Businesses.Count + " · players " + w.Characters.Count + " · price level " + w.Macro.PriceLevel.ToString("0.000", CultureInfo.InvariantCulture) + (w.Macro.InRecession ? " · RECESSION" : ""));
            sb.AppendLine("Ledger " + (w.Ledger.VerifyInvariant(out _) ? "balanced" : "BROKEN") + " · treasury " + w.Ledger.BalanceOf(w.Accounts.Treasury) + " · journal seq " + w.Transactions.LastSequence);
            foreach (var e in w.Calendar.ActiveOn(w.Clock.Now)) sb.AppendLine("Event: " + e.Name);
            foreach (var d in w.Disasters.Active) sb.AppendLine("Disaster: " + d.Headline);
            if (w.Weather.State.ActiveSystem != null) sb.AppendLine("Tropical system " + w.Weather.State.ActiveSystem.Name + " cat " + w.Weather.State.ActiveSystem.Category.ToString("0", CultureInfo.InvariantCulture));
            var open = 0;
            foreach (var i in w.Emergency.Incidents) if (i.Open) open++;
            var busy = 0;
            foreach (var u in w.Emergency.Units) if (!u.Free) busy++;
            sb.AppendLine("Emergency: " + open + " open incidents, " + busy + "/" + w.Emergency.Units.Count + " units busy");
            foreach (var s in w.Wanted.Snapshot()) sb.AppendLine("Wanted: " + s.Suspect + " level " + s.Level + " " + s.Phase);
            var mayor = w.Civic.Officeholders.Find(o => o.Office == Office.Mayor);
            sb.AppendLine("Mayor " + (mayor != null ? mayor.Name : "vacant") + " · ordinances " + w.Civic.Ordinances.Count + " · open elections " + w.Civic.Elections.Count);
            var broken = 0;
            foreach (var p in w.Destructibles.All) if (p.State != PropState.Intact) broken++;
            sb.Append("Street furniture broken: " + broken + " · collapsed buildings " + w.Destruction.Collapsed.Count + " · anomalies " + w.AnomalyLog.Count);
            return sb.ToString();
        }

        private string Npc(Context c)
        {
            var matches = _w.Population.FindByName(string.Join(" ", c.Args));
            if (matches.Count == 0) return "No match.";
            var n = matches[0];
            var now = _w.Schedules.Resolve(n, _w.Clock.Now);
            var place = _w.Geography.GetPlace(now.Place);
            var home = _w.Geography.GetPlace(n.Home);
            var occ = _w.Occupations.Get(n.OccupationId);
            var household = _w.Population.GetHousehold(n.Household);
            return n.FullName + " (" + n.Id + "), " + n.AgeYears(_w.Today) + ", " + (occ != null ? occ.Title : n.Employment.ToString()) +
                   "\n  now: " + now.Activity + (place != null ? " @ " + place.Name : "") + " · home " + (home != null ? home.Name : "none") +
                   "\n  household " + (household != null ? household.Members.Count + " people" : "-") + " · savings " + new Money(n.SavingsCents) + " · debt " + new Money(n.DebtCents) +
                   "\n  health " + n.Health.ToString("0.00", CultureInfo.InvariantCulture) + " · relationships " + n.Relationships.Count + (n.Powers != null && n.Powers.Powers.Count > 0 ? " · POWERED" : "");
        }

        private string NpcsNear(Context c)
        {
            var radius = (float)c.Num(0, 50);
            _w.Director.Index.Update(_w.Clock.Now);
            var ids = new HashSet<EntityId>();
            _w.Director.Index.Candidates(c.At, radius, ids);
            var list = new List<(float d, NpcRecord n)>();
            foreach (var id in ids)
                if (_w.Director.Index.TryGet(id, _w.Clock.Now, out _, out var pos))
                {
                    var d = WorldPosition.DistanceXZ(pos, c.At);
                    var npc = _w.Population.Get(id);
                    if (d <= radius && npc != null) list.Add((d, npc));
                }
            list.Sort((a, b) => a.d.CompareTo(b.d));
            var sb = new StringBuilder(list.Count + " within " + radius.ToString("0", CultureInfo.InvariantCulture) + " m");
            for (var i = 0; i < list.Count && i < 12; i++) sb.Append("\n  ").Append(list[i].n.FullName).Append(" · ").Append(list[i].d.ToString("0", CultureInfo.InvariantCulture)).Append(" m");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ time and weather

        private string Time(Context c)
        {
            if (c.Args.Length == 0) return _w.Clock.Now.ToString();
            var arg = c.Args[0];
            long seconds;
            if (arg.StartsWith("+", StringComparison.Ordinal)) seconds = (long)(double.Parse(arg.Substring(1), CultureInfo.InvariantCulture) * 3600);
            else
            {
                var hm = arg.Split(':');
                var target = int.Parse(hm[0], CultureInfo.InvariantCulture) * 60 + (hm.Length > 1 ? int.Parse(hm[1], CultureInfo.InvariantCulture) : 0);
                var delta = target - _w.Clock.Now.MinuteOfDay;
                if (delta <= 0) delta += 1440;
                seconds = delta * 60L;
            }
            if (seconds <= 0 || seconds > 60L * GameDateTime.SecondsPerDay) return "Advance between 1 second and 60 days.";
            _w.Clock.AdvanceGame(seconds);
            _sim.Update();
            return "Now " + _w.Clock.Now;
        }

        private string Weather(Context c)
        {
            if (c.Args.Length == 0) return _w.Weather.State.Current.Kind.ToString();
            if (c.Args[0].Equals("dynamic", StringComparison.OrdinalIgnoreCase))
            {
                _w.Weather.State.FixedWeather = "";
                return "Weather is dynamic.";
            }
            if (!Enum.TryParse(c.Args[0], true, out WeatherKind kind)) return "Unknown weather " + c.Args[0];
            _w.Weather.State.FixedWeather = kind.ToString();
            _w.Clock.AdvanceGame(GameDateTime.SecondsPerHour);
            _sim.Update();
            return "Weather forced to " + kind + " (use 'weather dynamic' to release).";
        }

        private string Storm(Context c)
        {
            var name = c.Arg(0, "Test");
            var category = (float)Math.Max(0.5, Math.Min(5, c.Num(1, 2)));
            var hours = Math.Max(1, Math.Min(96, c.Int(2, 24)));
            var hour = _w.Clock.Now.HourIndex;
            _w.Weather.State.ActiveSystem = new TropicalSystem { Name = name, AnnouncedHour = hour, LandfallHour = hour + hours, DurationHours = 12, Category = category };
            return "Tropical system " + name + " (category " + category.ToString("0.#", CultureInfo.InvariantCulture) + ") makes landfall in " + hours + " h.";
        }

        // ------------------------------------------------------------------ economy and ownership

        private string Give(Context c)
        {
            if (c.Actor == null) return "No character.";
            if (c.Args.Length == 0)
            {
                var ids = new List<string>();
                foreach (var i in _w.Content.Items) ids.Add(i.Id);
                return "Items: " + string.Join(", ", ids);
            }
            var item = _w.Content.FindItem(c.Args[0]);
            if (item == null) return "Unknown item '" + c.Args[0] + "'.";
            var n = Math.Max(1, Math.Min(999, c.Int(1, 1)));
            c.Actor.Inventory.Add(new Characters.InventoryStack { ItemId = item.Id, Quantity = n });
            var extra = "";
            foreach (var w in _w.Content.Weapons)
                if (w.ItemId == item.Id && w.UsesAmmo)
                {
                    c.Actor.Inventory.Add(new Characters.InventoryStack { ItemId = w.AmmoItemId, Quantity = Math.Max(1, w.AmmoPackSize) });
                    extra = " and " + Math.Max(1, w.AmmoPackSize) + " rounds";
                }
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.Actor.CharacterId);
            return "Gave " + n + " × " + item.DisplayName + extra + ".";
        }

        private string Money(Context c)
        {
            if (c.Actor == null) return "No character.";
            var dollars = c.Num(0, 1000);
            if (dollars <= 0 || dollars > 10_000_000) return "Grant between $1 and $10,000,000.";
            var result = _w.AdminGrant(c.Actor.CheckingAccount, Foundation.Money.FromDollars(dollars), c.AdminName, "debug grant");
            return result.Success ? "Balance " + _w.Ledger.BalanceOf(c.Actor.CheckingAccount) : result.Error;
        }

        private string Own(Context c)
        {
            if (c.Actor == null) return "No character.";
            PropertyRecord p = null;
            if (c.Arg(0) == "nearest" || c.Args.Length == 0)
            {
                var best = float.MaxValue;
                foreach (var prop in _w.Properties.All)
                {
                    var place = _w.Geography.GetPlace(prop.Place);
                    if (place == null) continue;
                    var d = WorldPosition.DistanceXZ(place.Position, c.At);
                    if (d < best || d == best && p != null && prop.Id.CompareTo(p.Id) < 0) { best = d; p = prop; }
                }
            }
            else if (EntityId.TryParse(c.Arg(0), out var id)) p = _w.Properties.Get(id);
            if (p == null) return "No such property.";
            var from = _w.Ownership.OwnerOf(p.Id);
            if (from == c.Actor.CharacterId) return "You already own " + p.Address + ".";
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                Source = TransactionSource.Admin, Initiator = c.Actor.CharacterId, Timestamp = _w.Clock.Now,
                Description = "Admin property transfer by " + c.AdminName,
                Ownership = { new OwnershipChange { Asset = p.Id, From = from, To = c.Actor.CharacterId } },
            });
            if (!result.Success) return result.Error;
            p.ForSale = false;
            _w.Dirty.Mark(SaveChunks.Properties);
            return "You now own " + p.Address + " (" + p.Id + ").";
        }

        private string SpawnCar(Context c)
        {
            if (c.Args.Length == 0)
            {
                var sb = new StringBuilder();
                foreach (var m in _w.Vehicles.Models) sb.Append(m.Id).Append(' ');
                return sb.ToString();
            }
            if (c.Actor == null) return "No character.";
            if (_w.Vehicles.Model(c.Args[0]) == null) return "Unknown model " + c.Args[0];
            var v = _w.Vehicles.Spawn(c.Args[0], _w.Accounts.Government, new WorldPosition(c.At.X + 4f, c.At.Y, c.At.Z), 0f, _w.Clock.Now);
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                Source = TransactionSource.Admin, Initiator = c.Actor.CharacterId, Timestamp = _w.Clock.Now,
                Description = "Admin vehicle grant by " + c.AdminName,
                Ownership = { new OwnershipChange { Asset = v.Id, From = _w.Accounts.Government, To = c.Actor.CharacterId } },
            });
            _w.Dirty.Mark(SaveChunks.Vehicles);
            return result.Success ? "Spawned " + _w.Vehicles.Model(v.ModelId).DisplayName + " " + v.Plate + " (" + v.Id + ")." : result.Error;
        }

        // ------------------------------------------------------------------ population

        private string SpawnNpcs(Context c)
        {
            var count = Math.Max(1, Math.Min(50, c.Int(0, 1)));
            var homes = new List<Place>();
            foreach (var p in _w.Geography.Places) if (p.Kind == PlaceKind.Residence || p.Kind == PlaceKind.ApartmentBuilding) homes.Add(p);
            homes.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (homes.Count == 0) return "No homes to move into.";
            var rng = DeterministicRandom.For(_w.Seed, (ulong)_w.Clock.Now.TotalSeconds, 0x5A3E);
            var names = _w.Content.Names;
            var created = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var home = homes[rng.NextInt(0, homes.Count)];
                var sex = rng.Chance(0.5) ? Sex.Female : Sex.Male;
                var last = rng.Pick(names.Last);
                var first = names.PickFirst(rng, sex, last);
                var household = new Household { Id = _w.Ids.Next(EntityKind.Household), Surname = last, Home = home.Id };
                _w.Population.Add(household);
                var npc = new NpcRecord
                {
                    Id = _w.Ids.Next(EntityKind.Npc), FirstName = first, LastName = last, Sex = sex,
                    BirthDay = _w.Today - (22 + rng.NextInt(0, 40)) * 365L, AppearanceSeed = rng.NextULong(),
                    Household = household.Id, Home = home.Id, Employment = EmploymentStatus.Unemployed,
                    Education = rng.Chance(0.35) ? EducationLevel.Bachelors : EducationLevel.HighSchool,
                    Personality = new Personality { Openness = (float)rng.NextDouble(), Conscientiousness = (float)rng.NextDouble(), Extraversion = (float)rng.NextDouble(), Agreeableness = (float)rng.NextDouble(), Neuroticism = (float)rng.NextDouble() },
                    SavingsCents = 200000 + rng.NextInt(0, 800000), LastSimulatedDay = _w.Today,
                };
                household.Members.Add(npc.Id);
                _w.Population.Add(npc);
                created.Add(npc.FullName);
            }
            _w.Dirty.Mark(SaveChunks.Population);
            _w.WorkplaceVersion++;
            _w.History.Record(_w.Today, HistoryCategory.People, 1, count == 1 ? created[0] + " moves to " + _w.Config.Identity.CityName : count + " newcomers move to " + _w.Config.Identity.CityName);
            return "Moved in: " + string.Join(", ", created);
        }

        // ------------------------------------------------------------------ events and emergencies

        private string Disaster(Context c)
        {
            if (!Enum.TryParse(c.Arg(0), true, out DisasterKind kind)) return "disaster FlashFlood|ChemicalIncident|Blackout|HeatWave [DISTRICT_KEY]";
            District district = null;
            foreach (var d in _w.Geography.Districts)
                if (c.Args.Length > 1 ? d.Key == c.Args[1] : district == null || WorldPosition.DistanceXZ(d.Center, c.At) < WorldPosition.DistanceXZ(district.Center, c.At)) district = d;
            if (district == null && kind != DisasterKind.HeatWave) return "Unknown district.";
            _w.Calendar.Trigger(kind, district, _w.Clock.Now);
            return kind + " started" + (district != null && kind != DisasterKind.HeatWave ? " in " + district.Name : "") + ".";
        }

        private string Fire(Context c)
        {
            PropertyRecord best = null;
            var bestD = float.MaxValue;
            foreach (var p in _w.Properties.All)
            {
                if (p.Kind == PropertyKind.Land || p.Damage == DamageState.Destroyed) continue;
                var place = _w.Geography.GetPlace(p.Place);
                if (place == null) continue;
                var d = WorldPosition.DistanceXZ(place.Position, c.At);
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best == null) return "No building nearby.";
            var incident = _w.Dispatch.ReportFire(best, 0.3f, "admin test");
            return "Fire at " + best.Address + " (" + incident.Id + "), " + incident.Units.Count + " units responding.";
        }

        private string Wanted(Context c)
        {
            if (c.Actor == null) return "No character.";
            var severity = Math.Max(1, Math.Min(10, c.Int(0, 5)));
            CrimeType type = null;
            foreach (var t in _w.Content.CrimeTypes) if (type == null || Math.Abs(t.Severity - severity) < Math.Abs(type.Severity - severity)) type = t;
            if (type == null) return "No crime types.";
            var incident = new CrimeIncident
            {
                Id = _w.Ids.Next(EntityKind.CrimeIncident), CrimeTypeId = type.Id, Perpetrator = c.Actor.CharacterId, OccurredAt = _w.Clock.Now, Position = c.At,
            };
            _w.Justice.Incidents.Add(incident);
            var status = _w.Wanted.ReportCrime(incident, type, _w.Clock.Now, policeWitnessed: true);
            _w.Dispatch.ReportCrime(incident, type);
            _w.Dirty.Mark(SaveChunks.Justice);
            return "Wanted level " + status.Level + " for " + type.DisplayName + " (test incident " + incident.Id + ").";
        }

        // ------------------------------------------------------------------ politics

        private string Election(Context c)
        {
            var gov = _w.Government;
            switch (c.Arg(0, "schedule").ToLowerInvariant())
            {
                case "schedule":
                    if (_w.Civic.Elections.Count > 0) return "Elections already open; election day " + _w.Civic.Elections[0].ElectionDay + ".";
                    gov.ScheduleElections(_w.Today);
                    return "Elections scheduled for day " + _w.Civic.Elections[0].ElectionDay + ".";
                case "run":
                    if (_w.Civic.Elections.Count == 0) gov.ScheduleElections(_w.Today);
                    var until = _w.Civic.Elections[0].ElectionDay;
                    for (var d = _w.Today + 1; d <= until; d++) gov.ProcessDay(d);
                    var sb = new StringBuilder();
                    for (var i = _w.Civic.PastElections.Count - 1; i >= 0 && i >= _w.Civic.PastElections.Count - 8; i--)
                    {
                        var e = _w.Civic.PastElections[i];
                        var winner = e.Candidates.Find(x => x.Person == e.Winner);
                        sb.AppendLine((e.Office == Office.Mayor ? "Mayor" : e.District + " council") + ": " + (winner != null ? winner.Name + " (" + winner.Votes + " of " + e.Turnout + ")" : "no winner"));
                    }
                    return sb.ToString().TrimEnd();
                case "mayor":
                    if (c.Actor == null) return "No character.";
                    _w.Civic.Officeholders.RemoveAll(o => o.Office == Office.Mayor);
                    _w.Civic.Officeholders.Add(new Officeholder { Office = Office.Mayor, Person = c.Actor.CharacterId, Name = c.AdminName, IsPlayer = true, Slate = Slates.Independent, TermEndsDay = _w.Today + 180 });
                    _w.Dirty.Mark(SaveChunks.Civic);
                    return "You are the mayor (test).";
                default:
                    return "election schedule | election run | election mayor";
            }
        }

        private string Ordinance(Context c)
        {
            var gov = _w.Government;
            if (c.Args.Length == 0)
            {
                var sb = new StringBuilder();
                foreach (var o in gov.Ordinances) sb.AppendLine((_w.Civic.IsActive(o.Id) ? "[in force] " : "           ") + o.Id + " — " + o.Title);
                return sb.ToString().TrimEnd();
            }
            var def = gov.Ordinance(c.Args[0]);
            if (def == null) return "Unknown ordinance.";
            if (c.Arg(1, "enact") == "repeal") gov.Repeal(def);
            else gov.Enact(def);
            return def.Title + (_w.Civic.IsActive(def.Id) ? " is in force." : " is not in force.");
        }

        // ------------------------------------------------------------------ businesses and props

        private string Business(Context c)
        {
            Business.BusinessRecord b = null;
            if (c.Arg(0, "nearest") == "nearest")
            {
                var best = float.MaxValue;
                foreach (var biz in _w.Businesses.Values)
                {
                    var place = _w.Geography.GetPlace(biz.Place);
                    if (place == null) continue;
                    var d = WorldPosition.DistanceXZ(place.Position, c.At);
                    if (d < best || d == best && b != null && biz.Id.CompareTo(b.Id) < 0) { best = d; b = biz; }
                }
            }
            else
                foreach (var biz in _w.Businesses.Values)
                    if (biz.Name.IndexOf(c.Args[0], StringComparison.OrdinalIgnoreCase) >= 0 && (b == null || biz.Id.CompareTo(b.Id) < 0)) b = biz;
            if (b == null) return "No business found.";
            if (c.Arg(1) == "days") _sim.AdvanceDays(Math.Max(1, Math.Min(60, c.Int(2, 7))));
            var sb = new StringBuilder(b.Name + " (" + b.TemplateId + ") · owner " + _w.Ownership.OwnerOf(b.Id) + " · till " + _w.Ledger.BalanceOf(b.Account) +
                                       " · staff " + b.Staff + " · reputation " + (int)(b.Reputation * 100) + " · " + (b.Open ? "open" : "closed"));
            for (var i = b.Reports.Count - 1; i >= 0 && i >= b.Reports.Count - 5; i--)
            {
                var r = b.Reports[i];
                sb.Append("\n  day ").Append(r.Day).Append(": ").Append(r.WasClosed ? "closed" : r.Customers + " customers, revenue " + new Money(r.RevenueCents)).Append(", profit ").Append(new Money(r.ProfitCents));
            }
            return sb.ToString();
        }

        private string Props(Context c)
        {
            var near = new List<PropInstance>();
            if (c.Arg(0) == "break")
            {
                _w.Destructibles.Query(c.At, 40f, near);
                if (near.Count == 0) return "Nothing nearby.";
                near.Sort((a, b) => WorldPosition.DistanceXZ(a.Position, c.At).CompareTo(WorldPosition.DistanceXZ(b.Position, c.At)));
                var p = near[0];
                _w.Destructibles.Damage(p, 100f, "admin");
                return p.Kind + " #" + p.Id + " destroyed.";
            }
            _w.Destructibles.Query(c.At, (float)c.Num(0, 60), near);
            var sb = new StringBuilder(near.Count + " props");
            foreach (var p in near) if (p.State != PropState.Intact) sb.Append("\n  ").Append(p.Kind).Append(" #").Append(p.Id).Append(' ').Append(p.State);
            return sb.ToString();
        }
    }
}
