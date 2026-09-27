using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.World;
using HeroGame.Persistence.Content;
using HeroGame.Persistence.Saves;
using HeroGame.Persistence.Storage;

namespace HeroGame.WorldHost
{
    /// <summary>
    /// herogame-world — headless authoritative world host.
    ///
    ///   herogame-world new     --save DIR [--data DIR] [--config FILE] [--layout FILE] [--server-id ID]
    ///   herogame-world run     --save DIR --days N [--data DIR]
    ///   herogame-world resume  --save DIR [--data DIR]          (applies real-time offline catch-up)
    ///   herogame-world inspect --save DIR [--data DIR] [economy|npc NAME|business|history|news|district]
    ///   herogame-world bench   [--layout FILE] [--days N] [--data DIR]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help")
            {
                PrintHelp();
                return 0;
            }
            var opts = Options.Parse(args);
            try
            {
                switch (args[0])
                {
                    case "new": return New(opts);
                    case "run": return Run(opts);
                    case "resume": return Resume(opts);
                    case "inspect": return Inspect(opts);
                    case "bench": return Bench(opts);
                    default:
                        Console.Error.WriteLine("Unknown command " + args[0]);
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static int New(Options o)
        {
            var content = LoadContent(o);
            var config = ContentLoader.LoadServerConfig(o.Get("config", Path.Combine(o.DataDir, ContentLoader.DefaultServerConfig)));
            var saves = new WorldSaveSystem(o.Require("save"));
            if (saves.Exists && !o.Has("force")) throw new InvalidOperationException("A world already exists there (use --force to overwrite).");
            var watch = Stopwatch.StartNew();
            using (var journal = saves.OpenJournal())
            {
                var world = WorldGenerator.Create(o.Get("server-id", "dev-local"), config, content, journal);
                var gen = watch.Elapsed.TotalMilliseconds;
                var result = saves.Save(world, full: true);
                Console.WriteLine("Created world '" + world.ServerId + "' (" + config.Identity.CityName + ") in " + gen.ToString("0") + " ms");
                PrintStatus(world);
                Console.WriteLine("Saved " + result.ChunksWritten + " chunks, " + (result.Bytes / 1024) + " KiB in " + result.Milliseconds.ToString("0") + " ms");
            }
            return 0;
        }

        private static int Run(Options o)
        {
            var days = int.Parse(o.Get("days", "1"));
            WithWorld(o, (world, saves, journal) =>
            {
                var sim = new WorldSimulation(world);
                var watch = Stopwatch.StartNew();
                sim.AdvanceDays(days);
                var ms = watch.Elapsed.TotalMilliseconds;
                Console.WriteLine("Simulated " + days + " day(s) in " + ms.ToString("0") + " ms (" + (ms / Math.Max(1, days)).ToString("0.0") + " ms/day; population " +
                                  sim.Stats.LastPopulationMilliseconds.ToString("0.0") + " ms, businesses " + sim.Stats.LastBusinessMilliseconds.ToString("0.0") + " ms on the last day)");
                Save(world, saves, journal);
                PrintStatus(world);
            });
            return 0;
        }

        private static int Resume(Options o)
        {
            WithWorld(o, (world, saves, journal) =>
            {
                var manifestTime = File.GetLastWriteTimeUtc(Path.Combine(o.Require("save"), WorldSaveSystem.ManifestFile));
                var realSeconds = Math.Max(0, (DateTime.UtcNow - manifestTime).TotalSeconds);
                var gameSeconds = (long)(realSeconds * world.Clock.TimeScale);
                Console.WriteLine("Server was offline for " + TimeSpan.FromSeconds(realSeconds).ToString(@"d\.hh\:mm\:ss") + " real time → catching up " +
                                  (gameSeconds / 3600.0).ToString("0.0") + " game hours (capped at " + WorldSimulation.MaxCatchUpDays + " days).");
                world.Clock.AdvanceGame(gameSeconds);
                var sim = new WorldSimulation(world);
                var watch = Stopwatch.StartNew();
                sim.Update();
                Console.WriteLine("Catch-up simulated " + sim.Stats.DaysSimulated + " day(s) in " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms.");
                Save(world, saves, journal);
                PrintStatus(world);
            });
            return 0;
        }

        private static int Inspect(Options o)
        {
            WithWorld(o, (world, saves, journal) =>
            {
                var topic = o.Positional.Count > 0 ? o.Positional[0] : "status";
                switch (topic)
                {
                    case "economy": Inspector.Economy(world); break;
                    case "npc": Inspector.Npc(world, o.Positional.Count > 1 ? string.Join(" ", o.Positional.Skip(1)) : ""); break;
                    case "business": Inspector.Businesses(world); break;
                    case "history": Inspector.History(world); break;
                    case "news": Inspector.News(world); break;
                    case "district": Inspector.Districts(world); break;
                    default: PrintStatus(world); break;
                }
            }, save: false);
            return 0;
        }

        private static int Bench(Options o)
        {
            var content = ContentLoader.Load(o.DataDir, o.Get("layout", "layout_stress.json"));
            var config = ContentLoader.LoadServerConfig(Path.Combine(o.DataDir, ContentLoader.DefaultServerConfig));
            var days = int.Parse(o.Get("days", "7"));
            var dir = Path.Combine(Path.GetTempPath(), "herogame-bench-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var saves = new WorldSaveSystem(dir);
            using (var journal = saves.OpenJournal())
            {
                var watch = Stopwatch.StartNew();
                var world = WorldGenerator.Create("bench", config, content, journal);
                var genMs = watch.Elapsed.TotalMilliseconds;
                Console.WriteLine("Generated " + world.Population.Count + " NPCs, " + world.Population.HouseholdCount + " households, " + world.Geography.PlaceCount +
                                  " places, " + world.Businesses.Count + " businesses in " + genMs.ToString("0") + " ms");

                var sim = new WorldSimulation(world);
                watch.Restart();
                sim.AdvanceDays(days);
                var simMs = watch.Elapsed.TotalMilliseconds;
                var npcDays = (double)world.Population.Count * days;
                Console.WriteLine("Simulated " + days + " days in " + simMs.ToString("0") + " ms → " + (simMs / days).ToString("0.0") + " ms/day, " +
                                  (simMs * 1000.0 / npcDays).ToString("0.00") + " µs per NPC-day");

                var observers = new List<WorldPosition>();
                foreach (var p in world.Geography.Places.Take(8)) observers.Add(p.Position);
                watch.Restart();
                world.Director.Evaluate(observers, world.Clock.Now);
                Console.WriteLine("Population director: initial location index build " + watch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms (" + world.Director.LastSchedulesResolved + " schedules)");
                watch.Restart();
                var requests = 0;
                const int evals = 50;
                // Realistic cadence: one evaluation per 1.5 real seconds = 45 game seconds at the default time scale.
                for (var i = 0; i < evals; i++) requests = world.Director.Evaluate(observers, world.Clock.Now.AddSeconds(i * 45)).Count;
                Console.WriteLine("Population director: steady state " + (watch.Elapsed.TotalMilliseconds / evals).ToString("0.00") + " ms per evaluation (8 observers, " + requests + " materialised, " + world.Director.LastCandidates + " candidates, " + world.Director.LastSchedulesResolved + " schedules re-resolved on the last call)");

                watch.Restart();
                var full = saves.Save(world, full: true);
                Console.WriteLine("Full save: " + full.ChunksWritten + " chunks, " + (full.Bytes / 1024) + " KiB in " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms");
                sim.AdvanceDays(1);
                watch.Restart();
                var inc = saves.Save(world);
                Console.WriteLine("Incremental save after 1 day: " + inc.ChunksWritten + " written, " + inc.ChunksSkipped + " skipped, " + (inc.Bytes / 1024) + " KiB in " + watch.Elapsed.TotalMilliseconds.ToString("0") +
                                  " ms (snapshot " + inc.SnapshotMilliseconds.ToString("0") + " ms + write/commit " + inc.WriteMilliseconds.ToString("0") + " ms)");
                sim.AdvanceDays(1);
                watch.Restart();
                var background = saves.SaveInBackground(world);
                var blocked = watch.Elapsed.TotalMilliseconds;
                var bg = background.Result;
                Console.WriteLine("Background save after 1 day: simulation thread blocked " + blocked.ToString("0") + " ms, write/commit " + bg.WriteMilliseconds.ToString("0") + " ms on a background thread");
                world.Clock.AdvanceGame(3 * 3600);
                sim.Update();
                watch.Restart();
                var routine = saves.SaveInBackground(world);
                var routineBlocked = watch.Elapsed.TotalMilliseconds;
                var r2 = routine.Result;
                Console.WriteLine("Routine autosave between daily steps: " + r2.ChunksWritten + " chunks, " + (r2.Bytes / 1024) + " KiB; simulation thread blocked " + routineBlocked.ToString("0.0") + " ms");

                watch.Restart();
                var load = saves.Load(content, journal);
                Console.WriteLine("Load: " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms, ledger invariant " + (load.Report.HasErrors ? "BROKEN" : "ok"));
                Console.WriteLine("Managed memory: " + (GC.GetTotalMemory(false) / (1024 * 1024)) + " MiB");
            }
            try { Directory.Delete(dir, true); } catch (IOException) { }
            return 0;
        }

        private static void WithWorld(Options o, Action<World, WorldSaveSystem, FileTransactionJournal> action, bool save = true)
        {
            var saves = new WorldSaveSystem(o.Require("save"));
            var content = LoadContent(o, saves);
            if (!saves.Exists) throw new InvalidOperationException("No world at " + o.Require("save") + ". Create one with 'new'.");
            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(content, journal);
                if (load.JournalEntriesReplayed > 0) Console.WriteLine("Recovered " + load.JournalEntriesReplayed + " journaled transaction(s) from an unclean shutdown.");
                foreach (var m in load.Report.Messages) Console.WriteLine(m);
                if (load.Report.HasErrors) throw new InvalidDataException("World failed integrity checks; refusing to continue.");
                action(load.World, saves, journal);
            }
        }

        private static void Save(World world, WorldSaveSystem saves, FileTransactionJournal journal)
        {
            var result = saves.Save(world);
            saves.CompactJournal(world, journal);
            Console.WriteLine("Saved generation " + result.Generation + ": " + result.ChunksWritten + " chunk(s) written, " + result.ChunksSkipped + " unchanged, " +
                              (result.Bytes / 1024) + " KiB, " + result.Milliseconds.ToString("0") + " ms");
        }

        private static ContentSet LoadContent(Options o, WorldSaveSystem saves = null)
        {
            // An existing world reopens on the layout it was generated from unless one is named explicitly.
            var layout = o.Get("layout", saves?.RecordedLayoutFile() ?? ContentLoader.DefaultLayout);
            var content = ContentLoader.Load(o.DataDir, layout);
            var report = ContentLoader.Validate(content);
            foreach (var m in report.Messages) Console.WriteLine("content: " + m);
            if (report.HasErrors) throw new InvalidDataException("Content validation failed.");
            return content;
        }

        private static void PrintStatus(World world)
        {
            var w = world.Weather.State.Current;
            var alive = world.Population.Ordered.Count(n => n.Alive);
            var employed = world.Population.Ordered.Count(n => n.Alive && n.Employment == EmploymentStatus.Employed);
            var workforce = world.Population.Ordered.Count(n => n.Alive && (n.Employment == EmploymentStatus.Employed || n.Employment == EmploymentStatus.Unemployed));
            var powered = world.Population.Ordered.Count(n => n.Powers != null && n.Powers.Powers.Count > 0);
            Console.WriteLine("── " + world.Config.Identity.CityName + " · " + world.Clock.Now + " (" + world.Clock.Now.DayOfWeek + ")");
            Console.WriteLine("   Weather     " + w.Kind + ", " + w.TemperatureC.ToString("0") + "°C, wind " + w.WindSpeedMs.ToString("0") + " m/s, flood " + (w.FloodLevel * 100).ToString("0") + "%" +
                              (world.Weather.State.ActiveSystem != null ? " — tracking tropical system " + world.Weather.State.ActiveSystem.Name : ""));
            Console.WriteLine("   Population  " + alive + " living in " + world.Population.HouseholdCount + " households; employment " +
                              (workforce > 0 ? (100.0 * employed / workforce).ToString("0.0") : "0") + "%; " + powered + " people with anomalous abilities");
            Console.WriteLine("   Economy     cycle " + world.Macro.CycleIndex.ToString("0.000") + (world.Macro.InRecession ? " (RECESSION)" : "") + ", inflation " +
                              (world.Macro.AnnualInflation * 100).ToString("0.0") + "%, unemployment model " + (world.Macro.UnemploymentRate * 100).ToString("0.0") + "%");
            Console.WriteLine("   Treasury    " + world.Ledger.BalanceOf(world.Accounts.Treasury) + " · net external inflow " + world.Ledger.NetExternalInflow());
            Console.WriteLine("   Businesses  " + world.Businesses.Count + " · properties " + world.Properties.Count + " (" + world.Properties.All.Count(p => p.ForSale) + " for sale)");
            Console.WriteLine("   Anomalies   " + world.AnomalyLog.Count + " recorded" + (world.Cursor.PendingAnomaly != null ? " · one brewing" : ""));
            Console.WriteLine("   Ledger      " + (world.Ledger.VerifyInvariant(out var sum) ? "balanced" : "BROKEN (" + sum + ")") + ", " + world.Ledger.AccountCount + " accounts, journal seq " + world.Transactions.LastSequence);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("herogame-world — headless world host");
            Console.WriteLine("  new     --save DIR [--data DIR] [--config FILE] [--layout FILE] [--server-id ID] [--force]");
            Console.WriteLine("          layouts: layout_vertical_slice.json (default, 3 districts), layout_port_arden.json (full metro, ~50k residents)");
            Console.WriteLine("  run     --save DIR --days N");
            Console.WriteLine("  resume  --save DIR            simulate the real time that passed while offline");
            Console.WriteLine("  inspect --save DIR [status|economy|npc NAME|business|history|news|district]");
            Console.WriteLine("  bench   [--layout layout_stress.json | layout_port_arden.json] [--days N]");
        }
    }

    internal sealed class Options
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();
        public readonly List<string> Positional = new List<string>();

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    var key = args[i].Substring(2);
                    var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                    o._values[key] = hasValue ? args[++i] : "true";
                }
                else
                {
                    o.Positional.Add(args[i]);
                }
            }
            return o;
        }

        public bool Has(string key) => _values.ContainsKey(key);
        public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : fallback;

        public string Require(string key)
        {
            if (!_values.TryGetValue(key, out var v)) throw new ArgumentException("Missing --" + key);
            return v;
        }

        public string DataDir
        {
            get
            {
                if (_values.TryGetValue("data", out var d)) return d;
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "Game", "Assets", "StreamingAssets", "Data");
                    if (Directory.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
                throw new DirectoryNotFoundException("Could not locate Game/Assets/StreamingAssets/Data; pass --data.");
            }
        }
    }
}
