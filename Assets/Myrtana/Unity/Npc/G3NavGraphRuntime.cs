using System;
using System.Linq;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The session's G3 navigation graph (<see cref="NavGraph"/> over the pack's <see cref="NavSectorDef"/>s, Docs/navigation.md)
    /// for the NPC views: guides, followers that fell behind and routine walks to far points route on it and walk each
    /// leg on the NavMesh (or straight along the ribbon). Switch: command line <c>-g3navgraph 0</c> / <c>-g3navgraph 1</c>
    /// (default on), or <see cref="Enabled"/> at run time. A pack without navigation data leaves <see cref="Graph"/> null:
    /// the NavMesh-only ways (GuideRoute, catch-up) are used then.
    /// </summary>
    public static class G3NavGraphRuntime
    {
        public const string CommandLineSwitch = "-g3navgraph";
        static WorldSectors _sectors;

        /// <summary>Route NPCs on the G3 graph (true by default; <c>-g3navgraph 0</c> turns it off).</summary>
        public static bool Enabled { get; set; } = FromCommandLine();

        /// <summary>The graph of the bound session; null without navigation data.</summary>
        public static NavGraph Graph { get; private set; }

        /// <summary>The graph to route on now: null when switched off or missing.</summary>
        public static NavGraph Active => Enabled ? Graph : null;

        static bool FromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.FindIndex(args, a => string.Equals(a, CommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            return i < 0 || i + 1 >= args.Length || args[i + 1] != "0";
        }

        /// <summary>Builds the graph of <paramref name="session"/>'s content; sector switches update it.</summary>
        public static void Bind(GameSession session)
        {
            if (_sectors != null) _sectors.Changed -= OnSectorChanged;
            _sectors = session?.WorldState?.Sectors;
            Graph = null;
            var defs = session?.Content?.Database?.All<NavSectorDef>()?.ToList();
            if (defs == null || defs.Count == 0)
            {
                Debug.Log("[nav] no G3 navigation data in the content pack: NPCs walk on the NavMesh only");
                return;
            }
            var sectors = _sectors;
            Graph = new NavGraph(defs, sectors != null ? (Func<string, bool>)sectors.IsEnabled : null);
            if (_sectors != null) _sectors.Changed += OnSectorChanged;
            Debug.Log($"[nav] G3 navigation graph: {Graph.ZoneCount} zones, {Graph.PathCount} paths, {Graph.NetCount} nets, " +
                      $"{Graph.DanglingPathEnds} path ends in no zone; {(Enabled ? "on" : "off (" + CommandLineSwitch + " 0)")}");
        }

        static void OnSectorChanged(string sector) => Graph?.RefreshSectors();

        /// <summary>
        /// The G3 route from <paramref name="from"/> to <paramref name="to"/> as world points (start and goal included) into
        /// <paramref name="points"/>; false when switched off, without a graph or without a route (<paramref name="note"/>
        /// says why). For callers that move a body themselves, e.g. a dev scenario's hero on an escort route.
        /// </summary>
        public static bool TryRoute(Vector3 from, Vector3 to, System.Collections.Generic.List<Vector3> points, out float length, out string note)
        {
            points?.Clear();
            length = 0;
            var graph = Active;
            if (graph == null)
            {
                note = Graph == null ? "no G3 navigation graph" : "G3 navigation graph switched off";
                return false;
            }
            var r = graph.FindRoute(from.x, from.y, from.z, to.x, to.y, to.z);
            note = r.Note;
            if (!r.Found) return false;
            length = r.Length;
            if (points != null)
                foreach (var p in r.Points) points.Add(new Vector3(p.X, p.Y, p.Z));
            return true;
        }
    }
}
