using System;

namespace HeroGame.Core.Time
{
    /// <summary>
    /// Persistent server clock (GDD §100). Converts real elapsed time to game time
    /// using a configurable day length. Fractional seconds are accumulated so no
    /// time is lost at high frame rates.
    /// </summary>
    [Serializable]
    public sealed class WorldClock
    {
        public GameDateTime Now;
        /// <summary>Game seconds that elapse per real second. 30 → a 48 real-minute day.</summary>
        public double TimeScale = 30.0;
        public bool Paused;

        private double _fraction;

        public WorldClock() { }

        public WorldClock(GameDateTime start, double timeScale)
        {
            Now = start;
            TimeScale = timeScale;
        }

        public static double TimeScaleForDayLength(double realMinutesPerGameDay)
        {
            if (realMinutesPerGameDay <= 0) throw new ArgumentOutOfRangeException(nameof(realMinutesPerGameDay));
            return GameDateTime.SecondsPerDay / (realMinutesPerGameDay * 60.0);
        }

        /// <summary>Advances by real seconds; returns whole game seconds elapsed.</summary>
        public long AdvanceReal(double realSeconds)
        {
            if (Paused || realSeconds <= 0) return 0;
            var game = realSeconds * TimeScale + _fraction;
            var whole = (long)Math.Floor(game);
            _fraction = game - whole;
            Now = Now.AddSeconds(whole);
            return whole;
        }

        /// <summary>Jumps forward in game time (debug "advance time", sleeping, offline catch-up).</summary>
        public void AdvanceGame(long gameSeconds)
        {
            if (gameSeconds < 0) throw new ArgumentOutOfRangeException(nameof(gameSeconds), "Time never runs backwards.");
            Now = Now.AddSeconds(gameSeconds);
        }
    }
}
