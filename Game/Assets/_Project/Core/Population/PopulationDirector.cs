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
        /// <summary>Only places within this radius are considered at all.</summary>
        public float ConsiderRadius = 260f;
        public int MaxFull = 40;
        public int MaxNearby = 160;
    }

    /// <summary>
    /// Decides which persistent NPCs are physically present near players and at what tier
    /// (TDD §7.2). Candidates come from the place→NPC index, so cost scales with the number of
    /// nearby places rather than the size of the city. Output is deterministic for a given time.
    /// Interiors are resolved separately by the runtime (NPCs inside buildings are not spawned on
    /// the street; they are spawned when the player enters the interior cell).
    /// </summary>
    public sealed class PopulationDirector
    {
        private readonly PopulationRegistry _population;
        private readonly Geography _geography;
        private readonly ScheduleResolver _schedules;
        public readonly PopulationDirectorSettings Settings;

        private readonly List<MaterializationRequest> _scratch = new List<MaterializationRequest>();
        private readonly HashSet<EntityId> _seen = new HashSet<EntityId>();

        public PopulationDirector(PopulationRegistry population, Geography geography, ScheduleResolver schedules, PopulationDirectorSettings settings = null)
        {
            _population = population;
            _geography = geography;
            _schedules = schedules;
            Settings = settings ?? new PopulationDirectorSettings();
        }

        /// <summary>Returns NPCs that should be materialised, nearest first, respecting budgets.</summary>
        public List<MaterializationRequest> Evaluate(IReadOnlyList<WorldPosition> observers, GameDateTime now)
        {
            _scratch.Clear();
            _seen.Clear();
            var considerSq = Settings.ConsiderRadius * Settings.ConsiderRadius;

            foreach (var place in _geography.Places)
            {
                if (!NearAny(place.Position, observers, considerSq)) continue;
                foreach (var npcId in _population.AssociatedWith(place.Id))
                {
                    if (!_seen.Add(npcId)) continue;
                    var npc = _population.Get(npcId);
                    if (npc == null || !npc.Alive) continue;
                    var activity = _schedules.Resolve(npc, now);
                    if (!TryGetPosition(activity, out var position)) continue;
                    var distance = NearestDistance(position, observers);
                    if (distance > Settings.NearbyRadius) continue;
                    _scratch.Add(new MaterializationRequest
                    {
                        Npc = npcId,
                        Activity = activity,
                        Position = position,
                        DistanceToObserver = distance,
                        Tier = distance <= Settings.FullRadius ? SimulationTier.Full : SimulationTier.Nearby,
                    });
                }
            }

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

        private static bool NearAny(WorldPosition p, IReadOnlyList<WorldPosition> observers, float radiusSq)
        {
            for (var i = 0; i < observers.Count; i++)
                if (WorldPosition.DistanceSquaredXZ(p, observers[i]) <= radiusSq) return true;
            return false;
        }

        private static float NearestDistance(WorldPosition p, IReadOnlyList<WorldPosition> observers)
        {
            var best = float.MaxValue;
            for (var i = 0; i < observers.Count; i++) best = Math.Min(best, WorldPosition.DistanceXZ(p, observers[i]));
            return best;
        }
    }
}
