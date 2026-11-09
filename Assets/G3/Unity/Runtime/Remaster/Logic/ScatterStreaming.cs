using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Which (tile, layer) units the scatter keeps round the camera (Docs/remaster-world.md section 18.2): a layer of a tile is
    /// wanted while the tile's rectangle is within the layer's range of the camera, and dropped again only beyond the range plus
    /// a hysteresis (no churn when the hero stands on a border). Nearest first.
    /// </summary>
    public static class ScatterTiles
    {
        public static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;
        public static int KeyX(long key) => (int)(key >> 32);
        public static int KeyZ(long key) => (int)(uint)(key & 0xFFFFFFFF);

        /// <summary>Distance (m) from (x, z) to the tile's rectangle (0 inside).</summary>
        public static float Distance(int tx, int tz, float tileSize, float x, float z)
        {
            float x0 = tx * tileSize, z0 = tz * tileSize;
            float dx = Math.Max(0, Math.Max(x0 - x, x - (x0 + tileSize)));
            float dz = Math.Max(0, Math.Max(z0 - z, z - (z0 + tileSize)));
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Tiles with the layers wanted at this camera position (bit per layer), nearest first. <paramref name="ranges"/> per
        /// layer (m, 0 = layer off).
        /// </summary>
        public static List<(int tx, int tz, int layers, float dist)> Wanted(float x, float z, float tileSize, float[] ranges)
        {
            float max = 0;
            foreach (var r in ranges) max = Math.Max(max, r);
            var list = new List<(int, int, int, float)>();
            if (max <= 0) return list;
            int tx0 = (int)Math.Floor((x - max) / tileSize), tx1 = (int)Math.Floor((x + max) / tileSize);
            int tz0 = (int)Math.Floor((z - max) / tileSize), tz1 = (int)Math.Floor((z + max) / tileSize);
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    float d = Distance(tx, tz, tileSize, x, z);
                    int mask = 0;
                    for (int l = 0; l < ranges.Length; l++) if (ranges[l] > 0 && d <= ranges[l]) mask |= 1 << l;
                    if (mask != 0) list.Add((tx, tz, mask, d));
                }
            list.Sort((a, b) => a.Item4.CompareTo(b.Item4));
            return list;
        }

        /// <summary>True when a resident layer should be dropped: beyond its range plus the hysteresis.</summary>
        public static bool Drop(int tx, int tz, float tileSize, float x, float z, float range, float hysteresis) =>
            range <= 0 || Distance(tx, tz, tileSize, x, z) > range + hysteresis;
    }

    /// <summary>
    /// G3's own vegetation colour round a point (Docs 17.2, "keep G3's palette"): the instance colours of G3's grass grid
    /// averaged per 8 m cell; the scatter's G3 plants take the local colour (dry where G3's grass is dry, lush where it is
    /// green). Built on the worker thread from the regions' instance data.
    /// </summary>
    public sealed class ScatterTintGrid
    {
        public const float Cell = 8f;
        readonly Dictionary<long, (float r, float g, float b, int n)> _sum = new Dictionary<long, (float, float, float, int)>();
        readonly Dictionary<long, uint> _avg = new Dictionary<long, uint>();
        bool _dirty;

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        public int Cells => _sum.Count;

        public void Add(float x, float z, uint argb)
        {
            long k = Key((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell));
            _sum.TryGetValue(k, out var s);
            _sum[k] = (s.r + ((argb >> 16) & 255), s.g + ((argb >> 8) & 255), s.b + (argb & 255), s.n + 1);
            _dirty = true;
        }

        void Finish()
        {
            _avg.Clear();
            foreach (var kv in _sum)
            {
                var (r, g, b, n) = kv.Value;
                uint R = (uint)Math.Round(r / n), G = (uint)Math.Round(g / n), B = (uint)Math.Round(b / n);
                _avg[kv.Key] = 0xFF000000u | R << 16 | G << 8 | B;
            }
            _dirty = false;
        }

        /// <summary>Average colour of the cell at (x, z), else the nearest filled cell within one ring; 0 = unknown.</summary>
        public uint At(float x, float z)
        {
            if (_dirty) Finish();
            int cx = (int)Math.Floor(x / Cell), cz = (int)Math.Floor(z / Cell);
            if (_avg.TryGetValue(Key(cx, cz), out var c)) return c;
            for (int r = 1; r <= 2; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                        if (_avg.TryGetValue(Key(cx + dx, cz + dz), out c)) return c;
                    }
            return 0;
        }
    }

    /// <summary>
    /// Bending by characters (Docs 17.5): a toroidal trail map round the camera (64 m, 0.25 m texels) that the GPU stamps
    /// every frame with the benders (hero, NPCs, animals within range) and lets recover. Engine-free maths, mirrored by
    /// G3ScatterCull.compute (kernel Bend) and G3ScatterBend.hlsl; tested.
    /// </summary>
    public static class ScatterBend
    {
        /// <summary>Recovery time constant (s): a plant stands up again to ~37 % bend in this time.</summary>
        public const float Recover = 1.6f;

        /// <summary>Remaining bend after <paramref name="dt"/> s.</summary>
        public static float Decay(float bend, float dt, float tau = Recover) => bend * (float)Math.Exp(-Math.Max(0, dt) / Math.Max(1e-3f, tau));

        /// <summary>
        /// Push of one bender on a point (dx, dz = point − bender centre, m): outward, full inside the core, fading to 0 at
        /// <paramref name="radius"/>; plus a share along the bender's motion (vx, vz in m/s) so plants lean the way it walks.
        /// Returns the push vector (|p| ≤ 1).
        /// </summary>
        public static (float x, float z) Push(float dx, float dz, float radius, float vx, float vz)
        {
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            if (d >= radius || radius <= 0) return (0, 0);
            float f = 1 - d / radius;
            f = f * f * (3 - 2 * f);
            float ox = d > 1e-4f ? dx / d : 0, oz = d > 1e-4f ? dz / d : 0;
            float sp = (float)Math.Sqrt(vx * vx + vz * vz);
            float mv = Math.Min(1, sp / 3f) * 0.6f;
            float mx = sp > 1e-3f ? vx / sp : 0, mz = sp > 1e-3f ? vz / sp : 0;
            float px = ox * (1 - mv) + mx * mv, pz = oz * (1 - mv) + mz * mv;
            float pl = (float)Math.Sqrt(px * px + pz * pz);
            if (pl > 1e-4f) { px /= pl; pz /= pl; }
            return (px * f, pz * f);
        }

        /// <summary>Combine an existing bend with a new stamp: the stronger one wins (per component magnitude).</summary>
        public static (float x, float z) Combine((float x, float z) old, (float x, float z) stamp)
        {
            float lo = old.x * old.x + old.z * old.z, ls = stamp.x * stamp.x + stamp.z * stamp.z;
            return ls > lo ? stamp : old;
        }

        /// <summary>Toroidal texel of a world coordinate (extent m over n texels).</summary>
        public static int Texel(float w, float extent, int n)
        {
            float t = w / extent;
            t -= (float)Math.Floor(t);
            return Math.Min(n - 1, (int)(t * n));
        }
    }
}

