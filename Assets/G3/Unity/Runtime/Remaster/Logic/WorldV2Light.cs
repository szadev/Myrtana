using System;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free maths of the remaster-look light pass (Docs/remaster-world.md section 11): sky occlusion from a
    /// top-down occluder map (solid tops by physics raycasts + tree crowns from the SpeedTree bounds; "world AO" as in Far
    /// Cry / Lumen's sky occlusion under canopy), screen-space contact shadows for the sun, and the ratio relight that applies
    /// both in a forward renderer without touching any material: with A = ambient irradiance at the pixel (G3's SH), D =
    /// direct sun (NdotL, shadow map), L = local lights, the frame colour albedo·(A + D + L) becomes albedo·(A·v + D·c + L)
    /// by multiplying with <see cref="Ratio"/> (albedo cancels; torches keep their light, the shade under trees and inside
    /// houses and caves loses the sky light it cannot see). Mirrored by Hidden/G3/Remaster/LightRatio (tested here).
    /// </summary>
    public static class WorldV2Light
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        /// <summary>
        /// Per-channel multiplier of the frame colour: (A·v + D·c + L) / (A + D + L), clamped to [floor, 1] (the pass only
        /// darkens). <paramref name="v"/> sky visibility 0..1, <paramref name="c"/> contact-shadow transmittance 0..1.
        /// </summary>
        public static float Ratio(float a, float d, float l, float v, float c, float floor = 0.05f, float bounce = 0)
        {
            a = Math.Max(a, 0); d = Math.Max(d, 0); l = Math.Max(l, 0);
            float sum = a + d + l;
            if (sum < 1e-6f) return 1;
            float r = (a * Clamp01(v) + Math.Max(bounce, 0) + d * Clamp01(c) + l) / sum;
            return Math.Max(Math.Min(r, 1), floor);
        }

        /// <summary>
        /// fix-258: the ratio under a solid ceiling (<paramref name="ceiling"/> 0..1: the occluder map's solid top straight
        /// above, mean of 4 taps of saturate((top - y - 1) x 0.4); full from 0.9) never below <paramref name="interiorFloor"/>: G3 lights its closed halls with its fill plus the sun its
        /// interior meshes let through; the remaster took the sky light (x 0.4) and the sun (blocked by the map) away and a
        /// hall without windows went near black (Bakaresh Beliar throne hall, mean 14 / 255 vs 30-37 in other interiors).
        /// Mirrors the LightRatio shader.
        /// </summary>
        public static float InteriorFloor(float ratio, float ceiling, float interiorFloor)
        {
            float t = Clamp01((Clamp01(ceiling) - 0.5f) / 0.4f);
            return Math.Max(ratio, Clamp01(interiorFloor) * t * t * (3 - 2 * t));
        }

        /// <summary>_G3LightRatio3: x interior floor (0 = off; only with the sky occlusion on), yzw unused.</summary>
        public static float[] Ratio3Global => new[] { S.v2 && S.v2SkyOcclusion ? S.v2InteriorFloor : 0f, 0f, 0f, 0f };

        /// <summary>
        /// One-bounce irradiance (section 11.8): the lit surroundings' colour (the blurred frame = albedo × their light, i.e.
        /// what they reflect) times strength, where the sky is hidden (1 − v). Added to the ratio's numerator per channel;
        /// with the ratio clamped to 1 it only gives back (tinted) what the occlusion removed, never more than G3's light.
        /// </summary>
        public static float Bounce(float surround, float v, float strength) => Math.Max(surround, 0) * Math.Max(strength, 0) * (1 - Clamp01(v));

        /// <summary>Remaster TAA mode for the Lighting part: 0 off (the options' AA), 1 URP TAA, 2 STP.</summary>
        public static int TaaMode => S.v2 ? S.v2Taa : 0;

        /// <summary>The visibility the ratio sees: 1 − strength·occlusion (strength &lt; 1 keeps some bounce light).</summary>
        public static float Visibility(float occlusion, float strength) => 1 - Clamp01(strength) * Clamp01(occlusion);

        /// <summary>A tree crown splat from its renderer bounds: centre, radius (horizontal), bottom and top (world y).</summary>
        public static (float x, float z, float radius, float bottom, float top) Crown(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            float h = Math.Max(maxY - minY, 0);
            float r = 0.5f * Math.Max(maxX - minX, maxZ - minZ) * 0.85f;
            // the crown starts at ~30 % of the height (G3's firs, buckthorns); bushes (short) are crowns from the ground
            float bottom = minY + (h < 4 ? 0.1f : 0.3f) * h;
            return (0.5f * (minX + maxX), 0.5f * (minZ + maxZ), Math.Max(r, 0.5f), bottom, maxY);
        }

        /// <summary>
        /// The occluder window (a square of <paramref name="size"/> metres around the camera, snapped to whole texels) must be
        /// rebuilt when the camera left the inner part (margin = a quarter of the size) or the window was never built.
        /// </summary>
        public static bool NeedsRecenter(float camX, float camZ, float centreX, float centreZ, float size, bool built)
        {
            if (!built) return true;
            float m = size * 0.25f;
            return Math.Abs(camX - centreX) > m || Math.Abs(camZ - centreZ) > m;
        }

        /// <summary>Texel-snapped window centre (so a rebuild does not swim).</summary>
        public static float Snap(float v, float texel) => (float)Math.Round(v / texel) * texel;

        /// <summary>The ring taps of the visibility estimate: (radius m, rise per m (tan of the cone elevation), weight).</summary>
        public static readonly (float radius, float rise, float weight)[] Taps =
        {
            (0f, 0f, 0.34f),      // straight up (roof, cave ceiling, crown above)
            (3f, 1.2f, 0.36f),    // ring 1: 6 taps, ~50 deg cone
            (9f, 0.55f, 0.30f),   // ring 2: 6 taps, ~29 deg (valley walls, nearby crowns)
        };

        /// <summary>
        /// CPU mirror of the shader's occlusion at one point: <paramref name="solidTop"/>(x, z) = highest solid surface,
        /// <paramref name="crownTop"/>(x, z) = highest crown above (or -inf), <paramref name="crown"/> crown density.
        /// 0 = open sky, 1 = fully covered.
        /// </summary>
        public static float Occlusion(Func<float, float, float> solidTop, Func<float, float, float> crownTop, float x, float y, float z, float crown)
        {
            float occ = 0;
            foreach (var t in Taps)
            {
                int n = t.radius <= 0 ? 1 : 6;
                float sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float ang = i * (float)(Math.PI * 2 / 6) + (t.radius > 5 ? 0.52f : 0f);
                    float px = x + (float)Math.Cos(ang) * t.radius, pz = z + (float)Math.Sin(ang) * t.radius;
                    float need = y + 1.0f + t.radius * t.rise;   // 1 m: the surface itself and its own small relief do not count
                    float o = solidTop(px, pz) > need ? 1 : 0;
                    if (crownTop(px, pz) > need) o = Math.Max(o, crown);
                    sum += o;
                }
                occ += t.weight * sum / n;
            }
            return Clamp01(occ);
        }

        /// <summary>
        /// Where the occluder map is read for a surface point: 1.6 m out along its normal, so a wall or a rock face is not
        /// "under" the top of its own collider (Montera rock, first in-engine A/B), while floors (normal up) read straight up.
        /// </summary>
        public static (float x, float y, float z) LookupPoint(float px, float py, float pz, float nx, float ny, float nz)
            => (px + nx * 1.6f, py + ny * 1.6f, pz + nz * 1.6f);

        /// <summary>
        /// Contact-shadow ray length (m) at a view distance: the base length, growing 1 % of the distance (so far contacts
        /// still cover a pixel), capped at 4× the base; 0 beyond the fade distance.
        /// </summary>
        public static float ContactLength(float dist)
        {
            if (!WorldV2.On(S.v2Contact) || dist > S.v2ContactFade) return 0;
            return Math.Min(S.v2ContactLength + dist * 0.01f, S.v2ContactLength * 4);
        }

        /// <summary>
        /// Highlight path to white (Khronos PBR Neutral's desaturation, D = <c>v2HighlightDesat</c>): after the shoulder
        /// compressed the peak channel from <paramref name="peak"/> to <paramref name="newPeak"/>, the colour moves towards
        /// white by 1 − 1/(D·(peak − newPeak) + 1): bright sun on sand and snow goes white instead of a clipped hue.
        /// </summary>
        public static float DesatAmount(float peak, float newPeak, float d)
        {
            if (d <= 0 || peak <= newPeak) return 0;
            return 1 - 1 / (d * (peak - newPeak) + 1);
        }

        /// <summary>_G3LightRatio: x sky-occlusion strength (0 = off), y crown density, z contact strength (0 = off), w contact length (m).</summary>
        public static float[] RatioGlobal => S.v2
            ? new[] { S.v2SkyOcclusion ? S.v2SkyOccStrength : 0f, S.v2SkyOccCanopy, S.v2Contact ? S.v2ContactStrength : 0f, S.v2ContactLength }
            : new float[4];

        /// <summary>_G3LightRatio2: x contact steps, y contact fade (m), z ratio floor, w unused.</summary>
        public static float[] Ratio2Global => new[] { (float)S.v2ContactSteps, S.v2ContactFade, S.v2LightFloor, 0f };

        /// <summary>True when the pass has anything to do.</summary>
        public static bool PassWanted => S.v2 && ((S.v2SkyOcclusion && S.v2SkyOccStrength > 0) || (S.v2Contact && S.v2ContactStrength > 0) || WorldGi.Wanted
                                          || (S.v2Clouds && S.v2CloudShadows && S.v2CloudShadowStrength > 0));

        /// <summary>Estimated GPU cost (ms, 1080p) of the pass at the configured resolution.</summary>
        public static float CostMs()
        {
            if (!PassWanted) return 0;
            float k = 1f / (S.v2LightDownsample * S.v2LightDownsample);
            float march = S.v2Contact ? 0.05f * S.v2ContactSteps : 0;
            return 0.1f + (0.4f + march) * k;   // full-res composite + the per-pixel work (taps, march, shadow, light loop)
        }

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
