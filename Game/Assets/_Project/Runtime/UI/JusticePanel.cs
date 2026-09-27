using HeroGame.Core.Crime;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// Police front desk / court clerk (GDD §32): your record, the open case, bail, counsel, plea, fines and
    /// surrender. Buttons call <see cref="Core.Simulation.JusticeService"/>. Placeholder IMGUI (ASSET_TRACKER).
    /// </summary>
    public sealed class JusticePanel : MonoBehaviour
    {
        private static JusticePanel _instance;
        private bool _open;
        private string _status = "";

        public static void Open()
        {
            if (_instance == null) _instance = new GameObject("Justice Panel").AddComponent<JusticePanel>();
            if (!_instance._open) UiFocus.Acquire();
            _instance._open = true;
            _instance._status = "";
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
            var r = me.Record;
            GUILayout.BeginArea(new Rect(Screen.width * 0.5f - 280, 60, 560, 480), GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Police & courts");
            if (GUILayout.Button("Close", GUILayout.Width(70))) Close();
            GUILayout.EndHorizontal();
            GUILayout.Label("Arrests " + r.Arrests + " · convictions " + r.Convictions + (r.ActiveWarrant ? " · ACTIVE WARRANT" : "")
                            + (r.OnProbation(w.Clock.Now) ? " · on probation until day " + r.ProbationUntil.DayIndex : ""));
            if (r.InCustody) GUILayout.Label(r.ServingSentence ? "Serving a sentence until day " + r.CustodyUntilDay + "." : "In custody awaiting your hearing.");

            var court = w.Justice.OpenCaseFor(me.CharacterId);
            if (court != null)
            {
                GUILayout.Label(court.Summary);
                GUILayout.Label("Counsel: " + court.Counsel + " · evidence strength " + (int)(court.EvidenceStrength * 100) + "%" + (court.PleadedGuilty ? " · guilty plea entered" : ""));
                GUILayout.BeginHorizontal();
                if (court.Stage == CaseStage.Charged && court.BailCents > 0 && GUILayout.Button("Post bail " + new Money(court.BailCents)))
                    Report(w.Courts.PostBail(me, session.NextRequestKey("bail")), "Bail posted.");
                if (court.Counsel == Counsel.PublicDefender && GUILayout.Button("Hire an attorney"))
                    Report(w.Courts.HireAttorney(me, session.NextRequestKey("attorney")), "Your attorney will argue the case.");
                if (court.PleaOffered && !court.PleadedGuilty && GUILayout.Button("Accept the plea deal"))
                    Report(w.Courts.AcceptPlea(me), "Plea entered.");
                GUILayout.EndHorizontal();
            }
            if (r.FinesOwedCents > 0)
            {
                GUILayout.Label("Fines owed: " + new Money(r.FinesOwedCents) + (r.FinesDueDay >= 0 ? ", due day " + r.FinesDueDay : ""));
                if (GUILayout.Button("Pay fines")) Report(w.Courts.PayFines(me, new Money(r.FinesOwedCents), session.NextRequestKey("fines")), "Fines paid.");
            }
            if (!r.InCustody && (r.ActiveWarrant || w.Wanted.Get(me.CharacterId) != null) && GUILayout.Button("Turn yourself in"))
            {
                var result = w.Courts.TurnSelfIn(me);
                _status = result.Success ? "You are booked. Cooperation earns you a plea offer." : result.Error;
            }
            GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        private void Report(OpResult result, string success) => _status = result.Success ? success : result.Error;

        private void OnDestroy()
        {
            if (_open) UiFocus.Release();
            if (_instance == this) _instance = null;
        }
    }
}