namespace G3.Unity
{
    /// <summary>
    /// G3's trees for the scatter (Docs/remaster-world.md section 18.2): crown positions/radii bucketed on a 16 m grid; the
    /// cover at a point is 1 - prod(1 - f) over the crowns, f = 1 inside 60 % of the crown radius fading to 0 at 1.4 x the
    /// radius (the floor under and just round a crown). Conifers and broadleaf trees separately (needles vs leaf litter).
    /// Written on the scatter's worker thread, read there and by the v2 grass build (pass 2: G3's grass thins out under the
    /// crowns): every access holds a lock.
    /// </summary>
    public sealed class ScatterCanopy
    {
        public const float Cell = 16f;
        readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<(float x, float z, float r, bool conifer, int id)>> _grid =
            new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<(float, float, float, bool, int)>>();
        readonly System.Collections.Generic.HashSet<int> _ids = new System.Collections.Generic.HashSet<int>();

        readonly object _sync = new object();

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        public int Count { get { lock (_sync) return _ids.Count; } }

        /// <summary>Trees whose crown centre lies in [x0, x1] x [z0, z1] (a region's tree count: the v2 grass rebuilds a region
        /// whose count changed since its build).</summary>
        public int CountIn(float x0, float z0, float x1, float z1)
        {
            int n = 0;
            lock (_sync)
            {
                int bx0 = (int)Math.Floor(x0 / Cell), bx1 = (int)Math.Floor(x1 / Cell), bz0 = (int)Math.Floor(z0 / Cell), bz1 = (int)Math.Floor(z1 / Cell);
                for (int bz = bz0; bz <= bz1; bz++)
                    for (int bx = bx0; bx <= bx1; bx++)
                        if (_grid.TryGetValue(Key(bx, bz), out var l))
                            foreach (var t in l)
                                if (t.x >= x0 && t.x <= x1 && t.z >= z0 && t.z <= z1 && (int)Math.Floor(t.x / Cell) == bx && (int)Math.Floor(t.z / Cell) == bz) n++;
            }
            return n;
        }

