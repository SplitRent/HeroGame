using System;
using System.Collections.Generic;

namespace HeroGame.Core.Servers
{
    public enum ServerKind
    {
        Official,
        Community,
        Private,
    }

    public enum EconomyStyle
    {
        Canonical,
        Hardcore,
        Relaxed,
        Custom,
    }

    public enum PowerFrequency
    {
        None,
        Rare,
        Canonical,
        Frequent,
    }

    /// <summary>What the master list advertises about a server (GDD §7).</summary>
    [Serializable]
    public sealed class ServerListing
    {
        public string ServerId = "";
        public string Name = "";
        public string Description = "";
        public string CityName = "";
        public string Owner = "";
        public string Region = "";
        public int Population;
        public int MaxPopulation;
        public int PingMs;
        public ServerKind Kind;
        public bool Roleplay;
        public bool Pvp = true;
        public bool Hardcore;
        public PowerFrequency Powers = PowerFrequency.Canonical;
        public EconomyStyle Economy;
        public string Difficulty = "Normal";
        public string Weather = "Dynamic";
        public string AgeRating = "M";
        public bool Moderated = true;
        public bool Whitelisted;
        public bool PasswordProtected;
        public string Version = "";
        public List<string> Rules = new List<string>();
        public List<string> FriendsOnline = new List<string>();
        public List<string> Tags = new List<string>();
    }

    [Serializable]
    public sealed class ServerFilter
    {
        public string Search = "";
        public string Region = "";
        public bool FriendsOnly;
        public bool HideFull;
        public bool HideEmpty;
        public int MaxPingMs;
        public bool? Roleplay;
        public bool? Pvp;
        public bool? Hardcore;
        public bool? PowersEnabled;
        public EconomyStyle? Economy;
        public ServerKind? Kind;
        public bool HidePasswordProtected;
        public bool HideWhitelisted;
        public bool CompatibleOnly = true;
    }

    public enum ServerSort
    {
        Population,
        Ping,
        Name,
        Friends,
    }

    /// <summary>Pure filtering/sorting logic for the server browser UI (unit tested, UI agnostic).</summary>
    public static class ServerBrowser
    {
        public static bool IsCompatible(ServerListing s, string clientVersion)
        {
            if (string.IsNullOrEmpty(clientVersion) || string.IsNullOrEmpty(s.Version)) return true;
            // Compatible when major.minor match (protocol changes bump minor).
            return MajorMinor(s.Version) == MajorMinor(clientVersion);
        }

        public static List<ServerListing> Apply(IEnumerable<ServerListing> servers, ServerFilter f, ServerSort sort, string clientVersion)
        {
            var result = new List<ServerListing>();
            foreach (var s in servers)
            {
                if (!string.IsNullOrEmpty(f.Search))
                {
                    var q = f.Search.ToLowerInvariant();
                    var hay = (s.Name + " " + s.Description + " " + s.CityName + " " + string.Join(" ", s.Tags)).ToLowerInvariant();
                    if (!hay.Contains(q)) continue;
                }
                if (!string.IsNullOrEmpty(f.Region) && !string.Equals(f.Region, s.Region, StringComparison.OrdinalIgnoreCase)) continue;
                if (f.FriendsOnly && s.FriendsOnline.Count == 0) continue;
                if (f.HideFull && s.Population >= s.MaxPopulation) continue;
                if (f.HideEmpty && s.Population == 0) continue;
                if (f.MaxPingMs > 0 && s.PingMs > f.MaxPingMs) continue;
                if (f.Roleplay.HasValue && s.Roleplay != f.Roleplay.Value) continue;
                if (f.Pvp.HasValue && s.Pvp != f.Pvp.Value) continue;
                if (f.Hardcore.HasValue && s.Hardcore != f.Hardcore.Value) continue;
                if (f.PowersEnabled.HasValue && (s.Powers != PowerFrequency.None) != f.PowersEnabled.Value) continue;
                if (f.Economy.HasValue && s.Economy != f.Economy.Value) continue;
                if (f.Kind.HasValue && s.Kind != f.Kind.Value) continue;
                if (f.HidePasswordProtected && s.PasswordProtected) continue;
                if (f.HideWhitelisted && s.Whitelisted) continue;
                if (f.CompatibleOnly && !IsCompatible(s, clientVersion)) continue;
                result.Add(s);
            }

            Comparison<ServerListing> cmp;
            switch (sort)
            {
                case ServerSort.Ping: cmp = (a, b) => a.PingMs.CompareTo(b.PingMs); break;
                case ServerSort.Name: cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case ServerSort.Friends: cmp = (a, b) => b.FriendsOnline.Count.CompareTo(a.FriendsOnline.Count); break;
                default: cmp = (a, b) => b.Population.CompareTo(a.Population); break;
            }
            result.Sort((a, b) =>
            {
                var c = cmp(a, b);
                return c != 0 ? c : string.CompareOrdinal(a.ServerId, b.ServerId);
            });
            return result;
        }

        private static string MajorMinor(string version)
        {
            var parts = version.Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : version;
        }
    }
}
