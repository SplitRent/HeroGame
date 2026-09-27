#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.Population;
using UnityEngine;

namespace HeroGame.Runtime.DevTools
{
    /// <summary>
    /// World / NPC / server inspector overlay (GDD §151–153), toggled with F3. Shows live world state,
    /// economy, weather, events, player state and — for the nearest materialised NPC — their full
    /// persistent record. Development builds only.
    /// </summary>
    public sealed class WorldInspectorOverlay : MonoBehaviour
    {
        public Transform Player;
        public NpcPopulationPresenter Population;
        public float RefreshInterval = 0.25f;

        private IPlayerInputSource _input;
        private bool _visible;
        private float _timer;
        private string _world = "";
        private string _npc = "";
        private GUIStyle _style;

        private void Start() => _input = PlayerInputRegistry.Create();

        private void Update()
        {
            if (_input != null && _input.Read().ToggleInspectorPressed) _visible = !_visible;
            if (!_visible) return;
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            _timer = RefreshInterval;
            _world = DescribeWorld(session);
            _npc = DescribeNearestNpc(session);
        }

        private void OnGUI()
        {
            if (!_visible) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 12, wordWrap = true };
            GUI.Box(new Rect(Screen.width - 430, 10, 420, 330), _world, _style);
            if (!string.IsNullOrEmpty(_npc)) GUI.Box(new Rect(Screen.width - 430, 350, 420, 300), _npc, _style);
        }

        private string DescribeWorld(GameSession session)
        {
            var w = session.World;
            var s = w.Weather.State.Current;
            var e = w.Weather.Effects;
            var sb = new StringBuilder();
            sb.AppendLine("WORLD  " + w.Config.Identity.CityName + " · " + w.ServerId + " · " + session.Mode);
            sb.AppendLine("Time   " + w.Clock.Now + " " + w.Clock.Now.DayOfWeek + " ×" + w.Clock.TimeScale.ToString("0"));
            sb.AppendLine("Weather " + s.Kind + " " + s.TemperatureC.ToString("0") + "°C wind " + s.WindSpeedMs.ToString("0") + " m/s vis " + e.VisibilityMetres.ToString("0") + " m flood " + (s.FloodLevel * 100).ToString("0") + "%");
            if (w.Weather.State.ActiveSystem != null) sb.AppendLine("Tropical system " + w.Weather.State.ActiveSystem.Name + " cat " + w.Weather.State.ActiveSystem.Category.ToString("0.0"));
            sb.AppendLine("Economy cycle " + w.Macro.CycleIndex.ToString("0.000") + " infl " + (w.Macro.AnnualInflation * 100).ToString("0.0") + "% unemp " + (w.Macro.UnemploymentRate * 100).ToString("0.0") + "%" + (w.Macro.InRecession ? " RECESSION" : ""));
            sb.AppendLine("Treasury " + w.Ledger.BalanceOf(w.Accounts.Treasury) + " · ledger " + (w.Ledger.VerifyInvariant(out _) ? "balanced" : "BROKEN"));
            sb.AppendLine("Population " + w.Population.Count + " · materialised " + (Population != null ? Population.ActiveCount : 0) + " · businesses " + w.Businesses.Count);
            sb.AppendLine("Anomalies " + w.AnomalyLog.Count + (w.Cursor.PendingAnomaly != null ? " (one scheduled " + w.Cursor.PendingAnomaly.OccurredAt + ")" : ""));
            sb.AppendLine("Sim last day " + session.Simulation.Stats.LastDayMilliseconds.ToString("0.0") + " ms · journal seq " + w.Transactions.LastSequence);
            var c = session.LocalCharacter;
            if (c != null)
            {
                sb.AppendLine("PLAYER " + c.CharacterId + " cash " + w.Ledger.BalanceOf(c.CheckingAccount) + " · assets " + w.Ownership.AssetsOf(c.CharacterId).Count);
                var wanted = w.Wanted.Get(c.CharacterId);
                sb.AppendLine("Wanted " + (wanted == null ? "none" : wanted.Phase + " L" + wanted.Level));
                foreach (var p in c.Powers.Powers) sb.AppendLine("Power " + p.Definition.Describe() + " — " + p.Stage + " xp " + p.Progress.Experience.ToString("0"));
            }
            // Active events: calendar, disasters, emergencies, manhunts, elections, broken street furniture.
            foreach (var ev in w.Calendar.ActiveOn(w.Clock.Now)) sb.AppendLine("EVENT " + ev.Name);
            foreach (var d in w.Disasters.Active) sb.AppendLine("DISASTER " + d.Headline);
            var open = 0;
            foreach (var i in w.Emergency.Incidents) if (i.Open) open++;
            var busy = 0;
            foreach (var u in w.Emergency.Units) if (!u.Free) busy++;
            sb.AppendLine("Emergency " + open + " open · units busy " + busy + "/" + w.Emergency.Units.Count + " · manhunts " + w.Wanted.Snapshot().Count +
                          " · elections " + w.Civic.Elections.Count + " · broken props " + CountBroken(w));
            var online = Online.NetworkSession.Current;
            sb.AppendLine("NETWORK " + (online == null ? "offline" : online.State + (string.IsNullOrEmpty(online.Status) ? "" : " · " + online.Status)));
            var recent = w.History.Recent;
            for (var i = System.Math.Max(0, recent.Count - 4); i < recent.Count; i++) sb.AppendLine("• " + recent[i].Headline);
            return sb.ToString();
        }

        private static int CountBroken(Core.Simulation.World w)
        {
            var n = 0;
            foreach (var p in w.Destructibles.All) if (p.State != Core.World.PropState.Intact) n++;
            return n;
        }

        private string DescribeNearestNpc(GameSession session)
        {
            if (Population == null || Player == null) return "";
            NpcAvatar nearest = null;
            var best = 20f * 20f;
            foreach (var a in Population.Active)
            {
                var d = (a.transform.position - Player.position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    nearest = a;
                }
            }
            if (nearest == null) return "";
            var n = session.World.Population.Get(nearest.NpcId);
            if (n == null) return "";
            var w = session.World;
            var sb = new StringBuilder();
            var occ = w.Occupations.Get(n.OccupationId);
            sb.AppendLine("NPC  " + n.FullName + " (" + n.Id + ") · " + nearest.Tier);
            sb.AppendLine("Age " + n.AgeYears(w.Today) + " · " + n.Presentation + " · " + n.Education);
            sb.AppendLine("Home " + (w.Geography.GetPlace(n.Home)?.Name ?? "-"));
            sb.AppendLine("Job  " + (occ != null ? occ.Title + " @ " + (w.Geography.GetPlace(n.Workplace)?.Name ?? "?") + " " + new Money(n.AnnualSalaryCents) + "/yr" : n.Employment.ToString()));
            sb.AppendLine("Now  " + nearest.Activity);
            sb.AppendLine("Money savings " + new Money(n.SavingsCents) + " debt " + new Money(n.DebtCents));
            var shown = 0;
            foreach (var r in n.Relationships)
            {
                if (r.Type == RelationshipType.Acquaintance || shown++ >= 6) continue;
                sb.AppendLine("  " + r.Type + ": " + (w.Population.Get(r.Other)?.FullName ?? r.Other.ToString()));
            }
            for (var i = System.Math.Max(0, n.History.Count - 4); i < n.History.Count; i++) sb.AppendLine("  day " + n.History[i].Day + ": " + n.History[i].Summary);
            return sb.ToString();
        }
    }
}
#endif
