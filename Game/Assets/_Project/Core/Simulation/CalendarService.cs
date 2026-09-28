using System;
using System.Collections.Generic;

namespace HeroGame.Core.Simulation
{
    using HeroGame.Core.Civic;
    using HeroGame.Core.Emergency;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Time;
    using HeroGame.Core.World;
    using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

    /// <summary>
    /// The city calendar and local disasters (GDD §38–40). Dated events (Bayou Carnival, the holidays) shift demand
    /// for the kinds of business people actually visit on those days. Disasters are rolled from the world's state —
    /// flash floods need heavy rain and low ground, chemical incidents happen where the chemicals are, heat waves in
    /// hot summers, blackouts after storms and during heat — and each has real consequences: damaged property,
    /// emergency calls, closed districts, dark businesses.
    /// </summary>
    public sealed class CalendarService
    {
        private readonly World _w;

        public CalendarService(World world)
        {
            _w = world;
        }

        private DisasterState S => _w.Disasters;
        private float Frequency => Math.Max(0f, _w.Config.Gameplay.DisasterFrequencyMultiplier);

        // ------------------------------------------------------------------ calendar

        /// <summary>Events running on the given date (multi-day events span their range, wrapping into the next year).</summary>
        public List<CalendarEvent> ActiveOn(GameDateTime date)
        {
            var list = new List<CalendarEvent>();
            var dayOfYear = date.ToDateTime().DayOfYear;
            foreach (var e in _w.Content.CalendarEvents)
            {
                var start = new DateTime(date.Year, Math.Max(1, Math.Min(12, e.Month)), 1).AddDays(Math.Max(1, e.Day) - 1).DayOfYear;
                var days = Math.Max(1, e.Days);
                var daysInYear = DateTime.IsLeapYear(date.Year) ? 366 : 365;
                var offset = (dayOfYear - start + daysInYear) % daysInYear;
                if (offset < days) list.Add(e);
            }
            return list;
        }

        /// <summary>
        /// Demand factor for a business of <paramref name="kind"/> in <paramref name="district"/> on a date: calendar
        /// events multiply demand for their place kinds; a heat wave keeps people indoors in the evening.
        /// </summary>
        public float DemandMultiplier(PlaceKind kind, GameDateTime date)
        {
            var m = 1f;
            var name = kind.ToString();
            foreach (var e in ActiveOn(date))
                if (e.Demand.TryGetValue(name, out var f)) m *= Math.Max(0.1f, f);
            if (HeatWaveOn(date))
            {
                if (kind == PlaceKind.Nightlife) m *= 0.85f;
                else if (kind == PlaceKind.Restaurant) m *= 0.9f;
                else if (kind == PlaceKind.Shop) m *= 1.05f;
            }
            return m;
        }

        /// <summary>Morning announcements for events that start today.</summary>
        public void AnnounceDay(long day)
        {
            var date = new GameDateTime(day * GameDateTime.SecondsPerDay);
            foreach (var e in ActiveOn(date))
            {
                var start = date.Month == e.Month && date.Day == e.Day;
                if (!start) continue;
                _w.History.Record(day, HistoryCategory.Community, 2, e.Name + ": " + e.Announcement);
            }
        }

        // ------------------------------------------------------------------ disasters

        public bool HeatWaveOn(GameDateTime t) => S.Active.Exists(d => d.Kind == DisasterKind.HeatWave && t.TotalSeconds >= d.StartSecond && t.TotalSeconds < d.EndSecond);

        public bool Closed(EntityId district, long second) => S.ClosesDistrict(district, second);

        public bool BlackoutAt(EntityId district, long second) =>
            S.Active.Exists(d => d.Kind == DisasterKind.Blackout && d.District == district && second >= d.StartSecond && second < d.EndSecond);

        public float OutageHoursToday(District d) => d != null && S.OutageHoursToday.TryGetValue(d.Key, out var h) ? h : 0f;

        /// <summary>A business closes for the day once its district has been under an emergency order for half of it.</summary>
        public bool ClosedToday(District d) => d != null && S.ClosureHoursToday.TryGetValue(d.Key, out var h) && h >= 12;

