using System;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;
using HeroGame.Core.World;

namespace HeroGame.Core.Population
{
    public struct ScheduledActivity
    {
        public ActivityKind Activity;
        /// <summary>Where the NPC is (or is heading, when commuting).</summary>
        public EntityId Place;
        /// <summary>Origin place while commuting; otherwise None.</summary>
        public EntityId FromPlace;
        /// <summary>0..1 progress of a commute.</summary>
        public float Progress;
    }

    /// <summary>
    /// Pure function: (NPC, time) → activity and place. Randomness is keyed by (world seed, NPC, day)
    /// so the answer is identical no matter when or how often it is asked. This is what lets an
    /// off-screen NPC be "materialised" exactly where their life says they should be (GDD §13–14).
    /// </summary>
    public sealed class ScheduleResolver
    {
        private const int CommuteMinutes = 30;

        private readonly ulong _worldSeed;
        private readonly OccupationTable _occupations;
        private readonly Geography _geography;

        public ScheduleResolver(ulong worldSeed, OccupationTable occupations, Geography geography)
        {
            _worldSeed = worldSeed;
            _occupations = occupations;
            _geography = geography;
        }

        public ScheduledActivity Resolve(NpcRecord npc, GameDateTime time) => Resolve(npc, time, out _);

        /// <summary>
        /// Resolves the activity and reports until when it stays the same (<paramref name="validUntil"/>, exclusive).
        /// Everything except the minute of day is constant within a day, so the answer can only change at one of
        /// the minute thresholds the decision compared against, or at midnight. Callers can cache the result
        /// until then (commute progress changes continuously and is interpolated by the caller).
        /// </summary>
        public ScheduledActivity Resolve(NpcRecord npc, GameDateTime time, out GameDateTime validUntil)
        {
            var dayStart = time.StartOfDay;
            var minute = time.MinuteOfDay;
            var next = 1440;
            var result = ResolveCore(npc, time, minute, ref next);
            if (CurfewStartMinute >= 0 && npc.Alive) result = ApplyCurfew(npc, time, minute, result, ref next);
            validUntil = dayStart.AddMinutes(next);
            return result;
        }

        /// <summary>Youth curfew window in minutes of day (set by the city's ordinance; -1 = none). May wrap midnight.</summary>
        public int CurfewStartMinute = -1;
        public int CurfewEndMinute = -1;
        public const int CurfewMinAge = 10;
        public const int CurfewMaxAge = 17;

        /// <summary>Minors go home during curfew instead of being out (work shifts and hospital/custody stand).</summary>
        private ScheduledActivity ApplyCurfew(NpcRecord npc, GameDateTime time, int minute, ScheduledActivity result, ref int next)
        {
            var age = npc.AgeYears(time.DayIndex);
            if (age < CurfewMinAge || age > CurfewMaxAge) return result;
            Boundary(CurfewStartMinute, minute, ref next);
            Boundary(CurfewEndMinute, minute, ref next);
            var inCurfew = CurfewStartMinute > CurfewEndMinute
                ? minute >= CurfewStartMinute || minute < CurfewEndMinute
                : minute >= CurfewStartMinute && minute < CurfewEndMinute;
            if (!inCurfew) return result;
            switch (result.Activity)
            {
                case ActivityKind.Sleeping:
                case ActivityKind.AtHome:
                case ActivityKind.Working:
                case ActivityKind.Hospitalized:
                case ActivityKind.InCustody:
                case ActivityKind.Deceased:
                    return result;
                default:
                    return AtHome(npc, false);
            }
        }

