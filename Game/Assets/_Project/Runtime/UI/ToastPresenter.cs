using HeroGame.Core.Characters;
using HeroGame.Core.Phone;
using HeroGame.Core.Presentation;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// On-screen notifications: phone messages to the local character (offline) and server notices (online), folded
    /// and prioritised by <see cref="ToastQueue"/>. Placeholder IMGUI (docs/ASSET_TRACKER.md).
    /// </summary>
    public sealed class ToastPresenter : MonoBehaviour
    {
        private static ToastPresenter _instance;
        private readonly ToastQueue _queue = new ToastQueue();
        private PhoneService _phone;

        /// <summary>Shows a notification from anywhere (network notices, UI results).</summary>
        public static void Push(ToastKind kind, string title, string body)
        {
            if (_instance != null) _instance._queue.Push(kind, title, body, Time.unscaledTimeAsDouble);
        }

        private void OnEnable() => _instance = this;

        private void OnDisable()
        {
            if (_instance == this) _instance = null;
            if (_phone != null) _phone.MessageReceived -= OnMessage;
            _phone = null;
        }

        private void Update()
        {
            if (_phone == null && ServiceRegistry.TryGet<GameSession>(out var session))
            {
                _phone = session.World.Phone;
                _phone.MessageReceived += OnMessage;
            }
            _queue.DurationSeconds = SettingsService.Current.NotificationSeconds;
            _queue.Update(Time.unscaledTimeAsDouble);
        }

        private void OnMessage(ServerCharacter to, PhoneMessage m)
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null || to.CharacterId != session.LocalCharacter.CharacterId) return;
            _queue.Push(ToastQueue.KindFor(m.Category, m.Body), m.FromName, m.Body, Time.unscaledTimeAsDouble);
            Audio.AudioDirector.Ui(Core.Audio.Synth.UiSound.Notification);
        }

        private void OnGUI()
        {
            var scale = SettingsService.Current.UiScale;
            var width = 340f * scale;
            var y = 12f;
            foreach (var t in _queue.Visible)
            {
                var height = 54f * scale;
                var rect = new Rect(12f, y, width, height);
                var old = GUI.color;
                GUI.color = Tint(t.Kind);
                GUI.Box(rect, GUIContent.none);
                GUI.color = old;
                GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 40, 20 * scale), Symbol(t.Kind) + " " + t.Title);
                GUI.Label(new Rect(rect.x + 8, rect.y + 22 * scale, rect.width - 16, height - 24 * scale), t.Text);
                if (GUI.Button(new Rect(rect.xMax - 26, rect.y + 4, 22, 18), "×")) _queue.Dismiss(t.Id, Time.unscaledTimeAsDouble);
                y += height + 6f;
            }
        }

        private static Color Tint(ToastKind k)
        {
            switch (k)
            {
                case ToastKind.Danger: return new Color(1f, 0.55f, 0.55f);
                case ToastKind.Warning: return new Color(1f, 0.85f, 0.5f);
                case ToastKind.Money: return new Color(0.6f, 1f, 0.7f);
                case ToastKind.Social: return new Color(0.7f, 0.8f, 1f);
                default: return Color.white;
            }
        }

        /// <summary>A shape per kind, so the colour is never the only cue (accessibility).</summary>
        private static string Symbol(ToastKind k)
        {
            switch (k)
            {
                case ToastKind.Danger: return "▲";
                case ToastKind.Warning: return "!";
                case ToastKind.Money: return "$";
                case ToastKind.Social: return "●";
                default: return "i";
            }
        }
    }
}
