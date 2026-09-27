using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Population
{
    /// <summary>
    /// Stores every NPC and household, plus a reverse index from places to the NPCs that are
    /// associated with them (live, work, study, frequent). The index lets the population director
    /// find who could plausibly be near a player without scanning the whole population.
    /// </summary>
    public sealed class PopulationRegistry
    {
        private readonly Dictionary<EntityId, NpcRecord> _npcs = new Dictionary<EntityId, NpcRecord>();
        private readonly Dictionary<EntityId, Household> _households = new Dictionary<EntityId, Household>();
        private readonly Dictionary<EntityId, HashSet<EntityId>> _npcsByPlace = new Dictionary<EntityId, HashSet<EntityId>>();
        private readonly List<NpcRecord> _ordered = new List<NpcRecord>();
        private bool _orderDirty;

        public int Count => _npcs.Count;
        public IEnumerable<Household> Households => _households.Values;
        public int HouseholdCount => _households.Count;

        /// <summary>All NPCs in stable id order (deterministic iteration for simulation passes).</summary>
        public IReadOnlyList<NpcRecord> Ordered
        {
            get
            {
                if (_orderDirty)
                {
                    _ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
                    _orderDirty = false;
                }
                return _ordered;
            }
        }

        public void Add(NpcRecord npc)
        {
            if (_npcs.ContainsKey(npc.Id)) return;
            _npcs.Add(npc.Id, npc);
            _ordered.Add(npc);
            _orderDirty = true;
            IndexNpc(npc);
        }

        public void Add(Household household) => _households[household.Id] = household;

        public NpcRecord Get(EntityId id) => _npcs.TryGetValue(id, out var n) ? n : null;
        public Household GetHousehold(EntityId id) => _households.TryGetValue(id, out var h) ? h : null;

        public IReadOnlyCollection<EntityId> AssociatedWith(EntityId place)
        {
            return _npcsByPlace.TryGetValue(place, out var set) ? (IReadOnlyCollection<EntityId>)set : System.Array.Empty<EntityId>();
        }

        /// <summary>Call after changing an NPC's home, workplace, school or favourite places.</summary>
        public void Reindex(NpcRecord npc)
        {
            foreach (var set in _npcsByPlace.Values) set.Remove(npc.Id);
            IndexNpc(npc);
        }

        private void IndexNpc(NpcRecord npc)
        {
            Associate(npc.Home, npc.Id);
            Associate(npc.Workplace, npc.Id);
            Associate(npc.School, npc.Id);
            foreach (var p in npc.FavoritePlaces) Associate(p, npc.Id);
        }

        private void Associate(EntityId place, EntityId npc)
        {
            if (!place.IsValid) return;
            if (!_npcsByPlace.TryGetValue(place, out var set))
            {
                set = new HashSet<EntityId>();
                _npcsByPlace.Add(place, set);
            }
            set.Add(npc);
        }

        public List<NpcRecord> FindByName(string text)
        {
            var result = new List<NpcRecord>();
            if (string.IsNullOrEmpty(text)) return result;
            var lower = text.ToLowerInvariant();
            foreach (var n in Ordered) if (n.FullName.ToLowerInvariant().Contains(lower)) result.Add(n);
            return result;
        }
    }
}
