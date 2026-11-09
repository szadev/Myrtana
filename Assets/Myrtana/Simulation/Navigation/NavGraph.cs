using System;
using System.Collections.Generic;
using System.Threading;

namespace Myrtana.Simulation
{
    /// <summary>What a position lies in: a nav zone, a nav path, or nothing.</summary>
    public enum NavAreaKind { None, Zone, Path }

    /// <summary>A located position (<see cref="NavGraph.Locate"/>).</summary>
    public readonly struct NavArea
    {
        public NavArea(NavAreaKind kind, int index, float snapped)
        {
            Kind = kind;
            Index = index;
            Snapped = snapped;
        }

        public NavAreaKind Kind { get; }
        /// <summary>Zone or path index in the graph.</summary>
        public int Index { get; }
        /// <summary>0 when the position lies in the area; else how far (flat metres) it was snapped onto it.</summary>
        public float Snapped { get; }
        public bool IsValid => Kind != NavAreaKind.None;
    }

    /// <summary>One point of a route: where to go, how wide the way is there (0 = inside a zone), and on what.</summary>
    public readonly struct NavRoutePoint
    {
        public NavRoutePoint(float x, float y, float z, float width, NavAreaKind on, int index)
        {
            X = x;
            Y = y;
            Z = z;
            Width = width;
            On = on;
            Index = index;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        /// <summary>Half-width of the path ribbon at this point (metres); 0 for zone points and the route's ends.</summary>
        public float Width { get; }
        /// <summary>The leg that ends at this point runs on a zone (straight, free) or along a path ribbon.</summary>
        public NavAreaKind On { get; }
        /// <summary>The zone / path index of that leg.</summary>
        public int Index { get; }
    }

    /// <summary>The result of <see cref="NavGraph.FindRoute"/>.</summary>
    public sealed class NavRoute
    {
        public bool Found { get; internal set; }
        public NavArea Start { get; internal set; }
        public NavArea Goal { get; internal set; }
        /// <summary>From the start position to the goal position (both included).</summary>
        public List<NavRoutePoint> Points { get; } = new List<NavRoutePoint>();
        /// <summary>Length of the polyline (metres, 3D).</summary>
        public float Length { get; internal set; }
        /// <summary>A* nodes expanded.</summary>
        public int Expanded { get; internal set; }
        /// <summary>Why there is no route, or how it goes (for the logs).</summary>
        public string Note { get; internal set; } = "";
    }

    /// <summary>
    /// Gothic 3's navigation graph over the designer-placed zones and paths (Docs/navigation.md; G3 gCNavigationMap
    /// BuildLists + FindWay, CP 1.75). Every path gives two nodes, its first point (tagged with zone A) and its last point
    /// (zone B), joined by an edge as long as the path; inside a zone every two of its nodes are joined by a straight edge.
    /// Zones meet only through paths. A position is located like G3 GetZone: the smallest zone whose polygon contains it
    /// and whose height band (or a LinkInner priority flag) admits it, else a path whose ribbon contains it. A route is an
    /// A* search from the start's area to the goal's; it returns a polyline with the ribbon width per point. Zones and
    /// paths of switched-off sectors are skipped. Pure C#: the presentation follows the polyline with its own local
    /// movement (NavMesh or the ribbon itself).
    /// </summary>
    public sealed class NavGraph
    {
        const float CellSize = 64f;

        sealed class Zone
        {
            public string Id, Name;
            public int Sector;
            public float[] P; // x, y, z
            public float Cx, Cz, Radius, Top, Bottom;
            public NavLinkFlags Link;
            public readonly List<int> Nodes = new List<int>();
            /// <summary>The way across it inside its polygon (lazy).</summary>
            public NavZoneWay Way;
        }

        sealed class Path
        {
            public string Id, ZoneAId, ZoneBId;
            public int Sector;
            public float[] P, R, Along; // Along: distance from the first point per point (3D)
            public int ZoneA = -1, ZoneB = -1;
            public float Top, Bottom, Length;
            public bool Unlimited;
            public NavLinkFlags Link;
            public int Count => R.Length;
        }

        readonly List<Zone> _zones = new List<Zone>();
        readonly List<Path> _paths = new List<Path>();
        readonly List<string> _sectors = new List<string>();
        readonly Dictionary<string, int> _zoneById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<long, List<int>> _zoneCells = new Dictionary<long, List<int>>();
        readonly Dictionary<long, List<int>> _pathCells = new Dictionary<long, List<int>>();
        int[] _net; // connected component per node
        float[] _nodePos; // x, y, z per node
        Func<string, bool> _sectorEnabled;
        bool[] _sectorOn;

        /// <param name="sectorEnabled">Whether a world sector is on now (null = all on); see <see cref="RefreshSectors"/>.</param>
        /// <param name="relink">Find each path end's zone by position like the G3 compiler (true), or trust the stored ids.</param>
        public NavGraph(IEnumerable<NavSectorDef> sectors, Func<string, bool> sectorEnabled = null, bool relink = true)
        {
            _sectorEnabled = sectorEnabled;
            foreach (var s in sectors ?? Array.Empty<NavSectorDef>())
            {
                if (s == null) continue;
                int si = _sectors.Count;
                _sectors.Add(s.Id);
                foreach (var z in s.Zones) AddZone(z, si);
                foreach (var p in s.Paths) AddPath(p, si);
            }
            // The zones at a path's ends, as the G3 navigation compiler sets them (CompileStaticNavigationScene, "zone
            // detection for all nav paths"): the zone GetZone finds at the first / last point, zones only, every sector.
            // The stored ids are stale in the data (1705 of 4159 paths share one placeholder pair).
            foreach (var p in _paths)
            {
                int n = p.Count - 1;
                p.ZoneA = relink ? BestZone(p.P[0], p.P[1], p.P[2], false, out _) : ZoneIndex(p.ZoneAId);
                p.ZoneB = relink ? BestZone(p.P[3 * n], p.P[3 * n + 1], p.P[3 * n + 2], false, out _) : ZoneIndex(p.ZoneBId);
                if (p.ZoneA < 0) DanglingPathEnds++;
                if (p.ZoneB < 0) DanglingPathEnds++;
                if (p.ZoneA != ZoneIndex(p.ZoneAId)) RelinkedPathEnds++;
                if (p.ZoneB != ZoneIndex(p.ZoneBId)) RelinkedPathEnds++;
            }
            RefreshSectors();
            BuildNets();
        }

        public int ZoneCount => _zones.Count;
        public int PathCount => _paths.Count;
        public int NodeCount => 2 * _paths.Count;
        /// <summary>Path ends in no zone (G3 "critical navigation path"): only their own path leads there.</summary>
        public int DanglingPathEnds { get; private set; }
        /// <summary>Path ends whose zone differs from the stored ZoneA / ZoneB id.</summary>
        public int RelinkedPathEnds { get; private set; }
        /// <summary>Connected components of the way net (ignoring sectors).</summary>
        public int NetCount { get; private set; }

        public string ZoneName(int zone) => zone >= 0 && zone < _zones.Count ? _zones[zone].Name : null;
        public string ZoneId(int zone) => zone >= 0 && zone < _zones.Count ? _zones[zone].Id : null;
        public string PathId(int path) => path >= 0 && path < _paths.Count ? _paths[path].Id : null;
        public string SectorOfZone(int zone) => zone >= 0 && zone < _zones.Count ? _sectors[_zones[zone].Sector] : null;
        public string SectorOfPath(int path) => path >= 0 && path < _paths.Count ? _sectors[_paths[path].Sector] : null;

        /// <summary>Describes an area for the logs.</summary>
        public string Describe(NavArea a)
        {
            switch (a.Kind)
            {
                case NavAreaKind.Zone: return $"zone {_zones[a.Index].Name} {{{_zones[a.Index].Id}}}" + (a.Snapped > 0 ? $" (snapped {a.Snapped:0.0} m)" : "");
                case NavAreaKind.Path: return $"path {{{_paths[a.Index].Id}}}" + (a.Snapped > 0 ? $" (snapped {a.Snapped:0.0} m)" : "");
                default: return "none";
            }
        }

        /// <summary>Re-reads the sector switches (call after <see cref="WorldSectors.Changed"/>).</summary>
        public void RefreshSectors()
        {
            _sectorOn = new bool[_sectors.Count];
            for (int i = 0; i < _sectors.Count; i++) _sectorOn[i] = _sectorEnabled == null || _sectorEnabled(_sectors[i]);
        }

        void AddZone(NavZoneDef def, int sector)
        {
            if (def.Points.Length < 9) return;
            var z = new Zone
            {
                Id = def.Id, Name = def.Name, Sector = sector, P = def.Points, Cx = def.Center.Length > 1 ? def.Center[0] : def.Points[0],
                Cz = def.Center.Length > 1 ? def.Center[1] : def.Points[2], Radius = def.Radius, Top = def.Top, Bottom = def.Bottom, Link = def.Link,
            };
            int index = _zones.Count;
            _zones.Add(z);
            if (def.Id != null && !_zoneById.ContainsKey(def.Id)) _zoneById[def.Id] = index;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < z.P.Length; i += 3)
            {
                minX = Math.Min(minX, z.P[i]); maxX = Math.Max(maxX, z.P[i]);
                minZ = Math.Min(minZ, z.P[i + 2]); maxZ = Math.Max(maxZ, z.P[i + 2]);
            }
            Register(_zoneCells, index, minX, minZ, maxX, maxZ);
        }

        void AddPath(NavPathDef def, int sector)
        {
            int n = def.Points.Length / 3;
            if (n < 2) return;
            var p = new Path
            {
                Id = def.Id, ZoneAId = def.ZoneA, ZoneBId = def.ZoneB, Sector = sector, P = def.Points, R = new float[n], Along = new float[n], Top = def.Top, Bottom = def.Bottom,
                Unlimited = def.UnlimitedHeight, Link = def.Link,
            };
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                p.R[i] = i < def.Radii.Length ? Math.Max(0.05f, def.Radii[i]) : 0.2f;
                if (i > 0) p.Along[i] = p.Along[i - 1] + Dist3(p.P, 3 * (i - 1), p.P, 3 * i);
                minX = Math.Min(minX, p.P[3 * i] - p.R[i]); maxX = Math.Max(maxX, p.P[3 * i] + p.R[i]);
                minZ = Math.Min(minZ, p.P[3 * i + 2] - p.R[i]); maxZ = Math.Max(maxZ, p.P[3 * i + 2] + p.R[i]);
            }
            p.Length = p.Along[n - 1];
            int index = _paths.Count;
            _paths.Add(p);
            Register(_pathCells, index, minX, minZ, maxX, maxZ);
        }

