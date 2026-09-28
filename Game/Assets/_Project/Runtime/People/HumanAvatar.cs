using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.People
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Population;
    using HeroGame.Core.World;

    /// <summary>
    /// Puts a person's looks on the human body (Art/Characters/Human/SK_Human.fbx): every creator slider drives a
    /// pair of blend shapes, presentation/age/weight/muscle drive whole-body shapes, height scales the body, and skin
    /// tone and eye colour tint its materials. Until garment meshes exist, clothes are painted on by body region: each
    /// region shows skin or the colour of the garment covering it, and underwear is always on.
    /// </summary>
    public sealed class HumanAvatar : MonoBehaviour
    {
        /// <summary>Material slots of the body, in the order the pipeline writes them (humans.REGIONS).</summary>
        public enum Region { Head, Hands, Torso, UpperArms, LowerArms, Hips, Thighs, Shins, Feet, Eyes }

        public SkinnedMeshRenderer Body;
        /// <summary>Tone-free skin detail (T_Skin_Detail): 0.5 = unchanged, so the tone is doubled when it is used.</summary>
        public Texture2D SkinDetail;
        public float DetailScale = 2f;
        /// <summary>Height of the neutral body in the FBX (MakeHuman average young adult).</summary>
        public float BaseHeightCm = 166f;

        private static readonly Dictionary<Mesh, Dictionary<string, int>> ShapeIndex = new Dictionary<Mesh, Dictionary<string, int>>();
        private static readonly Dictionary<string, Texture2D> EyeTextures = new Dictionary<string, Texture2D>();
        private static Texture2D _fabric;
        private MaterialPropertyBlock _block;

        private Dictionary<string, int> Shapes()
        {
            var mesh = Body != null ? Body.sharedMesh : null;
            if (mesh == null) return new Dictionary<string, int>();
            if (ShapeIndex.TryGetValue(mesh, out var map)) return map;
            map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < mesh.blendShapeCount; i++) map[mesh.GetBlendShapeName(i)] = i;
            ShapeIndex[mesh] = map;
            return map;
        }

        private void Set(Dictionary<string, int> shapes, string name, float weight01)
        {
            if (shapes.TryGetValue(name, out var index)) Body.SetBlendShapeWeight(index, Mathf.Clamp01(weight01) * 100f);
        }

        /// <summary>Body shape, height, skin and eyes.</summary>
        public void Apply(AppearanceData a, GenderPresentation presentation, int age)
        {
            if (Body == null || a == null) return;
            var shapes = Shapes();
            for (var i = 0; i < Body.sharedMesh.blendShapeCount; i++) Body.SetBlendShapeWeight(i, 0f);
            foreach (var m in a.FaceMorphs)
            {
                if (m.Name == "body_fat" || m.Name == "muscle" || m.Name == "bust_size" || m.Name == "posture") continue;
                Set(shapes, m.Name + "_lo", (0.5f - m.Value) * 2f);
                Set(shapes, m.Name + "_hi", (m.Value - 0.5f) * 2f);
            }
            var fem = presentation == GenderPresentation.Feminine ? 1f : 0f;
            var masc = presentation == GenderPresentation.Masculine ? 1f : 0f;
            Set(shapes, "feminine", fem);
            Set(shapes, "masculine", masc);
            var child = Mathf.Clamp01((18f - age) / 10f);
            var old = Mathf.Clamp01((age - 30f) / 45f);
            Set(shapes, "age_child", child);
            Set(shapes, "age_old", old);
            var fat = a.GetMorph("body_fat");
            var muscle = a.GetMorph("muscle");
            Set(shapes, "weight_min", (0.5f - fat) * 2f);
            Set(shapes, "weight_max", (fat - 0.5f) * 2f);
            Set(shapes, "muscle_min", (0.5f - muscle) * 2f);
            Set(shapes, "muscle_max", (muscle - 0.5f) * 2f);
            var bust = a.GetMorph("bust_size") * (1f - child);
            Set(shapes, "bust_min", (0.5f - bust) * 2f);
            Set(shapes, "bust_max", (bust - 0.5f) * 2f);

            // The shapes change height too (female −7 cm, male +7, child −35); scale the rest of the way.
            var shaped = BaseHeightCm - 7f * fem + 7f * masc - 35f * child - 2f * old;
            transform.localScale = Vector3.one * Mathf.Clamp(a.HeightCm / Mathf.Max(80f, shaped), 0.5f, 1.4f);

            _skin = SkinColour(a);
            _eyes = ParseHex(a.EyeColorHex, new Color(0.3f, 0.2f, 0.12f));
            for (var r = 0; r < Body.sharedMaterials.Length && r <= (int)Region.Eyes; r++)
            {
                if (r == (int)Region.Eyes) Paint(r, Color.white, EyeTexture(_eyes));
                else Paint(r, _skin * DetailScale, SkinDetail);
            }
        }

        private Color _skin = new Color(0.7f, 0.5f, 0.4f);
        private Color _eyes;

        /// <summary>Painted-on clothes: each region takes the colour of the outermost garment covering it.</summary>
        public void Dress(Outfit outfit, ContentSet content, GenderPresentation presentation)
        {
            if (Body == null) return;
            var cover = new Color?[(int)Region.Eyes];
            // Underwear first (always on), then clothes from the inside out.
            var underwear = new Color(0.9f, 0.9f, 0.88f);
            cover[(int)Region.Hips] = underwear;
            if (presentation != GenderPresentation.Masculine) cover[(int)Region.Torso] = underwear;
            if (outfit != null)
            {
                var order = new[] { ClothingSlot.Socks, ClothingSlot.Bottom, ClothingSlot.Top, ClothingSlot.FullBody, ClothingSlot.Outer, ClothingSlot.Shoes, ClothingSlot.Gloves };
                foreach (var slot in order)
                    foreach (var p in outfit.Pieces)
                    {
                        var item = content?.FindClothing(p.ItemId);
                        if (item == null || item.Slot != slot) continue;
                        var v = item.Variant(p.VariantId);
                        var colour = ParseHex(v != null ? v.Hex : "#808080", Color.grey);
                        foreach (var region in Covers(item)) cover[(int)region] = colour;
                    }
            }
            for (var r = 0; r < cover.Length && r < Body.sharedMaterials.Length; r++)
            {
                if (cover[r].HasValue) Paint(r, cover[r].Value, Fabric());
                else Paint(r, _skin * DetailScale, SkinDetail);
            }
        }

        /// <summary>Which body regions a garment covers (tags refine it: sleeveless, cropped, long, skirt…).</summary>
        public static IEnumerable<Region> Covers(ClothingItem item)
        {
            var id = item.Id;
            switch (item.Slot)
            {
                case ClothingSlot.Top:
                    yield return Region.Torso;
                    if (!item.Has("sleeveless")) yield return Region.UpperArms;
                    if (id.Contains("long") || id.Contains("sweater") || id.Contains("turtleneck") || id.Contains("oxford") || id.Contains("flannel") ||
                        id.Contains("dress_shirt") || id.Contains("henley") || id.Contains("blouse") || id.Contains("work_shirt")) yield return Region.LowerArms;
                    break;
                case ClothingSlot.Outer:
                    yield return Region.Torso;
                    if (!item.Has("sleeveless")) { yield return Region.UpperArms; yield return Region.LowerArms; }
                    if (item.Has("long")) { yield return Region.Hips; yield return Region.Thighs; }
                    break;
                case ClothingSlot.FullBody:
                    yield return Region.Torso;
                    yield return Region.Hips;
                    yield return Region.Thighs;
                    if (!item.Has("sleeveless") && !item.Has("dress")) yield return Region.UpperArms;
                    if (item.Has("long") || id == "coveralls" || id == "jumpsuit") yield return Region.Shins;
                    if (id == "coveralls") yield return Region.LowerArms;
                    break;
                case ClothingSlot.Bottom:
                    yield return Region.Hips;
                    if (id.Contains("shorts") || id.StartsWith("skirt", StringComparison.Ordinal) && !id.Contains("pleated")) { yield return Region.Thighs; break; }
                    yield return Region.Thighs;
                    yield return Region.Shins;
                    break;
                case ClothingSlot.Shoes:
                case ClothingSlot.Socks:
                    if (!id.Contains("sandal") && id != "flip_flops" && id != "slides") yield return Region.Feet;
                    break;
                case ClothingSlot.Gloves:
                    yield return Region.Hands;
                    break;
            }
        }

        private void Paint(int materialIndex, Color colour, Texture texture)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            Body.GetPropertyBlock(_block, materialIndex);
            _block.SetColor("_BaseColor", colour);
            _block.SetColor("_Color", colour);
            if (texture != null)
            {
                _block.SetTexture("_BaseColorMap", texture);
                _block.SetTexture("_BaseMap", texture);
                _block.SetTexture("_MainTex", texture);
            }
            Body.SetPropertyBlock(_block, materialIndex);
        }

        private static Color SkinColour(AppearanceData a)
        {
            var tone = ParseHex(ToneHex(a.SkinTone), new Color(0.69f, 0.49f, 0.34f));
            // Undertone: cool skin leans pink, warm leans golden.
            var cool = new Color(1f, 0.95f, 0.97f);
            var warm = new Color(1.02f, 0.99f, 0.9f);
            return tone * Color.Lerp(cool, warm, a.SkinUndertone);
        }

        /// <summary>The ten skin tones of appearance.json (light to deep), kept here so the avatar works without the catalog.</summary>
        public static readonly string[] Tones = { "#F3D9C8", "#E9C5AB", "#DDB091", "#C99772", "#B07C57", "#9A6A45", "#7F5436", "#61402A", "#4A3021", "#34221A" };

        public static string ToneHex(int tone) => Tones[Mathf.Clamp(tone, 0, Tones.Length - 1)];

        private static Color ParseHex(string hex, Color fallback) =>
            !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;

        /// <summary>Sclera, iris (with a darker limbal ring) and pupil, laid out on the eyeball's UVs (v = 1 at the front).</summary>
        private static Texture2D EyeTexture(Color iris)
        {
            var key = ColorUtility.ToHtmlStringRGB(iris);
            if (EyeTextures.TryGetValue(key, out var cached) && cached != null) return cached;
            const int w = 16, h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "Eye_" + key };
            var sclera = new Color(0.92f, 0.89f, 0.85f);
            for (var y = 0; y < h; y++)
            {
                var v = (y + 0.5f) / h;
                Color c;
                if (v < 0.8f) c = Color.Lerp(new Color(0.85f, 0.78f, 0.76f), sclera, Mathf.Clamp01(v / 0.5f));
                else if (v < 0.815f) c = iris * 0.45f;
                else if (v < 0.9f) c = Color.Lerp(iris * 0.75f, iris * 1.1f, (v - 0.815f) / 0.085f);
                else if (v < 0.915f) c = iris * 0.6f;
                else c = new Color(0.02f, 0.02f, 0.02f);
                c.a = 1f;
                for (var x = 0; x < w; x++) tex.SetPixel(x, y, c);
            }
            tex.Apply(true, true);
            EyeTextures[key] = tex;
            return tex;
        }

        /// <summary>A soft woven texture for painted-on clothes (so garments don't show skin pores).</summary>
        private static Texture2D Fabric()
        {
            if (_fabric != null) return _fabric;
            const int n = 64;
            _fabric = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, name = "Fabric_Weave" };
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var weave = ((x / 2 + y / 2) % 2 == 0 ? 0.03f : -0.03f) + Mathf.PerlinNoise(x * 0.3f, y * 0.3f) * 0.06f;
                var g = 0.92f + weave;
                _fabric.SetPixel(x, y, new Color(g, g, g, 1f));
            }
            _fabric.Apply(true, true);
            return _fabric;
        }
    }
}
