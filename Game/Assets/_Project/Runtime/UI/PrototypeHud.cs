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

        private void OnGUI()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
                _prompt = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            }
            var w = session.World;
            var now = w.Clock.Now;
            GUI.Label(new Rect(16, 12, 600, 24), now.DayOfWeek + " " + now.Hour.ToString("00") + ":" + now.Minute.ToString("00") + " · " +
                                                 w.Weather.State.Current.Kind + " " + w.Weather.State.Current.TemperatureC.ToString("0") + "°C", _style);
            if (session.LocalCharacter != null)
                GUI.Label(new Rect(16, 36, 600, 24), w.Ledger.BalanceOf(session.LocalCharacter.CheckingAccount).ToString(), _style);
            if (Interactor != null && !string.IsNullOrEmpty(Interactor.PromptText))
                GUI.Box(new Rect(Screen.width * 0.5f - 260, Screen.height - 110, 520, 40), "[E] " + Interactor.PromptText, _prompt);
        }
    }
}
