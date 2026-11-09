using System.Collections.Generic;
using G3.Player;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// The way of a guide to a far destination, planned in segments over the loaded NavMesh (fix-22, editor-runner-22:
    /// Xardas to the Adanos gate, Geldern_Slave_01, Hauke and Mort gave up after 15–30 s and were put at the destination).
    /// When there is no complete NavMesh path to the destination (it lies beyond the NavMesh around the hero, or the guide
    /// stands on a terrace island), waypoints in a corridor towards it are put on the NavMesh on every floor level under
    /// them and <see cref="NavRoutePlanner.Plan"/> searches a route over them: an edge is a complete NavMesh path between
    /// two waypoints, or one over a one-way drop link made for it (<see cref="NavDropLinks"/>, searched towards that
    /// waypoint as the <c>navislands</c> check does, not only towards the far destination). The guide walks to a leg point
    /// up to <see cref="LegMetres"/> m along that route; reaching it (or every <see cref="ReplanSeconds"/>, or when the
    /// NavMesh grew) plans the next segment. The hero follows, the NavMesh grows around him, the route goes on.
    /// </summary>
    public sealed class GuideRoute
    {
        const float ReplanSeconds = 6f, MinReplanSeconds = 0.75f, LegMetres = 80f, LegReached = 3f, GoalMoved = 8f;
        const float NodeSample = 1.5f, StartSample = 2f, GoalSample = 4f;
        const int MaxNodes = 140, MaxLinkSearches = 6;

        readonly List<RoutePoint> _nodes = new List<RoutePoint>();
        readonly List<Vector3> _points = new List<Vector3>();
        readonly Dictionary<long, float?> _edges = new Dictionary<long, float?>();
        static readonly RaycastHit[] _hits = new RaycastHit[12];
        NavMeshPath _path;
        Vector3 _goal, _leg;
        float _legRest; // the way the plan had left beyond its leg (m)
        float _nextPlan = float.NegativeInfinity, _lastPlan = float.NegativeInfinity;
        int _navUpdates = -1, _linkSearches;
        bool _hasLeg, _legDone;

        /// <summary>How the last plan went (for the logs).</summary>
        public string Note { get; private set; } = "none";
        /// <summary>The way left by the last plan (route + rest to the destination, m); 0 before any plan.</summary>
        public float WayMetres { get; private set; }

        public void Reset()
        {
            _hasLeg = _legDone = false;
            _nextPlan = _lastPlan = float.NegativeInfinity;
            WayMetres = 0;
            Note = "none";
        }

        /// <summary>The leg point was reached (or the walk to it got stuck): plan the next segment anew, soon.</summary>
        public void LegDone()
        {
            _legDone = true;
            _nextPlan = Mathf.Min(_nextPlan, _lastPlan + MinReplanSeconds);
        }

        /// <summary>
        /// The point the guide walks to now on its way to <paramref name="goal"/>; false when it goes straight for the goal
        /// (a complete NavMesh path exists, or no route gets it on: then the walker's own plan and the stuck rule decide).
        /// <paramref name="planned"/> is true when this call planned anew.
        /// </summary>
        public bool TryLeg(Vector3 self, Vector3 goal, out Vector3 leg, out bool planned)
        {
            float now = Time.time;
            planned = false;
            bool due = now >= _nextPlan || Flat(goal - _goal) > GoalMoved || (_hasLeg && Reached(self)) ||
                       NavStats.Updates != _navUpdates;
            if (due && now - _lastPlan >= MinReplanSeconds)
            {
                using (NavStats.RouteMarker.Auto())
                {
                    long t0 = NavStats.Now;
                    NavStats.Routes++;
                    bool fresh = _legDone || !_hasLeg || Flat(goal - _goal) > GoalMoved || Reached(self);
                    Replan(self, goal, fresh);
                    NavStats.RouteTicks += NavStats.Now - t0;
                }
                _legDone = false;
                _goal = goal;
                _lastPlan = now;
                _nextPlan = now + ReplanSeconds;
                _navUpdates = NavStats.Updates;
                planned = true;
            }
            leg = _leg;
            return _hasLeg;
        }

        /// <param name="fresh">
        /// The leg was reached, the goal moved, or there is none: plan anew. Otherwise (a periodic replan, the NavMesh grew)
        /// the current leg is kept while it is reachable and the new plan is not clearly shorter
        /// (<see cref="NavRoutePlanner.KeepLeg"/>).
        /// </param>
        void Replan(Vector3 self, Vector3 goal, bool fresh)
        {
            _path ??= new NavMeshPath();
            bool hadLeg = _hasLeg && !fresh;
            var oldLeg = _leg;
            float oldRest = _legRest;
            Plan(self, goal);
            if (!hadLeg || Note == "direct path" || !NavMesh.SamplePosition(self, out var s, StartSample, NavMesh.AllAreas)) return;
            float? toOld = Query(s.position, oldLeg);
            float kept = toOld.HasValue ? toOld.Value + oldRest : float.PositiveInfinity;
            float fresher = _hasLeg ? WayMetres : float.PositiveInfinity;
            if (!NavRoutePlanner.KeepLeg(kept, fresher)) return;
            if (_hasLeg && (_leg - oldLeg).sqrMagnitude < 0.01f) return; // the same leg anyway
            Note = FormattableStringInv($"keeps its leg ({kept:0} m way; new plan {(_hasLeg ? $"{fresher:0} m" : "none")}: ") + Note + ")";
            _leg = oldLeg;
            _legRest = oldRest;
            _hasLeg = true;
            WayMetres = kept;
        }

        void Plan(Vector3 self, Vector3 goal)
        {
            _hasLeg = false;
            _edges.Clear();
            _linkSearches = 0;
            if (!NavMesh.SamplePosition(self, out var s, StartSample, NavMesh.AllAreas))
            {
                Note = "off the NavMesh";
                WayMetres = Flat(goal - self);
                return;
            }
            bool goalOnMesh = NavMesh.SamplePosition(goal, out var g, GoalSample, NavMesh.AllAreas);
            if (goalOnMesh && Query(s.position, g.position) is float direct)
            {
                Note = "direct path";
                WayMetres = direct;
                return;
            }

            _nodes.Clear();
            _points.Clear();
            Add(s.position);
            if (goalOnMesh) Add(g.position);
            float top = Mathf.Max(self.y, goal.y) + 60f, bottom = Mathf.Min(self.y, goal.y) - 80f;
            foreach (var (x, z) in NavRoutePlanner.Corridor(self.x, self.z, goal.x, goal.z))
            {
                if (_nodes.Count >= MaxNodes) break;
                int n = Physics.RaycastNonAlloc(new Vector3(x, top, z), Vector3.down, _hits, top - bottom, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n && _nodes.Count < MaxNodes; i++)
                {
                    if (_hits[i].collider is CharacterController) continue;
                    if (!NavMesh.SamplePosition(_hits[i].point, out var h, NodeSample, NavMesh.AllAreas)) continue;
                    bool dup = false;
                    foreach (var p in _points)
                        if ((p - h.position).sqrMagnitude < 4f) { dup = true; break; }
                    if (!dup) Add(h.position);
                }
            }
            var goalPoint = new RoutePoint(goal.x, goal.y, goal.z);
            var r = NavRoutePlanner.Plan(_nodes, 0, goalPoint, Edge);
            WayMetres = (r.Moves ? r.Cost : 0f) + r.Left;
            if (!r.Moves)
            {
                // no waypoint edge leads on (an island whose neighbour waypoints are not lower): the island's edge, any
                // direction, with lower ground within the drop limit beyond it
                _linkSearches++;
                if (NavDropLinks.TryLinkOffIsland(s.position, goal, out var landing) && Query(s.position, landing) is float off)
                {
                    _leg = landing;
                    _hasLeg = Flat(_leg - s.position) > LegReached || Mathf.Abs(_leg.y - s.position.y) > SlopeRules.StepHeight;
                    _legRest = Flat(goal - landing);
                    WayMetres = off + _legRest;
                    Note = FormattableStringInv($"off the island: drop link, leg {Flat(_leg - s.position):0} m ({_leg.y - s.position.y:0.0} m), {_nodes.Count} waypoints, {r.Queries} queries, {_linkSearches} link searches");
                    return;
                }
                Note = $"no way on from here ({_nodes.Count} waypoints, {r.Queries} queries, {_linkSearches} link searches)";
                return;
            }
            // the leg: the first route point LegMetres along it (or the route's end)
            float along = 0;
            int legAt = r.Nodes[r.Nodes.Count - 1];
            for (int k = 1; k < r.Nodes.Count; k++)
            {
                along += Edge(r.Nodes[k - 1], r.Nodes[k]) ?? 0f;
                if (along >= LegMetres) { legAt = r.Nodes[k]; break; }
            }
            _leg = _points[legAt];
            _legRest = Mathf.Max(0f, WayMetres - along);
            _hasLeg = Flat(_leg - s.position) > LegReached;
            Note = FormattableStringInv($"route {r.Nodes.Count - 1} waypoints {r.Cost:0} m, ") +
                   (r.ReachesGoal ? "to the destination" : FormattableStringInv($"{r.Left:0} m short (the NavMesh ends there)")) +
                   FormattableStringInv($", leg {Flat(_leg - s.position):0} m, {_nodes.Count} waypoints, {r.Queries} queries, {_linkSearches} link searches");
        }

        void Add(Vector3 p)
        {
            _points.Add(p);
            _nodes.Add(new RoutePoint(p.x, p.y, p.z));
        }

        /// <summary>A complete NavMesh path a → b (its length), possibly over a drop link made for it now; null = no way.</summary>
        float? Edge(int a, int b)
        {
            long key = ((long)a << 32) | (uint)b;
            if (_edges.TryGetValue(key, out var known)) return known;
            var pa = _points[a];
            var pb = _points[b];
            float? cost = Query(pa, pb);
            // partial: a terrace edge between them (one-way, down only): a drop link towards b, then the path again
            if (cost == null && _linkSearches < MaxLinkSearches && pb.y < pa.y - SlopeRules.StepHeight)
            {
                _linkSearches++;
                if (NavDropLinks.TryLinkTowards(pa, pb)) cost = Query(pa, pb);
            }
            _edges[key] = cost;
            return cost;
        }

        float? Query(Vector3 a, Vector3 b)
        {
            NavStats.PathQueries++;
            if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete) return null;
            var c = _path.corners;
            float len = 0;
            for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
            return len;
        }

        /// <summary>At the leg point: within <see cref="LegReached"/> flat and on its floor (a drop leg lies below).</summary>
        bool Reached(Vector3 self) => Flat(_leg - self) <= LegReached && Mathf.Abs(_leg.y - self.y) < 2f;

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        static string FormattableStringInv(System.FormattableString s) => System.FormattableString.Invariant(s);
    }
}
