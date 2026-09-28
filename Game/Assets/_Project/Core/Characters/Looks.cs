using System;
using System.Collections.Generic;

namespace HeroGame.Core.Characters
{
    using HeroGame.Core.Population;

    // Everything a person can look like and wear, as data (StreamingAssets/Data/appearance.json, clothing.json,
    // animations.json). Characters store ids and numbers only, so new sliders, hairstyles, clothes and walks can be
    // added without breaking saves.

    /// <summary>One slider on the face or body (0..1, 0.5 = average). Id matches a blend shape on the human mesh.</summary>
    [Serializable]
    public sealed class MorphDefinition
    {
        public string Id = "";
        public string Label = "";
        /// <summary>"Face" or "Body".</summary>
        public string Group = "";
        /// <summary>Creator sub-section (Eyes, Nose, Jaw, Shoulders…).</summary>
        public string Region = "";
        /// <summary>How far generated people stray from average (0..0.5) — real faces vary more in some features.</summary>
        public float NaturalSpread = 0.18f;
    }

    /// <summary>A hairstyle, facial-hair style, eyebrow shape or similar pick-one option.</summary>
    [Serializable]
    public sealed class StyleOption
    {
        public string Id = "";
        public string Label = "";
        /// <summary>Free tags: length (short/medium/long), texture (straight/wavy/curly/coily), "masculine"/"feminine" leaning, "bald"…</summary>
        public List<string> Tags = new List<string>();
        /// <summary>Relative frequency among generated townspeople (0 = never generated, player-only).</summary>
        public float Commonness = 1f;
    }

    [Serializable]
    public sealed class ColorOption
    {
        public string Id = "";
        public string Label = "";
        public string Hex = "#000000";
        /// <summary>Natural colours are generated for townspeople (weighted); dyed ones appear more rarely.</summary>
        public bool Natural = true;
        public float Commonness = 1f;
    }

    [Serializable]
    public sealed class TattooDesign
    {
        public string Id = "";
        public string Label = "";
        public string Style = "";
        /// <summary>Zones the design fits (see <see cref="AppearanceCatalog.TattooZones"/>).</summary>
        public List<string> Zones = new List<string>();
    }

    /// <summary>The creator's menus and the ranges generated townspeople are drawn from.</summary>
    [Serializable]
    public sealed class AppearanceCatalog
    {
        public List<MorphDefinition> Morphs = new List<MorphDefinition>();
        public List<StyleOption> HairStyles = new List<StyleOption>();
        public List<StyleOption> FacialHair = new List<StyleOption>();
        public List<StyleOption> Eyebrows = new List<StyleOption>();
        public List<ColorOption> HairColors = new List<ColorOption>();
        public List<ColorOption> EyeColors = new List<ColorOption>();
        /// <summary>Skin tone swatches, light to deep (index = <see cref="AppearanceData.SkinTone"/>).</summary>
        public List<ColorOption> SkinTones = new List<ColorOption>();
        /// <summary>Freckles, moles, age lines… (0..1 amounts stored in <see cref="AppearanceData.SkinDetails"/>).</summary>
        public List<StyleOption> SkinDetails = new List<StyleOption>();
        public List<StyleOption> Makeup = new List<StyleOption>();
        public List<string> TattooZones = new List<string>();
        public List<TattooDesign> TattooDesigns = new List<TattooDesign>();

        public MorphDefinition Morph(string id) => Morphs.Find(m => m.Id == id);
        public StyleOption Hair(string id) => HairStyles.Find(h => h.Id == id);
        public TattooDesign Tattoo(string id) => TattooDesigns.Find(t => t.Id == id);
    }

    /// <summary>A tattoo on the body: a design in a zone, sized and faded.</summary>
    [Serializable]
    public sealed class TattooPlacement
    {
        public string Zone = "";
        public string DesignId = "";
        /// <summary>0.5..1.5 of the design's natural size.</summary>
        public float Scale = 1f;
        /// <summary>0 = fresh ink, 1 = old and blurred.</summary>
        public float Fade;
    }

    /// <summary>Where a piece of clothing or an accessory is worn. One item per slot.</summary>
    public enum ClothingSlot
    {
        Hat,
        Glasses,
        Mask,
        Earrings,
        FacePiercing,
        Necklace,
        /// <summary>Grills and other dental jewellery.</summary>
        Teeth,
        /// <summary>Shirt, tee, blouse, sweater, tank.</summary>
        Top,
        /// <summary>Jacket, coat, hoodie, blazer, vest worn over a top.</summary>
        Outer,
        /// <summary>Dress, jumpsuit, coveralls: fills both Top and Bottom.</summary>
        FullBody,
        Gloves,
        Watch,
        Bracelet,
        Rings,
        Belt,
        /// <summary>Jeans, trousers, shorts, skirt.</summary>
        Bottom,
        Socks,
        Shoes,
        Bag,
    }

