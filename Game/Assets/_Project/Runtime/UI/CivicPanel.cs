using System.Collections.Generic;
using HeroGame.Core.Civic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// City Hall (GDD §24–27): the budget and what it buys, the council's agenda, open elections with polls,
    /// filing, donations and ballots, and anomalous-abilities registration. Every button calls
    /// <see cref="Core.Simulation.CivicService"/>. Placeholder IMGUI (docs/ASSET_TRACKER.md).
    /// </summary>
    public sealed class CivicPanel : MonoBehaviour
    {
        private enum Tab { City, Council, Elections, Registration }

        private static CivicPanel _instance;
        private bool _open;
        private Tab _tab;
        private string _status = "";
        private string _amount = "100";
        private Vector2 _scroll;
        private readonly Dictionary<Department, float> _shares = new Dictionary<Department, float>();
        private float _rate = 0.85f;

        public static void Open()
        {
            if (_instance == null) _instance = new GameObject("Civic Panel").AddComponent<CivicPanel>();
            if (!_instance._open) UiFocus.Acquire();
            _instance._open = true;
            _instance._status = "";
            _instance._shares.Clear();
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            UiFocus.Release();
        }

        private void OnGUI()
        {
            if (!_open || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var w = session.World;
            var me = session.LocalCharacter;
            var gov = w.Government;
            var civic = w.Civic;

            GUILayout.BeginArea(new Rect(Screen.width * 0.5f - 320, 40, 640, Screen.height - 80), GUI.skin.box);
            GUILayout.BeginHorizontal();
            foreach (Tab t in System.Enum.GetValues(typeof(Tab)))
                if (GUILayout.Toggle(_tab == t, t.ToString(), GUI.skin.button)) _tab = t;
            if (GUILayout.Button("Close", GUILayout.Width(70))) Close();
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll);

            switch (_tab)
            {
                case Tab.City:
                {
                    var mayor = civic.Officeholders.Find(o => o.Office == Office.Mayor);
                    GUILayout.Label("Mayor: " + (mayor != null ? mayor.Name + " (" + mayor.Slate + ")" : "vacant"));
                    GUILayout.Label("Treasury: " + w.Ledger.BalanceOf(w.Accounts.Treasury) + " · last month revenue " + new Money(civic.Budget.LastMonthRevenueCents)
                                    + ", spending " + new Money(civic.Budget.LastMonthSpendingCents));
                    foreach (var d in civic.Budget.Departments)
                        GUILayout.Label("  " + d.Department + ": " + (int)(d.Share * 100) + "% of budget · service " + (int)(d.ServiceLevel * 100) + "%");
                    GUILayout.Label("Ordinances in force:");
                    foreach (var a in civic.Ordinances)
                        GUILayout.Label("  " + (gov.Ordinance(a.Id)?.Title ?? a.Id) + " (since day " + a.EnactedDay + ")");
                    foreach (var e in w.Calendar.ActiveOn(w.Clock.Now)) GUILayout.Label("Today: " + e.Name);
                    foreach (var d in w.Disasters.Active) GUILayout.Label("ACTIVE: " + d.Headline);

                    GUILayout.Space(6);
                    if (w.Combat.HasLicense(me, Core.Simulation.CombatService.FirearmPermit)) GUILayout.Label("Firearm permit: held");
                    else if (GUILayout.Button("Apply for a firearm permit (" + new Money((long)(Core.Simulation.CombatService.PermitFeeCents * w.Macro.PriceLevel)) + ", background check)"))
                        Report(w.Combat.ApplyForFirearmPermit(me, session.NextRequestKey("permit")), "Permit issued. Valid for five years.");

                    if (gov.Holds(me.CharacterId, Office.Mayor))
                    {
                        GUILayout.Space(8);
                        GUILayout.Label("Mayor's budget");
                        foreach (var d in civic.Budget.Departments)
                        {
                            if (!_shares.ContainsKey(d.Department)) _shares[d.Department] = d.Share;
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(d.Department.ToString(), GUILayout.Width(110));
                            _shares[d.Department] = GUILayout.HorizontalSlider(_shares[d.Department], 0f, 0.6f);
                            GUILayout.EndHorizontal();
                        }
                        GUILayout.Label("Spending rate " + (int)(_rate * 100) + "% of revenue");
                        _rate = GUILayout.HorizontalSlider(_rate, 0.5f, 1.2f);
                        if (GUILayout.Button("Sign the budget")) Report(gov.SetBudget(me, _shares, _rate), "Budget signed; it takes effect next month.");
                    }
                    break;
                }
                case Tab.Council:
                {
                    GUILayout.Label("Council");
                    foreach (var o in civic.Officeholders)
                        if (o.Office == Office.Council) GUILayout.Label("  " + o.District + ": " + o.Name + " (" + o.Slate + ")");
                    GUILayout.Label("Agenda");
                    foreach (var p in civic.Proposals)
                    {
                        var def = gov.Ordinance(p.OrdinanceId);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("  " + (p.Repeal == "" ? "" : "Repeal: ") + (def?.Title ?? p.OrdinanceId) + (p.Decided ? (p.Passed ? " — passed " : " — failed ") + p.Ayes + "–" + p.Nays : " — vote on day " + p.VoteDay));
                        if (!p.Decided && gov.HoldsOffice(me.CharacterId))
                        {
                            if (GUILayout.Button("Aye", GUILayout.Width(50))) Report(gov.CastCouncilVote(me, p.OrdinanceId, true), "Vote recorded.");
                            if (GUILayout.Button("Nay", GUILayout.Width(50))) Report(gov.CastCouncilVote(me, p.OrdinanceId, false), "Vote recorded.");
                        }
                        GUILayout.EndHorizontal();
                    }
                    if (gov.HoldsOffice(me.CharacterId))
                    {
                        GUILayout.Label("Bring an ordinance to a vote");
                        foreach (var def in gov.Ordinances)
                        {
                            var active = civic.IsActive(def.Id);
                            if (GUILayout.Button((active ? "Repeal " : "Propose ") + def.Title)) Report(gov.Propose(me.CharacterId, def.Id, active), "On the agenda.");
                        }
                    }
                    break;
                }
                case Tab.Elections:
                {
                    if (civic.Elections.Count == 0) GUILayout.Label("No election is open. Last scheduled on day " + civic.LastElectionScheduledDay + ".");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Amount $", GUILayout.Width(70));
                    _amount = GUILayout.TextField(_amount, 8, GUILayout.Width(100));
                    GUILayout.EndHorizontal();
                    var amount = long.TryParse(_amount, out var dollars) && dollars > 0 ? Money.FromDollars(dollars) : Money.Zero;
                    foreach (var e in civic.Elections)
                    {
                        GUILayout.Space(6);
                        GUILayout.Label((e.Office == Office.Mayor ? "Mayor" : e.District + " council") + " — election day " + e.ElectionDay);
                        foreach (var c in e.Candidates)
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("  " + c.Name + " (" + c.Slate + (c.Incumbent ? ", incumbent" : "") + ") polling " + (int)(c.Polling * 100) + "%");
                            if (!e.PlayerVoters.Contains(me.CharacterId) && GUILayout.Button("Vote", GUILayout.Width(60)))
                                Report(gov.CastBallot(me, e.Id, c.Person), "Ballot cast.");
                            if (c.CampaignAccount.IsValid && c.Person != me.CharacterId && GUILayout.Button("Donate", GUILayout.Width(70)))
                                Report(gov.Donate(me, e.Id, c.Person, amount, session.NextRequestKey("donate")), "Donated " + amount + ".");
                            if (c.Person == me.CharacterId && GUILayout.Button("Buy ads", GUILayout.Width(80)))
                                Report(gov.SpendCampaign(me, e.Id, amount, session.NextRequestKey("ads")), "Ads bought.");
                            GUILayout.EndHorizontal();
                        }
                        if (!e.Candidates.Exists(c => c.Person == me.CharacterId) && GUILayout.Button("File for this office (" + new Money(Core.Simulation.CivicService.FilingFeeCents) + ")"))
                            Report(gov.FileCandidacy(me, e.Office, e.District, Slates.Independent, LocalName(), session.NextRequestKey("file")), "You're on the ballot.");
                    }
                    foreach (var e in civic.PastElections)
                        GUILayout.Label("Result: " + (e.Office == Office.Mayor ? "Mayor" : e.District + " council") + " won by " + (e.Candidates.Find(c => c.Person == e.Winner)?.Name ?? "?") + ", turnout " + e.Turnout);
                    break;
                }
                case Tab.Registration:
                {
                    GUILayout.Label(gov.RegistrationRequired
                        ? "The Anomalous Abilities Registration Ordinance is in force. Unregistered public use is an offence."
                        : "Registration is voluntary: no ordinance requires it.");
                    GUILayout.Label(gov.IsRegistered(me.CharacterId) ? "You are registered." : "You are not registered.");
                    if (!gov.IsRegistered(me.CharacterId) && GUILayout.Button("Register")) Report(gov.RegisterPowers(me), "Registered.");
                    GUILayout.Label("Public reputation " + me.Reputation.Get(ReputationDimension.Public).ToString("0.00"));
                    break;
                }
            }
            GUILayout.Label(_status);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static string LocalName() =>
            ServiceRegistry.TryGet<Core.Characters.AccountProfile>(out var a) && a != null ? a.Character.FullName : "Candidate";

        private void Report(OpResult result, string success) => _status = result.Success ? success : result.Error;

        private void OnDestroy()
        {
            if (_open) UiFocus.Release();
            if (_instance == this) _instance = null;
        }
    }
}
