using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.Simulation;
    using HeroGame.Persistence.Json;

    public class LooksAndWardrobeTests
    {
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("looks", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
        }

        private List<NpcRecord> Adults() => _world.Population.Ordered.Where(n => Age(n) >= 18).ToList();
        private int Age(NpcRecord n) => (int)((_world.Today - n.BirthDay) / 365);

        [Test]
        public void Catalogs_AreBigAndDetailed()
        {
            var c = _world.Content;
            Assert.GreaterOrEqual(c.Looks.Morphs.Count(m => m.Group == "Face"), 45, "face sliders");
            Assert.GreaterOrEqual(c.Looks.Morphs.Count(m => m.Group == "Body"), 20, "body sliders");
            Assert.GreaterOrEqual(c.Looks.HairStyles.Count, 40);
            Assert.GreaterOrEqual(c.Looks.TattooDesigns.Count, 30);
            Assert.GreaterOrEqual(c.Clothing.Count, 150);
            foreach (ClothingSlot slot in System.Enum.GetValues(typeof(ClothingSlot)))
                Assert.IsTrue(c.Clothing.Any(i => i.Slot == slot), "something to wear in every slot: " + slot);
            Assert.IsTrue(c.Clothing.Any(i => i.Slot == ClothingSlot.Teeth), "grills");
            Assert.IsTrue(c.Clothing.Any(i => i.Slot == ClothingSlot.Belt) && c.Clothing.Any(i => i.Has("belt_loops")), "belts and trousers to wear them with");
            Assert.GreaterOrEqual(c.Animations.WalkStyles.Count(w => !w.Situational), 12, "personal walk styles");
            Assert.GreaterOrEqual(c.Animations.Clips.Count, 300, "animation set");
        }

        [Test]
        public void TownspeopleLook_TheSameEveryTime_ButChangeClothesDaily()
        {
            foreach (var npc in _world.Population.Ordered.Take(40))
            {
                var a = LooksGenerator.ForNpc(npc, _world.Today, _world.Content);
                var b = LooksGenerator.ForNpc(npc, _world.Today, _world.Content);
                Assert.AreEqual(JsonSetup.Serialize(a), JsonSetup.Serialize(b), "deterministic");
                var tomorrow = LooksGenerator.ForNpc(npc, _world.Today + 1, _world.Content);
                Assert.AreEqual(JsonSetup.Serialize(a.Appearance), JsonSetup.Serialize(tomorrow.Appearance), "the body never changes day to day");
                Assert.AreEqual(a.WalkStyle, tomorrow.WalkStyle, "nor the walk");
            }
            var changed = _world.Population.Ordered.Take(60).Count(n =>
                JsonSetup.Serialize(LooksGenerator.ForNpc(n, _world.Today, _world.Content).Outfit) != JsonSetup.Serialize(LooksGenerator.ForNpc(n, _world.Today + 1, _world.Content).Outfit));
            Assert.Greater(changed, 45, "most people wear something different the next day");
        }

        [Test]
        public void EveryGeneratedOutfit_FollowsTheWardrobeRules_AndTheTownIsVaried()
        {
            var people = _world.Population.Ordered.ToList();
            var looks = people.Select(n => (npc: n, looks: LooksGenerator.ForNpc(n, _world.Today, _world.Content, 22f))).ToList();
            foreach (var (npc, l) in looks)
            {
                var ok = WardrobeRules.Validate(l.Outfit, _world.Content.FindClothing);
                Assert.IsTrue(ok.Success, npc.FullName + ": " + ok.Error);
                Assert.IsNotNull(_world.Content.Animations.Walk(l.WalkStyle));
                Assert.IsFalse(_world.Content.Animations.Walk(l.WalkStyle).Situational);
                Assert.AreEqual(IdentityRules.Sanitize(new CharacterIdentity { FirstName = "a", LastName = "b", Appearance = l.Appearance }).Appearance.FaceMorphs.Count, l.Appearance.FaceMorphs.Count, "fits what a character can store");
            }
            Assert.GreaterOrEqual(looks.Select(x => x.looks.Appearance.HairStyle).Distinct().Count(), 25, "hairstyles");
            Assert.GreaterOrEqual(looks.Select(x => x.looks.WalkStyle).Distinct().Count(), 10, "walks");
            Assert.GreaterOrEqual(looks.SelectMany(x => x.looks.Outfit.Pieces).Select(p => p.ItemId).Distinct().Count(), 80, "garments in use");
            var adults = looks.Where(x => Age(x.npc) >= 18).ToList();
            var inked = adults.Count(x => x.looks.Appearance.Tattoos.Count > 0) / (double)adults.Count;
            Assert.That(inked, Is.InRange(0.1, 0.45), "about a quarter of adults have tattoos");
            var men = adults.Where(x => x.npc.Presentation == GenderPresentation.Masculine).ToList();
            Assert.Greater(men.Count(x => x.looks.Appearance.FacialHairStyle != "none"), men.Count / 4, "plenty of beards and stubble");
            var old = looks.Where(x => Age(x.npc) >= 75).ToList();
            if (old.Count > 5) Assert.Greater(old.Count(x => x.looks.WalkStyle == "elderly"), old.Count / 2, "the very old walk like it");
            var kids = looks.Where(x => Age(x.npc) <= 9).ToList();
            if (kids.Count > 5) Assert.Greater(kids.Count(x => x.looks.WalkStyle == "child"), kids.Count / 2, "young children mostly walk like children");
            Assert.IsFalse(kids.Any(x => x.looks.WalkStyle == "elderly" || x.looks.WalkStyle == "swagger"));
            Assert.Greater(looks.Count(x => x.looks.Appearance.GetMorph("body_fat") > 0.7f), 0);
            Assert.Greater(looks.Count(x => x.looks.Appearance.GetMorph("body_fat") < 0.3f), 0);
        }

        [Test]
        public void PeopleDressForTheWeather_AndForWork()
        {
            var adults = Adults();
            int Outer(float temp, bool rain) => adults.Count(n => LooksGenerator.ForNpc(n, _world.Today, _world.Content, temp, rain).Outfit.Pieces
                .Any(p => _world.Content.FindClothing(p.ItemId).Slot == ClothingSlot.Outer));
            Assert.Greater(Outer(3f, false), adults.Count * 0.8, "near freezing almost everyone wears a jacket or coat");
            Assert.Less(Outer(33f, false), adults.Count * 0.35, "few do in the summer heat");
            float Formality(string category)
            {
                var people = adults.Where(n => _world.Content.Occupations.Find(o => o.Id == n.OccupationId)?.Category == category).ToList();
                Assume.That(people.Count, Is.GreaterThan(3), "enough " + category + " workers in the test town");
                return (float)people.Average(n => LooksGenerator.ForNpc(n, _world.Today, _world.Content).Outfit.Pieces
                    .Select(p => _world.Content.FindClothing(p.ItemId)).Where(i => i.Slot == ClothingSlot.Top || i.Slot == ClothingSlot.Bottom || i.Slot == ClothingSlot.FullBody || i.Slot == ClothingSlot.Shoes)
                    .Average(i => i.Formality));
            }
            var office = new[] { "Finance", "Professional", "Government", "Administration" }.Where(c => adults.Count(n => _world.Content.Occupations.Find(o => o.Id == n.OccupationId)?.Category == c) > 3).ToList();
            var labour = new[] { "Trades", "Industry", "Port", "Logistics" }.Where(c => adults.Count(n => _world.Content.Occupations.Find(o => o.Id == n.OccupationId)?.Category == c) > 3).ToList();
            Assume.That(office.Count > 0 && labour.Count > 0);
            Assert.Greater(office.Average(Formality), labour.Average(Formality) + 0.1f, "office workers dress up more than tradespeople");
        }

        [Test]
        public void WardrobeRules_BeltsNeedLoops_DressesFillTopAndBottom_AndYouMustBeCovered()
        {
            OpResult Check(params (string item, string variant)[] pieces) =>
                WardrobeRules.Validate(new Outfit { Pieces = pieces.Select(p => new OutfitPiece { ItemId = p.item, VariantId = p.variant }).ToList() }, _world.Content.FindClothing);
            Assert.IsTrue(Check(("tee_crew", "white"), ("jeans_slim", "dark_wash"), ("belt_leather", "brown"), ("sneakers_white", "white")).Success);
            StringAssert.Contains("belt loops", Check(("tee_crew", "white"), ("joggers", "black"), ("belt_leather", "brown")).Error);
            StringAssert.Contains("Two tops", Check(("tee_crew", "white"), ("polo", "navy"), ("chinos", "khaki")).Error);
            StringAssert.Contains("covers top and bottom", Check(("sundress", "blue_floral"), ("tee_crew", "white")).Error);
            Assert.IsTrue(Check(("sundress", "blue_floral"), ("flats", "black"), ("earrings_hoops_small", "gold")).Success);
            StringAssert.Contains("on top", Check(("jeans_slim", "dark_wash")).Error);
            Assert.IsTrue(Check(("hoodie", "black"), ("joggers", "black")).Success, "a hoodie can be worn on its own");
            Assert.IsFalse(Check(("blazer", "navy"), ("chinos", "khaki")).Success, "a blazer needs something under it");
            Assert.IsTrue(Check(("board_shorts", "red")).Success, "swimwear");
            StringAssert.Contains("colour", Check(("tee_crew", "plaid_purple"), ("chinos", "khaki")).Error);
            Assert.IsTrue(WardrobeRules.TryParseStack(WardrobeRules.StackId("grill_top6", "iced"), out var item, out var variant));
            Assert.AreEqual(("grill_top6", "iced"), (item, variant));
            Assert.IsFalse(WardrobeRules.TryParseStack("laptop", out _, out _));
        }

        [Test]
        public void NewCharacters_WearTheirStarterPicks_ButCannotStartInLuxury()
        {
            var picked = new CharacterIdentity { FirstName = "Tess", LastName = "Monroe" };
            picked.StartingOutfit.Pieces.AddRange(new[]
            {
                new OutfitPiece { ItemId = "flannel", VariantId = "red" }, new OutfitPiece { ItemId = "jeans_skinny", VariantId = "black_denim" },
                new OutfitPiece { ItemId = "cowboy_boots", VariantId = "tan" }, new OutfitPiece { ItemId = "watch_chronograph", VariantId = "gold" },
            });
            // Skinny jeans, cowboy boots and the chronograph are not in the starter range: they are dropped.
            var tess = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 40), Character = picked }, new WorldPosition());
            var worn = _world.Wardrobe.Worn(tess);
            Assert.IsNotNull(worn);
            Assert.IsTrue(WardrobeRules.Validate(worn, _world.Content.FindClothing).Success);
            Assert.IsFalse(worn.Pieces.Any(p => p.ItemId == "watch_chronograph"), "no free luxury watch");
            Assert.IsTrue(worn.Pieces.All(p => _world.Content.FindClothing(p.ItemId).Starter));

            var good = new CharacterIdentity { FirstName = "Ray", LastName = "Soto" };
            good.StartingOutfit.Pieces.AddRange(new[]
            {
                new OutfitPiece { ItemId = "hoodie", VariantId = "forest" }, new OutfitPiece { ItemId = "joggers", VariantId = "olive" },
                new OutfitPiece { ItemId = "running_shoes", VariantId = "volt" }, new OutfitPiece { ItemId = "cap_baseball", VariantId = "navy" },
            });
            var ray = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 41), Character = good }, new WorldPosition());
            CollectionAssert.AreEquivalent(new[] { "hoodie", "joggers", "running_shoes", "cap_baseball" }, _world.Wardrobe.Worn(ray).Pieces.Select(p => p.ItemId));
            Assert.IsTrue(_world.Wardrobe.Owns(ray, "hoodie", "forest"));
        }

        [Test]
        public void ClothesStores_SellWhatTheyStock_ThroughTheLedger_AndYouWearWhatYouOwn()
        {
            var me = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 42), Character = new CharacterIdentity { FirstName = "Lou", LastName = "Park" } }, new WorldPosition());
            var boutique = _world.Businesses.Values.OrderBy(b => b.Id).First(b => b.TemplateId == "clothing_boutique");
            var hardware = _world.Businesses.Values.OrderBy(b => b.Id).First(b => b.TemplateId == "hardware_store");
            var now = _world.Clock.Now;
            var target = now.StartOfDay.AddHours(13);
            if (target <= now) target = target.AddDays(1);
            _world.Clock.AdvanceGame(target.TotalSeconds - now.TotalSeconds);

            _world.AdminGrant(me.CheckingAccount, new Money(500000), "test", "shopping money");
            var before = _world.Ledger.BalanceOf(me.CheckingAccount);
            var till = _world.Ledger.BalanceOf(boutique.Account);
            var price = _world.Wardrobe.PriceOf(_world.Content.FindClothing("leather_jacket"), "brown");
            Assert.IsTrue(_world.Wardrobe.Buy(me, boutique, "leather_jacket", "brown", "buy-1").Success);
            Assert.IsFalse(_world.Wardrobe.Buy(me, boutique, "leather_jacket", "brown", "buy-1").Success, "a retried request never charges twice");
            Assert.AreEqual(before - price, _world.Ledger.BalanceOf(me.CheckingAccount));
            Assert.AreEqual(till + price, _world.Ledger.BalanceOf(boutique.Account));
            Assert.IsTrue(_world.Wardrobe.Owns(me, "leather_jacket", "brown"));
            StringAssert.Contains("already own", _world.Wardrobe.Buy(me, boutique, "leather_jacket", "brown", "buy-2").Error);
            StringAssert.Contains("doesn't stock", _world.Wardrobe.Buy(me, hardware, "leather_jacket", "black", "buy-3").Error);
            Assert.IsTrue(_world.Wardrobe.Buy(me, hardware, "work_boots", "tan", "buy-4").Success, "hardware stores sell work gear");
            Assert.AreEqual(_world.Wardrobe.PriceOf(_world.Content.FindClothing("grill_top6"), "iced").Cents,
                _world.Wardrobe.PriceOf(_world.Content.FindClothing("grill_top6"), "gold").Cents * 3, "iced grills cost more than plain gold");

            var outfit = _world.Wardrobe.Worn(me).Copy();
            outfit.Id = "";
            outfit.Name = "Night out";
            outfit.Pieces.Add(new OutfitPiece { ItemId = "leather_jacket", VariantId = "brown" });
            Assert.IsTrue(_world.Wardrobe.Wear(me, outfit).Success);
            Assert.AreEqual("Night out", _world.Wardrobe.Worn(me).Name);
            var borrowed = _world.Wardrobe.Worn(me).Copy();
            borrowed.Pieces.Add(new OutfitPiece { ItemId = "watch_gold", VariantId = "gold" });
            StringAssert.Contains("don't own", _world.Wardrobe.Wear(me, borrowed).Error);
            StringAssert.Contains("Change out", _world.Wardrobe.DeleteOutfit(me, me.CurrentOutfit).Error);
            Assert.IsTrue(_world.Wardrobe.DeleteOutfit(me, "starter").Success);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));

            // Stores keep hours.
            _world.Clock.AdvanceGame(12 * 3600);
            StringAssert.Contains("closed", _world.Wardrobe.Buy(me, boutique, "bomber", "olive", "buy-5").Error);
        }
    }
}
