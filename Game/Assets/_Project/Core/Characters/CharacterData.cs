using System;
using System.Collections.Generic;

namespace HeroGame.Core.Characters
{
    using HeroGame.Core.Crime;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Identity;
    using HeroGame.Core.Phone;
    using HeroGame.Core.Population;
    using HeroGame.Core.Powers;

    // ------------------------------------------------------------------------------------------
    // Data layering (TDD §6.2):
    //   AccountProfile      — global, owned by the platform/account service. Appearance & name.
    //   ServerCharacter     — one per (account, server). Everything that is "your life" there.
    //   World state         — owned by the server (see WorldState).
    //   Session state       — runtime only, never persisted (lives in the Unity layer).
    // ------------------------------------------------------------------------------------------

    /// <summary>Face/body parameters. Morph values are named so new sliders never break old saves.</summary>
    [Serializable]
    public sealed class AppearanceData
    {
        public int BodyBaseId;
        public int SkinTone;
        public float HeightCm = 175f;
        public float BodyWeight = 0.5f;
        public float Muscularity = 0.5f;
        public int HairStyleId;
        public string HairColorHex = "#2B1B10";
        public int EyebrowStyleId;
        public string EyeColorHex = "#4B3621";
        public int FacialHairId;
        /// <summary>Face and body sliders by morph id (see <see cref="AppearanceCatalog.Morphs"/>); 0.5 = average.</summary>
        public List<NamedValue> FaceMorphs = new List<NamedValue>();

        // Catalog-based looks (appearance.json). The numeric style ids above are from before the catalog and unused.
        public string HairStyle = "";
        public string HairHighlightHex = "";
        public string FacialHairStyle = "";
        public string FacialHairColorHex = "";
        public string EyebrowStyle = "";
        public string EyebrowColorHex = "";
        /// <summary>0 = cool (pink) undertone, 1 = warm (golden/olive).</summary>
        public float SkinUndertone = 0.5f;
        /// <summary>Freckles, moles, age lines, blemishes… by id, 0..1.</summary>
        public List<NamedValue> SkinDetails = new List<NamedValue>();
        public string Makeup = "";
        public float MakeupIntensity;
        public List<TattooPlacement> Tattoos = new List<TattooPlacement>();

        public float GetDetail(string name)
        {
            foreach (var d in SkinDetails) if (d.Name == name) return d.Value;
            return 0f;
        }

        public void SetDetail(string name, float value)
        {
            value = Math.Max(0f, Math.Min(1f, value));
            for (var i = 0; i < SkinDetails.Count; i++)
            {
                if (SkinDetails[i].Name != name) continue;
                SkinDetails[i] = new NamedValue { Name = name, Value = value };
                return;
            }
            SkinDetails.Add(new NamedValue { Name = name, Value = value });
        }

        public AppearanceData Clone()
        {
            var copy = (AppearanceData)MemberwiseClone();
            copy.FaceMorphs = new List<NamedValue>(FaceMorphs ?? new List<NamedValue>());
            copy.SkinDetails = new List<NamedValue>(SkinDetails ?? new List<NamedValue>());
            copy.Tattoos = new List<TattooPlacement>();
            if (Tattoos != null)
                foreach (var t in Tattoos) copy.Tattoos.Add(new TattooPlacement { Zone = t.Zone, DesignId = t.DesignId, Scale = t.Scale, Fade = t.Fade });
            return copy;
        }

        public float GetMorph(string name, float fallback = 0.5f)
        {
            foreach (var m in FaceMorphs) if (m.Name == name) return m.Value;
            return fallback;
        }

        public void SetMorph(string name, float value)
        {
            value = Math.Max(0f, Math.Min(1f, value));
            for (var i = 0; i < FaceMorphs.Count; i++)
            {
                if (FaceMorphs[i].Name != name) continue;
                FaceMorphs[i] = new NamedValue { Name = name, Value = value };
                return;
            }
            FaceMorphs.Add(new NamedValue { Name = name, Value = value });
        }
    }

    [Serializable]
    public struct NamedValue
    {
        public string Name;
        public float Value;
    }

    /// <summary>The global character identity created once on first login (GDD §4).</summary>
    [Serializable]
    public sealed class CharacterIdentity
    {
        public string FirstName = "";
        public string LastName = "";
        public int Age = 21;
        public GenderPresentation Presentation;
        public string VoicePresetId = "";
        public AppearanceData Appearance = new AppearanceData();
        /// <summary>How this person walks (animations.json walk styles).</summary>
        public string WalkStyleId = "";
        /// <summary>Clothes picked in the creator from the starter range; each new world gives them to the character.</summary>
        public Outfit StartingOutfit = new Outfit();

