using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// Which side of a two-sided collision surface the NPC NavMesh may walk on (fix-99). G3 collision blocks from both
    /// sides, so the colliders are built two-sided (G3CollisionBuilder, CollisionRules.TwoSided: each triangle followed
    /// by its reversed copy). A NavMesh baked from them is walkable on the back of every surface too: on top of a cave
    /// ceiling, inside the rock under the hill (Reddock cave: a varan walked up into the ceiling and the fall rule put
    /// it back there). The render mesh of the same object shows the real side: a near-horizontal collision triangle that
    /// lies on a render triangle keeps only the copy facing like the render triangle (a ceiling seen from below stays a
    /// ceiling, a floor seen from above stays a floor); steep triangles keep one copy (never walkable either way);
    /// triangles without a render match stay two-sided (as before).
    /// </summary>
    public static class NavFacingRules
    {
        /// <summary>A render triangle this close to the collision triangle's centre (m, vertically) is the same surface.</summary>
        public const float MatchTolerance = 0.15f;
        /// <summary>|normal.y| below this: a wall (NavMesh never walks it: the walkable slope is 49°).</summary>
        public const float SteepNormalY = 0.5f;
        const float GridCell = 2f;

        public struct Result
        {
            public int[] Triangles;
            public int Pairs, Steep, KeptUp, KeptDown, Unmatched;
        }

        /// <param name="cp">Collision vertex positions (xyz, metres, collider space).</param>
        /// <param name="ct">Two-sided triangle list (pairs: a b c, then a c b).</param>
        /// <param name="rp">Render vertex positions in the same space.</param>
        /// <param name="rt">Render triangle list (Unity winding: front = cross(v1 - v0, v2 - v0)).</param>
        public static Result OneSided(float[] cp, IReadOnlyList<int> ct, float[] rp, IReadOnlyList<int> rt)
        {
            var res = new Result();
            var grid = BuildGrid(rp, rt);
            var o = new List<int>(ct.Count);
            for (int t = 0; t + 5 < ct.Count; t += 6)
            {
                int a = ct[t], b = ct[t + 1], c = ct[t + 2];
                bool pair = ct[t + 3] == a && ct[t + 4] == c && ct[t + 5] == b;
                if (!pair)
                {
                    // not a two-sided pair: keep as it is, one triangle at a time
                    o.Add(a); o.Add(b); o.Add(c);
                    t -= 3;
                    continue;
                }
                res.Pairs++;
                Normal(cp, a, b, c, out float nx, out float ny, out float nz);
                if (Math.Abs(ny) < SteepNormalY)
                {
                    res.Steep++;
                    o.Add(a); o.Add(b); o.Add(c);
                    continue;
                }
                float x = (cp[a * 3] + cp[b * 3] + cp[c * 3]) / 3, y = (cp[a * 3 + 1] + cp[b * 3 + 1] + cp[c * 3 + 1]) / 3,
                      z = (cp[a * 3 + 2] + cp[b * 3 + 2] + cp[c * 3 + 2]) / 3;
                int facing = RenderFacing(grid, rp, rt, x, y, z);
                if (facing == 0)
                {
                    res.Unmatched++;
                    o.Add(a); o.Add(b); o.Add(c);
                    o.Add(a); o.Add(c); o.Add(b);
                    continue;
                }
                bool keepFirst = (ny > 0) == (facing > 0);
                if (facing > 0) res.KeptUp++; else res.KeptDown++;
                if (keepFirst) { o.Add(a); o.Add(b); o.Add(c); }
                else { o.Add(a); o.Add(c); o.Add(b); }
            }
            res.Triangles = o.ToArray();
            return res;
        }

        static void Normal(float[] p, int a, int b, int c, out float nx, out float ny, out float nz)
        {
            float ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
            float vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
            nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx;
            float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l > 1e-12f) { nx /= l; ny /= l; nz /= l; }
        }

        static Dictionary<(int, int), List<int>> BuildGrid(float[] p, IReadOnlyList<int> t)
        {
            var g = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                float minX = Math.Min(p[a * 3], Math.Min(p[b * 3], p[c * 3])), maxX = Math.Max(p[a * 3], Math.Max(p[b * 3], p[c * 3]));
                float minZ = Math.Min(p[a * 3 + 2], Math.Min(p[b * 3 + 2], p[c * 3 + 2])), maxZ = Math.Max(p[a * 3 + 2], Math.Max(p[b * 3 + 2], p[c * 3 + 2]));
                int x0 = (int)Math.Floor(minX / GridCell), x1 = (int)Math.Floor(maxX / GridCell);
                int z0 = (int)Math.Floor(minZ / GridCell), z1 = (int)Math.Floor(maxZ / GridCell);
                if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 4096) continue;
                for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!g.TryGetValue((x, z), out var l)) g[(x, z)] = l = new List<int>();
                    l.Add(i);
                }
            }
            return g;
        }

        /// <summary>+1 / -1: the render surface at (x, y, z) faces up / down; 0: none there (or both).</summary>
        static int RenderFacing(Dictionary<(int, int), List<int>> g, float[] p, IReadOnlyList<int> t, float x, float y, float z)
        {
            if (!g.TryGetValue(((int)Math.Floor(x / GridCell), (int)Math.Floor(z / GridCell)), out var list)) return 0;
            int up = 0, down = 0;
            float best = MatchTolerance;
            foreach (int i in list)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                float ax = p[a * 3], az = p[a * 3 + 2], bx = p[b * 3], bz = p[b * 3 + 2], cx = p[c * 3], cz = p[c * 3 + 2];
                float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                if (Math.Abs(d) < 1e-9f) continue;
                float l1 = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d;
                float l2 = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d;
                float l3 = 1 - l1 - l2;
                const float e = -1e-3f;
                if (l1 < e || l2 < e || l3 < e) continue;
                float h = l1 * p[a * 3 + 1] + l2 * p[b * 3 + 1] + l3 * p[c * 3 + 1];
                float dy = Math.Abs(h - y);
                if (dy > best) continue;
                Normal(p, a, b, c, out _, out float ny, out _);
                if (Math.Abs(ny) < 0.3f) continue;
                if (dy < best - 0.02f) { up = 0; down = 0; best = dy + 0.02f; } // a clearly closer surface wins
                if (ny > 0) up++; else down++;
            }
            return up > 0 && down == 0 ? 1 : down > 0 && up == 0 ? -1 : 0;
        }
    }
}
