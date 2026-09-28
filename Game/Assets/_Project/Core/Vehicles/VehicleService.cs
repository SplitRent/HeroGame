using System;
using System.Collections.Generic;

namespace HeroGame.Core.Vehicles
{
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Time;

    /// <summary>
    /// Vehicle ownership economy (GDD §33–34): dealership sales, registration, fuel, wear, damage, repairs,
    /// customisation, garages, impound. Every payment is an atomic <see cref="WorldTransaction"/>; fuel and
    /// repairs pay the actual gas station / garage business, so player driving feeds the local economy.
    /// </summary>
    public sealed class VehicleService
    {
        public const int GarageCapacityHouse = 2;
        public const int GarageCapacityMansion = 6;
        public const int GarageCapacityCommercial = 8;
        public const long RegistrationDays = 365;

        private readonly Dictionary<string, VehicleModel> _models = new Dictionary<string, VehicleModel>();
        private readonly Dictionary<string, VehicleMod> _mods = new Dictionary<string, VehicleMod>();
        private readonly Dictionary<EntityId, VehicleRecord> _vehicles = new Dictionary<EntityId, VehicleRecord>();
        private readonly HashSet<string> _plates = new HashSet<string>();
        private readonly TransactionProcessor _processor;
        private readonly OwnershipRegistry _ownership;
        private readonly TaxPolicy _taxes;
        private readonly IdAllocator _ids;
        private readonly ulong _seed;

        public VehicleService(IEnumerable<VehicleModel> models, IEnumerable<VehicleMod> mods, TransactionProcessor processor,
            OwnershipRegistry ownership, TaxPolicy taxes, IdAllocator ids, ulong seed)
        {
            foreach (var m in models) _models[m.Id] = m;
            foreach (var m in mods) _mods[m.Id] = m;
            _processor = processor;
            _ownership = ownership;
            _taxes = taxes;
            _ids = ids;
            _seed = seed;
        }

        public IEnumerable<VehicleRecord> All => _vehicles.Values;
        public int Count => _vehicles.Count;
        public IEnumerable<VehicleModel> Models => _models.Values;

        public VehicleModel Model(string id) => id != null && _models.TryGetValue(id, out var m) ? m : null;
        public VehicleMod Mod(string id) => id != null && _mods.TryGetValue(id, out var m) ? m : null;
        public VehicleRecord Get(EntityId id) => _vehicles.TryGetValue(id, out var v) ? v : null;

        public void Restore(VehicleRecord v)
        {
            _vehicles[v.Id] = v;
            _plates.Add(v.Plate);
        }

        public List<VehicleRecord> OwnedBy(EntityId owner)
        {
            var list = new List<VehicleRecord>();
            foreach (var asset in _ownership.AssetsOf(owner))
                if (asset.Kind == EntityKind.Vehicle && _vehicles.TryGetValue(asset, out var v)) list.Add(v);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        /// <summary>Price including the server's price multiplier (difficulty/economy settings).</summary>
        public Money Price(VehicleModel model, float priceMultiplier) => new Money(model.BasePriceCents).Scale(priceMultiplier);

        /// <summary>
        /// Buys a new vehicle: price + sales tax + first-year registration in one atomic transaction that also
        /// creates ownership. The vehicle record is only kept if the transaction commits.
        /// </summary>
        public OpResult BuyNew(string modelId, EntityId buyer, EntityId buyerAccount, EntityId dealerAccount, EntityId treasuryAccount,
            GameDateTime now, string idempotencyKey, float priceMultiplier, string colorHex, out VehicleRecord vehicle)
        {
            vehicle = null;
            var model = Model(modelId);
            if (model == null) return OpResult.Fail("Unknown model " + modelId);
            if (!model.Civilian) return OpResult.Fail(model.DisplayName + " is not sold to civilians.");
            var price = Price(model, priceMultiplier);
            var tax = _taxes.SalesTax(price);
            var registration = _taxes.VehicleRegistrationFee;

            var id = _ids.Next(EntityKind.Vehicle);
            var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = "New " + model.DisplayName };
            money.Add(buyerAccount, -(price + tax + registration).Cents);
            money.Add(dealerAccount, price.Cents);
            money.Add(treasuryAccount, (tax + registration).Cents);
            var tx = new WorldTransaction { IdempotencyKey = idempotencyKey ?? "", Initiator = buyer, Timestamp = now, Description = "Vehicle purchase", Money = money };
            tx.Ownership.Add(new OwnershipChange { Asset = id, From = EntityId.None, To = buyer });
            var result = _processor.Execute(tx);
            if (!result.Success) return result;

            vehicle = new VehicleRecord
            {
                Id = id,
                ModelId = model.Id,
                Plate = NewPlate(id),
                ColorHex = string.IsNullOrEmpty(colorHex) ? "#8A8D91" : colorHex,
                FuelLitres = model.FuelCapacityLitres * 0.5f,
                RegisteredUntilDay = now.DayIndex + RegistrationDays,
                LocationKind = VehicleLocationKind.Dealership,
            };
            Restore(vehicle);
            return result;
        }

