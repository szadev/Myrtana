using System;

namespace G3.Weather
{
    /// <summary>
    /// fix-130 (owner: rain and wet, glossy walls and floors inside a mill): a top-down "rain shadow" around the camera.
    /// Every cell holds the height of the highest static surface over it (one downward ray from high above, the trace
    /// G3's precipitation uses for its drops, <see cref="PrecipitationColumns"/>); a surface lower than that by more than
    /// a tolerance lies under a roof and gets no rain (the v2 materials keep it dry). The grid is world-anchored and
    /// re-centred in whole cells when the camera leaves its middle half; cells are traced nearest-first from a per-frame
    /// budget and then refreshed round and round (cells of buildings that stream in later are picked up).
    /// [interp] G3 itself has no wet materials; only its drops stop on roofs.
    /// </summary>
    public sealed class RainShadowGrid
    {
        /// <summary>Height of a cell not traced yet or with nothing below: no occluder (rain reaches everything).</summary>
        public const float Open = -100000f;

        readonly int[] _order;
        readonly bool[] _known;   // traced since the grid was placed / the cell came into it
        int _cursor, _pendingCursor;

        public RainShadowGrid(int size = 96, float cellSize = 1f)
        {
            Size = Math.Max(4, size);
            CellSize = cellSize > 0 ? cellSize : 1f;
            Heights = new float[Size * Size];
            _known = new bool[Size * Size];
            for (int i = 0; i < Heights.Length; i++) Heights[i] = Open;
            // nearest to the centre first
            _order = new int[Heights.Length];
            var dist = new float[Heights.Length];
            float c = (Size - 1) * 0.5f;
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                {
                    int i = z * Size + x;
                    _order[i] = i;
                    dist[i] = (x - c) * (x - c) + (z - c) * (z - c);
                }
            Array.Sort(dist, _order);
            OriginX = OriginZ = int.MinValue;
        }

        public int Size { get; }
        public float CellSize { get; }
        /// <summary>Row-major (z · Size + x) heights, m; <see cref="Open"/> = nothing above.</summary>
        public float[] Heights { get; }
        /// <summary>World cell index of column / row 0.</summary>
        public int OriginX { get; private set; }
        public int OriginZ { get; private set; }
        /// <summary>Set when heights changed since <see cref="TakeDirty"/>.</summary>
        public bool Dirty { get; private set; }
        public bool Placed => OriginX != int.MinValue;
        /// <summary>Cells traced since the grid was (re)placed or cleared; a full pass is <c>Size²</c>.</summary>
        public int Traced { get; private set; }
        /// <summary>Cells never traced since the grid was placed or they came into it (<see cref="TracePending"/>).</summary>
        public int Pending { get; private set; }

        /// <summary>World x / z of the grid's lower corner, m.</summary>
        public float MinX => OriginX * CellSize;
        public float MinZ => OriginZ * CellSize;
        public float Extent => Size * CellSize;

        public int Cell(float v) => (int)Math.Floor(v / CellSize);

        public bool TakeDirty()
        {
            bool d = Dirty;
            Dirty = false;
            return d;
        }

        public void Clear()
        {
            for (int i = 0; i < Heights.Length; i++) { Heights[i] = Open; _known[i] = false; }
            _cursor = _pendingCursor = 0;
            Traced = 0;
            Pending = Heights.Length;
            Dirty = true;
        }

        /// <summary>
        /// Keeps the camera in the middle half of the grid: when it leaves, the grid moves (whole cells) to centre on it,
        /// keeping the heights it still covers. Returns whether it moved.
        /// </summary>
        public bool Recenter(float camX, float camZ)
        {
            int cx = Cell(camX), cz = Cell(camZ), half = Size / 2;
            if (Placed)
            {
                int lx = cx - OriginX, lz = cz - OriginZ, q = Size / 4;
                if (lx >= q && lx < Size - q && lz >= q && lz < Size - q) return false;
            }
            int nx = cx - half, nz = cz - half;
            if (Placed) Shift(nx - OriginX, nz - OriginZ);
            else Clear();
            OriginX = nx;
            OriginZ = nz;
            _cursor = _pendingCursor = 0; // nearest first again
            Dirty = true;
            return true;
        }

        void Shift(int dx, int dz)
        {
            if (Math.Abs(dx) >= Size || Math.Abs(dz) >= Size)
            {
                for (int i = 0; i < Heights.Length; i++) { Heights[i] = Open; _known[i] = false; }
                Pending = Heights.Length;
                return;
            }
            var old = (float[])Heights.Clone();
            var oldKnown = (bool[])_known.Clone();
            int pending = 0;
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                {
                    int ox = x + dx, oz = z + dz;
                    bool inside = ox >= 0 && ox < Size && oz >= 0 && oz < Size;
                    Heights[z * Size + x] = inside ? old[oz * Size + ox] : Open;
                    _known[z * Size + x] = inside && oldKnown[oz * Size + ox];
                    if (!_known[z * Size + x]) pending++;
                }
            Pending = pending;
        }

        /// <summary>Traces the next <paramref name="budget"/> cells (nearest to the centre first, then round again) at
        /// their centres; <paramref name="trace"/> returns the highest surface there or null. Returns the cells traced.</summary>
        public int Trace(Func<float, float, float?> trace, int budget)
        {
            if (!Placed || trace == null) return 0;
            int n = 0;
            for (; n < budget; n++)
            {
                int i = _order[_cursor];
                _cursor = (_cursor + 1) % _order.Length;
                TraceCell(trace, i);
            }
            return n;
        }

