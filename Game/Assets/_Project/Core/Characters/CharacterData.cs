using System;
using System.Collections.Generic;
using HeroGame.Core.Crime;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;

namespace HeroGame.Core.Characters
{
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
        public List<NamedValue> FaceMorphs = new List<NamedValue>();

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

        public string FullName => FirstName + " " + LastName;
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

        public EntityId CheckingAccount;
        public EntityId SavingsAccount;
        public EntityId HomeProperty;
        public EntityId Household;

        public WorldPosition LastPosition;
        public InjuryState Injury;
        public float Health = 1f;

        public List<InventoryStack> Inventory = new List<InventoryStack>();
        public List<string> OwnedOutfits = new List<string>();
        public string CurrentOutfit = "";
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
        public List<string> Contacts = new List<string>();
    }
}
