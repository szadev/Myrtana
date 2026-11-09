using System;
using System.Threading.Tasks;

namespace G3.Unity
{
    /// <summary>
    /// The wind-ripple tile of the remaster desert sand (Docs/remaster-world.md 22.4, sand-2), generated at runtime, engine-free:
    /// a seamless field of aeolian ripple crests grown by the anisotropic Swift-Hohenberg equation on a periodic grid
    /// (spectral, semi-implicit; started from noise and stopped early). The pattern-forming equation gives what real impact
    /// ripples show and analytic sines cannot: sinuous crests of one preferred wavelength that bend, fork (Y-junctions =
    /// dislocations) and end, across the wind (the across-wind wavevector is damped). The lee side is steepened by a
    /// trochoid-like shift (crests move downwind, troughs upwind; the field stays integrable, mean slope 0).
    /// Output RGBA bytes: R = slope along the wind (+u), G = slope across (+v) (0.5 = flat), in units of the 99.5th percentile
    /// of the along slope (the shader scales by 2 pi height / wavelength); B = height (0.5 = mean, 0/1 = -/+ the 99.5th
    /// percentile amplitude: parallax and the ripples' own soft shadows at low sun); A = 255 (unused).
    /// Deterministic for (size, seed); SandV2Tests.
    /// </summary>
    public static class SandRipples
    {
        /// <summary>Ripple wavelength in texels (the tile holds size / 8 crests: 128 at 1024 px).</summary>
        public const int WavePixels = 8;

        /// <summary>Crests across one tile of <paramref name="size"/> px (the shader's tile = wavelength x this).</summary>
        public static int CrestsPerTile(int size) => size / WavePixels;

        // Swift-Hohenberg: d psi/dt = r psi - (q0^2 + lap)^2 psi / q0^4 - beta k_across^2 / q0^2 psi - psi^3
        const float R = 0.35f, Dt = 0.8f, Beta = 0.14f;
        const int Steps = 110;
        const float LeeShift = 1.2f;   // px: crest downwind / trough upwind (lee ~1.9x steeper than the stoss side)

        /// <summary>RGBA bytes, row-major (v rows, u columns), <paramref name="size"/> x <paramref name="size"/> (power of two, ≥ 32).</summary>
        public static byte[] Generate(int size, uint seed)
        {
            var h = Height(size, seed);
            var (gu, gv) = Slopes(h, size);
            float hp = Peak(h, 0.995f);
            var o = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                o[4 * i] = ToByte(gu[i] * 0.5f + 0.5f);
                o[4 * i + 1] = ToByte(gv[i] * 0.5f + 0.5f);
                o[4 * i + 2] = ToByte(h[i] / hp * 0.5f + 0.5f);
                o[4 * i + 3] = 255;
            }
            return o;
        }

        /// <summary>The <paramref name="q"/> quantile of |a| (every 7th value; robust peak).</summary>
        static float Peak(float[] a, float q)
        {
            int n = a.Length, cnt = (n + 6) / 7;
            var s = new float[cnt];
            for (int i = 0, j = 0; i < n; i += 7, j++) s[j] = Math.Abs(a[i]);
            Array.Sort(s);
            return Math.Max(s[Math.Min(cnt - 1, (int)(cnt * q))], 1e-6f);
        }

        /// <summary>The ripple height field (psi after the lee shift, about -0.7..0.7) of the tile; tests and tools.</summary>
        public static float[] Height(int size, uint seed)
        {
            if (size < 32 || (size & (size - 1)) != 0) throw new ArgumentException("size must be a power of two >= 32", nameof(size));
            var psi = Grow(size, seed);
            return LeeSkew(psi, size);
        }

        /// <summary>Slopes (along u, across v) of the tile, scaled so the 99.5th percentile of |along| is 1.</summary>
        public static (float[] u, float[] v) Slopes(int size, uint seed) => Slopes(Height(size, seed), size);

