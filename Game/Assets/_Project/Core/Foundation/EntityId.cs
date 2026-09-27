using System;
using System.Globalization;

namespace HeroGame.Core.Foundation
{
    /// <summary>
    /// Category encoded into the top 8 bits of every <see cref="EntityId"/>.
    /// Values are persisted; never renumber an existing entry.
    /// </summary>
    public enum EntityKind : byte
    {
        None = 0,
        UserAccount = 1,
        Character = 2,
        Npc = 3,
        Household = 4,
        Property = 5,
        Business = 6,
        Vehicle = 7,
        Organization = 8,
        Place = 9,
        LedgerAccount = 10,
        District = 11,
        Loan = 12,
        Item = 13,
        CrimeIncident = 14,
        AnomalyEvent = 15,
        Power = 16,
        Server = 17,
        HistoryRecord = 18,
    }

    /// <summary>
    /// Stable 64-bit identity for every persistent object (TDD §6.1).
    /// Layout: [8 bits kind][56 bits sequence]. Zero is "no entity".
    /// </summary>
    [Serializable]
    public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
    {
        private const int KindShift = 56;
        public const ulong MaxSequence = (1UL << KindShift) - 1;

        public static readonly EntityId None = default;

        public readonly ulong Value;

        public EntityId(ulong value)
        {
            Value = value;
        }

        public static EntityId Create(EntityKind kind, ulong sequence)
        {
            if (kind == EntityKind.None) throw new ArgumentException("Kind must not be None.", nameof(kind));
            if (sequence == 0 || sequence > MaxSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
            return new EntityId(((ulong)kind << KindShift) | sequence);
        }

        public EntityKind Kind => (EntityKind)(Value >> KindShift);
        public ulong Sequence => Value & MaxSequence;
        public bool IsValid => Value != 0;

        public bool Equals(EntityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is EntityId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(EntityId other) => Value.CompareTo(other.Value);

        public static bool operator ==(EntityId a, EntityId b) => a.Value == b.Value;
        public static bool operator !=(EntityId a, EntityId b) => a.Value != b.Value;

        /// <summary>Human readable form, e.g. <c>Npc:42</c>. Round-trips through <see cref="Parse"/>.</summary>
        public override string ToString()
        {
            return IsValid ? Kind + ":" + Sequence.ToString(CultureInfo.InvariantCulture) : "None";
        }

        public static EntityId Parse(string text)
        {
            if (TryParse(text, out var id)) return id;
            throw new FormatException("Invalid EntityId: " + text);
        }

        public static bool TryParse(string text, out EntityId id)
        {
            id = None;
            if (string.IsNullOrEmpty(text)) return false;
            if (text == "None") return true;
            var colon = text.IndexOf(':');
            if (colon <= 0) return false;
            if (!Enum.TryParse(text.Substring(0, colon), out EntityKind kind) || kind == EntityKind.None) return false;
            if (!ulong.TryParse(text.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)) return false;
            if (seq == 0 || seq > MaxSequence) return false;
            id = Create(kind, seq);
            return true;
        }
    }
}
