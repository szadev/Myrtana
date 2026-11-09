using System;
using System.Collections.Generic;
using System.Numerics;

namespace G3.Unity
{
    /// <summary>
    /// fix-174: how far the relief may move each terrain position before it runs into other geometry (Docs/remaster-world.md
    /// section 14.6). Built offline over the whole world (<c>G3Cli relief facts</c>), stored per welded position in the facts table.
    /// <list type="bullet">
    /// <item><b>Open edges</b> of the terrain union (an edge of exactly one distinct triangle over all cells: G3's holes for
    /// tunnels and caves, the non-conforming seams where two cells meet with different vertices, the world rim) are pinned:
    /// whatever G3 fitted against them (a tunnel mesh, the other cell's T-junction) only stays closed while they do not move.</item>
    /// <item><b>Clearance</b>: the distance along the surface normal (both ways) to any other terrain sheet that does not touch
    /// the position (the other side of a thin wall or ridge, the roof of a pass) and the distance in any direction to a placed
    /// rock / building mesh (their walls are fitted into G3's surface; their bottoms are open). The relief's detail, swell and
    /// Phong bulge are capped to a share of it, so two sheets never cross and no mesh is uncovered or pierced.</item>
    /// </list>
    /// Engine-free (G3Tests): triangles in a uniform 3D grid, a DDA walk for the rays, a box scan for the nearest distance.
    /// </summary>
    public sealed class ReliefClearance
    {
        /// <summary>Metres per step of the stored byte (255 = this far or farther, i.e. no limit).</summary>
        public const float Step = 0.04f;
        public const byte NoLimit = 255;
        public static float MaxRange => Step * NoLimit;

        public static byte Encode(float metres) => metres >= MaxRange ? NoLimit : (byte)Math.Max(0, Math.Min(254, (int)Math.Floor(metres / Step)));
        public static float Decode(byte b) => b >= NoLimit ? float.PositiveInfinity : b * Step;

        readonly float _cell;
        readonly List<Vector3> _a = new List<Vector3>(), _b = new List<Vector3>(), _c = new List<Vector3>();
        readonly List<bool> _terrain = new List<bool>();
        readonly Dictionary<(int, int, int), List<int>> _grid = new Dictionary<(int, int, int), List<int>>();
        readonly HashSet<(Vector3, Vector3, Vector3)> _seen = new HashSet<(Vector3, Vector3, Vector3)>();
        readonly Dictionary<(Vector3, Vector3), int> _edgeUse = new Dictionary<(Vector3, Vector3), int>();

        public ReliefClearance(float cell = 4f) { _cell = Math.Max(0.5f, cell); }

        public int Triangles => _a.Count;

        static int Cmp(Vector3 a, Vector3 b)
        {
            if (a.X != b.X) return a.X < b.X ? -1 : 1;
            if (a.Y != b.Y) return a.Y < b.Y ? -1 : 1;
            if (a.Z != b.Z) return a.Z < b.Z ? -1 : 1;
            return 0;
        }

        static (Vector3, Vector3) EdgeKey(Vector3 a, Vector3 b) => Cmp(a, b) <= 0 ? (a, b) : (b, a);

        /// <summary>
        /// Adds a triangle (world metres). Terrain triangles are deduplicated (G3's cells overlap with identical triangles) and
        /// counted for the open edges; placed meshes are obstacles only.
        /// </summary>
        public void Add(Vector3 a, Vector3 b, Vector3 c, bool terrain)
        {
            if (terrain)
            {
                Vector3 x = a, y = b, z = c;
                if (Cmp(x, y) > 0) { var t = x; x = y; y = t; }
                if (Cmp(y, z) > 0) { var t = y; y = z; z = t; }
                if (Cmp(x, y) > 0) { var t = x; x = y; y = t; }
                if (Cmp(x, y) == 0 || Cmp(y, z) == 0) return;   // degenerate
                if (!_seen.Add((x, y, z))) return;
                Use(EdgeKey(a, b)); Use(EdgeKey(b, c)); Use(EdgeKey(c, a));
            }
            int id = _a.Count;
            _a.Add(a); _b.Add(b); _c.Add(c); _terrain.Add(terrain);
            var lo = Vector3.Min(a, Vector3.Min(b, c)); var hi = Vector3.Max(a, Vector3.Max(b, c));
            var i0 = CellOf(lo); var i1 = CellOf(hi);
            for (int i = i0.x; i <= i1.x; i++)
            for (int j = i0.y; j <= i1.y; j++)
            for (int k = i0.z; k <= i1.z; k++)
            {
                if (!_grid.TryGetValue((i, j, k), out var l)) _grid[(i, j, k)] = l = new List<int>(8);
                l.Add(id);
            }
        }

        void Use((Vector3, Vector3) e) { _edgeUse.TryGetValue(e, out int n); _edgeUse[e] = n + 1; }

        (int x, int y, int z) CellOf(Vector3 p) => ((int)Math.Floor(p.X / _cell), (int)Math.Floor(p.Y / _cell), (int)Math.Floor(p.Z / _cell));