        public string FullName => FirstName + " " + LastName;

        public CharacterIdentity Clone()
        {
            var copy = (CharacterIdentity)MemberwiseClone();
            copy.Appearance = (Appearance ?? new AppearanceData()).Clone();
            copy.StartingOutfit = (StartingOutfit ?? new Outfit()).Copy();
            return copy;
        }
    }

    [Serializable]
    public sealed class AccountProfile
    {
        public int SchemaVersion = 1;
        public EntityId AccountId;
        public string DisplayName = "";
        public CharacterIdentity Character = new CharacterIdentity();
        public bool CharacterCreated;
        public List<string> FavoriteServers = new List<string>();
        public List<string> RecentServers = new List<string>();
        public List<EntityId> Friends = new List<EntityId>();
    }

    [Serializable]
    public sealed class InventoryStack
    {
        public string ItemId = "";
        public int Quantity = 1;
        /// <summary>Unique items (weapons with serials, stolen goods) carry an entity id.</summary>
        public EntityId Instance;
        public bool Stolen;
    }

    [Serializable]
    public sealed class CareerEntry
    {
        public string OccupationId = "";
        public EntityId Employer;
        public long StartDay;
        public long EndDay;
        public int Rank;
        public float Performance = 0.5f;
        public string EndReason = "";
    }

    [Serializable]
    public sealed class License
    {
        public string Kind = "";
        public long IssuedDay;
        public long ExpiresDay;
        public bool Suspended;
    }

    public enum InjuryState
    {
        Healthy,
        Injured,
        SeverelyInjured,
        Incapacitated,
        Hospitalized,
        Dead,
    }

    public enum SocietalRole
    {
        OrdinaryCitizen,
        HiddenPowered,
        PublicHero,
        MaskedVigilante,
        SanctionedHero,
        Criminal,
        Villain,
        Mercenary,
        IndependentAnomaly,
    }

    /// <summary>
    /// A character's life on one server (GDD §5). Money and ownership live in the server ledger and
    /// ownership registry, referenced here by account id; this record never stores balances itself.
    /// </summary>
    [Serializable]
    public sealed class ServerCharacter
    {
        public int SchemaVersion = 1;
        public EntityId CharacterId;
        public EntityId AccountId;
        public string ServerId = "";
        public long CreatedDay;
        public long LastOnlineSecond;

        /// <summary>Name and appearance this character was created with (empty for characters saved before it was kept).</summary>
        public CharacterIdentity Identity = new CharacterIdentity();

        public EntityId CheckingAccount;
        public EntityId SavingsAccount;
        public EntityId HomeProperty;
        public EntityId Household;

        public WorldPosition LastPosition;
        public InjuryState Injury;
        public float Health = 1f;
        /// <summary>Hospital discharge time (game seconds); 0 when not admitted.</summary>
        public long HospitalUntilSecond;
        /// <summary>Unpaid share of hospital bills (hurts credit until paid).</summary>
        public long MedicalDebtCents;

        public List<InventoryStack> Inventory = new List<InventoryStack>();
        public List<string> OwnedOutfits = new List<string>();
        /// <summary>Id of the outfit being worn (one of <see cref="Outfits"/>, or an alias costume).</summary>
        public string CurrentOutfit = "";
        /// <summary>Saved outfits. The garments themselves are inventory items ("wear:item:variant").</summary>
        public List<Outfit> Outfits = new List<Outfit>();
        public List<CareerEntry> Careers = new List<CareerEntry>();
        public EducationLevel Education = EducationLevel.HighSchool;
        public List<License> Licenses = new List<License>();
        public ReputationProfile Reputation = new ReputationProfile();
        public CriminalRecord Record = new CriminalRecord();
        public CharacterPowers Powers = new CharacterPowers();
        public SocietalRole Role;
        public AliasIdentity Alias;
        public List<Relationship> Relationships = new List<Relationship>();
        public List<EntityId> Organizations = new List<EntityId>();
        public List<string> Achievements = new List<string>();
        public List<PhoneContact> PhoneContacts = new List<PhoneContact>();
        public List<PhoneMessage> Inbox = new List<PhoneMessage>();
        public long NextMessageId;
        public List<StatementLine> Statement = new List<StatementLine>();
    }
}
