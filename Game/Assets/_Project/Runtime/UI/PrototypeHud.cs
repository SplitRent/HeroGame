using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Interaction;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// Minimal gameplay HUD for the greybox slice: clock, weather, cash, interaction prompt.
    /// Placeholder (tracked in docs/ASSET_TRACKER.md) until the UI Toolkit HUD in Phase 24.
    /// </summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        public PlayerInteractor Interactor;
        private GUIStyle _style;
        private GUIStyle _prompt;
        private GUIStyle _subtitle;

        private void OnGUI()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
                _prompt = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
                _subtitle = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            }
            var w = session.World;
            var now = w.Clock.Now;
            GUI.Label(new Rect(16, 12, 600, 24), now.DayOfWeek + " " + now.Hour.ToString("00") + ":" + now.Minute.ToString("00") + " · " +
                                                 w.Weather.State.Current.Kind + " " + w.Weather.State.Current.TemperatureC.ToString("0") + "°C", _style);
            if (session.LocalCharacter != null)
                GUI.Label(new Rect(16, 36, 600, 24), w.Ledger.BalanceOf(session.LocalCharacter.CheckingAccount).ToString(), _style);
            var lines = SubtitleFeed.Current;
            for (var i = 0; i < lines.Count; i++)
                GUI.Label(new Rect(Screen.width * 0.5f - 400, Screen.height - 200 + i * 26, 800, 26), lines[i].Speaker + ": " + lines[i].Text, _subtitle);
            if (session.LocalCharacter != null)
            {
                var unread = w.Phone.UnreadCount(session.LocalCharacter);
                if (unread > 0) GUI.Label(new Rect(16, 60, 400, 24), "Phone: " + unread + " unread", _style);
            }
            if (Interactor != null && !string.IsNullOrEmpty(Interactor.PromptText))
                GUI.Box(new Rect(Screen.width * 0.5f - 260, Screen.height - 110, 520, 40), "[E] " + Interactor.PromptText, _prompt);
        }
    }
}
