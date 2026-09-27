using System;
using System.Collections.Generic;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;
using HeroGame.Core.World;

namespace HeroGame.Core.Property
{
    /// <summary>Property registry plus the gameplay operations on it (buy, sell, rent, valuation).</summary>
    public sealed class PropertyService
    {
        private readonly Dictionary<EntityId, PropertyRecord> _properties = new Dictionary<EntityId, PropertyRecord>();
        private readonly OwnershipRegistry _ownership;
        private readonly TransactionProcessor _processor;
        private readonly TaxPolicy _taxes;

        public PropertyService(OwnershipRegistry ownership, TransactionProcessor processor, TaxPolicy taxes)
        {
            _ownership = ownership;
            _processor = processor;
            _taxes = taxes;
        }

        public IEnumerable<PropertyRecord> All => _properties.Values;
        public int Count => _properties.Count;

        public void Add(PropertyRecord p) => _properties[p.Id] = p;
        public PropertyRecord Get(EntityId id) => _properties.TryGetValue(id, out var p) ? p : null;

        public EntityId OwnerOf(EntityId property) => _ownership.OwnerOf(property);

        /// <summary>
        /// Atomic purchase: buyer pays price + transfer tax, seller (or city, if unowned) receives the
        /// price, the city receives the tax, and ownership moves — all in one <see cref="WorldTransaction"/>.
        /// </summary>
        public OpResult Purchase(EntityId propertyId, EntityId buyer, EntityId buyerAccount, EntityId sellerAccount,
            EntityId treasuryAccount, GameDateTime now, string idempotencyKey, TransactionSource source = TransactionSource.Player)
        {
            var property = Get(propertyId);
            if (property == null) return OpResult.Fail("Unknown property " + propertyId);
            if (!property.ForSale) return OpResult.Fail("Property is not for sale.");
            if (property.Damage == DamageState.Destroyed) return OpResult.Fail("Property is destroyed; it must be rebuilt before sale.");

            var price = new Money(property.ListingPriceCents);
            var tax = _taxes.TransferTax(price);
            var seller = _ownership.OwnerOf(propertyId);

            var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = "Purchase of " + property.Address };
            money.Add(buyerAccount, -(price + tax).Cents);
            money.Add(sellerAccount, price.Cents);
            if (tax.Cents != 0) money.Add(treasuryAccount, tax.Cents);

            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Source = source,
                Initiator = buyer,
                Timestamp = now,
                Description = "Property purchase " + property.Address,
                Money = money,
            };
            tx.Ownership.Add(new OwnershipChange { Asset = propertyId, From = seller, To = buyer });

            var result = _processor.Execute(tx);
            if (result.Success)
            {
                property.ForSale = false;
                property.Tenancy = null;
            }
            return result;
        }

        public OpResult ListForSale(EntityId propertyId, EntityId owner, Money price)
        {
            var property = Get(propertyId);
            if (property == null) return OpResult.Fail("Unknown property.");
            if (_ownership.OwnerOf(propertyId) != owner) return OpResult.Fail("Only the owner can list a property.");
            if (price.Cents <= 0) return OpResult.Fail("Price must be positive.");
            property.ForSale = true;
            property.ListingPriceCents = price.Cents;
            return OpResult.Ok();
        }

        /// <summary>Daily market re-assessment from district wealth, condition and the macro cycle.</summary>
        public void Reassess(Geography geography, MacroEconomyState macro, float priceMultiplier)
        {
            foreach (var p in _properties.Values)
            {
                var district = geography.GetDistrict(p.District);
                var wealth = district != null ? district.Wealth : 0.5f;
                var locationFactor = 0.6 + wealth * 0.9;
                var conditionFactor = 0.55 + 0.45 * p.Condition;
                var damageFactor = p.Damage == DamageState.Destroyed ? 0.25 : p.Damage == DamageState.HeavilyDamaged ? 0.6 : 1.0;
                var value = p.BaseValueCents * locationFactor * conditionFactor * damageFactor * macro.PriceLevel
                            * (0.85 + 0.15 * macro.CycleIndex) * priceMultiplier;
                p.MarketValueCents = (long)Math.Round(value / 100.0) * 100;
                // Unowned stock is listed by the city at market value.
                if (!_ownership.OwnerOf(p.Id).IsValid)
                {
                    p.ForSale = true;
                    p.ListingPriceCents = p.MarketValueCents;
                }
            }
        }

        /// <summary>
        /// Sudden damage (storm, fire, vandalism): lowers condition and records the repair bill, which is what
        /// insurance settles against. Severity is the fraction of the structure's condition lost (0..1).
        /// </summary>
        /// <summary>Raised after any recorded damage (storms, fires, powers, vandalism, floods).</summary>
        public event Action<PropertyRecord> Damaged;

        public void ApplyDamage(PropertyRecord p, float severity)
        {
            severity = Math.Max(0f, Math.Min(p.Condition, severity));
            if (severity <= 0f) return;
            p.Condition -= severity;
            p.DamageConditionLoss += severity;
            // Structure is ~65 % of market value; repairs cost a premium over pro-rata value.
            p.DamageRepairCents += (long)Math.Round(Math.Max(p.MarketValueCents, p.BaseValueCents) * 0.65 * severity * 1.2);
            p.Damage = p.Condition <= 0.05f ? DamageState.Destroyed
                : p.Condition < 0.35f ? DamageState.HeavilyDamaged
                : p.Condition < 0.7f ? DamageState.Damaged
                : p.Damage == DamageState.Pristine ? DamageState.Worn : p.Damage;
            Damaged?.Invoke(p);
        }

        public Money RepairQuote(PropertyRecord p, double priceLevel) => new Money((long)Math.Round(p.DamageRepairCents * Math.Max(0.5, priceLevel)));

        /// <summary>Owner pays contractors to repair recorded damage; condition returns to its pre-damage level.</summary>
        public OpResult Repair(PropertyRecord p, EntityId owner, EntityId ownerAccount, EntityId contractorAccount, GameDateTime now, double priceLevel, string idempotencyKey)
        {
            if (!_ownership.IsOwnedBy(p.Id, owner)) return OpResult.Fail("Only the owner can order repairs.");
            var cost = RepairQuote(p, priceLevel);
            if (cost.Cents <= 0) return OpResult.Fail("Nothing to repair.");
            var result = _processor.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = owner,
                Timestamp = now,
                Description = "Repairs at " + p.Address,
                Money = LedgerTransaction.Transfer(ownerAccount, contractorAccount, cost, TransactionReason.Maintenance, "Repairs " + p.Address),
            });
            if (!result.Success) return result;
            MarkRepaired(p);
            return result;
        }

        /// <summary>Restores the structure after paid repairs or reconstruction.</summary>
        public void MarkRepaired(PropertyRecord p)
        {
            p.Condition = Math.Min(1f, Math.Max(p.Condition + p.DamageConditionLoss, p.Damage == DamageState.Destroyed ? 0.85f : 0f));
            p.DamageConditionLoss = 0f;
            p.DamageRepairCents = 0;
            p.InsuranceClaimedCents = 0;
            p.Damage = p.Condition >= 0.8f ? DamageState.Pristine : DamageState.Worn;
        }

        /// <summary>Physical wear: condition decays slowly unless maintained.</summary>
        public void ApplyDailyWear(float wearPerDay = 0.00015f)
        {
            foreach (var p in _properties.Values)
            {
                p.Condition = Math.Max(0f, p.Condition - wearPerDay);
                if (p.Damage == DamageState.Pristine && p.Condition < 0.8f) p.Damage = DamageState.Worn;
            }
        }
    }
}
