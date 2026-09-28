using System;
using System.Collections.Generic;

namespace HeroGame.Core.Presentation
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;

    /// <summary>One owned property as the player's property screen shows it.</summary>
    public sealed class PortfolioEntry
    {
        public EntityId Property;
        public string Address = "";
        public string District = "";
        public PropertyKind Kind;
        public Money MarketValue;
        public Money MortgageOwed;
        public Money Equity => MarketValue - MortgageOwed;
        public Money MonthlyRentIncome;
        public Money MonthlyMortgagePayment;
        public Money MonthlyInsurance;
        /// <summary>Rent minus mortgage and insurance (property tax is billed separately and shown in arrears).</summary>
        public Money MonthlyNet => MonthlyRentIncome - MonthlyMortgagePayment - MonthlyInsurance;
        public int Units;
        public int OccupiedUnits;
        public int TenantsBehindOnRent;
        public float Condition;
        public DamageState Damage;
        public bool Insured;
        public bool ForSale;
        public Money TaxArrears;
        /// <summary>Things that need the owner's attention, most urgent first.</summary>
        public readonly List<string> Alerts = new List<string>();
    }

    public sealed class Portfolio
    {
        public readonly List<PortfolioEntry> Entries = new List<PortfolioEntry>();
        public Money TotalValue;
        public Money TotalOwed;
        public Money NetWorthInProperty => TotalValue - TotalOwed;
        public Money MonthlyNet;
        public int AlertCount;
    }

    /// <summary>
    /// Read-only view-models for the player's screens (property portfolio, inventory). They only read core state, so
    /// the same code serves the offline UI and a server's "me" requests, and is tested without Unity.
    /// </summary>
    public static class PlayerViews
    {
        public static Portfolio PortfolioOf(Simulation.World w, ServerCharacter c)
        {
            var result = new Portfolio();
            var owned = new List<EntityId>(w.Ownership.AssetsOf(c.CharacterId));
            owned.Sort();
            foreach (var id in owned)
            {
                var p = w.Properties.Get(id);
                if (p == null) continue;
                var e = new PortfolioEntry
                {
                    Property = id, Address = p.Address, Kind = p.Kind, MarketValue = p.MarketValue, Condition = p.Condition, Damage = p.Damage,
                    ForSale = p.ForSale, TaxArrears = new Money(p.TaxArrearsCents),
                };
                var district = w.Geography.GetDistrict(p.District);
                if (district != null) e.District = district.Name;

                // Rent: the whole building's lease, or each unit's.
                if (p.Units.Count == 0)
                {
                    e.Units = 1;
                    if (p.Tenancy != null) Lease(e, p.Tenancy);
                }
                else
                {
                    e.Units = p.Units.Count;
                    foreach (var u in p.Units)
                        if (u.Tenancy != null) Lease(e, u.Tenancy);
                        else if (u.NpcHousehold.IsValid)
                        {
                            e.OccupiedUnits++;
                            e.MonthlyRentIncome += new Money(u.MonthlyRentCents);
                        }
                }

                foreach (var loan in w.Loans.Loans)
                    if (loan.Collateral == id && loan.Borrower == c.CharacterId && loan.Status != LoanStatus.PaidOff && loan.Status != LoanStatus.Defaulted)
                    {
                        e.MortgageOwed += new Money(loan.OutstandingCents);
                        e.MonthlyMortgagePayment += new Money(loan.MonthlyPaymentCents);
                        if (loan.MissedPayments > 0) e.Alerts.Add("Mortgage: " + loan.MissedPayments + " missed payment" + (loan.MissedPayments == 1 ? "" : "s"));
                    }
                foreach (var policy in w.Insurance.All)
                    if (policy.Asset == id && policy.Holder == c.CharacterId && policy.Active)
                    {
                        e.Insured = true;
                        e.MonthlyInsurance += new Money(policy.MonthlyPremiumCents);
                    }

                if (p.Damage >= DamageState.Destroyed) e.Alerts.Insert(0, "Destroyed: rebuild or claim insurance");
                else if (p.Damage >= DamageState.Damaged) e.Alerts.Add("Damaged: repairs needed");
                if (p.TaxArrearsCents > 0) e.Alerts.Add("Property tax overdue: " + e.TaxArrears);
                if (e.TenantsBehindOnRent > 0) e.Alerts.Add(e.TenantsBehindOnRent + " tenant" + (e.TenantsBehindOnRent == 1 ? " is" : "s are") + " behind on rent");
                if (!e.Insured && p.Kind != PropertyKind.Land) e.Alerts.Add("Uninsured");
                if (p.Condition < 0.4f && p.Damage < DamageState.Damaged) e.Alerts.Add("Poor condition");

                result.Entries.Add(e);
                result.TotalValue += e.MarketValue;
                result.TotalOwed += e.MortgageOwed;
                result.MonthlyNet += e.MonthlyNet;
                result.AlertCount += e.Alerts.Count;
            }
            return result;
        }

        private static void Lease(PortfolioEntry e, Tenancy t)
        {
            e.OccupiedUnits++;
            e.MonthlyRentIncome += new Money(t.MonthlyRentCents);
            if (t.MissedPayments > 0) e.TenantsBehindOnRent++;
        }

        /// <summary>One line of the inventory screen: identical items stacked, unique ones separate.</summary>
        public sealed class InventoryLine
        {
            public string ItemId = "";
            public string Name = "";
            public string Category = "";
            public int Quantity;
            public Money UnitValue;
            public Money TotalValue => new Money(UnitValue.Cents * Quantity);
            public bool Stolen;
            public bool Illegal;
            /// <summary>Police would take it on arrest (stolen or contraband).</summary>
            public bool Confiscatable => Stolen || Illegal;
        }

        public static List<InventoryLine> InventoryOf(World.ContentSet content, ServerCharacter c)
        {
            var lines = new List<InventoryLine>();
            var byKey = new Dictionary<string, InventoryLine>(StringComparer.Ordinal);
            foreach (var s in c.Inventory)
            {
                if (s.Quantity <= 0) continue;
                // Clothes live in the wardrobe screen, not in the item list.
                if (s.ItemId.StartsWith(WardrobeRules.StackPrefix, StringComparison.Ordinal)) continue;
                var def = content.FindItem(s.ItemId);
                var key = s.Instance.IsValid ? s.ItemId + "#" + s.Instance : s.ItemId + (s.Stolen ? "#stolen" : "");
                if (!byKey.TryGetValue(key, out var line))
                {
                    line = new InventoryLine
                    {
                        ItemId = s.ItemId, Name = def != null ? def.DisplayName : s.ItemId, Category = def != null ? def.Category : "misc",
                        UnitValue = new Money(def != null ? def.ValueCents : 0), Stolen = s.Stolen, Illegal = def != null && !def.Legal,
                    };
                    byKey[key] = line;
                    lines.Add(line);
                }
                line.Quantity += s.Quantity;
            }
            lines.Sort((a, b) =>
            {
                var c1 = string.CompareOrdinal(a.Category, b.Category);
                return c1 != 0 ? c1 : string.CompareOrdinal(a.Name, b.Name);
            });
            return lines;
        }
    }
}
