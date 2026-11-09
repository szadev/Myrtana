using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// The landscape for the scatter (Docs/remaster-world.md section 18.2): G3's landscape cells (collision = G3's own
    /// triangles, world space) bucketed on an XZ grid; <see cref="Sample"/> returns the top triangle under a point with its
    /// height, face normal (always up-facing), the submesh's two layer classes and G3's vertex-alpha blend. G3's cells overlap
    /// (the same triangles in two cells): the top hit is the same either way, so the density does not double. Not thread-safe:
    /// owned by the scatter's worker thread.
    /// </summary>
    public sealed class ScatterTerrainIndex : IScatterGround
    {
        public const float Cell = 8f;

        sealed class Part
        {
            public float[] P;      // xyz per vertex
            public float[] A;      // vertex alpha (G3's layer blend) per vertex, null = 0
            public int[] T;        // 3 indices per triangle
            public byte[] C;       // per triangle: class A | class B << 4
            public List<long> Keys = new List<long>();
            public float MinX, MaxX, MinZ, MaxZ;
        }

        readonly Dictionary<int, Part> _parts = new Dictionary<int, Part>();
        readonly Dictionary<long, List<long>> _grid = new Dictionary<long, List<long>>();   // bucket -> (part << 32 | tri)

        public int PartCount => _parts.Count;
        public long TriangleCount { get; private set; }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        public bool Contains(int id) => _parts.ContainsKey(id);

        /// <summary>Approximate managed memory of the index (bytes): vertex/triangle copies + bucket entries.</summary>
        public long MemoryBytes
        {
            get
            {
                long b = 0;
                foreach (var p in _parts.Values) b += p.P.Length * 4L + (p.A?.Length ?? 0) * 4L + p.T.Length * 4L + p.C.Length + p.Keys.Count * 8L;
                foreach (var l in _grid.Values) b += l.Count * 8L + 32;
                return b;
            }
        }

        /// <summary>Adds one cell. <paramref name="subTriangles"/> per submesh, <paramref name="subClasses"/> its layer classes.</summary>
        public void Add(int id, float[] positions, float[] alpha, int[][] subTriangles, (GroundClass a, GroundClass b)[] subClasses)
        {
            if (_parts.ContainsKey(id)) Remove(id);
            int triCount = 0;
            foreach (var s in subTriangles) triCount += s != null ? s.Length / 3 : 0;
            var p = new Part { P = positions, A = alpha, T = new int[triCount * 3], C = new byte[triCount],
                               MinX = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxZ = float.MinValue };
            int t = 0;
            for (int s = 0; s < subTriangles.Length; s++)
            {
                var tris = subTriangles[s];
                if (tris == null) continue;
                var (ca, cb) = s < subClasses.Length ? subClasses[s] : (GroundClass.None, GroundClass.None);
                byte cls = (byte)((int)ca | ((int)cb << 4));
                for (int k = 0; k + 2 < tris.Length; k += 3, t++)
                {
                    p.T[t * 3] = tris[k]; p.T[t * 3 + 1] = tris[k + 1]; p.T[t * 3 + 2] = tris[k + 2];
                    p.C[t] = cls;
                }
            }
            _parts[id] = p;
            var seen = new HashSet<long>();
            for (int tri = 0; tri < triCount; tri++)
            {
                if (ScatterGround.Barren((GroundClass)(p.C[tri] & 15)) && ScatterGround.Barren((GroundClass)(p.C[tri] >> 4)))
                    continue;   // towns/dungeons/water never take scatter: not indexed (a point over them finds nothing)
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (int v = 0; v < 3; v++)
                {
                    int i = p.T[tri * 3 + v] * 3;
                    x0 = Math.Min(x0, p.P[i]); x1 = Math.Max(x1, p.P[i]);
                    z0 = Math.Min(z0, p.P[i + 2]); z1 = Math.Max(z1, p.P[i + 2]);
                }
                if (x1 - x0 > 2000 || z1 - z0 > 2000) continue;
                p.MinX = Math.Min(p.MinX, x0); p.MaxX = Math.Max(p.MaxX, x1); p.MinZ = Math.Min(p.MinZ, z0); p.MaxZ = Math.Max(p.MaxZ, z1);
                int bx0 = (int)Math.Floor(x0 / Cell), bx1 = (int)Math.Floor(x1 / Cell), bz0 = (int)Math.Floor(z0 / Cell), bz1 = (int)Math.Floor(z1 / Cell);
                for (int bz = bz0; bz <= bz1; bz++)
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        long key = Key(bx, bz);
                        if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<long>();
                        list.Add(((long)id << 32) | (uint)tri);
                        if (seen.Add(key)) p.Keys.Add(key);
                    }
                TriangleCount++;
            }
        }

        public void Remove(int id)
        {
            if (!_parts.TryGetValue(id, out var p)) return;
            foreach (var key in p.Keys)
                if (_grid.TryGetValue(key, out var list))
                {
                    int before = list.Count;
                    list.RemoveAll(e => (int)(e >> 32) == id);
                    TriangleCount -= before - list.Count;
                    if (list.Count == 0) _grid.Remove(key);
                }
            _parts.Remove(id);
        }

        /// <summary>XZ bounds of a cell's indexed triangles (false: unknown id or nothing indexed).</summary>
        public bool Bounds(int id, out float minX, out float minZ, out float maxX, out float maxZ)
        {
            minX = minZ = maxX = maxZ = 0;
            if (!_parts.TryGetValue(id, out var p) || p.MinX > p.MaxX) return false;
            minX = p.MinX; minZ = p.MinZ; maxX = p.MaxX; maxZ = p.MaxZ;
            return true;
        }

        public bool Sample(float x, float z, out GroundSample s)
        {
            s = default;
            if (!_grid.TryGetValue(Key((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var list)) return false;
            float bestY = float.NegativeInfinity;
            foreach (long e in list)
            {
                var p = _parts[(int)(e >> 32)];
                int tri = (int)(e & 0xFFFFFFFF);
                int ia = p.T[tri * 3], ib = p.T[tri * 3 + 1], ic = p.T[tri * 3 + 2];
                float ax = p.P[ia * 3], ay = p.P[ia * 3 + 1], az = p.P[ia * 3 + 2];
                float bx = p.P[ib * 3], by = p.P[ib * 3 + 1], bz = p.P[ib * 3 + 2];
                float cx = p.P[ic * 3], cy = p.P[ic * 3 + 1], cz = p.P[ic * 3 + 2];
                // barycentric in XZ
                float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                if (Math.Abs(d) < 1e-8f) continue;
                float wa = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d;
                float wb = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d;
                float wc = 1 - wa - wb;
                const float eps = -1e-5f;
                if (wa < eps || wb < eps || wc < eps) continue;
                float y = wa * ay + wb * by + wc * cy;
                if (y <= bestY) continue;
                bestY = y;
                // face normal, up-facing
                float ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float nl = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl < 1e-12f) { nx = 0; ny = 1; nz = 0; nl = 1; }
                if (ny < 0) { nx = -nx; ny = -ny; nz = -nz; }
                s.Y = y; s.Nx = nx / nl; s.Ny = ny / nl; s.Nz = nz / nl;
                s.A = (GroundClass)(p.C[tri] & 15);
                s.B = (GroundClass)(p.C[tri] >> 4);
                s.Alpha = p.A != null ? Math.Max(0, Math.Min(1, wa * p.A[ia] + wb * p.A[ib] + wc * p.A[ic])) : 0;
            }
            return bestY > float.NegativeInfinity;
        }
    }
}
