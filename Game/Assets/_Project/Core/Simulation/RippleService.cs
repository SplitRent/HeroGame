using System;
using System.Collections.Generic;

namespace HeroGame.Core.Simulation
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Identity;
    using HeroGame.Core.Population;
    using HeroGame.Core.Social;
    using HeroGame.Core.World;

    /// <summary>
    /// Ripple, the in-world social platform (GDD §51). NPCs post about things that actually happened (every post
    /// traces back to a history record), players post, like, follow and read a feed, and hashtags trend. Player
    /// posts that land move public reputation a little; the network is a mirror of the server, not a chat room.
    /// </summary>
    public sealed class RippleService
    {
        public const int MaxPostLength = 280;
        public const int MaxPlayerPostsPerDay = 20;
        public const int MaxFollows = 500;

        private readonly World _w;
        private readonly Dictionary<string, List<RippleTemplate>> _templates = new Dictionary<string, List<RippleTemplate>>();
        private List<NpcRecord> _authors;
        private long _authorsDay = -1;

        public RippleService(World world)
        {
            _w = world;
            foreach (var t in world.Content.RippleTemplates)
            {
                if (!_templates.TryGetValue(t.Topic, out var list)) _templates[t.Topic] = list = new List<RippleTemplate>();
                list.Add(t);
            }
            world.History.Recorded += OnHistory;
        }

        private RippleState S => _w.Ripple;

        public static string TopicOf(HistoryCategory c)
        {
            switch (c)
            {
                case HistoryCategory.Crime: return "crime";
                case HistoryCategory.Anomaly: return "anomaly";
                case HistoryCategory.Weather:
                case HistoryCategory.Disaster: return "weather";
                case HistoryCategory.Business: return "business";
                case HistoryCategory.Economy: return "economy";
                case HistoryCategory.Politics: return "politics";
                case HistoryCategory.Community:
                case HistoryCategory.Sports: return "events";
                default: return "people";
            }
        }

        /// <summary>NPC reactions: more important events draw more posts, from people in the affected district when possible.</summary>
        private void OnHistory(HistoryRecord r)
        {
            if (r.Importance < 2 || r.Category == HistoryCategory.Server) return;
            if (!_templates.TryGetValue(TopicOf(r.Category), out var templates) || templates.Count == 0) return;
            var authors = Authors(r.District);
            if (authors.Count == 0) return;
            var count = Math.Min(4, r.Importance - 1);
            var district = _w.Geography.GetDistrict(r.District);
            var districtName = district != null ? district.Name : _w.Config.Identity.CityName;
            var weather = _w.Weather.State.Current.Kind.ToString();
            for (var i = 0; i < count; i++)
            {
                var rng = DeterministicRandom.For(_w.Seed, StableHash.Of(r.Headline), (ulong)r.Day, (ulong)(i + 1) * 0x51);
                var author = authors[rng.NextInt(0, authors.Count)];
                var template = templates[rng.NextInt(0, templates.Count)];
                if (template.Lines.Count == 0) continue;
                var text = template.Lines[rng.NextInt(0, template.Lines.Count)]
                    .Replace("{district}", districtName).Replace("{headline}", r.Headline.TrimEnd('.')).Replace("{weather}", weather);
                var post = new RipplePost
                {
                    Author = author.Id,
                    AuthorName = author.FullName,
                    At = _w.Clock.Now,
                    Text = Trim(text + " " + template.Tag),
                    Topic = template.Topic,
                    District = r.District,
                    SourceDay = r.Day,
                    Likes = rng.NextInt(0, 6 + r.Importance * 8),
                    Reposts = rng.NextInt(0, 1 + r.Importance * 2),
                };
                post.Tags.AddRange(ExtractTags(post.Text));
                S.Add(post);
            }
            _w.Dirty.Mark(SaveChunks.Social);
        }

        /// <summary>Adults who post, preferring residents of the district (cached for the day).</summary>
        private List<NpcRecord> Authors(EntityId district)
        {
            if (_authors == null || _authorsDay != _w.Today)
            {
                _authors = new List<NpcRecord>();
                foreach (var npc in _w.Population.Ordered)
                {
                    var age = npc.AgeYears(_w.Today);
                    if (npc.Alive && age >= 16 && age < 75) _authors.Add(npc);
                }
                _authorsDay = _w.Today;
            }
            if (!district.IsValid) return _authors;
            var local = new List<NpcRecord>();
            foreach (var npc in _authors)
            {
                var home = _w.Geography.GetPlace(npc.Home);
                if (home != null && home.District == district) local.Add(npc);
            }
            return local.Count > 0 ? local : _authors;
        }

        // ------------------------------------------------------------------ players

        public OpResult Post(ServerCharacter c, string displayName, string text, out RipplePost post)
        {
            post = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) return OpResult.Fail("Write something first.");
            if (text.Length > MaxPostLength) return OpResult.Fail("Posts are limited to " + MaxPostLength + " characters.");
            var today = 0;
            foreach (var p in S.Posts)
                if (p.AuthorIsPlayer && p.Author == c.CharacterId && p.At.DayIndex == _w.Today) today++;
            if (today >= MaxPlayerPostsPerDay) return OpResult.Fail("You've posted enough for one day.");
            var district = DistrictAt(c.LastPosition);
            post = new RipplePost
            {
                Author = c.CharacterId,
                AuthorName = string.IsNullOrWhiteSpace(displayName) ? "Anonymous" : displayName,
                AuthorIsPlayer = true,
                At = _w.Clock.Now,
                Text = text,
                Topic = "player",
                District = district,
                SourceDay = _w.Today,
            };
            post.Tags.AddRange(ExtractTags(text));
            S.Add(post);
            _w.Dirty.Mark(SaveChunks.Social);
            return OpResult.Ok();
        }

        public OpResult Like(ServerCharacter c, long postId)
        {
            var post = Find(postId);
            if (post == null) return OpResult.Fail("That post is gone.");
            if (post.LikedBy.Contains(c.CharacterId)) return OpResult.Fail("Already liked.");
            post.LikedBy.Add(c.CharacterId);
            post.Likes++;
            _w.Dirty.Mark(SaveChunks.Social);
            return OpResult.Ok();
        }

        public OpResult Follow(ServerCharacter c, EntityId target, bool follow = true)
        {
            if (!target.IsValid || target == c.CharacterId) return OpResult.Fail("You can't follow that account.");
            if (!_w.Characters.ContainsKey(target) && _w.Population.Get(target) == null) return OpResult.Fail("No such account.");
            var key = c.CharacterId.ToString();
            if (!S.Following.TryGetValue(key, out var list)) S.Following[key] = list = new List<EntityId>();
            if (follow && !list.Contains(target) && list.Count >= MaxFollows) return OpResult.Fail("You follow " + MaxFollows + " accounts already.");
            if (follow && !list.Contains(target)) list.Add(target);
            if (!follow) list.Remove(target);
            _w.Dirty.Mark(SaveChunks.Social);
            return OpResult.Ok();
        }

        public int FollowerCount(EntityId who)
        {
            var n = 0;
            foreach (var kv in S.Following) if (kv.Value.Contains(who)) n++;
            return n;
        }

        public RipplePost Find(long id)
        {
            // Ids are increasing: binary search.
            int lo = 0, hi = S.Posts.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var p = S.Posts[mid];
                if (p.Id == id) return p;
                if (p.Id < id) lo = mid + 1;
                else hi = mid - 1;
            }
            return null;
        }

        /// <summary>
        /// A character's feed, newest first: posts from accounts they follow and their own, then the rest of the
        /// city's recent posts ranked by engagement and recency. Optional tag filter ("#ArdenVotes").
        /// </summary>
        public List<RipplePost> Feed(EntityId reader, int count, string tag = null)
        {
            var follows = S.FollowsOf(reader);
            var followed = new List<RipplePost>();
            var rest = new List<RipplePost>();
            var now = _w.Clock.Now.TotalSeconds;
            for (var i = S.Posts.Count - 1; i >= 0; i--)
            {
                var p = S.Posts[i];
                if (!string.IsNullOrEmpty(tag) && !p.Tags.Exists(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) continue;
                if (p.Author == reader || follows.Contains(p.Author)) followed.Add(p);
                else if (now - p.At.TotalSeconds < 3L * 86400) rest.Add(p);
            }
            rest.Sort((a, b) =>
            {
                var sa = Score(a, now);
                var sb = Score(b, now);
                return sa != sb ? sb.CompareTo(sa) : b.Id.CompareTo(a.Id);
            });
            var result = new List<RipplePost>();
            foreach (var p in followed) { if (result.Count >= count) break; result.Add(p); }
            foreach (var p in rest) { if (result.Count >= count) break; result.Add(p); }
            return result;
        }

        private static double Score(RipplePost p, long now)
        {
            var ageHours = Math.Max(0, now - p.At.TotalSeconds) / 3600.0;
            return (1 + p.Likes + p.Reposts * 3) / Math.Pow(ageHours + 2, 1.3);
        }

        /// <summary>Most-used hashtags in the last <paramref name="days"/> days.</summary>
        public List<(string tag, int count)> Trending(int days = 2, int top = 5)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var since = _w.Today - days;
            foreach (var p in S.Posts)
            {
                if (p.At.DayIndex < since) continue;
                foreach (var t in p.Tags) counts[t] = (counts.TryGetValue(t, out var n) ? n : 0) + 1 + p.Reposts;
            }
            var list = new List<(string tag, int count)>();
            foreach (var kv in counts) list.Add((kv.Key, kv.Value));
            list.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : string.CompareOrdinal(a.tag, b.tag));
            if (list.Count > top) list.RemoveRange(top, list.Count - top);
            return list;
        }

        /// <summary>
        /// End of day: NPCs engage with yesterday's player posts. Reach grows with followers and public reputation;
        /// a post that takes off nudges the author's public standing.
        /// </summary>
        public void ProcessDay(long day)
        {
            foreach (var p in S.Posts)
            {
                if (!p.AuthorIsPlayer || p.At.DayIndex != day) continue;
                if (!_w.Characters.TryGetValue(p.Author, out var c)) continue;
                var reach = 3 + FollowerCount(c.CharacterId) * 2 + Math.Max(0f, c.Reputation.Get(ReputationDimension.Public)) * 40;
                var rng = DeterministicRandom.For(_w.Seed, 0x219913, (ulong)p.Id, (ulong)day);
                var likes = (int)(reach * (0.2 + rng.NextDouble() * (p.Tags.Count > 0 ? 1.2 : 0.8)));
                p.Likes += likes;
                p.Reposts += likes / 8;
                if (likes >= 25) c.Reputation.Add(ReputationDimension.Public, 0.01f);
            }
            _w.Dirty.Mark(SaveChunks.Social);
        }

        public static List<string> ExtractTags(string text)
        {
            var tags = new List<string>();
            var i = 0;
            while ((i = text.IndexOf('#', i)) >= 0)
            {
                var j = i + 1;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                if (j - i > 1)
                {
                    var tag = text.Substring(i, j - i);
                    if (!tags.Contains(tag)) tags.Add(tag);
                }
                i = j;
            }
            return tags;
        }

        private EntityId DistrictAt(WorldPosition p)
        {
            var best = EntityId.None;
            var bestD = float.MaxValue;
            foreach (var d in _w.Geography.Districts)
            {
                var dist = WorldPosition.DistanceXZ(d.Center, p) / Math.Max(1f, d.Radius);
                if (dist < bestD || dist == bestD && d.Id.CompareTo(best) < 0) { bestD = dist; best = d.Id; }
            }
            return best;
        }

        private static string Trim(string text) => text.Length <= MaxPostLength ? text : text.Substring(0, MaxPostLength);
    }
}
