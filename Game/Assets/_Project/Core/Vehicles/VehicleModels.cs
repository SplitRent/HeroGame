using System;
using System.Collections.Generic;

namespace HeroGame.Core.Vehicles
{
    using HeroGame.Core.Foundation;

    public enum VehicleClass
    {
        Compact,
        Sedan,
        Suv,
        Pickup,
        Van,
        Sports,
        Muscle,
        Luxury,
        Motorcycle,
        Bus,
        Truck,
        Police,
        Ambulance,
        FireEngine,
        Taxi,
        Boat,
        Helicopter,
    }

    /// <summary>Data-driven vehicle model (vehicle_catalog.json). All makes and models are original.</summary>
    [Serializable]
    public sealed class VehicleModel
    {
        public string Id = "";
        public string Make = "";
        public string Model = "";
        public VehicleClass Class;
        public long BasePriceCents;
        public int Seats = 4;
        public float MassKg = 1400f;
        public float TopSpeedKph = 180f;
        public float ZeroTo100Seconds = 9f;
        public float EnginePowerKw = 110f;
        public float MaxSteerDegrees = 32f;
        public float BrakeTorque = 3000f;
        public float FuelCapacityLitres = 55f;
        public float LitresPer100Km = 8f;
        public string Drivetrain = "FWD";
        /// <summary>1 = average insurance/theft risk.</summary>
        public float RiskFactor = 1f;
        /// <summary>Only dealerships sell civilian models; service vehicles spawn for emergency services.</summary>
        public bool Civilian = true;
        public List<string> Tags = new List<string>();

        public string DisplayName => Make + " " + Model;
    }

    public enum ModSlot
    {
        Paint,
        Wheels,
        Tint,
        Engine,
        Brakes,
        Suspension,
        Armor,
        Lights,
        Plate,
        Livery,
        /// <summary>Alarm, immobiliser or GPS tracker (vehicle theft and recovery).</summary>
        Security,
    }

    /// <summary>Customisation part (vehicle_mods.json). Stat effects are multipliers applied by the vehicle controller.</summary>
    [Serializable]
    public sealed class VehicleMod
    {
        public string Id = "";
        public string DisplayName = "";
        public ModSlot Slot;
        public long PriceCents;
        public float PowerMultiplier = 1f;
        public float BrakeMultiplier = 1f;
        public float GripMultiplier = 1f;
        public float DurabilityMultiplier = 1f;
        /// <summary>Restricted parts (armor, police lights) require a licence or are illegal for civilians.</summary>
        public bool Restricted;
        public List<string> AllowedClasses = new List<string>();
    }

    [Serializable]
    public sealed class InstalledMod
    {
        public ModSlot Slot;
        public string ModId = "";
        public string ColorHex = "";
    }

    public enum VehicleLocationKind
    {
        Street,
        Garage,
        Impound,
        Dealership,
        Destroyed,
    }

    /// <summary>A persistent vehicle (GDD §33). Ownership lives in the OwnershipRegistry.</summary>
    [Serializable]
    public sealed class VehicleRecord
    {
        public EntityId Id;
        public string ModelId = "";
        public string Plate = "";
        public string ColorHex = "#8A8D91";
        public List<InstalledMod> Mods = new List<InstalledMod>();
        public float FuelLitres;
        public float OdometerKm;
        /// <summary>0..1 health per component.</summary>
        public float EngineHealth = 1f;
        public float BodyHealth = 1f;
        public float TireHealth = 1f;
        public VehicleLocationKind LocationKind = VehicleLocationKind.Street;
        public EntityId GarageProperty;
        public WorldPosition Position;
        public float Heading;
        public long RegisteredUntilDay;
        public bool ReportedStolen;
        public long ImpoundFeeCents;
        /// <summary>Repair cost already settled by insurance; cleared when the vehicle is repaired.</summary>
        public long InsuranceClaimedCents;
        public long StolenSinceDay = -1;
        /// <summary>Who is currently holding the stolen vehicle (thief); cleared on recovery.</summary>
        public EntityId StolenBy;

        public bool Drivable => EngineHealth > 0.05f && LocationKind != VehicleLocationKind.Destroyed && LocationKind != VehicleLocationKind.Impound;
        public float Condition => (EngineHealth + BodyHealth + TireHealth) / 3f;
    }
}