        public void ProcessHour(GameDateTime t)
        {
            var now = t.TotalSeconds;
            Expire(now);
            var districts = SortedDistricts();
            foreach (var d in districts)
            {
                if (Closed(d.Id, now)) S.ClosureHoursToday[d.Key] = (S.ClosureHoursToday.TryGetValue(d.Key, out var c) ? c : 0) + 1;
                if (BlackoutAt(d.Id, now)) S.OutageHoursToday[d.Key] = (S.OutageHoursToday.TryGetValue(d.Key, out var h) ? h : 0f) + 1f;
            }
            if (Frequency <= 0f) return;

            var effects = _w.Weather.Effects;
            var heat = HeatWaveOn(t);
            foreach (var d in districts)
            {
                var rng = DeterministicRandom.For(_w.Seed, 0xD15A57, d.Id.Value, (ulong)t.HourIndex);
                // Flash floods: intense rain over low ground.
                if (effects.BadWeather > 0.45f && !ActiveIn(d.Id, DisasterKind.FlashFlood) &&
                    rng.Chance(0.02 * Frequency * d.FloodRisk * (effects.BadWeather - 0.35f)))
                    StartFlashFlood(d, t, rng);
                // Blackouts: rare, far likelier during storms and heat waves.
                var blackout = 1.0 / (24 * 180) * Frequency * (heat ? 5 : 1) * (effects.BadWeather > 0.5f ? 4 : 1);
                if (!ActiveIn(d.Id, DisasterKind.Blackout) && rng.Chance(blackout)) StartBlackout(d, t, rng);
            }
            if (heat) HeatCasualties(t);
        }

        public void ProcessDay(long day)
        {
            // Yesterday's totals have been used by the business day; start today's.
            S.OutageHoursToday.Clear();
            S.ClosureHoursToday.Clear();
            if (Frequency <= 0f) return;
            var tomorrow = new GameDateTime((day + 1) * GameDateTime.SecondsPerDay);
            // Chemical incidents, where chemicals are handled.
            foreach (var d in SortedDistricts())
            {
                if (d.Type != DistrictType.Industrial && d.Type != DistrictType.Port) continue;
                var rng = DeterministicRandom.For(_w.Seed, 0xC4E111, d.Id.Value, (ulong)day);
                if (!ActiveIn(d.Id, DisasterKind.ChemicalIncident) && rng.Chance(1.0 / 250 * Frequency))
                    StartChemicalIncident(d, tomorrow.AddHours(rng.NextInt(6, 20)), rng);
            }
            // Heat waves in hot summers.
            var month = tomorrow.Month;
            if (month >= 6 && month <= 9 && !S.Active.Exists(x => x.Kind == DisasterKind.HeatWave) && _w.Weather.State.Current.TemperatureC >= 31f)
            {
                var rng = DeterministicRandom.For(_w.Seed, 0x4EA7, (ulong)day);
                if (rng.Chance(0.07 * Frequency)) StartHeatWave(tomorrow, rng);
            }
        }

        /// <summary>Starts a disaster now (admin tools, story scripts, tests). District is ignored for heat waves.</summary>
        public void Trigger(DisasterKind kind, District d, GameDateTime t)
        {
            var rng = DeterministicRandom.For(_w.Seed, 0x7E1660, (ulong)kind, (ulong)t.TotalSeconds);
            switch (kind)
            {
                case DisasterKind.FlashFlood: StartFlashFlood(d, t, rng); break;
                case DisasterKind.ChemicalIncident: StartChemicalIncident(d, t, rng); break;
                case DisasterKind.Blackout: StartBlackout(d, t, rng); break;
                case DisasterKind.HeatWave: StartHeatWave(t, rng); break;
            }
        }

        private void StartFlashFlood(District d, GameDateTime t, DeterministicRandom rng)
        {
            var severity = (float)(0.3 + rng.NextDouble() * 0.7) * d.FloodRisk;
            var disaster = Begin(DisasterKind.FlashFlood, d.Id, t.TotalSeconds, rng.NextInt(6, 13) * GameDateTime.SecondsPerHour, severity,
                "Flash flooding swamps streets in " + d.Name);
            var damaged = 0;
            foreach (var p in _w.Properties.All)
            {
                if (p.District != d.Id || p.Kind == PropertyKind.Land || p.Damage == DamageState.Destroyed) continue;
                var prng = DeterministicRandom.For(_w.Seed, 0xF100D, p.Id.Value, (ulong)t.HourIndex);
                if (!prng.Chance(0.25 * severity)) continue;
                _w.Properties.ApplyDamage(p, (float)(0.02 + prng.NextDouble() * 0.1) * (0.5f + severity));
                damaged++;
                if (_w.Characters.TryGetValue(_w.Ownership.OwnerOf(p.Id), out var owner))
                    _w.Phone.Send(owner, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Emergency,
                        "Flood damage reported at " + p.Address + ". Estimated repairs: " + _w.Properties.RepairQuote(p, _w.Macro.PriceLevel) + ".");
            }
            if (damaged > 0) _w.Dirty.Mark(SaveChunks.Properties);
            _w.Dispatch.Report(EmergencyKind.Medical, d.Center, 2, "Water rescue: driver stranded in floodwater", severity: 0.4f);
            _w.History.Record(t.DayIndex, HistoryCategory.Disaster, 4, disaster.Headline, damaged + " properties damaged.", d.Id);
        }

