using System;
using System.Text.RegularExpressions;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Config
{
    /// <summary>
    /// Clamps server configuration into ranges that cannot break server integrity
    /// (GDD §177): owners get wide creative control, but no value can create
    /// infinite money, zero-cost property, or make multiple powers common.
    /// </summary>
    public static class ServerConfigValidator
    {
        public const float MaxMultiplePowerMultiplier = 5f;
        public const int AbsoluteMaxPlayers = 256;

        private static readonly Regex HexColor = new Regex("^#[0-9A-Fa-f]{6}$");

        /// <summary>Validates in place, clamping out-of-range values. Errors mean the config is unusable.</summary>
        public static ValidationReport ValidateAndClamp(ServerConfig config)
        {
            var report = new ValidationReport();
            if (config == null)
            {
                report.Error("", "Config is null.");
                return report;
            }

            if (config.Identity == null) { config.Identity = new ServerIdentityConfig(); report.Warn("Identity", "Missing; defaults applied."); }
            if (config.Gameplay == null) { config.Gameplay = new GameplayConfig(); report.Warn("Gameplay", "Missing; defaults applied."); }
            if (config.Economy == null) { config.Economy = new EconomyConfig(); report.Warn("Economy", "Missing; defaults applied."); }
            if (config.Powers == null) { config.Powers = new PowerConfig(); report.Warn("Powers", "Missing; defaults applied."); }
            if (config.Community == null) { config.Community = new CommunityConfig(); report.Warn("Community", "Missing; defaults applied."); }

            var id = config.Identity;
            if (string.IsNullOrWhiteSpace(id.ServerName)) report.Error("Identity.ServerName", "Server name is required.");
            else if (id.ServerName.Length > 64) { id.ServerName = id.ServerName.Substring(0, 64); report.Warn("Identity.ServerName", "Truncated to 64 characters."); }
            if (string.IsNullOrWhiteSpace(id.CityName)) { id.CityName = "Port Arden"; report.Warn("Identity.CityName", "Empty; using canonical name."); }
            if (!HexColor.IsMatch(id.PrimaryColorHex ?? "")) { id.PrimaryColorHex = "#1B4F72"; report.Warn("Identity.PrimaryColorHex", "Invalid colour; reset."); }
            if (!HexColor.IsMatch(id.SecondaryColorHex ?? "")) { id.SecondaryColorHex = "#F0B429"; report.Warn("Identity.SecondaryColorHex", "Invalid colour; reset."); }

            var g = config.Gameplay;
            g.MaxPlayers = ClampInt(report, "Gameplay.MaxPlayers", g.MaxPlayers, 1, AbsoluteMaxPlayers);
            g.CrimeSeverityMultiplier = Clamp(report, "Gameplay.CrimeSeverityMultiplier", g.CrimeSeverityMultiplier, 0.25f, 4f);
            g.PoliceResponseMultiplier = Clamp(report, "Gameplay.PoliceResponseMultiplier", g.PoliceResponseMultiplier, 0.25f, 4f);
            g.SentenceScale = Clamp(report, "Gameplay.SentenceScale", g.SentenceScale, 0.01f, 1f);
            g.ElectionIntervalDays = ClampInt(report, "Gameplay.ElectionIntervalDays", g.ElectionIntervalDays, 30, 3650);
            g.DisasterFrequencyMultiplier = Clamp(report, "Gameplay.DisasterFrequencyMultiplier", g.DisasterFrequencyMultiplier, 0f, 5f);
            g.NpcDensity = Clamp(report, "Gameplay.NpcDensity", g.NpcDensity, 0.1f, 2f);
            g.TrafficDensity = Clamp(report, "Gameplay.TrafficDensity", g.TrafficDensity, 0.1f, 2f);
            g.RealMinutesPerGameDay = Clamp(report, "Gameplay.RealMinutesPerGameDay", g.RealMinutesPerGameDay, 10f, 1440f);
            g.RespawnDelaySeconds = Clamp(report, "Gameplay.RespawnDelaySeconds", g.RespawnDelaySeconds, 0f, 600f);

            var e = config.Economy;
            e.Difficulty = Clamp(report, "Economy.Difficulty", e.Difficulty, 0.25f, 4f);
            e.IncomeTaxRate = Clamp(report, "Economy.IncomeTaxRate", e.IncomeTaxRate, 0f, 0.6f);
            e.SalesTaxRate = Clamp(report, "Economy.SalesTaxRate", e.SalesTaxRate, 0f, 0.3f);
            e.PropertyTaxAnnualRate = Clamp(report, "Economy.PropertyTaxAnnualRate", e.PropertyTaxAnnualRate, 0f, 0.1f);
            e.BusinessTaxRate = Clamp(report, "Economy.BusinessTaxRate", e.BusinessTaxRate, 0f, 0.6f);
            e.PropertyTransferTaxRate = Clamp(report, "Economy.PropertyTransferTaxRate", e.PropertyTransferTaxRate, 0f, 0.1f);
            e.VehicleRegistrationFeeCents = ClampLong(report, "Economy.VehicleRegistrationFeeCents", e.VehicleRegistrationFeeCents, 0, 10000000);
            // Price floors keep property from becoming effectively free (an exploit vector for laundering / flipping).
            e.PropertyPriceMultiplier = Clamp(report, "Economy.PropertyPriceMultiplier", e.PropertyPriceMultiplier, 0.25f, 10f);
            e.BusinessPriceMultiplier = Clamp(report, "Economy.BusinessPriceMultiplier", e.BusinessPriceMultiplier, 0.25f, 10f);
            e.WageMultiplier = Clamp(report, "Economy.WageMultiplier", e.WageMultiplier, 0.25f, 4f);
            e.BusinessRevenueMultiplier = Clamp(report, "Economy.BusinessRevenueMultiplier", e.BusinessRevenueMultiplier, 0.25f, 4f);
            e.CrimePayoutMultiplier = Clamp(report, "Economy.CrimePayoutMultiplier", e.CrimePayoutMultiplier, 0f, 4f);
            e.StartingCashCents = ClampLong(report, "Economy.StartingCashCents", e.StartingCashCents, 0, 100000000);
            e.LoanBaseAnnualRate = Clamp(report, "Economy.LoanBaseAnnualRate", e.LoanBaseAnnualRate, 0f, 0.35f);

            var p = config.Powers;
            p.AnomalyFrequencyMultiplier = Clamp(report, "Powers.AnomalyFrequencyMultiplier", p.AnomalyFrequencyMultiplier, 0f, 10f);
            p.ManifestationMultiplier = Clamp(report, "Powers.ManifestationMultiplier", p.ManifestationMultiplier, 0f, 10f);
            // Multiple powers must stay an extraordinary anomaly (GDD §45) regardless of server settings.
            p.MultiplePowerMultiplier = Clamp(report, "Powers.MultiplePowerMultiplier", p.MultiplePowerMultiplier, 0f, MaxMultiplePowerMultiplier);
            p.MaxPowersPerCharacter = ClampInt(report, "Powers.MaxPowersPerCharacter", p.MaxPowersPerCharacter, 1, 3);
            p.OutputCap = Clamp(report, "Powers.OutputCap", p.OutputCap, 0.2f, 1.5f);

            return report;
        }

        private static float Clamp(ValidationReport r, string path, float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                r.Warn(path, "Not a number; reset to minimum " + min + ".");
                return min;
            }
            var clamped = Math.Min(max, Math.Max(min, value));
            if (clamped != value) r.Warn(path, "Clamped " + value + " to " + clamped + ".");
            return clamped;
        }

        private static int ClampInt(ValidationReport r, string path, int value, int min, int max)
        {
            var clamped = Math.Min(max, Math.Max(min, value));
            if (clamped != value) r.Warn(path, "Clamped " + value + " to " + clamped + ".");
            return clamped;
        }

        private static long ClampLong(ValidationReport r, string path, long value, long min, long max)
        {
            var clamped = Math.Min(max, Math.Max(min, value));
            if (clamped != value) r.Warn(path, "Clamped " + value + " to " + clamped + ".");
            return clamped;
        }
    }
}
