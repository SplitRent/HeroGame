using System;
using System.Collections.Generic;

namespace HeroGame.Core.World
{
    using HeroGame.Core.Foundation;

    public enum HistoryCategory
    {
        Crime,
        Business,
        Politics,
        Weather,
        Disaster,
        Anomaly,
        People,
        Economy,
        Sports,
        Community,
        Server,
    }

    [Serializable]
    public sealed class HistoryRecord
    {
        public long Day;
        public HistoryCategory Category;
        /// <summary>1 (minor) .. 5 (defines the server's history).</summary>
        public int Importance = 1;
        public string Headline = "";
        public string Detail = "";
        public List<EntityId> Subjects = new List<EntityId>();
        public EntityId District;
    }

    /// <summary>
    /// The server's memory (GDD §99). Major events (importance ≥ 3) are kept forever; minor ones in a
    /// rolling window. News, NPC dialogue and the server inspector read from here.
    /// </summary>
    public sealed class ServerHistory
    {
        public const int MajorThreshold = 3;
        public int RecentCapacity = 500;

        public readonly List<HistoryRecord> Major = new List<HistoryRecord>();
        public readonly List<HistoryRecord> Recent = new List<HistoryRecord>();

        public event Action<HistoryRecord> Recorded;

        public HistoryRecord Record(long day, HistoryCategory category, int importance, string headline, string detail = "", EntityId district = default, params EntityId[] subjects)
        {
            var r = new HistoryRecord { Day = day, Category = category, Importance = importance, Headline = headline, Detail = detail ?? "", District = district };
            if (subjects != null) r.Subjects.AddRange(subjects);
            if (importance >= MajorThreshold) Major.Add(r);
            Recent.Add(r);
            if (Recent.Count > RecentCapacity) Recent.RemoveAt(0);
            Recorded?.Invoke(r);
            return r;
        }

        public List<HistoryRecord> Since(long day, int minImportance = 1)
        {
            var result = new List<HistoryRecord>();
            foreach (var r in Recent) if (r.Day >= day && r.Importance >= minImportance) result.Add(r);
            return result;
        }
    }

    [Serializable]
    public sealed class NewsArticle
    {
        public long Day;
        public string Outlet = "";
        public string Headline = "";
        public string Body = "";
        public HistoryCategory Category;
        public int Prominence;
    }

    /// <summary>
    /// Turns real world data into local news (GDD §52). No fabricated stories: every article is
    /// backed by a history record or live system state.
    /// </summary>
    public static class NewsDesk
    {
        public static List<NewsArticle> Edition(ServerHistory history, long day, IReadOnlyList<string> outlets, int maxArticles = 8)
        {
            var items = history.Since(day - 1, 1);
            items.Sort((a, b) => b.Importance != a.Importance ? b.Importance.CompareTo(a.Importance) : b.Day.CompareTo(a.Day));
            var result = new List<NewsArticle>();
            for (var i = 0; i < items.Count && result.Count < maxArticles; i++)
            {
                var r = items[i];
                if (r.Importance < 2 && r.Category == HistoryCategory.People) continue; // personal life is not news
                result.Add(new NewsArticle
                {
                    Day = r.Day,
                    Outlet = outlets != null && outlets.Count > 0 ? outlets[(int)(StableHash.Of(r.Headline) % (ulong)outlets.Count)] : "Local News",
                    Headline = r.Headline,
                    Body = string.IsNullOrEmpty(r.Detail) ? r.Headline : r.Detail,
                    Category = r.Category,
                    Prominence = r.Importance,
                });
            }
            return result;
        }
    }
}
