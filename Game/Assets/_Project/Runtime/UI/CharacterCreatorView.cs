using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeroGame.Runtime.UI
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.World;
    using HeroGame.Persistence.Content;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// The character creator's pages: basics, face, body, skin, hair, tattoos, starting clothes and walk. Every option
    /// comes from the catalogs (appearance.json, clothing.json, animations.json), so new sliders, hairstyles,
    /// clothes and walks appear here without code changes. Edits a <see cref="CharacterIdentity"/> in place.
    /// </summary>
    public sealed class CharacterCreatorView
    {
        public static readonly string[] Tabs = { "BASICS", "FACE", "BODY", "SKIN", "HAIR", "TATTOOS", "CLOTHES", "WALK" };

        private static ContentSet _catalogs;

        /// <summary>The catalogs the creator needs (loaded once from StreamingAssets/Data).</summary>
        public static ContentSet Catalogs
        {
            get
            {
                if (_catalogs != null) return _catalogs;
                var dir = GameSession.DataDirectory;
                _catalogs = new ContentSet
                {
                    Looks = ContentLoader.Read<AppearanceCatalog>(dir, ContentLoader.Appearance),
                    Clothing = ContentLoader.Read<List<ClothingItem>>(dir, ContentLoader.Clothing),
                    Animations = ContentLoader.Read<AnimationCatalog>(dir, ContentLoader.Animations),
                };
                return _catalogs;
            }
        }

        private readonly CharacterIdentity _c;
        private readonly VisualElement _host;
        private readonly string _fieldClass;
        private string _tab = Tabs[0];
        private string _tattooZone = "";

        public CharacterCreatorView(VisualElement host, CharacterIdentity identity, string fieldClass = "hg-field")
        {
            _host = host;
            _c = identity;
            _fieldClass = fieldClass;
            if (string.IsNullOrEmpty(_c.WalkStyleId)) _c.WalkStyleId = "casual";
            if (_c.StartingOutfit == null || _c.StartingOutfit.Pieces.Count == 0) _c.StartingOutfit = WardrobeRules.DefaultStarter();
            if (_c.Appearance.FaceMorphs.Count == 0) Randomise(new System.Random().Next());
        }

        public string Tab => _tab;

        public void Show(string tab)
        {
            _tab = tab;
            _host.Clear();
            switch (tab)
            {
                case "BASICS": Basics(); break;
                case "FACE": Morphs("Face"); break;
                case "BODY": Morphs("Body"); break;
                case "SKIN": Skin(); break;
                case "HAIR": Hair(); break;
                case "TATTOOS": Tattoos(); break;
                case "CLOTHES": Clothes(); break;
                case "WALK": Walk(); break;
            }
        }

        /// <summary>A believable random person of the chosen presentation and age (the same generator as the townspeople).</summary>
        public void Randomise(int seed)
        {
            var rng = new DeterministicRandom((ulong)(uint)seed * 2654435761UL + 17UL);
            var npc = new NpcRecord
            {
                AppearanceSeed = rng.NextULong(),
                Presentation = _c.Presentation,
                SkinTone = rng.NextInt(0, IdentityRules.SkinTones),
                HeightCm = _c.Presentation == GenderPresentation.Feminine ? 152f + rng.NextFloat() * 28f : 162f + rng.NextFloat() * 30f,
                BirthDay = 40000 - _c.Age * 365L,
                Personality = new Personality { Openness = rng.NextFloat(), Conscientiousness = rng.NextFloat(), Extraversion = rng.NextFloat(), Agreeableness = rng.NextFloat(), Neuroticism = rng.NextFloat() },
            };
            var starters = new ContentSet { Looks = Catalogs.Looks, Animations = Catalogs.Animations, Clothing = Catalogs.Clothing.FindAll(i => i.Starter) };
            var looks = LooksGenerator.ForNpc(npc, 40000, starters, 22f + rng.Range(-8f, 8f));
            _c.Appearance = looks.Appearance;
            _c.WalkStyleId = looks.WalkStyle;
            if (WardrobeRules.Validate(looks.Outfit, Catalogs.FindClothing).Success) _c.StartingOutfit = looks.Outfit;
        }

        // ------------------------------------------------------------------ pages

        private void Basics()
        {
            Note("Your name, age and presentation. Presentation sets the starting shape; every slider stays yours to change.");
            var height = new Slider("Height (cm)", IdentityRules.MinHeightCm, IdentityRules.MaxHeightCm) { value = _c.Appearance.HeightCm, showInputField = true };
            Add(height).RegisterValueChangedCallback(e => _c.Appearance.HeightCm = e.newValue);
            var age = new SliderInt("Age", IdentityRules.MinAge, IdentityRules.MaxAge) { value = Mathf.Clamp(_c.Age, IdentityRules.MinAge, IdentityRules.MaxAge), showInputField = true };
            Add(age).RegisterValueChangedCallback(e => _c.Age = e.newValue);
            var presentation = new DropdownField("Presentation", new List<string> { "Feminine", "Masculine", "Androgynous" }, (int)_c.Presentation);
            Add(presentation).RegisterValueChangedCallback(e => _c.Presentation = (GenderPresentation)Mathf.Max(0, presentation.index));
            Note("Names are set on the first screen of the creator.");
        }

        private void Morphs(string group)
        {
            Note(group == "Face" ? "Every feature of the face. 50 is average; real faces are rarely average everywhere." : "Build and proportions. Clothes fit the body you make.");
            if (group == "Body")
            {
                var build = new Slider("Overall build", 0f, 1f) { value = _c.Appearance.BodyWeight };
                Add(build).RegisterValueChangedCallback(e => { _c.Appearance.BodyWeight = e.newValue; _c.Appearance.SetMorph("body_fat", e.newValue); });
            }
            string region = null;
            Foldout fold = null;
            foreach (var m in Catalogs.Looks.Morphs)
            {
                if (m.Group != group) continue;
                if (m.Region != region)
                {
                    region = m.Region;
                    fold = new Foldout { text = region.ToUpperInvariant(), value = region == "Eyes" || region == "Build" || region == "Head" };
                    fold.AddToClassList("hg-foldout");
                    _host.Add(fold);
                }
                var id = m.Id;
                var slider = new SliderInt(m.Label, 0, 100) { value = Mathf.RoundToInt(_c.Appearance.GetMorph(id) * 100f), showInputField = true };
                slider.AddToClassList(_fieldClass);
                slider.RegisterValueChangedCallback(e => _c.Appearance.SetMorph(id, e.newValue / 100f));
                fold.Add(slider);
            }
        }

        private void Skin()
        {
            Section("Skin tone");
            Swatches(Catalogs.Looks.SkinTones, i => i == _c.Appearance.SkinTone, (col, i) => { _c.Appearance.SkinTone = i; Show(_tab); });
            var undertone = new Slider("Undertone (cool ↔ warm)", 0f, 1f) { value = _c.Appearance.SkinUndertone };
            Add(undertone).RegisterValueChangedCallback(e => _c.Appearance.SkinUndertone = e.newValue);
            Section("Eyes");
            Swatches(Catalogs.Looks.EyeColors, i => Catalogs.Looks.EyeColors[i].Hex == _c.Appearance.EyeColorHex, (col, i) => { _c.Appearance.EyeColorHex = col.Hex; Show(_tab); });
            Section("Skin detail");
            foreach (var d in Catalogs.Looks.SkinDetails)
            {
                var id = d.Id;
                var slider = new SliderInt(d.Label, 0, 100) { value = Mathf.RoundToInt(_c.Appearance.GetDetail(id) * 100f) };
                Add(slider).RegisterValueChangedCallback(e => _c.Appearance.SetDetail(id, e.newValue / 100f));
            }
            Section("Makeup");
            Pick("Look", Catalogs.Looks.Makeup, _c.Appearance.Makeup, v => _c.Appearance.Makeup = v);
            var intensity = new Slider("Intensity", 0f, 1f) { value = _c.Appearance.MakeupIntensity };
            Add(intensity).RegisterValueChangedCallback(e => _c.Appearance.MakeupIntensity = e.newValue);
        }

        private void Hair()
        {
            Section("Hair");
            Pick("Style", Catalogs.Looks.HairStyles, _c.Appearance.HairStyle, v => _c.Appearance.HairStyle = v);
            Swatches(Catalogs.Looks.HairColors, i => Catalogs.Looks.HairColors[i].Hex == _c.Appearance.HairColorHex, (col, i) => { _c.Appearance.HairColorHex = col.Hex; Show(_tab); });
            Section("Highlights");
            var none = new Button(() => { _c.Appearance.HairHighlightHex = ""; Show(_tab); }) { text = "NONE" };
            none.AddToClassList("hg-button");
            if (string.IsNullOrEmpty(_c.Appearance.HairHighlightHex)) none.AddToClassList("hg-button--accent");
            _host.Add(none);
            Swatches(Catalogs.Looks.HairColors, i => Catalogs.Looks.HairColors[i].Hex == _c.Appearance.HairHighlightHex, (col, i) => { _c.Appearance.HairHighlightHex = col.Hex; Show(_tab); });
            Section("Eyebrows");
            Pick("Shape", Catalogs.Looks.Eyebrows, _c.Appearance.EyebrowStyle, v => _c.Appearance.EyebrowStyle = v);
            Swatches(Catalogs.Looks.HairColors, i => Catalogs.Looks.HairColors[i].Hex == _c.Appearance.EyebrowColorHex, (col, i) => { _c.Appearance.EyebrowColorHex = col.Hex; Show(_tab); });
            Section("Facial hair");
            Pick("Style", Catalogs.Looks.FacialHair, _c.Appearance.FacialHairStyle, v => _c.Appearance.FacialHairStyle = v);
            Swatches(Catalogs.Looks.HairColors, i => Catalogs.Looks.HairColors[i].Hex == _c.Appearance.FacialHairColorHex, (col, i) => { _c.Appearance.FacialHairColorHex = col.Hex; Show(_tab); });
        }

        private void Tattoos()
        {
            var cat = Catalogs.Looks;
            Note("Up to " + IdentityRules.MaxTattoos + " tattoos. More can be added at a tattoo parlour later (planned).");
            foreach (var t in new List<TattooPlacement>(_c.Appearance.Tattoos))
            {
                var known = cat.Tattoo(t.DesignId);
                var row = new VisualElement();
                row.AddToClassList("hg-row");
                row.Add(new Label((known != null ? known.Label : t.DesignId) + " · " + Pretty(t.Zone)) { style = { flexGrow = 1 } });
                var remove = new Button(() => { _c.Appearance.Tattoos.Remove(t); Show(_tab); }) { text = "REMOVE" };
                remove.AddToClassList("hg-button");
                remove.AddToClassList("hg-button--quiet");
                row.Add(remove);
                _host.Add(row);
            }
            if (_c.Appearance.Tattoos.Count >= IdentityRules.MaxTattoos) return;
            Section("Add a tattoo");
            if (string.IsNullOrEmpty(_tattooZone) && cat.TattooZones.Count > 0) _tattooZone = cat.TattooZones[0];
            var zones = cat.TattooZones.ConvertAll(Pretty);
            var zone = new DropdownField("Where", zones, Math.Max(0, cat.TattooZones.IndexOf(_tattooZone)));
            Add(zone).RegisterValueChangedCallback(e => { _tattooZone = cat.TattooZones[Math.Max(0, zone.index)]; Show(_tab); });
            var designs = cat.TattooDesigns.FindAll(d => d.Zones.Contains(_tattooZone));
            if (designs.Count == 0) return;
            var design = new DropdownField("Design", designs.ConvertAll(d => d.Label + " (" + d.Style + ")"), 0);
            Add(design);
            var scale = new Slider("Size", 0.5f, 1.5f) { value = 1f };
            Add(scale);
            var fade = new Slider("Age of the ink", 0f, 1f) { value = 0f };
            Add(fade);
            var add = new Button(() =>
            {
                _c.Appearance.Tattoos.RemoveAll(x => x.Zone == _tattooZone);
                _c.Appearance.Tattoos.Add(new TattooPlacement { Zone = _tattooZone, DesignId = designs[Math.Max(0, design.index)].Id, Scale = scale.value, Fade = fade.value });
                Show(_tab);
            }) { text = "ADD TATTOO" };
            add.AddToClassList("hg-button");
            add.AddToClassList("hg-button--accent");
            _host.Add(add);
        }

        private static readonly ClothingSlot[] CreatorSlots =
        {
            ClothingSlot.Top, ClothingSlot.Outer, ClothingSlot.FullBody, ClothingSlot.Bottom, ClothingSlot.Belt, ClothingSlot.Socks, ClothingSlot.Shoes,
            ClothingSlot.Hat, ClothingSlot.Glasses, ClothingSlot.Earrings, ClothingSlot.FacePiercing, ClothingSlot.Necklace, ClothingSlot.Watch,
            ClothingSlot.Bracelet, ClothingSlot.Rings, ClothingSlot.Bag,
        };

        private void Clothes()
        {
            Note("Pick what you start in. Stores sell far more: suits, dresses, boots, watches, chains, grills, designer pieces.");
            var outfit = _c.StartingOutfit;
            foreach (var slot in CreatorSlots)
            {
                var items = WardrobeRules.ForSlot(Catalogs.Clothing, slot, startersOnly: true);
                if (items.Count == 0) continue;
                var current = outfit.Pieces.Find(p => Catalogs.FindClothing(p.ItemId)?.Slot == slot);
                var labels = new List<string> { "None" };
                labels.AddRange(items.ConvertAll(i => i.Label));
                var index = current == null ? 0 : items.FindIndex(i => i.Id == current.ItemId) + 1;
                var s = slot;
                var dropdown = new DropdownField(Pretty(slot.ToString()), labels, Math.Max(0, index));
                Add(dropdown).RegisterValueChangedCallback(e =>
                {
                    outfit.Pieces.RemoveAll(p => Catalogs.FindClothing(p.ItemId)?.Slot == s);
                    if (dropdown.index > 0)
                    {
                        var item = items[dropdown.index - 1];
                        outfit.Pieces.Add(new OutfitPiece { ItemId = item.Id, VariantId = item.Variants[0].Id });
                    }
                    Show(_tab);
                });
                if (current == null) continue;
                var chosen = Catalogs.FindClothing(current.ItemId);
                var piece = current;
                Swatches(chosen.Variants.ConvertAll(v => new ColorOption { Id = v.Id, Label = v.Label, Hex = v.Hex }),
                    i => chosen.Variants[i].Id == piece.VariantId, (col, i) => { piece.VariantId = chosen.Variants[i].Id; Show(_tab); });
            }
            var check = WardrobeRules.Validate(outfit, Catalogs.FindClothing);
            Note(check.Success ? "Outfit ready." : "⚠ " + check.Error);
        }

        private void Walk()
        {
            Note("How you carry yourself. Townspeople each have their own walk too; injuries, cold, rain and drink change anyone's.");
            foreach (var w in Catalogs.Animations.WalkStyles)
            {
                if (w.Situational) continue;
                var id = w.Id;
                var b = new Button(() => { _c.WalkStyleId = id; Show(_tab); }) { text = w.Label + "   ·   " + w.Speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" };
                b.AddToClassList("hg-button");
                b.style.width = Length.Percent(100);
                b.style.unityTextAlign = TextAnchor.MiddleLeft;
                if (_c.WalkStyleId == id) b.AddToClassList("hg-button--accent");
                _host.Add(b);
            }
        }

        // ------------------------------------------------------------------ helpers

        private T Add<T>(T field) where T : VisualElement
        {
            field.AddToClassList(_fieldClass);
            _host.Add(field);
            return field;
        }

        private void Note(string text)
        {
            var l = new Label(text);
            l.AddToClassList("hg-muted");
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginBottom = 8;
            _host.Add(l);
        }

        private void Section(string text)
        {
            var l = new Label(text.ToUpperInvariant());
            l.AddToClassList("hg-slot-number");
            l.style.marginTop = 14;
            _host.Add(l);
        }

        private void Pick(string label, List<StyleOption> options, string current, Action<string> set)
        {
            var labels = options.ConvertAll(o => o.Label);
            var dropdown = new DropdownField(label, labels, Math.Max(0, options.FindIndex(o => o.Id == current)));
            Add(dropdown).RegisterValueChangedCallback(e => set(options[Math.Max(0, dropdown.index)].Id));
        }

        private void Swatches(List<ColorOption> colors, Func<int, bool> selected, Action<ColorOption, int> choose)
        {
            var row = new VisualElement();
            row.AddToClassList("hg-swatches");
            for (var i = 0; i < colors.Count; i++)
            {
                var index = i;
                var col = colors[i];
                var b = new Button(() => choose(col, index)) { tooltip = col.Label };
                b.AddToClassList("hg-swatch");
                if (ColorUtility.TryParseHtmlString(col.Hex, out var c)) b.style.backgroundColor = c;
                if (selected(i)) b.AddToClassList("hg-swatch--selected");
                row.Add(b);
            }
            _host.Add(row);
        }

        private static string Pretty(string id)
        {
            var s = id.Replace('_', ' ');
            var sb = new System.Text.StringBuilder(s.Length + 4);
            for (var i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpperInvariant(s[i]) : s[i]);
            }
            return sb.ToString();
        }
    }
}
