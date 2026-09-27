using System;
using System.Globalization;

namespace HeroGame.Core.Time
{
    public enum Season
    {
        Winter,
        Spring,
        Summer,
        Autumn,
    }

    public enum DayPart
    {
        Night,
        Morning,
        Afternoon,
        Evening,
    }

    /// <summary>
    /// In-world timestamp: whole seconds since the game epoch (00:00, 1 Jan 2000).
    /// Integer based so every system agrees on "now" and save files are exact.
    /// </summary>
    [Serializable]
    public readonly struct GameDateTime : IEquatable<GameDateTime>, IComparable<GameDateTime>
    {
        public const long SecondsPerMinute = 60;
        public const long SecondsPerHour = 3600;
        public const long SecondsPerDay = 86400;

        private static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        public readonly long TotalSeconds;

        public GameDateTime(long totalSeconds)
        {
            TotalSeconds = totalSeconds;
        }

        public static GameDateTime FromCalendar(int year, int month, int day, int hour = 0, int minute = 0)
        {
            var dt = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            return new GameDateTime((long)(dt - Epoch).TotalSeconds);
        }

        public DateTime ToDateTime() => Epoch.AddSeconds(TotalSeconds);

        /// <summary>Whole days since epoch. Used to key per-day deterministic simulation.</summary>
        public long DayIndex => FloorDiv(TotalSeconds, SecondsPerDay);

        public long HourIndex => FloorDiv(TotalSeconds, SecondsPerHour);
        public int SecondOfDay => (int)(TotalSeconds - DayIndex * SecondsPerDay);
        public int MinuteOfDay => SecondOfDay / 60;
        public int Hour => SecondOfDay / 3600;
        public int Minute => (SecondOfDay / 60) % 60;
        public float HourFloat => SecondOfDay / 3600f;

        public int Year => ToDateTime().Year;
        public int Month => ToDateTime().Month;
        public int Day => ToDateTime().Day;
        public DayOfWeek DayOfWeek => ToDateTime().DayOfWeek;
        public bool IsWeekend => DayOfWeek == DayOfWeek.Saturday || DayOfWeek == DayOfWeek.Sunday;

        public Season Season
        {
            get
            {
                var m = Month;
                if (m == 12 || m <= 2) return Season.Winter;
                if (m <= 5) return Season.Spring;
                if (m <= 8) return Season.Summer;
                return Season.Autumn;
            }
        }

        /// <summary>Atlantic hurricane season window (June through November).</summary>
        public bool IsHurricaneSeason => Month >= 6 && Month <= 11;

        public DayPart DayPart
        {
            get
            {
                var h = Hour;
                if (h < 5) return DayPart.Night;
                if (h < 12) return DayPart.Morning;
                if (h < 18) return DayPart.Afternoon;
                if (h < 22) return DayPart.Evening;
                return DayPart.Night;
            }
        }

        public GameDateTime StartOfDay => new GameDateTime(DayIndex * SecondsPerDay);

        public GameDateTime AddSeconds(long seconds) => new GameDateTime(checked(TotalSeconds + seconds));
        public GameDateTime AddMinutes(long minutes) => AddSeconds(minutes * SecondsPerMinute);
        public GameDateTime AddHours(long hours) => AddSeconds(hours * SecondsPerHour);
        public GameDateTime AddDays(long days) => AddSeconds(days * SecondsPerDay);

        public static long operator -(GameDateTime a, GameDateTime b) => a.TotalSeconds - b.TotalSeconds;
        public static bool operator <(GameDateTime a, GameDateTime b) => a.TotalSeconds < b.TotalSeconds;
        public static bool operator >(GameDateTime a, GameDateTime b) => a.TotalSeconds > b.TotalSeconds;
        public static bool operator <=(GameDateTime a, GameDateTime b) => a.TotalSeconds <= b.TotalSeconds;
        public static bool operator >=(GameDateTime a, GameDateTime b) => a.TotalSeconds >= b.TotalSeconds;
        public static bool operator ==(GameDateTime a, GameDateTime b) => a.TotalSeconds == b.TotalSeconds;
        public static bool operator !=(GameDateTime a, GameDateTime b) => a.TotalSeconds != b.TotalSeconds;

        public bool Equals(GameDateTime other) => TotalSeconds == other.TotalSeconds;
        public override bool Equals(object obj) => obj is GameDateTime other && Equals(other);
        public override int GetHashCode() => TotalSeconds.GetHashCode();
        public int CompareTo(GameDateTime other) => TotalSeconds.CompareTo(other.TotalSeconds);

        public override string ToString() => ToDateTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private static long FloorDiv(long a, long b)
        {
            var q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }
    }
}
