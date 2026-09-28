using System;
using System.Collections.Generic;

namespace HeroGame.Core.Characters
{
    using HeroGame.Core.Foundation;

    /// <summary>
    /// How clothes go together: one item per slot, a dress or jumpsuit fills both top and bottom, a belt needs
    /// trousers with belt loops, and an outfit has to cover you (a top or a zip-less hoodie, and a bottom), unless the
    /// bottom is swimwear. Owned garments are inventory items named <see cref="StackId"/>.
    /// </summary>
    public static class WardrobeRules
    {
        public const string StackPrefix = "wear:";
        public const int MaxOutfits = 20;

        public static string StackId(string itemId, string variantId) => StackPrefix + itemId + ":" + variantId;

        public static bool TryParseStack(string stackId, out string itemId, out string variantId)
        {
            itemId = variantId = "";
            if (stackId == null || !stackId.StartsWith(StackPrefix, StringComparison.Ordinal)) return false;
            var rest = stackId.Substring(StackPrefix.Length);
            var colon = rest.IndexOf(':');
            if (colon <= 0 || colon == rest.Length - 1) return false;
            itemId = rest.Substring(0, colon);
            variantId = rest.Substring(colon + 1);
            return true;
        }

        public static OpResult Validate(Outfit outfit, Func<string, ClothingItem> find)
        {
            if (outfit == null) return OpResult.Fail("No outfit.");
            if (outfit.Pieces.Count > IdentityRules.MaxOutfitPieces) return OpResult.Fail("Too many pieces.");
            var bySlot = new Dictionary<ClothingSlot, ClothingItem>();
            foreach (var p in outfit.Pieces)
            {
                var item = find(p.ItemId);
                if (item == null) return OpResult.Fail("Unknown item " + p.ItemId + ".");
                if (item.Variant(p.VariantId) == null) return OpResult.Fail(item.Label + " doesn't come in that colour.");
                if (bySlot.ContainsKey(item.Slot)) return OpResult.Fail("Two " + SlotName(item.Slot) + " at once.");
                bySlot[item.Slot] = item;
            }
            var full = bySlot.ContainsKey(ClothingSlot.FullBody);
            if (full && (bySlot.ContainsKey(ClothingSlot.Top) || bySlot.ContainsKey(ClothingSlot.Bottom)))
                return OpResult.Fail("A " + bySlot[ClothingSlot.FullBody].Label.ToLowerInvariant() + " already covers top and bottom.");
            if (bySlot.TryGetValue(ClothingSlot.Belt, out var belt) && (!bySlot.TryGetValue(ClothingSlot.Bottom, out var bottomForBelt) || !bottomForBelt.Has("belt_loops")))
                return OpResult.Fail("A " + belt.Label.ToLowerInvariant() + " needs trousers with belt loops.");
            var swim = bySlot.TryGetValue(ClothingSlot.Bottom, out var b) && b.Has("swim");
            var covered = full || bySlot.ContainsKey(ClothingSlot.Top) || (bySlot.TryGetValue(ClothingSlot.Outer, out var outer) && outer.Has("standalone"));
            if (!covered && !swim) return OpResult.Fail("You need something on top.");
            if (!full && !bySlot.ContainsKey(ClothingSlot.Bottom)) return OpResult.Fail("You need something on the bottom.");
            return OpResult.Ok();
        }

        public static string SlotName(ClothingSlot slot)
        {
            switch (slot)
            {
                case ClothingSlot.FacePiercing: return "face piercings";
                case ClothingSlot.FullBody: return "dresses or jumpsuits";
                case ClothingSlot.Outer: return "jackets";
                case ClothingSlot.Top: return "tops";
                case ClothingSlot.Bottom: return "bottoms";
                case ClothingSlot.Teeth: return "grills";
                default: return slot.ToString().ToLowerInvariant();
            }
        }

        /// <summary>Items that fit <paramref name="slot"/>, for the wardrobe screens.</summary>
        public static List<ClothingItem> ForSlot(IEnumerable<ClothingItem> catalog, ClothingSlot slot, bool startersOnly = false)
        {
            var list = new List<ClothingItem>();
            foreach (var i in catalog) if (i.Slot == slot && (!startersOnly || i.Starter)) list.Add(i);
            return list;
        }

        /// <summary>"item:variant,item:variant" — an outfit's pieces as one short string (network requests).</summary>
        public static string EncodePieces(Outfit outfit)
        {
            var parts = new List<string>();
            foreach (var p in outfit.Pieces) parts.Add(p.ItemId + ":" + p.VariantId);
            return string.Join(",", parts);
        }

        public static Outfit DecodePieces(string text, string id, string name)
        {
            var outfit = new Outfit { Id = id ?? "", Name = name ?? "" };
            if (string.IsNullOrEmpty(text)) return outfit;
            foreach (var part in text.Split(','))
            {
                var colon = part.IndexOf(':');
                if (colon <= 0) continue;
                outfit.Pieces.Add(new OutfitPiece { ItemId = part.Substring(0, colon), VariantId = part.Substring(colon + 1) });
            }
            return IdentityRules.CleanOutfit(outfit);
        }

        /// <summary>The plain outfit anyone starts in when nothing (valid) was chosen.</summary>
        public static Outfit DefaultStarter() => new Outfit
        {
            Id = "starter",
            Name = "Everyday",
            Pieces =
            {
                new OutfitPiece { ItemId = "tee_crew", VariantId = "white" },
                new OutfitPiece { ItemId = "jeans_straight", VariantId = "mid_wash" },
                new OutfitPiece { ItemId = "socks_crew", VariantId = "white" },
                new OutfitPiece { ItemId = "sneakers_white", VariantId = "white" },
            },
        };
    }
}
