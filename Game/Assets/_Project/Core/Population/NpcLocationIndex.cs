using System;
using System.Collections.Generic;

namespace HeroGame.Core.Population
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Time;
    using HeroGame.Core.World;

    /// <summary>
    /// Where every NPC is, kept current incrementally (TDD §5.3). Each NPC's schedule is resolved once and
    /// cached until the resolver says it expires, so work per update is proportional to the number of
    /// people whose activity actually changed — not to the population. Stationary NPCs live in a spatial
    /// hash; commuters are kept in a separate set and interpolated along their route on demand.
    /// </summary>
    public sealed class NpcLocationIndex
    {
        private struct Entry
        {
            public ScheduledActivity Activity;
            public long ResolvedAt;
            public long ValidUntil;
            public long Cell;
            public bool InGrid;
            public WorldPosition Position;
        }

        private readonly PopulationRegistry _population;
        private readonly Geography _geography;
        private readonly ScheduleResolver _schedules;
        private readonly Dictionary<EntityId, Entry> _entries = new Dictionary<EntityId, Entry>();
        private readonly Dictionary<long, HashSet<EntityId>> _grid = new Dictionary<long, HashSet<EntityId>>();
        private readonly HashSet<EntityId> _commuters = new HashSet<EntityId>();
        private readonly List<KeyValuePair<long, EntityId>> _heap = new List<KeyValuePair<long, EntityId>>();
        private long _lastUpdate = long.MinValue;
        private int _indexedCount;

        public const float CellSize = 64f;

        public int ResolvesLastUpdate { get; private set; }
        public int Count => _entries.Count;
        public int CommuterCount => _commuters.Count;

        public NpcLocationIndex(PopulationRegistry population, Geography geography, ScheduleResolver schedules)
        {
            _population = population;
            _geography = geography;
            _schedules = schedules;
        }

        /// <summary>Brings every entry up to date for <paramref name="now"/>.</summary>
        public void Update(GameDateTime now)
        {
            ResolvesLastUpdate = 0;
            if (now.TotalSeconds < _lastUpdate) Clear(); // time never runs backwards in play; tests and tools may rewind
            _lastUpdate = now.TotalSeconds;

            var ordered = _population.Ordered;
            if (_indexedCount != ordered.Count)
            {
                for (var i = 0; i < ordered.Count; i++)
                    if (!_entries.ContainsKey(ordered[i].Id)) Refresh(ordered[i].Id, now);
                _indexedCount = ordered.Count;
            }

            while (_heap.Count > 0 && _heap[0].Key <= now.TotalSeconds)
            {
                var top = Pop();
                if (_entries.TryGetValue(top.Value, out var e) && e.ValidUntil == top.Key) Refresh(top.Value, now);
            }
        }

        /// <summary>Forces re-resolution (job change, override, move) at the next update.</summary>
        public void Invalidate(EntityId npc)
        {
            if (!_entries.TryGetValue(npc, out var e)) return;
            e.ValidUntil = long.MinValue;
            _entries[npc] = e;
            Push(long.MinValue, npc);
        }

        public void Clear()
        {
            _entries.Clear();
            _grid.Clear();
            _commuters.Clear();
            _heap.Clear();
            _indexedCount = 0;
        }

        public bool TryGet(EntityId npc, GameDateTime now, out ScheduledActivity activity, out WorldPosition position)
        {
            activity = default;
            position = default;
            if (!_entries.TryGetValue(npc, out var e)) return false;
            activity = e.Activity;
            if (e.Activity.Activity == ActivityKind.Commuting)
            {
                // Linear progress from the resolved point to arrival at ValidUntil.
                var span = Math.Max(1L, e.ValidUntil - e.ResolvedAt);
                var t = Math.Max(0.0, Math.Min(1.0, (now.TotalSeconds - e.ResolvedAt) / (double)span));
                activity.Progress = (float)(e.Activity.Progress + (1.0 - e.Activity.Progress) * t);
                return Interpolate(activity, out position);
            }
            if (!e.InGrid) return false;
            position = e.Position;
            return true;
        }

        /// <summary>Appends NPCs whose stationary position is within the radius, plus every current commuter.</summary>
        public void Candidates(WorldPosition center, float radius, HashSet<EntityId> results)
        {
            int x0 = Coord(center.X - radius), x1 = Coord(center.X + radius);
            int z0 = Coord(center.Z - radius), z1 = Coord(center.Z + radius);
            for (var x = x0; x <= x1; x++)
            for (var z = z0; z <= z1; z++)
                if (_grid.TryGetValue(Key(x, z), out var cell))
                    foreach (var id in cell) results.Add(id);
            foreach (var id in _commuters) results.Add(id);
        }

        private readonly HashSet<long> _cellScratch = new HashSet<long>();

        /// <summary>
        /// Everyone in the grid cells within <paramref name="radius"/> of any observer, plus commuters. Cells are
        /// collected first (a person is in exactly one cell), so overlapping observers cost nothing extra and no
        /// per-person de-duplication is needed.
        /// </summary>
        public void Candidates(IReadOnlyList<WorldPosition> observers, float radius, List<EntityId> results)
        {
            _cellScratch.Clear();
            foreach (var center in observers)
            {
                int x0 = Coord(center.X - radius), x1 = Coord(center.X + radius);
                int z0 = Coord(center.Z - radius), z1 = Coord(center.Z + radius);
                for (var x = x0; x <= x1; x++)
                for (var z = z0; z <= z1; z++)
                    _cellScratch.Add(Key(x, z));
            }
            foreach (var key in _cellScratch)
                if (_grid.TryGetValue(key, out var cell))
                    results.AddRange(cell);
            results.AddRange(_commuters);
        }

        /// <summary>NPCs whose current (non-commuting) activity is at <paramref name="place"/>, in id order.</summary>
        public void AtPlace(Place place, List<EntityId> results)
        {
            if (place == null) return;
            if (!_grid.TryGetValue(Key(Coord(place.Position.X), Coord(place.Position.Z)), out var cell)) return;
            var start = results.Count;
            foreach (var id in cell)
                if (_entries.TryGetValue(id, out var e) && e.Activity.Place == place.Id) results.Add(id);
            results.Sort(start, results.Count - start, null);
        }

        private void Refresh(EntityId id, GameDateTime now)
        {
            var npc = _population.Get(id);
            if (npc == null) return;
            ResolvesLastUpdate++;
            if (_entries.TryGetValue(id, out var old))
            {
                if (old.InGrid && _grid.TryGetValue(old.Cell, out var oldCell)) oldCell.Remove(id);
                _commuters.Remove(id);
            }

            var activity = _schedules.Resolve(npc, now, out var validUntil);
            var e = new Entry { Activity = activity, ResolvedAt = now.TotalSeconds, ValidUntil = validUntil.TotalSeconds };
            if (activity.Activity == ActivityKind.Commuting)
            {
                _commuters.Add(id);
            }
            else if (Interpolate(activity, out var position))
            {
                e.Position = position;
                e.Cell = Key(Coord(position.X), Coord(position.Z));
                e.InGrid = true;
                if (!_grid.TryGetValue(e.Cell, out var cell))
                {
                    cell = new HashSet<EntityId>();
                    _grid.Add(e.Cell, cell);
                }
                cell.Add(id);
            }
            _entries[id] = e;
            Push(e.ValidUntil, id);
        }

        private bool Interpolate(ScheduledActivity a, out WorldPosition position)
        {
            position = default;
            var to = _geography.GetPlace(a.Place);
            if (to == null) return false;
            if (a.Activity != ActivityKind.Commuting)
            {
                position = to.Position;
                return true;
            }
            var from = _geography.GetPlace(a.FromPlace);
            if (from == null)
            {
                position = to.Position;
                return true;
            }
            var t = a.Progress;
            position = new WorldPosition(
                from.Position.X + (to.Position.X - from.Position.X) * t,
                from.Position.Y + (to.Position.Y - from.Position.Y) * t,
                from.Position.Z + (to.Position.Z - from.Position.Z) * t);
            return true;
        }

        // Binary min-heap on (validUntil, id) with lazy deletion.
        private void Push(long key, EntityId id)
        {
            _heap.Add(new KeyValuePair<long, EntityId>(key, id));
            var i = _heap.Count - 1;
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (Less(_heap[parent], _heap[i])) break;
                Swap(i, parent);
                i = parent;
            }
        }

        private KeyValuePair<long, EntityId> Pop()
        {
            var top = _heap[0];
            var last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            var i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, smallest = i;
                if (l < _heap.Count && Less(_heap[l], _heap[smallest])) smallest = l;
                if (r < _heap.Count && Less(_heap[r], _heap[smallest])) smallest = r;
                if (smallest == i) break;
                Swap(i, smallest);
                i = smallest;
            }
            return top;
        }

        private static bool Less(KeyValuePair<long, EntityId> a, KeyValuePair<long, EntityId> b)
        {
            return a.Key < b.Key || (a.Key == b.Key && a.Value.Value < b.Value.Value);
        }

        private void Swap(int a, int b)
        {
            var t = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = t;
        }

        private static int Coord(float v) => (int)Math.Floor(v / CellSize);
        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
