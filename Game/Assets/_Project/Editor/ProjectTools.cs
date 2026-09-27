using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HeroGame.Persistence.Content;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HeroGame.Editor
{
    /// <summary>One-time project setup, validation and CI build entry points.</summary>
    public static class ProjectTools
    {
        [MenuItem("HeroGame/Setup/Configure Project Settings", priority = 20)]
        public static void ConfigureProject()
        {
            // Active Input Handling = Both (2): the game uses the Input System; "Both" keeps third-party tools working.
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings != null)
            {
                var so = new SerializedObject(settings);
                var handler = so.FindProperty("activeInputHandler");
                if (handler != null && handler.intValue != 2)
                {
                    handler.intValue = 2;
                    so.ApplyModifiedProperties();
                    Debug.Log("[Setup] Active Input Handling set to Both. Restart the editor to apply.");
                }
            }
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.companyName = "HeroGame";
            PlayerSettings.productName = Runtime.Bootstrap.GameInfo.WorkingTitle;
            PlayerSettings.bundleVersion = Runtime.Bootstrap.GameInfo.Version;
            EditorSettings.serializationMode = SerializationMode.ForceText;
            AssetDatabase.SaveAssets();
            Debug.Log("[Setup] Project configured. Next: Window ▸ Rendering ▸ HDRP Wizard ▸ Fix All, then HeroGame ▸ Build Greybox Vertical Slice.");
        }

        [MenuItem("HeroGame/Validate/Content Data", priority = 40)]
        public static void ValidateContent()
        {
            var content = ContentLoader.Load(Path.Combine(Application.streamingAssetsPath, "Data"));
            var report = ContentLoader.Validate(content);
            if (report.Messages.Count == 0) Debug.Log("[Validate] Content OK: " + content.Occupations.Count + " occupations, " + content.BusinessTemplates.Count +
                                                        " business templates, " + content.PowerArchetypes.Count + " power archetypes.");
            foreach (var m in report.Messages)
            {
                if (m.Severity == Core.Foundation.Severity.Error) Debug.LogError("[Validate] " + m);
                else Debug.LogWarning("[Validate] " + m);
            }
        }

        private static readonly Regex AssetName = new Regex("^(SM|SK|M|T|MI|A|AC|P|VFX|SFX|MUS|UI|FX)_[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)*$");

        /// <summary>Naming convention check (docs/CODING_STANDARDS.md §Assets) for production art.</summary>
        [MenuItem("HeroGame/Validate/Asset Naming", priority = 41)]
        public static void ValidateAssetNames()
        {
            var bad = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("", new[] { ModelImportRules.ArtRoot.TrimEnd('/') }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path) || path.EndsWith(".pipeline.json", StringComparison.Ordinal)) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (!AssetName.IsMatch(name)) bad.Add(path);
            }
            if (bad.Count == 0) Debug.Log("[Validate] All art assets follow the naming convention.");
            foreach (var b in bad) Debug.LogWarning("[Validate] Naming: " + b);
        }

        // ---- CI entry points (GameCI / -executeMethod) ------------------------------------------

        public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "Builds/Windows/HeroGame.exe");
        public static void BuildLinuxServer() => Build(BuildTarget.StandaloneLinux64, "Builds/LinuxServer/HeroGameServer.x86_64", server: true);

        private static void Build(BuildTarget target, string output, bool server = false)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                GreyboxWorldBuilder.BuildMenu();
                GreyboxWorldBuilder.BuildGreybox();
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            }
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = target,
                options = Environment.GetEnvironmentVariable("HEROGAME_DEV_BUILD") == "1" ? BuildOptions.Development : BuildOptions.None,
            };
#if UNITY_2021_2_OR_NEWER
            if (server) options.subtarget = (int)StandaloneBuildSubtarget.Server;
#else
            if (server) throw new NotSupportedException("Dedicated server builds require Unity 2021.2+.");
#endif
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log("[Build] " + report.summary.result + " " + report.summary.totalSize / (1024 * 1024) + " MiB in " + report.summary.totalTime);
            if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }
    }
}
