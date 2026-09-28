using System;
using System.Collections.Generic;

namespace HeroGame.Core.Property
{
    using HeroGame.Core.Building;
    using HeroGame.Core.Foundation;

    public enum PropertyKind
    {
        Apartment,
        Condo,
        House,
        Mansion,
        Commercial,
        Industrial,
        Warehouse,
        Land,
    }

    public enum ZoningType
    {
        Residential,
        Commercial,
        MixedUse,
        Industrial,
        Agricultural,
    }

    public enum DamageState
    {
        Pristine,
        Worn,
        Damaged,
        HeavilyDamaged,
        Destroyed,
    }

    [Serializable]
    public sealed class Tenancy
    {
        public EntityId Tenant;
        public EntityId TenantAccount;
        public EntityId LandlordAccount;
        public long MonthlyRentCents;
        public long DepositCents;
        public long NextRentDueSecond;
        public long StartDay;
        public int MissedPayments;
        /// <summary>Day the rent last changed (lease start or a raise); rent stabilization allows one capped raise a year.</summary>
        public long LastRentChangeDay;
    }

    /// <summary>A rentable unit inside a multi-unit building (apartments).</summary>
    [Serializable]
    public sealed class RentalUnit
    {
        public int Id;
        public string Label = "";
        public int Bedrooms = 1;
        public float AreaSqm = 60f;
        public long MonthlyRentCents;
        /// <summary>Occupied by an NPC household (aggregate; not a ledger tenancy).</summary>
        public EntityId NpcHousehold;
        public Tenancy Tenancy;
        public bool Vacant => Tenancy == null && !NpcHousehold.IsValid;
    }

    /// <summary>
    /// Persistent real-estate record (GDD §20). Ownership is NOT stored here: the
    /// <see cref="Economy.OwnershipRegistry"/> is the single source of truth.
    /// </summary>
    [Serializable]
    public sealed class PropertyRecord
    {
        /// <summary>A member-wise copy (nested objects shared) for building save payloads on the owning thread.</summary>
        public PropertyRecord ShallowCopy() => (PropertyRecord)MemberwiseClone();

        public EntityId Id;
        public string Address = "";
        public PropertyKind Kind;
        public ZoningType Zoning;
        public EntityId District;
        public EntityId Place;
        public float FloorAreaSqm;
        public float LotAreaSqm;
        public int Bedrooms;
        public int Floors = 1;
        public long BaseValueCents;
        /// <summary>Current market value, re-assessed daily.</summary>
        public long MarketValueCents;
        public long ListingPriceCents;
        public bool ForSale;
        public bool ForRent;
        public long AskingRentCents;
        /// <summary>0..1 physical condition. Decays without maintenance; damaged by events.</summary>
        public float Condition = 1f;
        public DamageState Damage;
        public int SecurityLevel;
        public bool HasAlarm;
        public bool HasCameras;
        public bool HasSafe;
        public bool UtilitiesConnected = true;
        public Tenancy Tenancy;
        /// <summary>
        /// Walls, rooms, openings and furniture (the building system). Null until generated/edited. Copy-on-write: an edit
        /// replaces the whole layout with a new object and never mutates the current one, which lets saves detect changed
        /// layouts by reference and serialize them off the simulation thread.
        /// </summary>
        public BuildingLayout Layout;
        public List<RentalUnit> Units = new List<RentalUnit>();
        /// <summary>Unpaid property tax. Long arrears lead to a tax sale.</summary>
        public long TaxArrearsCents;
        public long ArrearsSinceDay;
        /// <summary>Outstanding cost to repair sudden damage (storms, fire, vandalism). Normal wear is not included.</summary>
        public long DamageRepairCents;
        /// <summary>Condition lost to that damage; restored by a repair.</summary>
        public float DamageConditionLoss;
        /// <summary>Portion of <see cref="DamageRepairCents"/> already settled by insurance (no double claims).</summary>
        public long InsuranceClaimedCents;

        public Money MarketValue => new Money(MarketValueCents);
    }
}
