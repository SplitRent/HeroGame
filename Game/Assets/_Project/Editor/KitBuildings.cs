using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HeroGame.Editor
{
    using HeroGame.Core.World;

    /// <summary>
    /// Picks and fits a Blender kit building (Art/Environment/Buildings, described by KitBuildings.json) to a lot:
    /// the candidates for the place's kind are scored by how little they would have to stretch, one of the closest
    /// is chosen deterministically per place (so a street gets variety), and it is scaled to the lot's footprint and
    /// the simulation's height (within limits). Kinds the kit does not cover return null and keep their greybox box.
    /// </summary>
    public static class KitBuildings
    {
        public const string Folder = ModelImportRules.ArtRoot + "Environment/Buildings/";
        public const string ManifestPath = Folder + "KitBuildings.json";

        [Serializable]
        private sealed class Manifest
        {
            public List<Entry> Buildings = new List<Entry>();
        }

        [Serializable]
        private sealed class Entry
        {
            public string Name = "";
            public float Width = 10f;
            public float Depth = 10f;
            public float Height = 6f;
            public string[] Uses = new string[0];
            [NonSerialized] public GameObject Model;
        }

        private static List<Entry> _entries;

        /// <summary>Whether the kit is present in this project (the FBX files and manifest are committed with the game).</summary>
        public static bool Available => Load().Count > 0;

        public static void Reset() => _entries = null;

        private static List<Entry> Load()
        {
            if (_entries != null) return _entries;
            _entries = new List<Entry>();
            if (!File.Exists(ManifestPath)) return _entries;
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
            if (manifest?.Buildings == null) return _entries;
            foreach (var e in manifest.Buildings)
            {
                e.Model = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + e.Name + ".fbx");
                if (e.Model != null && e.Width > 0 && e.Depth > 0 && e.Height > 0) _entries.Add(e);
            }
            return _entries;
        }

        /// <summary>
        /// Instantiates the best-fitting kit building for a lot under <paramref name="parent"/> (local origin = footprint
        /// centre at ground level, front towards local −Z), or returns null when the kit has nothing for this kind.
        /// </summary>
        public static GameObject Place(Transform parent, PlaceKind kind, float width, float depth, float height, int seed)
        {
            var kindName = kind.ToString();
            var scored = new List<(Entry entry, float score)>();
            foreach (var e in Load())
            {
                if (Array.IndexOf(e.Uses, kindName) < 0) continue;
                var score = Mathf.Abs(Mathf.Log(width / e.Width)) + Mathf.Abs(Mathf.Log(depth / e.Depth)) + 0.7f * Mathf.Abs(Mathf.Log(Mathf.Max(height, 1f) / e.Height));
                scored.Add((e, score));
            }
            if (scored.Count == 0) return null;
            scored.Sort((a, b) => a.score.CompareTo(b.score) != 0 ? a.score.CompareTo(b.score) : string.CompareOrdinal(a.entry.Name, b.entry.Name));
            // Variety: anything within a small margin of the best fit is fair game for this lot.
            var close = scored.FindAll(s => s.score <= scored[0].score + 0.25f);
            var pick = close[(int)((uint)seed % (uint)close.Count)].entry;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(pick.Model);
            instance.name = "Exterior (" + pick.Name + ")";
            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            // Footprints must match exactly (neighbouring lots); height only within limits, since roofs, steeples and
            // floor heights look wrong when squashed or stretched far.
            var heightScale = Mathf.Clamp(Mathf.Max(height, 1f) / pick.Height, 0.8f, 1.6f);
            instance.transform.localScale = new Vector3(width / pick.Width, heightScale, depth / pick.Depth);
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            return instance;
        }
    }
}
