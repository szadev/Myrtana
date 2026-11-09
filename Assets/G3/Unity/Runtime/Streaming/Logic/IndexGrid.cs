using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// mt-1 (Docs/multithreading-plan.md, item E.1): a uniform grid of item indices on the ground plane (x, z). Built once per
    /// frame over the walkers' candidate bodies, queried per walker step instead of testing every live body. A query returns
    /// every index whose cell overlaps the square around the point, in ascending index order (the order of the list the
    /// indices came from), so the caller's following exact tests see the same bodies in the same order as a full scan.
    /// Engine-free (unit-tested in Tools/G3Tests).
    /// </summary>
    public sealed class IndexGrid
    {
        readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        readonly Stack<List<int>> _spare = new Stack<List<int>>();
        float _cell = 16f;

        public float CellSize => _cell;
        public int Count { get; private set; }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        int CellOf(float v) => (int)Math.Floor(v / _cell);

        public void Clear(float cellSize)
        {
            if (!(cellSize > 0f)) throw new ArgumentOutOfRangeException(nameof(cellSize));
            _cell = cellSize;
            foreach (var l in _cells.Values)
            {
                l.Clear();
                _spare.Push(l);
            }
            _cells.Clear();
            Count = 0;
        }

        /// <summary>Adds <paramref name="index"/> at (x, z). Add in ascending index order (each cell list stays sorted).</summary>
        public void Add(int index, float x, float z)
        {
            long k = Key(CellOf(x), CellOf(z));
            if (!_cells.TryGetValue(k, out var l))
            {
                l = _spare.Count > 0 ? _spare.Pop() : new List<int>();
                _cells[k] = l;
            }
            l.Add(index);
            Count++;
        }

        /// <summary>
        /// Every index of the cells that overlap [x - r, x + r] x [z - r, z + r], ascending, appended to <paramref name="into"/>
        /// (cleared first). Non-finite input: every index (the caller then tests them all, as before the grid).
        /// </summary>
        public void Query(float x, float z, float r, List<int> into)
        {
            into.Clear();
            if (float.IsNaN(x) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(z) || float.IsNaN(r) || float.IsInfinity(r) ||
                r / _cell > 64f)
            {
                foreach (var l in _cells.Values) into.AddRange(l);
                into.Sort();
                return;
            }
            int x0 = CellOf(x - r), x1 = CellOf(x + r), z0 = CellOf(z - r), z1 = CellOf(z + r);
            int lists = 0;
            for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++)
                if (_cells.TryGetValue(Key(cx, cz), out var l) && l.Count > 0)
                {
                    into.AddRange(l);
                    lists++;
                }
            if (lists > 1) into.Sort();
        }
    }
}
