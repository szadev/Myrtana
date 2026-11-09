using System;
using System.Collections.Generic;
using System.Numerics;

namespace G3.Unity
{
    /// <summary>XZ buckets of a <see cref="ReliefMesh"/>'s source (collision) triangles, for ground queries.</summary>
    public sealed class ReliefTriGrid
    {
        public float X0, Z0, Cell;
        public int Nx, Nz;
        public int[] Start;   // Nx * Nz + 1 offsets into Tris
        public int[] Tris;

        /// <summary>Buckets every triangle by its XZ bounds (counting sort; <paramref name="cell"/> m buckets).</summary>
        public static ReliefTriGrid Build(ReliefMesh m, float cell = 4f)
        {
            var g = new ReliefTriGrid { Cell = cell, X0 = m.BoundsMin.X, Z0 = m.BoundsMin.Z };
            g.Nx = Math.Max(1, (int)Math.Ceiling((m.BoundsMax.X - m.BoundsMin.X) / cell) + 1);
            g.Nz = Math.Max(1, (int)Math.Ceiling((m.BoundsMax.Z - m.BoundsMin.Z) / cell) + 1);
            var count = new int[g.Nx * g.Nz + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int t = 0; t < m.TriCount; t++)
                {
                    g.Range(m, t, out int i0, out int i1, out int j0, out int j1);
                    for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        int k = j * g.Nx + i;
                        if (pass == 0) count[k + 1]++;
                        else g.Tris[g.Start[k] + count[k]++] = t;
                    }
                }
                if (pass == 0)
                {
                    for (int k = 1; k < count.Length; k++) count[k] += count[k - 1];
                    g.Start = count;
                    g.Tris = new int[count[count.Length - 1]];
                    count = new int[g.Nx * g.Nz];
                }
            }
            return g;
        }

        void Range(ReliefMesh m, int t, out int i0, out int i1, out int j0, out int j1)
        {
            Vector3 a = m.WPos[m.Weld[m.TriV[t * 3]]], b = m.WPos[m.Weld[m.TriV[t * 3 + 1]]], c = m.WPos[m.Weld[m.TriV[t * 3 + 2]]];
            i0 = Clamp((int)Math.Floor((Math.Min(a.X, Math.Min(b.X, c.X)) - X0) / Cell), Nx);
            i1 = Clamp((int)Math.Floor((Math.Max(a.X, Math.Max(b.X, c.X)) - X0) / Cell), Nx);
            j0 = Clamp((int)Math.Floor((Math.Min(a.Z, Math.Min(b.Z, c.Z)) - Z0) / Cell), Nz);
            j1 = Clamp((int)Math.Floor((Math.Max(a.Z, Math.Max(b.Z, c.Z)) - Z0) / Cell), Nz);
        }

        static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

        /// <summary>Bucket of a point (false outside the grid).</summary>
        public bool Bucket(float x, float z, out int start, out int end)
        {
            int i = (int)Math.Floor((x - X0) / Cell), j = (int)Math.Floor((z - Z0) / Cell);
            if (i < 0 || j < 0 || i >= Nx || j >= Nz) { start = end = 0; return false; }
            int k = j * Nx + i;
            start = Start[k]; end = Start[k + 1];
            return true;
        }
    }

    /// <summary>
    /// Ground queries on the relief (Docs/remaster-world.md section 14): where the collision (G3's triangle) and the visual
    /// relief surface are at a point, so characters, corpses, items and grass can stand on what is drawn while physics keeps
    /// G3's ground. Engine-free; <see cref="ReliefGroundMap"/> is the rasterised form round the camera.
    /// </summary>
    public static class ReliefGround
    {
        /// <summary>Faces flatter than this (normal.y) carry things; walls are skipped.</summary>
        public const float MinUp = 0.25f;

        /// <summary>
        /// The collision triangle under (<paramref name="x"/>, <paramref name="z"/>) nearest in height to <paramref name="yHint"/>
        /// (within <paramref name="maxDy"/>), with the XZ barycentrics of the point and the triangle's height there.
        /// </summary>
        public static bool CollisionAt(ReliefMesh m, float x, float z, float yHint, float maxDy, out int tri, out Vector3 bary, out float y)
        {
            tri = -1; bary = default; y = 0;
            var g = m.Grid;
            if (g == null || !g.Bucket(x, z, out int st, out int en)) return false;
            float best = maxDy;
            for (int k = st; k < en; k++)
            {
                int t = g.Tris[k];
                if (!Bary(m, t, x, z, out var b, out float ty, out float ny) || ny < MinUp) continue;
                float d = Math.Abs(ty - yHint);
                if (d <= best) { best = d; tri = t; bary = b; y = ty; }
            }
            return tri >= 0;
        }

        /// <summary>XZ barycentrics of a point in source triangle <paramref name="t"/> (true when inside, small tolerance).</summary>
        public static bool Bary(ReliefMesh m, int t, float x, float z, out Vector3 b, out float y, out float ny)
        {
            Vector3 a = m.WPos[m.Weld[m.TriV[t * 3]]], p1 = m.WPos[m.Weld[m.TriV[t * 3 + 1]]], p2 = m.WPos[m.Weld[m.TriV[t * 3 + 2]]];
            float d = (p1.Z - p2.Z) * (a.X - p2.X) + (p2.X - p1.X) * (a.Z - p2.Z);
            b = default; y = 0;
            var fn = Vector3.Cross(p1 - a, p2 - a);
            float fl = fn.Length();
            ny = fl > 1e-9f ? fn.Y / fl : 0;
            if (Math.Abs(d) < 1e-9f) return false;
            float b0 = ((p1.Z - p2.Z) * (x - p2.X) + (p2.X - p1.X) * (z - p2.Z)) / d;
            float b1 = ((p2.Z - a.Z) * (x - p2.X) + (a.X - p2.X) * (z - p2.Z)) / d;
            float b2 = 1 - b0 - b1;
            const float e = -1e-4f;
            if (b0 < e || b1 < e || b2 < e) return false;
            b = new Vector3(b0, b1, b2);
            y = b0 * a.Y + b1 * p1.Y + b2 * p2.Y;
            return true;
        }

        /// <summary>
        /// Collision and visual height at (<paramref name="x"/>, <paramref name="z"/>) on ground near <paramref name="yHint"/>:
        /// the relief surface point is laterally displaced (Phong, detail along the normal), so the query point is moved
        /// against that displacement a few times until the surface point lies above (x, z). <paramref name="nearTris"/>
        /// (may be null) marks the triangles drawn by the near part, whose detail band follows <paramref name="points"/>.
        /// </summary>
        public static bool SurfaceAt(ReliefMesh m, float x, float z, float yHint, float maxDy, ReliefPoint[] points, int count, bool[] nearTris,
                                     ReliefSettings s, out float collisionY, out float visualY)
        {
            visualY = collisionY = 0;
            if (!CollisionAt(m, x, z, yHint, maxDy, out int t, out var b, out collisionY)) return false;
            float qx = x, qz = z, hint = collisionY;
            visualY = collisionY;
            for (int it = 0; it < 4; it++)
            {
                if (it > 0 && !Bary(m, t, qx, qz, out b, out _, out _))
                {
                    if (!CollisionAt(m, qx, qz, hint, maxDy, out t, out b, out _)) break;
                }
                var flat = Flat(m, t, b);
                float sp = nearTris != null && t < nearTris.Length && nearTris[t] && count > 0
                    ? TerrainRelief.SpacingAt(flat, points, count, s.farSpacing, true) : s.farSpacing;
                var v = TerrainRelief.SurfacePoint(m, t, b.X, b.Y, b.Z, sp, s);
                visualY = v.Y;
                float ex = v.X - x, ez = v.Z - z;
                if (ex * ex + ez * ez < 1e-4f) break;   // 1 cm
                qx -= ex; qz -= ez;
            }
            return true;
        }

        static Vector3 Flat(ReliefMesh m, int t, Vector3 b)
            => b.X * m.WPos[m.Weld[m.TriV[t * 3]]] + b.Y * m.WPos[m.Weld[m.TriV[t * 3 + 1]]] + b.Z * m.WPos[m.Weld[m.TriV[t * 3 + 2]]];
    }

    /// <summary>
    /// The ground round a point as two height grids (Docs/remaster-world.md section 14): <see cref="Col"/> = G3's collision
    /// (the highest carrying face), <see cref="Vis"/> = the drawn relief mesh (its triangles rasterised, the one nearest the
    /// collision), on grid points X0 + i * Texel. Exact for the mesh as drawn (linear between its vertices); NaN = no ground.
    /// Built on the relief worker after each near batch; read by characters (CPU) and grass (GPU, <see cref="Delta"/>).
    /// snow3: a second layer, <see cref="Low"/> / <see cref="VisLow"/> = the lowest carrying face and its drawn ground, so a
    /// query with a height (<see cref="Sample(float, float, float, out float, out float)"/>) under an overhang, a ledge or a
    /// terrain lip finds the ground it stands on (build 20c: the hero's footing read a face 6-32 m above him, "ik False").
    /// </summary>
    public sealed class ReliefGroundMap
    {
        public readonly int N;
        public readonly float Texel;
        public float X0, Z0;
        public readonly float[] Col, Vis, Low, VisLow;
        /// <summary>fx-1: loose-sand share of the drawn ground per point (0..1: the layers' sand class x the vertex blend x
        /// flatness), taken from the drawn triangle chosen for <see cref="Vis"/>; the sand streamers spawn only where it is high.</summary>
        public readonly float[] Sand;
        public bool Ready;

        public ReliefGroundMap(int n, float texel)
        {
            N = n; Texel = texel;
            Col = new float[n * n]; Vis = new float[n * n]; Low = new float[n * n]; VisLow = new float[n * n];
            Sand = new float[n * n];
        }

        public float Size => (N - 1) * Texel;

        /// <summary>Centres the grid on (<paramref name="cx"/>, <paramref name="cz"/>) (snapped to the texel) and clears it.</summary>
        public void Begin(float cx, float cz)
        {
            float h = Size * 0.5f;
            X0 = (float)Math.Round((cx - h) / Texel) * Texel; Z0 = (float)Math.Round((cz - h) / Texel) * Texel;
            for (int i = 0; i < Col.Length; i++) { Col[i] = float.NaN; Vis[i] = float.NaN; Low[i] = float.NaN; VisLow[i] = float.NaN; Sand[i] = 0; }
            Ready = false;
        }

        public bool Overlaps(Vector3 min, Vector3 max) => max.X >= X0 && max.Z >= Z0 && min.X <= X0 + Size && min.Z <= Z0 + Size;

        /// <summary>A collision triangle (Unity winding: front = cross(b - a, c - a)); keeps the highest carrying face per point.</summary>
        public void Collision(Vector3 a, Vector3 b, Vector3 c) => Raster(a, b, c, true, 0, 0, 0);

        /// <summary>A drawn triangle; per point keeps the one nearest the collision (run after every <see cref="Collision"/>).</summary>
        public void Visual(Vector3 a, Vector3 b, Vector3 c) => Raster(a, b, c, false, 0, 0, 0);

        /// <summary>fx-1: a drawn triangle with the loose-sand share of its corners (<see cref="Sand"/>).</summary>
        public void Visual(Vector3 a, Vector3 b, Vector3 c, float sa, float sb, float sc) => Raster(a, b, c, false, sa, sb, sc);

        /// <summary>fx-1: streamers keep to ground flatter than ~25-35 deg (lee faces near the angle of repose take a few, cliffs none).</summary>
        public static float SandFlatness(float ny) => ny <= 0.80f ? 0 : ny >= 0.92f ? 1 : Smooth((ny - 0.80f) / 0.12f);

        static float Smooth(float t) => t * t * (3 - 2 * t);

        void Raster(Vector3 a, Vector3 b, Vector3 c, bool col, float sa, float sb, float sc)
        {
            var fn = Vector3.Cross(b - a, c - a);
            float fl = fn.Length();
            if (fl < 1e-9f || fn.Y / fl < ReliefGround.MinUp) return;
            bool sand = !col && (sa > 0 || sb > 0 || sc > 0);
            float flat = sand ? SandFlatness(fn.Y / fl) : 0;
            float minX = Math.Min(a.X, Math.Min(b.X, c.X)), maxX = Math.Max(a.X, Math.Max(b.X, c.X));
            float minZ = Math.Min(a.Z, Math.Min(b.Z, c.Z)), maxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z));
            int i0 = Math.Max(0, (int)Math.Ceiling((minX - X0) / Texel)), i1 = Math.Min(N - 1, (int)Math.Floor((maxX - X0) / Texel));
            int j0 = Math.Max(0, (int)Math.Ceiling((minZ - Z0) / Texel)), j1 = Math.Min(N - 1, (int)Math.Floor((maxZ - Z0) / Texel));
            if (i0 > i1 || j0 > j1) return;
            float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Math.Abs(d) < 1e-12f) return;
            float inv = 1f / d;
            const float e = -1e-5f;
            for (int j = j0; j <= j1; j++)
            {
                float z = Z0 + j * Texel;
                for (int i = i0; i <= i1; i++)
                {
                    float x = X0 + i * Texel;
                    float b0 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) * inv;
                    if (b0 < e) continue;
                    float b1 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) * inv;
                    if (b1 < e) continue;
                    float b2 = 1 - b0 - b1;
                    if (b2 < e) continue;
                    float y = b0 * a.Y + b1 * b.Y + b2 * c.Y;
                    int k = j * N + i;
                    if (col)
                    {
                        if (float.IsNaN(Col[k]) || y > Col[k]) Col[k] = y;
                        if (float.IsNaN(Low[k]) || y < Low[k]) Low[k] = y;
                    }
                    else
                    {
                        float g = Col[k];
                        if (float.IsNaN(g)) continue;
                        if (float.IsNaN(Vis[k]) || Math.Abs(y - g) < Math.Abs(Vis[k] - g))
                        {
                            Vis[k] = y;
                            Sand[k] = sand ? (b0 * sa + b1 * sb + b2 * sc) * flat : 0;
                        }
                        float gl = Low[k];
                        if (float.IsNaN(VisLow[k]) || Math.Abs(y - gl) < Math.Abs(VisLow[k] - gl)) VisLow[k] = y;
                    }
                }
            }
        }

        /// <summary>Bilinear collision and visual height at (x, z); a missing corner falls back to the nearest valid one.</summary>
        public bool Sample(float x, float z, out float col, out float vis)
        {
            col = vis = 0;
            if (!Ready) return false;
            float fx = (x - X0) / Texel, fz = (z - Z0) / Texel;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
            if (i < 0 || j < 0 || i >= N - 1 || j >= N - 1) return false;
            float u = fx - i, v = fz - j;
            int k = j * N + i;
            float c00 = Col[k], c10 = Col[k + 1], c01 = Col[k + N], c11 = Col[k + N + 1];
            float v00 = Vis[k], v10 = Vis[k + 1], v01 = Vis[k + N], v11 = Vis[k + N + 1];
            if (Valid(c00, v00) && Valid(c10, v10) && Valid(c01, v01) && Valid(c11, v11))
            {
                col = Bilerp(c00, c10, c01, c11, u, v);
                vis = Bilerp(v00, v10, v01, v11, u, v);
                return true;
            }
            // nearest valid corner
            float best = float.MaxValue; bool any = false;
            float rc = 0, rv = 0;
            for (int q = 0; q < 4; q++)
            {
                float c = q == 0 ? c00 : q == 1 ? c10 : q == 2 ? c01 : c11, w = q == 0 ? v00 : q == 1 ? v10 : q == 2 ? v01 : v11;
                if (!Valid(c, w)) continue;
                float du = (q & 1) == 0 ? u : 1 - u, dv = q < 2 ? v : 1 - v;
                float dd = du * du + dv * dv;
                if (dd < best) { best = dd; rc = c; rv = w; any = true; }
            }
            col = rc; vis = rv;
            return any;
        }

        /// <summary>
        /// As <see cref="Sample(float, float, out float, out float)"/>, but per grid point the layer (highest or lowest carrying
        /// face) nearer <paramref name="y"/>: the ground a thing at that height stands on, also under an overhang.
        /// </summary>
        public bool Sample(float x, float y, float z, out float col, out float vis)
        {
            col = vis = 0;
            if (!Ready) return false;
            float fx = (x - X0) / Texel, fz = (z - Z0) / Texel;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
            if (i < 0 || j < 0 || i >= N - 1 || j >= N - 1) return false;
            float u = fx - i, v = fz - j;
            int k = j * N + i;
            Layer(k, y, out float c00, out float v00); Layer(k + 1, y, out float c10, out float v10);
            Layer(k + N, y, out float c01, out float v01); Layer(k + N + 1, y, out float c11, out float v11);
            if (Valid(c00, v00) && Valid(c10, v10) && Valid(c01, v01) && Valid(c11, v11))
            {
                col = Bilerp(c00, c10, c01, c11, u, v);
                vis = Bilerp(v00, v10, v01, v11, u, v);
                return true;
            }
            float best = float.MaxValue; bool any = false;
            float rc = 0, rv = 0;
            for (int q = 0; q < 4; q++)
            {
                float c = q == 0 ? c00 : q == 1 ? c10 : q == 2 ? c01 : c11, w = q == 0 ? v00 : q == 1 ? v10 : q == 2 ? v01 : v11;
                if (!Valid(c, w)) continue;
                float du = (q & 1) == 0 ? u : 1 - u, dv = q < 2 ? v : 1 - v;
                float dd = du * du + dv * dv;
                if (dd < best) { best = dd; rc = c; rv = w; any = true; }
            }
            col = rc; vis = rv;
            return any;
        }

        void Layer(int k, float y, out float c, out float v)
        {
            c = Col[k]; v = Vis[k];
            float cl = Low[k];
            if (!float.IsNaN(cl) && !float.IsNaN(c) && Math.Abs(y - cl) < Math.Abs(y - c)) { c = cl; v = VisLow[k]; }
        }

        static bool Valid(float c, float v) => !float.IsNaN(c) && !float.IsNaN(v);
        static float Bilerp(float a, float b, float c, float d, float u, float v) => (a + (b - a) * u) + ((c + (d - c) * u) - (a + (b - a) * u)) * v;

        /// <summary>
        /// The GPU texture (RG16 float, interleaved): R = <see cref="Delta"/>, G = collision height minus
        /// <paramref name="baseY"/> (shaders tell ground things from things on rocks or roofs); a missing collision = -1000.
        /// </summary>
        public void DeltaHalf(ushort[] into, float baseY, float limit = 2f)
        {
            for (int k = 0; k < Col.Length; k++)
            {
                float c = Col[k], v = Vis[k];
                float d = float.IsNaN(c) || float.IsNaN(v) ? 0 : v - c;
                into[2 * k] = Half(d < -limit ? -limit : d > limit ? limit : d);
                into[2 * k + 1] = Half(float.IsNaN(c) ? -1000f : c - baseY);
            }
        }

        /// <summary>
        /// fx-1: loose-sand share of the terrain at (x, z) for a thing standing at height <paramref name="y"/>: 0 when it does
        /// not stand on the terrain's top collision there (more than <paramref name="tol"/> m off: a carpet, a floor, a ledge, a
        /// bridge, an interior) or off the map; else the nearest point's <see cref="Sand"/>. -1 when the map is not ready.
        /// </summary>
        public float SandAt(float x, float y, float z, float tol)
        {
            if (!Ready) return -1;
            int i = (int)Math.Round((x - X0) / Texel), j = (int)Math.Round((z - Z0) / Texel);
            if (i < 0 || j < 0 || i >= N || j >= N) return -1;
            int k = j * N + i;
            float c = Col[k];
            if (float.IsNaN(c) || Math.Abs(y - c) > tol) return 0;
            return Sand[k];
        }

        /// <summary>fx-1: <see cref="Sand"/> as bytes (0..255) for the GPU mask.</summary>
        public void SandBytes(byte[] into)
        {
            for (int k = 0; k < Sand.Length && k < into.Length; k++)
            {
                float v = Sand[k];
                into[k] = (byte)(v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255f + 0.5f));
            }
        }

        /// <summary>A reference height for <see cref="DeltaHalf"/>: the collision at the centre (else the mean), so heights stay small.</summary>
        public float BaseHeight()
        {
            int mid = (N / 2) * N + N / 2;
            if (!float.IsNaN(Col[mid])) return Col[mid];
            double sum = 0; int n = 0;
            for (int k = 0; k < Col.Length; k += 97) if (!float.IsNaN(Col[k])) { sum += Col[k]; n++; }
            return n > 0 ? (float)(sum / n) : 0f;
        }

        /// <summary>Float to IEEE half (round to nearest; small values flush to 0; |v| here is at most a few m).</summary>
        public static ushort Half(float v)
        {
            uint b = (uint)BitConverter.SingleToInt32Bits(v);
            uint sign = (b >> 16) & 0x8000u;
            int exp = (int)((b >> 23) & 0xFF) - 127 + 15;
            uint man = b & 0x7FFFFFu;
            if (exp <= 0) return (ushort)sign;
            if (exp >= 31) return (ushort)(sign | 0x7BFFu);
            uint h = sign | ((uint)exp << 10) | (man >> 13);
            if ((man & 0x1000u) != 0) h++;   // round
            return (ushort)h;
        }

        /// <summary>Visual minus collision height per grid point (0 where either is missing), clamped to ±<paramref name="limit"/> m.</summary>
        public void Delta(float[] into, float limit = 2f)
        {
            for (int k = 0; k < Col.Length; k++)
            {
                float c = Col[k], v = Vis[k];
                float d = float.IsNaN(c) || float.IsNaN(v) ? 0 : v - c;
                into[k] = d < -limit ? -limit : d > limit ? limit : d;
            }
        }
    }

    /// <summary>Fills a <see cref="ReliefGroundMap"/> from prepared cells: their collision triangles, then their drawn ones.</summary>
    public static class ReliefMapRaster
    {
        /// <summary>The source (collision) triangles of <paramref name="m"/>.</summary>
        public static void Collision(ReliefGroundMap map, ReliefMesh m)
        {
            if (m == null || !m.Prepared || !map.Overlaps(m.BoundsMin, m.BoundsMax)) return;
            for (int t = 0; t < m.TriCount; t++)
                map.Collision(m.WPos[m.Weld[m.TriV[t * 3]]], m.WPos[m.Weld[m.TriV[t * 3 + 1]]], m.WPos[m.Weld[m.TriV[t * 3 + 2]]]);
        }

        /// <summary>The drawn triangles: the far ones not replaced by the near part, then the near part (as <see cref="TerrainRelief.Combine"/>).</summary>
        public static void Visual(ReliefGroundMap map, ReliefMesh m, ReliefOutput far, ReliefOutput near)
        {
            if (m == null || far == null || far.TriStart == null || !map.Overlaps(m.BoundsMin - new Vector3(4), m.BoundsMax + new Vector3(4))) return;
            bool[] skip = near != null && near.NearTriangles > 0 ? near.Near : null;
            var fv = far.Vertices;
            for (int t = 0; t < m.TriCount; t++)
            {
                if (skip != null && skip[t]) continue;
                int sub = m.TriSub[t];
                var l = far.Indices[sub];
                int a = far.TriStart[t], e = a + far.TriCount[t];
                if (HasSand(m, sub, out float la, out float lb))
                    for (int k = a; k < e; k += 3)
                        map.Visual(fv[l[k]].Position, fv[l[k + 1]].Position, fv[l[k + 2]].Position,
                                   Loose(la, lb, fv[l[k]].Color), Loose(la, lb, fv[l[k + 1]].Color), Loose(la, lb, fv[l[k + 2]].Color));
                else
                    for (int k = a; k < e; k += 3) map.Visual(fv[l[k]].Position, fv[l[k + 1]].Position, fv[l[k + 2]].Position);
            }
            if (skip == null) return;
            var nv = near.Vertices;
            for (int s = 0; s < near.Indices.Length; s++)
            {
                var l = near.Indices[s];
                if (HasSand(m, s, out float la, out float lb))
                    for (int k = 0; k + 2 < l.Count; k += 3)
                        map.Visual(nv[l[k]].Position, nv[l[k + 1]].Position, nv[l[k + 2]].Position,
                                   Loose(la, lb, nv[l[k]].Color), Loose(la, lb, nv[l[k + 1]].Color), Loose(la, lb, nv[l[k + 2]].Color));
                else
                    for (int k = 0; k + 2 < l.Count; k += 3) map.Visual(nv[l[k]].Position, nv[l[k + 1]].Position, nv[l[k + 2]].Position);
            }
        }

        /// <summary>fx-1: the loose-sand share of submesh <paramref name="sub"/>'s layers A and B (false: no sand layer).</summary>
        static bool HasSand(ReliefMesh m, int sub, out float la, out float lb)
        {
            la = lb = 0;
            if (m.SubLooseA == null || m.SubLooseB == null || sub < 0 || sub >= m.SubLooseA.Length || sub >= m.SubLooseB.Length) return false;
            la = m.SubLooseA[sub]; lb = m.SubLooseB[sub];
            return la > 0 || lb > 0;
        }

        /// <summary>
        /// fx-1: loose-sand share at a vertex. Layer A's weight is 1 - vertex alpha (G3's lerp(A, B, VC.a), as the WorldV2
        /// shader; a swap is folded into A/B by the caller). <paramref name="color"/> = RGBA bytes, A in the high byte.
        /// </summary>
        public static float Loose(float la, float lb, uint color)
        {
            float wA = 1f - (color >> 24) / 255f;
            return lb + (la - lb) * wA;
        }
    }
}
