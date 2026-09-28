using System;
using System.Collections.Generic;
using System.IO;

namespace HeroGame.Persistence.Saves
{
    using HeroGame.Core.Characters;
    using HeroGame.Persistence.Json;
    using HeroGame.Persistence.Storage;

    /// <summary>
    /// Local store for global account profiles (name + appearance). In multiplayer the account
    /// service is authoritative and this acts as a cache; offline/Story it is the source of truth.
    /// </summary>
    public sealed class AccountStore
    {
        private readonly string _directory;

        public AccountStore(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(directory);
        }

        public AccountProfile Load(string accountKey)
        {
            var path = PathFor(accountKey);
            return File.Exists(path) ? JsonSetup.Deserialize<AccountProfile>(AtomicFile.ReadAllText(path)) : null;
        }

        public void Save(string accountKey, AccountProfile profile)
        {
            AtomicFile.WriteAllText(PathFor(accountKey), JsonSetup.Serialize(profile, true));
        }

        private string PathFor(string key)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            return Path.Combine(_directory, key + ".profile.json");
        }
    }

    public enum SlotKind
    {
        Manual,
        Autosave,
        Checkpoint,
    }

    [Serializable]
    public sealed class SlotInfo
    {
        public string SlotId = "";
        public SlotKind Kind;
        public string Label = "";
        public string ChapterId = "";
        public string MissionId = "";
        public long GameTimeSeconds;
        public long SavedAtUnix;
        public double PlaytimeSeconds;
    }

    /// <summary>
    /// Story Mode save slots (GDD §172): manual slots, rotating autosaves and a mission checkpoint.
    /// Each slot is a full <see cref="WorldSaveSystem"/> directory, so Story uses the same
    /// persistence code as servers — never a single fragile save file.
    /// </summary>
    public sealed class StorySlotManager
    {
        public const int ManualSlots = 10;
        public const int AutosaveRotation = 3;

        private readonly string _root;

        public StorySlotManager(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
        }

        public string DirectoryFor(string slotId) => Path.Combine(_root, slotId);

        public string ManualSlotId(int index)
        {
            if (index < 0 || index >= ManualSlots) throw new ArgumentOutOfRangeException(nameof(index));
            return "manual_" + index;
        }

        /// <summary>Returns the autosave slot to write next (oldest of the rotation).</summary>
        public string NextAutosaveSlot()
        {
            string oldest = null;
            long oldestTime = long.MaxValue;
            for (var i = 0; i < AutosaveRotation; i++)
            {
                var id = "auto_" + i;
                var info = ReadInfo(id);
                if (info == null) return id;
                if (info.SavedAtUnix < oldestTime)
                {
                    oldestTime = info.SavedAtUnix;
                    oldest = id;
                }
            }
            return oldest;
        }

        public const string CheckpointSlot = "checkpoint";

        public void WriteInfo(SlotInfo info)
        {
            Directory.CreateDirectory(DirectoryFor(info.SlotId));
            AtomicFile.WriteAllText(Path.Combine(DirectoryFor(info.SlotId), "slot.json"), JsonSetup.Serialize(info, true));
        }

        public SlotInfo ReadInfo(string slotId)
        {
            var path = Path.Combine(DirectoryFor(slotId), "slot.json");
            return File.Exists(path) ? JsonSetup.Deserialize<SlotInfo>(AtomicFile.ReadAllText(path)) : null;
        }

        public List<SlotInfo> List()
        {
            var result = new List<SlotInfo>();
            if (!Directory.Exists(_root)) return result;
            foreach (var dir in Directory.GetDirectories(_root))
            {
                var info = ReadInfo(Path.GetFileName(dir));
                if (info != null) result.Add(info);
            }
            result.Sort((a, b) => b.SavedAtUnix.CompareTo(a.SavedAtUnix));
            return result;
        }
    }
}
