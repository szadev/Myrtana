using System;

namespace G3.Unity
{
    /// <summary>
    /// fx-1 (Docs/remaster-world.md 22.11): "covered" ground round the camera - a roof, a tent, an awning, an arcade, an
    /// overhang or a building interior above the terrain. Per grid point the first surface a ray straight down from high above
    /// meets (<c>top</c>) is compared with the terrain's top collision there (<c>ground</c>): more than <see cref="MinClear"/> m
    /// above it = covered. Grown by one point (a roof's eaves keep the fx out of its edge) and stored as bytes (255 = covered).
    /// Read by the blown-sand fx (no streamers or dust under roofs) and shared with the arch shaders (no sand indoors, arch-4):
    /// globals <c>_G3CoveredMask</c> / <c>_G3CoveredP</c> (G3CoveredMap).
    /// </summary>
    public static class CoveredMap
    {
        /// <summary>Clearance (m) above the terrain from which a surface over it counts as a roof (people, props and walls lower).</summary>
        public const float MinClear = 1.6f;

        /// <summary>Covered per point (1/0): top surface above the terrain by more than <see cref="MinClear"/>; NaN inputs = open.</summary>
        public static float Covered(float top, float ground)
            => float.IsNaN(top) || float.IsNaN(ground) ? 0f : top - ground > MinClear ? 1f : 0f;

        /// <summary>Classifies a grid (row-major n x n) into bytes and grows the covered points by one (8-neighbourhood).</summary>
        public static void Build(float[] top, float[] ground, int n, byte[] into)
        {
            var raw = new byte[n * n];
            for (int k = 0; k < raw.Length; k++) raw[k] = Covered(top[k], ground[k]) > 0 ? (byte)255 : (byte)0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    byte v = 0;
                    for (int dj = -1; dj <= 1 && v == 0; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int x = i + di, z = j + dj;
                            if (x < 0 || z < 0 || x >= n || z >= n) continue;
                            if (raw[z * n + x] != 0) { v = 255; break; }
                        }
                    into[j * n + i] = v;
                }
        }

        /// <summary>Covered (0..1) at (x, z) from a grid at origin (x0, z0), texel size; 0 off the grid.</summary>
        public static float At(byte[] map, int n, float x0, float z0, float texel, float x, float z)
        {
            if (map == null) return 0f;
            int i = (int)Math.Round((x - x0) / texel), j = (int)Math.Round((z - z0) / texel);
            if (i < 0 || j < 0 || i >= n || j >= n) return 0f;
            return map[j * n + i] / 255f;
        }
    }
}
