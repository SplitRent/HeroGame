using System;
using System.Collections.Generic;

namespace HeroGame.Core.Business
{
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Time;

    /// <summary>Per-day inputs to a business simulation (built by the world from district, weather and macro state).</summary>
    public struct BusinessDayContext
    {
        public long Day;
        public DayOfWeek DayOfWeek;
        public float FootTraffic;
        /// <summary>Calendar events and conditions: demand × (1 + adjustment). 0 = an ordinary day.</summary>
        public float DemandAdjustment;
        /// <summary>0 = clear, 1 = severe weather all day.</summary>
        public float BadWeather;
        /// <summary>Hours without electricity today (storms, blackouts).</summary>
        public float PowerOutageHours;
        /// <summary>Emergency closure (hurricane evacuation, disaster).</summary>
        public bool ForcedClosure;
        public double CycleIndex;
        public double PriceLevel;
        public float RevenueMultiplier;
        public float WageMultiplier;
        public EntityId ExternalAccount;
        public EntityId TreasuryAccount;
        public GameDateTime Timestamp;
    }

    /// <summary>
    /// Aggregate business model (GDD §19): customers are a demand curve, not individual agents.
    /// Runs identically for online and offline owners, so a restaurant keeps trading while its
    /// owner is logged out, and a storm genuinely hurts its revenue.
    /// </summary>
    public sealed class BusinessSimulator
    {
        private readonly TransactionProcessor _processor;
        private readonly TaxPolicy _taxes;
        private readonly Dictionary<string, BusinessTemplate> _templates = new Dictionary<string, BusinessTemplate>();

        public BusinessSimulator(TransactionProcessor processor, TaxPolicy taxes, IEnumerable<BusinessTemplate> templates)
        {
            _processor = processor;
            _taxes = taxes;
            foreach (var t in templates) _templates[t.Id] = t;
        }

        public BusinessTemplate Template(string id) => id != null && _templates.TryGetValue(id, out var t) ? t : null;
        public IEnumerable<BusinessTemplate> Templates => _templates.Values;

        public static float OpenHours(BusinessTemplate t)
        {
            if (t.OpenMinute == t.CloseMinute) return 24f;
            var minutes = t.CloseMinute > t.OpenMinute ? t.CloseMinute - t.OpenMinute : 1440 - t.OpenMinute + t.CloseMinute;
            return minutes / 60f;
        }