        void TraceCell(Func<float, float, float?> trace, int i)
        {
            int x = i % Size, z = i / Size;
            float wx = (OriginX + x + 0.5f) * CellSize, wz = (OriginZ + z + 0.5f) * CellSize;
            float h = trace(wx, wz) ?? Open;
            if (h != Heights[i]) { Heights[i] = h; Dirty = true; }
            if (!_known[i]) { _known[i] = true; Pending--; }
            Traced++;
        }

        /// <summary>
        /// Traces up to <paramref name="budget"/> cells that were never traced (nearest first), skipping known ones - the
        /// economy mode for dry weather (remaster snow keeps the grid only for the floors under roofs): new cells get traced
        /// once when the grid moves, the rest is refreshed slowly with a small <see cref="Trace"/> budget. Returns the cells traced.
        /// </summary>
        public int TracePending(Func<float, float, float?> trace, int budget)
        {
            if (!Placed || trace == null) return 0;
            int n = 0;
            while (n < budget && Pending > 0 && _pendingCursor < _order.Length)
            {
                int i = _order[_pendingCursor++];
                if (_known[i]) continue;
                TraceCell(trace, i);
                n++;
            }
            if (_pendingCursor >= _order.Length) Pending = 0;
            return n;
        }

        /// <summary>The occluder height over (x, z), or <see cref="Open"/> outside the grid / where nothing was found.</summary>
        public float HeightAt(float x, float z)
        {
            if (!Placed) return Open;
            int ix = Cell(x) - OriginX, iz = Cell(z) - OriginZ;
            if (ix < 0 || iz < 0 || ix >= Size || iz >= Size) return Open;
            return Heights[iz * Size + ix];
        }

        /// <summary>How far below the cell's occluder a surface may lie and still count as that occluder (a sloped roof
        /// within one cell), m.</summary>
        public const float Tolerance = 0.75f;
        /// <summary>Blend band below <see cref="Tolerance"/>, m.</summary>
        public const float Ramp = 0.5f;

        /// <summary>
        /// 0 = open to the rain, 1 = under a roof, for a surface at <paramref name="surfaceY"/> under an occluder at
        /// <paramref name="occluderY"/> (the shader's G3RainShelter does the same).
        /// </summary>
        public static float Shelter(float surfaceY, float occluderY)
        {
            float t = (occluderY - surfaceY - Tolerance) / Ramp;
            return t <= 0 ? 0 : t >= 1 ? 1 : t;
        }

        /// <summary>
        /// The occluder height at (x, z) interpolated bilinearly between the four nearest cell centres (what the shader's
        /// G3RainShelter compares with; snow3): on open ground that is the ground itself at any steepness, so a slope is not
        /// "under a roof" in the lower half of every cell (point sampling gave stair steps). A corner with nothing traced takes
        /// the lowest valid corner; <see cref="Open"/> when none is valid or (x, z) is outside the grid.
        /// </summary>
        public float TopAt(float x, float z)
        {
            if (!Placed) return Open;
            float gx = x / CellSize - OriginX - 0.5f, gz = z / CellSize - OriginZ - 0.5f;
            int cx = (int)Math.Floor(gx), cz = (int)Math.Floor(gz);
            if (cx < -1 || cz < -1 || cx >= Size || cz >= Size) return Open;
            float fx = gx - cx, fz = gz - cz;
            int x0 = Math.Max(cx, 0), x1 = Math.Min(cx + 1, Size - 1), z0 = Math.Max(cz, 0), z1 = Math.Min(cz + 1, Size - 1);
            float t00 = Heights[z0 * Size + x0], t10 = Heights[z0 * Size + x1], t01 = Heights[z1 * Size + x0], t11 = Heights[z1 * Size + x1];
            float lo = Math.Min(Math.Min(V(t00), V(t10)), Math.Min(V(t01), V(t11)));
            if (lo == float.MaxValue) return Open;
            if (t00 <= Open * 0.9f) t00 = lo; if (t10 <= Open * 0.9f) t10 = lo;
            if (t01 <= Open * 0.9f) t01 = lo; if (t11 <= Open * 0.9f) t11 = lo;
            float a = t00 + (t10 - t00) * fx, b = t01 + (t11 - t01) * fx;
            return a + (b - a) * fz;
        }

        static float V(float t) => t > Open * 0.9f ? t : float.MaxValue;

        /// <summary>
        /// The occluder the shelter test compares with (fix-169): the lower of the point's own cell and the bilinear
        /// <see cref="TopAt"/>. Bilinear alone is right on continuous ground (snow3: slopes without stair steps) but smears a
        /// roof into the open cells next to it - up to half a cell of open ground beside a house counted as half under its
        /// eave (b1021a: fix130 probe 1 m from House_Small_01, 0.46). The own cell alone gave the stair steps on slopes. The
        /// minimum keeps both right: on open ground (any slope) it is never above the bilinear ground plane -> no shelter;
        /// beside a roof the own open cell wins; under a roof both are the roof (only the outermost half cell under an eave
        /// blends towards the ground outside, still far above a floor under a real roof). The shader's
        /// G3RainShelter does the same.
        /// </summary>
        public float OccluderAt(float x, float z)
        {
            float top = TopAt(x, z);
            if (top <= Open * 0.9f) return Open;
            float own = HeightAt(x, z);
            return own > Open * 0.9f && own < top ? own : top;
        }

        /// <summary><see cref="Shelter(float,float)"/> at a world point (the occluder of <see cref="OccluderAt"/>).</summary>
        public float ShelterAt(float x, float y, float z) => Shelter(y, OccluderAt(x, z));
    }
}
