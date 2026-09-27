using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;
using HeroGame.Core.World;

namespace HeroGame.Core.Population
{
    /// <summary>An NPC that should currently exist as a physical agent near an observer.</summary>
    public struct MaterializationRequest
    {
        public EntityId Npc;
        public SimulationTier Tier;
        public ScheduledActivity Activity;
        public WorldPosition Position;
        public float DistanceToObserver;
    }

    public sealed class PopulationDirectorSettings
    {
        public float FullRadius = 60f;
        public float NearbyRadius = 220f;
        public int MaxFull = 40;
        public int MaxNearby = 160;
    }

    /// <summary>
    /// Decides which persistent NPCs are physically present near players and at what tier (TDD §7.2).
    /// Positions come from <see cref="NpcLocationIndex"/>, which re-resolves an NPC only when its schedule
    /// changes, so an evaluation costs O(NPCs near observers + current commuters). Output is deterministic.
    /// Interiors are resolved separately by the runtime (NPCs inside buildings are spawned when the player
    /// enters the interior, not on the street).
    /// </summary>
    public sealed class PopulationDirector
    {
        private readonly Geography _geography;
        private readonly NpcLocationIndex _index;
        public readonly PopulationDirectorSettings Settings;

        private readonly List<MaterializationRequest> _scratch = new List<MaterializationRequest>();
        private readonly HashSet<EntityId> _candidates = new HashSet<EntityId>();

        public PopulationDirector(PopulationRegistry population, Geography geography, ScheduleResolver schedules, PopulationDirectorSettings settings = null)
        {
            _geography = geography;
            _index = new NpcLocationIndex(population, geography, schedules);
            Settings = settings ?? new PopulationDirectorSettings();
        }

        public NpcLocationIndex Index => _index;

        /// <summary>Candidates examined and schedules re-resolved by the last evaluation (profiling).</summary>
        public int LastCandidates { get; private set; }
        public int LastSchedulesResolved { get; private set; }

        /// <summary>Call when something outside the daily simulation changes an NPC's schedule (hire, override, move).</summary>
        public void Invalidate(EntityId npc) => _index.Invalidate(npc);

        /// <summary>Returns NPCs that should be materialised, nearest first, respecting budgets.</summary>
        public List<MaterializationRequest> Evaluate(IReadOnlyList<WorldPosition> observers, GameDateTime now)
        {
            _index.Update(now);
            LastSchedulesResolved = _index.ResolvesLastUpdate;
            _scratch.Clear();
            _candidates.Clear();
            for (var i = 0; i < observers.Count; i++) _index.Candidates(observers[i], Settings.NearbyRadius, _candidates);
            LastCandidates = _candidates.Count;

            foreach (var id in _candidates)
            {
                if (!_index.TryGet(id, now, out var activity, out var position)) continue;
                var distance = NearestDistance(position, observers);
                if (distance > Settings.NearbyRadius) continue;
                _scratch.Add(new MaterializationRequest
                {
                    Npc = id,
                    Activity = activity,
                    Position = position,
                    DistanceToObserver = distance,
                    Tier = distance <= Settings.FullRadius ? SimulationTier.Full : SimulationTier.Nearby,
                });
            }

            // HashSet order is not meaningful; sort for deterministic output.
            _scratch.Sort((a, b) =>
            {
                var c = a.DistanceToObserver.CompareTo(b.DistanceToObserver);
                return c != 0 ? c : a.Npc.CompareTo(b.Npc);
            });

            var result = new List<MaterializationRequest>();
            int full = 0, nearby = 0;
            foreach (var r in _scratch)
            {
                var req = r;
                if (req.Tier == SimulationTier.Full && full >= Settings.MaxFull) req.Tier = SimulationTier.Nearby;
                if (req.Tier == SimulationTier.Full) full++;
                else if (nearby < Settings.MaxNearby) nearby++;
                else continue;
                result.Add(req);
            }
            return result;
        }

        /// <summary>Who is inside a place right now (interiors materialise these when the player enters).</summary>
        public List<EntityId> PeopleAt(Place place, GameDateTime now)
        {
            _index.Update(now);
            var list = new List<EntityId>();
            _index.AtPlace(place, list);
            return list;
        }

        /// <summary>Street position for an activity; commuters are interpolated between origin and destination.</summary>
        public bool TryGetPosition(ScheduledActivity activity, out WorldPosition position)
        {
            position = default;
            var to = _geography.GetPlace(activity.Place);
            if (to == null) return false;
            if (activity.Activity == ActivityKind.Commuting)
            {
                var from = _geography.GetPlace(activity.FromPlace);
                if (from == null)
                {
                    position = to.Position;
                    return true;
                }
                var t = activity.Progress;
                position = new WorldPosition(
                    from.Position.X + (to.Position.X - from.Position.X) * t,
                    from.Position.Y + (to.Position.Y - from.Position.Y) * t,
                    from.Position.Z + (to.Position.Z - from.Position.Z) * t);
                return true;
            }
            position = to.Position;
            return true;
        }

        private static float NearestDistance(WorldPosition p, IReadOnlyList<WorldPosition> observers)
        {
            var best = float.MaxValue;
            for (var i = 0; i < observers.Count; i++) best = Math.Min(best, WorldPosition.DistanceXZ(p, observers[i]));
            return best;
        }
    }
}
