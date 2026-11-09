using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>A point of a guide route (Unity metres; no engine types).</summary>
    public readonly struct RoutePoint
    {
        public readonly float X, Y, Z;

        public RoutePoint(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float FlatDistance(RoutePoint o)
        {
            float dx = o.X - X, dz = o.Z - Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public override string ToString() => FormattableString.Invariant($"({X:0.0},{Y:0.0},{Z:0.0})");
    }

    /// <summary>
    /// Pure route planning for NPCs led to far targets (guides: Xardas to the Adanos gate, Geldern_Slave_01, Hauke, Mort;
    /// editor-runner-22). The runtime NavMesh covers only the cells around the hero and a terrace higher than the step
    /// height is an island left only by a one-way drop link, so a direct NavMesh query towards a target hundreds of metres
    /// away is partial or finds nothing. The planner works in segments over what is loaded: waypoint candidates in a
    /// corridor towards the target (<see cref="Corridor"/>; the caller puts them on the NavMesh, every floor level), and a
    /// lazy A* over them whose edges the caller answers (a complete NavMesh path between two waypoints, possibly over a drop
    /// link made for it, with its length; null = no way). The result is the route to the target, or (partial) to the
    /// reached waypoint closest to it: the guide walks there, the hero follows, the NavMesh grows with him, and the next
    /// plan goes on. Tested in Tools/G3Tests.
    /// </summary>
    public static class NavRoutePlanner
    {
        /// <summary>Waypoint spacing along and across the way (m).</summary>
        public const float Spacing = 20f;
        /// <summary>Half width of the corridor (m): detours around a lake or down a terrace at the side.</summary>
        public const float HalfWidth = 40f;
        /// <summary>Farthest waypoint ahead (m): about the NavMesh build radius around the hero.</summary>
        public const float MaxAhead = 180f;
        /// <summary>Waypoints closer than this (flat m) are neighbours, i.e. edges the A* asks for.</summary>
        public const float NeighbourRadius = 46f;
        /// <summary>Edge queries one plan may ask (NavMesh path queries are the cost).</summary>
        public const int MaxQueries = 90;

        public sealed class Result
        {
            /// <summary>Waypoint indices from the start (included) to the end of the route.</summary>
            public readonly List<int> Nodes = new List<int>();
            /// <summary>Summed edge costs (m of path).</summary>
            public float Cost;
            /// <summary>Flat distance from the route's end to the goal (m).</summary>
            public float Left;
            /// <summary>The route ends within the arrive radius of the goal.</summary>
            public bool ReachesGoal;
            /// <summary>Edge queries asked.</summary>
            public int Queries;
            /// <summary>The route ends elsewhere than at the start (it gets the walker on).</summary>
            public bool Moves => Nodes.Count > 1;
        }

        /// <summary>
        /// Waypoint columns (x, z) between the start and the goal: every <paramref name="spacing"/> m along the straight way
        /// up to <paramref name="maxAhead"/> m (the goal itself when closer), each at lateral offsets 0, ±spacing … ±halfWidth.
        /// Nearest rows first.
        /// </summary>
        public static List<(float x, float z)> Corridor(float fromX, float fromZ, float goalX, float goalZ,
                                                       float spacing = Spacing, float halfWidth = HalfWidth, float maxAhead = MaxAhead)
        {
            var list = new List<(float, float)>();
            float dx = goalX - fromX, dz = goalZ - fromZ;
            float dist = (float)Math.Sqrt(dx * dx + dz * dz);
            if (dist < 1e-3f || spacing <= 0) return list;
            float ux = dx / dist, uz = dz / dist; // along
            float sx = -uz, sz = ux;              // across
            int lanes = (int)Math.Floor(halfWidth / spacing + 1e-4f);
            for (float a = spacing; ; a += spacing)
            {
                bool last = a >= dist || a >= maxAhead;
                float along = Math.Min(a, Math.Min(dist, maxAhead));
                float cx = fromX + ux * along, cz = fromZ + uz * along;
                list.Add((cx, cz));
                for (int k = 1; k <= lanes; k++)
                {
                    list.Add((cx + sx * spacing * k, cz + sz * spacing * k));
                    list.Add((cx - sx * spacing * k, cz - sz * spacing * k));
                }
                if (last) break;
            }
            return list;
        }

        /// <summary>
        /// Lazy A* from <paramref name="start"/> towards <paramref name="goal"/> over <paramref name="nodes"/>: neighbours are
        /// the nodes within <paramref name="neighbourRadius"/> (flat), an edge is asked from <paramref name="edge"/> only when
        /// its tail is expanded (cost in m, null = no way; one-way: a drop link goes down only), the heuristic is the flat
        /// distance to the goal (admissible: a path is never shorter). Ends at the first expanded node within
        /// <paramref name="arriveRadius"/> of the goal; otherwise (no way, or the query budget spent) at the expanded node
        /// closest to the goal (ties: the cheaper route).
        /// </summary>
        public static Result Plan(IReadOnlyList<RoutePoint> nodes, int start, RoutePoint goal, Func<int, int, float?> edge,
                                  float neighbourRadius = NeighbourRadius, float arriveRadius = 4f, int maxQueries = MaxQueries)
        {
            var result = new Result();
            int n = nodes.Count;
            if (start < 0 || start >= n) return result;
            var g = new float[n];
            var parent = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.PositiveInfinity; parent[i] = -1; }
            g[start] = 0;
            var open = new List<int> { start };
            int best = start;
            float bestLeft = nodes[start].FlatDistance(goal);
            float r2 = neighbourRadius * neighbourRadius;
            while (open.Count > 0)
            {
                // the open node with the lowest f = g + h (lists here are a few dozen nodes)
                int bi = 0;
                float bf = float.PositiveInfinity;
                for (int k = 0; k < open.Count; k++)
                {
                    int o = open[k];
                    float f = g[o] + nodes[o].FlatDistance(goal);
                    if (f < bf) { bf = f; bi = k; }
                }
                int cur = open[bi];
                open.RemoveAt(bi);
                if (closed[cur]) continue;
                closed[cur] = true;
                float left = nodes[cur].FlatDistance(goal);
                if (left < bestLeft - 1e-3f || (Math.Abs(left - bestLeft) <= 1e-3f && g[cur] < g[best])) { best = cur; bestLeft = left; }
                if (left <= arriveRadius) { best = cur; bestLeft = left; break; }
                if (result.Queries >= maxQueries) continue; // budget spent: finish the open list without new edges
                // neighbours, nearest to the goal first (the budget goes where it helps)
                var next = new List<int>();
                for (int j = 0; j < n; j++)
                {
                    if (j == cur || closed[j]) continue;
                    float dx = nodes[j].X - nodes[cur].X, dz = nodes[j].Z - nodes[cur].Z;
                    if (dx * dx + dz * dz <= r2) next.Add(j);
                }
                next.Sort((a, b) => nodes[a].FlatDistance(goal).CompareTo(nodes[b].FlatDistance(goal)));
                foreach (int j in next)
                {
                    if (result.Queries >= maxQueries) break;
                    result.Queries++;
                    float? c = edge(cur, j);
                    if (c == null || float.IsNaN(c.Value) || c.Value < 0) continue;
                    float ng = g[cur] + c.Value;
                    if (ng >= g[j]) continue;
                    g[j] = ng;
                    parent[j] = cur;
                    open.Add(j);
                }
            }
            for (int v = best; v >= 0; v = parent[v]) result.Nodes.Add(v);
            result.Nodes.Reverse();
            result.Cost = g[best];
            result.Left = bestLeft;
            result.ReachesGoal = bestLeft <= arriveRadius;
            return result;
        }

        /// <summary>A new plan replaces the current leg only when its way is shorter by this share of the kept way …</summary>
        public const float SwitchRatio = 0.15f;
        /// <summary>… plus these metres (hysteresis: replans every few seconds must not flip the leg between floor levels).</summary>
        public const float SwitchMetres = 15f;

        /// <summary>
        /// Route stickiness (editor-runner-24: Hauke's and Mort's legs flipped between floor levels on every 6 s replan, the
        /// guide zig-zagged): the guide keeps walking to its current leg while that leg is still reachable
        /// (<paramref name="keptWay"/>: the NavMesh path to the leg plus the way the plan had left beyond it; NaN or infinity
        /// = no longer reachable) unless the new plan's way (<paramref name="newWay"/>; infinity = no way on) is shorter
        /// by more than <see cref="SwitchRatio"/> of it plus <see cref="SwitchMetres"/>.
        /// </summary>
        public static bool KeepLeg(float keptWay, float newWay, float ratio = SwitchRatio, float metres = SwitchMetres)
        {
            if (float.IsNaN(keptWay) || float.IsInfinity(keptWay) || keptWay < 0) return false;
            if (float.IsNaN(newWay)) return true;
            return newWay >= keptWay - (keptWay * ratio + metres);
        }

        /// <summary>
        /// Directions (degrees from the way to the goal) in which a guide stuck on a NavMesh island (no waypoint edge leads
        /// on: a plateau whose neighbour waypoints are all at its level or higher, Xardas on his tower plateau) looks for the
        /// island's edge with lower ground beyond: all around, those towards the goal first.
        /// </summary>
        public static float[] IslandExitDegrees(int count = 24)
        {
            if (count < 1) count = 1;
            var list = new List<float>(count) { 0f };
            float step = 360f / count;
            for (int k = 1; list.Count < count; k++)
            {
                list.Add(k * step);
                if (list.Count < count && k * step < 180f - 1e-3f) list.Add(-k * step);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Which island exit to take (<see cref="IslandExitDegrees"/>): a landing whose NavMesh path to the goal is complete
        /// wins, then the one whose way ends closest to the goal (<paramref name="left"/>, flat m), then the nearer edge.
        /// </summary>
        public static bool BetterExit(bool complete, float left, float edgeDistance, bool bestComplete, float bestLeft, float bestEdgeDistance)
        {
            if (complete != bestComplete) return complete;
            if (Math.Abs(left - bestLeft) > 1f) return left < bestLeft;
            return edgeDistance < bestEdgeDistance;
        }

        /// <summary>
        /// Stuck periods (4 s each, without progress and without a NavMesh build around) before a guide is put at its
        /// destination: two for a short way, one more per 50 m of the remaining way, at most 12 (48 s).
        /// </summary>
        public static int StuckPeriods(float wayMetres)
        {
            if (float.IsNaN(wayMetres) || wayMetres < 0) wayMetres = 0;
            return Math.Min(12, 2 + (int)(wayMetres / 50f));
        }
    }
}
