using System;
using System.Collections.Generic;

namespace HeroGame.Core.Config
{
    /// <summary>
    /// Complete data-driven configuration of one world (a multiplayer server or Story Mode).
    /// Loaded from JSON; changing a server never requires rebuilding the game (GDD §8).
    /// Every numeric value is clamped by <see cref="ServerConfigValidator"/> before use.
    /// </summary>
    [Serializable]
    public sealed class ServerConfig
    {
        public int SchemaVersion = 1;
        public ServerIdentityConfig Identity = new ServerIdentityConfig();
        public GameplayConfig Gameplay = new GameplayConfig();
        public EconomyConfig Economy = new EconomyConfig();
        public PowerConfig Powers = new PowerConfig();
        public CommunityConfig Community = new CommunityConfig();
    }

    [Serializable]
    public sealed class ServerIdentityConfig
    {
        public string ServerName = "Port Arden";
        public string Description = "";
        public string CityName = "Port Arden";
        public string CitySlogan = "Built on the Tide";
        public string PrimaryColorHex = "#1B4F72";
        public string SecondaryColorHex = "#F0B429";
        public string FlagAssetId = "";
        public string LogoAssetId = "";
        public string GovernmentName = "City of Port Arden";
        public string PoliceDepartmentName = "Port Arden Police Department";
        public string FireDepartmentName = "Port Arden Fire Rescue";
        public List<string> HospitalNames = new List<string> { "Arden General Hospital", "St. Brigid Medical Center" };
        public List<string> SchoolNames = new List<string> { "Eastwater High School", "Pinecrest Hollow High School" };
        public List<string> UniversityNames = new List<string> { "Tarrow State University" };
        public List<SportsTeamConfig> SportsTeams = new List<SportsTeamConfig>();
        public List<string> NewsOrganizations = new List<string> { "Channel 9 Gulfwatch", "The Arden Ledger" };
        public string Region = "NA-South";
        public int WorldSeed = 20260927;
    }

    [Serializable]
    public sealed class SportsTeamConfig
    {
        public string Sport = "";
        public string Name = "";
        public string Mascot = "";
        public string PrimaryColorHex = "#000000";
    }

    public enum DeathRule
    {
        /// <summary>Hospitalised, pay bill, keep everything.</summary>
        Hospital,
        /// <summary>Hospitalised and lose carried cash/illegal items.</summary>
        HospitalWithLoss,
        /// <summary>Hardcore: character life ends on this server.</summary>
        Permadeath,
    }

    [Serializable]
    public sealed class GameplayConfig
    {
        public int MaxPlayers = 64;
        public bool PvpEnabled = true;
        public bool RoleplayServer = false;
        public float CrimeSeverityMultiplier = 1f;
        public float PoliceResponseMultiplier = 1f;
        public float NpcDensity = 1f;
        public float TrafficDensity = 1f;
        public bool DynamicWeather = true;
        public string FixedWeather = "";
        public float RealMinutesPerGameDay = 48f;
        public DeathRule DeathRule = DeathRule.Hospital;
        public float RespawnDelaySeconds = 20f;
        public bool InjuriesEnabled = true;
        public bool VehiclePersistence = true;
        public bool PropertyPersistence = true;
        public bool PlayerElections = false;
        /// <summary>Served game days per guideline sentence day (0.1 → a 30-day sentence is served in 3 game days).</summary>
        public float SentenceScale = 0.1f;
    }

    [Serializable]
    public sealed class EconomyConfig
    {
        /// <summary>Global cost-of-living/price difficulty. 1 = canonical.</summary>
        public float Difficulty = 1f;
        public float IncomeTaxRate = 0.12f;
        public float SalesTaxRate = 0.0825f;
        public float PropertyTaxAnnualRate = 0.021f;
        public float BusinessTaxRate = 0.09f;
        public float PropertyTransferTaxRate = 0.01f;
        public long VehicleRegistrationFeeCents = 7500;
        public float PropertyPriceMultiplier = 1f;
        public float BusinessPriceMultiplier = 1f;
        public float WageMultiplier = 1f;
        public float BusinessRevenueMultiplier = 1f;
        public float CrimePayoutMultiplier = 1f;
        public long StartingCashCents = 250000;
        public float LoanBaseAnnualRate = 0.069f;
    }

    [Serializable]
    public sealed class PowerConfig
    {
        public bool PowersEnabled = true;
        /// <summary>Multiplier on the base daily anomaly-event chance.</summary>
        public float AnomalyFrequencyMultiplier = 1f;
        /// <summary>Multiplier on exposure → manifestation chance.</summary>
        public float ManifestationMultiplier = 1f;
        /// <summary>Multiplier on second/third power chance. Hard capped by the validator.</summary>
        public float MultiplePowerMultiplier = 1f;
        public int MaxPowersPerCharacter = 3;
    }

    [Serializable]
    public sealed class CommunityConfig
    {
        public string OwnerAccountId = "";
        public List<string> Administrators = new List<string>();
        public List<string> Moderators = new List<string>();
        public bool WhitelistEnabled = false;
        public List<string> Whitelist = new List<string>();
        public List<string> Blacklist = new List<string>();
        public bool PasswordProtected = false;
        public string AgeRating = "M";
        public bool VoiceChatEnabled = true;
        public bool ProximityVoice = true;
        public List<string> ChatRules = new List<string>();
        public List<string> Announcements = new List<string>();
    }
}
