using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.World;

namespace HeroGame.Core.Business
{
    /// <summary>Data-driven business type (GDD §18). Loaded from business_templates.json.</summary>
    [Serializable]
    public sealed class BusinessTemplate
    {
        public string Id = "";
        public string DisplayName = "";
        public PlaceKind PlaceKind = PlaceKind.Shop;
        /// <summary>Customers per open hour at reputation 0.5, normal foot traffic, fair prices.</summary>
        public float BaseCustomersPerHour = 12f;
        public long AverageTicketCents = 1500;
        /// <summary>Fraction of revenue spent on goods sold.</summary>
        public float CostOfGoodsRatio = 0.35f;
        /// <summary>Card fees, utilities, insurance, supplies and repairs as a fraction of revenue.</summary>
        public float OperatingExpenseRatio = 0.18f;
        public int StaffRequired = 3;
        public long HourlyWageCents = 1500;
        public int OpenMinute = 9 * 60;
        public int CloseMinute = 21 * 60;
        /// <summary>How strongly customers react to price changes (0 = inelastic).</summary>
        public float PriceElasticity = 1.2f;
        /// <summary>How much bad weather suppresses demand (0..1). Beach bars ≈ 0.9, pharmacies ≈ 0.1.</summary>
        public float WeatherSensitivity = 0.4f;
        public float NightlifeFactor = 0f;
        /// <summary>How much district pedestrian traffic matters (1 = walk-in retail, ~0.1 = B2B/logistics).</summary>
        public float FootTrafficSensitivity = 1f;
        public long DailyFixedCostCents = 8000;
        public long PurchasePriceCents = 25000000;
        /// <summary>Inventory units held per day of expected sales (0 for service businesses).</summary>
        public float InventoryDays = 5f;
        public List<string> Tags = new List<string>();
        /// <summary>Loot table (items.json LootTags) for shoplifting and burglary here.</summary>
        public string LootTag = "retail_general";
    }

    [Serializable]
    public sealed class BusinessDayReport
    {
        public long Day;
        public int Customers;
        public long RevenueCents;
        public long CostOfGoodsCents;
        public long WagesCents;
        public long FixedCostsCents;
        public long TaxesCents;
        public long ProfitCents;
        public bool WasClosed;
        /// <summary>Closed by an emergency order (insurable business interruption), not by the owner.</summary>
        public bool ForcedClosure;
        public string Note = "";
    }

    /// <summary>
    /// Persistent business state (GDD §19). Operates whether or not the owner is online; all money
    /// flows through its ledger account.
    /// </summary>
    [Serializable]
    public sealed class BusinessRecord
    {
        public const int ReportHistoryDays = 60;

        public EntityId Id;
        public string Name = "";
        public string TemplateId = "";
        public EntityId Place;
        public EntityId Property;
        public EntityId Account;
        /// <summary>1.0 = template default price; &gt;1 more expensive.</summary>
        public float PriceLevel = 1f;
        /// <summary>0..1 public reputation.</summary>
        public float Reputation = 0.5f;
        public int Staff;
        /// <summary>Daily marketing spend.</summary>
        public long AdvertisingCents;
        /// <summary>Inventory measured in "days of normal sales".</summary>
        public float InventoryDays = 5f;
        public bool AutoRestock = true;
        public bool Open = true;
        public bool IsFront;
        public long LifetimeRevenueCents;
        public long LifetimeProfitCents;
        public int ConsecutiveLossDays;
        public long LastSimulatedDay = long.MinValue;
        public List<BusinessDayReport> Reports = new List<BusinessDayReport>();
        public List<EntityId> Employees = new List<EntityId>();
        /// <summary>Pay relative to the template wage (0.8 .. 2.0). Better pay: fewer quits, better service.</summary>
        public float WageLevel = 1f;
        public bool ForSale;
        public long AskingPriceCents;
        /// <summary>Characters the owner has allowed to manage day-to-day operations.</summary>
        public List<EntityId> Managers = new List<EntityId>();
        /// <summary>Day the current owner took over (for reports and valuation).</summary>
        public long AcquiredDay;
    }
}
