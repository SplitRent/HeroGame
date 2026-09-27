using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Economy
{
    public enum InsuranceKind
    {
        /// <summary>Buildings: storm, fire, vandalism and burglary damage to the structure.</summary>
        Property,
        /// <summary>Collision, storm and theft for one vehicle.</summary>
        Vehicle,
        /// <summary>Lost trading days when a business is closed by an emergency order.</summary>
        BusinessInterruption,
        /// <summary>A share of the holder's medical bills (hospital stays, treatment).</summary>
        Health,
    }

    public enum PolicyStatus
    {
        Active,
        Lapsed,
        Cancelled,
    }

    /// <summary>
    /// An insurance contract (GDD §27). The insurer never trusts a claimed amount: losses are computed from
    /// the insured asset's recorded damage (repair bills, business reports), so claims cannot be inflated.
    /// </summary>
    [Serializable]
    public sealed class InsurancePolicy
    {
        public EntityId Id;
        public InsuranceKind Kind;
        public EntityId Holder;
        public EntityId HolderAccount;
        /// <summary>Property, vehicle or business covered (None for health).</summary>
        public EntityId Asset;
        public long CoverageCents;
        public long DeductibleCents;
        public long MonthlyPremiumCents;
        public long StartDay;
        public long NextPremiumDay;
        public int MissedPremiums;
        public PolicyStatus Status;
        public int Claims;
        public long PaidOutCents;
        /// <summary>Health: deductible already met this policy year, and the year it applies to.</summary>
        public long DeductibleMetCents;
        public long DeductibleYear;
        /// <summary>Health: fraction of bills above the deductible that the insurer pays.</summary>
        public float CoverShare = 0.8f;

        public bool Active => Status == PolicyStatus.Active;
        public long RemainingCoverageCents => Math.Max(0, CoverageCents - PaidOutCents);
    }

    public sealed class InsuranceQuote
    {
        public bool Available;
        public string Reason = "";
        public InsuranceKind Kind;
        public EntityId Asset;
        public long CoverageCents;
        public long DeductibleCents;
        public long MonthlyPremiumCents;
    }

    /// <summary>All policies on a server. Persisted in the transactional chunk; new policies arrive with their first premium.</summary>
    public sealed class InsuranceBook
    {
        private readonly Dictionary<EntityId, InsurancePolicy> _policies = new Dictionary<EntityId, InsurancePolicy>();

        public IEnumerable<InsurancePolicy> All => _policies.Values;
        public int Count => _policies.Count;

        public void Restore(InsurancePolicy p) => _policies[p.Id] = p;
        public InsurancePolicy Get(EntityId id) => _policies.TryGetValue(id, out var p) ? p : null;

        public List<InsurancePolicy> ForHolder(EntityId holder)
        {
            var list = new List<InsurancePolicy>();
            foreach (var p in _policies.Values) if (p.Holder == holder) list.Add(p);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        public InsurancePolicy ActiveFor(EntityId asset, InsuranceKind kind)
        {
            InsurancePolicy best = null;
            foreach (var p in _policies.Values)
                if (p.Active && p.Kind == kind && p.Asset == asset && (best == null || p.Id.CompareTo(best.Id) < 0)) best = p;
            return best;
        }

        public InsurancePolicy ActiveHealth(EntityId holder)
        {
            foreach (var p in _policies.Values)
                if (p.Active && p.Kind == InsuranceKind.Health && p.Holder == holder) return p;
            return null;
        }

        public int ClaimsBy(EntityId holder)
        {
            var n = 0;
            foreach (var p in _policies.Values) if (p.Holder == holder) n += p.Claims;
            return n;
        }
    }

    /// <summary>Actuarial pricing. Pure functions so quotes are reproducible and testable.</summary>
    public static class InsurancePricing
    {
        public const double PropertyAnnualRate = 0.0035;
        public const double VehicleAnnualRate = 0.035;
        public const long VehicleBaseAnnualCents = 30000;
        public const long HealthMonthlyCents = 28000;
        public const long HealthDeductibleCents = 150000;
        public const long HealthCoverageCents = 100000000;
        /// <summary>Claims filed in the first days of a policy are refused (no buying cover as the storm arrives).</summary>
        public const int WaitingPeriodDays = 14;

        /// <summary>A higher deductible (relative to 10 % of coverage) lowers the premium by up to 35 %.</summary>
        public static double DeductibleFactor(long deductibleCents, long coverageCents)
        {
            var reference = Math.Max(1.0, coverageCents * 0.1);
            return 1.0 - 0.35 * Math.Min(1.0, Math.Max(0, deductibleCents) / reference);
        }

        public static double ClaimsLoading(int priorClaims) => 1.0 + 0.15 * Math.Min(6, Math.Max(0, priorClaims));

        public static long PropertyMonthly(long valueCents, long deductibleCents, float floodRisk, int securityLevel, bool alarm, bool cameras, int priorClaims)
        {
            var security = 1.0 - 0.05 * Math.Min(3, Math.Max(0, securityLevel)) - (alarm ? 0.05 : 0) - (cameras ? 0.03 : 0);
            var annual = valueCents * PropertyAnnualRate * (1.0 + 2.5 * floodRisk) * security * ClaimsLoading(priorClaims) * DeductibleFactor(deductibleCents, valueCents);
            return Math.Max(1500, (long)Math.Round(annual / 12.0));
        }

        public static long VehicleMonthly(long valueCents, long deductibleCents, int priorClaims, float driverRisk)
        {
            var annual = (valueCents * VehicleAnnualRate + VehicleBaseAnnualCents) * ClaimsLoading(priorClaims) * (1.0 + Math.Max(0f, driverRisk))
                         * DeductibleFactor(deductibleCents, valueCents);
            return Math.Max(2500, (long)Math.Round(annual / 12.0));
        }

        /// <summary>Priced on expected closure days: coastal (flood-prone) districts close more often.</summary>
        public static long InterruptionMonthly(long averageDailyProfitCents, long dailyFixedCostsCents, float floodRisk)
        {
            var dailyLoss = Math.Max(0, averageDailyProfitCents) + Math.Max(0, dailyFixedCostsCents);
            var expectedClosureDays = 1.5 + 6.0 * floodRisk;
            var annual = dailyLoss * expectedClosureDays * 1.35; // loss ratio ~74 %
            return Math.Max(2000, (long)Math.Round(annual / 12.0));
        }

        public static long HealthMonthly(int priorClaims) => (long)(HealthMonthlyCents * (1.0 + 0.05 * Math.Min(6, priorClaims)));
    }
}
