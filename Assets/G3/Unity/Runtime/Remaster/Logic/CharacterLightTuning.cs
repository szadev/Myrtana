using System;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free tuning of the character close-up light rig (<c>G3CharacterLightRig</c>, Docs/remaster-characters.md
    /// "v2 hero showcase"): fill and rim scaled to the scene's own light so faces read in G3's lighting without looking
    /// lit by a studio. The rig's fixed values (fill 0.8, rim 3.0) were tuned on the remaster test scene's light; in the
    /// game they wash faces out at night and vanish under a noon sun.
    /// <list type="bullet">
    /// <item>Fill lifts the shadow side to <see cref="ShadowSideShare"/> of the lit side: lit = sun on the face + ambient,
    /// shadow = ambient (+ fill), so fill = max(0, share × lit − ambient), within [<see cref="FillMin"/>, <see cref="FillMax"/>].</item>
    /// <item>Rim scales with the scene level (it must stand out against the background, not glow) and drops when the sun
    /// already back-lights the subject (a natural rim), within [<see cref="RimMin"/>, <see cref="RimMax"/>].</item>
    /// <item>Colours: the fill is a warm white, the rim takes the sun's tint by day and the sky's by night.</item>
    /// </list>
    /// </summary>
    public static class CharacterLightTuning
    {
        public const float ShadowSideShare = 0.35f;
        public const float FillMin = 0.12f, FillMax = 1.2f;
        public const float RimMin = 0.5f, RimMax = 3.5f;
        public const float RimPerLevel = 1.6f;
        /// <summary>How much of the rim a full back light (sun behind the subject, facing the camera) removes.</summary>
        public const float BacklitRimCut = 0.6f;

        public struct Result
        {
            public float FillIntensity, RimIntensity;
            public float[] FillColor, RimColor; // linear RGB
        }

        /// <param name="sunIntensity">main light intensity (0 at night / indoors without sun)</param>
        /// <param name="sunElevationDeg">sun angle above the horizon</param>
        /// <param name="sunColor">main light colour (linear RGB)</param>
        /// <param name="ambientLuminance">ambient (SH) luminance around the subject</param>
        /// <param name="ambientColor">ambient sky colour (linear RGB)</param>
        /// <param name="backlit">0..1: how much the sun shines from behind the subject towards the camera</param>
        public static Result Compute(float sunIntensity, float sunElevationDeg, float[] sunColor, float ambientLuminance,
                                     float[] ambientColor, float backlit)
        {
            float elev = Math.Max(0f, (float)Math.Sin(sunElevationDeg * Math.PI / 180.0));
            float sun = Math.Max(0f, sunIntensity) * elev;
            float amb = Math.Max(0f, ambientLuminance);
            float lit = sun + amb;
            float fill = Clamp(ShadowSideShare * lit - amb, FillMin, FillMax);
            float b = Clamp(backlit, 0f, 1f);
            float rim = Clamp(RimPerLevel * lit * (1f - BacklitRimCut * b), RimMin, RimMax);
            // rim tint: the sun's by day, the sky's at night (weighted by their share of the level)
            float dayShare = lit > 1e-4f ? sun / lit : 0f;
            var sc = Normalised(sunColor ?? new[] { 1f, 0.95f, 0.88f });
            var ac = Normalised(ambientColor ?? new[] { 0.7f, 0.8f, 1f });
            var rimColor = new float[3];
            for (int i = 0; i < 3; i++) rimColor[i] = dayShare * sc[i] + (1f - dayShare) * ac[i];
            return new Result
            {
                FillIntensity = fill,
                RimIntensity = rim,
                FillColor = new[] { 1f, 0.93f, 0.85f },
                RimColor = Normalised(rimColor),
            };
        }

        /// <summary>Colour scaled so its brightest channel is 1 (black stays a neutral white).</summary>
        public static float[] Normalised(float[] c)
        {
            float m = Math.Max(c[0], Math.Max(c[1], c[2]));
            return m > 1e-5f ? new[] { c[0] / m, c[1] / m, c[2] / m } : new[] { 1f, 1f, 1f };
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
