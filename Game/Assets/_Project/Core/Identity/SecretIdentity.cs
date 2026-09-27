using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Identity
{
    public enum IdentityExposure
    {
        Unknown,
        Suspected,
        PartiallyDiscovered,
        PubliclyKnown,
    }

    public enum ClueKind
    {
        SeenChanging,
        VoiceRecognized,
        SharedInjury,
        VehicleLink,
        LocationPattern,
        PowerSignatureMatch,
        Photograph,
        Confession,
        DigitalTrail,
        Unmasked,
        /// <summary>Told by someone else who believes it.</summary>
        Hearsay,
    }

    /// <summary>What one observer (NPC, player, organisation) believes about who is behind an alias.</summary>
    [Serializable]
    public sealed class ObserverKnowledge
    {
        public EntityId Observer;
        /// <summary>0..1 belief that the alias is the civilian.</summary>
        public float Belief;
        public List<ClueKind> Clues = new List<ClueKind>();
        public GameDateTime LastClueAt;
        /// <summary>Has told others / published.</summary>
        public bool Shared;
    }

    /// <summary>A costumed/criminal persona and who knows it belongs to the civilian (GDD §48–50).</summary>
    [Serializable]
    public sealed class AliasIdentity
    {
        public string Alias = "";
        public string CostumeOutfitId = "";
        public EntityId Hideout;
        public IdentityExposure PublicExposure;
        public List<ObserverKnowledge> Knowledge = new List<ObserverKnowledge>();
        /// <summary>People the player chose to tell. They know with certainty.</summary>
        public List<EntityId> Confidants = new List<EntityId>();
    }

    /// <summary>
    /// Evidence-based identity discovery. Nobody discovers an identity automatically: observers
    /// accumulate clues, belief is a noisy-OR of clue strengths, and knowledge spreads only when a
    /// believer shares it. Public exposure needs a public reveal or enough independent believers.
    /// </summary>
    public static class IdentityDiscovery
    {
        public const float SuspectThreshold = 0.35f;
        public const float DiscoveredThreshold = 0.8f;
        public const int PublicBelieversThreshold = 6;

        public static float ClueStrength(ClueKind kind)
        {
            switch (kind)
            {
                case ClueKind.Unmasked: return 0.95f;
                case ClueKind.Confession: return 1f;
                case ClueKind.SeenChanging: return 0.85f;
                case ClueKind.Photograph: return 0.6f;
                case ClueKind.PowerSignatureMatch: return 0.55f;
                case ClueKind.VoiceRecognized: return 0.4f;
                case ClueKind.VehicleLink: return 0.35f;
                case ClueKind.SharedInjury: return 0.3f;
                case ClueKind.DigitalTrail: return 0.3f;
                case ClueKind.LocationPattern: return 0.15f;
                case ClueKind.Hearsay: return 0.7f;
                default: return 0.1f;
            }
        }

        public static ObserverKnowledge AddClue(AliasIdentity alias, EntityId observer, ClueKind clue, GameDateTime when, float quality = 1f)
        {
            ObserverKnowledge k = null;
            foreach (var existing in alias.Knowledge) if (existing.Observer == observer) { k = existing; break; }
            if (k == null)
            {
                k = new ObserverKnowledge { Observer = observer };
                alias.Knowledge.Add(k);
            }
            k.Clues.Add(clue);
            k.LastClueAt = when;
            var strength = Math.Max(0f, Math.Min(1f, ClueStrength(clue) * quality));
            k.Belief = 1f - (1f - k.Belief) * (1f - strength);
            Recompute(alias);
            return k;
        }

        /// <summary>A believer tells someone else; the listener's belief is discounted by trust.</summary>
        public static void Share(AliasIdentity alias, EntityId from, EntityId to, float listenerTrust, GameDateTime when)
        {
            ObserverKnowledge source = null;
            foreach (var k in alias.Knowledge) if (k.Observer == from) { source = k; break; }
            if (source == null || source.Belief < SuspectThreshold) return;
            source.Shared = true;
            AddClue(alias, to, ClueKind.Hearsay, when, Math.Max(0f, Math.Min(1f, source.Belief * listenerTrust)));
        }

        public static void PublicReveal(AliasIdentity alias)
        {
            alias.PublicExposure = IdentityExposure.PubliclyKnown;
        }

        public static IdentityExposure BeliefLevel(AliasIdentity alias, EntityId observer)
        {
            if (alias.PublicExposure == IdentityExposure.PubliclyKnown || alias.Confidants.Contains(observer)) return IdentityExposure.PubliclyKnown;
            foreach (var k in alias.Knowledge)
            {
                if (k.Observer != observer) continue;
                if (k.Belief >= DiscoveredThreshold) return IdentityExposure.PartiallyDiscovered;
                if (k.Belief >= SuspectThreshold) return IdentityExposure.Suspected;
            }
            return IdentityExposure.Unknown;
        }

        private static void Recompute(AliasIdentity alias)
        {
            if (alias.PublicExposure == IdentityExposure.PubliclyKnown) return;
            int believers = 0, suspects = 0, sharers = 0;
            foreach (var k in alias.Knowledge)
            {
                if (k.Belief >= DiscoveredThreshold) believers++;
                else if (k.Belief >= SuspectThreshold) suspects++;
                if (k.Shared && k.Belief >= DiscoveredThreshold) sharers++;
            }
            if (believers >= PublicBelieversThreshold && sharers >= 2) alias.PublicExposure = IdentityExposure.PubliclyKnown;
            else if (believers > 0) alias.PublicExposure = IdentityExposure.PartiallyDiscovered;
            else if (suspects > 0) alias.PublicExposure = IdentityExposure.Suspected;
            else alias.PublicExposure = IdentityExposure.Unknown;
        }
    }
}