    [Serializable]
    public sealed class ClothingVariant
    {
        public string Id = "";
        public string Label = "";
        /// <summary>Main and trim colours (material tint on the garment mesh).</summary>
        public string Hex = "#808080";
        public string TrimHex = "";
        /// <summary>Price multiplier (gold vs silver, leather vs canvas).</summary>
        public float PriceFactor = 1f;
    }

    /// <summary>One garment or accessory in the catalog (StreamingAssets/Data/clothing.json).</summary>
    [Serializable]
    public sealed class ClothingItem
    {
        public string Id = "";
        public string Label = "";
        public ClothingSlot Slot;
        /// <summary>Casual, Street, Formal, Work, Athletic, Outdoor, Nightlife, Luxury, Uniform, Swim, Sleep.</summary>
        public string Category = "";
        /// <summary>Tags the rules and the mesh use: belt_loops, tucked, sleeveless, open_front, heeled, hood…</summary>
        public List<string> Tags = new List<string>();
        public List<ClothingVariant> Variants = new List<ClothingVariant>();
        public long PriceCents;
        /// <summary>Business templates that stock it (clothes stores).</summary>
        public List<string> SoldBy = new List<string>();
        /// <summary>Offered free in the character creator.</summary>
        public bool Starter;
        /// <summary>0 = beachwear, 1 = winter coat (NPCs dress for the weather).</summary>
        public float Warmth = 0.3f;
        /// <summary>0 = gym clothes, 1 = black tie.</summary>
        public float Formality = 0.3f;
        /// <summary>Relative frequency on generated townspeople (0 = players only: grills, costumes, luxury pieces).</summary>
        public float Commonness = 1f;
        /// <summary>Empty = anyone; otherwise the presentation(s) it is cut for (the fit changes, nobody is barred).</summary>
        public List<GenderPresentation> Cut = new List<GenderPresentation>();

        public ClothingVariant Variant(string id) => Variants.Find(v => v.Id == id);
        public bool Has(string tag) => Tags.Contains(tag);
    }

    /// <summary>One worn item: which garment in which colourway.</summary>
    [Serializable]
    public sealed class OutfitPiece
    {
        public string ItemId = "";
        public string VariantId = "";

        public OutfitPiece Copy() => new OutfitPiece { ItemId = ItemId, VariantId = VariantId };
    }

    [Serializable]
    public sealed class Outfit
    {
        public string Id = "";
        public string Name = "";
        public List<OutfitPiece> Pieces = new List<OutfitPiece>();

        public Outfit Copy()
        {
            var o = new Outfit { Id = Id, Name = Name };
            foreach (var p in Pieces) o.Pieces.Add(p.Copy());
            return o;
        }
    }

    /// <summary>
    /// A way of walking. The numbers drive the procedural gait layer on top of the base locomotion clips (and pick
    /// the authored clip set when one exists), so every townsperson moves a little differently.
    /// </summary>
    [Serializable]
    public sealed class WalkStyle
    {
        public string Id = "";
        public string Label = "";
        /// <summary>Metres per second at a normal walk.</summary>
        public float Speed = 1.35f;
        /// <summary>Step length relative to leg length (1 = average).</summary>
        public float Stride = 1f;
        public float ArmSwing = 0.5f;
        public float HipSway = 0.3f;
        public float Bounce = 0.3f;
        /// <summary>Forward lean of the torso in degrees (negative = leaning back).</summary>
        public float Lean;
        public float ShoulderRoll = 0.2f;
        /// <summary>Head drop in degrees (tired, sad, looking at a phone).</summary>
        public float HeadDown;
        /// <summary>Who tends to walk like this (weights for generated townspeople).</summary>
        public int MinAge;
        public int MaxAge = 120;
        public float Commonness = 1f;
        /// <summary>Personality leanings: +1 favours high trait scores, -1 low (extraversion, conscientiousness, neuroticism, agreeableness).</summary>
        public float Extraversion;
        public float Conscientiousness;
        public float Neuroticism;
        public float Agreeableness;
        /// <summary>Only applied by circumstance (injured, drunk, cold, carrying), never assigned as someone's own walk.</summary>
        public bool Situational;
    }

    /// <summary>animations.json: the walk styles and every clip in the character animation set.</summary>
    [Serializable]
    public sealed class AnimationCatalog
    {
        public List<WalkStyle> WalkStyles = new List<WalkStyle>();
        public List<AnimationClipDefinition> Clips = new List<AnimationClipDefinition>();

        public WalkStyle Walk(string id) => WalkStyles.Find(w => w.Id == id);
    }

    /// <summary>An animation in the character set (StreamingAssets/Data/animations.json).</summary>
    [Serializable]
    public sealed class AnimationClipDefinition
    {
        public string Id = "";
        /// <summary>Locomotion, Idle, Conversation, Social, Emote, Phone, Sit, Eat, Work, Reaction, Combat, Vehicle, Dance, Swim…</summary>
        public string Category = "";
        public string Label = "";
        public bool Loop;
        /// <summary>Walk style this locomotion clip belongs to, if any.</summary>
        public string WalkStyle = "";
        /// <summary>Planned, Procedural (generated placeholder), Captured, Final.</summary>
        public string Status = "Planned";
    }
}
