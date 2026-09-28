using System;
using System.Collections.Generic;
using System.Text;

namespace HeroGame.Core.Social
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;
    using HeroGame.Core.Time;
    using HeroGame.Core.Weather;

    /// <summary>How an NPC regards a specific player character, derived from memory.</summary>
    public enum Familiarity
    {
        Stranger,
        Recognized,
        Acquaintance,
        Friend,
        Close,
        Wary,
        Hostile,
    }

    /// <summary>
    /// One contextual line (GDD §92). Every non-empty condition must match. More matched conditions = more
    /// specific = preferred, so a line about "rain + friend + evening" beats a generic greeting when it applies.
    /// </summary>
    [Serializable]
    public sealed class BarkLine
    {
        public string Id = "";
        /// <summary>What prompted the line: greet, farewell, weather, gossip, crime_reaction, work, idle, anomaly, economy, sports.</summary>
        public string Topic = "greet";
        public string Text = "";
        public float Weight = 1f;
        public List<Familiarity> Familiarity = new List<Familiarity>();
        public List<DayPart> DayParts = new List<DayPart>();
        public List<WeatherKind> Weather = new List<WeatherKind>();
        /// <summary>Required memory flags (all).</summary>
        public MemoryFlags RequiresFlags;
        public List<string> OccupationCategories = new List<string>();
        /// <summary>Place kinds where the NPC is when speaking (as strings, e.g. "Shop").</summary>
        public List<string> PlaceKinds = new List<string>();
        /// <summary>World tags: "recession", "storm_coming", "anomaly_recent", "heat", "flooding", "player_wanted", "player_notorious".</summary>
        public List<string> WorldTags = new List<string>();
        public float MinAffinity = -1f;
        public float MaxAffinity = 1f;
    }

    /// <summary>Everything the selector knows at the moment an NPC speaks.</summary>
    public sealed class BarkContext
    {
        public string Topic = "greet";
        public Familiarity Familiarity;
        public float Affinity;
        public MemoryFlags Flags;
        public DayPart DayPart;
        public WeatherKind Weather;
        public string OccupationCategory = "";
        public string PlaceKind = "";
        public HashSet<string> WorldTags = new HashSet<string>();
        public Dictionary<string, string> Tokens = new Dictionary<string, string>();
    }

    public struct BarkResult
    {
        public BarkLine Line;
        public string Text;
        public int Specificity;
    }

    /// <summary>
    /// Picks the most specific applicable line, weighted-random among ties, avoiding lines the speaker used
    /// recently. Deterministic for a given RNG.
    /// </summary>
    public sealed class BarkSelector
    {
        private readonly Dictionary<string, List<BarkLine>> _byTopic = new Dictionary<string, List<BarkLine>>();

        public BarkSelector(IEnumerable<BarkLine> lines)
        {
            foreach (var l in lines)
            {
                if (!_byTopic.TryGetValue(l.Topic, out var list)) _byTopic[l.Topic] = list = new List<BarkLine>();
                list.Add(l);
            }
        }

        public bool TrySelect(BarkContext ctx, DeterministicRandom rng, ICollection<string> recentlyUsed, out BarkResult result)
        {
            result = default;
            if (!_byTopic.TryGetValue(ctx.Topic, out var lines)) return false;
            var best = -1;
            var candidates = new List<BarkLine>();
            var weights = new List<double>();
            for (var pass = 0; pass < 2 && candidates.Count == 0; pass++)
            {
                best = -1;
                foreach (var l in lines)
                {
                    // First pass skips recently used lines; second pass allows them rather than going silent.
                    if (pass == 0 && recentlyUsed != null && recentlyUsed.Contains(l.Id)) continue;
                    var score = Match(l, ctx);
                    if (score < 0) continue;
                    if (score > best)
                    {
                        best = score;
                        candidates.Clear();
                        weights.Clear();
                    }
                    if (score == best)
                    {
                        candidates.Add(l);
                        weights.Add(l.Weight);
                    }
                }
            }
            if (candidates.Count == 0) return false;
            var index = rng.PickWeighted(weights);
            var line = candidates[Math.Max(0, index)];
            result = new BarkResult { Line = line, Text = Fill(line.Text, ctx.Tokens), Specificity = best };
            return true;
        }

        /// <summary>Returns -1 if the line does not apply, otherwise the number of conditions it matched.</summary>
        public static int Match(BarkLine l, BarkContext c)
        {
            var score = 0;
            if (l.Familiarity.Count > 0) { if (!l.Familiarity.Contains(c.Familiarity)) return -1; score++; }
            if (l.DayParts.Count > 0) { if (!l.DayParts.Contains(c.DayPart)) return -1; score++; }
            if (l.Weather.Count > 0) { if (!l.Weather.Contains(c.Weather)) return -1; score++; }
            if (l.RequiresFlags != MemoryFlags.None) { if ((c.Flags & l.RequiresFlags) != l.RequiresFlags) return -1; score += 3; } // memories of the player outrank setting
            if (l.OccupationCategories.Count > 0) { if (!l.OccupationCategories.Contains(c.OccupationCategory)) return -1; score++; }
            if (l.PlaceKinds.Count > 0) { if (!l.PlaceKinds.Contains(c.PlaceKind)) return -1; score++; }
            foreach (var tag in l.WorldTags) { if (!c.WorldTags.Contains(tag)) return -1; score += 2; }
            if (c.Affinity < l.MinAffinity || c.Affinity > l.MaxAffinity) return -1;
            return score;
        }

        /// <summary>Replaces {token} markers; unknown tokens are left visible so content bugs are noticed.</summary>
        public static string Fill(string text, Dictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
            var sb = new StringBuilder(text.Length + 16);
            var i = 0;
            while (i < text.Length)
            {
                var open = text.IndexOf('{', i);
                if (open < 0) { sb.Append(text, i, text.Length - i); break; }
                var close = text.IndexOf('}', open);
                if (close < 0) { sb.Append(text, i, text.Length - i); break; }
                sb.Append(text, i, open - i);
                var key = text.Substring(open + 1, close - open - 1);
                sb.Append(tokens != null && tokens.TryGetValue(key, out var value) ? value : "{" + key + "}");
                i = close + 1;
            }
            return sb.ToString();
        }

        public static Familiarity FamiliarityOf(CharacterMemory memory)
        {
            if (memory == null) return Familiarity.Stranger;
            if (memory.Fear > 0.5f || memory.Trust < -0.3f) return Familiarity.Wary; // even strangers who saw something
            if (memory.Interactions == 0) return Familiarity.Stranger;
            if (memory.Affinity <= -0.5f || ((memory.Flags & MemoryFlags.HadConflict) != 0 && memory.Affinity < -0.2f)) return Familiarity.Hostile;
            if (memory.Fear > 0.5f || memory.Trust < -0.3f) return Familiarity.Wary;
            if (memory.Affinity >= 0.7f && memory.Interactions >= 12) return Familiarity.Close;
            if (memory.Affinity >= 0.35f && memory.Interactions >= 5) return Familiarity.Friend;
            if (memory.Interactions >= 3) return Familiarity.Acquaintance;
            return Familiarity.Recognized;
        }
    }
}