        int ZoneIndex(string id) => id != null && _zoneById.TryGetValue(id, out int i) ? i : -1;

        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        static void Register(Dictionary<long, List<int>> cells, int index, float minX, float minZ, float maxX, float maxZ)
        {
            int x0 = (int)Math.Floor(minX / CellSize), x1 = (int)Math.Floor(maxX / CellSize);
            int z0 = (int)Math.Floor(minZ / CellSize), z1 = (int)Math.Floor(maxZ / CellSize);
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (!cells.TryGetValue(Key(x, z), out var list)) cells[Key(x, z)] = list = new List<int>();
                list.Add(index);
            }
        }

        void BuildNets()
        {
            _net = new int[NodeCount];
            _nodePos = new float[3 * NodeCount];
            for (int i = 0; i < NodeCount; i++)
            {
                var q = Pos(i);
                _nodePos[3 * i] = q[0];
                _nodePos[3 * i + 1] = q[1];
                _nodePos[3 * i + 2] = q[2];
            }
            var parent = new int[NodeCount];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            for (int i = 0; i < _paths.Count; i++)
            {
                var p = _paths[i];
                if (p.ZoneA >= 0) _zones[p.ZoneA].Nodes.Add(2 * i);
                if (p.ZoneB >= 0) _zones[p.ZoneB].Nodes.Add(2 * i + 1);
                Union(2 * i, 2 * i + 1);
            }
            foreach (var z in _zones)
                for (int k = 1; k < z.Nodes.Count; k++) Union(z.Nodes[0], z.Nodes[k]);
            var ids = new Dictionary<int, int>();
            for (int i = 0; i < _net.Length; i++)
            {
                int root = Find(i);
                if (!ids.TryGetValue(root, out int id)) ids[root] = id = ids.Count;
                _net[i] = id;
            }
            NetCount = ids.Count;
        }

        /// <summary>The way-net component of a zone's / path's nodes (-1 = a zone without paths).</summary>
        public int NetOf(NavArea a)
        {
            if (a.Kind == NavAreaKind.Path) return _net[2 * a.Index];
            if (a.Kind == NavAreaKind.Zone && _zones[a.Index].Nodes.Count > 0) return _net[_zones[a.Index].Nodes[0]];
            return -1;
        }

        // ------------------------------------------------------------------------------------------------- locate

        /// <summary>
        /// The area of a position (G3 GetZone): the smallest zone (bounding radius) that contains it in the polygon and
        /// admits its height (band, or a LinkInner priority flag; a priority claim beats one without); when no zone wins
        /// by priority, a path whose ribbon contains it and whose band admits it (over a zone only with priority).
        /// Positions in no area are snapped to the nearest zone / path within <paramref name="snap"/> flat metres whose
        /// band is within <paramref name="snapHeight"/> metres (G3 falls back to the NPC's last usable nav position).
        /// </summary>
        public NavArea Locate(float x, float y, float z, float snap = 0f, float snapHeight = 3f)
        {
            int best = BestZone(x, y, z, true, out bool bestPrio);
            if (best < 0 || !bestPrio)
            {
                if (_pathCells.TryGetValue(CellOf(x, z), out var paths))
                    foreach (int i in paths)
                    {
                        var p = _paths[i];
                        if (!_sectorOn[p.Sector]) continue;
                        Band(p.Top, p.Bottom, p.Link, y, out bool inBand, out bool prio);
                        if (best >= 0 ? !prio : !(prio || inBand)) continue;
                        if (OnPath(p, x, z, out _, out _) < 0) continue;
                        return new NavArea(NavAreaKind.Path, i, 0);
                    }
            }
            if (best >= 0) return new NavArea(NavAreaKind.Zone, best, 0);
            return snap > 0 ? Snap(x, y, z, snap, snapHeight) : default;
        }

        /// <summary>G3 GetZone over zones only: the smallest admitting zone, a priority claim first; -1 = none.</summary>
        int BestZone(float x, float y, float z, bool sectors, out bool bestPrio)
        {
            int best = -1;
            bestPrio = false;
            if (!_zoneCells.TryGetValue(CellOf(x, z), out var zones)) return -1;
            foreach (int i in zones)
            {
                var zo = _zones[i];
                if (sectors && !_sectorOn[zo.Sector]) continue;
                Band(zo.Top, zo.Bottom, zo.Link, y, out bool inBand, out bool prio);
                if (!prio && !inBand) continue;
                if (best >= 0 && (bestPrio && !prio)) continue;
                if (best >= 0 && bestPrio == prio && zo.Radius >= _zones[best].Radius) continue;
                float dx = x - zo.Cx, dz = z - zo.Cz;
                if (dx * dx + dz * dz > zo.Radius * zo.Radius) continue;
                if (!InPolygon(zo.P, x, z)) continue;
                best = i;
                bestPrio = prio;
            }
            return best;
        }

        long CellOf(float x, float z) => Key((int)Math.Floor(x / CellSize), (int)Math.Floor(z / CellSize));

        static void Band(float top, float bottom, NavLinkFlags link, float y, out bool inBand, out bool prio)
        {
            inBand = y >= bottom && y <= top;
            prio = inBand ? (link & NavLinkFlags.Inner) != 0 : y > top ? (link & NavLinkFlags.Top) != 0 : (link & NavLinkFlags.Bottom) != 0;
        }

        /// <summary>
        /// The area of a position within the way net <paramref name="net"/> (any net when negative; zones without paths never
        /// count): a containing zone of that net (the smallest), else a path whose ribbon contains it, else the nearest zone /
        /// path of the net within <paramref name="snap"/> flat metres (band within <paramref name="snapHeight"/> m). Used when
        /// <see cref="Locate"/>'s area has no paths or lies in another net (fix-37).
        /// </summary>
        public NavArea LocateInNet(float x, float y, float z, int net, float snap = 30f, float snapHeight = 3f)
        {
            int best = -1;
            if (_zoneCells.TryGetValue(CellOf(x, z), out var zones))
                foreach (int i in zones)
                {
                    var zo = _zones[i];
                    if (!_sectorOn[zo.Sector] || !InNet(new NavArea(NavAreaKind.Zone, i, 0), net)) continue;
                    Band(zo.Top, zo.Bottom, zo.Link, y, out bool inBand, out bool prio);
                    if (!inBand && !prio) continue;
                    if (best >= 0 && zo.Radius >= _zones[best].Radius) continue;
                    float dx = x - zo.Cx, dz = z - zo.Cz;
                    if (dx * dx + dz * dz > zo.Radius * zo.Radius || !InPolygon(zo.P, x, z)) continue;
                    best = i;
                }
            if (best >= 0) return new NavArea(NavAreaKind.Zone, best, 0);
            if (_pathCells.TryGetValue(CellOf(x, z), out var paths))
                foreach (int i in paths)
                {
                    var p = _paths[i];
                    if (!_sectorOn[p.Sector] || !InNet(new NavArea(NavAreaKind.Path, i, 0), net)) continue;
                    Band(p.Top, p.Bottom, p.Link, y, out bool inBand, out bool prio);
                    if (!inBand && !prio) continue;
                    if (OnPath(p, x, z, out _, out _) >= 0) return new NavArea(NavAreaKind.Path, i, 0);
                }
            return snap > 0 ? Snap(x, y, z, snap, snapHeight, a => InNet(a, net)) : default;
        }

        bool InNet(NavArea a, int net)
        {
            int n = NetOf(a);
            return n >= 0 && (net < 0 || n == net);
        }

        NavArea Snap(float x, float y, float z, float snap, float snapHeight, Func<NavArea, bool> accept = null)
        {
            int cr = (int)Math.Ceiling(snap / CellSize);
            int cx = (int)Math.Floor(x / CellSize), cz = (int)Math.Floor(z / CellSize);
            float bestD = snap;
            var best = default(NavArea);
            // seen zones / paths: the graph's reusable stamp sets (two HashSets per call before, perf-9)
            var seen = Interlocked.Exchange(ref _snapSeen, null) ?? new SnapSeen();
            seen.Begin(_zones.Count, _paths.Count);
            try
            {
                best = SnapCells(seen, cx, cz, cr, x, y, z, snapHeight, accept, ref bestD);
            }
            finally
            {
                _snapSeen = seen;
            }
            return best;
        }

        SnapSeen _snapSeen; // the idle scratch (null while a snap uses it)

        NavArea SnapCells(SnapSeen seen, int cx, int cz, int cr, float x, float y, float z, float snapHeight, Func<NavArea, bool> accept,
                          ref float bestD)
        {
            var best = default(NavArea);
            for (int gx = cx - cr; gx <= cx + cr; gx++)
            for (int gz = cz - cr; gz <= cz + cr; gz++)
            {
                if (_zoneCells.TryGetValue(Key(gx, gz), out var zs))
                    foreach (int i in zs)
                    {
                        var zo = _zones[i];
                        if (!seen.AddZone(i) || !_sectorOn[zo.Sector] || y > zo.Top + snapHeight || y < zo.Bottom - snapHeight) continue;
                        float d = InPolygon(zo.P, x, z) ? 0f : EdgeDistance(zo.P, x, z);
                        if (d < bestD && (accept == null || accept(new NavArea(NavAreaKind.Zone, i, 0)))) { bestD = d; best = new NavArea(NavAreaKind.Zone, i, Math.Max(d, 0.001f)); }
                    }
                if (_pathCells.TryGetValue(Key(gx, gz), out var ps))
                    foreach (int i in ps)
                    {
                        var p = _paths[i];
                        if (!seen.AddPath(i) || !_sectorOn[p.Sector] || y > p.Top + snapHeight || y < p.Bottom - snapHeight) continue;
                        float d = Math.Max(0f, PathDistance(p, x, z, out _, out _));
                        if (d < bestD && (accept == null || accept(new NavArea(NavAreaKind.Path, i, 0)))) { bestD = d; best = new NavArea(NavAreaKind.Path, i, Math.Max(d, 0.001f)); }
                    }
            }
            return best;
        }

        /// <summary>The zones and paths one <see cref="Snap"/> has looked at: an index counts as seen when its stamp is the current one.</summary>
        sealed class SnapSeen
        {
            int[] _zones = Array.Empty<int>(), _paths = Array.Empty<int>();
            int _stamp;

            public void Begin(int zones, int paths)
            {
                if (_zones.Length < zones || _paths.Length < paths)
                {
                    _zones = new int[Math.Max(zones, _zones.Length)];
                    _paths = new int[Math.Max(paths, _paths.Length)];
                    _stamp = 0;
                }
                if (++_stamp == int.MaxValue)
                {
                    Array.Clear(_zones, 0, _zones.Length);
                    Array.Clear(_paths, 0, _paths.Length);
                    _stamp = 1;
                }
            }

            /// <summary>Like HashSet.Add: true the first time.</summary>
            public bool AddZone(int i) => Mark(_zones, i);
            public bool AddPath(int i) => Mark(_paths, i);

            bool Mark(int[] stamps, int i)
            {
                if (stamps[i] == _stamp) return false;
                stamps[i] = _stamp;
                return true;
            }
        }

        /// <summary>Even-odd test of (x, z) in the polygon's x/z projection.</summary>
        static bool InPolygon(float[] p, float x, float z)
        {
            bool inside = false;
            int n = p.Length / 3;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = p[3 * i], zi = p[3 * i + 2], xj = p[3 * j], zj = p[3 * j + 2];
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        static float EdgeDistance(float[] p, float x, float z)
        {
            int n = p.Length / 3;
            float best = float.MaxValue;
            for (int i = 0, j = n - 1; i < n; j = i++)
                best = Math.Min(best, SegmentDistance(p[3 * j], p[3 * j + 2], p[3 * i], p[3 * i + 2], x, z, out _));
            return best;
        }

        static float SegmentDistance(float ax, float az, float bx, float bz, float x, float z, out float t)
        {
            float dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
            t = len2 > 1e-8f ? Math.Max(0f, Math.Min(1f, ((x - ax) * dx + (z - az) * dz) / len2)) : 0f;
            float px = ax + t * dx - x, pz = az + t * dz - z;
            return (float)Math.Sqrt(px * px + pz * pz);
        }

        /// <summary>
        /// Flat distance from (x, z) to the ribbon's edge (negative inside; G3 Test3DPointOnPath: each segment a "double
        /// cylinder" with the radii of its two points), and the closest segment and its parameter.
        /// </summary>
        static float PathDistance(Path p, float x, float z, out int segment, out float t)
        {
            float best = float.MaxValue;
            segment = 0;
            t = 0;
            for (int i = 0; i + 1 < p.Count; i++)
            {
                float d = SegmentDistance(p.P[3 * i], p.P[3 * i + 2], p.P[3 * i + 3], p.P[3 * i + 5], x, z, out float u);
                d -= p.R[i] + (p.R[i + 1] - p.R[i]) * u;
                if (d < best) { best = d; segment = i; t = u; }
            }
            return best;
        }

        static int OnPath(Path p, float x, float z, out float along, out float t)
        {
            float d = PathDistance(p, x, z, out int seg, out t);
            along = p.Along[seg] + (p.Along[seg + 1] - p.Along[seg]) * t;
            return d <= 0f ? seg : -1;
        }

        // ------------------------------------------------------------------------------------------------- route

        /// <summary>
        /// A route from <paramref name="from"/> to <paramref name="to"/> (x, y, z world metres). Start and goal are located
        /// with <see cref="Locate"/> (snapped within <paramref name="snap"/> m). Every path is usable (G3 skips limited
        /// paths only for NPCs with DontUseLimitedNavPaths, and closed paths: doors, which this world does not close).
        /// </summary>
        public NavRoute FindRoute(float fx, float fy, float fz, float tx, float ty, float tz, float snap = 30f, int maxExpanded = 20000)
        {
            var route = new NavRoute();
            var s = Locate(fx, fy, fz, snap);
            var g = Locate(tx, ty, tz, snap);
            route.Start = s;
            route.Goal = g;
            if (!s.IsValid) { route.Note = "start in no nav zone / path"; return route; }
            if (!g.IsValid) { route.Note = "goal in no nav zone / path"; return route; }
            string moved = Connect(ref s, ref g, fx, fy, fz, tx, ty, tz, snap);
            route.Start = s;
            route.Goal = g;
            var start = new[] { fx, fy, fz };
            var goal = new[] { tx, ty, tz };
            route.Points.Add(new NavRoutePoint(fx, fy, fz, 0, s.Kind, s.Index));
            if (s.Kind == g.Kind && s.Index == g.Index)
            {
                if (s.Kind == NavAreaKind.Path) AppendAlong(route, s.Index, AlongOf(s.Index, fx, fz), AlongOf(s.Index, tx, tz));
                else AddBends(route, s.Index, tx, tz);
                route.Points.Add(new NavRoutePoint(tx, ty, tz, 0, g.Kind, g.Index));
                Finish(route, moved == null ? "same area" : "same area; " + moved);
                return route;
            }
            int sn = NetOf(s), gn = NetOf(g);
            if (sn < 0 || gn < 0 || sn != gn)
            {
                route.Note = sn < 0 ? "start zone has no paths" : gn < 0 ? "goal zone has no paths" : $"start and goal are in different nets ({sn} / {gn})";
                if (moved != null) route.Note += $" ({moved})";
                route.Points.Clear();
                return route;
            }

            // A* over the path end nodes; the start and the goal are virtual nodes. The search state is the graph's reusable
            // scratch (perf-9: three node arrays, a heap and a map were allocated per route, ~28 KB for the world graph).
            var search = RentSearch();
            try
            {
                return Search(route, search, s, g, start, goal, fx, fz, tx, ty, tz, maxExpanded, moved);
            }
            finally
            {
                ReturnSearch(search);
            }
        }

        NavRoute Search(NavRoute route, SearchState search, NavArea s, NavArea g, float[] start, float[] goal, float fx, float fz,
                        float tx, float ty, float tz, int maxExpanded, string moved)
        {
            search.Begin(NodeCount);
            var open = search.Open;
            foreach (var (node, cost) in Entries(s, start))
            {
                if (cost < search.Cost(node)) { search.Reach(node, cost, -1); open.Push(node, cost + H(node, goal)); }
            }
            var goalCost = search.GoalCost;
            foreach (var (node, cost) in Entries(g, goal)) goalCost[node] = cost;
            float bestTotal = float.PositiveInfinity;
            int bestLast = -1, expanded = 0;
            while (open.Count > 0)
            {
                open.Pop(out int u, out float f);
                if (search.IsClosed(u)) continue;
                if (f >= bestTotal) break;
                search.Close(u);
                if (++expanded > maxExpanded) break;
                if (goalCost.TryGetValue(u, out float last) && search.Cost(u) + last < bestTotal) { bestTotal = search.Cost(u) + last; bestLast = u; }
                int pi = u >> 1;
                var p = _paths[pi];
                if (_sectorOn[p.Sector]) Relax(u, u ^ 1, p.Length, search, goal);
                int zi = (u & 1) == 0 ? p.ZoneA : p.ZoneB;
                if (zi >= 0 && _sectorOn[_zones[zi].Sector])
                    foreach (int v in _zones[zi].Nodes)
                        if (v != u) Relax(u, v, Dist3(_nodePos, 3 * u, _nodePos, 3 * v), search, goal);
            }
            route.Expanded = expanded;
            if (bestLast < 0)
            {
                route.Points.Clear();
                route.Note = $"no way ({expanded} nodes searched)";
                return route;
            }
            var chain = search.Chain;
            for (int v = bestLast; v >= 0; v = search.From[v]) chain.Add(v);
            chain.Reverse();
            // the start's leg to the first node
            int first = chain[0];
            if (s.Kind == NavAreaKind.Path) AppendAlong(route, s.Index, AlongOf(s.Index, fx, fz), (first & 1) == 0 ? 0f : _paths[s.Index].Length);
            else AddNode(route, first, NavAreaKind.Zone, s.Index);
            for (int k = 1; k < chain.Count; k++)
            {
                int a = chain[k - 1], b = chain[k];
                if ((a >> 1) == (b >> 1)) AppendAlong(route, a >> 1, (a & 1) == 0 ? 0f : _paths[a >> 1].Length, (b & 1) == 0 ? 0f : _paths[b >> 1].Length);
                else AddNode(route, b, NavAreaKind.Zone, (a & 1) == 0 ? _paths[a >> 1].ZoneA : _paths[a >> 1].ZoneB);
            }
            if (g.Kind == NavAreaKind.Path) AppendAlong(route, g.Index, (bestLast & 1) == 0 ? 0f : _paths[g.Index].Length, AlongOf(g.Index, tx, tz));
            else AddBends(route, g.Index, tx, tz);
            route.Points.Add(new NavRoutePoint(tx, ty, tz, 0, g.Kind, g.Index));
            Finish(route, $"{chain.Count} nodes, {expanded} searched" + (moved == null ? "" : "; " + moved));
            return route;
        }

        /// <summary>
        /// A start or goal whose area has no paths (a small zone of its own: Hammerclan_Ore inside the camp's zone) or lies in
        /// another net than the other end steps onto the other end's net: the containing zone of that net, else its nearest
        /// zone / path within <see cref="ConnectSnap"/> m (G3's compiler links every path end to the zone GetZone finds, and an
        /// NPC off the net walks from its last usable nav position, which is on the net) (fix-37). Both ends without paths:
        /// the start goes onto the nearest net, the goal onto the start's. Null when both ends were fine, else what moved.
        /// </summary>
        /// <summary>How far (flat m) <see cref="Connect"/> looks for the other end's net (Hammerclan_Ore: 38 m to the camp's zone).</summary>
        public const float ConnectSnap = 60f;

        string Connect(ref NavArea s, ref NavArea g, float fx, float fy, float fz, float tx, float ty, float tz, float snap)
        {
            int sn = NetOf(s), gn = NetOf(g);
            if (sn >= 0 && sn == gn) return null;
            string moved = null;
            if (sn < 0 || (gn >= 0 && sn != gn))
            {
                var s2 = LocateInNet(fx, fy, fz, gn, Math.Max(snap, ConnectSnap));
                if (s2.IsValid)
                {
                    moved = $"start {(sn < 0 ? "zone without paths" : "in net " + sn)} -> {Describe(s2)}";
                    s = s2;
                    sn = NetOf(s);
                }
                else moved = "no start on the goal's net";
            }
            if (gn < 0 || (sn >= 0 && gn != sn))
            {
                var g2 = LocateInNet(tx, ty, tz, sn, Math.Max(snap, ConnectSnap));
                if (g2.IsValid)
                {
                    moved = (moved == null ? "" : moved + "; ") + $"goal {(gn < 0 ? "zone without paths" : "in net " + gn)} -> {Describe(g2)}";
                    g = g2;
                }
                else moved = (moved == null ? "" : moved + "; ") + "no goal on the start's net";
            }
            return moved;
        }

        void Relax(int u, int v, float cost, SearchState search, float[] goal)
        {
            if (search.IsClosed(v)) return;
            float c = search.Cost(u) + cost;
            if (c >= search.Cost(v)) return;
            search.Reach(v, c, u);
            search.Open.Push(v, c + H(v, goal));
        }

        SearchState _search; // the idle scratch (null while a search uses it)

        SearchState RentSearch() => Interlocked.Exchange(ref _search, null) ?? new SearchState();

        void ReturnSearch(SearchState search) => _search = search;

        /// <summary>
        /// A* state over the node indices, reused between routes: a node's cost and predecessor count only when its stamp is
        /// the current search's, so nothing is cleared or allocated per route.
        /// </summary>
        sealed class SearchState
        {
            float[] _cost = Array.Empty<float>();
            public int[] From = Array.Empty<int>(); // previous node; -1 = from the start position
            int[] _reached = Array.Empty<int>(), _closed = Array.Empty<int>();
            int _stamp;
            public readonly MinHeap Open = new MinHeap();
            public readonly Dictionary<int, float> GoalCost = new Dictionary<int, float>();
            public readonly List<int> Chain = new List<int>();

            public void Begin(int nodes)
            {
                if (_cost.Length < nodes)
                {
                    _cost = new float[nodes];
                    From = new int[nodes];
                    _reached = new int[nodes];
                    _closed = new int[nodes];
                    _stamp = 0;
                }
                if (++_stamp == int.MaxValue)
                {
                    Array.Clear(_reached, 0, _reached.Length);
                    Array.Clear(_closed, 0, _closed.Length);
                    _stamp = 1;
                }
                Open.Clear();
                GoalCost.Clear();
                Chain.Clear();
            }

            public float Cost(int node) => _reached[node] == _stamp ? _cost[node] : float.PositiveInfinity;

            public void Reach(int node, float cost, int from)
            {
                _cost[node] = cost;
                From[node] = from;
                _reached[node] = _stamp;
            }

            public bool IsClosed(int node) => _closed[node] == _stamp;

            public void Close(int node) => _closed[node] = _stamp;
        }

        float H(int node, float[] goal) => Dist3(_nodePos, 3 * node, goal, 0);

        float[] Pos(int node)
        {
            var p = _paths[node >> 1];
            int i = (node & 1) == 0 ? 0 : p.Count - 1;
            return new[] { p.P[3 * i], p.P[3 * i + 1], p.P[3 * i + 2] };
        }

        /// <summary>The nodes reachable from a position in an area and what reaching them costs.</summary>
        IEnumerable<(int node, float cost)> Entries(NavArea a, float[] pos)
        {
            if (a.Kind == NavAreaKind.Zone)
            {
                foreach (int v in _zones[a.Index].Nodes) yield return (v, Dist3(pos, 0, _nodePos, 3 * v));
                yield break;
            }
            var p = _paths[a.Index];
            float along = AlongOf(a.Index, pos[0], pos[2]);
            yield return (2 * a.Index, along);
            yield return (2 * a.Index + 1, p.Length - along);
        }

        float AlongOf(int path, float x, float z)
        {
            var p = _paths[path];
            PathDistance(p, x, z, out int seg, out float t);
            return p.Along[seg] + (p.Along[seg + 1] - p.Along[seg]) * t;
        }

        void AddNode(NavRoute route, int node, NavAreaKind on, int index)
        {
            var q = Pos(node);
            if (on == NavAreaKind.Zone) AddBends(route, index, q[0], q[2]);
            route.Points.Add(new NavRoutePoint(q[0], q[1], q[2], 0, on, index));
        }

        NavZoneWay WayOf(int zone) => _zones[zone].Way ??= new NavZoneWay(_zones[zone].P);

        /// <summary>
        /// The bends of the way across the zone from the route's last point to (x, z): the reflex corners the straight
        /// line would cut (a winding valley: fix-35), as zone points. Only the chosen route gets them: the search still
        /// weighs a crossing by its straight length (bends everywhere made a plan 50-90 ms instead of under 15 ms).
        /// </summary>
        void AddBends(NavRoute route, int zone, float x, float z)
        {
            if (route.Points.Count == 0 || zone < 0) return;
            var way = WayOf(zone);
            if (way.Convex) return;
            var last = route.Points[route.Points.Count - 1];
            foreach (int i in way.Way(last.X, last.Z, x, z, out _))
            {
                way.Corner(i, out float cx, out float cy, out float cz);
                route.Points.Add(new NavRoutePoint(cx, cy, cz, 0, NavAreaKind.Zone, zone));
            }
        }

        /// <summary>The path's centre line from distance <paramref name="a"/> to <paramref name="b"/> (either way), with its widths.</summary>
        void AppendAlong(NavRoute route, int path, float a, float b)
        {
            var p = _paths[path];
            if (Math.Abs(b - a) < 1e-3f) { Add(route, p, a, path); return; }
            Add(route, p, a, path);
            if (b > a)
            {
                for (int i = 0; i < p.Count; i++) if (p.Along[i] > a && p.Along[i] < b) AddPoint(route, p, i, path);
            }
            else
            {
                for (int i = p.Count - 1; i >= 0; i--) if (p.Along[i] < a && p.Along[i] > b) AddPoint(route, p, i, path);
            }
            Add(route, p, b, path);
        }

        static void AddPoint(NavRoute route, Path p, int i, int path) =>
            route.Points.Add(new NavRoutePoint(p.P[3 * i], p.P[3 * i + 1], p.P[3 * i + 2], p.R[i], NavAreaKind.Path, path));

        static void Add(NavRoute route, Path p, float along, int path)
        {
            int i = 0;
            while (i + 2 < p.Count && p.Along[i + 1] < along) i++;
            float len = p.Along[i + 1] - p.Along[i];
            float t = len > 1e-5f ? Math.Max(0f, Math.Min(1f, (along - p.Along[i]) / len)) : 0f;
            float L(int k) => p.P[3 * i + k] + (p.P[3 * i + 3 + k] - p.P[3 * i + k]) * t;
            var pt = new NavRoutePoint(L(0), L(1), L(2), p.R[i] + (p.R[i + 1] - p.R[i]) * t, NavAreaKind.Path, path);
            if (route.Points.Count > 0)
            {
                var last = route.Points[route.Points.Count - 1];
                float dx = last.X - pt.X, dy = last.Y - pt.Y, dz = last.Z - pt.Z;
                if (dx * dx + dy * dy + dz * dz < 0.01f) return;
            }
            route.Points.Add(pt);
        }

        static void Finish(NavRoute route, string note)
        {
            float len = 0;
            for (int i = 1; i < route.Points.Count; i++)
            {
                var a = route.Points[i - 1];
                var b = route.Points[i];
                len += (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
            }
            route.Length = len;
            route.Found = true;
            route.Note = note;
        }

        static float Dist3(float[] a, int ia, float[] b, int ib)
        {
            float dx = a[ia] - b[ib], dy = a[ia + 1] - b[ib + 1], dz = a[ia + 2] - b[ib + 2];
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Binary min-heap of (node, priority) with lazy deletion.</summary>
        sealed class MinHeap
        {
            readonly List<(int node, float f)> _items = new List<(int, float)>();
            public int Count => _items.Count;

            public void Clear() => _items.Clear();

            public void Push(int node, float f)
            {
                _items.Add((node, f));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_items[parent].f <= f) break;
                    _items[i] = _items[parent];
                    i = parent;
                }
                _items[i] = (node, f);
            }

            public void Pop(out int node, out float f)
            {
                (node, f) = _items[0];
                var last = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0) return;
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    float mf = last.f;
                    if (l < _items.Count && _items[l].f < mf) { m = l; mf = _items[l].f; }
                    if (r < _items.Count && _items[r].f < mf) m = r;
                    if (m == i) break;
                    _items[i] = _items[m];
                    i = m;
                }
                _items[i] = last;
            }
        }
    }
}
