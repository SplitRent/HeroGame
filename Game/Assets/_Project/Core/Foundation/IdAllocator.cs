using System;

namespace HeroGame.Core.Foundation
{
    /// <summary>
    /// Hands out monotonically increasing sequences per <see cref="EntityKind"/>.
    /// State is persisted with the world so ids are never reused, even across restarts.
    /// </summary>
    [Serializable]
    public sealed class IdAllocator
    {
        /// <summary>Last issued sequence, indexed by (int)EntityKind. Public for serialization.</summary>
        public ulong[] LastIssued = new ulong[256];

        public EntityId Next(EntityKind kind)
        {
            var index = (int)kind;
            var next = LastIssued[index] + 1;
            if (next > EntityId.MaxSequence) throw new InvalidOperationException("Id space exhausted for " + kind);
            LastIssued[index] = next;
            return EntityId.Create(kind, next);
        }

        /// <summary>Ensures future ids never collide with an id created elsewhere (e.g. imported content).</summary>
        public void Reserve(EntityId id)
        {
            if (!id.IsValid) return;
            var index = (int)id.Kind;
            if (LastIssued[index] < id.Sequence) LastIssued[index] = id.Sequence;
        }
    }
}