        /// <summary>Creates an unowned or institution-owned vehicle (service fleets, parked NPC cars, story vehicles).</summary>
        public VehicleRecord Spawn(string modelId, EntityId owner, WorldPosition position, float heading, GameDateTime now)
        {
            var model = Model(modelId) ?? throw new ArgumentException("Unknown model " + modelId);
            var v = new VehicleRecord
            {
                Id = _ids.Next(EntityKind.Vehicle),
                ModelId = model.Id,
                FuelLitres = model.FuelCapacityLitres,
                Position = position,
                Heading = heading,
                RegisteredUntilDay = now.DayIndex + RegistrationDays,
            };
            v.Plate = NewPlate(v.Id);
            Restore(v);
            if (owner.IsValid) _ownership.AssignInitial(v.Id, owner);
            return v;
        }

        public OpResult Refuel(VehicleRecord v, float litres, EntityId payerAccount, EntityId stationAccount, EntityId treasuryAccount,
            GameDateTime now, double priceLevel, EntityId payer)
        {
            var model = Model(v.ModelId);
            if (model == null) return OpResult.Fail("Unknown model.");
            litres = Math.Min(litres, model.FuelCapacityLitres - v.FuelLitres);
            if (litres <= 0.01f) return OpResult.Fail("Tank is full.");
            var cost = new Money((long)Math.Round(litres * 95 * priceLevel)); // ~$0.95/L at price level 1
            var tax = _taxes.SalesTax(cost);
            var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = "Fuel " + litres.ToString("0.0") + " L" };
            money.Add(payerAccount, -(cost + tax).Cents).Add(stationAccount, cost.Cents);
            if (tax.Cents != 0) money.Add(treasuryAccount, tax.Cents);
            var result = _processor.Execute(new WorldTransaction { Initiator = payer, Timestamp = now, Description = "Fuel", Money = money });
            if (result.Success) v.FuelLitres += litres;
            return result;
        }

        /// <summary>Wear and fuel for a driven distance. Returns false if the tank ran dry.</summary>
        public bool Drive(VehicleRecord v, float km, float averageSpeedKph)
        {
            var model = Model(v.ModelId);
            if (model == null || !v.Drivable) return false;
            // Aggressive speeds burn more fuel and wear tyres faster.
            var speedFactor = 1f + Math.Max(0f, averageSpeedKph - 90f) / 120f;
            var needed = km * model.LitresPer100Km / 100f * speedFactor;
            var ranDry = needed > v.FuelLitres;
            var driven = ranDry ? km * v.FuelLitres / Math.Max(0.0001f, needed) : km;
            v.FuelLitres = Math.Max(0f, v.FuelLitres - needed);
            v.OdometerKm += driven;
            v.TireHealth = Math.Max(0f, v.TireHealth - driven / 60000f * speedFactor);
            v.EngineHealth = Math.Max(0f, v.EngineHealth - driven / 400000f);
            return !ranDry;
        }

        /// <summary>Collision damage from an impulse (N·s) reported by physics; armor reduces it.</summary>
        public void ApplyCollision(VehicleRecord v, float impulse, bool frontal)
        {
            var model = Model(v.ModelId);
            var mass = model != null ? model.MassKg : 1400f;
            var durability = 1f;
            foreach (var m in v.Mods)
            {
                var mod = Mod(m.ModId);
                if (mod != null) durability *= mod.DurabilityMultiplier;
            }
            // Δv in m/s; below ~2 m/s is a scrape.
            var deltaV = impulse / mass;
            var severity = Math.Max(0f, deltaV - 2f) / 25f / durability;
            v.BodyHealth = Math.Max(0f, v.BodyHealth - severity);
            if (frontal) v.EngineHealth = Math.Max(0f, v.EngineHealth - severity * 0.7f);
            if (v.BodyHealth <= 0f && v.EngineHealth <= 0.05f) v.LocationKind = VehicleLocationKind.Destroyed;
        }

