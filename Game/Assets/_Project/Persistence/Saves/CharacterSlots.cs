using System;
using System.Collections.Generic;
using System.IO;

namespace HeroGame.Persistence.Saves
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Foundation;
    using HeroGame.Persistence.Storage;

    /// <summary>
    /// The three characters a player can keep on this machine. Each slot is its own person with its own worlds:
    /// its own story save and its own local servers (<see cref="WorldId"/>), and online its own character on every
    /// server (the slot number travels in the join hello). Slot 1 carries on from the single "local" profile that
    /// came before slots, so existing saves keep working.
    /// </summary>
    public sealed class CharacterSlots
    {
        public const int Count = 3;
        public const string LegacyKey = "local";
        private const string ActiveFile = "active-slot.txt";

        private readonly AccountStore _store;
        private readonly string _directory;

        public CharacterSlots(string accountsDirectory)
        {
            _directory = accountsDirectory;
            _store = new AccountStore(accountsDirectory);
            MigrateLegacy();
        }

        public static string Key(int slot) => "local-slot-" + Check(slot);

        /// <summary>Offline account id of a slot (the world's owner id for that character).</summary>
        public static EntityId AccountIdFor(int slot) => EntityId.Create(EntityKind.UserAccount, (ulong)Check(slot));

        /// <summary>
        /// Save-folder name of <paramref name="baseId"/> (a server id or story slot) for one character. Slot 1 uses
        /// the plain name (the folders that existed before slots); others add "-c2", "-c3".
        /// </summary>
        public static string WorldId(string baseId, int slot) => Check(slot) == 1 ? baseId : baseId + "-c" + slot;

        /// <summary>The slot a save-folder name belongs to (the inverse of <see cref="WorldId"/>).</summary>
        public static int SlotOfWorld(string worldId)
        {
            for (var slot = 2; slot <= Count; slot++)
                if (worldId.EndsWith("-c" + slot, StringComparison.Ordinal)) return slot;
            return 1;
        }

        /// <summary>The slot's character, or null if the slot is empty (never created, or deleted).</summary>
        public AccountProfile Get(int slot)
        {
            var profile = _store.Load(Key(slot));
            return profile != null && profile.CharacterCreated ? profile : null;
        }

        public bool IsEmpty(int slot) => Get(slot) == null;

        public bool Any
        {
            get
            {
                for (var slot = 1; slot <= Count; slot++) if (!IsEmpty(slot)) return true;
                return false;
            }
        }

        /// <summary>First empty slot, or 0 when all are taken.</summary>
        public int FirstEmpty
        {
            get
            {
                for (var slot = 1; slot <= Count; slot++) if (IsEmpty(slot)) return slot;
                return 0;
            }
        }

        /// <summary>The character played last (falls back to the first one that exists, else slot 1).</summary>
        public int Active
        {
            get
            {
                var path = Path.Combine(_directory, ActiveFile);
                if (File.Exists(path) && int.TryParse(AtomicFile.ReadAllText(path).Trim(), out var slot) && slot >= 1 && slot <= Count && !IsEmpty(slot)) return slot;
                for (var s = 1; s <= Count; s++) if (!IsEmpty(s)) return s;
                return 1;
            }
            set => AtomicFile.WriteAllText(Path.Combine(_directory, ActiveFile), Check(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>A blank profile for the creator, already carrying the slot's account id.</summary>
        public static AccountProfile NewProfile(int slot) => new AccountProfile
        {
            AccountId = AccountIdFor(slot),
            Character = new CharacterIdentity { FirstName = "", LastName = "", Age = 21 },
        };

        /// <summary>Stores a created character (name and look cleaned by <see cref="IdentityRules"/>) and makes it active.</summary>
        public void Save(int slot, AccountProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!IdentityRules.IsValid(profile.Character)) throw new ArgumentException("A character needs a first name and a surname.", nameof(profile));
            profile.AccountId = AccountIdFor(slot);
            profile.Character = IdentityRules.Sanitize(profile.Character);
            profile.CharacterCreated = true;
            if (string.IsNullOrEmpty(profile.DisplayName)) profile.DisplayName = profile.Character.FullName;
            _store.Save(Key(slot), profile);
            Active = slot;
        }

        /// <summary>Removes the slot's character. Its worlds are deleted by the caller (see <see cref="WorldFolders"/>).</summary>
        public void Delete(int slot)
        {
            _store.Delete(Key(slot));
            if (slot == 1) _store.Delete(LegacyKey);
        }

        /// <summary>
        /// Every save folder under <paramref name="saveRoot"/> (servers/… and story/…) that belongs to the slot, so
        /// deleting a character can delete its worlds too.
        /// </summary>
        public static List<string> WorldFolders(string saveRoot, int slot)
        {
            Check(slot);
            var result = new List<string>();
            foreach (var kind in new[] { "servers", "story" })
            {
                var dir = Path.Combine(saveRoot, kind);
                if (!Directory.Exists(dir)) continue;
                var folders = new List<string>(Directory.GetDirectories(dir));
                folders.Sort(StringComparer.Ordinal);
                foreach (var folder in folders)
                    if (SlotOfWorld(Path.GetFileName(folder)) == slot) result.Add(folder);
            }
            return result;
        }

        /// <summary>The single profile from before slots becomes slot 1 (once; the old file is left as a backup).</summary>
        private void MigrateLegacy()
        {
            if (_store.Exists(Key(1))) return;
            var legacy = _store.Load(LegacyKey);
            if (legacy == null || !legacy.CharacterCreated || !IdentityRules.IsValid(legacy.Character)) return;
            legacy.AccountId = AccountIdFor(1);
            _store.Save(Key(1), legacy);
        }

        private static int Check(int slot)
        {
            if (slot < 1 || slot > Count) throw new ArgumentOutOfRangeException(nameof(slot), "Character slots are 1 to " + Count + ".");
            return slot;
        }
    }
}
