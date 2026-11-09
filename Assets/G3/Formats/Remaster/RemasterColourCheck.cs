using System;
using System.Globalization;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Colour fidelity of a remaster A/B still (Docs/remaster-pipeline.md §9.6): the mean colour of a screen region (face,
    /// torso) of the remastered render B must stay close to the original A — "the same character, better". Only
    /// character pixels count: a pixel is foreground when A or B differs from a background plate (the same camera with
    /// no character) by more than <see cref="BackgroundThreshold"/>. Means are taken in sRGB and compared in CIELAB
    /// (D65, CIE76 ΔE). Pure: shared by the Unity test scene capture and the tests.
    /// </summary>
    public static class RemasterColourCheck
    {
        /// <summary>
        /// Max ΔE76 of the region means. First in-engine captures (r20): Paladin, judged good, 3.4–3.5; Mercenary 5.3–5.8
        /// and hero 6.9–7.5 (bluer, b* −4…−5, greyer skin), judged off-tone.
        /// </summary>
        public const double MaxDeltaE = 4.0;

        /// <summary>Per-channel difference (0..255) above which a pixel is not background.</summary>
        public const int BackgroundThreshold = 12;

        /// <summary>Regions with fewer character pixels are skipped (too small to judge, e.g. the face at 30 m).</summary>
        public const int MinPixels = 400;

        public readonly struct Result
        {
            public readonly string Name;
            public readonly int Pixels;
            public readonly double[] MeanA, MeanB, LabA, LabB; // sRGB 0..255; L*, a*, b*
            public readonly double DeltaE;
            public readonly double MaxDeltaE;

            public Result(string name, int pixels, double[] meanA, double[] meanB, double maxDeltaE)
            {
                Name = name;
                Pixels = pixels;
                MeanA = meanA;
                MeanB = meanB;
                MaxDeltaE = maxDeltaE;
                if (meanA == null || meanB == null)
                {
                    LabA = LabB = null;
                    DeltaE = double.NaN;
                    return;
                }
                LabA = SrgbToLab(meanA[0], meanA[1], meanA[2]);
                LabB = SrgbToLab(meanB[0], meanB[1], meanB[2]);
                DeltaE = Math.Sqrt(Sq(LabB[0] - LabA[0]) + Sq(LabB[1] - LabA[1]) + Sq(LabB[2] - LabA[2]));
            }

            /// <summary>False when the region had too few character pixels (no verdict).</summary>
            public bool Judged => Pixels >= MinPixels && MeanA != null;

            public bool Pass => !Judged || DeltaE <= MaxDeltaE;

            public override string ToString()
            {
                var c = CultureInfo.InvariantCulture;
                if (!Judged) return $"{Name}: skipped ({Pixels} px)";
                return string.Format(c,
                    "{0}: A rgb({1:0},{2:0},{3:0}) B rgb({4:0},{5:0},{6:0}) dE {7:0.0} (dL {8:+0.0;-0.0} da {9:+0.0;-0.0} db {10:+0.0;-0.0}) {11} px {12}",
                    Name, MeanA[0], MeanA[1], MeanA[2], MeanB[0], MeanB[1], MeanB[2], DeltaE,
                    LabB[0] - LabA[0], LabB[1] - LabA[1], LabB[2] - LabA[2], Pixels, Pass ? "PASS" : $"FAIL (> {MaxDeltaE:0.0})");
            }
        }

        /// <summary>
        /// Compares the region [x0, x1) × [y0, y1) (clamped to the image) of A and B. Images are RGB24, row-major,
        /// <paramref name="width"/> × <paramref name="height"/>, all three in the same row order.
        /// </summary>
        public static Result Compare(string name, byte[] a, byte[] b, byte[] background, int width, int height,
                                     int x0, int y0, int x1, int y1, double maxDeltaE = MaxDeltaE)
        {
            if (a == null || b == null || background == null) throw new ArgumentNullException(a == null ? nameof(a) : b == null ? nameof(b) : nameof(background));
            int n3 = width * height * 3;
            if (a.Length < n3 || b.Length < n3 || background.Length < n3) throw new ArgumentException("image smaller than width × height × 3");
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(width, x1); y1 = Math.Min(height, y1);
            long ra = 0, ga = 0, ba = 0, rb = 0, gb = 0, bb = 0;
            int count = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * width + x) * 3;
                    if (!Differs(a, background, i) && !Differs(b, background, i)) continue;
                    ra += a[i]; ga += a[i + 1]; ba += a[i + 2];
                    rb += b[i]; gb += b[i + 1]; bb += b[i + 2];
                    count++;
                }
            if (count == 0) return new Result(name, 0, null, null, maxDeltaE);
            double k = 1.0 / count;
            return new Result(name, count, new[] { ra * k, ga * k, ba * k }, new[] { rb * k, gb * k, bb * k }, maxDeltaE);
        }

        static bool Differs(byte[] img, byte[] bg, int i) =>
            Math.Abs(img[i] - bg[i]) > BackgroundThreshold || Math.Abs(img[i + 1] - bg[i + 1]) > BackgroundThreshold ||
            Math.Abs(img[i + 2] - bg[i + 2]) > BackgroundThreshold;

        /// <summary>sRGB (0..255) to CIELAB (D65).</summary>
        public static double[] SrgbToLab(double r, double g, double b)
        {
            double lr = Linear(r / 255.0), lg = Linear(g / 255.0), lb = Linear(b / 255.0);
            double x = (0.4124 * lr + 0.3576 * lg + 0.1805 * lb) / 0.95047;
            double y = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
            double z = (0.0193 * lr + 0.1192 * lg + 0.9505 * lb) / 1.08883;
            double fx = F(x), fy = F(y), fz = F(z);
            return new[] { 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz) };
        }

        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double F(double t) => t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : 7.787 * t + 16.0 / 116.0;
        static double Sq(double v) => v * v;
    }
}