        private ScheduledActivity ResolveCore(NpcRecord npc, GameDateTime time, int minute, ref int next)
        {
            if (!npc.Alive) return new ScheduledActivity { Activity = ActivityKind.Deceased };
            var day = time.DayIndex;
            if (npc.OverrideUntilDay > day)
                return new ScheduledActivity { Activity = npc.OverrideActivity, Place = npc.OverridePlace };

            var age = npc.AgeYears(day);
            var rng = DeterministicRandom.For(_worldSeed, npc.Id.Value, (ulong)day, 0x5C4ED);

            // Personal rhythm: early birds vs night owls, stable per NPC.
            var chronotype = (int)((StableHash.Mix(npc.Id.Value) % 91) - 45); // ±45 minutes
            var wake = 6 * 60 + 30 + chronotype;
            var sleep = 22 * 60 + 45 + chronotype;

            if (age < 5)
            {
                Boundary(wake, minute, ref next);
                Boundary(sleep - 90, minute, ref next);
                return AtHome(npc, minute < wake || minute >= sleep - 90);
            }

            // Friday/Saturday late nights (teens and young adults) run past midnight into the next day.
            if (minute < 6 * 60 && LateNight(npc, day - 1, out var yStart, out var yDuration, out var yPlace) && yStart + yDuration > 1440)
            {
                var endToday = yStart + yDuration - 1440;
                Boundary(endToday, minute, ref next);
                Boundary(endToday + 20, minute, ref next);
                if (minute < endToday) return new ScheduledActivity { Activity = ActivityFor(_geography.GetPlace(yPlace).Kind), Place = yPlace };
                if (minute < endToday + 20) return Commute(yPlace, npc.Home, (minute - endToday) / 20f);
                return AtHome(npc, true); // sleeps in after a late night
            }

            // Work/school commitment for today.
            int commitStart = -1, commitEnd = -1;
            var commitPlace = EntityId.None;
            var commitActivity = ActivityKind.Working;

            if (npc.Employment == EmploymentStatus.Student && npc.School.IsValid && IsSchoolDay(time))
            {
                commitStart = 7 * 60 + 45;
                commitEnd = 15 * 60 + 15;
                commitPlace = npc.School;
                commitActivity = ActivityKind.AtSchool;
            }
            else if (npc.Employment == EmploymentStatus.Employed && npc.Workplace.IsValid)
            {
                var occ = _occupations.Get(npc.OccupationId);
                if (occ != null)
                {
                    var worksToday = occ.WorksOn(time.DayOfWeek);
                    // Overnight shift that started yesterday still covers the early hours.
                    var yesterday = time.AddDays(-1);
                    var shiftEnd = occ.ShiftStartMinute + occ.ShiftLengthMinutes;
                    if (shiftEnd > 1440 && occ.WorksOn(yesterday.DayOfWeek))
                    {
                        Boundary(shiftEnd - 1440, minute, ref next);
                        if (minute < shiftEnd - 1440) return new ScheduledActivity { Activity = ActivityKind.Working, Place = npc.Workplace };
                    }
                    if (worksToday)
                    {
                        commitStart = occ.ShiftStartMinute;
                        commitEnd = Math.Min(1440, shiftEnd);
                        commitPlace = npc.Workplace;
                        // Night-shift workers sleep during the day instead.
                        if (occ.ShiftStartMinute >= 18 * 60 || occ.ShiftStartMinute < 5 * 60)
                        {
                            wake = 14 * 60;
                            sleep = Math.Max(0, occ.ShiftStartMinute - 60 - CommuteMinutes);
                            Boundary(7 * 60, minute, ref next);
                            Boundary(wake, minute, ref next);
                            if (minute >= 7 * 60 && minute < wake) return AtHome(npc, true);
                        }
                    }
                }
            }

            if (commitStart >= 0)
            {
                Boundary(commitStart - CommuteMinutes, minute, ref next);
                Boundary(commitStart, minute, ref next);
                Boundary(commitEnd, minute, ref next);
                Boundary(commitEnd + CommuteMinutes, minute, ref next);
                if (minute >= commitStart && minute < commitEnd)
                    return new ScheduledActivity { Activity = commitActivity, Place = commitPlace };
                if (minute >= commitStart - CommuteMinutes && minute < commitStart)
                    return Commute(npc.Home, commitPlace, (minute - (commitStart - CommuteMinutes)) / (float)CommuteMinutes);
                if (minute >= commitEnd && minute < commitEnd + CommuteMinutes)
                    return Commute(commitPlace, npc.Home, (minute - commitEnd) / (float)CommuteMinutes);
                if (commitStart < wake) wake = Math.Max(0, commitStart - CommuteMinutes - 45);
            }

            if (commitEnd <= 21 * 60 && LateNight(npc, day, out var lStart, out var lDuration, out var lPlace))
            {
                var end = Math.Min(1440, lStart + lDuration);
                Boundary(lStart - 20, minute, ref next);
                Boundary(lStart, minute, ref next);
                if (lStart + lDuration + 20 < 1440) Boundary(lStart + lDuration, minute, ref next);
                if (lStart + lDuration + 20 < 1440) Boundary(lStart + lDuration + 20, minute, ref next);
                if (minute >= lStart && minute < end) return new ScheduledActivity { Activity = ActivityFor(_geography.GetPlace(lPlace).Kind), Place = lPlace };
                if (minute >= lStart - 20 && minute < lStart) return Commute(npc.Home, lPlace, (minute - (lStart - 20)) / 20f);
                if (lStart + lDuration < 1440 && minute >= lStart + lDuration && minute < lStart + lDuration + 20) return Commute(lPlace, npc.Home, (minute - lStart - lDuration) / 20f);
                if (minute >= lStart - 20) return AtHome(npc, true);
                sleep = Math.Max(sleep, 1439);
            }

            Boundary(wake, minute, ref next);
            Boundary(sleep, minute, ref next);
            if (minute < wake || minute >= sleep) return AtHome(npc, true);

            // Free time: one outing per day at most, chosen deterministically.
            var outingChance = 0.25 + 0.45 * npc.Personality.Extraversion;
            if (npc.FavoritePlaces.Count > 0 && rng.Chance(outingChance))
            {
                var place = npc.FavoritePlaces[rng.NextInt(0, npc.FavoritePlaces.Count)];
                var start = time.IsWeekend ? rng.NextInt(10 * 60, 19 * 60) : rng.NextInt(17 * 60 + 30, 20 * 60);
                var duration = rng.NextInt(60, 180);
                var placeData = _geography.GetPlace(place);
                var fits = commitStart < 0 || start >= commitEnd + CommuteMinutes || start + duration <= commitStart - CommuteMinutes;
                if (fits && placeData != null && placeData.IsOpenAt(start))
                {
                    Boundary(start - 20, minute, ref next);
                    Boundary(start, minute, ref next);
                    Boundary(start + duration, minute, ref next);
                    Boundary(start + duration + 20, minute, ref next);
                    if (minute >= start && minute < start + duration)
                        return new ScheduledActivity { Activity = ActivityFor(placeData.Kind), Place = place };
                    if (minute >= start - 20 && minute < start)
                        return Commute(npc.Home, place, (minute - (start - 20)) / 20f);
                    if (minute >= start + duration && minute < start + duration + 20)
                        return Commute(place, npc.Home, (minute - start - duration) / 20f);
                }
            }

            return AtHome(npc, false);
        }

