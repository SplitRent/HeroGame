using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.World;

namespace HeroGame.Core.Traffic
{
    public sealed class RoadNode
    {
        public int Id;
        public WorldPosition Position;
        public readonly List<int> Edges = new List<int>();
        /// <summary>More than two edges meet here (signals, stop lines).</summary>
        public bool IsIntersection => Edges.Count > 2;
    }

    public sealed class RoadEdge
    {
        public int Id;
        public int From;
        public int To;
        public float Length;
        public string RoadName = "";
        public string Kind = "street";
        public int LanesPerDirection = 1;
        public float SpeedLimitMs = 13.4f;
        /// <summary>Runtime capacity factor (a dead traffic signal, a closure). 1 = normal. Not persisted: recomputed.</summary>
        public float CapacityFactor = 1f;

        /// <summary>Vehicles per hour per lane at free flow before congestion rises sharply.</summary>
        public float CapacityPerLane => Kind == "avenue" || Kind == "bridge" ? 1800f : Kind == "highway" ? 2000f : 900f;
        public float FreeFlowSeconds => Length / SpeedLimitMs;
        public int Other(int node) => node == From ? To : From;
    }

    /// <summary>
    /// Road graph built from the layout's road polylines (TDD §10.3). Polylines are split wherever they
    /// cross, so intersections are discovered automatically. Supports A* routing for GPS, NPC drivers,
    /// taxis and emergency units, and nearest-point queries for spawning traffic on lanes.
    /// </summary>
    public sealed class RoadNetwork
    {
        public readonly List<RoadNode> Nodes = new List<RoadNode>();
        public readonly List<RoadEdge> Edges = new List<RoadEdge>();

        private const float MergeDistance = 1.0f;

        public static RoadNetwork Build(IEnumerable<RoadLayout> roads)
        {
            var net = new RoadNetwork();
            var segments = new List<(WorldPosition a, WorldPosition b, RoadLayout road)>();
            foreach (var r in roads)
                for (var i = 0; i + 3 < r.Points.Count; i += 2)
                    segments.Add((new WorldPosition(r.Points[i], 0, r.Points[i + 1]), new WorldPosition(r.Points[i + 2], 0, r.Points[i + 3]), r));

            // Split points along each segment: its endpoints plus every crossing with another segment.
            for (var i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                var cuts = new List<float> { 0f, 1f };
                for (var j = 0; j < segments.Count; j++)
                {
                    if (i == j) continue;
                    if (Intersect(s.a, s.b, segments[j].a, segments[j].b, out var t)) cuts.Add(t);
                    // T-junctions: another segment's endpoint touching this one.
                    foreach (var end in new[] { segments[j].a, segments[j].b })
                        if (PointOnSegment(end, s.a, s.b, out var te)) cuts.Add(te);
                }
                cuts.Sort();
                for (var k = 0; k + 1 < cuts.Count; k++)
                {
                    if (cuts[k + 1] - cuts[k] < 1e-4f) continue;
                    var p0 = Lerp(s.a, s.b, cuts[k]);
                    var p1 = Lerp(s.a, s.b, cuts[k + 1]);
                    net.AddEdge(net.NodeAt(p0), net.NodeAt(p1), s.road);
                }
            }
            return net;
        }

        private int NodeAt(WorldPosition p)
        {
            foreach (var n in Nodes)
                if (WorldPosition.DistanceXZ(n.Position, p) <= MergeDistance) return n.Id;
            var node = new RoadNode { Id = Nodes.Count, Position = p };
            Nodes.Add(node);
            return node.Id;
        }

        private void AddEdge(int a, int b, RoadLayout road)
        {
            if (a == b) return;
            foreach (var existing in Nodes[a].Edges)
            {
                var e = Edges[existing];
                if (e.Other(a) == b) return;
            }
            var edge = new RoadEdge
            {
                Id = Edges.Count,
                From = a,
                To = b,
                Length = WorldPosition.DistanceXZ(Nodes[a].Position, Nodes[b].Position),
                RoadName = road.Name,
                Kind = road.Kind,
                LanesPerDirection = road.Width >= 14f ? 2 : 1,
                SpeedLimitMs = road.Kind == "avenue" || road.Kind == "bridge" ? 17.9f : road.Kind == "highway" ? 29f : 13.4f,
            };
            Edges.Add(edge);
            Nodes[a].Edges.Add(edge.Id);
            Nodes[b].Edges.Add(edge.Id);
        }

