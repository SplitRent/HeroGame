namespace HeroGame.Runtime.Bootstrap
{
    /// <summary>What the front end asked the next gameplay scene to open. Consumed once by <see cref="GameBootstrap"/>.</summary>
    public static class LaunchRequest
    {
        public static bool Pending { get; private set; }
        public static SessionMode Mode { get; private set; }
        public static string ServerId { get; private set; } = "";
        public static string SaveSlot { get; private set; } = "";

        public static void Set(SessionMode mode, string serverId, string saveSlot)
        {
            Mode = mode;
            ServerId = serverId;
            SaveSlot = saveSlot;
            Pending = true;
        }

        public static bool TryConsume(out SessionMode mode, out string serverId, out string saveSlot)
        {
            mode = Mode;
            serverId = ServerId;
            saveSlot = SaveSlot;
            if (!Pending) return false;
            Pending = false;
            return true;
        }
    }

    /// <summary>Title and version shown in the front end.</summary>
    public static class GameInfo
    {
        /// <summary>
        /// WORKING TITLE ONLY. "Second Life" is a registered trademark of Linden Research, Inc. and must be
        /// replaced before any public build, store page or marketing. Change it here; nothing else hardcodes it.
        /// </summary>
        public const string WorkingTitle = "SECOND LIFE";
        public const string Version = "0.1.0-foundation";
    }
}