        public Money RepairQuote(VehicleRecord v, double priceLevel)
        {
            var model = Model(v.ModelId);
            var value = model != null ? model.BasePriceCents : 2500000;
            var damage = (1f - v.BodyHealth) * 0.25 + (1f - v.EngineHealth) * 0.35 + (1f - v.TireHealth) * 0.03;
            return new Money((long)(value * damage * priceLevel) + (damage > 0 ? 5000 : 0));
        }

        public OpResult Repair(VehicleRecord v, EntityId payer, EntityId payerAccount, EntityId garageAccount, EntityId treasuryAccount, GameDateTime now, double priceLevel)
        {
            if (v.LocationKind == VehicleLocationKind.Destroyed) return OpResult.Fail("Vehicle is written off.");
            var cost = RepairQuote(v, priceLevel);
            if (cost.Cents <= 0) return OpResult.Fail("Nothing to repair.");
            var tax = _taxes.SalesTax(cost);
            var money = new LedgerTransaction { Reason = TransactionReason.Maintenance, Memo = "Repair " + v.Plate };
            money.Add(payerAccount, -(cost + tax).Cents).Add(garageAccount, cost.Cents);
            if (tax.Cents != 0) money.Add(treasuryAccount, tax.Cents);
            var result = _processor.Execute(new WorldTransaction { Initiator = payer, Timestamp = now, Description = "Vehicle repair", Money = money });
            if (result.Success)
            {
                v.BodyHealth = 1f;
                v.EngineHealth = 1f;
                v.TireHealth = 1f;
                v.InsuranceClaimedCents = 0;
            }
            return result;
        }

