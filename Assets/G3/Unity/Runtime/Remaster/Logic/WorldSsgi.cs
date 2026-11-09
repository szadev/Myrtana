using System;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free maths of the remaster's screen-space indirect light (Docs/remaster-world.md section 19, agent
    /// remaster-ssgi): visibility-bitmask horizon tracing (Therrien, Levesque, Gilet 2023, "Screen Space Indirect Lighting
    /// with Visibility Bitmask", The Visual Computer 39) at half resolution under Modern lighting. Per pixel a few screen
    /// slices are marched; every depth sample is a slab of constant thickness that occludes the angular sectors between its
    /// front and its back (32 sectors per slice, one bit each). Sectors newly occluded by a sample take the sky light away
    /// (AO on G3's fill) and give that sample's radiance instead (colour bleeding). Accumulated over frames (rotated
    /// slices) and bilaterally filtered. Mirrored by Hidden/G3/Remaster/Ssgi (tested here).
    /// </summary>
    public static class WorldSsgi
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        /// <summary>Sectors per slice (one uint).</summary>
        public const int Sectors = 32;

        /// <summary>True when the screen-space pass runs: Modern lighting is applied and the JSON switch is on.</summary>
        public static bool Wanted => WorldGi.Modern && S.v2Ssgi && (S.v2SsgiAo > 0 || S.v2SsgiGain > 0);

        /// <summary>
        /// Bits of the sectors covered by the angular interval [u0, u1] of the hemisphere (0 = one horizon of the slice,
        /// 1 = the other). A sample must touch a sector to occupy it (ceil), as in the paper's reference code.
        /// </summary>
        public static uint SectorMask(float u0, float u1)
        {
            float a = Clamp01(Math.Min(u0, u1)), b = Clamp01(Math.Max(u0, u1));
            int start = (int)Math.Min(Sectors - 1, Math.Floor(a * Sectors));
            int count = (int)Math.Ceiling((b - a) * Sectors);
            count = Math.Max(0, Math.Min(count, Sectors - start));
            if (count <= 0) return 0;
            uint m = count >= 32 ? 0xFFFFFFFFu : (1u << count) - 1u;
            return m << start;
        }

        /// <summary>Number of set bits.</summary>
        public static int CountBits(uint v)
        {
            v = v - ((v >> 1) & 0x55555555u);
            v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
            return (int)((((v + (v >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
        }

        /// <summary>
        /// Position of a direction in the slice hemisphere: <paramref name="theta"/> = its signed angle from the view
        /// vector in the slice plane (radians, positive towards the slice tangent), <paramref name="normalAngle"/> = the
        /// projected normal's. 0 and 1 are the two horizons of the hemisphere around the normal.
        /// </summary>
        public static float HemiUnit(float theta, float normalAngle) => Clamp01((theta - normalAngle + (float)Math.PI * 0.5f) / (float)Math.PI);

        /// <summary>
        /// Cosine-weighted share of the hemisphere of <paramref name="bits"/> new sectors at angle
        /// <paramref name="alpha"/> from the normal: each sector spans pi/32, the cosine lobe integrates to 1 over the slice.
        /// </summary>
        public static float CosShare(int bits, float alpha) => bits * ((float)Math.PI / (2 * Sectors)) * Math.Max(0f, (float)Math.Cos(alpha));

        /// <summary>World thickness of a depth sample at view distance <paramref name="dist"/> (grows linearly beyond 15 m, as the paper suggests).</summary>
        public static float Thickness(float dist) => S.v2SsgiThickness * Math.Max(1f, dist / 15f);

        /// <summary>Projected march radius in half-res pixels: world radius x projection scale / depth, capped.</summary>
        public static float RadiusPixels(float dist, float projScale) => Math.Min(S.v2SsgiMaxPixels, S.v2SsgiRadius * projScale / Math.Max(dist, 0.05f));

        /// <summary>Screen terms' weight at a distance (1 near, 0 at v2SsgiFade).</summary>
        public static float Fade(float dist) => Clamp01(1 - (dist - S.v2SsgiFade * 0.6f) / (S.v2SsgiFade * 0.4f));

        /// <summary>
        /// Temporal accumulation: <paramref name="age"/> frames of valid history blend with the new frame at
        /// max(1 / (age + 1), 1 - history): fast convergence after a disocclusion, then the steady blend.
        /// </summary>
        public static float Blend(float age) => Math.Max(1f / (Math.Max(age, 0) + 1), 1 - S.v2SsgiHistory);

        /// <summary>History is kept when the reprojected depth matches within 5 % (+ 5 cm).</summary>
        public static bool HistoryValid(float depthNow, float depthPrev) => depthPrev > 0 && Math.Abs(depthNow - depthPrev) <= 0.05f * depthNow + 0.05f;

        /// <summary>
        /// Multi-bounce fit of the visibility (Jimenez et al. 2016, GTAO): the light the occluders reflect back keeps
        /// corners from going black; <paramref name="albedo"/> is a typical G3 albedo.
        /// </summary>
        public static float MultiBounce(float v, float albedo = 0.3f)
        {
            float a = 2.0404f * albedo - 0.3324f, b = -4.7951f * albedo + 0.6417f, c = 2.7552f * albedo + 0.6903f;
            return Math.Max(v, ((v * a + b) * v + c) * v);
        }

        /// <summary>
        /// Fill visibility applied by the ratio pass: v' = (lerp(v, multi-bounce(v), v2SsgiMultiBounce))^v2SsgiAoPower,
        /// then lerp(1, v', strength x distance fade).
        /// </summary>
        public static float FillVisibility(float v, float dist)
        {
            v = Clamp01(v);
            float m = v + (MultiBounce(v) - v) * S.v2SsgiMultiBounce;
            float p = (float)Math.Pow(Math.Max(m, 0), S.v2SsgiAoPower);
            return 1 + (p - 1) * S.v2SsgiAo * Fade(dist);
        }

        /// <summary>_G3SsgiRatio2: x multi-bounce blend, y AO power.</summary>
        public static float[] RatioGlobal2() => new[] { S.v2SsgiMultiBounce, S.v2SsgiAoPower };

        /// <summary>_G3Ssgi: x radius (m), y max pixels, z thickness (m), w fade (m); _G3Ssgi2: x slices, y steps, z AO strength, w gain.</summary>
        public static float[] Global() => Wanted
            ? new[] { S.v2SsgiRadius, S.v2SsgiMaxPixels, S.v2SsgiThickness, S.v2SsgiFade, S.v2SsgiSlices, S.v2SsgiSteps, S.v2SsgiAo, S.v2SsgiGain }
            : new float[8];

        /// <summary>GPU estimate (RTX 3080, 1080p): trace (slices x 2 sides x steps taps at half res) + temporal + filter.</summary>
        public static float CostMs() => !Wanted ? 0 : 0.12f + S.v2SsgiSlices * 2 * S.v2SsgiSteps * 0.012f + 0.1f;

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
