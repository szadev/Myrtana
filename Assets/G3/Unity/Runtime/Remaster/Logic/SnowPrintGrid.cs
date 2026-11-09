using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// The footprints round the camera (Docs/remaster-world.md 12.5), engine-free: a ring of prints (centre, walk direction,
    /// size, ground height, stamp clock, depth, kind, side, rim; 12 floats = the shader's <c>G3SnowPrintData</c>) and a
    /// toroidal grid of cells (<see cref="SnowV2.Slots"/> print indices each) that tells a pixel which prints can reach it.
    /// The shader evaluates the prints' shapes analytically (<see cref="SnowV2.Print"/>): no texture, so no texel stair steps
    /// and no maintenance pass - a slot that still names a print from 64 m away (the grid repeats) or an overwritten ring
    /// entry simply evaluates to nothing at the pixel. A new print takes in every cell it reaches a free slot, else one whose
    /// print is gone, does not reach the cell (stale) or else the oldest.
    /// </summary>
    public sealed class SnowPrintGrid
    {
        /// <summary>Floats per print (three float4).</summary>
        public const int Stride = 12;

        readonly List<int> _dirtyCells = new List<int>();
        readonly List<int> _dirtyPrints = new List<int>();

        public SnowPrintGrid(float extent, int cells, int capacity)
        {
            Cells = Math.Max(4, cells);
            CellSize = SnowV2.CellSize(extent, Cells);
            Capacity = Math.Max(16, capacity);
            Prints = new float[Capacity * Stride];
            Slots = new uint[Cells * Cells * SnowV2.Slots];
            Clear();
        }

        public int Cells { get; }
        public float CellSize { get; }
        public int Capacity { get; }
        /// <summary>The ring (12 floats per print).</summary>
        public float[] Prints { get; }
        /// <summary>Cell slots (row-major cells, <see cref="SnowV2.Slots"/> each; <see cref="SnowV2.Empty"/> = free).</summary>
        public uint[] Slots { get; }
        /// <summary>Prints made since the grid was created.</summary>
        public int Made { get; private set; }
        /// <summary>remaster-sand: the latest clock of each domain (snow prints age on the snow refill clock, sand prints, kind
        /// &gt;= <see cref="SandV2.SandKind"/>, on the sand wind clock); set by <see cref="Add"/> or the caller each frame.</summary>
        public float SnowNow, SandNow;
        int _next;

        public void Clear()
        {
            for (int i = 0; i < Slots.Length; i++) Slots[i] = SnowV2.Empty;
            for (int i = 0; i < Prints.Length; i++) Prints[i] = 0;
            _next = 0;
            Made = 0;
            _dirtyCells.Clear();
            _dirtyPrints.Clear();
        }

        /// <summary>
        /// Adds a print; <paramref name="clock"/> is the wrapped refill clock now (<see cref="SnowV2.Wrapped"/>). Returns its
        /// ring index.
        /// </summary>
        public int Add(float x, float z, float dirX, float dirZ, float halfLength, float halfWidth, float groundY, float clock,
                       float depth, int kind, float side, float rim)
        {
            float len = (float)Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len < 1e-4f) { dirX = 0; dirZ = 1; } else { dirX /= len; dirZ /= len; }
            bool sand = SandV2.IsSandPrint(kind);
            if (sand) SandNow = clock; else SnowNow = clock;
            int id = _next;
            _next = (_next + 1) % Capacity;
            int o = id * Stride;
            Prints[o + 0] = x; Prints[o + 1] = z; Prints[o + 2] = dirX; Prints[o + 3] = dirZ;
            Prints[o + 4] = halfLength; Prints[o + 5] = halfWidth; Prints[o + 6] = groundY; Prints[o + 7] = clock;
            Prints[o + 8] = depth; Prints[o + 9] = kind; Prints[o + 10] = side; Prints[o + 11] = rim;
            _dirtyPrints.Add(id);
            Made++;
            float r = SnowV2.Reach(halfLength, halfWidth);
            int x0 = SnowV2.WorldCell(x - r, CellSize), x1 = SnowV2.WorldCell(x + r, CellSize);
            int z0 = SnowV2.WorldCell(z - r, CellSize), z1 = SnowV2.WorldCell(z + r, CellSize);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!Reaches(id, cx, cz)) continue;
                    int b = SnowV2.SlotBase(cx, cz, Cells);
                    int pick = -1; float oldest = -1;
                    for (int s = 0; s < SnowV2.Slots; s++)
                    {
                        uint p = Slots[b + s];
                        if (p == SnowV2.Empty || p == (uint)id || p >= Capacity || !Reaches((int)p, cx, cz)) { pick = s; break; }
                        // each print ages on its own domain's clock (remaster-sand: sand prints fill in on the wind clock)
                        float age = SnowV2.Age(SandV2.IsSandPrint(Prints[p * Stride + 9]) ? SandNow : SnowNow, Prints[p * Stride + 7]);
                        if (age >= SnowV2.AgeGone) { pick = s; break; }
                        if (age > oldest) { oldest = age; pick = s; }
                    }
                    Slots[b + pick] = (uint)id;
                    _dirtyCells.Add(b / SnowV2.Slots);
                }
            return id;
        }

        /// <summary>fix-178: takes ring entry <paramref name="id"/> out (size and depth 0: evaluates to nothing, its cell slots
        /// count as free); the persistent store drops covered / old prints this way.</summary>
        public void Remove(int id)
        {
            if (id < 0 || id >= Capacity) return;
            int o = id * Stride;
            Prints[o + 4] = 0; Prints[o + 5] = 0; Prints[o + 8] = 0;
            _dirtyPrints.Add(id);
        }

        /// <summary>Whether print <paramref name="id"/>'s shape can reach world cell (cx, cz).</summary>
        public bool Reaches(int id, int cx, int cz)
        {
            int o = id * Stride;
            float x = Prints[o], z = Prints[o + 1], r = SnowV2.Reach(Prints[o + 4], Prints[o + 5]);
            if (Prints[o + 4] <= 0) return false;
            float minX = cx * CellSize, minZ = cz * CellSize;
            float dx = Math.Max(Math.Max(minX - x, 0), x - (minX + CellSize));
            float dz = Math.Max(Math.Max(minZ - z, 0), z - (minZ + CellSize));
            return dx * dx + dz * dz <= r * r;
        }

        /// <summary>The print indices stored for the world cell at (x, z).</summary>
        public IEnumerable<int> At(float x, float z)
        {
            int b = SnowV2.SlotBase(SnowV2.WorldCell(x, CellSize), SnowV2.WorldCell(z, CellSize), Cells);
            for (int s = 0; s < SnowV2.Slots; s++) if (Slots[b + s] != SnowV2.Empty) yield return (int)Slots[b + s];
        }

        /// <summary>
        /// Depth and rim at a world point the way the shader sums them: the deepest print (faded by its age, on this level),
        /// the rim outside every print. Sand prints age on <paramref name="sandClockNow"/> (default: the same clock).
        /// </summary>
        public SnowV2.Shape Sample(float x, float y, float z, float clockNow, float soft = SnowV2.Wall, float sandClockNow = float.NaN)
        {
            if (float.IsNaN(sandClockNow)) sandClockNow = clockNow;
            var r = new SnowV2.Shape();
            foreach (int id in At(x, z))
            {
                int o = id * Stride;
                float dx = x - Prints[o], dz = z - Prints[o + 1];
                float u = dx * Prints[o + 2] + dz * Prints[o + 3], v = -dx * Prints[o + 3] + dz * Prints[o + 2];
                bool sand = SandV2.IsSandPrint(Prints[o + 9]);
                var s = SnowV2.Print(u, v, Prints[o + 4], Prints[o + 5], SandV2.BaseKind((int)Prints[o + 9]), Prints[o + 10], soft);
                float k = SnowV2.Fade(SnowV2.Age(sand ? sandClockNow : clockNow, Prints[o + 7])) * SnowV2.HeightMatch(y - Prints[o + 6]) * Prints[o + 8];
                r.depth = Math.Max(r.depth, s.depth * k);
                r.rim = Math.Max(r.rim, s.rim * k * Prints[o + 11]);
            }
            r.rim *= 1 - Math.Min(1, r.depth * 2);
            return r;
        }

        /// <summary>Cells (index = slot base / Slots) and prints changed since the last call (for the GPU upload).</summary>
        public void TakeDirty(List<int> cells, List<int> prints)
        {
            cells.Clear(); prints.Clear();
            cells.AddRange(_dirtyCells); prints.AddRange(_dirtyPrints);
            _dirtyCells.Clear(); _dirtyPrints.Clear();
        }
    }
}