        static (float[] u, float[] v) Slopes(float[] h, int size)
        {
            int n = size * size, m = size - 1;
            var gu = new float[n];
            var gv = new float[n];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int k = y * size + x;
                    gu[k] = (h[y * size + ((x + 1) & m)] - h[y * size + ((x + m) & m)]) * 0.5f;
                    gv[k] = (h[((y + 1) & m) * size + x] - h[((y + m) & m) * size + x]) * 0.5f;
                }
            float peak = Peak(gu, 0.995f);
            for (int i = 0; i < n; i++) { gu[i] /= peak; gv[i] /= peak; }
            return (gu, gv);
        }

        // ------------------------------------------------------------------ Swift-Hohenberg on the periodic grid

        static float[] Grow(int size, uint seed)
        {
            int n = size * size;
            var re = new float[n];
            var im = new float[n];
            var psi = new float[n];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    psi[y * size + x] = (SnowV2.GrainHash(x, y, seed).r0 - 0.5f) * 0.35f;   // uniform, sd ~0.1
            // 1 / (1 - dt L(k)), stored in the transposed spectrum layout [ku * size + kv] (see Fft2: rows, transpose, rows)
            float q0 = (float)(2 * Math.PI / WavePixels), q2 = q0 * q0, q4 = q2 * q2;
            var inv = new float[n];
            for (int a = 0; a < size; a++)
                for (int b = 0; b < size; b++)
                {
                    float ku = Freq(a, size), kv = Freq(b, size);
                    float k2 = ku * ku + kv * kv, d = q2 - k2;
                    float l = R - d * d / q4 - Beta * kv * kv / q2;
                    inv[a * size + b] = 1f / (1f - Dt * l) / n;   // with the 1/n of the inverse transform
                }
            var fft = new Fft(size);
            for (int step = 0; step < Steps; step++)
            {
                for (int i = 0; i < n; i++) { float p = psi[i]; re[i] = p - Dt * p * p * p; im[i] = 0; }
                fft.Forward2(re, im);
                for (int i = 0; i < n; i++) { re[i] *= inv[i]; im[i] *= inv[i]; }
                fft.Inverse2(re, im);
                Array.Copy(re, psi, n);
            }
            return psi;
        }

        static float Freq(int i, int size) => (float)(2 * Math.PI * (i < size / 2 ? i : i - size) / size);

        /// <summary>
        /// Lee steepening: h(x) = psi(x0) with x0 + c psi(x0) = x along +u (two fixed-point steps, periodic linear sampling):
        /// crests move downwind and troughs upwind, the downwind (lee) flank gets shorter and steeper.
        /// </summary>
        static float[] LeeSkew(float[] psi, int size)
        {
            var h = new float[psi.Length];
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float x0 = x - LeeShift * psi[row + x];
                    x0 = x - LeeShift * Row(psi, row, size, x0);
                    x0 = x - LeeShift * Row(psi, row, size, x0);
                    h[row + x] = Row(psi, row, size, x0);
                }
            }
            return h;
        }

        static float Row(float[] a, int row, int size, float x)
        {
            float f = (float)Math.Floor(x);
            int i = (int)f;
            float t = x - f;
            int i0 = ((i % size) + size) % size, i1 = (i0 + 1) % size;
            return a[row + i0] + (a[row + i1] - a[row + i0]) * t;
        }

        static byte ToByte(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255f)));

        /// <summary>Radix-2 complex FFT of a square power-of-two grid; rows in parallel, transposes between the passes.</summary>
        sealed class Fft
        {
            readonly int _n, _log;
            readonly float[] _cos, _sin;
            readonly int[] _rev;

            public Fft(int n)
            {
                _n = n;
                while ((1 << _log) < n) _log++;
                _cos = new float[n / 2];
                _sin = new float[n / 2];
                for (int i = 0; i < n / 2; i++) { double a = -2 * Math.PI * i / n; _cos[i] = (float)Math.Cos(a); _sin[i] = (float)Math.Sin(a); }
                _rev = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int r = 0;
                    for (int b = 0; b < _log; b++) if ((i & (1 << b)) != 0) r |= 1 << (_log - 1 - b);
                    _rev[i] = r;
                }
            }

            /// <summary>Spatial [y][x] → spectrum in the transposed layout [kx][ky].</summary>
            public void Forward2(float[] re, float[] im) { Rows(re, im, false); Transpose(re); Transpose(im); Rows(re, im, false); }

            /// <summary>Transposed spectrum [kx][ky] → spatial [y][x] (unscaled).</summary>
            public void Inverse2(float[] re, float[] im) { Rows(re, im, true); Transpose(re); Transpose(im); Rows(re, im, true); }

            void Rows(float[] re, float[] im, bool inverse) =>
                Parallel.For(0, _n, r => Row(re, im, r * _n, inverse));

            void Row(float[] re, float[] im, int o, bool inverse)
            {
                int n = _n;
                for (int i = 0; i < n; i++)
                {
                    int j = _rev[i];
                    if (j > i)
                    {
                        float t = re[o + i]; re[o + i] = re[o + j]; re[o + j] = t;
                        t = im[o + i]; im[o + i] = im[o + j]; im[o + j] = t;
                    }
                }
                float sg = inverse ? -1f : 1f;
                for (int len = 2; len <= n; len <<= 1)
                {
                    int half = len >> 1, stride = n / len;
                    for (int s = 0; s < n; s += len)
                        for (int k = 0; k < half; k++)
                        {
                            float wr = _cos[k * stride], wi = _sin[k * stride] * sg;
                            int a = o + s + k, b = a + half;
                            float xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                            re[b] = re[a] - xr; im[b] = im[a] - xi;
                            re[a] += xr; im[a] += xi;
                        }
                }
            }

            void Transpose(float[] a)
            {
                int n = _n;
                Parallel.For(0, n, y =>
                {
                    for (int x = y + 1; x < n; x++)
                    {
                        float t = a[y * n + x]; a[y * n + x] = a[x * n + y]; a[x * n + y] = t;
                    }
                });
            }
        }
    }
}
