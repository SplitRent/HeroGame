using System;
using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;
using HeroGame.Core.Time;
using HeroGame.Core.Vehicles;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// Player banking and insurance (GDD §26–27): savings accounts, underwritten loans and mortgages, early
    /// repayment, policies, premiums and claims. Every operation is a single <see cref="WorldTransaction"/>; the
    /// records it creates (loan, policy, account) travel in that transaction so they survive a crash with it.
    /// </summary>
    public sealed class FinanceService
    {
        public const string BankName = "Gulf Tidewater Bank";
        public const string InsurerName = "Harborline Mutual";

        private readonly World _w;

        public FinanceService(World world)
        {
            _w = world;
        }

        // ------------------------------------------------------------------ accounts

        /// <summary>The character's savings account, found by owner so it is never lost with a stale reference.</summary>
        public EntityId SavingsOf(ServerCharacter c)
        {
            if (c.SavingsAccount.IsValid && _w.Ledger.Exists(c.SavingsAccount)) return c.SavingsAccount;
            EntityId found = EntityId.None;
            foreach (var a in _w.Ledger.Accounts)
                if (a.Owner == c.CharacterId && a.Kind == LedgerAccountKind.PersonalSavings && (!found.IsValid || a.Id.CompareTo(found) < 0)) found = a.Id;
            c.SavingsAccount = found;
            return found;
        }

        public OpResult OpenSavings(ServerCharacter c, string idempotencyKey)
        {
            if (SavingsOf(c).IsValid) return OpResult.Fail("You already have a savings account.");
            var id = _w.Ids.Next(EntityKind.LedgerAccount);
            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Open savings account",
                OpenAccounts = { new LedgerAccount { Id = id, Owner = c.CharacterId, Kind = LedgerAccountKind.PersonalSavings, Label = BankName + " savings" } },
            };
            var result = _w.Transactions.Execute(tx);
            if (result.Success) c.SavingsAccount = id;
            return result;
        }

        /// <summary>Moves money between accounts the character owns (checking ↔ savings).</summary>
        public OpResult TransferOwn(ServerCharacter c, EntityId from, EntityId to, Money amount, string idempotencyKey)
        {
            if (amount.Cents <= 0) return OpResult.Fail("Amount must be positive.");
            if (!OwnsAccount(c, from) || !OwnsAccount(c, to) || from == to) return OpResult.Fail("You can only move money between your own accounts.");
            return _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Transfer between own accounts",
                Money = LedgerTransaction.Transfer(from, to, amount, TransactionReason.PlayerTransfer),
            });
        }

        private bool OwnsAccount(ServerCharacter c, EntityId account)
        {
            if (account == c.CheckingAccount) return true;
            return _w.Ledger.TryGet(account, out var a) && a.Owner == c.CharacterId;
        }

        public Money LiquidAssets(ServerCharacter c)
        {
            var total = _w.Ledger.BalanceOf(c.CheckingAccount);
            var savings = SavingsOf(c);
            if (savings.IsValid) total += _w.Ledger.BalanceOf(savings);
            return total;
        }

        /// <summary>Income a bank can verify: owned businesses' recent profits and rent received from tenants.</summary>
        public Money VerifiedMonthlyIncome(ServerCharacter c)
        {
            long monthly = 0;
            foreach (var b in _w.BusinessOps.OwnedBy(c.CharacterId))
                monthly += Math.Max(0, _w.BusinessOps.AverageDailyProfit(b, 30)) * 30;
            foreach (var p in _w.Properties.All)
            {
                if (p.Tenancy != null && p.Tenancy.LandlordAccount == c.CheckingAccount) monthly += p.Tenancy.MonthlyRentCents;
                foreach (var u in p.Units)
                    if (u.Tenancy != null && u.Tenancy.LandlordAccount == c.CheckingAccount) monthly += u.Tenancy.MonthlyRentCents;
            }
            return new Money(monthly);
        }

        public CreditProfile Credit(ServerCharacter c)
        {
            var profile = Underwriter.Profile(c.CharacterId, _w.Loans, _w.Today - c.CreatedDay);
            // Medical debt in collections is reported to the bureau.
            if (c.MedicalDebtCents > 0) profile.Score = Math.Max(300, profile.Score - 60);
            return profile;
        }

        /// <summary>Pays down outstanding medical debt to the hospital.</summary>
        public OpResult PayMedicalDebt(ServerCharacter c, Money amount, string idempotencyKey)
        {
            var pay = Math.Min(amount.Cents, c.MedicalDebtCents);
            if (pay <= 0) return OpResult.Fail("You have no medical debt.");
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Medical debt payment",
                Money = LedgerTransaction.Transfer(c.CheckingAccount, _w.Accounts.HospitalAccount, new Money(pay), TransactionReason.Purchase, "Medical debt"),
            });
            if (result.Success)
            {
                c.MedicalDebtCents -= pay;
                _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
            }
            return result;
        }

        // ------------------------------------------------------------------ loans

        /// <summary>Appraised value of an asset used as collateral (owned property, vehicle or business).</summary>
        public Money AppraisedValue(EntityId asset)
        {
            switch (asset.Kind)
            {
                case EntityKind.Property:
                    var p = _w.Properties.Get(asset);
                    return p != null ? new Money(p.MarketValueCents) : Money.Zero;
                case EntityKind.Vehicle:
                    var v = _w.Vehicles.Get(asset);
                    return v != null ? VehicleValue(v) : Money.Zero;
                case EntityKind.Business:
                    return _w.Businesses.TryGetValue(asset, out var b) ? _w.BusinessOps.Valuation(b) : Money.Zero;
                default:
                    return Money.Zero;
            }
        }

        public Money VehicleValue(VehicleRecord v)
        {
            var model = _w.Vehicles.Model(v.ModelId);
            if (model == null || v.LocationKind == VehicleLocationKind.Destroyed) return Money.Zero;
            var mileage = Math.Max(0.3, 1.0 - v.OdometerKm / 250000.0);
            return new Money((long)(model.BasePriceCents * _w.Macro.PriceLevel * mileage * (0.5 + 0.5 * v.Condition)));
        }

        private bool IsPledged(EntityId asset)
        {
            foreach (var l in _w.Loans.Loans)
                if (l.Collateral == asset && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Delinquent)) return true;
            return false;
        }

        public LoanOffer QuoteLoan(ServerCharacter c, LoanKind kind, Money principal, int termMonths, EntityId collateral)
        {
            long collateralValue = 0;
            if (kind != LoanKind.Personal)
            {
                if (!collateral.IsValid || !_w.Ownership.IsOwnedBy(collateral, c.CharacterId))
                    return new LoanOffer { Reason = "Collateral must be an asset you own.", CreditScore = Credit(c).Score };
                if (IsPledged(collateral)) return new LoanOffer { Reason = "That asset already secures another loan.", CreditScore = Credit(c).Score };
                collateralValue = AppraisedValue(collateral).Cents;
            }
            return Underwriter.Quote(new LoanApplication
            {
                Kind = kind,
                Borrower = c.CharacterId,
                PrincipalCents = principal.Cents,
                TermMonths = termMonths,
                CollateralValueCents = collateralValue,
                MonthlyIncomeCents = VerifiedMonthlyIncome(c).Cents,
                LiquidAssetsCents = LiquidAssets(c).Cents,
            }, Credit(c), _w.Macro);
        }

        public OpResult TakeLoan(ServerCharacter c, LoanKind kind, Money principal, int termMonths, EntityId collateral, string idempotencyKey)
        {
            if (kind == LoanKind.Mortgage) return OpResult.Fail("Mortgages are arranged when buying a property.");
            var offer = QuoteLoan(c, kind, principal, termMonths, collateral);
            if (!offer.Approved) return OpResult.Fail(offer.Reason);
            var loan = LoanBook.Create(_w.Ids.Next(EntityKind.Loan), kind, c.CharacterId, c.CheckingAccount, _w.Accounts.BankReserves, principal,
                offer.AnnualRate, termMonths, _w.Clock.Now, kind == LoanKind.Personal ? EntityId.None : collateral);
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = kind + " loan",
                Money = LedgerTransaction.Transfer(_w.Accounts.BankReserves, c.CheckingAccount, principal, TransactionReason.LoanDisbursement, kind + " loan " + loan.Id),
                Records = new TransactionRecords { Loan = loan },
            });
            if (result.Success)
                _w.Phone.Send(c, _w.Accounts.BankOrganization, BankName, PhoneCategory.Bank,
                    kind + " loan of " + principal + " approved at " + (offer.AnnualRate * 100).ToString("0.00") + "% — " + new Money(loan.MonthlyPaymentCents) + "/month for " + termMonths + " months.");
            return result;
        }

        /// <summary>Mortgage terms for buying <paramref name="property"/> with <paramref name="downPayment"/> down.</summary>
        public LoanOffer QuoteMortgage(ServerCharacter c, PropertyRecord property, Money downPayment, int termMonths)
        {
            if (property == null || !property.ForSale) return new LoanOffer { Reason = "That property is not for sale." };
            var price = property.ListingPriceCents;
            var tax = _w.Taxes.TransferTax(new Money(price)).Cents;
            var principal = price - downPayment.Cents;
            if (principal <= 0) return new LoanOffer { Reason = "Your down payment covers the price; no mortgage needed." };
            var liquid = LiquidAssets(c).Cents - downPayment.Cents - tax;
            if (liquid < 0) return new LoanOffer { Reason = "You need " + new Money(downPayment.Cents + tax) + " for the down payment and transfer tax." };
            return Underwriter.Quote(new LoanApplication
            {
                Kind = LoanKind.Mortgage,
                Borrower = c.CharacterId,
                PrincipalCents = principal,
                TermMonths = termMonths,
                // Banks lend against the lower of price and appraisal.
                CollateralValueCents = Math.Min(price, Math.Max(property.MarketValueCents, 1)),
                MonthlyIncomeCents = VerifiedMonthlyIncome(c).Cents,
                LiquidAssetsCents = liquid,
            }, Credit(c), _w.Macro);
        }

        /// <summary>Atomic mortgage purchase: bank funds + down payment → seller, tax → city, deed → buyer, loan recorded.</summary>
        public OpResult BuyWithMortgage(ServerCharacter c, PropertyRecord property, Money downPayment, int termMonths, string idempotencyKey)
        {
            var offer = QuoteMortgage(c, property, downPayment, termMonths);
            if (!offer.Approved) return OpResult.Fail(offer.Reason);
            var price = new Money(property.ListingPriceCents);
            var tax = _w.Taxes.TransferTax(price);
            var seller = _w.Ownership.OwnerOf(property.Id);
            var sellerAccount = _w.AccountFor(seller);
            var principal = new Money(offer.PrincipalCents);
            var loan = LoanBook.Create(_w.Ids.Next(EntityKind.Loan), LoanKind.Mortgage, c.CharacterId, c.CheckingAccount, _w.Accounts.BankReserves,
                principal, offer.AnnualRate, termMonths, _w.Clock.Now, property.Id);

            var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = "Mortgage purchase of " + property.Address };
            money.Add(c.CheckingAccount, -(downPayment + tax).Cents);
            money.Add(_w.Accounts.BankReserves, -principal.Cents);
            money.Add(sellerAccount, price.Cents);
            if (tax.Cents != 0) money.Add(_w.Accounts.Treasury, tax.Cents);
            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Mortgage purchase " + property.Address,
                Money = money,
                Records = new TransactionRecords { Loan = loan },
            };
            tx.Ownership.Add(new OwnershipChange { Asset = property.Id, From = seller, To = c.CharacterId });
            var result = _w.Transactions.Execute(tx);
            if (!result.Success) return result;
            property.ForSale = false;
            property.Tenancy = null;
            _w.Phone.Send(c, _w.Accounts.BankOrganization, BankName, PhoneCategory.Bank,
                "Congratulations on " + property.Address + ". Your mortgage: " + principal + " at " + (offer.AnnualRate * 100).ToString("0.00") + "%, "
                + new Money(loan.MonthlyPaymentCents) + "/month. Miss three payments and the bank takes the property.");
            return result;
        }

        /// <summary>Early (partial or full) repayment. There is no prepayment penalty.</summary>
        public OpResult Repay(ServerCharacter c, Loan loan, Money amount, string idempotencyKey)
        {
            if (loan == null || loan.Borrower != c.CharacterId) return OpResult.Fail("Not your loan.");
            if (loan.Status == LoanStatus.PaidOff || loan.Status == LoanStatus.Defaulted) return OpResult.Fail("This loan is closed.");
            var pay = Math.Min(amount.Cents, loan.OutstandingCents);
            if (pay <= 0) return OpResult.Fail("Amount must be positive.");
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Early repayment " + loan.Id,
                Money = LedgerTransaction.Transfer(c.CheckingAccount, loan.LenderAccount, new Money(pay), TransactionReason.LoanRepayment, "Prepayment " + loan.Id),
            });
            if (!result.Success) return result;
            loan.OutstandingCents -= pay;
            if (loan.OutstandingCents <= 0)
            {
                loan.Status = LoanStatus.PaidOff;
                _w.Phone.Send(c, _w.Accounts.BankOrganization, BankName, PhoneCategory.Bank, "Loan " + loan.Id + " is paid in full. Thank you.");
            }
            else
            {
                // Same payment, shorter remaining schedule.
                loan.MonthlyPaymentCents = Math.Min(loan.MonthlyPaymentCents, Loan.ComputeMonthlyPayment(loan.OutstandingCents, loan.AnnualRate, Math.Max(1, loan.TermMonths)));
            }
            _w.Dirty.Mark(SaveChunks.Transactional);
            return result;
        }

        // ------------------------------------------------------------------ insurance

        public InsuranceQuote QuoteInsurance(ServerCharacter c, InsuranceKind kind, EntityId asset, Money deductible)
        {
            var q = new InsuranceQuote { Kind = kind, Asset = asset, DeductibleCents = Math.Max(0, deductible.Cents) };
            var claims = _w.Insurance.ClaimsBy(c.CharacterId);
            switch (kind)
            {
                case InsuranceKind.Property:
                {
                    var p = _w.Properties.Get(asset);
                    if (p == null || !_w.Ownership.IsOwnedBy(asset, c.CharacterId)) return Refuse(q, "You can only insure property you own.");
                    if (p.DamageRepairCents > 0) return Refuse(q, "Existing damage must be repaired before cover can start.");
                    var district = _w.Geography.GetDistrict(p.District);
                    q.CoverageCents = Math.Max(p.MarketValueCents, p.BaseValueCents);
                    q.MonthlyPremiumCents = InsurancePricing.PropertyMonthly(q.CoverageCents, q.DeductibleCents, district != null ? district.FloodRisk : 0.2f,
                        p.SecurityLevel, p.HasAlarm, p.HasCameras, claims);
                    break;
                }
                case InsuranceKind.Vehicle:
                {
                    var v = _w.Vehicles.Get(asset);
                    if (v == null || !_w.Ownership.IsOwnedBy(asset, c.CharacterId)) return Refuse(q, "You can only insure a vehicle you own.");
                    if (v.ReportedStolen) return Refuse(q, "A stolen vehicle cannot be insured.");
                    if (_w.Vehicles.RepairQuote(v, _w.Macro.PriceLevel).Cents > 0 && v.Condition < 0.9f) return Refuse(q, "Repair the vehicle before insuring it.");
                    q.CoverageCents = VehicleValue(v).Cents;
                    q.MonthlyPremiumCents = InsurancePricing.VehicleMonthly(q.CoverageCents, q.DeductibleCents, claims, (c.Record.Convictions + c.Record.Charges.Count) * 0.1f);
                    break;
                }
                case InsuranceKind.BusinessInterruption:
                {
                    if (!_w.Businesses.TryGetValue(asset, out var b) || !_w.Ownership.IsOwnedBy(asset, c.CharacterId)) return Refuse(q, "You can only insure a business you own.");
                    var t = _w.BusinessSim.Template(b.TemplateId);
                    var place = _w.Geography.GetPlace(b.Place);
                    var district = place != null ? _w.Geography.GetDistrict(place.District) : null;
                    var daily = Math.Max(0, _w.BusinessOps.AverageDailyProfit(b, 30)) + (t != null ? (long)(t.DailyFixedCostCents * _w.Macro.PriceLevel) : 0);
                    q.CoverageCents = daily * 30;
                    q.MonthlyPremiumCents = InsurancePricing.InterruptionMonthly(daily - (t != null ? t.DailyFixedCostCents : 0), t != null ? t.DailyFixedCostCents : 0,
                        district != null ? district.FloodRisk : 0.2f);
                    break;
                }
                case InsuranceKind.Health:
                    if (_w.Insurance.ActiveHealth(c.CharacterId) != null) return Refuse(q, "You already have health cover.");
                    q.CoverageCents = InsurancePricing.HealthCoverageCents;
                    q.DeductibleCents = InsurancePricing.HealthDeductibleCents;
                    q.MonthlyPremiumCents = InsurancePricing.HealthMonthly(claims);
                    break;
            }
            if (kind != InsuranceKind.Health && _w.Insurance.ActiveFor(asset, kind) != null) return Refuse(q, "This is already insured.");
            if (q.CoverageCents <= 0) return Refuse(q, "Nothing to insure yet.");
            q.Available = true;
            return q;
        }

        private static InsuranceQuote Refuse(InsuranceQuote q, string reason)
        {
            q.Available = false;
            q.Reason = reason;
            return q;
        }

        /// <summary>Starts a policy; the first month's premium is paid now in the same transaction.</summary>
        public OpResult BuyInsurance(ServerCharacter c, InsuranceKind kind, EntityId asset, Money deductible, string idempotencyKey)
        {
            var q = QuoteInsurance(c, kind, asset, deductible);
            if (!q.Available) return OpResult.Fail(q.Reason);
            var payer = kind == InsuranceKind.BusinessInterruption ? _w.Businesses[asset].Account : c.CheckingAccount;
            var policy = new InsurancePolicy
            {
                Id = _w.Ids.Next(EntityKind.InsurancePolicy),
                Kind = kind,
                Holder = c.CharacterId,
                HolderAccount = payer,
                Asset = kind == InsuranceKind.Health ? EntityId.None : asset,
                CoverageCents = q.CoverageCents,
                DeductibleCents = q.DeductibleCents,
                MonthlyPremiumCents = q.MonthlyPremiumCents,
                StartDay = _w.Today,
                NextPremiumDay = _w.Today + 30,
                Status = PolicyStatus.Active,
            };
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = kind + " insurance",
                Money = LedgerTransaction.Transfer(payer, _w.Accounts.InsurerAccount, new Money(q.MonthlyPremiumCents), TransactionReason.Premium, kind + " policy " + policy.Id),
                Records = new TransactionRecords { Policy = policy },
            });
            if (result.Success)
                _w.Phone.Send(c, _w.Accounts.Insurer, InsurerName, PhoneCategory.Bank,
                    kind + " cover is active: " + new Money(policy.CoverageCents) + " cover, " + new Money(policy.DeductibleCents) + " deductible, "
                    + new Money(policy.MonthlyPremiumCents) + "/month. Claims open after " + InsurancePricing.WaitingPeriodDays + " days.");
            return result;
        }

        public OpResult CancelInsurance(ServerCharacter c, InsurancePolicy policy)
        {
            if (policy == null || policy.Holder != c.CharacterId) return OpResult.Fail("Not your policy.");
            if (!policy.Active) return OpResult.Fail("The policy is not active.");
            policy.Status = PolicyStatus.Cancelled;
            _w.Dirty.Mark(SaveChunks.Transactional);
            return OpResult.Ok();
        }

        /// <summary>The loss the insurer recognises right now for a policy (computed from recorded damage, never self-reported).</summary>
        public Money AssessedLoss(InsurancePolicy policy)
        {
            switch (policy.Kind)
            {
                case InsuranceKind.Property:
                {
                    var p = _w.Properties.Get(policy.Asset);
                    return p == null ? Money.Zero : new Money(Math.Max(0, p.DamageRepairCents - p.InsuranceClaimedCents));
                }
                case InsuranceKind.Vehicle:
                {
                    var v = _w.Vehicles.Get(policy.Asset);
                    if (v == null) return Money.Zero;
                    if (v.ReportedStolen && v.StolenSinceDay >= 0 && _w.Today - v.StolenSinceDay >= 7) return VehicleValueForTheft(v);
                    return new Money(Math.Max(0, _w.Vehicles.RepairQuote(v, _w.Macro.PriceLevel).Cents - v.InsuranceClaimedCents));
                }
                default:
                    return Money.Zero;
            }
        }

        private Money VehicleValueForTheft(VehicleRecord v)
        {
            var model = _w.Vehicles.Model(v.ModelId);
            if (model == null) return Money.Zero;
            var mileage = Math.Max(0.3, 1.0 - v.OdometerKm / 250000.0);
            return new Money((long)(model.BasePriceCents * _w.Macro.PriceLevel * mileage));
        }

        /// <summary>Files a property or vehicle claim. Theft (unrecovered for 7 days) is a total loss: the insurer pays and takes title.</summary>
        public OpResult Claim(ServerCharacter c, InsurancePolicy policy, string idempotencyKey)
        {
            if (policy == null || policy.Holder != c.CharacterId) return OpResult.Fail("Not your policy.");
            if (!policy.Active) return OpResult.Fail("The policy is not active.");
            if (policy.Kind != InsuranceKind.Property && policy.Kind != InsuranceKind.Vehicle)
                return OpResult.Fail(policy.Kind == InsuranceKind.Health ? "Medical bills are settled with the hospital directly." : "Interruption claims are paid automatically.");
            if (_w.Today - policy.StartDay < InsurancePricing.WaitingPeriodDays) return OpResult.Fail("Claims are not accepted during the first " + InsurancePricing.WaitingPeriodDays + " days of cover.");
            if (!_w.Ownership.IsOwnedBy(policy.Asset, c.CharacterId)) return OpResult.Fail("You no longer own the insured asset.");
            if (FraudSuspected != null && FraudSuspected(policy)) return OpResult.Fail("Claim refused: the loss is under investigation.");

            var loss = AssessedLoss(policy);
            var theft = policy.Kind == InsuranceKind.Vehicle && _w.Vehicles.Get(policy.Asset) is VehicleRecord sv && sv.ReportedStolen && sv.StolenSinceDay >= 0 && _w.Today - sv.StolenSinceDay >= 7;
            if (loss.Cents <= 0) return OpResult.Fail(policy.Kind == InsuranceKind.Vehicle && _w.Vehicles.Get(policy.Asset)?.ReportedStolen == true
                ? "Stolen vehicles are paid out after 7 days if not recovered." : "There is no recorded damage to claim.");
            var payout = Math.Min(policy.RemainingCoverageCents, loss.Cents - policy.DeductibleCents);
            if (payout <= 0) return OpResult.Fail("The loss (" + loss + ") is within your deductible (" + new Money(policy.DeductibleCents) + ").");

            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Insurance claim " + policy.Id,
                Money = LedgerTransaction.Transfer(_w.Accounts.InsurerAccount, c.CheckingAccount, new Money(payout), TransactionReason.Insurance, "Claim on " + policy.Id),
            };
            if (theft) tx.Ownership.Add(new OwnershipChange { Asset = policy.Asset, From = c.CharacterId, To = _w.Accounts.Insurer });
            var result = _w.Transactions.Execute(tx);
            if (!result.Success) return result;

            policy.Claims++;
            policy.PaidOutCents += payout;
            if (policy.Kind == InsuranceKind.Property)
            {
                var p = _w.Properties.Get(policy.Asset);
                p.InsuranceClaimedCents += loss.Cents;
                _w.Dirty.Mark(SaveChunks.Properties);
            }
            else
            {
                var v = _w.Vehicles.Get(policy.Asset);
                if (theft)
                {
                    policy.Status = PolicyStatus.Cancelled;
                }
                else v.InsuranceClaimedCents += loss.Cents;
                _w.Dirty.Mark(SaveChunks.Vehicles);
            }
            if (policy.RemainingCoverageCents <= 0) policy.Status = PolicyStatus.Cancelled;
            _w.Dirty.Mark(SaveChunks.Transactional);
            _w.Phone.Send(c, _w.Accounts.Insurer, InsurerName, PhoneCategory.Bank,
                "Claim approved: assessed loss " + loss + ", deductible " + new Money(policy.DeductibleCents) + ", paid " + new Money(payout) + ".");
            return result;
        }

        /// <summary>Hook for the crime layer: return true when the policyholder caused the loss (arson, staged theft).</summary>
        public Func<InsurancePolicy, bool> FraudSuspected;

        /// <summary>
        /// Settles a medical bill: the insurer pays its share straight to the provider and the patient pays the rest.
        /// Returns the patient's share, or fails if the patient cannot pay it.
        /// </summary>
        public OpResult PayMedicalBill(ServerCharacter c, Money bill, EntityId providerAccount, string description, string idempotencyKey, out Money insurerShare)
        {
            insurerShare = Money.Zero;
            if (bill.Cents <= 0) return OpResult.Fail("Nothing to pay.");
            var policy = _w.Insurance.ActiveHealth(c.CharacterId);
            long covered = 0;
            var year = _w.Today / 365;
            if (policy != null && _w.Today - policy.StartDay >= 0)
            {
                if (policy.DeductibleYear != year)
                {
                    policy.DeductibleYear = year;
                    policy.DeductibleMetCents = 0;
                }
                var deductibleLeft = Math.Max(0, policy.DeductibleCents - policy.DeductibleMetCents);
                var aboveDeductible = Math.Max(0, bill.Cents - deductibleLeft);
                covered = Math.Min(policy.RemainingCoverageCents, (long)Math.Round(aboveDeductible * policy.CoverShare));
            }
            var patient = bill.Cents - covered;
            var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = description ?? "Medical bill" };
            money.Add(providerAccount, bill.Cents);
            if (covered > 0) money.Add(_w.Accounts.InsurerAccount, -covered);
            if (patient > 0) money.Add(c.CheckingAccount, -patient);
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = description ?? "Medical bill",
                Money = money,
            });
            if (!result.Success) return result;
            if (policy != null)
            {
                policy.DeductibleMetCents += Math.Min(bill.Cents, Math.Max(0, policy.DeductibleCents - policy.DeductibleMetCents));
                if (covered > 0)
                {
                    policy.Claims++;
                    policy.PaidOutCents += covered;
                }
                _w.Dirty.Mark(SaveChunks.Transactional);
            }
            insurerShare = new Money(covered);
            return result;
        }

        // ------------------------------------------------------------------ daily

        /// <summary>Premiums, savings interest and automatic business-interruption settlements. Runs after businesses trade.</summary>
        public void ProcessDay(long day, GameDateTime timestamp)
        {
            var policies = new List<InsurancePolicy>(_w.Insurance.All);
            policies.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var p in policies)
            {
                if (!p.Active) continue;
                if (p.Kind == InsuranceKind.BusinessInterruption) SettleInterruption(p, day, timestamp);
                if (day < p.NextPremiumDay) continue;
                p.NextPremiumDay += 30;
                var paid = _w.Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = timestamp,
                    Description = "Premium " + p.Id,
                    Money = LedgerTransaction.Transfer(p.HolderAccount, _w.Accounts.InsurerAccount, new Money(p.MonthlyPremiumCents), TransactionReason.Premium, "Premium " + p.Id),
                }).Success;
                _w.Characters.TryGetValue(p.Holder, out var holder);
                if (paid)
                {
                    p.MissedPremiums = 0;
                    continue;
                }
                p.MissedPremiums++;
                if (p.MissedPremiums >= 2) p.Status = PolicyStatus.Lapsed;
                if (holder != null)
                    _w.Phone.Send(holder, _w.Accounts.Insurer, InsurerName, PhoneCategory.Bank, p.Status == PolicyStatus.Lapsed
                        ? "Your " + p.Kind + " policy has lapsed after two missed premiums. You are no longer covered."
                        : "We could not collect your " + p.Kind + " premium of " + new Money(p.MonthlyPremiumCents) + ". Cover lapses if the next one is missed.");
            }

            if (day % 30 == 0) PaySavingsInterest(timestamp);
        }

        /// <summary>Annual savings yield tracks inflation (0.5 %–5 %).</summary>
        public double SavingsRate => Math.Max(0.005, Math.Min(0.05, 0.02 + (_w.Macro.AnnualInflation - 0.028) * 0.5));

        private void PaySavingsInterest(GameDateTime timestamp)
        {
            var accounts = new List<LedgerAccount>();
            foreach (var a in _w.Ledger.Accounts) if (a.Kind == LedgerAccountKind.PersonalSavings && a.BalanceCents > 0) accounts.Add(a);
            accounts.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var a in accounts)
            {
                var interest = (long)Math.Round(a.BalanceCents * SavingsRate / 12.0);
                if (interest <= 0) continue;
                _w.Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = timestamp,
                    Description = "Savings interest",
                    Money = LedgerTransaction.Transfer(_w.Accounts.BankReserves, a.Id, new Money(interest), TransactionReason.Interest, "Monthly interest"),
                });
            }
        }

        private void SettleInterruption(InsurancePolicy p, long day, GameDateTime timestamp)
        {
            if (!_w.Businesses.TryGetValue(p.Asset, out var b) || b.Reports.Count == 0) return;
            var report = b.Reports[b.Reports.Count - 1];
            if (report.Day != day || !report.ForcedClosure || day - p.StartDay < InsurancePricing.WaitingPeriodDays) return;
            // A closure event is consecutive closed days; the deductible applies once per event.
            if (p.DeductibleYear != day - 1) p.DeductibleMetCents = 0;
            p.DeductibleYear = day;
            var loss = Math.Max(0, _w.BusinessOps.AverageDailyProfit(b, 30)) + report.FixedCostsCents;
            var deductibleLeft = Math.Max(0, p.DeductibleCents - p.DeductibleMetCents);
            var absorbed = Math.Min(loss, deductibleLeft);
            p.DeductibleMetCents += absorbed;
            var payout = Math.Min(p.RemainingCoverageCents, loss - absorbed);
            if (payout <= 0) return;
            var ok = _w.Transactions.Execute(new WorldTransaction
            {
                Source = TransactionSource.Simulation,
                Timestamp = timestamp,
                Description = "Business interruption " + p.Id,
                Money = LedgerTransaction.Transfer(_w.Accounts.InsurerAccount, b.Account, new Money(payout), TransactionReason.Insurance, "Interruption " + b.Name),
            }).Success;
            if (!ok) return;
            p.Claims++;
            p.PaidOutCents += payout;
            if (_w.Characters.TryGetValue(p.Holder, out var holder))
                _w.Phone.Send(holder, _w.Accounts.Insurer, InsurerName, PhoneCategory.Business, b.Name + " was closed by emergency order; interruption cover paid " + new Money(payout) + ".");
        }

        /// <summary>
        /// Hurricane damage for one hour (GDD §38): each property rolls independently (keyed by seed, property and hour,
        /// so results do not depend on iteration order). Flood-prone districts and stronger storms do more harm.
        /// </summary>
        public int ApplyStormDamage(long hourIndex, float category)
        {
            if (category < 1f) return 0;
            var damaged = 0;
            foreach (var p in _w.Properties.All)
            {
                if (p.Kind == PropertyKind.Land || p.Damage == DamageState.Destroyed) continue;
                var district = _w.Geography.GetDistrict(p.District);
                var flood = district != null ? district.FloodRisk : 0.2f;
                var rng = DeterministicRandom.For(_w.Seed, 0x570A3D, p.Id.Value, (ulong)hourIndex);
                if (!rng.Chance(0.0035 * category * (0.3 + flood))) continue;
                var severity = (float)((0.03 + rng.NextDouble() * 0.12) * (category / 3.0) * (0.6 + flood));
                _w.Properties.ApplyDamage(p, severity);
                damaged++;
                var owner = _w.Ownership.OwnerOf(p.Id);
                if (_w.Characters.TryGetValue(owner, out var c))
                    _w.Phone.Send(c, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Emergency,
                        "Storm damage reported at " + p.Address + ". Estimated repairs: " + _w.Properties.RepairQuote(p, _w.Macro.PriceLevel) + ".");
            }
            if (damaged > 0) _w.Dirty.Mark(SaveChunks.Properties);
            return damaged;
        }
    }
}
