using System;

namespace Myrtana.Simulation
{
    /// <summary>
    /// One NPC's walk along a <see cref="NavGraph"/> route (G3 RunNPCNavigation / DetectNextNavPoint: the route's points
    /// are the goals one after the other). It plans the route, hands out the point to walk to now (the leg), moves on when
    /// the body reaches it (or has passed it on the ribbon), and plans anew when the goal moved, the body left the route
    /// or no route was found a while ago. The presentation walks each leg on its NavMesh; when that makes no progress
    /// (<see cref="Stuck"/>) the follower switches to the ribbon itself (<see cref="Ribbon"/>: straight along the
    /// designer's path, which G3 NPCs walk), then skips the leg, and finally gives up. Pure C#.
    /// </summary>
    public sealed class NavRouteFollower
    {
        /// <summary>The goal moved this far (flat metres): plan anew.</summary>
        public const float GoalMoved = 8f;
        /// <summary>The body is this far (flat metres) from the leg it walks: plan anew.</summary>
        public const float OffRoute = 25f;
        public const double MinReplanSeconds = 1.0, FailedReplanSeconds = 5.0;
        /// <summary>After giving a route up the follower stays off the graph this long (s) unless the goal moves.</summary>
        public const double GiveUpSeconds = 20.0;
        /// <summary>Stuck periods on one leg before the follower skips it / gives up.</summary>
        public const int SkipAfterStuck = 2, GiveUpAfterStuck = 4;
        /// <summary>A route point counts as reached within its half-width, clamped to these (flat metres).</summary>
        public const float MinReach = 1.5f, MaxReach = 4f;

        NavRoute _route;
        int _next, _stuck;
        float _gx, _gy, _gz;
        double _lastPlan = double.NegativeInfinity, _retryAt = double.NegativeInfinity;

        public NavRoute Route => _route;
        public bool HasRoute => _route != null && _route.Found;
        /// <summary>Index of the route point walked to now.</summary>
        public int Next => _next;
        /// <summary>Walk the current leg straight along the ribbon / across the zone instead of on the NavMesh.</summary>
        public bool Ribbon { get; private set; }
        /// <summary>The leg is the route's last point (the goal itself).</summary>
        public bool AtLastPoint => HasRoute && _next >= _route.Points.Count - 1;
        /// <summary>How the last plan went (for the logs).</summary>
        public string Note { get; private set; } = "none";

        public void Reset()
        {
            _route = null;
            _next = _stuck = 0;
            _lastPlan = _retryAt = double.NegativeInfinity;
            Ribbon = false;
            Note = "none";
        }

        /// <summary>
        /// The point to walk to now on the way from (x, y, z) to the goal; false without a route (no graph, start or goal in
        /// no zone / path, no connection): the caller then walks its own way. <paramref name="planned"/>: this call planned.
        /// </summary>
        public bool Update(NavGraph graph, double now, float x, float y, float z, float gx, float gy, float gz, out NavRoutePoint leg,
                           out bool planned)
        {
            leg = default;
            planned = false;
            if (graph == null) return false;
            bool goalMoved = Flat(gx - _gx, gz - _gz) > GoalMoved;
            bool due = goalMoved || (HasRoute ? OffLeg(x, z) > OffRoute : now >= _retryAt);
            if (due && now - _lastPlan >= MinReplanSeconds)
            {
                _route = graph.FindRoute(x, y, z, gx, gy, gz);
                _gx = gx; _gy = gy; _gz = gz;
                _lastPlan = now;
                if (!_route.Found) _retryAt = now + FailedReplanSeconds;
                _next = 1;
                _stuck = 0;
                Ribbon = false;
                planned = true;
                Note = _route.Found
                    ? FormattableString.Invariant($"G3 route {_route.Length:0} m, {_route.Points.Count} points ({_route.Note}); from {graph.Describe(_route.Start)} to {graph.Describe(_route.Goal)}")
                    : "no G3 route: " + _route.Note;
            }
            if (!HasRoute) return false;
            var pts = _route.Points;
            while (_next < pts.Count - 1)
            {
                var p = pts[_next];
                float reach = Math.Max(MinReach, Math.Min(p.Width, MaxReach));
                if (Flat(p.X - x, p.Z - z) <= reach || Passed(p, pts[_next + 1], x, z)) Advance();
                else break;
            }
            leg = pts[_next];
            return true;
        }

        /// <summary>The body reached the leg (its walker arrived): on to the next point.</summary>
        public void LegReached()
        {
            if (HasRoute && _next < _route.Points.Count - 1) Advance();
        }

        /// <summary>
        /// The walk to the leg made no progress for a period: walk the ribbon instead, then skip the leg; true when the
        /// follower gives up (the caller's own fallback decides then; the graph is not used for <see cref="GiveUpSeconds"/>).
        /// </summary>
        public bool Stuck(double now)
        {
            _stuck++;
            if (_stuck >= GiveUpAfterStuck)
            {
                Note = $"gave the G3 route up at point {_next} of {_route?.Points.Count ?? 0}";
                _route = null;
                _stuck = 0;
                Ribbon = false;
                _retryAt = now + GiveUpSeconds;
                return true;
            }
            if (_stuck >= SkipAfterStuck && HasRoute && _next < _route.Points.Count - 1) _next++;
            Ribbon = true;
            return false;
        }

        /// <summary>The way left along the route from (x, z) (flat metres); 0 without a route.</summary>
        public float WayLeft(float x, float z)
        {
            if (!HasRoute) return 0;
            var pts = _route.Points;
            float left = Flat(pts[_next].X - x, pts[_next].Z - z);
            for (int i = _next + 1; i < pts.Count; i++) left += Flat(pts[i].X - pts[i - 1].X, pts[i].Z - pts[i - 1].Z);
            return left;
        }

        void Advance()
        {
            _next++;
            _stuck = 0;
            Ribbon = false;
        }

        /// <summary>On the next segment, beyond the point and within its ribbon (a body that cut the corner).</summary>
        static bool Passed(NavRoutePoint p, NavRoutePoint q, float x, float z)
        {
            float dx = q.X - p.X, dz = q.Z - p.Z, len2 = dx * dx + dz * dz;
            if (len2 < 1e-6f) return true;
            float t = ((x - p.X) * dx + (z - p.Z) * dz) / len2;
            if (t <= 0f || t >= 1f) return false;
            float px = p.X + t * dx - x, pz = p.Z + t * dz - z;
            return Flat(px, pz) <= Math.Max(p.Width, 2f) + 1f;
        }

        /// <summary>Flat distance from (x, z) to the segment the body walks now (previous point → leg).</summary>
        float OffLeg(float x, float z)
        {
            var pts = _route.Points;
            var a = pts[Math.Max(0, _next - 1)];
            var b = pts[_next];
            float dx = b.X - a.X, dz = b.Z - a.Z, len2 = dx * dx + dz * dz;
            float t = len2 > 1e-6f ? Math.Max(0f, Math.Min(1f, ((x - a.X) * dx + (z - a.Z) * dz) / len2)) : 0f;
            return Flat(a.X + t * dx - x, a.Z + t * dz - z);
        }

        static float Flat(float dx, float dz) => (float)Math.Sqrt(dx * dx + dz * dz);
    }
}
