using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HeroGame.Editor
{
    using HeroGame.Core.Economy;
    using HeroGame.Core.Simulation;
    using HeroGame.Persistence.Content;
    using HeroGame.Persistence.Saves;
    using HeroGame.Runtime.Bootstrap;
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// "Did it work?" in one click. Checks the things a first Unity run can get wrong — setup steps, render pipeline,
    /// input handling, content, the simulation running inside this editor, saving and loading, generated scenes — and
    /// writes a plain report (Logs/HeroGame_HealthCheck.txt, also copied to the clipboard) that can be pasted into an
    /// issue or a chat. If the HeroGame menu is missing entirely, the scripts did not compile: see the Console.
    /// </summary>
    public static class HealthCheck
    {
        private enum Result { Pass, Warn, Fail }

        private sealed class Line
        {
            public Result Result;
            public string Check;
            public string Detail;
            public string Fix;
        }

        [MenuItem("HeroGame/Health Check (run me first)", priority = 0)]
        public static void Run()
        {
            var lines = new List<Line>();
            void Add(Result r, string check, string detail, string fix = "") => lines.Add(new Line { Result = r, Check = check, Detail = detail, Fix = fix });

            Add(Result.Pass, "Scripts compile", "all HeroGame assemblies compiled (this check could not run otherwise)");

            var version = Application.unityVersion;
            Add(version.StartsWith("6000", StringComparison.Ordinal) ? Result.Pass : Result.Warn, "Unity version", version,
                "The project targets Unity 6 (6000.0.23f1). Other versions may upgrade or break assets.");

            var pipeline = GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) Add(Result.Warn, "Render pipeline", "no render pipeline asset assigned (the scene will render pink or unlit)",
                "Window ▸ Rendering ▸ HDRP Wizard ▸ Fix All.");
            else Add(pipeline.GetType().Name.Contains("HDRenderPipeline") ? Result.Pass : Result.Warn, "Render pipeline", pipeline.GetType().Name,
                "The project is set up for HDRP.");

            var input = InputHandling();
            Add(input == 1 || input == 2 ? Result.Pass : Result.Warn, "Input handling", input == 0 ? "old Input Manager only" : input == 1 ? "Input System" : input == 2 ? "Both" : "unknown",
                "HeroGame ▸ Setup ▸ Configure Project Settings, then restart the editor.");

            Add(PlayerSettings.colorSpace == ColorSpace.Linear ? Result.Pass : Result.Warn, "Colour space", PlayerSettings.colorSpace.ToString(),
                "HeroGame ▸ Setup ▸ Configure Project Settings.");

            Core.World.ContentSet content = null;
            try
            {
                var watch = Stopwatch.StartNew();
                content = ContentLoader.Load(GameSession.DataDirectory);
                var report = ContentLoader.Validate(content);
                var errors = report.Messages.Count(m => m.Severity == Core.Foundation.Severity.Error);
                Add(errors == 0 ? Result.Pass : Result.Fail, "Game data", content.Occupations.Count + " occupations, " + content.BusinessTemplates.Count + " business templates, " +
                    content.Items.Count + " items, " + content.Ordinances.Count + " ordinances, " + content.RadioStations.Count + " radio stations; " + errors + " errors (" +
                    watch.ElapsedMilliseconds + " ms)", errors == 0 ? "" : "Run HeroGame ▸ Validate ▸ Content Data and read the Console.");
                var story = ContentLoader.ValidateStory(ContentLoader.LoadStory(GameSession.DataDirectory), content);
                var storyErrors = story.Messages.Count(m => m.Severity == Core.Foundation.Severity.Error);
                Add(storyErrors == 0 ? Result.Pass : Result.Fail, "Story data", storyErrors + " errors");
            }
            catch (Exception ex)
            {
                Add(Result.Fail, "Game data", ex.GetType().Name + ": " + ex.Message, "StreamingAssets/Data must be present and readable.");
            }

            if (content != null) SimulationSmoke(content, Add);

            foreach (var scene in new[] { GreyboxWorldBuilder.GreyboxScene, GreyboxWorldBuilder.MenuScene })
            {
                var exists = File.Exists(scene);
                var inBuild = EditorBuildSettings.scenes.Any(s => s.path == scene && s.enabled);
                Add(exists ? (inBuild ? Result.Pass : Result.Warn) : Result.Warn, "Scene " + Path.GetFileNameWithoutExtension(scene),
                    exists ? (inBuild ? "generated, in build settings" : "generated, not in build settings") : "not generated yet",
                    scene == GreyboxWorldBuilder.GreyboxScene ? "HeroGame ▸ Build Greybox Vertical Slice." : "HeroGame ▸ Build Main Menu Scene.");
            }

            // A greybox generated by an older builder lacks newer systems (UI Toolkit HUD, combat, street encounters…).
            if (File.Exists(GreyboxWorldBuilder.GreyboxScene))
            {
                var text = File.ReadAllText(GreyboxWorldBuilder.GreyboxScene);
                var current = text.Contains("m_Name: " + GreyboxWorldBuilder.VersionMarker + " " + GreyboxWorldBuilder.BuilderVersion);
                Add(current ? Result.Pass : Result.Warn, "Greybox scene version", current ? "built by the current builder (v" + GreyboxWorldBuilder.BuilderVersion + ")" : "built by an older builder",
                    "HeroGame ▸ Build Greybox Vertical Slice (again) to get the new HUD, phone, combat and street encounters.");
            }

            WriteReport(lines);
        }

        private static void SimulationSmoke(Core.World.ContentSet content, Action<Result, string, string, string> add)
        {
            var dir = Path.Combine(Path.GetTempPath(), "herogame-healthcheck-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var config = ContentLoader.LoadServerConfig(Path.Combine(GameSession.DataDirectory, ContentLoader.DefaultServerConfig));
                var saves = new WorldSaveSystem(dir);
                long money;
                int npcs;
                var watch = Stopwatch.StartNew();
                using (var journal = saves.OpenJournal())
                {
                    var world = WorldGenerator.Create("healthcheck", config, content, journal);
                    npcs = world.Population.Count;
                    var generated = watch.ElapsedMilliseconds;
                    watch.Restart();
                    new WorldSimulation(world).AdvanceDays(3);
                    var simulated = watch.ElapsedMilliseconds;
                    add(Result.Pass, "Simulation", npcs + " residents generated in " + generated + " ms; 3 days simulated in " + simulated + " ms", "");
                    watch.Restart();
                    saves.Save(world);
                    money = world.Ledger.BalanceOf(world.Accounts.Treasury).Cents;
                }
                using (var journal = saves.OpenJournal())
                {
                    var load = saves.Load(content, journal);
                    var ok = !load.Report.HasErrors && load.World.Population.Count == npcs && load.World.Ledger.BalanceOf(load.World.Accounts.Treasury).Cents == money
                             && load.World.Ledger.VerifyInvariant(out _);
                    add(ok ? Result.Pass : Result.Fail, "Save and load", ok ? "round trip identical, books balance (" + watch.ElapsedMilliseconds + " ms)" : load.Report.ToString(),
                        ok ? "" : "Please send this report.");
                }
            }
            catch (Exception ex)
            {
                add(Result.Fail, "Simulation", ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace, "Please send this report.");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { /* temp files */ }
            }
        }

        private static int InputHandling()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings == null) return -1;
            var p = new SerializedObject(settings).FindProperty("activeInputHandler");
            return p != null ? p.intValue : -1;
        }

        private static void WriteReport(List<Line> lines)
        {
            var fails = lines.Count(l => l.Result == Result.Fail);
            var warns = lines.Count(l => l.Result == Result.Warn);
            var sb = new StringBuilder();
            sb.AppendLine("HeroGame health check · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · Unity " + Application.unityVersion + " · " + SystemInfo.operatingSystem);
            sb.AppendLine(fails == 0 ? (warns == 0 ? "RESULT: everything checks out." : "RESULT: working, with " + warns + " setup step(s) left.") : "RESULT: " + fails + " problem(s) found.");
            sb.AppendLine();
            foreach (var l in lines)
            {
                sb.AppendLine((l.Result == Result.Pass ? "[ OK ] " : l.Result == Result.Warn ? "[TODO] " : "[FAIL] ") + l.Check + ": " + l.Detail);
                if (l.Result != Result.Pass && !string.IsNullOrEmpty(l.Fix)) sb.AppendLine("        → " + l.Fix);
            }
            sb.AppendLine();
            sb.AppendLine("Next: open " + GreyboxWorldBuilder.GreyboxScene + " and press Play. The Console should show a line starting with");
            sb.AppendLine("\"[Bootstrap] Port Arden\" and you should be able to walk (WASD), open the phone (Up Arrow) and talk to people (E).");

            var text = sb.ToString();
            var path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs", "HeroGame_HealthCheck.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            EditorGUIUtility.systemCopyBuffer = text;
            if (fails > 0) Debug.LogError(text);
            else if (warns > 0) Debug.LogWarning(text);
            else Debug.Log(text);
            EditorUtility.DisplayDialog("HeroGame health check",
                (fails == 0 ? (warns == 0 ? "Everything checks out." : "It works. " + warns + " setup step(s) left.") : fails + " problem(s) found.") +
                "\n\nThe full report is in the Console, in Logs/HeroGame_HealthCheck.txt, and on your clipboard (paste it to share).", "OK");
        }
    }
}
