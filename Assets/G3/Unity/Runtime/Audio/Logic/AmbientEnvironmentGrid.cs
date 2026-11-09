using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>
    /// Coarse world grid of the dominant SpeedTree ambient environment (eCSpeedTree_PS.AmbientEnvironment). G3
    /// registers every tree as an "ambient object" and plays one-shots at them; this grid stands in for the 57k tree
    /// positions. Cell value 0 = no trees, 1 + environment otherwise.
    /// </summary>
    [Serializable]
    public sealed class AmbientEnvironmentGrid
    {
        public float originX, originZ, cellSize = 25f;
        public int countX, countZ;
        public byte[] cells = new byte[0];

        public bool IsEmpty => cells == null || cells.Length == 0;

        public static AmbientEnvironmentGrid Build(float originX, float originZ, float width, float depth, float cellSize,
                                                   IEnumerable<(float x, float z, AmbientEnvironment env)> trees)
        {
            var g = new AmbientEnvironmentGrid
            {
                originX = originX, originZ = originZ, cellSize = cellSize,
                countX = (int)Math.Ceiling(width / cellSize), countZ = (int)Math.Ceiling(depth / cellSize),
            };
            var counts = new int[g.countX * g.countZ * 6];
            foreach (var (x, z, env) in trees)
            {
                int i = g.Index(x, z);
                if (i >= 0) counts[i * 6 + (int)env]++;
            }
            g.cells = new byte[g.countX * g.countZ];
            for (int i = 0; i < g.cells.Length; i++)
            {
                int best = -1, bestN = 0;
                for (int e = 0; e < 6; e++)
                    if (counts[i * 6 + e] > bestN) { bestN = counts[i * 6 + e]; best = e; }
                g.cells[i] = (byte)(best + 1);
            }
            return g;
        }

        public int Index(float x, float z)
        {
            int ix = (int)Math.Floor((x - originX) / cellSize), iz = (int)Math.Floor((z - originZ) / cellSize);
            return ix < 0 || iz < 0 || ix >= countX || iz >= countZ ? -1 : iz * countX + ix;
        }

        public AmbientEnvironment? At(float x, float z)
        {
            int i = IsEmpty ? -1 : Index(x, z);
            return i < 0 || cells[i] == 0 ? (AmbientEnvironment?)null : (AmbientEnvironment)(cells[i] - 1);
        }

        /// <summary>Majority environment of the (2r+1)² cells around a point; null when there are no trees.</summary>
        public AmbientEnvironment? DominantAround(float x, float z, int radius)
        {
            var votes = new int[6];
            int best = -1;
            ForCells(x, z, radius, (cx, cz, v) =>
            {
                votes[v]++;
                if (best < 0 || votes[v] > votes[best]) best = v;
            });
            return best < 0 ? (AmbientEnvironment?)null : (AmbientEnvironment)best;
        }

        /// <summary>A random point (cell centre ± jitter) of a nearby cell with environment <paramref name="env"/>.</summary>
        public bool TryRandomPoint(AmbientEnvironment env, float x, float z, int radius, Random rng, out float px, out float pz)
        {
            var hits = new List<(float, float)>();
            ForCells(x, z, radius, (cx, cz, v) => { if (v == (int)env) hits.Add((cx, cz)); });
            px = pz = 0;
            if (hits.Count == 0) return false;
            var (hx, hz) = hits[rng.Next(hits.Count)];
            px = hx + (float)(rng.NextDouble() - 0.5) * cellSize;
            pz = hz + (float)(rng.NextDouble() - 0.5) * cellSize;
            return true;
        }

        void ForCells(float x, float z, int radius, Action<float, float, int> visit)
        {
            if (IsEmpty) return;
            int ix = (int)Math.Floor((x - originX) / cellSize), iz = (int)Math.Floor((z - originZ) / cellSize);
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int cx = ix + dx, cz = iz + dz;
                if (cx < 0 || cz < 0 || cx >= countX || cz >= countZ) continue;
                int v = cells[cz * countX + cx];
                if (v == 0) continue;
                visit(originX + (cx + 0.5f) * cellSize, originZ + (cz + 0.5f) * cellSize, v - 1);
            }
        }
    }
}
