#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROGAME_SERVER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HeroGame.Runtime.DevTools
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Powers;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.Time;
    using HeroGame.Core.Weather;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// Developer command set (GDD §150). Kept separate from the console UI so the same commands can
    /// be issued by the headless host, automated tests, or a server admin RPC (which checks
    /// <c>ServerPermission.WorldAdmin</c> first). Money grants go through the audited admin path —
    /// debug tools never create currency silently.
    /// </summary>
    public sealed class DevCommands
    {
        private readonly Dictionary<string, (string help, Func<GameSession, string[], string> run)> _commands =
            new Dictionary<string, (string, Func<GameSession, string[], string>)>(StringComparer.OrdinalIgnoreCase);

        public Func<UnityEngine.Vector3> PlayerPosition;

        public DevCommands()
        {
            Add("help", "List commands", (s, a) => Help() + Admin(s).Execute("help", s.LocalCharacter, new WorldPosition()));
            Add("time", "time +H | time HH:MM — advance the world clock (simulation catches up)", Time);
            Add("days", "days N — advance N days", (s, a) => { s.Simulation.AdvanceDays(Int(a, 0, 1)); return "Now " + s.World.Clock.Now; });
            Add("weather", "weather Clear|Thunderstorm|Hurricane|... | weather dynamic", Weather);
            Add("money", "money AMOUNT — audited admin grant to your checking account", Money);
            Add("anomaly", "anomaly [causeId] — trigger an anomaly event at your position", Anomaly);
            Add("power", "power ARCHETYPE — grant a power (manifesting) to your character", Power);
            Add("npc", "npc NAME — inspect an NPC", Npc);
            Add("status", "World status", (s, a) => Status(s));
            Add("save", "Save now", (s, a) => { var r = s.Save(); return "Saved generation " + r.Generation + " (" + r.ChunksWritten + " chunks)"; });
            Add("buyable", "List properties for sale", Buyable);
            Add("car", "car MODEL — buy a new car at list price (needs cash) and park it next to you; 'car' lists models", Car);
            Add("give", "give ITEM [N] — add an item (tools such as crowbar, lockpick_set, ski_mask); 'give' lists items", Give);
            Add("mask", "mask — pull the ski mask on or off (needs one in your inventory)", (s, a) =>
            {
                Crime.PlayerConcealment.MaskOn = !Crime.PlayerConcealment.MaskOn;
                return Crime.PlayerConcealment.MaskOn ? "Mask on." : "Mask off.";
            });
            Add("arrest", "arrest — police take you in (as if caught in a pursuit)", (s, a) =>
            {
                var r = s.World.Courts.Arrest(s.LocalCharacter, caughtInPursuit: s.World.Wanted.Get(s.LocalCharacter.CharacterId) != null, resisted: false);
                return r.Success ? s.World.Justice.OpenCaseFor(s.LocalCharacter.CharacterId)?.Summary ?? "Booked." : r.Error;
            });
        }

        public IEnumerable<string> Names => _commands.Keys;

        private AdminCommands _admin;
        private World _adminWorld;

        /// <summary>World commands shared with the dedicated server and the admin.cmd request (spawn, events, politics…).</summary>
        private AdminCommands Admin(GameSession s)
        {
            if (_admin == null || _adminWorld != s.World)
            {
                _admin = new AdminCommands(s.World, s.Simulation);
                _adminWorld = s.World;
            }
            return _admin;
        }

        private static string Give(GameSession s, string[] a)
        {
            if (s.LocalCharacter == null) return "No character.";
            if (a.Length == 0)
            {
                var sb = new StringBuilder();
                foreach (var i in s.World.Content.Items) sb.Append(i.Id).Append(' ');
                return sb.ToString();
            }
            var item = s.World.Content.FindItem(a[0]);
            if (item == null) return "Unknown item " + a[0];
            var n = Int(a, 1, 1);
            s.LocalCharacter.Inventory.Add(new Core.Characters.InventoryStack { ItemId = item.Id, Quantity = Math.Max(1, n) });
            s.World.Dirty.Mark(SaveChunks.CharacterPrefix + s.LocalCharacter.CharacterId);
            return "Added " + n + " × " + item.DisplayName;
        }

        public string Execute(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return "No active session.";
            var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (!_commands.TryGetValue(parts[0], out var cmd))
            {
                var admin = Admin(session);
                if (!admin.Has(parts[0])) return "Unknown command '" + parts[0] + "'. Try help.";
                var at = PlayerPosition != null ? PlayerPosition().ToWorld() : new WorldPosition();
                return admin.Execute(line, session.LocalCharacter, at, "dev-console");
            }
            var args = new string[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
            try
            {
                return cmd.run(session, args);
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        private void Add(string name, string help, Func<GameSession, string[], string> run) => _commands[name] = (help, run);

        private string Help()
        {
            var sb = new StringBuilder();
            foreach (var kv in _commands) sb.AppendLine(kv.Key.PadRight(9) + kv.Value.help);
            return sb.ToString();
        }

        private static string Time(GameSession s, string[] a)
        {
            if (a.Length == 0) return s.World.Clock.Now.ToString();
            var arg = a[0];
            long seconds;
            if (arg.StartsWith("+", StringComparison.Ordinal)) seconds = (long)(double.Parse(arg.Substring(1), CultureInfo.InvariantCulture) * 3600);
            else
            {
                var hm = arg.Split(':');
                var target = int.Parse(hm[0], CultureInfo.InvariantCulture) * 60 + (hm.Length > 1 ? int.Parse(hm[1], CultureInfo.InvariantCulture) : 0);
                var delta = target - s.World.Clock.Now.MinuteOfDay;
                if (delta <= 0) delta += 1440;
                seconds = delta * 60L;
            }
            s.World.Clock.AdvanceGame(seconds);
            s.Simulation.Update();
            return "Now " + s.World.Clock.Now;
        }

        private static string Weather(GameSession s, string[] a)
        {
            if (a.Length == 0) return s.World.Weather.State.Current.Kind.ToString();
            if (a[0].Equals("dynamic", StringComparison.OrdinalIgnoreCase))
            {
                s.World.Weather.State.FixedWeather = "";
                return "Weather is dynamic.";
            }
            if (!Enum.TryParse(a[0], true, out WeatherKind kind)) return "Unknown weather " + a[0];
            s.World.Weather.State.FixedWeather = kind.ToString();
            s.World.Clock.AdvanceGame(GameDateTime.SecondsPerHour);
            s.Simulation.Update();
            return "Weather forced to " + kind + " (use 'weather dynamic' to release).";
        }

        private static string Money(GameSession s, string[] a)
        {
            if (s.LocalCharacter == null) return "No character.";
            var dollars = double.Parse(a.Length > 0 ? a[0] : "1000", CultureInfo.InvariantCulture);
            var result = s.World.AdminGrant(s.LocalCharacter.CheckingAccount, Core.Foundation.Money.FromDollars(dollars), "dev-console", "debug grant");
            return result.Success ? "Balance " + s.World.Ledger.BalanceOf(s.LocalCharacter.CheckingAccount) : result.Error;
        }

        private string Anomaly(GameSession s, string[] a)
        {
            var world = s.World;
            var pos = PlayerPosition != null ? PlayerPosition().ToWorld() : new WorldPosition();
            var district = world.Geography.DistrictAt(pos);
            var rng = DeterministicRandom.For(world.Seed, (ulong)world.Clock.Now.TotalSeconds, 0xDE);
            var evt = world.Anomalies.Create(district != null ? district.Id : EntityId.None, district != null ? district.Type.ToString() : "", pos, 5f,
                world.Clock.Now, rng, world.Ids, a.Length > 0 ? a[0] : null);
            if (evt == null) return "No anomaly cause matched.";
            var character = s.LocalCharacter;
            if (character != null)
            {
                world.OnlineCharacters = () => new[] { new ExposureCandidate { Character = character.CharacterId, Position = pos, Openness = 1f, Powers = character.Powers } };
            }
            s.Simulation.TriggerAnomaly(evt);
            world.OnlineCharacters = null;
            var gained = character != null && character.Powers.Powers.Count > 0 ? " You feel... different." : "";
            return "Anomaly '" + evt.CauseId + "' (intensity " + evt.Signature.Intensity.ToString("0.00") + ", radius " + evt.Radius.ToString("0") + " m), " + evt.Exposed.Count + " exposed." + gained;
        }

        private static string Power(GameSession s, string[] a)
        {
            if (s.LocalCharacter == null || a.Length == 0) return "Usage: power ARCHETYPE";
            var archetype = s.World.PowerGenerator.Find(a[0]);
            if (archetype == null)
            {
                var names = new List<string>();
                foreach (var ar in s.World.PowerGenerator.Archetypes) names.Add(ar.Id);
                return "Unknown archetype. Known: " + string.Join(", ", names);
            }
            var def = s.World.PowerGenerator.Instantiate(archetype, new AnomalySignature { Intensity = 0.5f }, new DeterministicRandom((ulong)s.World.Clock.Now.TotalSeconds));
            s.LocalCharacter.Powers.Powers.Add(new PowerInstance { Definition = def, Stage = PowerStage.Manifesting, AcquiredDay = s.World.Today, StageChangedDay = s.World.Today, ManifestDay = s.World.Today });
            return "Granted " + def.Describe() + " (manifesting).";
        }

        private static string Npc(GameSession s, string[] a)
        {
            var matches = s.World.Population.FindByName(string.Join(" ", a));
            if (matches.Count == 0) return "No match.";
            var n = matches[0];
            var now = s.World.Schedules.Resolve(n, s.World.Clock.Now);
            var place = s.World.Geography.GetPlace(now.Place);
            var occ = s.World.Occupations.Get(n.OccupationId);
            return n.FullName + " (" + n.Id + "), " + n.AgeYears(s.World.Today) + " — " + (occ != null ? occ.Title : n.Employment.ToString()) +
                   "\n  now: " + now.Activity + (place != null ? " @ " + place.Name : "") +
                   "\n  savings " + new Money(n.SavingsCents) + ", debt " + new Money(n.DebtCents) + ", relationships " + n.Relationships.Count;
        }

        private static string Buyable(GameSession s, string[] a)
        {
            var sb = new StringBuilder();
            var count = 0;
            foreach (var p in s.World.Properties.All)
            {
                if (!p.ForSale) continue;
                sb.AppendLine(p.Address.PadRight(36) + p.Kind.ToString().PadRight(12) + new Money(p.ListingPriceCents));
                if (++count >= 15) break;
            }
            return count == 0 ? "Nothing for sale." : sb.ToString();
        }

        private string Car(GameSession s, string[] a)
        {
            if (a.Length == 0)
            {
                var sb = new StringBuilder();
                foreach (var m in s.World.Vehicles.Models)
                    if (m.Civilian) sb.AppendLine(m.Id.PadRight(20) + m.DisplayName.PadRight(26) + new Money(m.BasePriceCents));
                return sb.ToString();
            }
            if (s.LocalCharacter == null) return "No character.";
            var dealer = EntityId.None;
            foreach (var b in s.World.Businesses.Values) if (b.TemplateId == "car_dealership") dealer = b.Account;
            if (!dealer.IsValid) return "No dealership in this world.";
            var result = s.World.Vehicles.BuyNew(a[0], s.LocalCharacter.CharacterId, s.LocalCharacter.CheckingAccount, dealer, s.World.Accounts.Treasury,
                s.World.Clock.Now, "dev-car:" + s.World.Clock.Now.TotalSeconds, s.World.Config.Economy.PropertyPriceMultiplier, "", out var v);
            if (!result.Success) return result.Error;
            var pos = PlayerPosition != null ? PlayerPosition() + UnityEngine.Vector3.right * 4f : UnityEngine.Vector3.zero;
            s.World.Vehicles.TakeOut(v, pos.ToWorld(), 0f);
            return "Bought " + s.World.Vehicles.Model(v.ModelId).DisplayName + " (" + v.Plate + ").";
        }

        private static string Status(GameSession s)
        {
            var w = s.World;
            return w.Config.Identity.CityName + " " + w.Clock.Now + " " + w.Clock.Now.DayOfWeek +
                   "\nweather " + w.Weather.State.Current.Kind + " " + w.Weather.State.Current.TemperatureC.ToString("0") + "°C" +
                   "\npopulation " + w.Population.Count + ", businesses " + w.Businesses.Count + ", anomalies " + w.AnomalyLog.Count +
                   "\nledger " + (w.Ledger.VerifyInvariant(out _) ? "balanced" : "BROKEN") + ", journal seq " + w.Transactions.LastSequence;
        }

        private static int Int(string[] a, int index, int fallback) => a.Length > index ? int.Parse(a[index], CultureInfo.InvariantCulture) : fallback;
    }
}
#endif
