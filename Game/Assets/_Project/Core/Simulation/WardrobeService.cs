using System;
using System.Collections.Generic;

namespace HeroGame.Core.Simulation
{
    using HeroGame.Core.Business;
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;

    /// <summary>
    /// Clothes you own and wear. Garments are bought in clothes stores (every item lists the shop types that stock
    /// it) through the transaction processor and kept as inventory items; outfits are saved combinations of them.
    /// A new character starts with the clothes picked in the creator (starter range only).
    /// </summary>
    public sealed class WardrobeService
    {
        private readonly World _w;

        public WardrobeService(World world) => _w = world;

        private ClothingItem Find(string id) => _w.Content.FindClothing(id);

        public bool Owns(ServerCharacter c, string itemId, string variantId)
        {
            var id = WardrobeRules.StackId(itemId, variantId);
            return c.Inventory.Exists(s => s.ItemId == id && s.Quantity > 0);
        }

        /// <summary>Everything the character owns to wear, as outfit pieces.</summary>
        public List<OutfitPiece> Owned(ServerCharacter c)
        {
            var list = new List<OutfitPiece>();
            foreach (var s in c.Inventory)
                if (s.Quantity > 0 && WardrobeRules.TryParseStack(s.ItemId, out var item, out var variant) && Find(item) != null)
                    list.Add(new OutfitPiece { ItemId = item, VariantId = variant });
            return list;
        }

        public Money PriceOf(ClothingItem item, string variantId)
        {
            var v = item.Variant(variantId);
            return new Money((long)Math.Round(item.PriceCents * (v != null ? v.PriceFactor : 1f) * _w.Macro.PriceLevel));
        }

        /// <summary>What <paramref name="shop"/> stocks.</summary>
        public List<ClothingItem> Stock(BusinessRecord shop)
        {
            var list = new List<ClothingItem>();
            if (shop == null) return list;
            foreach (var i in _w.Content.Clothing) if (i.SoldBy.Contains(shop.TemplateId)) list.Add(i);
            return list;
        }

        public OpResult Buy(ServerCharacter c, BusinessRecord shop, string itemId, string variantId, string idempotencyKey)
        {
            var item = Find(itemId);
            if (item == null) return OpResult.Fail("They don't sell that.");
            if (item.Variant(variantId) == null) return OpResult.Fail(item.Label + " doesn't come in that colour.");
            if (shop == null) return OpResult.Fail("No shop.");
            if (!item.SoldBy.Contains(shop.TemplateId)) return OpResult.Fail(shop.Name + " doesn't stock that.");
            if (c.Record.InCustody) return OpResult.Fail("You are in custody.");
            var place = _w.Geography.GetPlace(shop.Place);
            if (place != null && !place.IsOpenAt(_w.Clock.Now.MinuteOfDay)) return OpResult.Fail(shop.Name + " is closed.");
            if (Owns(c, itemId, variantId)) return OpResult.Fail("You already own that.");
            var description = "Bought " + item.Label.ToLowerInvariant();
            return _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = description,
                Money = LedgerTransaction.Transfer(c.CheckingAccount, shop.Account, PriceOf(item, variantId), TransactionReason.Purchase, description),
                Records = new TransactionRecords { Items = new List<ItemGrant> { new ItemGrant { Character = c.CharacterId, ItemId = WardrobeRules.StackId(itemId, variantId), Quantity = 1 } } },
            });
        }

        /// <summary>The outfit being worn, or null (costume or nothing saved yet).</summary>
        public Outfit Worn(ServerCharacter c) => c.Outfits.Find(o => o.Id == c.CurrentOutfit);

        /// <summary>Saves <paramref name="outfit"/> (replacing one with the same id) and puts it on. Every piece must be owned.</summary>
        public OpResult Wear(ServerCharacter c, Outfit outfit)
        {
            var clean = IdentityRules.CleanOutfit(outfit);
            var rules = WardrobeRules.Validate(clean, Find);
            if (!rules.Success) return rules;
            foreach (var p in clean.Pieces)
                if (!Owns(c, p.ItemId, p.VariantId)) return OpResult.Fail("You don't own the " + Find(p.ItemId).Label.ToLowerInvariant() + " in that colour.");
            if (string.IsNullOrEmpty(clean.Id))
            {
                var n = c.Outfits.Count + 1;
                while (c.Outfits.Exists(o => o.Id == "outfit_" + n)) n++;
                clean.Id = "outfit_" + n;
            }
            if (string.IsNullOrEmpty(clean.Name)) clean.Name = "Outfit";
            var existing = c.Outfits.FindIndex(o => o.Id == clean.Id);
            if (existing >= 0) c.Outfits[existing] = clean;
            else
            {
                if (c.Outfits.Count >= WardrobeRules.MaxOutfits) return OpResult.Fail("Your wardrobe holds " + WardrobeRules.MaxOutfits + " outfits. Delete one first.");
                c.Outfits.Add(clean);
            }
            c.CurrentOutfit = clean.Id;
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
            return OpResult.Ok();
        }

        public OpResult DeleteOutfit(ServerCharacter c, string outfitId)
        {
            if (outfitId == c.CurrentOutfit) return OpResult.Fail("Change out of it first.");
            if (c.Outfits.RemoveAll(o => o.Id == outfitId) == 0) return OpResult.Fail("No such outfit.");
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
            return OpResult.Ok();
        }

        /// <summary>
        /// Gives a new character the clothes chosen in the creator and dresses them. Only starter items count (the
        /// creator cannot hand out luxury pieces); anything invalid falls back to a plain everyday outfit.
        /// </summary>
        public void GiveStarter(ServerCharacter c, Outfit chosen)
        {
            if (_w.Content.Clothing.Count == 0) return;
            var outfit = new Outfit { Id = "starter", Name = "Everyday" };
            if (chosen != null)
                foreach (var p in IdentityRules.CleanOutfit(chosen).Pieces)
                {
                    var item = Find(p.ItemId);
                    if (item != null && item.Starter && item.Variant(p.VariantId) != null) outfit.Pieces.Add(p);
                }
            if (!WardrobeRules.Validate(outfit, Find).Success) outfit = WardrobeRules.DefaultStarter();
            if (!WardrobeRules.Validate(outfit, Find).Success) return; // catalog without the defaults (tests)
            foreach (var p in outfit.Pieces)
                if (!Owns(c, p.ItemId, p.VariantId)) c.Inventory.Add(new InventoryStack { ItemId = WardrobeRules.StackId(p.ItemId, p.VariantId) });
            c.Outfits.RemoveAll(o => o.Id == outfit.Id);
            c.Outfits.Add(outfit);
            c.CurrentOutfit = outfit.Id;
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
        }
    }
}
