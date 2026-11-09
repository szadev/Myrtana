using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace G3.Unity
{
    /// <summary>Knobs of the dune bake (<see cref="DuneShape"/>; G3Cli <c>dune bake</c>). Changing them needs a re-bake.</summary>
    public sealed class DuneBakeSettings
    {
        public float WindAzimuth = 60f;     // degrees, G3Wind / sandWindAngle (0 = towards +Z, 90 = +X): the ripples' frame
        public float Cap = DuneShape.MaxChange;   // m: largest change of the ground (soft, tanh)
        public float SharpCap = 1.2f;       // m: largest crest / slip-face raise of one dune (the whole dune is scaled, not clipped)
        public float BaseBlur = 3f;         // m: G3's facets smoothed before the shape is read
        public float Reach = 14f;           // m: how far a flank's tangent is extended over the crest
        public float LeeGain = 1.0f;        // lee flank slope x this (towards the slip face) ...
        public float Repose = 0.65f;        // ... at most tan 33 deg (angle of repose)
        public float WindwardGain = 1.0f;   // windward tangent slope x this
        public float ScaleRadius = 18f;     // m: the sharpening scale is taken over this neighbourhood (one dune)
        public float DuneletAmp = 0.75f;    // m: secondary dunes on flat open sand (crest to trough)
        public float DuneletWave = 20f;     // m: their mean spacing along the wind
        public float DuneletStoss = 0.76f;  // share of the wavelength on the windward (stoss) side
        public float FlatStart = 0.07f, FlatEnd = 0.22f;   // |slope| where the dunelets fade (4 .. 12 deg)
        public float MegaAmp = 0.05f;       // m: metre-scale undulation (megaripples) on all dune sand
        public float MegaWave = 5.5f;       // m
        public float CoarseSigma = 1.0f;    // m: the coarse band (C) = the change blurred by this (wavelengths >= ~4 m)
        public float NormalSigma = 0.6f;    // m: the slope of the change is smoothed by this
        public float AboveCut = 0f;         // x (G3 - smoothed) subtracted from the raise (1: no overshoot on G3's sharp crests, but G3's
                                            // 6 m facets print into the change and its shading); 0: the raise is smooth
        public uint Seed = 23;
    }

    /// <summary>One protection layer: cells where the ground must not move, zero within <see cref="Zero"/> m of them, full from <see cref="Full"/> m.</summary>
    public struct DuneProtect
    {
        public string Name;
        public bool[] Cells;
        public float Zero, Full;
        public DuneProtect(string name, bool[] cells, float zero, float full) { Name = name; Cells = cells; Zero = zero; Full = full; }
    }

    /// <summary>Statistics of one bake (G3Cli output, the handoff).</summary>
    public sealed class DuneBakeStats
    {
        public long SandCells, DuneCells, Raised, Lowered;
        public float MaxUp, MaxDown;
        public double SumAbs;
        public readonly Dictionary<string, long> Protected = new Dictionary<string, long>();
        public override string ToString() =>
            $"sand cells {SandCells}, dune cells (W > 0) {DuneCells}, |change| mean {(DuneCells > 0 ? SumAbs / DuneCells : 0):F3} m, max +{MaxUp:F2} / -{MaxDown:F2} m, " +
            $"raised > 5 cm {Raised}, lowered > 5 cm {Lowered}; protected: " + string.Join(", ", ProtectedList());
        IEnumerable<string> ProtectedList() { foreach (var kv in Protected) yield return $"{kv.Key} {kv.Value}"; }
    }

    /// <summary>
    /// dune-1 (Docs/remaster-world.md section 23): the offline dune shaping of Varant's loose sand, engine-free. Input: G3's
    /// collision height on a grid (top surface), the loose-sand weight and protection layers; output: a <see cref="DuneField"/>.
    /// <para>Shape: G3's dunes are soft blobs (6 m facets, Phong). Along the prevailing wind <i>w</i> every point is lifted to the
    /// lower of two tangent extensions: the windward flank's tangent carried downwind over the crest, and the lee flank's
    /// tangent, steepened towards the angle of repose, carried upwind from the lee; the two meet in a sharp brink with a
    /// slip face below it, a gentle stoss side, concave parts (troughs) untouched. Each dune's raise is scaled to
    /// <see cref="DuneBakeSettings.SharpCap"/> as a whole (keeps the profile, no clipped plateaus). Flat open sand gets
    /// barchanoid dunelets (asymmetric profile, crests across the wind, wandering and breaking), all sand a metre-scale
    /// undulation. Everything is faded to exactly 0 within the protection margins and soft-capped at
    /// <see cref="DuneBakeSettings.Cap"/>.</para>
    /// </summary>
    public static class DuneShape
    {
        /// <summary>The largest change of the ground the bake makes (m; <see cref="DuneBakeSettings.Cap"/>, soft).</summary>
        public const float MaxChange = 1.5f;

        /// <summary>
        /// Loose-sand weight of a terrain material at a vertex with vertex alpha <paramref name="alpha"/> (0..1): G3 blends
        /// lerp(A, B, VC.a), layer A weight 1 - alpha. Varant's dune materials: Desert_Ground_01 (PlainSand + WaveSand, both
        /// loose: 1), PlainSand_To_Rock / _To_Wetsand / _To_Drysand / _To_Gravel (A = PlainSand_01: 1 - alpha); paths, rock,
        /// wet sand, everything else 0.
        /// </summary>
        public static float SandWeight(string material, float alpha)
        {
            if (string.IsNullOrEmpty(material)) return 0;
            if (material.IndexOf("Path", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (material.IndexOf("Desert_Ground_01", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (material.IndexOf("Desert_PlainSand_To", StringComparison.OrdinalIgnoreCase) >= 0 ||
                material.IndexOf("Plainsand_zu_", StringComparison.OrdinalIgnoreCase) >= 0)
                return Math.Max(0, Math.Min(1, 1 - alpha));
            return 0;
        }

        public sealed class Grid
        {
            public int Nx, Nz;
            public float X0, Z0, Res = 1f;
            public float[] H;       // G3 height (top terrain surface), NaN = no terrain
            public float[] S;       // loose-sand weight 0..1
            public int Count => Nx * Nz;
        }

        public static DuneField Build(Grid g, IList<DuneProtect> protect, DuneBakeSettings s, out DuneBakeStats stats, float[] debugD = null)
        {
            stats = new DuneBakeStats();
            int nx = g.Nx, nz = g.Nz, n = nx * nz;
            float res = g.Res;
            double az = s.WindAzimuth * Math.PI / 180;
            float wx = (float)Math.Sin(az), wz = (float)Math.Cos(az);

            // base: G3's height smoothed (normalised convolution over the valid cells)
            var valid = new float[n];
            var h0 = new float[n];
            for (int k = 0; k < n; k++) { bool v = !float.IsNaN(g.H[k]); valid[k] = v ? 1 : 0; h0[k] = v ? g.H[k] : 0; }
            var hs = Blur(h0, nx, nz, s.BaseBlur / res);
            var vs = Blur(valid, nx, nz, s.BaseBlur / res);
            for (int k = 0; k < n; k++) hs[k] = vs[k] > 1e-3f ? hs[k] / vs[k] : 0;
            // the tent is built on the smoothed surface (tangents of single facets would streak along the wind)
            var hb = hs;

            // where dunes may form: sand (smoothed) x every protection layer's fade (exactly 0 inside its zero margin)
            var sand = Blur(g.S, nx, nz, 1.5f / res);
            var m = new float[n];
            for (int k = 0; k < n; k++)
            {
                m[k] = valid[k] > 0 ? ReliefNoise.SmoothStep(0.35f, 0.8f, sand[k]) : 0;
                if (g.S[k] > 0.5f) stats.SandCells++;
            }
            var hard = new bool[n];   // inside a zero margin (or no terrain): the ground keeps G3's height exactly
            for (int k = 0; k < n; k++) hard[k] = valid[k] == 0;
            foreach (var p in protect)
            {
                var d = Distance(p.Cells, nx, nz);
                long cnt = 0;
                for (int k = 0; k < n; k++)
                {
                    float dm = d[k] * res;
                    if (dm <= p.Zero) { if (!hard[k]) cnt++; hard[k] = true; m[k] = 0; continue; }
                    m[k] *= ReliefNoise.SmoothStep(p.Zero, p.Full, dm);
                }
                stats.Protected[p.Name] = cnt;
            }
            for (int k = 0; k < n; k++) if (hard[k]) m[k] = 0;

            // slope along the wind (dh/ds, s downwind) and the slope magnitude
            var gw = new float[n]; var gm = new float[n];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    float dx = (hs[j * nx + Math.Min(nx - 1, i + 1)] - hs[j * nx + Math.Max(0, i - 1)]) / ((Math.Min(nx - 1, i + 1) - Math.Max(0, i - 1)) * res);
                    float dz = (hs[Math.Min(nz - 1, j + 1) * nx + i] - hs[Math.Max(0, j - 1) * nx + i]) / ((Math.Min(nz - 1, j + 1) - Math.Max(0, j - 1)) * res);
                    gw[k] = dx * wx + dz * wz;
                    gm[k] = (float)Math.Sqrt(dx * dx + dz * dz);
                }

            // crest sharpening: the tent of the windward and the (steepened) lee tangents
            var raise = new float[n];
            const float step = 0.5f;   // cells per tangent step (finer than the grid: no streaks where the max switches points)
            int steps = Math.Max(1, (int)Math.Round(s.Reach / res / step));
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    if (m[k] <= 0) continue;
                    float x = i, z = j, h = hb[k];
                    float up = h, dn = h;
                    for (int a = 1; a <= steps; a++)
                    {
                        float dist = a * step * res;
                        // upwind point: its tangent carried downwind to here
                        float ux = x - a * step * wx, uz = z - a * step * wz;
                        if (ux >= 0 && uz >= 0 && ux <= nx - 1 && uz <= nz - 1)
                        {
                            float hu = Bilinear(hb, nx, nz, ux, uz), su = Bilinear(gw, nx, nz, ux, uz);
                            float e = hu + dist * su * s.WindwardGain;
                            if (e > up) up = e;
                        }
                        // downwind point: its tangent carried upwind; a lee flank (falling downwind) steepened towards repose
                        float dx2 = x + a * step * wx, dz2 = z + a * step * wz;
                        if (dx2 >= 0 && dz2 >= 0 && dx2 <= nx - 1 && dz2 <= nz - 1)
                        {
                            float hd = Bilinear(hb, nx, nz, dx2, dz2), sd = Bilinear(gw, nx, nz, dx2, dz2);
                            float lee = -sd;
                            if (lee > 0) lee = Math.Max(lee, Math.Min(lee * s.LeeGain, s.Repose));
                            float e = hd + dist * lee;
                            if (e > dn) dn = e;
                        }
                    }
                    // where G3's own surface is above the smoothed one (its crest / slip face is already sharp) that much less
                    float above = valid[k] > 0 && s.AboveCut > 0 ? s.AboveCut * Math.Max(0, g.H[k] - h) : 0;
                    raise[k] = Math.Max(0, Math.Min(up, dn) - h - above);
                }
            });
            // scale each dune's raise as a whole to SharpCap
            var rmax = MaxFilter(raise, nx, nz, (int)Math.Round(s.ScaleRadius / res));
            rmax = Blur(rmax, nx, nz, s.ScaleRadius / 3 / res);

            // the change: crest raise, dunelets on flat sand, megaripples; soft-capped, x the weight (exactly 0 where hard)
            var dOut = debugD ?? new float[n];
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    if (m[k] <= 0) { dOut[k] = 0; continue; }
                    float k1 = rmax[k] > s.SharpCap ? s.SharpCap / rmax[k] : 1;
                    float r = raise[k] * k1;
                    float x = g.X0 + i * res, z = g.Z0 + j * res;
                    float flat = 1 - ReliefNoise.SmoothStep(s.FlatStart, s.FlatEnd, gm[k]);
                    float dl = flat > 0 ? flat * Dunelets(x, z, wx, wz, s) : 0;
                    float mega = Mega(x, z, wx, wz, s);
                    float d = r + dl + mega;
                    dOut[k] = m[k] * s.Cap * (float)Math.Tanh(d / s.Cap);
                }
            });

            // bands: C = the change blurred (wavelengths >= ~4 m), B = the rest
            var cBand = Blur(dOut, nx, nz, s.CoarseSigma / res);
            var bBand = new float[n];
            for (int k = 0; k < n; k++)
            {
                if (m[k] <= 0) { cBand[k] = 0; continue; }
                bBand[k] = dOut[k] - cBand[k];
                float v = dOut[k];
                stats.DuneCells++;
                stats.SumAbs += Math.Abs(v);
                if (v > stats.MaxUp) stats.MaxUp = v;
                if (-v > stats.MaxDown) stats.MaxDown = -v;
                if (v > 0.05f) stats.Raised++; else if (v < -0.05f) stats.Lowered++;
            }
            // the slope of the change (not of G3's surface: its 6 m facets would show as flat patches): the relief adds it to
            // the shading normal of G3's smooth vertex normals, so the raised crests, slip faces and dunelets are shaded
            var fin = dOut;
            var sx = new float[n]; var sz = new float[n];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    int i0 = Math.Max(0, i - 1), i1 = Math.Min(nx - 1, i + 1), j0 = Math.Max(0, j - 1), j1 = Math.Min(nz - 1, j + 1);
                    sx[k] = (fin[j * nx + i1] - fin[j * nx + i0]) / ((i1 - i0) * res);
                    sz[k] = (fin[j1 * nx + i] - fin[j0 * nx + i]) / ((j1 - j0) * res);
                }
            sx = Blur(sx, nx, nz, s.NormalSigma / res); sz = Blur(sz, nx, nz, s.NormalSigma / res);
            var f = DuneField.Empty(g.X0, g.Z0, nx, nz, res, s.WindAzimuth);
            f.Fill(cBand, bBand, m, sx, sz);
            return f;
        }

        /// <summary>
        /// Barchanoid dunelets (m): an asymmetric profile along the wind (gentle stoss rising to a sharp brink, a steeper lee down
        /// to a rounded trough), crest lines warped and broken across the wind, amplitude patchy; mean about 0.
        /// </summary>
        public static float Dunelets(float x, float z, float wx, float wz, DuneBakeSettings s)
        {
            float u = x * wx + z * wz;          // along the wind
            float v = -x * wz + z * wx;         // across
            uint seed = s.Seed;
            float warp = 0.45f * Fbm(v / 55f, u / 110f, seed + 1, 3) + 0.12f * Fbm(v / 14f, u / 28f, seed + 2, 2);
            float lambda = s.DuneletWave * (1 + 0.25f * Fbm(x / 260f, z / 260f, seed + 3, 2));
            float phase = u / lambda + warp;
            float t = phase - (float)Math.Floor(phase);
            float ts = s.DuneletStoss, p;
            if (t < ts) { float a = t / ts; p = 2 * a * a - a * a * a; }       // trough slope 0 .. brink slope > 0
            else { float b = (t - ts) / (1 - ts); p = (1 - b) * (1 - b); }       // steep at the brink .. 0 at the trough
            // amplitude: patches (some plains rippled, some smooth) and crests that fade along their length (barchanoid)
            float patch = ReliefNoise.SmoothStep(-0.25f, 0.45f, Fbm(x / 140f, z / 140f, seed + 4, 3));
            float along = ReliefNoise.SmoothStep(-0.5f, 0.35f, Fbm(v / 38f, (float)Math.Floor(phase) * 0.37f, seed + 5, 2));
            return s.DuneletAmp * (0.15f + 0.85f * patch) * (0.25f + 0.75f * along) * (p - 0.4f);
        }

        /// <summary>Metre-scale undulation (megaripples): a softer asymmetric wave across the wind, wandering.</summary>
        public static float Mega(float x, float z, float wx, float wz, DuneBakeSettings s)
        {
            if (s.MegaAmp <= 0) return 0;
            float u = x * wx + z * wz, v = -x * wz + z * wx;
            float phase = u / s.MegaWave + 0.6f * Fbm(v / 9f, u / 17f, s.Seed + 7, 2);
            float t = phase - (float)Math.Floor(phase);
            float p = t < 0.7f ? (float)Math.Sin(t / 0.7f * Math.PI * 0.5) : 1 - (t - 0.7f) / 0.3f;
            float amp = 0.5f + 0.5f * ReliefNoise.SmoothStep(-0.3f, 0.4f, Fbm(x / 45f, z / 45f, s.Seed + 8, 2));
            return s.MegaAmp * amp * (p - 0.55f);
        }

        static float Fbm(float x, float y, uint seed, int octaves)
        {
            float sum = 0, a = 1, norm = 0;
            for (int o = 0; o < octaves; o++, a *= 0.5f, x *= 2.03f, y *= 2.03f)
            {
                sum += a * ReliefNoise.Value3(x, y, 0.5f + o * 7.1f, seed);
                norm += a;
            }
            return sum / norm;
        }

        // ------------------------------------------------------------------ grid helpers

        public static float Bilinear(float[] a, int nx, int nz, float x, float z)
        {
            int i = Math.Max(0, Math.Min(nx - 2, (int)x)), j = Math.Max(0, Math.Min(nz - 2, (int)z));
            float u = x - i, v = z - j;
            int k = j * nx + i;
            return (1 - v) * ((1 - u) * a[k] + u * a[k + 1]) + v * ((1 - u) * a[k + nx] + u * a[k + nx + 1]);
        }

        /// <summary>Separable Gaussian (sigma in cells, clamped edges).</summary>
        public static float[] Blur(float[] src, int nx, int nz, float sigma)
        {
            int n = nx * nz;
            var outp = new float[n];
            if (sigma < 0.3f) { Array.Copy(src, outp, n); return outp; }
            int r = (int)Math.Ceiling(sigma * 3);
            var w = new float[2 * r + 1];
            float ws = 0;
            for (int i = -r; i <= r; i++) { w[i + r] = (float)Math.Exp(-0.5 * i * i / (sigma * sigma)); ws += w[i + r]; }
            for (int i = 0; i < w.Length; i++) w[i] /= ws;
            var tmp = new float[n];
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    float acc = 0;
                    for (int t = -r; t <= r; t++) acc += w[t + r] * src[j * nx + Math.Max(0, Math.Min(nx - 1, i + t))];
                    tmp[j * nx + i] = acc;
                }
            });
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    float acc = 0;
                    for (int t = -r; t <= r; t++) acc += w[t + r] * tmp[Math.Max(0, Math.Min(nz - 1, j + t)) * nx + i];
                    outp[j * nx + i] = acc;
                }
            });
            return outp;
        }

        /// <summary>Separable max over a square of half-size <paramref name="r"/> cells.</summary>
        public static float[] MaxFilter(float[] src, int nx, int nz, int r)
        {
            int n = nx * nz;
            var tmp = new float[n]; var outp = new float[n];
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    float mx = 0;
                    for (int t = Math.Max(0, i - r); t <= Math.Min(nx - 1, i + r); t++) mx = Math.Max(mx, src[j * nx + t]);
                    tmp[j * nx + i] = mx;
                }
            });
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    float mx = 0;
                    for (int t = Math.Max(0, j - r); t <= Math.Min(nz - 1, j + r); t++) mx = Math.Max(mx, tmp[t * nx + i]);
                    outp[j * nx + i] = mx;
                }
            });
            return outp;
        }

        /// <summary>Distance (cells) to the nearest marked cell: 8-neighbour chamfer (1, sqrt 2), two passes; 1e9 when none.</summary>
        public static float[] Distance(bool[] marked, int nx, int nz)
        {
            var d = new float[nx * nz];
            for (int k = 0; k < d.Length; k++) d[k] = marked[k] ? 0 : 1e9f;
            const float D1 = 1f, D2 = 1.41421356f;
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i; float v = d[k];
                    if (i > 0) v = Math.Min(v, d[k - 1] + D1);
                    if (j > 0) { v = Math.Min(v, d[k - nx] + D1); if (i > 0) v = Math.Min(v, d[k - nx - 1] + D2); if (i < nx - 1) v = Math.Min(v, d[k - nx + 1] + D2); }
                    d[k] = v;
                }
            for (int j = nz - 1; j >= 0; j--)
                for (int i = nx - 1; i >= 0; i--)
                {
                    int k = j * nx + i; float v = d[k];
                    if (i < nx - 1) v = Math.Min(v, d[k + 1] + D1);
                    if (j < nz - 1) { v = Math.Min(v, d[k + nx] + D1); if (i < nx - 1) v = Math.Min(v, d[k + nx + 1] + D2); if (i > 0) v = Math.Min(v, d[k + nx - 1] + D2); }
                    d[k] = v;
                }
            return d;
        }
    }
}