        /// <summary>
        /// Whether <paramref name="npc"/> goes out late on <paramref name="day"/>: Friday and Saturday nights, ages 15–29,
        /// likelier for extraverts, to a favourite nightlife/food/park spot that is open at 21:00–22:00. Deterministic
        /// per (world, NPC, day) so the part after midnight can be recomputed the next day.
        /// </summary>
        private bool LateNight(NpcRecord npc, long day, out int start, out int duration, out EntityId place)
        {
            start = 0;
            duration = 0;
            place = EntityId.None;
            var date = new GameDateTime(day * GameDateTime.SecondsPerDay);
            if (date.DayOfWeek != DayOfWeek.Friday && date.DayOfWeek != DayOfWeek.Saturday) return false;
            var age = npc.AgeYears(day);
            if (age < 15 || age > 29 || npc.FavoritePlaces.Count == 0) return false;
            var rng = DeterministicRandom.For(_worldSeed, npc.Id.Value, (ulong)day, 0x1A7E);
            if (!rng.Chance(0.12 + 0.4 * npc.Personality.Extraversion)) return false;
            start = rng.NextInt(21 * 60, 22 * 60);
            duration = rng.NextInt(150, 300);
            foreach (var candidate in npc.FavoritePlaces)
            {
                var p = _geography.GetPlace(candidate);
                if (p == null || !p.IsOpenAt(start)) continue;
                if (p.Kind == PlaceKind.Nightlife || p.Kind == PlaceKind.Restaurant || p.Kind == PlaceKind.Park || p.Kind == PlaceKind.Beach)
                {
                    place = candidate;
                    return true;
                }
            }
            return false;
        }

        private static void Boundary(int threshold, int minute, ref int next)
        {
            if (threshold > minute && threshold < next) next = threshold;
        }

        public static bool IsSchoolDay(GameDateTime time)
        {
            if (time.IsWeekend) return false;
            var month = time.Month;
            return month != 6 && month != 7; // summer break
        }

        public static ActivityKind ActivityFor(PlaceKind kind)
        {
            switch (kind)
            {
                case PlaceKind.Shop: return ActivityKind.Shopping;
                case PlaceKind.Restaurant: return ActivityKind.Eating;
                case PlaceKind.Gym: return ActivityKind.Exercising;
                case PlaceKind.Church: return ActivityKind.Worship;
                case PlaceKind.Nightlife: return ActivityKind.Socializing;
                default: return ActivityKind.Leisure;
            }
        }

        private static ScheduledActivity AtHome(NpcRecord npc, bool sleeping)
        {
            return new ScheduledActivity { Activity = sleeping ? ActivityKind.Sleeping : ActivityKind.AtHome, Place = npc.Home };
        }

        private static ScheduledActivity Commute(EntityId from, EntityId to, float progress)
        {
            return new ScheduledActivity { Activity = ActivityKind.Commuting, FromPlace = from, Place = to, Progress = Math.Max(0f, Math.Min(1f, progress)) };
        }
    }
}
