using System;
using System.Collections.Generic;
using HeroGame.Core.Audio;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;
using HeroGame.Core.World;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// In-world radio (GDD §53). Each station airs a deterministic hourly running order built from the live world:
    /// bulletins from real history, weather from the simulation, ads for real businesses (those that pay for
    /// advertising get airtime), host breaks that mention what is going on, and emergency alerts that cut in
    /// during hurricanes and disasters. Every client tuned to a station at the same game time hears the same thing.
    /// </summary>
    public sealed class RadioService
    {
        public const int NewsSeconds = 120;
        public const int WeatherSeconds = 45;
        public const int AdSeconds = 30;
        public const int TalkSeconds = 40;
        public const int AlertSeconds = 60;

        private readonly World _w;
        private readonly Dictionary<string, RadioStation> _stations = new Dictionary<string, RadioStation>();
        private readonly Dictionary<string, List<RadioSegment>> _schedules = new Dictionary<string, List<RadioSegment>>();
        private long _scheduleHour = long.MinValue;

        public RadioService(World world)
        {
            _w = world;
            foreach (var s in world.Content.RadioStations) _stations[s.Id] = s;
        }

        public IReadOnlyCollection<RadioStation> Stations => _stations.Values;
        public RadioStation Station(string id) => id != null && _stations.TryGetValue(id, out var s) ? s : null;

        /// <summary>
        /// Radio runs on real time, not game time: game time passes ~30× faster, and a song must last as long as the
        /// song. The radio timeline is the world clock divided by its time scale, so it is still the same for every
        /// client and every replay. Segment start/end seconds are on this timeline.
        /// </summary>
        public long RadioSecond(GameDateTime t) => (long)Math.Floor(t.TotalSeconds / Math.Max(1.0, _w.Clock.TimeScale));

        public long HourAt(GameDateTime t) => RadioSecond(t) / GameDateTime.SecondsPerHour;

        /// <summary>What <paramref name="stationId"/> is airing at <paramref name="t"/> (null for an unknown station).</summary>
        public RadioSegment OnAir(string stationId, GameDateTime t)
        {
            var schedule = Schedule(stationId, HourAt(t));
            if (schedule == null) return null;
            var now = RadioSecond(t);
            foreach (var s in schedule)
                if (now >= s.StartSecond && now < s.EndSecond) return s;
            return schedule[schedule.Count - 1];
        }

        /// <summary>
        /// The running order for one real-time hour (<see cref="HourAt"/>). Built once per hour from the world as it is when the hour is first
        /// requested and then cached, so a station doesn't rewrite a song that is already playing.
        /// </summary>
        public List<RadioSegment> Schedule(string stationId, long hourIndex)
        {
            var station = Station(stationId);
            if (station == null) return null;
            if (hourIndex != _scheduleHour)
            {
                _schedules.Clear();
                _scheduleHour = hourIndex;
            }
            var key = stationId;
            if (_schedules.TryGetValue(key, out var cached)) return cached;
            var list = Build(station, hourIndex);
            _schedules[key] = list;
            return list;
        }

        private List<RadioSegment> Build(RadioStation station, long hourIndex)
        {
            var rng = DeterministicRandom.For(_w.Seed, StableHash.Of(station.Id), (ulong)hourIndex, 0xAD10);
            var start = hourIndex * GameDateTime.SecondsPerHour;
            var end = start + GameDateTime.SecondsPerHour;
            var list = new List<RadioSegment>();
            var t = start;

            void Add(SegmentKind kind, int seconds, string title, string text, string trackId = "")
            {
                var e = Math.Min(end, t + seconds);
                if (e <= t) return;
                list.Add(new RadioSegment { Station = station.Id, Kind = kind, StartSecond = t, EndSecond = e, Title = title, Text = text, TrackId = trackId });
                t = e;
            }

            var alert = EmergencyAlert();
            if (alert != null) Add(SegmentKind.EmergencyAlert, AlertSeconds, "Emergency Alert", alert);
            if (station.NewsAtTopOfHour || station.Format == StationFormat.News)
            {
                Add(SegmentKind.News, NewsSeconds, station.Name + " News", Bulletin(3));
                Add(SegmentKind.Weather, WeatherSeconds, "Weather", WeatherReport());
            }

            // Decide where the breaks go: talk and ads are spread through the hour between songs.
            var breaks = new List<SegmentKind>();
            for (var i = 0; i < station.TalkPerHour; i++) breaks.Add(SegmentKind.Talk);
            for (var i = 0; i < station.AdsPerHour; i++) breaks.Add(SegmentKind.Ad);
            if (station.Format == StationFormat.News)
                for (var i = 0; i < 3; i++) breaks.Add(SegmentKind.News); // news stations run bulletins through the hour
            Shuffle(breaks, rng);

            var trackIndex = rng.NextInt(0, Math.Max(1, station.Tracks.Count));
            var b = 0;
            while (t < end)
            {
                if (station.Tracks.Count > 0)
                {
                    var track = station.Tracks[trackIndex++ % station.Tracks.Count];
                    Add(SegmentKind.Track, Math.Max(30, track.Seconds), track.Title, track.Artist, track.Id);
                }
                if (b < breaks.Count && t < end)
                {
                    switch (breaks[b++])
                    {
                        case SegmentKind.Talk: Add(SegmentKind.Talk, TalkSeconds, station.Host, TalkLine(station, rng, t)); break;
                        case SegmentKind.Ad:
                            var (title, text) = Advert(rng);
                            Add(SegmentKind.Ad, AdSeconds, title, text);
                            break;
                        case SegmentKind.News: Add(SegmentKind.News, NewsSeconds, station.Name + " News", Bulletin(3)); break;
                    }
                }
                if (station.Tracks.Count == 0 && b >= breaks.Count) Add(SegmentKind.Talk, TalkSeconds, station.Host, TalkLine(station, rng, t));
            }
            return list;
        }

        /// <summary>Headlines from real history (never invented).</summary>
        public string Bulletin(int items)
        {
            var edition = NewsDesk.Edition(_w.History, _w.Today - 1, _w.Config.Identity.NewsOrganizations, items);
            if (edition.Count == 0) return "A quiet day in " + _w.Config.Identity.CityName + ". No major stories to report.";
            var parts = new List<string>();
            foreach (var a in edition) parts.Add(a.Headline.TrimEnd('.') + ".");
            return string.Join(" ", parts);
        }

        public string WeatherReport()
        {
            var s = _w.Weather.State;
            var now = s.Current;
            var text = Describe(now.Kind) + ", " + Math.Round(now.TemperatureC * 9 / 5 + 32) + " degrees, wind " + Math.Round(now.WindSpeedMs * 2.237) + " miles an hour.";
            var storm = s.ActiveSystem;
            if (storm != null) text += " Forecasters are tracking " + storm.Name + ", category " + Math.Round(storm.Category) + ".";
            if (_w.Calendar.HeatWaveOn(_w.Clock.Now)) text += " A heat advisory is in effect.";
            return text;
        }

        /// <summary>Alerts cut in while a hurricane emergency or a local disaster is under way.</summary>
        public string EmergencyAlert()
        {
            var effects = _w.Weather.Effects;
            if (effects.EmergencyDeclared)
                return "The following is an emergency message from the " + _w.Config.Identity.GovernmentName + ". A state of emergency is in effect. Stay off the roads and away from flood water.";
            var now = _w.Clock.Now.TotalSeconds;
            foreach (var d in _w.Disasters.Active)
            {
                if (now < d.StartSecond || now >= d.EndSecond) continue;
                if (d.Kind == Civic.DisasterKind.ChemicalIncident || d.Kind == Civic.DisasterKind.FlashFlood)
                    return "Emergency alert: " + d.Headline + ". Follow instructions from emergency services.";
            }
            return null;
        }

        /// <summary>Ads for real businesses. Those that spend on advertising are far likelier to be on air.</summary>
        private (string title, string text) Advert(DeterministicRandom rng)
        {
            var ids = new List<EntityId>(_w.Businesses.Keys);
            ids.Sort();
            double total = 0;
            var weights = new List<double>(ids.Count);
            foreach (var id in ids)
            {
                var b = _w.Businesses[id];
                var weight = !b.Open ? 0 : 0.2 + Math.Sqrt(b.AdvertisingCents / 100.0);
                weights.Add(weight);
                total += weight;
            }
            if (total <= 0) return ("Public service announcement", "Check your smoke alarms and your flood insurance. A message from the " + _w.Config.Identity.GovernmentName + ".");
            var pick = rng.NextDouble() * total;
            for (var i = 0; i < ids.Count; i++)
            {
                pick -= weights[i];
                if (pick > 0) continue;
                var b = _w.Businesses[ids[i]];
                var t = _w.BusinessSim.Template(b.TemplateId);
                var place = _w.Geography.GetPlace(b.Place);
                var district = place != null ? _w.Geography.GetDistrict(place.District) : null;
                var kind = t != null ? t.DisplayName.ToLowerInvariant() : "business";
                return (b.Name, b.Name + ", your neighbourhood " + kind + (district != null ? " in " + district.Name : "") + ". " +
                                (b.PriceLevel < 0.95f ? "Prices you'll love." : b.Reputation > 0.7f ? "Ask anybody." : "Come see us.") +
                                (place != null && place.OpenMinute != place.CloseMinute ? " Open " + Clock(place.OpenMinute) + " to " + Clock(place.CloseMinute) + "." : " Open all hours."));
            }
            return ("Public service announcement", "Look out for your neighbours.");
        }

        private string TalkLine(RadioStation station, DeterministicRandom rng, long second)
        {
            if (station.Talk.Count == 0) return "";
            var line = station.Talk[rng.NextInt(0, station.Talk.Count)];
            var at = new GameDateTime((long)(second * Math.Max(1.0, _w.Clock.TimeScale)));
            var weather = _w.Weather.State.Current;
            var top = NewsDesk.Edition(_w.History, _w.Today - 1, _w.Config.Identity.NewsOrganizations, 1);
            var districts = new List<District>(_w.Geography.Districts);
            districts.Sort((a, b) => a.Id.CompareTo(b.Id));
            var district = districts.Count > 0 ? districts[rng.NextInt(0, districts.Count)].Name : _w.Config.Identity.CityName;
            return line.Replace("{time}", Clock(at.MinuteOfDay))
                .Replace("{weather}", Describe(weather.Kind).ToLowerInvariant())
                .Replace("{temp}", Math.Round(weather.TemperatureC * 9 / 5 + 32).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{city}", _w.Config.Identity.CityName)
                .Replace("{district}", district)
                .Replace("{headline}", top.Count > 0 ? top[0].Headline.TrimEnd('.') : "Quiet one out there today");
        }

        public static string Describe(Weather.WeatherKind kind)
        {
            switch (kind)
            {
                case Weather.WeatherKind.Clear: return "Clear skies";
                case Weather.WeatherKind.PartlyCloudy: return "Partly cloudy";
                case Weather.WeatherKind.Overcast: return "Overcast";
                case Weather.WeatherKind.Fog: return "Fog";
                case Weather.WeatherKind.LightRain: return "Light rain";
                case Weather.WeatherKind.HeavyRain: return "Heavy rain";
                case Weather.WeatherKind.Thunderstorm: return "Thunderstorms";
                case Weather.WeatherKind.TropicalStorm: return "Tropical storm conditions";
                default: return "Hurricane conditions";
            }
        }

        private static string Clock(int minuteOfDay)
        {
            var h = minuteOfDay / 60 % 24;
            var m = minuteOfDay % 60;
            var h12 = h % 12 == 0 ? 12 : h % 12;
            return h12 + (m == 0 ? "" : ":" + m.ToString("00", System.Globalization.CultureInfo.InvariantCulture)) + (h < 12 ? " a.m." : " p.m.");
        }

        private static void Shuffle<T>(List<T> list, DeterministicRandom rng)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.NextInt(0, i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