        /// <summary>Every position on an open edge of the terrain union (call after all terrain triangles are added).</summary>
        public HashSet<Vector3> OpenPositions()
        {
            var r = new HashSet<Vector3>();
            foreach (var kv in _edgeUse)
                if (kv.Value == 1) { r.Add(kv.Key.Item1); r.Add(kv.Key.Item2); }
            return r;
        }

        static bool Touches(Vector3 p, Vector3 a, Vector3 b, Vector3 c) => p == a || p == b || p == c;

        /// <summary>
        /// Distance (m) from <paramref name="p"/> along +<paramref name="n"/> and -<paramref name="n"/> to the nearest triangle that
        /// does not have <paramref name="p"/> as a corner (min of both directions; +inf if nothing within <paramref name="max"/>).
        /// Hits closer than <paramref name="skin"/> are ignored (coplanar neighbours, the same sheet's float noise).
        /// </summary>
        public float AlongNormal(Vector3 p, Vector3 n, float max, float skin = 0.02f)
        {
            float best = float.PositiveInfinity;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                var d = n * sgn;
                float lim = Math.Min(max, best);
                Walk(p, d, lim, id =>
                {
                    var a = _a[id]; var b = _b[id]; var c = _c[id];
                    if (Touches(p, a, b, c)) return;
                    if (RayTri(p, d, a, b, c, out float t) && t > skin && t < best) best = t;
                });
            }
            return best;
        }

        /// <summary>Distance (m) from <paramref name="p"/> to the nearest placed-mesh (non-terrain) triangle within <paramref name="max"/>.</summary>
        public float NearestMesh(Vector3 p, float max)
        {
            float best = max * max;
            bool any = false;
            var lo = CellOf(p - new Vector3(max)); var hi = CellOf(p + new Vector3(max));
            var done = new HashSet<int>();
            for (int i = lo.x; i <= hi.x; i++)
            for (int j = lo.y; j <= hi.y; j++)
            for (int k = lo.z; k <= hi.z; k++)
            {
                if (!_grid.TryGetValue((i, j, k), out var l)) continue;
                foreach (int id in l)
                {
                    if (_terrain[id] || !done.Add(id)) continue;
                    float d2 = DistSq(p, _a[id], _b[id], _c[id]);
                    if (d2 < best) { best = d2; any = true; }
                }
            }
            return any ? (float)Math.Sqrt(best) : float.PositiveInfinity;
        }

        /// <summary>The clearance of a terrain position: min(along the normal to any other sheet / mesh, nearest placed mesh).</summary>
        public float At(Vector3 p, Vector3 n, float max) => Math.Min(AlongNormal(p, n, max), NearestMesh(p, max));

        // Amanatides-Woo walk over the grid cells of the segment p .. p + d * len
        void Walk(Vector3 p, Vector3 d, float len, Action<int> visit)
        {
            var c = CellOf(p);
            int x = c.x, y = c.y, z = c.z;
            int sx = d.X > 0 ? 1 : -1, sy = d.Y > 0 ? 1 : -1, sz = d.Z > 0 ? 1 : -1;
            float Next(float pc, float dc, int ic, int s) => Math.Abs(dc) < 1e-9f ? float.PositiveInfinity : (((ic + (s > 0 ? 1 : 0)) * _cell) - pc) / dc;
            float tx = Next(p.X, d.X, x, sx), ty = Next(p.Y, d.Y, y, sy), tz = Next(p.Z, d.Z, z, sz);
            float dx = Math.Abs(d.X) < 1e-9f ? float.PositiveInfinity : _cell / Math.Abs(d.X);
            float dy = Math.Abs(d.Y) < 1e-9f ? float.PositiveInfinity : _cell / Math.Abs(d.Y);
            float dz = Math.Abs(d.Z) < 1e-9f ? float.PositiveInfinity : _cell / Math.Abs(d.Z);
            float t = 0;
            for (int guard = 0; guard < 4096 && t <= len; guard++)
            {
                if (_grid.TryGetValue((x, y, z), out var l)) foreach (int id in l) visit(id);
                if (tx <= ty && tx <= tz) { t = tx; tx += dx; x += sx; }
                else if (ty <= tz) { t = ty; ty += dy; y += sy; }
                else { t = tz; tz += dz; z += sz; }
            }
        }

        // Moeller-Trumbore, both faces
        static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0;
            var e1 = b - a; var e2 = c - a;
            var pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (Math.Abs(det) < 1e-12f) return false;
            float inv = 1 / det;
            var tv = o - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0 || u > 1) return false;
            var qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, qv) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, qv) * inv;
            return t > 0;
        }

        // squared distance point - triangle (Ericson, Real-Time Collision Detection 5.1.5)
        public static float DistSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return ap.LengthSquared();
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return bp.LengthSquared();
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return (p - (a + v * ab)).LengthSquared(); }
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return cp.LengthSquared();
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return (p - (a + w * ac)).LengthSquared(); }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { float w = (d4 - d3) / (d4 - d3 + (d5 - d6)); return (p - (b + w * (c - b))).LengthSquared(); }
            float den = 1 / (va + vb + vc);
            float vv = vb * den, ww = vc * den;
            return (p - (a + ab * vv + ac * ww)).LengthSquared();
        }
    }
}
