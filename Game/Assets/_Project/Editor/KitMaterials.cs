using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HeroGame.Editor
{
    /// <summary>
    /// Unity materials for the Blender environment kit. The kit exports <c>Art/Environment/Materials/KitMaterials.json</c>
    /// (colour, roughness, metallic and texture names per material); this builds <c>M_*.mat</c> assets from it for
    /// whichever render pipeline is active, and <see cref="ModelImportRules"/> hands them to the FBX importer by name.
    /// Re-run whenever the kit is re-exported: HeroGame ▸ Art ▸ Refresh Kit Materials (the greybox builder does it too).
    /// </summary>
    public static class KitMaterials
    {
        public const string Folder = ModelImportRules.ArtRoot + "Environment/Materials/";
        public const string TextureFolder = ModelImportRules.ArtRoot + "Environment/Textures/";
        public const string ManifestPath = Folder + "KitMaterials.json";

        [Serializable]
        private sealed class Manifest
        {
            public List<Entry> Materials = new List<Entry>();
        }

        [Serializable]
        private sealed class Entry
        {
            public string Name = "";
            public float[] Color = { 0.7f, 0.7f, 0.7f };
            public float Roughness = 0.6f;
            public float Metallic = 0f;
            public string BaseMap = "";
            public string NormalMap = "";
        }

        private static Dictionary<string, Entry> _entries;

        private static Dictionary<string, Entry> Entries()
        {
            if (_entries != null) return _entries;
            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (!File.Exists(ManifestPath)) return _entries;
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
            if (manifest?.Materials != null)
                foreach (var e in manifest.Materials)
                    if (!string.IsNullOrEmpty(e.Name)) _entries[e.Name] = e;
            return _entries;
        }

        /// <summary>The kit material with this name, created from the manifest if it does not exist yet (null if unknown).</summary>
        public static Material GetOrCreate(string name, Material source = null)
        {
            var path = Folder + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Entries().TryGetValue(name, out var entry);
            if (entry == null && source == null) return null;
            Directory.CreateDirectory(Folder);
            var material = new Material(LitShader()) { name = name };
            Apply(material, entry, source);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        [MenuItem("HeroGame/Art/Refresh Kit Materials", priority = 60)]
        public static void RefreshAll()
        {
            _entries = null;
            var count = 0;
            foreach (var entry in Entries().Values)
            {
                var material = GetOrCreate(entry.Name);
                if (material == null) continue;
                if (material.shader != LitShader()) material.shader = LitShader();
                Apply(material, entry, null);
                EditorUtility.SetDirty(material);
                count++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log(count > 0 ? "[Art] " + count + " kit materials up to date." : "[Art] No kit material manifest at " + ManifestPath + ".");
        }

        private static Shader LitShader() =>
            Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        private static void Apply(Material m, Entry e, Material source)
        {
            var color = e != null && e.Color != null && e.Color.Length >= 3 ? new Color(e.Color[0], e.Color[1], e.Color[2]) : source != null ? source.color : Color.gray;
            var baseMap = e != null && !string.IsNullOrEmpty(e.BaseMap) ? AssetDatabase.LoadAssetAtPath<Texture2D>(TextureFolder + e.BaseMap + ".png") : null;
            var normalMap = e != null && !string.IsNullOrEmpty(e.NormalMap) ? AssetDatabase.LoadAssetAtPath<Texture2D>(TextureFolder + e.NormalMap + ".png") : null;
            // A textured surface takes its colour from the texture.
            if (baseMap != null) color = Color.white;
            var smoothness = 1f - (e != null ? e.Roughness : 0.6f);
            var metallic = e != null ? e.Metallic : 0f;

            SetColor(m, "_BaseColor", color);   // HDRP / URP
            SetColor(m, "_Color", color);       // Built-in
            SetTexture(m, "_BaseColorMap", baseMap); // HDRP
            SetTexture(m, "_BaseMap", baseMap);      // URP
            SetTexture(m, "_MainTex", baseMap);      // Built-in
            SetTexture(m, "_NormalMap", normalMap);  // HDRP
            SetTexture(m, "_BumpMap", normalMap);    // URP / Built-in
            SetFloat(m, "_Smoothness", smoothness);
            SetFloat(m, "_Glossiness", smoothness);
            SetFloat(m, "_Metallic", metallic);
            if (normalMap != null)
            {
                m.EnableKeyword("_NORMALMAP");
                m.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
            else m.DisableKeyword("_NORMALMAP");
            ValidateHdrp(m);
        }

        /// <summary>Lets HDRP set the keywords/passes that match the properties (reflection: no package dependency).</summary>
        private static void ValidateHdrp(Material m)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial", false);
                var validate = type?.GetMethod("ValidateMaterial", new[] { typeof(Material) });
                if (validate == null) continue;
                try { validate.Invoke(null, new object[] { m }); }
                catch (Exception ex) { Debug.LogWarning("[Art] HDRP could not validate " + m.name + ": " + ex.Message); }
                return;
            }
        }

        private static void SetColor(Material m, string property, Color value)
        {
            if (m.HasProperty(property)) m.SetColor(property, value);
        }

        private static void SetFloat(Material m, string property, float value)
        {
            if (m.HasProperty(property)) m.SetFloat(property, value);
        }

        private static void SetTexture(Material m, string property, Texture value)
        {
            if (m.HasProperty(property)) m.SetTexture(property, value);
        }

        /// <summary>Textures can finish importing after the models that use them: refresh materials once they land.</summary>
        private sealed class Watcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                foreach (var path in imported)
                {
                    if (path.StartsWith(TextureFolder, StringComparison.Ordinal) || path == ManifestPath)
                    {
                        EditorApplication.delayCall -= RefreshAll;
                        EditorApplication.delayCall += RefreshAll;
                        return;
                    }
                }
            }
        }
    }
}
