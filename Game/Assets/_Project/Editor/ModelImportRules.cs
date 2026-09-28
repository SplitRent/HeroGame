using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HeroGame.Editor
{
    /// <summary>
    /// Enforces the Blender → Unity contract (docs/ASSET_PIPELINE.md) at import time for everything
    /// under Assets/_Project/Art:
    ///  • metre scale, Y-up, no Unity-side rescaling (Blender exports with FBX_SCALE_UNITS)
    ///  • meshes named UCX_* become convex MeshColliders and their renderers are removed
    ///  • meshes named *_LOD0.._LODn are grouped into a LODGroup automatically
    ///  • materials are extracted/remapped by name, never embedded
    /// The same rules are validated in Blender before export, so problems are caught on both sides.
    /// </summary>
    public sealed class ModelImportRules : AssetPostprocessor
    {
        public const string ArtRoot = "Assets/_Project/Art/";
        private static readonly float[] LodTransitions = { 0.5f, 0.2f, 0.07f, 0.02f };

        private bool Applies => assetPath.StartsWith(ArtRoot, System.StringComparison.Ordinal);

        private void OnPreprocessModel()
        {
            if (!Applies) return;
            var importer = (ModelImporter)assetImporter;
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.bakeAxisConversion = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.isReadable = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.External;
            importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            importer.materialSearch = ModelImporterMaterialSearch.Everywhere;
            // Collision is authored (UCX_) — never generated from render meshes.
            importer.addCollider = false;
            var isCharacter = assetPath.Contains("/Characters/");
            importer.animationType = isCharacter ? ModelImporterAnimationType.Human : ModelImporterAnimationType.None;
            importer.importAnimation = isCharacter || assetPath.Contains("/Animations/");
        }

        /// <summary>
        /// Kit materials come from <see cref="KitMaterials"/> (built from the kit's manifest, textures included) instead of
        /// the colour-only copies the FBX carries, so every building sharing M_Brick_Red shares one material.
        /// </summary>
        private Material OnAssignMaterialModel(Material material, Renderer renderer)
        {
            if (!Applies || material == null || !material.name.StartsWith("M_", System.StringComparison.Ordinal)) return null;
            return KitMaterials.GetOrCreate(material.name, material);
        }

        private void OnPostprocessModel(GameObject root)
        {
            if (!Applies) return;
            var renderers = root.GetComponentsInChildren<MeshFilter>(true);

            foreach (var mf in renderers)
            {
                if (!mf.name.StartsWith("UCX_", System.StringComparison.Ordinal)) continue;
                var collider = mf.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = mf.sharedMesh;
                collider.convex = true;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr != null) Object.DestroyImmediate(mr);
                Object.DestroyImmediate(mf);
            }

            var lods = new SortedDictionary<int, List<Renderer>>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var idx = r.name.LastIndexOf("_LOD", System.StringComparison.Ordinal);
                if (idx < 0 || !int.TryParse(r.name.Substring(idx + 4), out var level)) continue;
                if (!lods.TryGetValue(level, out var list)) lods[level] = list = new List<Renderer>();
                list.Add(r);
            }
            if (lods.Count > 1)
            {
                var group = root.GetComponent<LODGroup>() ?? root.AddComponent<LODGroup>();
                var array = lods.Select((kv, i) => new LOD(i < LodTransitions.Length ? LodTransitions[i] : 0.01f, kv.Value.ToArray())).ToArray();
                group.SetLODs(array);
                group.RecalculateBounds();
            }
        }

        private void OnPreprocessTexture()
        {
            if (!Applies) return;
            var importer = (TextureImporter)assetImporter;
            var file = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            if (file.EndsWith("_N", System.StringComparison.Ordinal)) importer.textureType = TextureImporterType.NormalMap;
            // Mask maps (metallic/AO/detail/smoothness) and other data textures are linear.
            if (file.EndsWith("_M", System.StringComparison.Ordinal) || file.EndsWith("_ORM", System.StringComparison.Ordinal)) importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = true;
            importer.maxTextureSize = assetPath.Contains("/Hero/") ? 4096 : 2048;
        }
    }
}
