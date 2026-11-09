using System;

namespace G3.Unity
{
    /// <summary>
    /// The sand grain detail texture (Docs/remaster-world.md 22.3), generated at runtime, engine-free: a seamless tile of
    /// individual grains (~1 mm, jittered grid, 3x3 neighbourhood: every pixel takes its nearest grain) over a faint mm-cm
    /// relief, as RGBA bytes: R, G = tangent normal x, y (0.5 = flat), B = height (mean 0.5), A = albedo factor (mean exactly
    /// 0.5 → 1.0 in the shader: the low frequencies stay G3's sand image, only the grain is added). Grain tones: mostly quartz
    /// (around the mean), a few darker mineral grains, a few lighter ones. No assets, no AI: the colour is G3's by construction.
    /// Deterministic for (size, seed); tested in SandV2Tests (seamless, means, unit normals).
    /// </summary>
    public static class SandDetail
    {
        /// <summary>Grains per side at the default 1024 px (2 px per grain cell).</summary>
        public const int CellPixels = 2;

        /// <summary>RGBA bytes, row-major, <paramref name="size"/> x <paramref name="size"/> (a power of two, ≥ 16).</summary>
        public static byte[] Generate(int size, uint seed)
        {
            if (size < 16 || (size & (size - 1)) != 0) throw new ArgumentException("size must be a power of two >= 16", nameof(size));
            int n = size * size;
            var height = new float[n];
            var tone = new float[n];
            int cells = Math.Max(4, size / CellPixels);
            float cellPx = size / (float)cells;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float gx = (x + 0.5f) / cellPx, gy = (y + 0.5f) / cellPx;
                    int cx = (int)Math.Floor(gx), cy = (int)Math.Floor(gy);
                    float best = float.MaxValue, bestR = 1, bestTone = 0;
                    for (int j = -1; j <= 1; j++)
                        for (int i = -1; i <= 1; i++)
                        {
                            int ix = cx + i, iy = cy + j;
                            int wx = ((ix % cells) + cells) % cells, wy = ((iy % cells) + cells) % cells;   // wrap: seamless
                            var (r0, r1, r2) = SnowV2.GrainHash(wx, wy, seed);
                            float px = ix + 0.15f + 0.7f * r0, py = iy + 0.15f + 0.7f * r1;
                            float dx = gx - px, dy = gy - py;
                            float rad = 0.55f + 0.35f * r2;        // grain radius in cells (0.55..0.9)
                            float d = (dx * dx + dy * dy) / (rad * rad);
                            if (d < best) { best = d; bestR = rad; bestTone = r2; }
                        }
                    // dome of the nearest grain (in the gaps between grains: low)
                    float dome = best < 1 ? (float)Math.Sqrt(1 - best) : 0;
                    int k = y * size + x;
                    height[k] = dome * (0.6f + 0.4f * bestR);
                    // tone: a pseudo-random second number of the grain decides its mineral (from its tone hash)
                    float t = Frac(bestTone * 7.31f + 0.13f);
                    float g = t < 0.07f ? -0.55f : t < 0.16f ? 0.35f : (t - 0.58f) * 0.5f;   // dark minerals, light grains, quartz
                    tone[k] = g * (0.55f + 0.45f * dome) - (1 - dome) * 0.25f;            // gaps darker (shade between grains)
                }
            }
            // a faint mm-cm relief under the grains (wind-sorted micro hummocks), seamless: wrapped value noise, 4 and 16 cells
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    float r = WrapNoise(u * 4, v * 4, 4, seed + 101u) * 0.6f + WrapNoise(u * 16, v * 16, 16, seed + 203u) * 0.4f;
                    height[y * size + x] = height[y * size + x] * 0.8f + r * 0.35f;
                }
            Normalize(height, 0.5f, 0.22f);
            Normalize(tone, 0.5f, 0.16f);
            var px8 = new byte[n * 4];
            float slope = size / 64f;   // normal strength: height units per pixel step → about 45 deg at a grain's flank
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int xl = (x + size - 1) & (size - 1), xr = (x + 1) & (size - 1), yd = (y + size - 1) & (size - 1), yu = (y + 1) & (size - 1);
                    float hx = (height[y * size + xr] - height[y * size + xl]) * 0.5f * slope;
                    float hy = (height[yu * size + x] - height[yd * size + x]) * 0.5f * slope;
                    float nx = -hx, ny = -hy, nz = 1;
                    float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    int o = (y * size + x) * 4;
                    px8[o] = ToByte(nx / l * 0.5f + 0.5f);
                    px8[o + 1] = ToByte(ny / l * 0.5f + 0.5f);
                    px8[o + 2] = ToByte(height[y * size + x]);
                    px8[o + 3] = ToByte(tone[y * size + x]);
                }
            return px8;
        }

        static float Frac(float v) => v - (float)Math.Floor(v);

        static byte ToByte(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255f)));

        /// <summary>Value noise (0..1) on a lattice that wraps every <paramref name="period"/> cells.</summary>
        static float WrapNoise(float x, float y, int period, uint seed)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            float ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
            float H(int a, int b) => SnowV2.GrainHash(((a % period) + period) % period, ((b % period) + period) % period, seed).r0;
            float a00 = H(ix, iy), a10 = H(ix + 1, iy), a01 = H(ix, iy + 1), a11 = H(ix + 1, iy + 1);
            return a00 + (a10 - a00) * ux + (a01 - a00) * uy + (a00 - a10 - a01 + a11) * ux * uy;
        }

        /// <summary>Rescales to the given mean and standard deviation, clamped to 0..1 (then the mean is re-centred).</summary>
        static void Normalize(float[] a, float mean, float sd)
        {
            double s = 0, s2 = 0;
            foreach (var v in a) { s += v; s2 += v * v; }
            double m = s / a.Length, var = Math.Max(s2 / a.Length - m * m, 1e-12);
            float k = (float)(sd / Math.Sqrt(var));
            for (int i = 0; i < a.Length; i++) a[i] = Math.Max(0, Math.Min(1, (float)((a[i] - m) * k + mean)));
            // clamping moved the mean a little: shift back (twice is plenty)
            for (int pass = 0; pass < 2; pass++)
            {
                double t = 0;
                foreach (var v in a) t += v;
                float shift = (float)(mean - t / a.Length);
                for (int i = 0; i < a.Length; i++) a[i] = Math.Max(0, Math.Min(1, a[i] + shift));
            }
        }

        /// <summary>Mean of one byte channel (0..1): tests and the runtime log.</summary>
        public static float ChannelMean(byte[] rgba, int channel)
        {
            long s = 0;
            for (int i = channel; i < rgba.Length; i += 4) s += rgba[i];
            return s / (255f * (rgba.Length / 4));
        }
    }
}
