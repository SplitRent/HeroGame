using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    using HeroGame.Core.Business;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.Simulation;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// Owner's office / buyer's view for one business (GDD §18–19). Every button calls a core
    /// <see cref="BusinessOperations"/> method, so the UI holds no rules of its own.
    /// Placeholder IMGUI (docs/ASSET_TRACKER.md) until the UI Toolkit screens of Phase 24.
    /// </summary>
    public sealed class BusinessPanel : MonoBehaviour
    {
        private static BusinessPanel _instance;
        private BusinessRecord _business;
        private string _status = "";
        private string _amount = "1000";
        private Vector2 _scroll;
        private List<NpcRecord> _candidates;

        public static void Open(BusinessRecord business)
        {
            if (_instance == null) _instance = new GameObject("Business Panel").AddComponent<BusinessPanel>();
            if (_instance._business == null) UiFocus.Acquire();
            _instance._business = business;
            _instance._status = "";
            _instance._candidates = null;
        }

        public static bool IsOpen => _instance != null && _instance._business != null;

        private void Close()
        {
            if (_business == null) return;
            _business = null;
            UiFocus.Release();
        }

        private void OnGUI()
        {
            if (_business == null || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var w = session.World;
            var me = session.LocalCharacter;
            var ops = w.BusinessOps;
            var b = _business;
            var t = w.BusinessSim.Template(b.TemplateId);
            var owner = w.Ownership.IsOwnedBy(b.Id, me.CharacterId);
            var manager = ops.CanManage(b, me.CharacterId);

            GUILayout.BeginArea(new Rect(Screen.width * 0.5f - 300, 40, 600, Screen.height - 80), GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(b.Name + (t != null ? "  ·  " + t.DisplayName : ""));
            if (GUILayout.Button("Close", GUILayout.Width(70))) Close();
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Reputation " + (int)(b.Reputation * 100) + "/100 · staff " + b.Staff + (t != null ? "/" + t.StaffRequired : "") +
                            " · stock " + b.InventoryDays.ToString("0.0") + " days · " + (b.Open ? "open" : "closed"));
            GUILayout.Label("Till: " + w.Ledger.BalanceOf(b.Account) + " · 30-day avg profit " + new Money(ops.AverageDailyProfit(b, 30)) + "/day · valuation " + ops.Valuation(b));
            for (var i = b.Reports.Count - 1; i >= 0 && i >= b.Reports.Count - 5; i--)
            {
                var r = b.Reports[i];
                GUILayout.Label("  Day " + r.Day + ": " + (r.WasClosed ? "closed" : r.Customers + " customers, revenue " + new Money(r.RevenueCents)) +
                                ", profit " + new Money(r.ProfitCents) + (string.IsNullOrEmpty(r.Note) ? "" : " — " + r.Note));
            }

            if (!manager)
            {
                var forSale = b.ForSale || !w.Ownership.OwnerOf(b.Id).IsValid;
                if (forSale)
                {
                    var price = ops.PriceOf(b);
                    GUILayout.Label("For sale: business " + price.Business + " + till " + price.Cash + (price.Property.Cents > 0 ? " + building " + price.Property : "") + " + tax " + price.Tax);
                    if (GUILayout.Button("Buy for " + price.Total))
                    {
                        var allowed = session.CheckAllowed("business.buy");
                        Report(allowed.Success ? ops.Buy(b, me, session.NextRequestKey("buy-business")) : allowed, "Purchased.");
                    }
                }
                else GUILayout.Label("Not for sale.");
            }
            else
            {
                GUILayout.Space(8);
                GUILayout.Label("Prices ×" + b.PriceLevel.ToString("0.00"));
                var price = GUILayout.HorizontalSlider(b.PriceLevel, BusinessOperations.MinPriceLevel, BusinessOperations.MaxPriceLevel);
                if (Mathf.Abs(price - b.PriceLevel) > 0.01f) ops.SetPrice(b, me.CharacterId, Mathf.Round(price * 20f) / 20f);
                GUILayout.Label("Wages ×" + b.WageLevel.ToString("0.00") + " (" + ops.OfferedHourly(b) + "/hour)");
                var wage = GUILayout.HorizontalSlider(b.WageLevel, BusinessOperations.MinWageLevel, BusinessOperations.MaxWageLevel);
                if (Mathf.Abs(wage - b.WageLevel) > 0.01f) ops.SetWageLevel(b, me.CharacterId, Mathf.Round(wage * 20f) / 20f);
                GUILayout.Label("Advertising " + new Money(b.AdvertisingCents) + "/day");
                var ads = GUILayout.HorizontalSlider(b.AdvertisingCents, 0, BusinessOperations.MaxAdvertisingCents);
                if (Mathf.Abs(ads - b.AdvertisingCents) > 500f) ops.SetAdvertising(b, me.CharacterId, new Money((long)(ads / 1000f) * 1000));

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(b.Open ? "Close for business" : "Open for business")) ops.SetOpen(b, me.CharacterId, !b.Open);
                if (GUILayout.Button(b.AutoRestock ? "Auto-restock: on" : "Auto-restock: off")) ops.SetAutoRestock(b, me.CharacterId, !b.AutoRestock);
                if (t != null && t.InventoryDays > 0 && GUILayout.Button("Order 3 days of stock (" + ops.RestockCost(b, 3f) + ")"))
                    Report(ops.Restock(b, me.CharacterId, 3f, session.NextRequestKey("restock")), "Stock ordered.");
                GUILayout.EndHorizontal();

                GUILayout.Space(8);
                GUILayout.Label("Staff");
                foreach (var id in b.Employees.ToArray())
                {
                    var npc = w.Population.Get(id);
                    if (npc == null) continue;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(npc.FullName + ", " + npc.AgeYears(w.Today));
                    if (GUILayout.Button("Let go", GUILayout.Width(80))) Report(ops.Fire(b, me.CharacterId, id), npc.FullName + " was let go.");
                    GUILayout.EndHorizontal();
                }
                if (GUILayout.Button(_candidates == null ? "Find job seekers" : "Refresh job seekers")) _candidates = ops.Candidates(b, 8);
                if (_candidates != null)
                    foreach (var npc in _candidates.ToArray())
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(npc.FullName + ", " + npc.AgeYears(w.Today) + " · " + npc.Education + (npc.Employment == EmploymentStatus.Employed ? " (currently employed)" : ""));
                        if (GUILayout.Button("Hire", GUILayout.Width(80)))
                        {
                            Report(ops.Hire(b, me.CharacterId, npc.Id), "Hired " + npc.FullName + ".");
                            _candidates.Remove(npc);
                        }
                        GUILayout.EndHorizontal();
                    }

                if (owner)
                {
                    GUILayout.Space(8);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Amount $", GUILayout.Width(70));
                    _amount = GUILayout.TextField(_amount, 12, GUILayout.Width(120));
                    var parsed = long.TryParse(_amount, out var dollars) && dollars > 0 ? Money.FromDollars(dollars) : Money.Zero;
                    if (GUILayout.Button("Withdraw")) Report(ops.Withdraw(b, me, parsed, session.NextRequestKey("draw")), "Withdrawn " + parsed + ".");
                    if (GUILayout.Button("Invest")) Report(ops.Invest(b, me, parsed, session.NextRequestKey("invest")), "Invested " + parsed + ".");
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if (!b.ForSale && GUILayout.Button("List for sale at valuation")) Report(ops.ListForSale(b, me.CharacterId, ops.Valuation(b)), "Listed.");
                    if (b.ForSale && GUILayout.Button("Take off the market")) Report(ops.Delist(b, me.CharacterId), "Delisted.");
                    if (GUILayout.Button("Sell now (60 % of valuation)")) Report(ops.SellToMarket(b, me, session.NextRequestKey("sell")), "Sold.");
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Label(_status);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void Report(OpResult result, string success) => _status = result.Success ? success : result.Error;

        private void OnDestroy()
        {
            if (_business != null) UiFocus.Release();
            if (_instance == this) _instance = null;
        }
    }
}
