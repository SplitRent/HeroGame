using System;
using System.Collections.Generic;

namespace HeroGame.Core.Characters
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.World;

    /// <summary>How a townsperson looks today: face and body, what they are wearing and how they walk.</summary>
    public sealed class NpcLooks
    {
        public AppearanceData Appearance = new AppearanceData();
        public Outfit Outfit = new Outfit();
        public string WalkStyle = "casual";
    }

    /// <summary>
    /// Everyone in town looks like a particular person. Face, body, hair, skin, tattoos and walk come from the
    /// resident's appearance seed, so they look the same every time you meet them. Clothes depend on age, job,
    /// income, personality and the weather. Nothing is stored: it is recomputed from the record and the catalogs.
    /// </summary>
    public static class LooksGenerator
    {
        public static NpcLooks ForNpc(NpcRecord npc, long today, ContentSet content, float temperatureC = 22f, bool raining = false)
        {
            var age = (int)Math.Max(0, (today - npc.BirthDay) / 365);
            var looks = new NpcLooks();
            var rng = new DeterministicRandom(npc.AppearanceSeed ^ 0x100C5UL);
            looks.Appearance = Body(npc, age, content.Looks, rng);
            looks.WalkStyle = Walk(npc, age, looks.Appearance, content.Animations, new DeterministicRandom(npc.AppearanceSeed ^ 0x3A1CUL));
            var occupation = string.IsNullOrEmpty(npc.OccupationId) ? null : content.Occupations.Find(o => o.Id == npc.OccupationId);
            // Clothes change daily (and with the weather); the body never does.
            var dressRng = DeterministicRandom.For(npc.AppearanceSeed, (ulong)today, 0xD8E55UL);
            looks.Outfit = Dress(npc, age, occupation, content.Clothing, temperatureC, raining, dressRng);
            return looks;
        }

        // ------------------------------------------------------------------ body

        private static AppearanceData Body(NpcRecord npc, int age, AppearanceCatalog cat, DeterministicRandom rng)
        {
            var a = new AppearanceData
            {
                HeightCm = npc.HeightCm,
                SkinTone = Math.Max(0, Math.Min(IdentityRules.SkinTones - 1, npc.SkinTone)),
                SkinUndertone = Clamp01(0.5f + (float)rng.NextGaussian() * 0.2f),
            };
            var fem = npc.Presentation == GenderPresentation.Feminine ? 1f : npc.Presentation == GenderPresentation.Masculine ? -1f : 0f;
            foreach (var m in cat.Morphs)
            {
                var v = 0.5f + (float)rng.NextGaussian() * m.NaturalSpread;
                v += Bias(m.Id, fem, age);
                a.SetMorph(m.Id, Clamp01(v));
            }
            // Build follows the simulation's body (weight) where it has one.
            a.BodyWeight = a.GetMorph("body_fat");
            a.Muscularity = a.GetMorph("muscle");

            var deep = a.SkinTone >= 6;
            var light = a.SkinTone <= 2;
            a.HairStyle = PickStyle(cat.HairStyles, rng, h =>
            {
                var w = h.Commonness;
                if (h.Tags.Contains("feminine")) w *= fem > 0 ? 2.0f : fem < 0 ? 0.08f : 0.6f;
                if (h.Tags.Contains("masculine")) w *= fem < 0 ? 2.0f : fem > 0 ? 0.08f : 0.6f;
                if (h.Tags.Contains("older")) w *= age >= 50 ? 3f : 0.05f;
                if (h.Tags.Contains("bald") || h.Id == "receding_short") w *= fem < 0 && age >= 35 ? 1f + (age - 35) / 10f : 0.1f;
                if (h.Tags.Contains("coily") || h.Tags.Contains("braided")) w *= deep ? 2.2f : light ? 0.25f : 0.8f;
                if (age < 13 && (h.Tags.Contains("bald") || h.Tags.Contains("older"))) w = 0f;
                return w;
            });
            var grey = age < 35 ? 0f : Math.Min(0.95f, (age - 35) / 35f);
            var hair = PickColor(cat.HairColors, rng, col =>
            {
                var w = col.Commonness * (col.Natural ? 1f : age < 45 ? 1f : 0.3f);
                var isGrey = col.Id == "grey" || col.Id == "white" || col.Id == "salt_pepper";
                if (isGrey) return w * grey * 6f;
                w *= 1f - grey * 0.8f;
                if (deep && (col.Id.Contains("blonde") || col.Id == "platinum" || col.Id == "ginger" || col.Id == "auburn")) w *= 0.08f;
                if (light && (col.Id == "ginger" || col.Id == "auburn")) w *= 2.5f;
                return w;
            });
            a.HairColorHex = hair.Hex;
            if (rng.Chance(0.06) && age > 15 && age < 50) a.HairHighlightHex = cat.HairColors.Find(h => h.Id == "honey")?.Hex ?? "";
            a.EyebrowStyle = PickStyle(cat.Eyebrows, rng, e => e.Commonness * (e.Id == "groomed" || e.Id == "arched" ? (fem > 0 ? 2f : 0.5f) : e.Id == "bushy" ? (fem < 0 ? 1.5f : 0.2f) : 1f));
            a.EyebrowColorHex = hair.Natural ? hair.Hex : "";
            a.FacialHairStyle = fem < 0 && age >= 17 ? PickStyle(cat.FacialHair, rng, f => f.Commonness * (f.Id == "long_beard" && age > 50 ? 3f : 1f)) :
                fem == 0 && age >= 17 && rng.Chance(0.15) ? "stubble_light" : "none";
            a.FacialHairColorHex = a.HairColorHex;
            a.EyeColorHex = PickColor(cat.EyeColors, rng, col =>
                col.Commonness * (deep && (col.Id.Contains("blue") || col.Id == "green" || col.Id == "grey") ? 0.1f : light && col.Id.Contains("blue") ? 3f : 1f)).Hex;

            foreach (var d in cat.SkinDetails)
            {
                var chance = d.Commonness * 0.3f;
                if (d.Id == "freckles") chance *= light ? 3f : deep ? 0.2f : 1f;
                if (d.Id == "age_lines" || d.Id == "sun_damage") chance = age < 30 ? 0f : Math.Min(1f, (age - 30) / 35f) * (d.Id == "sun_damage" ? 0.5f : 1f);
                if (d.Id == "blemishes") chance *= age >= 12 && age <= 25 ? 2.5f : 0.4f;
                if (d.Id == "dark_circles") chance *= npc.Personality.Neuroticism > 0.6f ? 1.8f : 1f;
                if (!rng.Chance(Math.Min(0.95, chance))) continue;
                a.SetDetail(d.Id, d.Id == "age_lines" ? Clamp01((age - 30) / 50f + rng.Range(-0.1f, 0.1f)) : rng.Range(0.2f, 0.9f));
            }

            var makeupChance = age < 14 ? 0.0 : fem > 0 ? 0.6 : fem == 0 ? 0.2 : 0.03;
            a.Makeup = rng.Chance(makeupChance) ? PickStyle(cat.Makeup, rng, mk => mk.Id == "none" ? 0f : mk.Commonness) : "none";
            a.MakeupIntensity = a.Makeup == "none" ? 0f : rng.Range(0.3f, 0.9f);

            var tattooChance = age < 18 ? 0.0 : age <= 45 ? 0.32 : age <= 65 ? 0.18 : 0.08;
            tattooChance *= 0.7 + npc.Personality.Openness * 0.6;
            if (rng.Chance(tattooChance) && cat.TattooDesigns.Count > 0)
            {
                var count = rng.Chance(0.12) ? rng.NextInt(4, 9) : rng.NextInt(1, 4);
                var used = new HashSet<string>();
                for (var i = 0; i < count; i++)
                {
                    var design = cat.TattooDesigns[rng.NextInt(0, cat.TattooDesigns.Count)];
                    var zone = design.Zones[rng.NextInt(0, design.Zones.Count)];
                    if (zone == "face" && !rng.Chance(0.1)) continue;
                    if (!used.Add(zone)) continue;
                    a.Tattoos.Add(new TattooPlacement { Zone = zone, DesignId = design.Id, Scale = rng.Range(0.7f, 1.3f), Fade = Clamp01((age - 20) / 60f + rng.Range(-0.1f, 0.2f)) });
                }
            }
            return a;
        }

        /// <summary>Average differences by presentation and age (people still vary a lot around them).</summary>
        private static float Bias(string morph, float fem, int age)
        {
            switch (morph)
            {
                case "bust_size": return fem * 0.2f;
                case "hip_width": return fem * 0.08f;
                case "shoulder_width": return -fem * 0.1f;
                case "jaw_width": return -fem * 0.08f;
                case "brow_ridge": return -fem * 0.12f;
                case "adams_apple": return -fem * 0.3f;
                case "neck_thickness": return -fem * 0.08f;
                case "lip_upper_fullness":
                case "lip_lower_fullness": return fem * 0.05f;
                case "muscle": return -fem * 0.06f - Math.Max(0, age - 50) * 0.004f;
                case "body_fat": return Math.Max(0, Math.Min(age, 65) - 25) * 0.004f;
                case "posture": return Math.Max(0, age - 55) * 0.008f;
                case "eye_bags": return Math.Max(0, age - 30) * 0.006f;
                case "cheek_hollow": return Math.Max(0, age - 55) * 0.005f;
                case "double_chin": return Math.Max(0, age - 40) * 0.004f;
                case "ear_size": return Math.Max(0, age - 50) * 0.003f;
                default: return 0f;
            }
        }

        // ------------------------------------------------------------------ walk

        /// <summary>A personal walk: age range, personality and build decide which styles fit; the seed picks among them.</summary>
        public static string Walk(NpcRecord npc, int age, AppearanceData body, AnimationCatalog anim, DeterministicRandom rng)
        {
            var styles = new List<WalkStyle>();
            var weights = new List<double>();
            var p = npc.Personality;
            foreach (var w in anim.WalkStyles)
            {
                if (w.Situational || age < w.MinAge || age > w.MaxAge || w.Commonness <= 0f) continue;
                var fit = w.Extraversion * (p.Extraversion - 0.5f) + w.Conscientiousness * (p.Conscientiousness - 0.5f)
                          + w.Neuroticism * (p.Neuroticism - 0.5f) + w.Agreeableness * (p.Agreeableness - 0.5f);
                var weight = w.Commonness * Math.Exp(fit * 3.0);
                if (w.Id == "heavy") weight *= body.GetMorph("body_fat") > 0.68f ? 4.0 : 0.2;
                // Past seventy, age shows in the walk more and more.
                if (age >= 70) weight *= w.Id == "elderly" ? 1.0 + (age - 70) / 4.0 : 0.6;
                if (age < 10) weight *= w.Id == "child" ? 4.0 : 0.3;
                styles.Add(w);
                weights.Add(weight);
            }
            if (styles.Count == 0) return "casual";
            return styles[rng.PickWeighted(weights)].Id;
        }

        // ------------------------------------------------------------------ clothes

        private static Outfit Dress(NpcRecord npc, int age, OccupationDefinition job, List<ClothingItem> catalog, float temperatureC, bool raining, DeterministicRandom rng)
        {
            var outfit = new Outfit { Id = "today", Name = "Today" };
            if (catalog.Count == 0) return outfit;
            var formality = TargetFormality(npc, age, job, rng);
            var warmth = Clamp01((24f - temperatureC) / 26f);
            var budget = Math.Max(3000L, (npc.AnnualSalaryCents + npc.SavingsCents / 4) / 120);
            var presentation = npc.Presentation;

            double Weight(ClothingItem i)
            {
                if (i.Commonness <= 0f) return 0;
                var w = (double)i.Commonness;
                w *= Math.Exp(-Math.Abs(i.Formality - formality) * 4.0);
                if (i.Slot == ClothingSlot.Top || i.Slot == ClothingSlot.Bottom || i.Slot == ClothingSlot.FullBody)
                    w *= Math.Exp(-Math.Abs(i.Warmth - warmth) * 2.5);
                if (i.Cut.Count > 0 && !i.Cut.Contains(presentation)) w *= presentation == GenderPresentation.Androgynous ? 0.4 : 0.03;
                if (i.PriceCents > budget) w *= Math.Max(0.01, budget / (double)i.PriceCents);
                if (i.Category == "Uniform" || i.Tags.Contains("uniform")) w *= JobUniform(job) ? 20 : 0.02;
                if (i.Category == "Work" && JobLabor(job)) w *= 2;
                if (i.Category == "Swim") w *= temperatureC > 28 ? 0.4 : 0.01;
                if (age < 16 && (i.Category == "Formal" || i.Category == "Luxury" || i.Slot == ClothingSlot.Teeth)) w *= 0.05;
                return w;
            }

            ClothingItem Pick(ClothingSlot slot, Func<ClothingItem, bool> filter = null)
            {
                var options = new List<ClothingItem>();
                var weights = new List<double>();
                foreach (var i in catalog)
                {
                    if (i.Slot != slot || (filter != null && !filter(i))) continue;
                    var w = Weight(i);
                    if (w <= 0) continue;
                    options.Add(i);
                    weights.Add(w);
                }
                return options.Count == 0 ? null : options[rng.PickWeighted(weights)];
            }

            void Wear(ClothingItem i)
            {
                if (i == null || i.Variants.Count == 0) return;
                outfit.Pieces.Add(new OutfitPiece { ItemId = i.Id, VariantId = i.Variants[rng.NextInt(0, i.Variants.Count)].Id });
            }

            var dressChance = presentation == GenderPresentation.Feminine ? 0.22 : presentation == GenderPresentation.Androgynous ? 0.06 : 0.01;
            var full = rng.Chance(dressChance) || JobUniform(job) && rng.Chance(0.3) ? Pick(ClothingSlot.FullBody) : null;
            ClothingItem bottom = null;
            if (full != null) Wear(full);
            else
            {
                Wear(Pick(ClothingSlot.Top));
                bottom = Pick(ClothingSlot.Bottom, i => temperatureC > 26 || !i.Tags.Contains("swim"));
                Wear(bottom);
            }
            var outerNeeded = temperatureC < 17 || raining || formality > 0.8f && rng.Chance(0.7);
            if (outerNeeded || rng.Chance(0.12))
                Wear(Pick(ClothingSlot.Outer, i => !raining || i.Has("waterproof") || i.Has("hood") || rng.Chance(0.3)));
            var shoes = Pick(ClothingSlot.Shoes, i => !raining || !i.Has("heeled") || rng.Chance(0.3));
            Wear(shoes);
            if (shoes != null && !shoes.Id.Contains("sandal") && shoes.Id != "flip_flops" && shoes.Id != "slides" && shoes.Id != "pumps" && shoes.Id != "flats")
                Wear(Pick(ClothingSlot.Socks));
            if (bottom != null && bottom.Has("belt_loops") && rng.Chance(formality > 0.5f ? 0.85 : 0.4)) Wear(Pick(ClothingSlot.Belt));

            // Accessories: what people actually carry and wear.
            if (age >= 12 && rng.Chance(0.35)) Wear(Pick(ClothingSlot.Watch));
            var glassesChance = 0.18 + Math.Max(0, age - 40) * 0.012;
            if (rng.Chance(glassesChance)) Wear(Pick(ClothingSlot.Glasses, i => i.Has("prescription")));
            else if (age >= 12 && rng.Chance(0.12)) Wear(Pick(ClothingSlot.Glasses, i => i.Has("sun")));
            if (age >= 10 && rng.Chance(presentation == GenderPresentation.Feminine ? 0.6 : 0.12)) Wear(Pick(ClothingSlot.Earrings));
            if (age >= 14 && rng.Chance(0.06)) Wear(Pick(ClothingSlot.FacePiercing));
            if (age >= 12 && rng.Chance(0.16)) Wear(Pick(ClothingSlot.Necklace));
            if (age >= 18 && rng.Chance(age >= 28 ? 0.4 : 0.15)) Wear(Pick(ClothingSlot.Rings));
            if (age >= 12 && rng.Chance(0.1)) Wear(Pick(ClothingSlot.Bracelet));
            if (rng.Chance(temperatureC > 26 ? 0.2 : temperatureC < 8 ? 0.35 : 0.1)) Wear(Pick(ClothingSlot.Hat, i => temperatureC < 8 || i.Warmth < 0.3f));
            if (temperatureC < 5 && rng.Chance(0.3)) Wear(Pick(ClothingSlot.Gloves, i => i.Warmth >= 0.4f));
            if (age >= 12 && rng.Chance(0.22)) Wear(Pick(ClothingSlot.Bag));
            if (age >= 18 && rng.Chance(0.004)) Wear(catalog.Find(i => i.Slot == ClothingSlot.Teeth && i.Id == "grill_single_cap"));
            return outfit;
        }

        private static bool JobUniform(OccupationDefinition job) =>
            job != null && (job.Category == "Healthcare" || job.Category == "Trades" || job.Category == "Industry" || job.Category == "Port");

        private static bool JobLabor(OccupationDefinition job) =>
            job != null && (job.Category == "Trades" || job.Category == "Industry" || job.Category == "Port" || job.Category == "Logistics");

        private static float TargetFormality(NpcRecord npc, int age, OccupationDefinition job, DeterministicRandom rng)
        {
            var f = 0.25f;
            if (age < 18) f = 0.15f;
            else if (job != null)
                switch (job.Category)
                {
                    case "Finance":
                    case "Professional":
                    case "Real Estate":
                    case "Government":
                    case "Administration": f = 0.7f; break;
                    case "Media":
                    case "Education":
                    case "Technology": f = 0.45f; break;
                    case "Retail":
                    case "Hospitality":
                    case "Food Service":
                    case "Personal Services": f = 0.35f; break;
                    case "Fitness": f = 0.05f; break;
                    default: f = 0.2f; break;
                }
            else if (age >= 65) f = 0.4f;
            f += (npc.Personality.Conscientiousness - 0.5f) * 0.2f;
            f += (float)rng.NextGaussian() * 0.12f;
            return Clamp01(f);
        }

        // ------------------------------------------------------------------ helpers

        private static string PickStyle(List<StyleOption> options, DeterministicRandom rng, Func<StyleOption, float> weight)
        {
            if (options.Count == 0) return "";
            var weights = new List<double>(options.Count);
            var total = 0.0;
            foreach (var o in options)
            {
                var w = Math.Max(0f, weight(o));
                weights.Add(w);
                total += w;
            }
            return total <= 0 ? options[0].Id : options[rng.PickWeighted(weights)].Id;
        }

        private static ColorOption PickColor(List<ColorOption> options, DeterministicRandom rng, Func<ColorOption, float> weight)
        {
            if (options.Count == 0) return new ColorOption();
            var weights = new List<double>(options.Count);
            var total = 0.0;
            foreach (var o in options)
            {
                var w = Math.Max(0f, weight(o));
                weights.Add(w);
                total += w;
            }
            return total <= 0 ? options[0] : options[rng.PickWeighted(weights)];
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
