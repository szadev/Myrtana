#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;

namespace Myrtana.Session.Dev
{
    /// <summary>A world point of a <see cref="HeroRoute"/> (metres, Y up).</summary>
    public readonly struct HeroRoutePoint
    {
        public HeroRoutePoint(float x, float y, float z) { X = x; Y = y; Z = z; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public override string ToString() => FormattableString.Invariant($"({X:0.0},{Y:0.0},{Z:0.0})");
    }

    /// <summary>
    /// The dev-scenario hero's progress along a G3 navigation route (fix-34, Docs/dev-scenarios.md "escort"): the route's
    /// points are walked one after the other (each leg on the NavMesh by the runner), and the anti-loop rules of a step live
    /// here. A stuck spot is remembered as blocked for the whole step: a later route keeps clear of it (a route point at it is
    /// skipped) and the stuck teleport goes forward to the first route point beyond it, never
    /// back to the same approach. Teleports are counted; <see cref="MaxTeleports"/> within <see cref="TeleportWindowSeconds"/>
    /// end the step (the runner fails it). Pure C#.
    /// </summary>
    public sealed class HeroRoute
    {
        /// <summary>A route point counts as reached this close (flat metres).</summary>
        public const float Reach = 2f;
        /// <summary>A blocked spot blocks route points and legs within this distance (flat metres).</summary>
        public const float BlockRadius = 3f;
        /// <summary>A stuck teleport goes at least this far (flat metres) from the stuck spot.</summary>
        public const float MinAhead = 4f;
        public const int MaxTeleports = 3;
        public const double TeleportWindowSeconds = 60.0;

        readonly List<HeroRoutePoint> _points = new List<HeroRoutePoint>();
        readonly List<HeroRoutePoint> _blocked = new List<HeroRoutePoint>();
        readonly List<double> _teleports = new List<double>();

        public bool HasRoute => _points.Count >= 2;
        public IReadOnlyList<HeroRoutePoint> Points => _points;
        public IReadOnlyList<HeroRoutePoint> Blocked => _blocked;
        /// <summary>Index of the route point walked to now.</summary>
        public int Next { get; private set; }
        public int TeleportCount => _teleports.Count;

        /// <summary>The route is dropped (the blocked spots and teleports of the step stay).</summary>
        public void ClearRoute()
        {
            _points.Clear();
            Next = 0;
        }

        /// <summary>A new route (start and goal included) for a hero at <paramref name="hero"/>: walked from the point after
        /// the route segment nearest to him, past the points a blocked spot sits at (the approach that got him stuck).</summary>
        public void SetRoute(IReadOnlyList<HeroRoutePoint> points, HeroRoutePoint hero)
        {
            _points.Clear();
            if (points != null) _points.AddRange(points);
            Next = 0;
            if (!HasRoute) { _points.Clear(); return; }
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < _points.Count - 1; i++)
            {
                float d = SegmentDistance(hero, _points[i], _points[i + 1], out _);
                if (d < bestD) { bestD = d; best = i; }
            }
            Next = best + 1;
            SkipBlocked();
        }

        /// <summary>The goal moved (a guide walks on): the route's last point follows it.</summary>
        public void MoveGoal(HeroRoutePoint goal)
        {
            if (HasRoute) _points[_points.Count - 1] = goal;
        }

        /// <summary>The point to walk to now; moves on when the hero reached or passed the current one.</summary>
        public HeroRoutePoint Leg(HeroRoutePoint hero)
        {
            if (!HasRoute) return hero;
            int last = _points.Count - 1;
            while (Next < last)
            {
                var p = _points[Next];
                bool reached = Flat(p, hero) <= Reach;
                // passed: the hero is already along the next segment (beyond the point)
                SegmentDistance(hero, p, _points[Next + 1], out float t);
                bool passed = t > 0f && SegmentDistance(hero, p, _points[Next + 1], out _) < Flat(p, hero);
                if (reached || passed) Next++;
                else break;
            }
            return _points[Next];
        }

        /// <summary>The flat way left along the route from the hero (metres).</summary>
        public float WayLeft(HeroRoutePoint hero)
        {
            if (!HasRoute) return 0f;
            float s = Flat(hero, _points[Next]);
            for (int i = Next; i < _points.Count - 1; i++) s += Flat(_points[i], _points[i + 1]);
            return s;
        }

        /// <summary>The route's flat length (metres).</summary>
        public float Length
        {
            get
            {
                float s = 0f;
                for (int i = 0; i < _points.Count - 1; i++) s += Flat(_points[i], _points[i + 1]);
                return s;
            }
        }

        /// <summary>
        /// The hero is stuck at <paramref name="hero"/>: the spot is blocked for the rest of the step and the route goes on at
        /// the first point beyond it (<paramref name="index"/>, the teleport target): ahead of the current point, at least
        /// <see cref="MinAhead"/> m from the stuck spot and from every blocked spot. False when no such point is left (the
        /// caller teleports next to the guide / the goal instead).
        /// </summary>
        public bool StuckAt(HeroRoutePoint hero, out int index)
        {
            index = -1;
            if (!IsBlocked(hero, 0.5f)) _blocked.Add(hero);
            if (!HasRoute) return false;
            for (int i = Next; i < _points.Count; i++)
            {
                var p = _points[i];
                if (Flat(p, hero) < MinAhead || IsBlocked(p, MinAhead)) continue;
                index = i;
                Next = i;
                return true;
            }
            return false;
        }

        /// <summary>The escort's remaining way must shrink by this much (flat metres) between teleports, or they count as a loop.</summary>
        public const float LoopProgress = 5f;

        /// <summary>A teleport at <paramref name="now"/> (s); true when it makes <see cref="MaxTeleports"/> within
        /// <see cref="TeleportWindowSeconds"/> (the step should fail instead of looping).</summary>
        /// <param name="remaining">The escort's way left at the teleport (the guide's or the hero's distance to the target, flat
        /// metres); NaN = unknown. Teleports that brought the escort <see cref="LoopProgress"/> m on since the first one of the
        /// window are progress, not a loop: the window starts again (fix-46, r28 hammerclan_04: three stuck teleports up a
        /// Nordmar slope, each further on, Mort 184 -> 33 m from FP_Wolfclan, failed as "the hero loops").</param>
        public bool RecordTeleport(double now, float remaining = float.NaN)
        {
            for (int i = _teleports.Count - 1; i >= 0; i--)
                if (now - _teleports[i] > TeleportWindowSeconds) { _teleports.RemoveAt(i); _remaining.RemoveAt(i); }
            if (!float.IsNaN(remaining) && _remaining.Count > 0 && !float.IsNaN(_remaining[0]) && _remaining[0] - remaining >= LoopProgress)
            {
                _teleports.Clear();
                _remaining.Clear();
            }
            _teleports.Add(now);
            _remaining.Add(remaining);
            return _teleports.Count >= MaxTeleports;
        }

        readonly List<float> _remaining = new List<float>();

        /// <summary>Within <paramref name="radius"/> m (flat) of a blocked spot.</summary>
        public bool IsBlocked(HeroRoutePoint p, float radius = BlockRadius)
        {
            foreach (var b in _blocked)
                if (Flat(b, p) < radius) return true;
            return false;
        }

        void SkipBlocked()
        {
            int last = _points.Count - 1;
            while (Next < last)
            {
                var p = _points[Next];
                if (IsBlocked(p)) Next++;
                else break;
            }
        }

        static float Flat(HeroRoutePoint a, HeroRoutePoint b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Flat distance from <paramref name="p"/> to segment a-b; <paramref name="t"/> the projection (0..1 clamped).</summary>
        static float SegmentDistance(HeroRoutePoint p, HeroRoutePoint a, HeroRoutePoint b, out float t)
        {
            float vx = b.X - a.X, vz = b.Z - a.Z;
            float len2 = vx * vx + vz * vz;
            t = len2 < 1e-6f ? 0f : ((p.X - a.X) * vx + (p.Z - a.Z) * vz) / len2;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            float cx = a.X + vx * t - p.X, cz = a.Z + vz * t - p.Z;
            return (float)Math.Sqrt(cx * cx + cz * cz);
        }
    }
    /// <summary>
    /// Escort leg choices of the dev runner (fix-48, editor-runner-29 hammerclan_01/02: the hero circled 10.0 m from the
    /// waiting guide Mort below the ramp to FP_Feuerclan). Pure C#.
    /// </summary>
    public static class EscortLegRules
    {
        /// <summary>Farther than this (flat m) from where the escort leads, the hero takes the G3 route (fix-34).</summary>
        public const float RouteMinMetres = 10f;
        /// <summary>Off the route, the hero takes it again only beyond this (flat m): the hysteresis.</summary>
        public const float RouteResumeMetres = 15f;
        /// <summary>A stuck hero this close (flat m) to a waiting guide is put next to it rather than on a route point.</summary>
        public const float GuideNearMetres = 25f;

        /// <summary>
        /// The hero goes along the G3 route this frame. A single 10 m threshold flipped every frame where the NavMesh way to
        /// the guide first leads away (round an obstacle): route leg towards the obstacle, NavMesh leg away, round and round.
        /// </summary>
        public static bool UseRoute(bool wasOnRoute, float toDestination) =>
            wasOnRoute ? toDestination > RouteMinMetres : toDestination > RouteResumeMetres;

        /// <summary>
        /// A stuck hero (all detours tried) is put next to the guide when it stands waiting for him nearby (G3
        /// ZS_GuidePlayer_Wait: it goes on once he is within 8 m). The route point beyond the stuck spot lies past the guide
        /// on its trail (up the ramp), and its NavMesh snap landed below the ramp, on the stuck spot again.
        /// </summary>
        public static bool StuckTeleportToGuide(bool guideMode, bool guideWaits, float toGuide) =>
            guideMode && guideWaits && toGuide > 6f && toGuide <= GuideNearMetres;

        /// <summary>How far past the target (flat m) the leading hero steps so the follower comes up to it.</summary>
        public const float PastMetres = 2.5f;

        /// <summary>
        /// fix-186 (regress-1021b silden_03: 40 stuck events and 41 detours 5 m from Grompel in a Silden hut): the turns
        /// (degrees from "straight on, away from the follower") of the spots past the target the hero tries in turn, from
        /// <paramref name="attempt"/> on; each must lie on the NavMesh in straight reach of the target. Null: all tried, the
        /// hero stands at the target and lets the follower come up.
        /// </summary>
        public static float? PastTurn(int attempt) => attempt >= 0 && attempt < PastTurns.Length ? PastTurns[attempt] : (float?)null;

        static readonly float[] PastTurns = { 0f, 45f, -45f, 90f, -90f, 135f, -135f };
    }
}
#endif
