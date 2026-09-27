using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>Spoken-line queue shown by the HUD (subtitles are on by default; GDD accessibility).</summary>
    public static class SubtitleFeed
    {
        public struct Entry
        {
            public string Speaker;
            public string Text;
            public float ExpiresAt;
        }

        private static readonly List<Entry> Lines = new List<Entry>();
        public const int MaxLines = 3;

        public static void Say(string speaker, string text, float seconds = 4f)
        {
            if (string.IsNullOrEmpty(text)) return;
            Lines.Add(new Entry { Speaker = speaker, Text = text, ExpiresAt = Time.unscaledTime + seconds + text.Length * 0.03f });
            if (!string.IsNullOrEmpty(speaker)) Audio.AudioDirector.Voice(speaker, text);
            while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }

        public static IReadOnlyList<Entry> Current
        {
            get
            {
                Lines.RemoveAll(e => e.ExpiresAt < Time.unscaledTime);
                return Lines;
            }
        }
    }
}
