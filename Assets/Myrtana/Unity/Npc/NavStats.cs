using System.Diagnostics;
using System.Globalization;
using Unity.Profiling;

namespace Myrtana.Unity
{
    /// <summary>
    /// Dev counters of the NPC navigation cost (fix-22, editor-runner-22: Faring frame avg +3..+8 ms after the finer
    /// NavMesh and the drop links): walker plans, drop-link searches, guide route plans (count and main-thread ms), the
    /// walkers' ground rays, and the background NavMesh updates. <see cref="G3.Unity.G3PerfProbe"/> appends
    /// <see cref="Line"/> to each sample line ("nav ..."); the profiler markers (NpcView.*) show up in the probe's
    /// profile dump.
    /// </summary>
    public static class NavStats
    {
        public static readonly ProfilerMarker PlanMarker = new ProfilerMarker("NpcView.NavPlan");
        public static readonly ProfilerMarker LinkMarker = new ProfilerMarker("NpcView.NavDropLinks");
        public static readonly ProfilerMarker RouteMarker = new ProfilerMarker("NpcView.GuideRoute");

        public static int Plans, LinkSearches, Routes, PathQueries, GroundRays, Updates;
        public static long PlanTicks, LinkTicks, RouteTicks;
        /// <summary>Wall time of the last finished NavMesh update (s) and whether one runs now.</summary>
        public static float LastUpdateSeconds;
        public static bool Building;
        public static int BuildWorkers;

        static int _plans, _links, _routes, _queries, _rays, _updates, _frame;
        static long _planTicks, _linkTicks, _routeTicks;
        static float _time;

        public static long Now => Stopwatch.GetTimestamp();

        /// <summary>Starts a sample window (<see cref="G3.Unity.G3PerfProbe.SampleBegin"/>).</summary>
        public static void Begin()
        {
            _plans = Plans; _links = LinkSearches; _routes = Routes; _queries = PathQueries; _rays = GroundRays; _updates = Updates;
            _planTicks = PlanTicks; _linkTicks = LinkTicks; _routeTicks = RouteTicks;
            _frame = UnityEngine.Time.frameCount;
            _time = UnityEngine.Time.realtimeSinceStartup;
        }

        /// <summary>The window since <see cref="Begin"/>: per second (and ms of main thread per second), rays per frame.</summary>
        public static string Line()
        {
            float secs = UnityEngine.Mathf.Max(0.01f, UnityEngine.Time.realtimeSinceStartup - _time);
            int frames = UnityEngine.Mathf.Max(1, UnityEngine.Time.frameCount - _frame);
            double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / secs;
            return string.Format(CultureInfo.InvariantCulture,
                "nav plans {0:F1}/s {1:F2} ms/s, links {2:F1}/s {3:F2} ms/s, routes {4:F1}/s {5:F2} ms/s, path queries {6:F1}/frame, ground rays {7:F1}/frame, " +
                "navmesh updates {8} (last {9:F1} s, building {10}, workers {11}), drop links {12}",
                (Plans - _plans) / secs, ms(PlanTicks - _planTicks), (LinkSearches - _links) / secs, ms(LinkTicks - _linkTicks),
                (Routes - _routes) / secs, ms(RouteTicks - _routeTicks), (PathQueries - _queries) / (float)frames,
                (GroundRays - _rays) / (float)frames, Updates - _updates, LastUpdateSeconds, Building ? "yes" : "no", BuildWorkers,
                NavDropLinks.Count);
        }
    }
}