        public int NearestNode(WorldPosition p)
        {
            var best = -1;
            var bestD = float.MaxValue;
            foreach (var n in Nodes)
            {
                var d = WorldPosition.DistanceSquaredXZ(n.Position, p);
                if (d < bestD)
                {
                    bestD = d;
                    best = n.Id;
                }
            }
            return best;
        }

        /// <summary>A* over travel time. <paramref name="edgeCost"/> lets traffic/weather/closures change costs.</summary>
        public List<int> Route(int start, int goal, Func<RoadEdge, float> edgeCost = null)
        {
            var cost = edgeCost ?? (e => e.FreeFlowSeconds);
            var g = new Dictionary<int, float> { { start, 0f } };
            var came = new Dictionary<int, int>();
            var open = new SortedSet<(float f, int node)>();
            open.Add((Heuristic(start, goal), start));
            var closed = new HashSet<int>();
            while (open.Count > 0)
            {
                var current = open.Min;
                open.Remove(current);
                var node = current.node;
                if (node == goal) return Reconstruct(came, goal);
                if (!closed.Add(node)) continue;
                foreach (var edgeId in Nodes[node].Edges)
                {
                    var edge = Edges[edgeId];
                    var c = cost(edge);
                    if (float.IsInfinity(c)) continue; // closed road
                    var next = edge.Other(node);
                    var tentative = g[node] + c;
                    if (g.TryGetValue(next, out var old) && tentative >= old) continue;
                    g[next] = tentative;
                    came[next] = node;
                    open.Add((tentative + Heuristic(next, goal), next));
                }
            }
            return null;
        }

        public float RouteLength(List<int> nodes)
        {
            var total = 0f;
            for (var i = 0; i + 1 < nodes.Count; i++) total += WorldPosition.DistanceXZ(Nodes[nodes[i]].Position, Nodes[nodes[i + 1]].Position);
            return total;
        }

        public RoadEdge EdgeBetween(int a, int b)
        {
            foreach (var id in Nodes[a].Edges)
                if (Edges[id].Other(a) == b) return Edges[id];
            return null;
        }

        private float Heuristic(int a, int b) => WorldPosition.DistanceXZ(Nodes[a].Position, Nodes[b].Position) / 29f; // admissible: fastest limit

        private static List<int> Reconstruct(Dictionary<int, int> came, int goal)
        {
            var path = new List<int> { goal };
            while (came.TryGetValue(path[path.Count - 1], out var prev)) path.Add(prev);
            path.Reverse();
            return path;
        }

        private static WorldPosition Lerp(WorldPosition a, WorldPosition b, float t) => new WorldPosition(a.X + (b.X - a.X) * t, 0, a.Z + (b.Z - a.Z) * t);

        private static bool Intersect(WorldPosition p1, WorldPosition p2, WorldPosition p3, WorldPosition p4, out float t)
        {
            t = 0;
            var d = (p2.X - p1.X) * (p4.Z - p3.Z) - (p2.Z - p1.Z) * (p4.X - p3.X);
            if (Math.Abs(d) < 1e-6f) return false;
            t = ((p3.X - p1.X) * (p4.Z - p3.Z) - (p3.Z - p1.Z) * (p4.X - p3.X)) / d;
            var u = ((p3.X - p1.X) * (p2.Z - p1.Z) - (p3.Z - p1.Z) * (p2.X - p1.X)) / d;
            return t > 1e-4f && t < 1 - 1e-4f && u >= -1e-4f && u <= 1 + 1e-4f;
        }

        private static bool PointOnSegment(WorldPosition p, WorldPosition a, WorldPosition b, out float t)
        {
            t = 0;
            var abx = b.X - a.X;
            var abz = b.Z - a.Z;
            var len2 = abx * abx + abz * abz;
            if (len2 < 1e-6f) return false;
            t = ((p.X - a.X) * abx + (p.Z - a.Z) * abz) / len2;
            if (t <= 1e-4f || t >= 1 - 1e-4f) return false;
            var cx = a.X + abx * t - p.X;
            var cz = a.Z + abz * t - p.Z;
            return cx * cx + cz * cz <= MergeDistance * MergeDistance;
        }
    }
}