        /// <summary>Conifers by G3's SpeedTree name (firs, pines, spruces, cedars, cypresses, larches; snowy Nordmar trees).</summary>
        public static bool IsConifer(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var w in new[] { "Fir", "Pine", "Spruce", "Cedar", "Cypress", "Larch", "Juniper", "Hemlock", "Conifer", "Tanne", "Kiefer" })
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public void Add(int id, float x, float z, float radius, bool conifer)
        {
            lock (_sync) AddLocked(id, x, z, radius, conifer);
        }

        void AddLocked(int id, float x, float z, float radius, bool conifer)
        {
            if (!_ids.Add(id)) return;
            radius = Math.Max(0.5f, radius);
            int x0 = (int)Math.Floor((x - radius * 1.4f) / Cell), x1 = (int)Math.Floor((x + radius * 1.4f) / Cell);
            int z0 = (int)Math.Floor((z - radius * 1.4f) / Cell), z1 = (int)Math.Floor((z + radius * 1.4f) / Cell);
            for (int bz = z0; bz <= z1; bz++)
                for (int bx = x0; bx <= x1; bx++)
                {
                    long k = Key(bx, bz);
                    if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new System.Collections.Generic.List<(float, float, float, bool, int)>();
                    l.Add((x, z, radius, conifer, id));
                }
        }

        public void At(float x, float z, out float broad, out float conifer)
        {
            lock (_sync) AtLocked(x, z, out broad, out conifer);
        }

        /// <summary>Max of the broadleaf and conifer cover at (x, z).</summary>
        public float Cover(float x, float z)
        {
            At(x, z, out float b, out float c);
            return Math.Max(b, c);
        }

        /// <summary>
        /// Pass 2: whether a G3 grass plant at (x, z) stays under the crowns (deterministic by position): kept with
        /// probability 1 - (1 - <paramref name="keepUnder"/>) x cover - the forest floor reads as litter, not as a meadow.
        /// </summary>
        public static bool KeepGrass(float cover, float keepUnder, float x, float z)
        {
            if (cover <= 0.001f) return true;
            float keep = 1 - (1 - keepUnder) * Math.Min(1, cover);
            uint h = ScatterSampler.Hash((int)Math.Floor(x * 7.31f), (int)Math.Floor(z * 7.31f), 0x6A09E667u);
            return ScatterSampler.Hash01(h) < keep;
        }

        void AtLocked(float x, float z, out float broad, out float conifer)
        {
            float pb = 1, pc = 1;
            if (_grid.TryGetValue(Key((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var l))
                foreach (var t in l)
                {
                    float dx = x - t.x, dz = z - t.z;
                    float d = (float)Math.Sqrt(dx * dx + dz * dz);
                    float f = ScatterSampler.SmoothStep(t.r * 1.4f, t.r * 0.6f, d);
                    if (f <= 0) continue;
                    if (t.conifer) pc *= 1 - f; else pb *= 1 - f;
                }
            broad = 1 - pb;
            conifer = 1 - pc;
        }
    }
}