        public BusinessDayReport SimulateDay(BusinessRecord b, BusinessDayContext ctx)
        {
            var t = Template(b.TemplateId);
            var report = new BusinessDayReport { Day = ctx.Day };
            if (t == null)
            {
                report.Note = "Unknown template " + b.TemplateId;
                return Finish(b, report);
            }

            var hours = OpenHours(t);
            if (!b.Open || ctx.ForcedClosure)
            {
                report.WasClosed = true;
                report.ForcedClosure = ctx.ForcedClosure;
                report.Note = ctx.ForcedClosure ? "Closed by emergency order." : "Closed by owner.";
                // Fixed costs still accrue while closed.
                report.FixedCostsCents = (long)(t.DailyFixedCostCents * ctx.PriceLevel);
                if (!Post(b, ctx, report))
                {
                    b.Reputation = Math.Max(0f, b.Reputation - 0.005f);
                    report.Note += " Could not cover fixed costs.";
                }
                return Finish(b, report);
            }

            hours = Math.Max(0f, hours - ctx.PowerOutageHours * 0.8f);

            var weekend = ctx.DayOfWeek == DayOfWeek.Friday || ctx.DayOfWeek == DayOfWeek.Saturday;
            var weekendFactor = weekend ? 1f + 0.25f + t.NightlifeFactor * 0.6f : ctx.DayOfWeek == DayOfWeek.Sunday ? 0.9f : 1f;
            var priceFactor = Math.Pow(Math.Max(0.2, b.PriceLevel), -t.PriceElasticity);
            var reputationFactor = 0.35 + b.Reputation * 1.3;
            var weatherFactor = 1.0 - t.WeatherSensitivity * ctx.BadWeather;
            var adFactor = 1.0 + Math.Min(0.35, Math.Sqrt(b.AdvertisingCents / 100.0) * 0.012);
            var footTraffic = 1f + (ctx.FootTraffic - 1f) * t.FootTrafficSensitivity;
            var demand = t.BaseCustomersPerHour * hours * footTraffic * reputationFactor * priceFactor * weatherFactor
                         * weekendFactor * adFactor * (0.6 + 0.4 * ctx.CycleIndex) * Math.Max(0.1, 1.0 + ctx.DemandAdjustment);

            // Capacity limits: staff and stock.
            var staffRatio = t.StaffRequired <= 0 ? 1.0 : Math.Min(1.0, b.Staff / (double)t.StaffRequired);
            var served = demand * staffRatio;
            var normalDaily = t.BaseCustomersPerHour * OpenHours(t);
            if (t.InventoryDays > 0)
            {
                var stockCustomers = b.InventoryDays * normalDaily;
                if (served > stockCustomers)
                {
                    served = stockCustomers;
                    report.Note = "Ran out of stock.";
                }
                b.InventoryDays = Math.Max(0f, b.InventoryDays - (float)(served / Math.Max(1.0, normalDaily)));
            }
            var unmet = Math.Max(0.0, demand - served);

            report.Customers = (int)Math.Round(served);
            var ticket = t.AverageTicketCents * b.PriceLevel * ctx.PriceLevel;
            report.RevenueCents = (long)(served * ticket * ctx.RevenueMultiplier);
            report.WagesCents = (long)(b.Staff * t.HourlyWageCents * OpenHours(t) * ctx.WageMultiplier * ctx.PriceLevel * Math.Max(0.5f, b.WageLevel));
            report.FixedCostsCents = (long)(t.DailyFixedCostCents * ctx.PriceLevel) + b.AdvertisingCents + (long)(report.RevenueCents * t.OperatingExpenseRatio);

            // Restocking: buy back to target level at wholesale cost.
            if (t.InventoryDays > 0 && b.AutoRestock)
            {
                var toBuy = Math.Max(0f, t.InventoryDays - b.InventoryDays);
                report.CostOfGoodsCents = (long)(toBuy * normalDaily * t.AverageTicketCents * t.CostOfGoodsRatio * ctx.PriceLevel);
                b.InventoryDays += toBuy;
            }
            else if (t.InventoryDays <= 0)
            {
                report.CostOfGoodsCents = (long)(report.RevenueCents * t.CostOfGoodsRatio / Math.Max(0.2f, b.PriceLevel));
            }

            var preTax = report.RevenueCents - report.CostOfGoodsCents - report.WagesCents - report.FixedCostsCents;
            report.TaxesCents = _taxes.BusinessTax(new Money(preTax)).Cents;

            var paid = Post(b, ctx, report);
            if (!paid)
            {
                // Could not meet obligations: staff quit, reputation suffers, restock is undone.
                b.Staff = Math.Max(0, b.Staff - 1);
                b.Reputation = Math.Max(0f, b.Reputation - 0.02f);
                report.Note = "Missed payroll; an employee quit.";
            }

            // Reputation drifts toward the quality customers perceive.
            var perceived = 0.5 + (1.0 - b.PriceLevel) * 0.35 + (staffRatio - 1.0) * 0.5 - (demand > 0 ? unmet / demand : 0) * 0.3
                            + (Math.Max(0.5f, b.WageLevel) - 1.0) * 0.15;
            b.Reputation = (float)Math.Max(0, Math.Min(1, b.Reputation + (perceived - b.Reputation) * 0.01));
            return Finish(b, report);
        }

        /// <summary>Posts the day's money movements. Returns false if the business could not pay.</summary>
        private bool Post(BusinessRecord b, BusinessDayContext ctx, BusinessDayReport r)
        {
            var costs = r.CostOfGoodsCents + r.WagesCents + r.FixedCostsCents;
            var money = new LedgerTransaction { Reason = TransactionReason.BusinessRevenue, Memo = b.Name + " day " + ctx.Day };
            var net = r.RevenueCents - costs - r.TaxesCents;
            money.Add(b.Account, net);
            money.Add(ctx.ExternalAccount, -(r.RevenueCents - costs));
            if (r.TaxesCents != 0) money.Add(ctx.TreasuryAccount, r.TaxesCents);
            var tx = new WorldTransaction { Source = TransactionSource.Simulation, Timestamp = ctx.Timestamp, Description = "Business day", Money = money };
            var result = _processor.Execute(tx);
            if (result.Success)
            {
                r.ProfitCents = net;
                return true;
            }

            // Fallback: book revenue only, nothing else could be paid.
            r.ProfitCents = 0;
            if (r.RevenueCents > 0)
            {
                var revenueOnly = new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = ctx.Timestamp,
                    Description = "Business revenue (unpaid costs)",
                    Money = LedgerTransaction.Transfer(ctx.ExternalAccount, b.Account, new Money(r.RevenueCents), TransactionReason.BusinessRevenue),
                };
                if (_processor.Execute(revenueOnly).Success) r.ProfitCents = r.RevenueCents;
            }
            r.CostOfGoodsCents = 0;
            r.WagesCents = 0;
            r.TaxesCents = 0;
            return false;
        }

        private static BusinessDayReport Finish(BusinessRecord b, BusinessDayReport r)
        {
            b.LifetimeRevenueCents += r.RevenueCents;
            b.LifetimeProfitCents += r.ProfitCents;
            b.ConsecutiveLossDays = r.ProfitCents < 0 ? b.ConsecutiveLossDays + 1 : 0;
            b.Reports.Add(r);
            if (b.Reports.Count > BusinessRecord.ReportHistoryDays) b.Reports.RemoveAt(0);
            b.LastSimulatedDay = r.Day;
            return r;
        }
    }
}
