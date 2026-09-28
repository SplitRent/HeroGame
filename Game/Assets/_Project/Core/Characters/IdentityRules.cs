using System;
using System.Text;

namespace HeroGame.Core.Characters
{
    using HeroGame.Core.Population;

    /// <summary>
    /// What a player-made character may look like. A server never trusts the identity a client sends: it keeps
    /// only letters, spaces, hyphens and apostrophes in names, and clamps every appearance value to the ranges the
    /// character creator offers. The creator uses the same rules, so what you make is what the server keeps.
    /// </summary>
    public static class IdentityRules
    {
        public const int MaxNameLength = 24;
        public const int MinAge = 18;
        public const int MaxAge = 70;
        public const float MinHeightCm = 150f;
        public const float MaxHeightCm = 205f;
        public const int SkinTones = 10;
        public const int MaxStyleId = 63;
        public const int MaxMorphs = 96;
        public const int MaxSkinDetails = 16;
        public const int MaxTattoos = 24;
        public const int MaxOutfitPieces = 19;

        /// <summary>Letters (any alphabet), inner spaces, hyphens and apostrophes; trimmed and length-capped.</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(Math.Min(name.Length, MaxNameLength));
            var lastWasSeparator = true;
            foreach (var ch in name)
            {
                if (sb.Length >= MaxNameLength) break;
                if (char.IsLetter(ch))
                {
                    sb.Append(ch);
                    lastWasSeparator = false;
                }
                else if ((ch == ' ' || ch == '-' || ch == '\'') && !lastWasSeparator)
                {
                    sb.Append(ch);
                    lastWasSeparator = true;
                }
            }
            return sb.ToString().TrimEnd(' ', '-', '\'');
        }

        public static bool IsValid(CharacterIdentity identity) =>
            identity != null && CleanName(identity.FirstName).Length > 0 && CleanName(identity.LastName).Length > 0;

