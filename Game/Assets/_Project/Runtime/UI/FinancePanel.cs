using System.Collections.Generic;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Player;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// The phone's bank and insurance apps (GDD §26–27, §61) in prototype form: balances and transfers,
    /// credit score, loans with early repayment, and policies with quotes and claims for everything the
    /// character owns. Opened with the phone key. Placeholder IMGUI until the Phase 23 phone UI.
    /// </summary>
    public sealed class FinancePanel : MonoBehaviour
    {
        private enum Tab { Bank, Loans, Insurance, Businesses }

        private IPlayerInputSource _input;
        private bool _open;
        private Tab _tab;
        private string _amount = "500";
        private string _loanAmount = "5000";
        private string _deductible = "1000";
        private string _status = "";
        private Vector2 _scroll;

        private void Update()
        {
            if (_input == null) _input = PlayerInputRegistry.Create();
            // Read regardless of GameplayEnabled so the phone can also be closed with its own key.
            if (!_input.Read().PhonePressed) return;
            _open = !_open;
            if (_open) UiFocus.Acquire();
            else UiFocus.Release();
        }

        private void OnGUI()
        {
            if (!_open || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var w = session.World;
            var me = session.LocalCharacter;
            var fin = w.Finance;

            GUILayout.BeginArea(new Rect(Screen.width - 460, 40, 440, Screen.height - 80), GUI.skin.box);
            GUILayout.BeginHorizontal();
            foreach (Tab t in System.Enum.GetValues(typeof(Tab)))
                if (GUILayout.Toggle(_tab == t, t.ToString(), GUI.skin.button)) _tab = t;
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll);

            switch (_tab)
            {
                case Tab.Bank:
                {
                    var savings = fin.SavingsOf(me);
                    GUILayout.Label("Checking: " + w.Ledger.BalanceOf(me.CheckingAccount));
                    GUILayout.Label("Savings: " + (savings.IsValid ? w.Ledger.BalanceOf(savings).ToString() : "none") + "  (" + (fin.SavingsRate * 100).ToString("0.00") + "% a year)");
                    if (!savings.IsValid && GUILayout.Button("Open a savings account")) Report(fin.OpenSavings(me, session.NextRequestKey("savings")), "Savings account opened.");
                    if (savings.IsValid)
                    {
                        GUILayout.BeginHorizontal();
                        _amount = GUILayout.TextField(_amount, 10, GUILayout.Width(100));
                        var amount = Parse(_amount);
                        if (GUILayout.Button("To savings")) Report(fin.TransferOwn(me, me.CheckingAccount, savings, amount, session.NextRequestKey("xfer")), "Moved " + amount + ".");
                        if (GUILayout.Button("To checking")) Report(fin.TransferOwn(me, savings, me.CheckingAccount, amount, session.NextRequestKey("xfer")), "Moved " + amount + ".");
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.Space(6);
                    GUILayout.Label("Statement");
                    for (var i = me.Statement.Count - 1; i >= 0 && i >= me.Statement.Count - 12; i--)
                    {
                        var s = me.Statement[i];
                        GUILayout.Label("  " + new Money(s.AmountCents) + "  " + s.Reason + (string.IsNullOrEmpty(s.Memo) ? "" : " · " + s.Memo));
                    }
                    break;
                }
                case Tab.Loans:
                {
                    var credit = fin.Credit(me);
                    GUILayout.Label("Credit score " + credit.Score + " · verified income " + fin.VerifiedMonthlyIncome(me) + "/month");
                    GUILayout.Label("Debt payments " + new Money(credit.MonthlyDebtPaymentsCents) + "/month, outstanding " + new Money(credit.OutstandingCents));
                    foreach (var loan in w.Loans.Loans)
                    {
                        if (loan.Borrower != me.CharacterId) continue;
                        GUILayout.Label(loan.Kind + " · " + loan.Status + " · " + new Money(loan.OutstandingCents) + " left · " + new Money(loan.MonthlyPaymentCents) + "/mo @ " + (loan.AnnualRate * 100).ToString("0.00") + "%");
                        if (loan.Status == LoanStatus.Active || loan.Status == LoanStatus.Delinquent)
                        {
                            GUILayout.BeginHorizontal();
                            if (GUILayout.Button("Pay " + Parse(_amount))) Report(fin.Repay(me, loan, Parse(_amount), session.NextRequestKey("repay")), "Payment made.");
                            if (GUILayout.Button("Pay off")) Report(fin.Repay(me, loan, new Money(loan.OutstandingCents), session.NextRequestKey("payoff")), "Loan paid off.");
                            GUILayout.EndHorizontal();
                        }
                    }
                    GUILayout.Space(6);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Personal loan $", GUILayout.Width(110));
                    _loanAmount = GUILayout.TextField(_loanAmount, 8, GUILayout.Width(90));
                    GUILayout.EndHorizontal();
                    var offer = fin.QuoteLoan(me, LoanKind.Personal, Parse(_loanAmount), 36, EntityId.None);
                    GUILayout.Label(offer.Approved ? "36 months at " + (offer.AnnualRate * 100).ToString("0.00") + "%: " + new Money(offer.MonthlyPaymentCents) + "/month" : offer.Reason);
                    if (offer.Approved && GUILayout.Button("Accept")) Report(fin.TakeLoan(me, LoanKind.Personal, Parse(_loanAmount), 36, EntityId.None, session.NextRequestKey("loan")), "Funds deposited.");
                    break;
                }
                case Tab.Insurance:
                {
                    foreach (var p in w.Insurance.ForHolder(me.CharacterId))
                    {
                        GUILayout.Label(p.Kind + " · " + p.Status + " · " + new Money(p.MonthlyPremiumCents) + "/mo · cover " + new Money(p.RemainingCoverageCents) + " · claims " + p.Claims);
                        if (!p.Active) continue;
                        GUILayout.BeginHorizontal();
                        var loss = fin.AssessedLoss(p);
                        if (loss.Cents > 0 && GUILayout.Button("Claim " + loss)) Report(fin.Claim(me, p, session.NextRequestKey("claim")), "Claim paid.");
                        if (GUILayout.Button("Cancel")) Report(fin.CancelInsurance(me, p), "Policy cancelled.");
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.Space(6);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Deductible $", GUILayout.Width(90));
                    _deductible = GUILayout.TextField(_deductible, 8, GUILayout.Width(90));
                    GUILayout.EndHorizontal();
                    var deductible = Parse(_deductible);
                    QuoteRow(session, InsuranceKind.Health, EntityId.None, "Health", deductible);
                    foreach (var asset in new List<EntityId>(w.Ownership.AssetsOf(me.CharacterId)))
                    {
                        if (asset.Kind == EntityKind.Property) QuoteRow(session, InsuranceKind.Property, asset, w.Properties.Get(asset)?.Address ?? asset.ToString(), deductible);
                        else if (asset.Kind == EntityKind.Vehicle) QuoteRow(session, InsuranceKind.Vehicle, asset, w.Vehicles.Get(asset)?.Plate ?? asset.ToString(), deductible);
                        else if (asset.Kind == EntityKind.Business && w.Businesses.TryGetValue(asset, out var biz)) QuoteRow(session, InsuranceKind.BusinessInterruption, asset, biz.Name, deductible);
                    }
                    break;
                }
                case Tab.Businesses:
                {
                    foreach (var b in w.BusinessOps.OwnedBy(me.CharacterId))
                        if (GUILayout.Button("Manage " + b.Name + " (" + w.Ledger.BalanceOf(b.Account) + ")")) BusinessPanel.Open(b);
                    GUILayout.Space(6);
                    GUILayout.Label("For sale");
                    foreach (var b in w.BusinessOps.ForSale())
                        if (GUILayout.Button(b.Name + " — " + w.BusinessOps.PriceOf(b).Total)) BusinessPanel.Open(b);
                    break;
                }
            }
            GUILayout.Label(_status);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void QuoteRow(GameSession session, InsuranceKind kind, EntityId asset, string label, Money deductible)
        {
            var q = session.World.Finance.QuoteInsurance(session.LocalCharacter, kind, asset, deductible);
            if (!q.Available) return;
            GUILayout.BeginHorizontal();
            GUILayout.Label(kind + ": " + label + " — " + new Money(q.MonthlyPremiumCents) + "/mo");
            if (GUILayout.Button("Insure", GUILayout.Width(70)))
                Report(session.World.Finance.BuyInsurance(session.LocalCharacter, kind, asset, deductible, session.NextRequestKey("insure")), "Covered.");
            GUILayout.EndHorizontal();
        }

        private static Money Parse(string dollars) => long.TryParse(dollars, out var d) && d > 0 ? Money.FromDollars(d) : Money.Zero;

        private void Report(OpResult result, string success) => _status = result.Success ? success : result.Error;

        private void OnDestroy()
        {
            if (_open) UiFocus.Release();
        }
    }
}
