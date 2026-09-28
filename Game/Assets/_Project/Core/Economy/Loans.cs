using System;
using System.Collections.Generic;

namespace HeroGame.Core.Economy
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Time;

    public enum LoanKind
    {
        Personal,
        Mortgage,
        Business,
        Vehicle,
    }

    public enum LoanStatus
    {
        Active,
        PaidOff,
        Delinquent,
        Defaulted,
    }

    /// <summary>
    /// Fixed-rate amortising loan. The principal is disbursed through the ledger; the loan record
    /// tracks the liability (outstanding principal) and schedule. Collateral, if any, is an asset id
    /// that can be repossessed on default.
    /// </summary>
    [Serializable]
    public sealed class Loan
    {
        public EntityId Id;
        public LoanKind Kind;
        public EntityId Borrower;
        public EntityId BorrowerAccount;
        public EntityId LenderAccount;
        public EntityId Collateral;
        public long PrincipalCents;
        public long OutstandingCents;
        public double AnnualRate;
        public int TermMonths;
        public long MonthlyPaymentCents;
        public GameDateTime NextPaymentDue;
        public int MissedPayments;
        public LoanStatus Status;

        /// <summary>Standard amortisation formula: P·r / (1 − (1+r)^−n).</summary>
        public static long ComputeMonthlyPayment(long principalCents, double annualRate, int termMonths)
        {
            if (termMonths <= 0) throw new ArgumentOutOfRangeException(nameof(termMonths));
            if (annualRate <= 0) return (long)Math.Ceiling(principalCents / (double)termMonths);
            var r = annualRate / 12.0;
            var payment = principalCents * r / (1.0 - Math.Pow(1.0 + r, -termMonths));
            return (long)Math.Ceiling(payment);
        }

        /// <summary>Splits a payment into interest and principal for the current outstanding balance.</summary>
        public void SplitPayment(long paymentCents, out long interestCents, out long principalCents)
        {
            interestCents = (long)Math.Round(OutstandingCents * AnnualRate / 12.0, MidpointRounding.AwayFromZero);
            principalCents = Math.Min(OutstandingCents, Math.Max(0, paymentCents - interestCents));
        }
    }

    /// <summary>Issues and services loans through the <see cref="TransactionProcessor"/>.</summary>
    public sealed class LoanBook
    {
        private readonly Dictionary<EntityId, Loan> _loans = new Dictionary<EntityId, Loan>();

        public IEnumerable<Loan> Loans => _loans.Values;
        public int Count => _loans.Count;

        public void Restore(Loan loan) => _loans[loan.Id] = loan;
        public bool TryGet(EntityId id, out Loan loan) => _loans.TryGetValue(id, out loan);

        public Money OutstandingFor(EntityId borrower)
        {
            long total = 0;
            foreach (var l in _loans.Values) if (l.Borrower == borrower && l.Status != LoanStatus.PaidOff) total += l.OutstandingCents;
            return new Money(total);
        }

        /// <summary>Creates the loan record and a disbursement transaction. The caller commits the transaction.</summary>
        public Loan Originate(EntityId loanId, LoanKind kind, EntityId borrower, EntityId borrowerAccount, EntityId lenderAccount,
            Money principal, double annualRate, int termMonths, GameDateTime now, EntityId collateral, out LedgerTransaction disbursement)
        {
            var loan = Create(loanId, kind, borrower, borrowerAccount, lenderAccount, principal, annualRate, termMonths, now, collateral);
            disbursement = LedgerTransaction.Transfer(lenderAccount, borrowerAccount, principal, TransactionReason.LoanDisbursement, kind + " loan " + loanId);
            _loans[loanId] = loan;
            return loan;
        }

        /// <summary>Builds a loan record without registering it (it is registered when its transaction applies).</summary>
        public static Loan Create(EntityId loanId, LoanKind kind, EntityId borrower, EntityId borrowerAccount, EntityId lenderAccount,
            Money principal, double annualRate, int termMonths, GameDateTime now, EntityId collateral)
        {
            if (principal.Cents <= 0) throw new ArgumentOutOfRangeException(nameof(principal));
            return new Loan
            {
                Id = loanId,
                Kind = kind,
                Borrower = borrower,
                BorrowerAccount = borrowerAccount,
                LenderAccount = lenderAccount,
                Collateral = collateral,
                PrincipalCents = principal.Cents,
                OutstandingCents = principal.Cents,
                AnnualRate = annualRate,
                TermMonths = termMonths,
                MonthlyPaymentCents = Loan.ComputeMonthlyPayment(principal.Cents, annualRate, termMonths),
                NextPaymentDue = now.AddDays(30),
                Status = LoanStatus.Active,
            };
        }

        /// <summary>Discards a loan whose disbursement transaction failed to commit.</summary>
        public void Cancel(EntityId loanId) => _loans.Remove(loanId);

        /// <summary>
        /// Services every loan whose payment is due. Returns loans that just defaulted so the
        /// caller can repossess collateral. Runs daily inside the simulation (offline too).
        /// </summary>
        public List<Loan> ProcessDuePayments(GameDateTime now, TransactionProcessor processor, Ledger ledger)
        {
            var defaulted = new List<Loan>();
            foreach (var loan in _loans.Values)
            {
                if (loan.Status == LoanStatus.PaidOff || loan.Status == LoanStatus.Defaulted) continue;
                while (loan.NextPaymentDue <= now && loan.OutstandingCents > 0)
                {
                    loan.SplitPayment(loan.MonthlyPaymentCents, out var interest, out var principal);
                    var due = interest + principal;
                    var tx = new WorldTransaction
                    {
                        Source = TransactionSource.Simulation,
                        Timestamp = now,
                        Description = "Loan payment " + loan.Id,
                        Money = LedgerTransaction.Transfer(loan.BorrowerAccount, loan.LenderAccount, new Money(due), TransactionReason.LoanRepayment),
                    };
                    if (processor.Execute(tx).Success)
                    {
                        loan.OutstandingCents -= principal;
                        loan.MissedPayments = 0;
                        if (loan.Status == LoanStatus.Delinquent) loan.Status = LoanStatus.Active;
                    }
                    else
                    {
                        loan.MissedPayments++;
                        loan.Status = loan.MissedPayments >= 3 ? LoanStatus.Defaulted : LoanStatus.Delinquent;
                        if (loan.Status == LoanStatus.Defaulted) defaulted.Add(loan);
                    }
                    loan.NextPaymentDue = loan.NextPaymentDue.AddDays(30);
                    if (loan.Status == LoanStatus.Defaulted) break;
                }
                if (loan.OutstandingCents <= 0 && loan.Status != LoanStatus.Defaulted) loan.Status = LoanStatus.PaidOff;
            }
            return defaulted;
        }
    }
}
