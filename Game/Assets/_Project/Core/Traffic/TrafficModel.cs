using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;
using HeroGame.Core.Weather;

namespace HeroGame.Core.Traffic
{
    public struct TrafficSpawn
    {
        public int Edge;
        /// <summary>0..1 along the edge from From to To.</summary>
        public float T;
        /// <summary>True when travelling To→From.</summary>
        public bool Reverse;
        public int Lane;
        public ulong Seed;
        public float SpeedMs;
    }

    /// <summary>
    /// Statistical traffic (GDD §35, §184): every road segment has a flow (vehicles/hour) from an hourly
    /// demand curve, city population, server density and weather. Travel times follow the BPR congestion
    /// function, so GPS routes avoid rush hour. Near players the flow becomes concrete, deterministic spawn
    /// positions for ambient cars, so traffic density matches the simulation and never pops in arbitrarily.
    /// </summary>
    public sealed class TrafficModel
    {
        private static readonly float[] HourlyDemand =
        {
            0.08f, 0.05f, 0.04f, 0.04f, 0.07f, 0.2f, 0.55f, 0.95f, 1.0f, 0.7f, 0.55f, 0.6f,
            0.65f, 0.6f, 0.6f, 0.7f, 0.9f, 1.0f, 0.85f, 0.6f, 0.45f, 0.35f, 0.25f, 0.14f,
        };

        private readonly RoadNetwork _network;
        private readonly ulong _seed;
        public float DensityMultiplier = 1f;
        /// <summary>Peak vehicles/hour per lane on an avenue for the whole-city population baseline.</summary>
        public float PeakFlowPerLane = 1100f;

        public TrafficModel(RoadNetwork network, ulong seed, float densityMultiplier)
        {
            _network = network;
            _seed = seed;
            DensityMultiplier = densityMultiplier;
        }

        public RoadNetwork Network => _network;

        public static float DemandAt(GameDateTime t)
        {
            var h = t.Hour;
            var next = (h + 1) % 24;
            var frac = t.Minute / 60f;
            var demand = HourlyDemand[h] + (HourlyDemand[next] - HourlyDemand[h]) * frac;
            return t.IsWeekend ? demand * 0.75f + 0.1f : demand;
        }

        /// <summary>Vehicles per hour per direction on an edge.</summary>
        public float Flow(RoadEdge e, GameDateTime t, WeatherEffects weather)
        {
            var kindShare = e.Kind == "avenue" || e.Kind == "bridge" ? 1f : e.Kind == "highway" ? 1.6f : 0.35f;
            var weatherFactor = weather.EmergencyDeclared ? 0.15f : 0.75f + 0.25f * weather.PedestrianDensity;
            return PeakFlowPerLane * kindShare * e.LanesPerDirection * DemandAt(t) * DensityMultiplier * weatherFactor;
        }

        /// <summary>BPR congested travel time: t0 · (1 + 0.15 (v/c)^4), slowed further by weather.</summary>
        public float TravelSeconds(RoadEdge e, GameDateTime t, WeatherEffects weather)
        {
            var vc = Flow(e, t, weather) / (e.CapacityPerLane * e.LanesPerDirection * Math.Max(0.1f, e.CapacityFactor));
            return e.FreeFlowSeconds * (1f + 0.15f * (float)Math.Pow(vc, 4)) / Math.Max(0.3f, weather.TrafficSpeedMultiplier);
        }

        /// <summary>Average vehicle spacing implies how many vehicles are on an edge right now.</summary>
        public float ExpectedVehicles(RoadEdge e, GameDateTime t, WeatherEffects weather)
        {
            var speed = e.Length / Math.Max(1f, TravelSeconds(e, t, weather));
            var perDirection = Flow(e, t, weather) / 3600f * e.Length / Math.Max(1f, speed); // density = flow / speed
            return perDirection * 2f;
        }

        /// <summary>
        /// Concrete ambient vehicles for edges within <paramref name="radius"/> of an observer. Positions come from a
        /// per-edge, per-minute stream so the same query gives the same cars, and cars advance smoothly between queries.
        /// </summary>
        public List<TrafficSpawn> Materialize(WorldPosition observer, float radius, GameDateTime t, WeatherEffects weather, int budget)
        {
            var result = new List<TrafficSpawn>();
            var candidates = new List<(float dist, RoadEdge edge)>();
            foreach (var e in _network.Edges)
            {
                var a = _network.Nodes[e.From].Position;
                var b = _network.Nodes[e.To].Position;
                var d = DistanceToSegment(observer, a, b);
                if (d <= radius) candidates.Add((d, e));
            }
            candidates.Sort((x, y) => x.dist != y.dist ? x.dist.CompareTo(y.dist) : x.edge.Id.CompareTo(y.edge.Id));
            foreach (var (_, e) in candidates)
            {
                var expected = ExpectedVehicles(e, t, weather);
                var rng = DeterministicRandom.For(_seed, (ulong)e.Id, (ulong)t.DayIndex, 0x7AFF);
                var count = (int)Math.Floor(expected + rng.NextDouble());
                var speed = e.Length / Math.Max(1f, TravelSeconds(e, t, weather));
                for (var i = 0; i < count && result.Count < budget; i++)
                {
                    var reverse = i % 2 == 1;
                    // Each vehicle has a stable phase; position = phase + distance travelled since midnight.
                    var phase = (float)rng.NextDouble();
                    var travelled = (t.SecondOfDay * speed / Math.Max(1f, e.Length)) % 1f;
                    var along = (phase + travelled) % 1f;
                    result.Add(new TrafficSpawn
                    {
                        Edge = e.Id,
                        T = reverse ? 1f - along : along,
                        Reverse = reverse,
                        Lane = rng.NextInt(0, e.LanesPerDirection),
                        Seed = rng.NextULong(),
                        SpeedMs = speed * (0.9f + (float)rng.NextDouble() * 0.2f),
                    });
                }
                if (result.Count >= budget) break;
            }
            return result;
        }

        private static float DistanceToSegment(WorldPosition p, WorldPosition a, WorldPosition b)
        {
            var abx = b.X - a.X;
            var abz = b.Z - a.Z;
            var len2 = abx * abx + abz * abz;
            var t = len2 < 1e-6f ? 0f : Math.Max(0f, Math.Min(1f, ((p.X - a.X) * abx + (p.Z - a.Z) * abz) / len2));
            var cx = a.X + abx * t - p.X;
            var cz = a.Z + abz * t - p.Z;
            return (float)Math.Sqrt(cx * cx + cz * cz);
        }
    }
}
