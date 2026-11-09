using System;
using System.Collections.Generic;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The shortest way across one nav zone inside its polygon (x/z projection). A G3 zone is a walkable area, but many are
    /// not convex: Nordmar's valley below Xardas' tower (Nav_Zone df679bb8, 27 points) winds between cliffs, and the
    /// straight line from the tower's path end to the next path start leaves it through 60 m of overhanging rock
    /// (fix-35: Xardas walked that line on the ribbon into the mountain and the escort hero could never reach him). The
    /// way bends at the polygon's reflex corners, each moved <see cref="Inset"/> m into the zone, so a body passes the
    /// cliff foot instead of scraping it [interp: G3 NPCs collide with the cliff and slide round it; our straight walks do
    /// not collide]. Pure C#; a bend point's visibility is worked out once per zone.
    /// </summary>
    public sealed class NavZoneWay
    {
        /// <summary>How far a bend point lies inside the zone from its reflex corner (m); smaller where the zone is narrow.</summary>
        public static readonly float[] Insets = { 1.0f, 0.5f, 0.2f };
        public static float Inset => Insets[0];

        readonly float[] _p;
        readonly float[] _c; // bend points: x, y, z
        bool[] _see; // bend point visibility, k x k (rows filled lazily: _row)
        bool[] _row;

        public NavZoneWay(float[] polygon)
        {
            _p = polygon;
            int n = polygon.Length / 3;
            float area = 0;
            for (int i = 0, j = n - 1; i < n; j = i++) area += polygon[3 * j] * polygon[3 * i + 2] - polygon[3 * i] * polygon[3 * j + 2];
            var corners = new List<float>();
            if (n >= 4 && Math.Abs(area) > 1e-3f)
            {
                float sign = Math.Sign(area);
                for (int i = 0; i < n; i++)
                {
                    int a = (i + n - 1) % n, b = (i + 1) % n;
                    float px = X(a), pz = Z(a), vx = X(i), vz = Z(i), qx = X(b), qz = Z(b);
                    float cross = (vx - px) * (qz - vz) - (vz - pz) * (qx - vx);
                    if (cross * sign >= -1e-4f) continue; // convex or straight: no bend there
                    // inward normals of the two edges (the interior is left of a counter-clockwise edge)
                    Inward(px, pz, vx, vz, sign, out float n1x, out float n1z);
                    Inward(vx, vz, qx, qz, sign, out float n2x, out float n2z);
                    float dx = n1x + n2x, dz = n1z + n2z, len = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (len < 1e-4f) continue;
                    dx /= len;
                    dz /= len;
                    foreach (float d in Insets)
                    {
                        float cx = vx + dx * d, cz = vz + dz * d;
                        if (!Inside(cx, cz) || EdgeDistance(cx, cz) < d * 0.5f) continue;
                        corners.Add(cx);
                        corners.Add(_p[3 * i + 1]);
                        corners.Add(cz);
                        break;
                    }
                }
            }
            _c = corners.ToArray();
        }

        /// <summary>Bend points (reflex corners moved inside).</summary>
        public int CornerCount => _c.Length / 3;
        /// <summary>No reflex corner: every way across is straight.</summary>
        public bool Convex => _c.Length == 0;

        public void Corner(int i, out float x, out float y, out float z)
        {
            x = _c[3 * i];
            y = _c[3 * i + 1];
            z = _c[3 * i + 2];
        }

        /// <summary>
        /// The bend points (indexes for <see cref="Corner"/>) of the shortest way from a to b inside the zone; empty when the
        /// straight line stays inside (or no way round is found: the caller goes straight, as before). The ends may lie a
        /// little outside the polygon (path ends near its edge, a body pushed off it): crossings near them do not count.
        /// A Dijkstra search over the bend points from a that stops at b; a bend point's visibility of the others is
        /// worked out when a search first expands it and kept for the zone.
        /// </summary>
        public List<int> Way(float ax, float az, float bx, float bz, out float flatLength)
        {
            var way = new List<int>();
            flatLength = Flat(bx - ax, bz - az);
            float sa = Slack(ax, az), sb = Slack(bx, bz);
            if (Convex || Visible(ax, az, sa, bx, bz, sb)) return way;
            int k = CornerCount;
            if (_see == null) { _see = new bool[k * k]; _row = new bool[k]; }
            var dist = new float[k + 1]; // index k = b
            var prev = new int[k + 1];
            var done = new bool[k + 1];
            for (int i = 0; i < k; i++)
            {
                dist[i] = Visible(ax, az, sa, _c[3 * i], _c[3 * i + 2], 0.05f) ? Flat(_c[3 * i] - ax, _c[3 * i + 2] - az) : float.PositiveInfinity;
                prev[i] = -1;
            }
            dist[k] = float.PositiveInfinity;
            prev[k] = -1;
            while (true)
            {
                int u = -1;
                for (int i = 0; i <= k; i++) if (!done[i] && (u < 0 || dist[i] < dist[u])) u = i;
                if (u < 0 || float.IsPositiveInfinity(dist[u])) return way; // no way round found: straight
                if (u == k) break;
                done[u] = true;
                Row(u);
                float ux = _c[3 * u], uz = _c[3 * u + 2];
                for (int v = 0; v < k; v++)
                {
                    if (done[v] || !_see[u * k + v]) continue;
                    float c = dist[u] + Flat(_c[3 * v] - ux, _c[3 * v + 2] - uz);
                    if (c < dist[v]) { dist[v] = c; prev[v] = u; }
                }
                if (Visible(ux, uz, 0.05f, bx, bz, sb))
                {
                    float c = dist[u] + Flat(bx - ux, bz - uz);
                    if (c < dist[k]) { dist[k] = c; prev[k] = u; }
                }
            }
            for (int v = prev[k]; v >= 0; v = prev[v]) way.Add(v);
            way.Reverse();
            flatLength = dist[k];
            return way;
        }

        /// <summary>Bend point <paramref name="u"/>'s visibility of the others (symmetric; computed once per zone).</summary>
        void Row(int u)
        {
            if (_row[u]) return;
            int k = CornerCount;
            for (int v = 0; v < k; v++)
            {
                if (v == u || _row[v]) continue; // a finished row already holds (v, u)
                bool see = Visible(_c[3 * u], _c[3 * u + 2], 0.05f, _c[3 * v], _c[3 * v + 2], 0.05f);
                _see[u * k + v] = _see[v * k + u] = see;
            }
            _row[u] = true;
        }

        /// <summary>Crossings of the polygon's edge this close to an end do not count: inside, a hair; outside, its way in.</summary>
        float Slack(float x, float z) => Inside(x, z) ? 0.05f : EdgeDistance(x, z) + 0.5f;

        /// <summary>
        /// The segment stays inside the polygon: it crosses no edge (except within the slack of an end) and its middle is
        /// inside (or on the edge).
        /// </summary>
        bool Visible(float ax, float az, float slackA, float bx, float bz, float slackB)
        {
            float len = Flat(bx - ax, bz - az);
            if (len < 1e-4f) return true;
            int n = _p.Length / 3;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float px = X(j), pz = Z(j), qx = X(i), qz = Z(i);
                float d1 = Orient(ax, az, bx, bz, px, pz), d2 = Orient(ax, az, bx, bz, qx, qz);
                if (d1 * d2 >= 0) continue;
                float d3 = Orient(px, pz, qx, qz, ax, az), d4 = Orient(px, pz, qx, qz, bx, bz);
                if (d3 * d4 >= 0) continue;
                float t = d3 / (d3 - d4); // along a -> b
                if (t * len < slackA || (1 - t) * len < slackB) continue;
                return false;
            }
            float mx = (ax + bx) * 0.5f, mz = (az + bz) * 0.5f;
            return Inside(mx, mz) || EdgeDistance(mx, mz) < 0.5f;
        }

        static float Orient(float ax, float az, float bx, float bz, float cx, float cz) => (bx - ax) * (cz - az) - (bz - az) * (cx - ax);

        static void Inward(float ax, float az, float bx, float bz, float sign, out float nx, out float nz)
        {
            float dx = bx - ax, dz = bz - az, len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6f) { nx = nz = 0; return; }
            nx = -dz / len * sign;
            nz = dx / len * sign;
        }

        float X(int i) => _p[3 * i];
        float Z(int i) => _p[3 * i + 2];

        bool Inside(float x, float z)
        {
            bool inside = false;
            int n = _p.Length / 3;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = X(i), zi = Z(i), xj = X(j), zj = Z(j);
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        float EdgeDistance(float x, float z)
        {
            int n = _p.Length / 3;
            float best = float.MaxValue;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float ax = X(j), az = Z(j), dx = X(i) - ax, dz = Z(i) - az, len2 = dx * dx + dz * dz;
                float t = len2 > 1e-8f ? Math.Max(0f, Math.Min(1f, ((x - ax) * dx + (z - az) * dz) / len2)) : 0f;
                best = Math.Min(best, Flat(ax + t * dx - x, az + t * dz - z));
            }
            return best;
        }

        static float Flat(float dx, float dz) => (float)Math.Sqrt(dx * dx + dz * dz);
    }
}
