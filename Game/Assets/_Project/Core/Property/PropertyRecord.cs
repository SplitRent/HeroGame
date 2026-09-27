using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Property
{
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
    public sealed class FurnitureItem
    {
        public string CatalogId = "";
        public float X, Y, Z, RotationY;
        public int Floor;
    }

    [Serializable]
    public sealed class Tenancy
    {
        public EntityId Tenant;
        public EntityId TenantAccount;
        public long MonthlyRentCents;
        public long NextRentDueSecond;
        public int MissedPayments;
    }

    /// <summary>
    /// Persistent real-estate record (GDD §20). Ownership is NOT stored here: the
    /// <see cref="Economy.OwnershipRegistry"/> is the single source of truth.
    /// </summary>
    [Serializable]
    public sealed class PropertyRecord
    {
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
        public List<FurnitureItem> Furniture = new List<FurnitureItem>();
        /// <summary>Id of a building-system layout (rooms/walls) if customised.</summary>
        public string LayoutId = "";

        public Money MarketValue => new Money(MarketValueCents);
    }
}
