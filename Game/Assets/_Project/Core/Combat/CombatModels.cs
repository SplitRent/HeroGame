using System;
using System.Collections.Generic;

namespace HeroGame.Core.Combat
{
    using HeroGame.Core.Foundation;

    public enum WeaponKind { Melee, Spray, Stun, Firearm }

    /// <summary>An original weapon design (weapons.json). Balance lives in data; rules live in CombatService.</summary>
    [Serializable]
    public sealed class WeaponDefinition
    {
        public string Id = "";
        public string DisplayName = "";
        /// <summary>Inventory item the player must carry ("" for fists).</summary>
        public string ItemId = "";
        public WeaponKind Kind;
        /// <summary>Health removed per hit (0..1 of a healthy adult), before a ±20% roll.</summary>
        public float Damage;
        public float Range;
        public float CooldownSeconds;
        /// <summary>Hit chance at point blank for a healthy attacker.</summary>
        public float Accuracy;
        /// <summary>Can kill. Non-lethal weapons never take anyone below a safe floor.</summary>
        public bool Lethal;
        public float StunSeconds;
        public float BlindSeconds;
        public string AmmoItemId = "";
        public int AmmoPackSize;
        public long AmmoPackPriceCents;
        /// <summary>How far away people notice (gunshots carry for blocks).</summary>
        public float NoiseRadius = 20f;
        /// <summary>Crime charged for an unjustified attack with this weapon (homicide if it kills).</summary>
        public string CrimeId = "assault";
        /// <summary>License needed to buy it ("firearm_permit"), or "".</summary>
        public string RequiresLicense = "";
        public long PriceCents;
        /// <summary>Business templates that sell it.</summary>
        public List<string> SoldBy = new List<string>();
        public string Description = "";

        public bool UsesAmmo => !string.IsNullOrEmpty(AmmoItemId);
        public bool Ranged => Kind == WeaponKind.Firearm || Kind == WeaponKind.Stun;
    }

    public enum AttackTargetKind { None, Npc, Character }

    public sealed class AttackRequest
    {
        public string WeaponId = "fists";
        public AttackTargetKind TargetKind;
        public EntityId Target;
        /// <summary>Where the attacker stands (the server's own record online).</summary>
        public WorldPosition Origin;
        /// <summary>Where the target is (from the server's schedule/connection online; ignored for None).</summary>
        public WorldPosition TargetPosition;
    }

    public sealed class AttackOutcome
    {
        public bool Attempted;
        public bool Hit;
        public float Damage;
        public bool TargetDown;
        public bool TargetKilled;
        public bool Stunned;
        public bool Blinded;
        /// <summary>Defending yourself against someone who attacked you first: no crime.</summary>
        public bool Justified;
        /// <summary>The target hit back.</summary>
        public bool Retaliated;
        public int NpcsFled;
        public int AmmoLeft = -1;
        public string Message = "";
        public Simulation.CrimeResult Crime;

        public static AttackOutcome Refused(string why) => new AttackOutcome { Message = why };
    }

    /// <summary>
    /// Short-lived combat state: cooldowns, who attacked whom recently (self-defence), stuns and blindness. Deliberately
    /// not saved: everything here lasts seconds to minutes of game time, and a restart ending a scuffle is fine.
    /// </summary>
    public sealed class CombatState
    {
        public readonly Dictionary<(EntityId, string), long> ReadyAt = new Dictionary<(EntityId, string), long>();
        /// <summary>(aggressor, victim) → game second of the last attack.</summary>
        public readonly Dictionary<(EntityId, EntityId), long> Aggression = new Dictionary<(EntityId, EntityId), long>();
        public readonly Dictionary<EntityId, long> StunnedUntil = new Dictionary<EntityId, long>();
        public readonly Dictionary<EntityId, long> BlindedUntil = new Dictionary<EntityId, long>();
        public long Attacks;

        public bool IsStunned(EntityId who, long now) => StunnedUntil.TryGetValue(who, out var t) && t > now;
        public bool IsBlinded(EntityId who, long now) => BlindedUntil.TryGetValue(who, out var t) && t > now;

        /// <summary>Drops entries that no longer matter (called now and then so the maps stay small).</summary>
        public void Prune(long now, long keepSeconds)
        {
            Prune(ReadyAt, now);
            Prune(StunnedUntil, now);
            Prune(BlindedUntil, now);
            List<(EntityId, EntityId)> old = null;
            foreach (var kv in Aggression)
                if (now - kv.Value > keepSeconds) (old ?? (old = new List<(EntityId, EntityId)>())).Add(kv.Key);
            if (old != null) foreach (var k in old) Aggression.Remove(k);
        }

        private static void Prune<TKey>(Dictionary<TKey, long> map, long now)
        {
            List<TKey> old = null;
            foreach (var kv in map)
                if (kv.Value <= now) (old ?? (old = new List<TKey>())).Add(kv.Key);
            if (old != null) foreach (var k in old) map.Remove(k);
        }
    }
}
