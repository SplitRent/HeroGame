using System;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Economy
{
    /// <summary>Credit standing derived entirely from the server's own loan records (no hidden state to persist).</summary>
    public sealed class CreditProfile
    {
        public int Score;
        public int Defaults;
        public int Delinquent;
        public int PaidOff;
        public int Active;
        public long MonthlyDebtPaymentsCents;
        public long OutstandingCents;
    }

    public sealed class LoanApplication
    {
        public LoanKind Kind;
        public EntityId Borrower;
        public long PrincipalCents;
        public int TermMonths;
        /// <summary>Appraised value of the collateral (0 for unsecured personal loans).</summary>
        public long CollateralValueCents;
        /// <summary>Verifiable monthly income (business profits, rents, wages).</summary>
        public long MonthlyIncomeCents;
        /// <summary>Cash the borrower holds (checking + savings).</summary>
        public long LiquidAssetsCents;
    }

    public sealed class LoanOffer
    {
        public bool Approved;
        public string Reason = "";
        public int CreditScore;
        public double AnnualRate;
        public long PrincipalCents;
        public long MaxPrincipalCents;
        public int TermMonths;
        public long MonthlyPaymentCents;
    }

    /// <summary>
    /// Bank underwriting (GDD §26): score from repayment history, loan-to-value limits per loan kind, and a
    /// debt-to-income cap — with an asset-depletion alternative for borrowers who hold cash but have no
    /// provable income. Rates follow the macro economy (inflation) plus a risk spread.
    /// </summary>
    public static class Underwriter
    {
        public const int MinimumScore = 520;
        public const double MaxDebtToIncome = 0.43;
        public const int AssetDepletionMonths = 36;
        public const long UnsecuredLimitCents = 2500000; // $25,000

        public static CreditProfile Profile(EntityId borrower, LoanBook loans, long historyDays)
        {
            var p = new CreditProfile();
            foreach (var l in loans.Loans)
            {
                if (l.Borrower != borrower) continue;
                switch (l.Status)
                {
                    case LoanStatus.Defaulted: p.Defaults++; break;
                    case LoanStatus.Delinquent: p.Delinquent++; p.Active++; break;
                    case LoanStatus.PaidOff: p.PaidOff++; break;
                    default: p.Active++; break;
                }
                if (l.Status == LoanStatus.Active || l.Status == LoanStatus.Delinquent)
                {
                    p.MonthlyDebtPaymentsCents += l.MonthlyPaymentCents;
                    p.OutstandingCents += l.OutstandingCents;
                }
            }
            var score = 640.0
                        + Math.Min(90.0, Math.Max(0, historyDays) / 4.0)   // length of history
                        + 30.0 * Math.Min(3, p.PaidOff)                    // repaid loans
                        + 10.0 * Math.Min(2, p.Active)                     // a healthy active account helps a little
                        - 45.0 * p.Delinquent
                        - 140.0 * p.Defaults;
            p.Score = (int)Math.Max(300, Math.Min(850, Math.Round(score)));
            return p;
        }

        public static double BaseRate(MacroEconomyState macro)
        {
            var inflation = macro != null ? macro.AnnualInflation : 0.028;
            return Math.Max(0.02, 0.022 + inflation);
        }

        public static double Spread(LoanKind kind)
        {
            switch (kind)
            {
                case LoanKind.Mortgage: return 0.015;
                case LoanKind.Vehicle: return 0.03;
                case LoanKind.Business: return 0.045;
                default: return 0.075;
            }
        }

        public static double MaxLoanToValue(LoanKind kind, int score)
        {
            switch (kind)
            {
                case LoanKind.Mortgage: return score >= 740 ? 0.9 : 0.8;
                case LoanKind.Vehicle: return 0.9;
                case LoanKind.Business: return 0.7;
                default: return 0.0;
            }
        }

        public static int MaxTermMonths(LoanKind kind)
        {
            switch (kind)
            {
                case LoanKind.Mortgage: return 360;
                case LoanKind.Vehicle: return 84;
                case LoanKind.Business: return 120;
                default: return 60;
            }
        }

        public static LoanOffer Quote(LoanApplication app, CreditProfile credit, MacroEconomyState macro)
        {
            var offer = new LoanOffer { CreditScore = credit.Score, TermMonths = app.TermMonths, PrincipalCents = app.PrincipalCents };
            offer.AnnualRate = Math.Round(BaseRate(macro) + Spread(app.Kind) + Math.Max(0, 760 - credit.Score) / 100.0 * 0.012, 4);

            if (app.PrincipalCents <= 0) return Decline(offer, "Enter an amount to borrow.");
            if (app.TermMonths < 6 || app.TermMonths > MaxTermMonths(app.Kind)) return Decline(offer, "Term must be between 6 and " + MaxTermMonths(app.Kind) + " months.");
            if (credit.Defaults > 0 && credit.Score < 600) return Decline(offer, "A recent default is on file.");
            if (credit.Score < MinimumScore) return Decline(offer, "Credit score " + credit.Score + " is below " + MinimumScore + ".");

            long max;
            if (app.Kind == LoanKind.Personal)
            {
                max = Math.Min(UnsecuredLimitCents, Math.Max(app.MonthlyIncomeCents * 4, app.LiquidAssetsCents / 2));
            }
            else
            {
                if (app.CollateralValueCents <= 0) return Decline(offer, "This loan must be secured by collateral.");
                max = (long)(app.CollateralValueCents * MaxLoanToValue(app.Kind, credit.Score));
            }
            offer.MaxPrincipalCents = max;
            if (app.PrincipalCents > max)
                return Decline(offer, "The most we can lend against this is " + new Money(max) + (app.Kind == LoanKind.Mortgage ? " (a larger down payment is needed)." : "."));

            offer.MonthlyPaymentCents = Loan.ComputeMonthlyPayment(app.PrincipalCents, offer.AnnualRate, app.TermMonths);
            var totalDebt = credit.MonthlyDebtPaymentsCents + offer.MonthlyPaymentCents;
            var incomeOk = app.MonthlyIncomeCents > 0 && totalDebt <= app.MonthlyIncomeCents * MaxDebtToIncome;
            var assetsOk = app.LiquidAssetsCents >= totalDebt * (long)AssetDepletionMonths;
            if (!incomeOk && !assetsOk)
                return Decline(offer, "Payments of " + new Money(totalDebt) + "/month are too high for your verified income and savings.");

            offer.Approved = true;
            offer.Reason = "Approved at " + (offer.AnnualRate * 100).ToString("0.00") + "% APR.";
            return offer;
        }

        private static LoanOffer Decline(LoanOffer offer, string reason)
        {
            offer.Approved = false;
            offer.Reason = reason;
            return offer;
        }
    }
}