        /// <summary>A clean copy of <paramref name="identity"/> (never the same instance, never null).</summary>
        public static CharacterIdentity Sanitize(CharacterIdentity identity)
        {
            var source = identity ?? new CharacterIdentity();
            var a = source.Appearance ?? new AppearanceData();
            var clean = new CharacterIdentity
            {
                FirstName = CleanName(source.FirstName),
                LastName = CleanName(source.LastName),
                Age = Math.Max(MinAge, Math.Min(MaxAge, source.Age)),
                Presentation = Enum.IsDefined(typeof(GenderPresentation), source.Presentation) ? source.Presentation : GenderPresentation.Androgynous,
                VoicePresetId = CleanId(source.VoicePresetId),
                WalkStyleId = CleanId(source.WalkStyleId),
                Appearance = new AppearanceData
                {
                    BodyBaseId = Style(a.BodyBaseId),
                    SkinTone = Math.Max(0, Math.Min(SkinTones - 1, a.SkinTone)),
                    HeightCm = Clamp(a.HeightCm, MinHeightCm, MaxHeightCm, 175f),
                    BodyWeight = Clamp(a.BodyWeight, 0f, 1f, 0.5f),
                    Muscularity = Clamp(a.Muscularity, 0f, 1f, 0.5f),
                    HairStyleId = Style(a.HairStyleId),
                    HairColorHex = Hex(a.HairColorHex, "#2B1B10"),
                    EyebrowStyleId = Style(a.EyebrowStyleId),
                    EyeColorHex = Hex(a.EyeColorHex, "#4B3621"),
                    FacialHairId = Style(a.FacialHairId),
                    HairStyle = CleanId(a.HairStyle),
                    HairHighlightHex = string.IsNullOrEmpty(a.HairHighlightHex) ? "" : Hex(a.HairHighlightHex, ""),
                    FacialHairStyle = CleanId(a.FacialHairStyle),
                    FacialHairColorHex = string.IsNullOrEmpty(a.FacialHairColorHex) ? "" : Hex(a.FacialHairColorHex, ""),
                    EyebrowStyle = CleanId(a.EyebrowStyle),
                    EyebrowColorHex = string.IsNullOrEmpty(a.EyebrowColorHex) ? "" : Hex(a.EyebrowColorHex, ""),
                    SkinUndertone = Clamp(a.SkinUndertone, 0f, 1f, 0.5f),
                    Makeup = CleanId(a.Makeup),
                    MakeupIntensity = Clamp(a.MakeupIntensity, 0f, 1f, 0f),
                },
            };
            if (a.SkinDetails != null)
                foreach (var d in a.SkinDetails)
                {
                    if (clean.Appearance.SkinDetails.Count >= MaxSkinDetails) break;
                    var name = CleanId(d.Name);
                    if (name.Length == 0 || clean.Appearance.SkinDetails.Exists(x => x.Name == name)) continue;
                    clean.Appearance.SetDetail(name, Clamp(d.Value, 0f, 1f, 0f));
                }
            if (a.Tattoos != null)
                foreach (var t in a.Tattoos)
                {
                    if (t == null || clean.Appearance.Tattoos.Count >= MaxTattoos) continue;
                    var zone = CleanId(t.Zone);
                    var design = CleanId(t.DesignId);
                    if (zone.Length == 0 || design.Length == 0) continue;
                    clean.Appearance.Tattoos.Add(new TattooPlacement { Zone = zone, DesignId = design, Scale = Clamp(t.Scale, 0.5f, 1.5f, 1f), Fade = Clamp(t.Fade, 0f, 1f, 0f) });
                }
            clean.StartingOutfit = CleanOutfit(source.StartingOutfit);
            if (a.FaceMorphs != null)
                foreach (var m in a.FaceMorphs)
                {
                    if (clean.Appearance.FaceMorphs.Count >= MaxMorphs) break;
                    var name = CleanId(m.Name);
                    if (name.Length == 0 || clean.Appearance.FaceMorphs.Exists(x => x.Name == name)) continue;
                    clean.Appearance.SetMorph(name, Clamp(m.Value, 0f, 1f, 0.5f));
                }
            return clean;
        }

        /// <summary>Ids cleaned, at most one piece per item and <see cref="MaxOutfitPieces"/> pieces (slot rules need the catalog: <see cref="WardrobeRules"/>).</summary>
        public static Outfit CleanOutfit(Outfit outfit)
        {
            var clean = new Outfit();
            if (outfit == null) return clean;
            clean.Id = CleanId(outfit.Id);
            clean.Name = CleanName(outfit.Name);
            if (outfit.Pieces == null) return clean;
            foreach (var p in outfit.Pieces)
            {
                if (p == null || clean.Pieces.Count >= MaxOutfitPieces) continue;
                var item = CleanId(p.ItemId);
                if (item.Length == 0 || clean.Pieces.Exists(x => x.ItemId == item)) continue;
                clean.Pieces.Add(new OutfitPiece { ItemId = item, VariantId = CleanId(p.VariantId) });
            }
            return clean;
        }

        private static int Style(int id) => Math.Max(0, Math.Min(MaxStyleId, id));

        private static float Clamp(float v, float min, float max, float fallback) =>
            float.IsNaN(v) || float.IsInfinity(v) ? fallback : Math.Max(min, Math.Min(max, v));

        /// <summary>Lower-case letters, digits and underscores, at most 32 (morph and preset ids).</summary>
        private static string CleanId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var sb = new StringBuilder();
            foreach (var ch in id)
            {
                if (sb.Length >= 32) break;
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_') sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string Hex(string hex, string fallback)
        {
            if (hex == null || hex.Length != 7 || hex[0] != '#') return fallback;
            for (var i = 1; i < 7; i++)
                if (!Uri.IsHexDigit(hex[i])) return fallback;
            return hex.ToUpperInvariant();
        }
    }
}
