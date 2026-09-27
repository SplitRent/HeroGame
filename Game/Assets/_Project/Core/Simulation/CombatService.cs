using System;
using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Characters;
using HeroGame.Core.Combat;
using HeroGame.Core.Economy;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Population;
using HeroGame.Core.World;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// Fighting, grounded (GDD §9: violence with non-lethal options and real consequences). The server decides every
    /// hit from data (weapons.json) and a deterministic roll; people react like people (most run, some fight back,
    /// everyone who saw it can call it in); hitting back at someone who attacked you is self-defence, anything else
    /// goes through the same witness/evidence/police/court path as every other crime. Firearms need a City Hall permit
    /// to buy and are heard for blocks.
    /// </summary>
    public sealed class CombatService
    {
        public const string FirearmPermit = "firearm_permit";
        public const long PermitFeeCents = 15000;
        public const int PermitYears = 5;
        /// <summary>Attacking someone who attacked you within this many game seconds is self-defence.</summary>
        public const long SelfDefenceSeconds = 120;
        /// <summary>Tolerance on top of weapon range for position lag.</summary>
        public const float RangeSlack = 1.0f;
        /// <summary>Non-lethal weapons and fists never take a person below this.</summary>
        public const float NonLethalFloor = 0.1f;

        private readonly World _w;
        public readonly CombatState State = new CombatState();

        public CombatService(World world)
        {
            _w = world;
        }

        private long Now => _w.Clock.Now.TotalSeconds;

        public WeaponDefinition Weapon(string id) => _w.Content.FindWeapon(id);

        /// <summary>Weapons this character can use right now (fists always; others if carried).</summary>
        public List<WeaponDefinition> Usable(ServerCharacter c)
        {
            var list = new List<WeaponDefinition>();
            foreach (var w in _w.Content.Weapons)
                if (string.IsNullOrEmpty(w.ItemId) || Count(c, w.ItemId) > 0) list.Add(w);
            return list;
        }

        public static int Count(ServerCharacter c, string itemId)
        {
            var n = 0;
            foreach (var s in c.Inventory) if (s.ItemId == itemId) n += s.Quantity;
            return n;
        }

        public bool HasLicense(ServerCharacter c, string kind)
        {
            foreach (var l in c.Licenses)
                if (l.Kind == kind && !l.Suspended && (l.ExpiresDay <= 0 || l.ExpiresDay > _w.Today)) return true;
            return false;
        }

        public bool IsStunned(EntityId who) => State.IsStunned(who, Now);
        public bool IsBlinded(EntityId who) => State.IsBlinded(who, Now);

        // ------------------------------------------------------------------ attacks

        public AttackOutcome Attack(ServerCharacter c, AttackRequest r)
        {
            if (c.Record.InCustody) return AttackOutcome.Refused("You are in custody.");
            if (c.Injury == InjuryState.Hospitalized || c.Injury == InjuryState.Incapacitated || c.Injury == InjuryState.Dead) return AttackOutcome.Refused("You can't.");
            if (State.IsStunned(c.CharacterId, Now)) return AttackOutcome.Refused("You can't move your arms.");
            var weapon = Weapon(r.WeaponId);
            if (weapon == null) return AttackOutcome.Refused("Unknown weapon.");
            if (!string.IsNullOrEmpty(weapon.ItemId) && Count(c, weapon.ItemId) <= 0) return AttackOutcome.Refused("You don't have a " + weapon.DisplayName.ToLowerInvariant() + ".");
            if (State.ReadyAt.TryGetValue((c.CharacterId, weapon.Id), out var ready) && ready > Now) return AttackOutcome.Refused("");
            if (!Finite(r.Origin) || !Finite(r.TargetPosition)) return AttackOutcome.Refused("Bad position.");

            NpcRecord npc = null;
            ServerCharacter other = null;
            switch (r.TargetKind)
            {
                case AttackTargetKind.Npc:
                    npc = _w.Population.Get(r.Target);
                    if (npc == null || !npc.Alive) return AttackOutcome.Refused("There is nobody there.");
                    if (npc.CurrentActivity == ActivityKind.InCustody) return AttackOutcome.Refused("They are in custody.");
                    break;
                case AttackTargetKind.Character:
                    if (!_w.Characters.TryGetValue(r.Target, out other) || other == c) return AttackOutcome.Refused("There is nobody there.");
                    if (!_w.Config.Gameplay.PvpEnabled) return AttackOutcome.Refused("This server does not allow fighting other players.");
                    if (other.Record.InCustody || other.Injury == InjuryState.Hospitalized || other.Injury == InjuryState.Dead) return AttackOutcome.Refused("They are out of reach.");
                    break;
            }
            if (r.TargetKind != AttackTargetKind.None && WorldPosition.DistanceXZ(r.Origin, r.TargetPosition) > weapon.Range + RangeSlack)
                return AttackOutcome.Refused("Too far away.");

            var outcome = new AttackOutcome { Attempted = true };
            if (weapon.UsesAmmo)
            {
                if (!TakeOne(c, weapon.AmmoItemId)) return AttackOutcome.Refused("Out of ammunition.");
                outcome.AmmoLeft = Count(c, weapon.AmmoItemId);
            }
            State.ReadyAt[(c.CharacterId, weapon.Id)] = Now + (long)Math.Ceiling(weapon.CooldownSeconds);
            State.Attacks++;
            if (State.Attacks % 64 == 0) State.Prune(Now, SelfDefenceSeconds * 4);
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);

            var rng = DeterministicRandom.For(_w.Seed, c.CharacterId.Value, (ulong)Now, 0xC0B7 ^ (ulong)State.Attacks);
            var targetId = npc != null ? npc.Id : other != null ? other.CharacterId : EntityId.None;

            // Firing into the air (or at nothing) is still a crime with a firearm.
            if (r.TargetKind == AttackTargetKind.None)
            {
                if (weapon.Kind == WeaponKind.Firearm)
                    outcome.Crime = _w.Crimes.ResolveViolence(c, "unlawful_discharge", r.Origin, EntityId.None, weapon.NoiseRadius, null, rng, alwaysReported: true);
                outcome.NpcsFled = Scatter(r.Origin, weapon, EntityId.None);
                outcome.Message = weapon.Kind == WeaponKind.Firearm ? "The shot echoes down the street." : "You swing at the air.";
                return outcome;
            }

            outcome.Justified = State.Aggression.TryGetValue((targetId, c.CharacterId), out var when) && Now - when <= SelfDefenceSeconds;
            State.Aggression[(c.CharacterId, targetId)] = Now;

            // The roll: accuracy falls off with distance (steeply for firearms), and with the attacker's state.
            var distance = WorldPosition.DistanceXZ(r.Origin, r.TargetPosition);
            var chance = (double)weapon.Accuracy;
            if (weapon.Ranged) chance *= 1.0 - 0.55 * Math.Pow(Math.Min(1.0, distance / Math.Max(1f, weapon.Range)), 2);
            if (State.IsBlinded(c.CharacterId, Now)) chance *= 0.3;
            if (c.Health < 0.5f) chance *= 0.8;
            if (npc != null && State.IsStunned(npc.Id, Now) || other != null && State.IsStunned(other.CharacterId, Now)) chance = Math.Max(chance, 0.95);
            outcome.Hit = rng.Chance(Math.Max(0.02, Math.Min(0.98, chance)));

            if (outcome.Hit)
            {
                var damage = weapon.Damage * (float)(0.8 + 0.4 * rng.NextDouble());
                outcome.Damage = damage;
                if (weapon.StunSeconds > 0) { State.StunnedUntil[targetId] = Now + (long)weapon.StunSeconds; outcome.Stunned = true; }
                if (weapon.BlindSeconds > 0) { State.BlindedUntil[targetId] = Now + (long)weapon.BlindSeconds; outcome.Blinded = true; }
                if (npc != null) HitNpc(c, npc, weapon, damage, r.TargetPosition, outcome);
                else if (other != null) HitCharacter(c, other, weapon, damage, outcome);
            }

            if (!outcome.Justified)
            {
                var crimeId = outcome.TargetKilled ? "homicide" : weapon.CrimeId;
                // Gunshots and bodies are always called in; a scuffle depends on who saw it.
                outcome.Crime = _w.Crimes.ResolveViolence(c, crimeId, r.TargetPosition, targetId, Math.Max(weapon.NoiseRadius, 30f), npc, rng,
                    alwaysReported: weapon.Kind == WeaponKind.Firearm || outcome.TargetKilled);
            }

            // The target's reaction: run, or (sometimes, when they can) fight back.
            if (npc != null && npc.Alive && !State.IsStunned(npc.Id, Now)) React(c, npc, weapon, outcome, rng);
            if (weapon.Kind == WeaponKind.Firearm || outcome.TargetKilled) outcome.NpcsFled += Scatter(r.TargetPosition, weapon, targetId);

            if (string.IsNullOrEmpty(outcome.Message))
                outcome.Message = !outcome.Hit ? "Missed." : outcome.TargetKilled ? "They don't get up." : outcome.TargetDown ? "They go down hard." :
                    outcome.Stunned ? "They seize up and drop." : outcome.Blinded ? "They stagger back, eyes streaming." : "Hit.";
            if (outcome.Justified) outcome.Message += " (self-defence)";
            return outcome;
        }

        private void HitNpc(ServerCharacter c, NpcRecord npc, WeaponDefinition weapon, float damage, WorldPosition at, AttackOutcome outcome)
        {
            var floor = weapon.Lethal ? 0f : NonLethalFloor;
            npc.Health = Math.Max(floor, npc.Health - damage);
            var memory = npc.MemoryOf(c.CharacterId, true, _w.Today);
            memory.Fear = Math.Min(1f, memory.Fear + 0.3f + damage);
            memory.Affinity = Math.Max(-1f, memory.Affinity - 0.4f);
            memory.Flags |= MemoryFlags.HadConflict | MemoryFlags.VictimOfCrime;
            _w.Dirty.Mark(SaveChunks.Population);

            if (npc.Health <= 0f)
            {
                npc.Alive = false;
                npc.DeathDay = _w.Today;
                npc.CurrentActivity = ActivityKind.Deceased;
                npc.AddHistory(_w.Today, "killed", npc.FullName + " was killed in an attack.");
                _w.History.Record(_w.Today, HistoryCategory.Crime, 4, npc.FullName + " killed in " + DistrictName(at),
                    "Police are treating the death as a homicide.", _w.Crimes.DistrictAt(at), npc.Id);
                _w.Dispatch.Report(EmergencyKind.Medical, at, 1, npc.FullName + " — no pulse", subject: npc.Id, severity: 1f);
                _w.Director.Invalidate(npc.Id);
                outcome.TargetKilled = outcome.TargetDown = true;
                return;
            }
            if (npc.Health <= 0.3f)
            {
                outcome.TargetDown = true;
                _w.Dispatch.Report(EmergencyKind.Medical, at, npc.Health < 0.15f ? 1 : 2, npc.FullName + " injured in an attack", subject: npc.Id, severity: 1f - npc.Health);
            }
        }

        private void HitCharacter(ServerCharacter c, ServerCharacter other, WeaponDefinition weapon, float damage, AttackOutcome outcome)
        {
            // Non-lethal weapons stop short of putting anyone in hospital.
            if (!weapon.Lethal) damage = Math.Min(damage, Math.Max(0f, other.Health - NonLethalFloor));
            _w.PowerUse.Hurt(other, damage, weapon.DisplayName);
            outcome.TargetDown = other.Health <= 0f || other.Injury == InjuryState.Incapacitated;
            _w.Phone.Send(other, EntityId.None, "", Phone.MessageCategory.Emergency,
                (outcome.TargetDown ? "You were knocked down" : "You were attacked") + (weapon.Id == "fists" ? "." : " with a " + weapon.DisplayName.ToLowerInvariant() + "."));
        }

        /// <summary>Most people run; a few fight back if the attacker is not holding a gun and they are able.</summary>
        private void React(ServerCharacter c, NpcRecord npc, WeaponDefinition weapon, AttackOutcome outcome, DeterministicRandom rng)
        {
            var bold = (1f - npc.Personality.Agreeableness) * 0.6f + (1f - npc.Personality.Neuroticism) * 0.4f;
            var age = npc.AgeYears(_w.Today);
            var able = npc.Health > 0.45f && age >= 16 && age <= 65 && !State.IsBlinded(npc.Id, Now);
            var fightChance = able && weapon.Kind == WeaponKind.Melee ? 0.35 * bold : 0.0;
            if (rng.Chance(fightChance))
            {
                outcome.Retaliated = true;
                State.Aggression[(npc.Id, c.CharacterId)] = Now;
                if (rng.Chance(0.6)) _w.PowerUse.Hurt(c, 0.05f + 0.05f * (float)rng.NextDouble(), "a fight");
                return;
            }
            Flee(npc);
        }

        /// <summary>Anyone close to gunfire or a killing goes home for the day.</summary>
        private int Scatter(WorldPosition at, WeaponDefinition weapon, EntityId except)
        {
            var radius = Math.Min(weapon.NoiseRadius, 60f);
            var fled = 0;
            foreach (var o in _w.Crimes.ObserversNear(at, radius, except, 1f))
            {
                if (o.IsCamera) continue;
                var npc = _w.Population.Get(o.Id);
                if (npc == null || !npc.Alive) continue;
                Flee(npc);
                if (++fled >= 40) break;
            }
            return fled;
        }

        private void Flee(NpcRecord npc)
        {
            if (npc.OverrideUntilDay > _w.Today && npc.OverrideActivity == ActivityKind.Hospitalized) return;
            var household = _w.Population.GetHousehold(npc.Household);
            npc.OverrideActivity = ActivityKind.AtHome;
            npc.OverridePlace = household != null ? household.Home : npc.Home;
            npc.OverrideUntilDay = _w.Today + 1;
            _w.Director.Invalidate(npc.Id);
            _w.Dirty.Mark(SaveChunks.Population);
        }

        private string DistrictName(WorldPosition at)
        {
            var d = _w.Geography.GetDistrict(_w.Crimes.DistrictAt(at));
            return d != null ? d.Name : _w.Config.Identity.CityName;
        }

        private static bool Finite(WorldPosition p) =>
            !float.IsNaN(p.X) && !float.IsNaN(p.Z) && !float.IsInfinity(p.X) && !float.IsInfinity(p.Z) && Math.Abs(p.X) < 100000f && Math.Abs(p.Z) < 100000f;

        private static bool TakeOne(ServerCharacter c, string itemId)
        {
            for (var i = 0; i < c.Inventory.Count; i++)
            {
                var s = c.Inventory[i];
                if (s.ItemId != itemId || s.Quantity <= 0) continue;
                s.Quantity--;
                if (s.Quantity == 0) c.Inventory.RemoveAt(i);
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ buying and permits

        /// <summary>Buys a weapon over the counter. The item rides in the payment's journal entry, so a crash cannot lose it.</summary>
        public OpResult BuyWeapon(ServerCharacter c, BusinessRecord shop, string weaponId, string idempotencyKey)
        {
            var weapon = Weapon(weaponId);
            if (weapon == null || string.IsNullOrEmpty(weapon.ItemId)) return OpResult.Fail("They don't sell that.");
            var check = CanSell(c, shop, weapon);
            if (!check.Success) return check;
            if (!string.IsNullOrEmpty(weapon.RequiresLicense) && !HasLicense(c, weapon.RequiresLicense))
                return OpResult.Fail("You need a " + (weapon.RequiresLicense == FirearmPermit ? "firearm permit from City Hall" : weapon.RequiresLicense) + " to buy that.");
            return Sell(c, shop, weapon.ItemId, 1, new Money((long)(weapon.PriceCents * _w.Macro.PriceLevel)), "Bought " + weapon.DisplayName, idempotencyKey);
        }

        public OpResult BuyAmmo(ServerCharacter c, BusinessRecord shop, string weaponId, int packs, string idempotencyKey)
        {
            var weapon = Weapon(weaponId);
            if (weapon == null || !weapon.UsesAmmo || weapon.AmmoPackSize <= 0) return OpResult.Fail("That takes no ammunition.");
            if (packs < 1 || packs > 10) return OpResult.Fail("Between 1 and 10 boxes.");
            var check = CanSell(c, shop, weapon);
            if (!check.Success) return check;
            if (Count(c, weapon.ItemId) <= 0) return OpResult.Fail("They only sell ammunition for a weapon you own.");
            if (!string.IsNullOrEmpty(weapon.RequiresLicense) && !HasLicense(c, weapon.RequiresLicense)) return OpResult.Fail("Permit holders only.");
            return Sell(c, shop, weapon.AmmoItemId, weapon.AmmoPackSize * packs, new Money((long)(weapon.AmmoPackPriceCents * packs * _w.Macro.PriceLevel)),
                "Bought ammunition", idempotencyKey);
        }

        private OpResult CanSell(ServerCharacter c, BusinessRecord shop, WeaponDefinition weapon)
        {
            if (shop == null) return OpResult.Fail("No shop.");
            if (!weapon.SoldBy.Contains(shop.TemplateId)) return OpResult.Fail(shop.Name + " doesn't sell that.");
            if (c.Record.InCustody) return OpResult.Fail("You are in custody.");
            if (_w.Story != null && _w.Story.Restrictions.Contains("weapons.buy")) return OpResult.Fail("They won't sell weapons to a teenager.");
            var place = _w.Geography.GetPlace(shop.Place);
            if (place != null && !place.IsOpenAt(_w.Clock.Now.MinuteOfDay)) return OpResult.Fail(shop.Name + " is closed.");
            if (c.Record.ActiveWarrant && weapon.Kind == WeaponKind.Firearm) return OpResult.Fail("The background check comes back with a warrant.");
            return OpResult.Ok();
        }

        private OpResult Sell(ServerCharacter c, BusinessRecord shop, string itemId, int quantity, Money price, string description, string key)
        {
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = key ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = description,
                Money = LedgerTransaction.Transfer(c.CheckingAccount, shop.Account, price, TransactionReason.Purchase, description),
                Records = new TransactionRecords { Items = new List<ItemGrant> { new ItemGrant { Character = c.CharacterId, ItemId = itemId, Quantity = quantity } } },
            });
            return result;
        }

        /// <summary>
        /// A five-year firearm permit from City Hall: no felony convictions, no warrant, not in custody, fee to the
        /// treasury. The license rides in the fee's journal entry.
        /// </summary>
        public OpResult ApplyForFirearmPermit(ServerCharacter c, string idempotencyKey)
        {
            if (HasLicense(c, FirearmPermit)) return OpResult.Fail("You already hold a permit.");
            if (c.Record.InCustody) return OpResult.Fail("You are in custody.");
            if (c.Record.ActiveWarrant) return OpResult.Fail("Denied: there is a warrant out for you.");
            if (_w.Story != null && _w.Story.Restrictions.Contains("civic.firearm_permit")) return OpResult.Fail("Denied: you must be 21.");
            foreach (var charge in c.Record.Charges)
            {
                if (!charge.Convicted) continue;
                var type = _w.Content.FindCrime(charge.CrimeTypeId);
                if (type != null && (type.Severity >= 5 || type.Category == Crime.CrimeCategory.Violent))
                    return OpResult.Fail("Denied: a conviction for " + type.DisplayName.ToLowerInvariant() + " disqualifies you.");
            }
            var fee = new Money((long)(PermitFeeCents * _w.Macro.PriceLevel));
            return _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Firearm permit",
                Money = LedgerTransaction.Transfer(c.CheckingAccount, _w.Accounts.Treasury, fee, TransactionReason.Fee, "Firearm permit fee"),
                Records = new TransactionRecords
                {
                    License = new LicenseGrant
                    {
                        Character = c.CharacterId,
                        License = new License { Kind = FirearmPermit, IssuedDay = _w.Today, ExpiresDay = _w.Today + PermitYears * 365 },
                    },
                },
            });
        }
    }
}
