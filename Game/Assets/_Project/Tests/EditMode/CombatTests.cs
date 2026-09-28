using System.Linq;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Combat;
    using HeroGame.Core.Crime;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.Simulation;
    using HeroGame.Persistence.Saves;

    /// <summary>Combat and weapons: grounded fights, reactions, self-defence, firearms and permits, non-lethal options.</summary>
    public class CombatTests
    {
        private World _world;
        private ServerCharacter _me;

        [SetUp]
        public void SetUp()
        {
            _world = NewWorld("combat");
            _me = _world.Characters.Values.First();
        }

        private static World NewWorld(string id, Core.Economy.ITransactionJournal journal = null)
        {
            var w = WorldGenerator.Create(id, TestContent.DefaultConfig(), TestContent.Load(), journal ?? new MemoryTransactionJournal());
            w.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 17), Character = new CharacterIdentity { FirstName = "Dom" } }, new WorldPosition());
            return w;
        }

        /// <summary>An adult NPC out in the world right now, and where they are.</summary>
        private NpcRecord Someone(out WorldPosition at, int skip = 0)
        {
            foreach (var npc in _world.Population.Ordered)
            {
                var age = npc.AgeYears(_world.Today);
                if (!npc.Alive || age < 20 || age > 60) continue;
                if (!_world.Director.TryGetPosition(_world.Schedules.Resolve(npc, _world.Clock.Now), out at)) continue;
                if (skip-- > 0) continue;
                return npc;
            }
            at = default;
            Assert.Fail("No one around.");
            return null;
        }

        private AttackOutcome Swing(string weapon, NpcRecord npc, WorldPosition at, float distance = 1f)
        {
            _world.Clock.AdvanceGame(5);
            return _world.Combat.Attack(_me, new AttackRequest
            {
                WeaponId = weapon, TargetKind = AttackTargetKind.Npc, Target = npc.Id, TargetPosition = at,
                Origin = new WorldPosition(at.X + distance, 0f, at.Z),
            });
        }

        private void Give(string item, int quantity = 1) => _me.Inventory.Add(new InventoryStack { ItemId = item, Quantity = quantity });

        private Core.Business.BusinessRecord Shop(string template)
        {
            var shop = _world.Businesses.Values.OrderBy(b => b.Id).First(b => b.TemplateId == "hardware_store");
            shop.TemplateId = template; // the slice has no outfitter; any storefront can play one here
            var place = _world.Geography.GetPlace(shop.Place);
            _world.Clock.AdvanceGame(((place.OpenMinute + 60) - _world.Clock.Now.MinuteOfDay + 1440) % 1440 * 60);
            return shop;
        }

        // ------------------------------------------------------------------ fists and melee

        [Test]
        public void APunch_HurtsReportsAndScaresPeople_AndIsDeterministic()
        {
            var victim = Someone(out var at);
            var before = victim.Health;
            var incidents = _world.Justice.Incidents.Count;
            AttackOutcome hit = null;
            for (var i = 0; i < 10 && (hit == null || !hit.Hit); i++) hit = Swing("fists", victim, at);
            Assert.IsTrue(hit.Hit, "fists land most of the time");
            Assert.Less(victim.Health, before);
            Assert.Greater(_world.Justice.Incidents.Count, incidents);
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.CrimeTypeId == "assault" && i.Perpetrator == _me.CharacterId));
            var memory = victim.MemoryOf(_me.CharacterId, false, 0);
            Assert.IsNotNull(memory);
            Assert.IsTrue((memory.Flags & MemoryFlags.HadConflict) != 0);
            Assert.Greater(memory.Fear, 0f);
            Assert.IsTrue(hit.Retaliated || victim.OverrideActivity == ActivityKind.AtHome && victim.OverrideUntilDay > _world.Today, "they fight back or they run home");

            // Same world, same swings, same result.
            var health = victim.Health;
            _world = NewWorld("combat");
            _me = _world.Characters.Values.First();
            var twin = Someone(out var twinAt);
            Assert.AreEqual(victim.Id, twin.Id);
            AttackOutcome replay = null;
            for (var i = 0; i < 10 && (replay == null || !replay.Hit); i++) replay = Swing("fists", twin, twinAt);
            Assert.AreEqual(health, twin.Health, "combat is deterministic");
        }

        [Test]
        public void Reach_Cooldowns_AndTheAbilityToFight_AreChecked()
        {
            var victim = Someone(out var at);
            Assert.AreEqual("Too far away.", Swing("fists", victim, at, distance: 12f).Message);
            Assert.IsTrue(Swing("fists", victim, at).Attempted);
            var tooSoon = _world.Combat.Attack(_me, new AttackRequest { WeaponId = "fists", TargetKind = AttackTargetKind.Npc, Target = victim.Id, TargetPosition = at, Origin = at });
            Assert.IsFalse(tooSoon.Attempted, "cooldown");
            StringAssert.Contains("don't have", Swing("baseball_bat", victim, at).Message);
            _me.Record.InCustody = true;
            Assert.AreEqual("You are in custody.", Swing("fists", victim, at).Message);
            _me.Record.InCustody = false;
            Assert.AreEqual("Unknown weapon.", Swing("rocket_launcher", victim, at).Message);
            Assert.IsFalse(_world.Combat.Attack(_me, new AttackRequest { WeaponId = "fists", TargetKind = AttackTargetKind.Npc, Target = victim.Id, Origin = new WorldPosition(float.NaN, 0, 0), TargetPosition = at }).Attempted);
        }

        [Test]
        public void HittingBack_IsSelfDefence_AndNotACrime()
        {
            // Find someone who swings back.
            for (var skip = 0; skip < 60; skip++)
            {
                var npc = Someone(out var at, skip);
                AttackOutcome first = null;
                for (var i = 0; i < 6 && npc.Alive && (first == null || !first.Retaliated); i++) first = Swing("fists", npc, at);
                if (first == null || !first.Retaliated) continue;
                var crimes = _world.Justice.Incidents.Count;
                // They hit us: now we are defending ourselves.
                var reply = Swing("fists", npc, at);
                Assert.IsTrue(reply.Justified, reply.Message);
                Assert.AreEqual(crimes, _world.Justice.Incidents.Count, "self-defence is not filed as a crime");
                StringAssert.Contains("self-defence", reply.Message);
                return;
            }
            Assert.Fail("Nobody in town ever fought back.");
        }

        [Test]
        public void FistsAndNonLethalWeapons_NeverKill()
        {
            var victim = Someone(out var at);
            for (var i = 0; i < 60; i++) Swing("fists", victim, at);
            Assert.IsTrue(victim.Alive);
            Assert.GreaterOrEqual(victim.Health, CombatService.NonLethalFloor - 1e-4f);

            Give("pepper_spray");
            var other = Someone(out var at2, 3);
            AttackOutcome spray = null;
            for (var i = 0; i < 6 && (spray == null || !spray.Hit); i++) spray = Swing("pepper_spray", other, at2);
            Assert.IsTrue(spray.Blinded);
            Assert.IsTrue(_world.Combat.IsBlinded(other.Id));
            Assert.IsTrue(other.Alive);
        }

        [Test]
        public void StunPistol_TakesACartridgePerShot_AndStunnedPeopleCantDodge()
        {
            Give("stun_pistol");
            var victim = Someone(out var at);
            StringAssert.Contains("ammunition", Swing("stun_pistol", victim, at, 5f).Message);
            Give("stun_cartridge", 3);
            AttackOutcome zap = null;
            for (var i = 0; i < 3 && (zap == null || !zap.Hit); i++) zap = Swing("stun_pistol", victim, at, 5f);
            Assert.IsTrue(zap.Stunned);
            Assert.IsTrue(_world.Combat.IsStunned(victim.Id));
            Assert.Less(CombatService.Count(_me, "stun_cartridge"), 3);
            Assert.IsFalse(zap.Retaliated, "stunned people don't fight back");
            Assert.IsTrue(Swing("fists", victim, at).Hit, "a stunned target is (almost) always hit");
        }

        // ------------------------------------------------------------------ firearms

        [Test]
        public void Firearms_NeedAPermit_WhichAViolentRecordDenies()
        {
            var shop = Shop("sporting_goods");
            Assert.IsTrue(_world.AdminGrant(_me.CheckingAccount, Money.FromDollars(5000), "admin", "test").Success);
            StringAssert.Contains("firearm permit", _world.Combat.BuyWeapon(_me, shop, "compact_pistol", "k1").Error);

            _me.Record.Charges.Add(new Charge { CrimeTypeId = "aggravated_assault", Convicted = true });
            StringAssert.Contains("disqualifies", _world.Combat.ApplyForFirearmPermit(_me, "p1").Error);
            _me.Record.Charges.Clear();

            var cash = _world.Ledger.BalanceOf(_me.CheckingAccount);
            Assert.IsTrue(_world.Combat.ApplyForFirearmPermit(_me, "p2").Success);
            Assert.IsTrue(_world.Combat.HasLicense(_me, CombatService.FirearmPermit));
            Assert.Less(_world.Ledger.BalanceOf(_me.CheckingAccount).Cents, cash.Cents, "the fee is paid");
            Assert.IsFalse(_world.Combat.ApplyForFirearmPermit(_me, "p3").Success, "one permit");

            Assert.IsTrue(_world.Combat.BuyWeapon(_me, shop, "compact_pistol", "k2").Success);
            Assert.AreEqual(1, CombatService.Count(_me, "compact_pistol"));
            StringAssert.Contains("Duplicate", _world.Combat.BuyWeapon(_me, shop, "compact_pistol", "k2").Error, "a retry with the same key is recognised");
            Assert.AreEqual(1, CombatService.Count(_me, "compact_pistol"), "…and is not a second pistol");
            Assert.IsTrue(_world.Combat.BuyAmmo(_me, shop, "compact_pistol", 2, "a1").Success);
            Assert.AreEqual(48, CombatService.Count(_me, "ammo_9mm"));
            StringAssert.Contains("doesn't sell", _world.Combat.BuyWeapon(_me, _world.Businesses.Values.First(b => b.TemplateId == "corner_store"), "compact_pistol", "k3").Error);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void AShooting_CanKill_IsAHomicide_AndClearsTheStreet()
        {
            Give("compact_pistol");
            Give("ammo_9mm", 30);
            var victim = Someone(out var at);
            var history = _world.History.Recent.Count();
            for (var i = 0; i < 30 && victim.Alive; i++) Swing("compact_pistol", victim, at, 6f);
            Assert.IsFalse(victim.Alive, "a pistol can kill");
            Assert.AreEqual(ActivityKind.Deceased, _world.Schedules.Resolve(victim, _world.Clock.Now).Activity);
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.CrimeTypeId == "homicide" && i.Perpetrator == _me.CharacterId && i.ReportedToPolice), "gunfire is always heard");
            Assert.Greater(_world.History.Recent.Count(), history);
            Assert.IsTrue(_world.History.Recent.Any(h => h.Headline.Contains(victim.FullName)));
            Assert.Less(CombatService.Count(_me, "ammo_9mm"), 30);
            Assert.IsNotNull(_world.Wanted.Get(_me.CharacterId), "the police are looking");

            _world.Clock.AdvanceGame(5);
            var shotInTheAir = _world.Combat.Attack(_me, new AttackRequest { WeaponId = "compact_pistol", Origin = at, TargetPosition = at });
            Assert.IsTrue(shotInTheAir.Attempted);
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.CrimeTypeId == "unlawful_discharge"));
        }

        [Test]
        public void ArrestedWithAnUnlicensedGun_IsChargedAndDisarmed()
        {
            Give("compact_pistol");
            Give("ammo_9mm", 12);
            var victim = Someone(out var at);
            for (var i = 0; i < 5; i++) Swing("fists", victim, at);
            _world.Courts.Arrest(_me, caughtInPursuit: false, resisted: false);
            Assert.IsTrue(_me.Record.InCustody);
            Assert.AreEqual(0, CombatService.Count(_me, "compact_pistol"));
            Assert.AreEqual(0, CombatService.Count(_me, "ammo_9mm"));
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.CrimeTypeId == "unlicensed_firearm" && i.Perpetrator == _me.CharacterId));
        }

        // ------------------------------------------------------------------ players

        [Test]
        public void PlayersCanFight_OnlyWherePvpIsAllowed_AndTheVictimIsTold()
        {
            var other = _world.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, 18), Character = new CharacterIdentity { FirstName = "Kay" } }, new WorldPosition());
            var at = new WorldPosition(10, 0, 10);
            AttackOutcome Hit() { _world.Clock.AdvanceGame(5); return _world.Combat.Attack(_me, new AttackRequest { WeaponId = "fists", TargetKind = AttackTargetKind.Character, Target = other.CharacterId, Origin = at, TargetPosition = at }); }

            _world.Config.Gameplay.PvpEnabled = false;
            StringAssert.Contains("does not allow", Hit().Message);
            _world.Config.Gameplay.PvpEnabled = true;
            AttackOutcome o = null;
            for (var i = 0; i < 10 && (o == null || !o.Hit); i++) o = Hit();
            Assert.Less(other.Health, 1f);
            Assert.IsTrue(other.Inbox.Any(m => m.Body.StartsWith("You were")));
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.Victim == other.CharacterId && i.ReportedToPolice), "players report assaults on them");

            // Kay hits back: self-defence.
            _world.Clock.AdvanceGame(5);
            var reply = _world.Combat.Attack(other, new AttackRequest { WeaponId = "fists", TargetKind = AttackTargetKind.Character, Target = _me.CharacterId, Origin = at, TargetPosition = at });
            Assert.IsTrue(reply.Justified);
        }

        // ------------------------------------------------------------------ persistence and story

        [Test]
        public void AWeaponPurchase_SurvivesACrash_BecauseTheItemRidesInTheJournal()
        {
            var dir = TestContent.TempDirectory("combat-crash");
            var saves = new WorldSaveSystem(dir);
            EntityId me;
            using (var journal = saves.OpenJournal())
            {
                _world = NewWorld("crash", journal);
                _me = _world.Characters.Values.First();
                me = _me.CharacterId;
                Assert.IsTrue(_world.AdminGrant(_me.CheckingAccount, Money.FromDollars(500), "admin", "test").Success);
                saves.Save(_world, full: true);
                Assert.IsTrue(_world.Combat.BuyWeapon(_me, Shop("sporting_goods"), "baseball_bat", "bat-1").Success);
                // Power cut: no save after the purchase.
            }
            using (var journal = saves.OpenJournal())
            {
                var loaded = saves.Load(TestContent.Load(), journal).World;
                Assert.AreEqual(1, CombatService.Count(loaded.Characters[me], "baseball_bat"), "paid for and still in the bag");
                Assert.IsTrue(loaded.Ledger.VerifyInvariant(out _));
            }
        }

        [Test]
        public void Teenagers_CannotBuyWeaponsOrPermits_InStoryMode()
        {
            _world.Story = new Core.Story.StoryState();
            _world.Story.Restrictions.Add("weapons.buy");
            _world.Story.Restrictions.Add("civic.firearm_permit");
            Assert.IsTrue(_world.AdminGrant(_me.CheckingAccount, Money.FromDollars(500), "admin", "test").Success);
            StringAssert.Contains("teenager", _world.Combat.BuyWeapon(_me, Shop("sporting_goods"), "baseball_bat", "t1").Error);
            Assert.IsFalse(_world.Combat.ApplyForFirearmPermit(_me, "t2").Success);
        }
    }
}
