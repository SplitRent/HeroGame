using System;
using System.Collections.Generic;

namespace HeroGame.Core.Economy
{
    using HeroGame.Core.Foundation;

    [Serializable]
    public struct OwnershipEntry
    {
        public EntityId Asset;
        public EntityId Owner;
    }

    /// <summary>
    /// Single source of truth for who owns every transferable asset (property, vehicle,
    /// business, organisation-held items). Assets not present are unowned (city/market stock).
    /// </summary>
    public sealed class OwnershipRegistry
    {
        private readonly Dictionary<EntityId, EntityId> _ownerByAsset = new Dictionary<EntityId, EntityId>();
        private readonly Dictionary<EntityId, HashSet<EntityId>> _assetsByOwner = new Dictionary<EntityId, HashSet<EntityId>>();

        public event Action<EntityId, EntityId, EntityId> Transferred;

        public int Count => _ownerByAsset.Count;

        public EntityId OwnerOf(EntityId asset) => _ownerByAsset.TryGetValue(asset, out var owner) ? owner : EntityId.None;

        public bool IsOwnedBy(EntityId asset, EntityId owner) => OwnerOf(asset) == owner;

        public IReadOnlyCollection<EntityId> AssetsOf(EntityId owner)
        {
            return _assetsByOwner.TryGetValue(owner, out var set) ? (IReadOnlyCollection<EntityId>)set : Array.Empty<EntityId>();
        }

        public IEnumerable<OwnershipEntry> Entries
        {
            get
            {
                foreach (var kv in _ownerByAsset) yield return new OwnershipEntry { Asset = kv.Key, Owner = kv.Value };
            }
        }

        public OpResult ValidateTransfer(EntityId asset, EntityId expectedCurrentOwner, EntityId newOwner)
        {
            if (!asset.IsValid) return OpResult.Fail("Invalid asset id.");
            var current = OwnerOf(asset);
            if (current != expectedCurrentOwner) return OpResult.Fail("Asset " + asset + " is owned by " + current + ", not " + expectedCurrentOwner + ".");
            if (current == newOwner) return OpResult.Fail("Asset already owned by " + newOwner + ".");
            return OpResult.Ok();
        }

        /// <summary>Sets ownership without validation; <see cref="EntityId.None"/> releases the asset.</summary>
        internal void SetOwnerUnchecked(EntityId asset, EntityId newOwner)
        {
            var previous = OwnerOf(asset);
            if (previous.IsValid && _assetsByOwner.TryGetValue(previous, out var oldSet))
            {
                oldSet.Remove(asset);
                if (oldSet.Count == 0) _assetsByOwner.Remove(previous);
            }
            if (newOwner.IsValid)
            {
                _ownerByAsset[asset] = newOwner;
                if (!_assetsByOwner.TryGetValue(newOwner, out var set))
                {
                    set = new HashSet<EntityId>();
                    _assetsByOwner.Add(newOwner, set);
                }
                set.Add(asset);
            }
            else
            {
                _ownerByAsset.Remove(asset);
            }
            Transferred?.Invoke(asset, previous, newOwner);
        }

        /// <summary>World generation / load only. Gameplay transfers must go through <see cref="TransactionProcessor"/>.</summary>
        public void AssignInitial(EntityId asset, EntityId owner) => SetOwnerUnchecked(asset, owner);
    }
}
