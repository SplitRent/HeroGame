using System;
using System.Collections.Generic;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Property
{
    public enum RentalEventKind
    {
        RentPaid,
        RentMissed,
        Evicted,
        MovedOut,
        TaxPaid,
        TaxArrears,
        TaxSale,
    }

    public struct RentalEvent
    {
        public RentalEventKind Kind;
        public EntityId Property;
        public EntityId Party;
        public long AmountCents;
        public string Detail;
    }

    /// <summary>
    /// Renting (GDD §20): whole properties or apartment units, first month + one month deposit up front,
    /// monthly rent collected by the simulation (online or offline), eviction after two missed payments,
    /// deposit returned on a clean move-out. Also collects monthly property tax from player owners, with
    /// arrears leading to a tax sale after six months.
    /// </summary>
    public sealed class RentalService
    {
        public const long MonthSeconds = 30 * GameDateTime.SecondsPerDay;
        public const int MissedPaymentsBeforeEviction = 2;
        public const int TaxSaleAfterDays = 180;

        private readonly PropertyService _properties;
        private readonly TransactionProcessor _processor;
        private readonly OwnershipRegistry _ownership;
        private readonly TaxPolicy _taxes;

        public event Action<RentalEvent> Event;

        public RentalService(PropertyService properties, TransactionProcessor processor, OwnershipRegistry ownership, TaxPolicy taxes)
        {
            _properties = properties;
            _processor = processor;
            _ownership = ownership;
            _taxes = taxes;
        }

        public List<(PropertyRecord property, RentalUnit unit)> Vacancies()
        {
            var list = new List<(PropertyRecord, RentalUnit)>();
            foreach (var p in _properties.All)
            {
                if (p.Units.Count > 0)
                {
                    foreach (var u in p.Units) if (u.Vacant && u.MonthlyRentCents > 0) list.Add((p, u));
                }
                else if (p.ForRent && p.Tenancy == null)
                {
                    list.Add((p, null));
                }
            }
            list.Sort((a, b) => a.Item1.Id != b.Item1.Id ? a.Item1.Id.CompareTo(b.Item1.Id) : (a.Item2?.Id ?? 0).CompareTo(b.Item2?.Id ?? 0));
            return list;
        }

        public OpResult ListForRent(PropertyRecord property, EntityId owner, Money monthlyRent)
        {
            if (!_ownership.IsOwnedBy(property.Id, owner)) return OpResult.Fail("Only the owner can list a rental.");
            if (monthlyRent.Cents <= 0) return OpResult.Fail("Rent must be positive.");
            if (property.Tenancy != null) return OpResult.Fail("Already rented.");
            property.ForRent = true;
            property.AskingRentCents = monthlyRent.Cents;
            return OpResult.Ok();
        }

        /// <summary>Signs a lease: first month's rent + deposit are paid atomically to the landlord.</summary>
        public OpResult Rent(PropertyRecord property, int unitId, EntityId tenant, EntityId tenantAccount, EntityId landlordAccount, GameDateTime now, string idempotencyKey)
        {
            RentalUnit unit = null;
            long rent;
            if (unitId >= 0)
            {
                unit = property.Units.Find(u => u.Id == unitId);
                if (unit == null) return OpResult.Fail("No such unit.");
                if (!unit.Vacant) return OpResult.Fail(unit.Label + " is taken.");
                rent = unit.MonthlyRentCents;
            }
            else
            {
                if (!property.ForRent || property.Tenancy != null) return OpResult.Fail("Not available to rent.");
                rent = property.AskingRentCents;
            }
            if (_ownership.IsOwnedBy(property.Id, tenant)) return OpResult.Fail("You own this property.");
            var result = _processor.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = tenant,
                Timestamp = now,
                Description = "Lease signed: " + property.Address,
                Money = LedgerTransaction.Transfer(tenantAccount, landlordAccount, new Money(rent * 2), TransactionReason.Rent, "First month + deposit"),
            });
            if (!result.Success) return result;
            var tenancy = new Tenancy
            {
                Tenant = tenant,
                TenantAccount = tenantAccount,
                LandlordAccount = landlordAccount,
                MonthlyRentCents = rent,
                DepositCents = rent,
                NextRentDueSecond = now.TotalSeconds + MonthSeconds,
                StartDay = now.DayIndex,
            };
            if (unit != null) unit.Tenancy = tenancy;
            else
            {
                property.Tenancy = tenancy;
                property.ForRent = false;
            }
            return result;
        }

        public OpResult MoveOut(PropertyRecord property, int unitId, EntityId tenant, GameDateTime now)
        {
            var tenancy = unitId >= 0 ? property.Units.Find(u => u.Id == unitId)?.Tenancy : property.Tenancy;
            if (tenancy == null || tenancy.Tenant != tenant) return OpResult.Fail("You do not rent this.");
            // Deposit returned unless rent is owed or the place was damaged.
            var refund = property.Damage >= DamageState.Damaged || tenancy.MissedPayments > 0 ? 0 : tenancy.DepositCents;
            if (refund > 0)
            {
                var r = _processor.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = now,
                    Description = "Deposit returned",
                    Money = LedgerTransaction.Transfer(tenancy.LandlordAccount, tenancy.TenantAccount, new Money(refund), TransactionReason.Refund, "Deposit " + property.Address),
                });
                if (!r.Success) refund = 0; // landlord can't pay: it becomes a dispute (future: small-claims court)
            }
            Clear(property, unitId);
            Raise(RentalEventKind.MovedOut, property, tenant, refund, "Moved out of " + property.Address);
            return OpResult.Ok();
        }

        /// <summary>Daily: collect due rent from player tenants and monthly property tax from player owners.</summary>
        public void ProcessDay(GameDateTime now, Func<EntityId, EntityId> checkingAccountOfCharacter, EntityId treasuryAccount, bool taxDay)
        {
            foreach (var p in _properties.All)
            {
                if (p.Tenancy != null) Collect(p, -1, p.Tenancy, now);
                foreach (var u in p.Units) if (u.Tenancy != null) Collect(p, u.Id, u.Tenancy, now);
                if (taxDay) CollectPropertyTax(p, now, checkingAccountOfCharacter, treasuryAccount);
            }
        }

        private void Collect(PropertyRecord p, int unitId, Tenancy t, GameDateTime now)
        {
            while (t.NextRentDueSecond <= now.TotalSeconds)
            {
                var result = _processor.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = new GameDateTime(t.NextRentDueSecond),
                    Description = "Rent " + p.Address,
                    Money = LedgerTransaction.Transfer(t.TenantAccount, t.LandlordAccount, new Money(t.MonthlyRentCents), TransactionReason.Rent, "Rent " + p.Address),
                });
                t.NextRentDueSecond += MonthSeconds;
                if (result.Success)
                {
                    t.MissedPayments = 0;
                    Raise(RentalEventKind.RentPaid, p, t.Tenant, t.MonthlyRentCents, "Rent paid for " + p.Address);
                    continue;
                }
                t.MissedPayments++;
                Raise(RentalEventKind.RentMissed, p, t.Tenant, t.MonthlyRentCents, "Rent missed for " + p.Address + " (" + t.MissedPayments + ")");
                if (t.MissedPayments >= MissedPaymentsBeforeEviction)
                {
                    Clear(p, unitId);
                    Raise(RentalEventKind.Evicted, p, t.Tenant, 0, "Evicted from " + p.Address);
                    return;
                }
            }
        }

        private void CollectPropertyTax(PropertyRecord p, GameDateTime now, Func<EntityId, EntityId> accountOf, EntityId treasury)
        {
            var owner = _ownership.OwnerOf(p.Id);
            if (owner.Kind != EntityKind.Character) return; // NPC/institutional property tax is part of the aggregate economy
            var account = accountOf(owner);
            if (!account.IsValid) return;
            var due = _taxes.DailyPropertyTax(p.MarketValue).Cents * 30 + p.TaxArrearsCents;
            if (due <= 0) return;
            var result = _processor.Execute(new WorldTransaction
            {
                Source = TransactionSource.Simulation,
                Timestamp = now,
                Description = "Property tax " + p.Address,
                Money = LedgerTransaction.Transfer(account, treasury, new Money(due), TransactionReason.Tax, "Property tax " + p.Address),
            });
            if (result.Success)
            {
                p.TaxArrearsCents = 0;
                p.ArrearsSinceDay = 0;
                Raise(RentalEventKind.TaxPaid, p, owner, due, "Property tax paid for " + p.Address);
                return;
            }
            if (p.TaxArrearsCents == 0) p.ArrearsSinceDay = now.DayIndex;
            p.TaxArrearsCents = due;
            Raise(RentalEventKind.TaxArrears, p, owner, due, "Property tax overdue on " + p.Address);
            if (now.DayIndex - p.ArrearsSinceDay >= TaxSaleAfterDays)
            {
                // Tax sale: the city takes the title and lists it; arrears are written off against the sale.
                _processor.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = now,
                    Description = "Tax sale " + p.Address,
                    Ownership = { new OwnershipChange { Asset = p.Id, From = owner, To = EntityId.None } },
                });
                p.TaxArrearsCents = 0;
                p.ArrearsSinceDay = 0;
                p.ForSale = true;
                p.ListingPriceCents = p.MarketValueCents;
                Raise(RentalEventKind.TaxSale, p, owner, 0, p.Address + " was seized for unpaid property tax.");
            }
        }

        private static void Clear(PropertyRecord p, int unitId)
        {
            if (unitId >= 0)
            {
                var unit = p.Units.Find(u => u.Id == unitId);
                if (unit != null) unit.Tenancy = null;
            }
            else
            {
                p.Tenancy = null;
            }
        }

        private void Raise(RentalEventKind kind, PropertyRecord p, EntityId party, long amount, string detail)
        {
            Event?.Invoke(new RentalEvent { Kind = kind, Property = p.Id, Party = party, AmountCents = amount, Detail = detail });
        }
    }
}