        public OpResult InstallMod(VehicleRecord v, string modId, string colorHex, EntityId payer, EntityId payerAccount, EntityId shopAccount,
            EntityId treasuryAccount, GameDateTime now, bool hasRestrictedLicence)
        {
            if (!_ownership.IsOwnedBy(v.Id, payer)) return OpResult.Fail("You do not own this vehicle.");
            var mod = Mod(modId);
            var model = Model(v.ModelId);
            if (mod == null || model == null) return OpResult.Fail("Unknown part.");
            if (mod.Restricted && !hasRestrictedLicence) return OpResult.Fail(mod.DisplayName + " requires a licence.");
            if (mod.AllowedClasses.Count > 0 && !mod.AllowedClasses.Contains(model.Class.ToString())) return OpResult.Fail(mod.DisplayName + " does not fit a " + model.Class + ".");
            var price = new Money(mod.PriceCents);
            var tax = _taxes.SalesTax(price);
            var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = mod.DisplayName };
            money.Add(payerAccount, -(price + tax).Cents).Add(shopAccount, price.Cents);
            if (tax.Cents != 0) money.Add(treasuryAccount, tax.Cents);
            var result = _processor.Execute(new WorldTransaction { Initiator = payer, Timestamp = now, Description = "Vehicle modification", Money = money });
            if (!result.Success) return result;
            v.Mods.RemoveAll(m => m.Slot == mod.Slot);
            v.Mods.Add(new InstalledMod { Slot = mod.Slot, ModId = mod.Id, ColorHex = colorHex ?? "" });
            if (mod.Slot == ModSlot.Paint && !string.IsNullOrEmpty(colorHex)) v.ColorHex = colorHex;
            return result;
        }

        /// <summary>Combined stat multipliers from installed mods, read by the vehicle controller.</summary>
        public void Performance(VehicleRecord v, out float power, out float brakes, out float grip)
        {
            power = brakes = grip = 1f;
            foreach (var m in v.Mods)
            {
                var mod = Mod(m.ModId);
                if (mod == null) continue;
                power *= mod.PowerMultiplier;
                brakes *= mod.BrakeMultiplier;
                grip *= mod.GripMultiplier;
            }
            power *= 0.5f + 0.5f * v.EngineHealth;
            grip *= 0.6f + 0.4f * v.TireHealth;
        }

        public OpResult RenewRegistration(VehicleRecord v, EntityId payer, EntityId payerAccount, EntityId treasuryAccount, GameDateTime now)
        {
            var fee = _taxes.VehicleRegistrationFee;
            var result = _processor.Execute(new WorldTransaction
            {
                Initiator = payer,
                Timestamp = now,
                Description = "Registration " + v.Plate,
                Money = LedgerTransaction.Transfer(payerAccount, treasuryAccount, fee, TransactionReason.Fee, "Registration " + v.Plate),
            });
            if (result.Success) v.RegisteredUntilDay = Math.Max(v.RegisteredUntilDay, now.DayIndex) + RegistrationDays;
            return result;
        }

        public bool IsRegistered(VehicleRecord v, long day) => v.RegisteredUntilDay >= day;

        /// <summary>Park in a garage at a property the owner holds; capacity depends on the property kind.</summary>
        public OpResult StoreInGarage(VehicleRecord v, EntityId owner, PropertyRecord property)
        {
            if (!_ownership.IsOwnedBy(v.Id, owner)) return OpResult.Fail("Not your vehicle.");
            if (property == null || !_ownership.IsOwnedBy(property.Id, owner)) return OpResult.Fail("You can only use garages at properties you own.");
            var capacity = property.Kind == PropertyKind.Mansion ? GarageCapacityMansion
                : property.Kind == PropertyKind.House ? GarageCapacityHouse
                : property.Kind == PropertyKind.Commercial || property.Kind == PropertyKind.Warehouse || property.Kind == PropertyKind.Industrial ? GarageCapacityCommercial : 0;
            var used = 0;
            foreach (var other in _vehicles.Values)
                if (other.LocationKind == VehicleLocationKind.Garage && other.GarageProperty == property.Id && other.Id != v.Id) used++;
            if (used >= capacity) return OpResult.Fail(capacity == 0 ? "This property has no parking." : "Garage full (" + capacity + ").");
            v.LocationKind = VehicleLocationKind.Garage;
            v.GarageProperty = property.Id;
            return OpResult.Ok();
        }

        public void TakeOut(VehicleRecord v, WorldPosition position, float heading)
        {
            v.LocationKind = VehicleLocationKind.Street;
            v.GarageProperty = EntityId.None;
            v.Position = position;
            v.Heading = heading;
        }

        public void Impound(VehicleRecord v, long feeCents)
        {
            v.LocationKind = VehicleLocationKind.Impound;
            v.ImpoundFeeCents = feeCents;
        }

        public OpResult ReleaseFromImpound(VehicleRecord v, EntityId payer, EntityId payerAccount, EntityId treasuryAccount, GameDateTime now)
        {
            if (v.LocationKind != VehicleLocationKind.Impound) return OpResult.Fail("Not impounded.");
            if (!_ownership.IsOwnedBy(v.Id, payer)) return OpResult.Fail("Only the owner can release it.");
            var result = _processor.Execute(new WorldTransaction
            {
                Initiator = payer,
                Timestamp = now,
                Description = "Impound release",
                Money = LedgerTransaction.Transfer(payerAccount, treasuryAccount, new Money(v.ImpoundFeeCents), TransactionReason.Fee, "Impound " + v.Plate),
            });
            if (result.Success)
            {
                v.LocationKind = VehicleLocationKind.Street;
                v.ImpoundFeeCents = 0;
            }
            return result;
        }

        /// <summary>Unique fictional plate: three letters, four digits, derived from the id and world seed.</summary>
        private string NewPlate(EntityId id)
        {
            const string letters = "ABCDEFGHJKLMNPRSTUVWXYZ"; // no I/O/Q (readability)
            for (ulong attempt = 0; ; attempt++)
            {
                var h = StableHash.Combine(_seed, id.Value, attempt, 0x91A7E);
                var plate = "" + letters[(int)(h % 23)] + letters[(int)(h / 23 % 23)] + letters[(int)(h / 529 % 23)] + "-" + (h / 12167 % 10000).ToString("0000");
                if (_plates.Add(plate)) return plate;
            }
        }
    }
}
