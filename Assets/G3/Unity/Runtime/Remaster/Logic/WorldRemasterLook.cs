using System;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free parameters of the world remaster's look layer (Docs/remaster-world.md sections 7-8): sky clouds, soft
    /// wind, aerial perspective + distance clarity, colour grading, the SSAO boost and the global material multipliers.
    /// All numbers come from <see cref="Current"/> (a <see cref="WorldLookSettings"/>, loaded from G3WorldLook.json by
    /// <c>G3WorldRemasterLook</c>). Applied only while the world remaster switch is on.
    /// </summary>
    public static class WorldRemasterLook
    {
        static WorldLookSettings _current = WorldLookSettings.Default;

        /// <summary>The active settings (always clamped).</summary>
        public static WorldLookSettings Current
        {
            get => _current;
            set => _current = (value ?? WorldLookSettings.Default).Clamped();
        }

        /// <summary>
        /// fix-171: copies <paramref name="value"/> (clamped) into the object <see cref="Current"/> already is, so code that
        /// holds the reference (the runner's A/B, cached settings) sees the change (lighting quality switches).
        /// </summary>
        public static void Overwrite(WorldLookSettings value)
        {
            var v = (value ?? WorldLookSettings.Default).Clamped();
            foreach (var f in typeof(WorldLookSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                f.SetValue(_current, f.GetValue(v));
        }

        // _G3RemasterSky: x cloud amount (0 = no clouds), y base coverage, z drift (plane units/s), w scale
        public static float[] Sky => new[] { _current.clouds ? _current.cloudAmount : 0f, _current.cloudCoverage, _current.cloudDrift, _current.cloudScale };

        // _G3RemasterWind: x rolling grass waves (m per m of plant height), y extra leaf rock (fraction), z wave length (m), w speed (m/s)
        public static float[] Wind => new[] { _current.windWaves, _current.windLeafRock, _current.windWaveLength, _current.windSpeed };

        /// <summary>
        /// _G3RemasterWind for the parts that are on (fix-71): the grass waves (x, G3Wind.hlsl) belong to the grass part, the
        /// extra leaf rock (y, G3SpeedTree.hlsl) to the trees; wave length and speed stay set (unused while x is 0).
        /// </summary>
        public static float[] WindFor(bool grass, bool trees) => new[]
        {
            grass ? _current.windWaves : 0f, trees ? _current.windLeafRock : 0f, _current.windWaveLength, _current.windSpeed,
        };

        public static float AerialMax => _current.aerialMax;

        // Material tuning globals (G3RemasterTune.hlsl). Unset globals read as 0 in the shaders, so every vector is stored
        // as an offset from its neutral value: the remaster materials look exactly as baked when nothing was set.

        /// <summary>_G3RemasterDetailTune: macro, anti-tile, meso, triplanar multipliers, each minus 1.</summary>
        public static float[] DetailTune => new[] { _current.macro - 1, _current.antiTile - 1, _current.meso - 1, _current.triplanar - 1 };

        /// <summary>_G3RemasterSurfaceTune: normal and occlusion multipliers minus 1, cavity, variance-preserving anti-tile.</summary>
        public static float[] SurfaceTune => new[] { _current.normal - 1, _current.occlusion - 1, _current.cavity, _current.antiTileContrast };

        /// <summary>_G3RemasterFadeTune: meso fade start - 20 m, end - 60 m (r1 values are neutral), bark multiplier - 1, unused.</summary>
        public static float[] FadeTune => new[] { _current.mesoFadeStart - 20, _current.mesoFadeEnd - 60, _current.bark - 1, 0 };

        /// <summary>_G3RemasterFoliageTune: translucency multiplier - 1, back-light wrap, sheen, unused.</summary>
        public static float[] FoliageTune => new[] { _current.translucency - 1, _current.translucencyWrap, _current.foliageSheen, 0 };

        /// <summary>
        /// Aerial perspective amount (0..aerialMax) for a surface <paramref name="distance"/> metres from the camera,
        /// camera and surface heights relative to the reference height (clamped at 0: nothing below the reference gets
        /// denser haze, so views from high ground do not grey out): exponential height fog integrated along the ray.
        /// Mirrors the shader.
        /// </summary>
        public static float AerialAmount(float distance, float camHeight, float pointHeight)
        {
            if (distance <= 0 || !_current.aerial) return 0;
            float k = _current.aerialFalloff;
            float h0 = Math.Max(camHeight, 0f), h1 = Math.Max(pointHeight, 0f);
            float dh = h1 - h0;
            // mean of exp(-k h) along the segment
            float mean = Math.Abs(dh) < 1e-3f || k < 1e-6f
                ? (float)Math.Exp(-k * h0)
                : (float)((Math.Exp(-k * h0) - Math.Exp(-k * h1)) / (k * dh));
            float od = _current.aerialDensity * distance * mean;
            return Math.Min(1f - (float)Math.Exp(-od), _current.aerialMax);
        }

        /// <summary>Distance clarity weight (0..1) at <paramref name="distance"/> m: smoothstep from clarityStart to clarityFull.</summary>
        public static float ClarityWeight(float distance)
        {
            if (!_current.clarity) return 0;
            float t = Clamp01((distance - _current.clarityStart) / Math.Max(_current.clarityFull - _current.clarityStart, 1f));
            return t * t * (3 - 2 * t);
        }

        /// <summary>
        /// _G3Grade of the grade pass (Hidden/G3/Remaster/Atmosphere pass 1): contrast and saturation as fractions
        /// (gradeContrast / 100, gradeSaturation / 100), black lift and gain in display space; all 0 when grade is off.
        /// </summary>
        public static float[] Grade => _current.grade
            ? new[] { _current.gradeContrast / 100f, _current.gradeSaturation / 100f, _current.gradeShadows, _current.gradeGain }
            : new float[4];

        /// <summary>
        /// The grade of one linear scene colour, mirroring the shader: saturation around the luminance, contrast in log
        /// space around 0.18 on the luminance (hue kept), then lift/gain in display space (gamma 2.2).
        /// </summary>
        public static float[] GradeColor(float r, float g, float b)
        {
            var p = Grade;
            double[] c = { Math.Max(r, 0), Math.Max(g, 0), Math.Max(b, 0) };
            double l = Lum(c);
            for (int i = 0; i < 3; i++) c[i] = Math.Max(l + (c[i] - l) * (1 + p[1]), 0);
            l = Lum(c);
            if (l > 1e-6)
            {
                double k = 0.18 * Math.Pow(l / 0.18, 1 + p[0]) / l;
                for (int i = 0; i < 3; i++) c[i] *= k;
            }
            var o = new float[3];
            for (int i = 0; i < 3; i++)
            {
                double d = Math.Pow(c[i], 1 / 2.2);
                d = (d + p[2] * (1 - Math.Min(d, 1))) * (1 + p[3]);
                o[i] = (float)Math.Pow(Math.Max(d, 0), 2.2);
            }
            return o;
        }

        /// <summary>
        /// grade-1 (section 27): _G3Vibrance of the grade pass - x global saturation offset (json <c>saturation</c>, the
        /// options' Saturation), y soft-limit strength (<c>vibrance</c>), z knee (<c>vibranceKnee</c>); applied in every
        /// lighting mode as the grade's last colour step (display space, before lift/gain).
        /// </summary>
        public static float[] VibranceGlobal => new[] { _current.saturation, _current.vibrance, _current.vibranceKnee, 0f };

        /// <summary>
        /// The shader's G3Vibrance on one display-space colour (0..1): saturation around the luminance by
        /// <paramref name="sat"/>, then the HSV saturation s above <paramref name="knee"/> compressed,
        /// s' = knee + (1 - knee) x (1 - vib/2 x), x = (s - knee) / (1 - knee), keeping the max channel.
        /// </summary>
        public static float[] Vibrance(float r, float g, float b, float sat, float vib, float knee)
        {
            double[] d = { r, g, b };
            double l = Lum(d);
            for (int i = 0; i < 3; i++) d[i] = Math.Max(l + (d[i] - l) * (1 + sat), 0);
            double mx = Math.Max(d[0], Math.Max(d[1], d[2])), mn = Math.Min(d[0], Math.Min(d[1], d[2]));
            double s = (mx - mn) / Math.Max(mx, 1e-4);
            if (vib > 0 && s > knee)
            {
                double x = Math.Min(1, Math.Max(0, (s - knee) / Math.Max(1 - knee, 1e-3)));
                double s2 = knee + (1 - knee) * x * (1 - 0.5 * vib * x);
                for (int i = 0; i < 3; i++) d[i] = mx - (mx - d[i]) * (s2 / s);
            }
            return new[] { (float)d[0], (float)d[1], (float)d[2] };
        }

        static double Lum(double[] c) => 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];

        /// <summary>Cloud coverage of the remaster sky for G3's overcast (CloudThickness 0..1): base + 0.6 x overcast.</summary>
        public static float CloudCoverage(float overcast) => Clamp01(_current.cloudCoverage + Clamp01(overcast) * 0.6f);

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
