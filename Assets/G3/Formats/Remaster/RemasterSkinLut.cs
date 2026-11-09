using System;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Pre-integrated skin scattering lookup (Penner and Borshukov, "Pre-Integrated Skin Shading", GPU Pro 2, 2011) for
    /// G3/Remaster/V2/Skin: the diffuse light a curved skin surface of radius r receives at a given N·L when light
    /// scatters under the surface with the measured skin diffusion profile (sum of six Gaussians, d'Eon and Luebke,
    /// GPU Gems 3 ch. 14). Flat skin (large r) gives Lambert; strongly curved skin (nose, ears, lids) carries red light
    /// past the terminator. The importer writes it once as a PNG (<see cref="ToRgba"/>); the shader samples
    /// (u = N·L × 0.5 + 0.5, v = <see cref="LutV"/>(curvature)) and squares the stored value.
    /// </summary>
    public static class RemasterSkinLut
    {
        /// <summary>Curvature (1/m) at v = 1: radius 2 mm (ear rim, nostril wing).</summary>
        public const double MaxCurvature = 500.0;

        // d'Eon and Luebke's three-layer skin fit: variance (mm²) and RGB weight of each Gaussian
        static readonly double[] Variance = { 0.0064, 0.0484, 0.187, 0.567, 1.99, 7.41 };
        static readonly double[,] Weight =
        {
            { 0.233, 0.455, 0.649 },
            { 0.100, 0.336, 0.344 },
            { 0.118, 0.198, 0.000 },
            { 0.113, 0.007, 0.007 },
            { 0.358, 0.004, 0.000 },
            { 0.078, 0.000, 0.000 },
        };

        /// <summary>The v coordinate for a curvature in 1/m: sqrt(c / <see cref="MaxCurvature"/>) (more rows for faces).</summary>
        public static double LutV(double curvaturePerMetre) =>
            Math.Sqrt(Math.Max(0.0, Math.Min(1.0, curvaturePerMetre / MaxCurvature)));

        /// <summary>Radius (mm) of the row at v (infinite at v = 0).</summary>
        public static double RadiusMm(double v) => v <= 0 ? double.PositiveInfinity : 1000.0 / (v * v * MaxCurvature);

        /// <summary>
        /// Diffusion profile along a line through the surface at distance d (mm), per channel: the marginal of the radial
        /// sum of Gaussians, i.e. 1D Gaussians of the same variances (the ring integral below runs along a line).
        /// </summary>
        public static void Profile(double dMm, double[] rgb)
        {
            rgb[0] = rgb[1] = rgb[2] = 0;
            for (int i = 0; i < Variance.Length; i++)
            {
                double g = Math.Exp(-dMm * dMm / (2 * Variance[i])) / Math.Sqrt(2 * Math.PI * Variance[i]);
                for (int c = 0; c < 3; c++) rgb[c] += Weight[i, c] * g;
            }
        }

        /// <summary>
        /// Scattered diffuse at N·L for a ring of radius <paramref name="radiusMm"/>: ∫ max(cos(θ + x), 0) R(2r sin(x/2)) dx
        /// / ∫ R(2r sin(x/2)) dx over x ∈ (−π, π]. Linear RGB, 0..1.
        /// </summary>
        public static void Integrate(double nDotL, double radiusMm, double[] rgb, int samples = 512)
        {
            double theta = Math.Acos(Math.Max(-1.0, Math.Min(1.0, nDotL)));
            if (double.IsInfinity(radiusMm) || radiusMm > 1e5)
            {
                double l = Math.Max(0.0, nDotL);
                rgb[0] = rgb[1] = rgb[2] = l;
                return;
            }
            double[] tot = new double[3], norm = new double[3], w = new double[3];
            for (int k = 0; k < samples; k++)
            {
                double x = -Math.PI + (k + 0.5) * (2 * Math.PI / samples);
                double diffuse = Math.Max(0.0, Math.Cos(theta + x));
                double dist = Math.Abs(2.0 * radiusMm * Math.Sin(x * 0.5));
                Profile(dist, w);
                for (int c = 0; c < 3; c++) { tot[c] += diffuse * w[c]; norm[c] += w[c]; }
            }
            for (int c = 0; c < 3; c++) rgb[c] = norm[c] > 0 ? Math.Max(0.0, Math.Min(1.0, tot[c] / norm[c])) : Math.Max(0.0, nDotL);
        }

        /// <summary>The table, linear RGB, row-major from v = 0 (row 0) to v = 1, u = N·L × 0.5 + 0.5.</summary>
        public static float[] Compute(int width, int height, int samples = 512)
        {
            var t = new float[width * height * 3];
            var rgb = new double[3];
            for (int y = 0; y < height; y++)
            {
                double v = height == 1 ? 0 : (double)y / (height - 1);
                double r = RadiusMm(v);
                for (int x = 0; x < width; x++)
                {
                    double nl = ((x + 0.5) / width) * 2 - 1;
                    Integrate(nl, r, rgb, samples);
                    int i = (y * width + x) * 3;
                    t[i] = (float)rgb[0]; t[i + 1] = (float)rgb[1]; t[i + 2] = (float)rgb[2];
                }
            }
            return t;
        }

        /// <summary>
        /// RGBA8 for a PNG, stored as sqrt(value) (more precision in the dark terminator; the shader squares it). Row 0 of
        /// the result is the TOP image row = v = 1 (PNG rows top first; Unity puts the top row at v = 1).
        /// </summary>
        public static byte[] ToRgba(float[] table, int width, int height)
        {
            var px = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int src = (y * width + x) * 3, dst = ((height - 1 - y) * width + x) * 4;
                    for (int c = 0; c < 3; c++)
                        px[dst + c] = (byte)Math.Round(255.0 * Math.Sqrt(Math.Max(0f, Math.Min(1f, table[src + c]))));
                    px[dst + 3] = 255;
                }
            return px;
        }
    }
}
