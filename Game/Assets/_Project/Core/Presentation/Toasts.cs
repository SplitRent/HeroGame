using System;
using System.Collections.Generic;

namespace HeroGame.Core.Presentation
{
    public enum ToastKind { Info, Money, Warning, Danger, Social }

    public sealed class Toast
    {
        public long Id;
        public ToastKind Kind;
        public string Title = "";
        public string Body = "";
        /// <summary>How many identical toasts were folded into this one ("×3").</summary>
        public int Count = 1;
        public double ShownAt;
        public double ExpiresAt;

        public string Text => Count > 1 ? Body + "  ×" + Count : Body;
    }

    /// <summary>
    /// On-screen notifications (GDD Phase 24). Identical toasts arriving close together fold into one with a counter
    /// instead of flooding the screen; at most <see cref="MaxVisible"/> show at once, dangers first, and the rest wait.
    /// Time is whatever clock the presenter passes in (real seconds), so this is engine-free and testable.
    /// </summary>
    public sealed class ToastQueue
    {
        public const int MaxVisible = 4;
        public const int MaxWaiting = 32;
        /// <summary>Repeats of the same toast within this many seconds fold together.</summary>
        public const double FoldSeconds = 10.0;

        private readonly List<Toast> _visible = new List<Toast>();
        private readonly List<Toast> _waiting = new List<Toast>();
        private long _nextId;

        public double DurationSeconds { get; set; } = 5.0;
        public IReadOnlyList<Toast> Visible => _visible;
        public int WaitingCount => _waiting.Count;

        public Toast Push(ToastKind kind, string title, string body, double now)
        {
            title = title ?? "";
            body = body ?? "";
            foreach (var list in new[] { _visible, _waiting })
                foreach (var t in list)
                    if (t.Kind == kind && t.Title == title && t.Body == body && now - t.ShownAt < FoldSeconds + DurationSeconds)
                    {
                        t.Count++;
                        if (list == _visible) t.ExpiresAt = now + DurationSeconds; // keep it up while it repeats
                        return t;
                    }

            var toast = new Toast { Id = ++_nextId, Kind = kind, Title = title, Body = body, ShownAt = now, ExpiresAt = now + DurationSeconds };
            if (_visible.Count < MaxVisible) _visible.Add(toast);
            else
            {
                _waiting.Add(toast);
                // Dangers jump the queue; when it overflows the oldest low-priority toast is dropped.
                _waiting.Sort((a, b) => a.Kind == b.Kind ? a.Id.CompareTo(b.Id) : Priority(b.Kind).CompareTo(Priority(a.Kind)));
                if (_waiting.Count > MaxWaiting) _waiting.RemoveAt(_waiting.Count - 1);
            }
            return toast;
        }

        /// <summary>Expires old toasts and promotes waiting ones; call every frame (or a few times a second).</summary>
        public void Update(double now)
        {
            _visible.RemoveAll(t => t.ExpiresAt <= now);
            while (_visible.Count < MaxVisible && _waiting.Count > 0)
            {
                var t = _waiting[0];
                _waiting.RemoveAt(0);
                t.ShownAt = now;
                t.ExpiresAt = now + DurationSeconds;
                _visible.Add(t);
            }
        }

        public void Dismiss(long id, double now)
        {
            _visible.RemoveAll(t => t.Id == id);
            Update(now);
        }

        public void Clear()
        {
            _visible.Clear();
            _waiting.Clear();
        }

        private static int Priority(ToastKind k)
        {
            switch (k)
            {
                case ToastKind.Danger: return 3;
                case ToastKind.Warning: return 2;
                case ToastKind.Money: return 1;
                default: return 0;
            }
        }

        /// <summary>Which kind of toast a phone message becomes.</summary>
        public static ToastKind KindFor(Phone.MessageCategory category, string body)
        {
            switch (category)
            {
                case Phone.MessageCategory.Emergency: return ToastKind.Danger;
                case Phone.MessageCategory.Bank:
                    return body != null && (body.IndexOf("overdue", StringComparison.OrdinalIgnoreCase) >= 0 || body.IndexOf("declined", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            body.IndexOf("missed", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? ToastKind.Warning
                        : ToastKind.Money;
                case Phone.MessageCategory.Personal: return ToastKind.Social;
                default: return ToastKind.Info;
            }
        }
    }
}