        private void StartBlackout(District d, GameDateTime t, DeterministicRandom rng)
        {
            var disaster = Begin(DisasterKind.Blackout, d.Id, t.TotalSeconds, rng.NextInt(3, 11) * GameDateTime.SecondsPerHour, 0.5f,
                "Power out across " + d.Name);
            _w.History.Record(t.DayIndex, HistoryCategory.Disaster, 3, disaster.Headline, "The utility says crews are working to restore service.", d.Id);
        }

        private ActiveDisaster StartChemicalIncident(District d, GameDateTime t, DeterministicRandom rng)
        {
            var disaster = Begin(DisasterKind.ChemicalIncident, d.Id, t.TotalSeconds, rng.NextInt(18, 37) * GameDateTime.SecondsPerHour, (float)(0.4 + rng.NextDouble() * 0.6),
                "Chemical leak forces shelter-in-place order in " + d.Name);
            _w.Dispatch.Report(EmergencyKind.Medical, d.Center, 1, "Hazmat exposure: workers overcome by fumes", severity: 0.6f);
            _w.History.Record(t.DayIndex, HistoryCategory.Disaster, 4, disaster.Headline, "Businesses in the zone are ordered closed until the all-clear.", d.Id);
            foreach (var c in _w.Characters.Values)
                _w.Phone.Send(c, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Emergency,
                    "EMERGENCY ALERT: chemical release in " + d.Name + ". Shelter in place; businesses there are closed until further notice.");
            return disaster;
        }

        private void StartHeatWave(GameDateTime t, DeterministicRandom rng)
        {
            var disaster = Begin(DisasterKind.HeatWave, EntityId.None, t.TotalSeconds, rng.NextInt(3, 6) * GameDateTime.SecondsPerDay, 0.7f,
                "Heat advisory: " + _w.Config.Identity.CityName + " braces for days of dangerous heat");
            _w.History.Record(t.DayIndex, HistoryCategory.Weather, 4, disaster.Headline, "Cooling centres open at City Hall and the library.");
        }

        /// <summary>Heat exhaustion calls among older residents.</summary>
        private void HeatCasualties(GameDateTime t)
        {
            var hour = t.Hour;
            if (hour < 11 || hour > 19) return;
            var rng = DeterministicRandom.For(_w.Seed, 0x4EA7CA, (ulong)t.HourIndex);
            if (!rng.Chance(0.12 * Frequency)) return;
            var ordered = _w.Population.Ordered;
            if (ordered.Count == 0) return;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var npc = ordered[rng.NextInt(0, ordered.Count)];
                if (!npc.Alive || npc.AgeYears(_w.Today) < 65) continue;
                var home = _w.Geography.GetPlace(npc.Home);
                if (home == null) continue;
                _w.Dispatch.Report(EmergencyKind.Medical, home.Position, 2, "Heat exhaustion: " + npc.FullName, npc.Id, severity: 0.35f);
                return;
            }
        }

        private ActiveDisaster Begin(DisasterKind kind, EntityId district, long start, long duration, float severity, string headline)
        {
            var d = new ActiveDisaster { Kind = kind, District = district, StartSecond = start, EndSecond = start + duration, Severity = severity, Headline = headline };
            S.Active.Add(d);
            S.Total++;
            _w.Dirty.Mark(SaveChunks.Civic);
            return d;
        }

        private void Expire(long now)
        {
            for (var i = 0; i < S.Active.Count; i++)
            {
                var d = S.Active[i];
                if (now < d.EndSecond) continue;
                S.Active.RemoveAt(i--);
                S.Recent.Add(d);
                if (S.Recent.Count > 30) S.Recent.RemoveAt(0);
                var district = _w.Geography.GetDistrict(d.District);
                var where = district != null ? district.Name : _w.Config.Identity.CityName;
                switch (d.Kind)
                {
                    case DisasterKind.ChemicalIncident:
                        _w.History.Record(new GameDateTime(now).DayIndex, HistoryCategory.Disaster, 2, "All-clear given in " + where + " after chemical leak", "", d.District);
                        break;
                    case DisasterKind.Blackout:
                        _w.History.Record(new GameDateTime(now).DayIndex, HistoryCategory.Disaster, 1, "Power restored in " + where, "", d.District);
                        break;
                    case DisasterKind.HeatWave:
                        _w.History.Record(new GameDateTime(now).DayIndex, HistoryCategory.Weather, 2, "Heat advisory lifted", "");
                        break;
                }
                _w.Dirty.Mark(SaveChunks.Civic);
            }
        }

        private bool ActiveIn(EntityId district, DisasterKind kind) => S.Active.Exists(d => d.District == district && d.Kind == kind);

        private List<District> SortedDistricts()
        {
            var list = new List<District>(_w.Geography.Districts);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }
    }
}
