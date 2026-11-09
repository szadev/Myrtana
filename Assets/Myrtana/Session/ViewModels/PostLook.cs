using System;

namespace Myrtana.Session
{
    /// <summary>
    /// G3's hardware gamma ramp from the Video page's colour sliders (Engine <c>eCGfxMixerAdmin::UpdateGammaRamp</c>, CP 1.75
    /// 0x1008d1c0): per channel, on display values t = i/255,
    /// <c>out = (t^exp(−3·gamma) − 127/255) · exp(3·contrast) + 127/255 + brightness</c>, clamped to 0…1. The inputs are the
    /// mixer values of <see cref="VideoOptions.Value"/> (brightness ±0.2, contrast ±0.075, gamma ±0.3); all 0 = identity.
    /// </summary>
    public readonly struct GammaRamp : IEquatable<GammaRamp>
    {
        public const float Mid = 127f / 255f;

        public readonly float Brightness, Contrast, GammaRed, GammaGreen, GammaBlue;

        public GammaRamp(float brightness, float contrast, float gammaRed, float gammaGreen, float gammaBlue)
        {
            Brightness = brightness;
            Contrast = contrast;
            GammaRed = gammaRed;
            GammaGreen = gammaGreen;
            GammaBlue = gammaBlue;
        }

        public static GammaRamp From(VideoOptions video) => new GammaRamp(video.Value(VideoColor.Brightness),
            video.Value(VideoColor.Contrast), video.Value(VideoColor.GammaRed), video.Value(VideoColor.GammaGreen),
            video.Value(VideoColor.GammaBlue));

        public bool IsNeutral => Brightness == 0 && Contrast == 0 && GammaRed == 0 && GammaGreen == 0 && GammaBlue == 0;

        /// <summary>The power applied to channel 0/1/2 (R/G/B): exp(−3·gamma), so +100 % gamma brightens (0.41).</summary>
        public float Exponent(int channel) =>
            (float)Math.Exp(-3.0 * (channel == 0 ? GammaRed : channel == 1 ? GammaGreen : GammaBlue));

        /// <summary>The contrast slope round 127/255: exp(3·contrast).</summary>
        public float Slope => (float)Math.Exp(3.0 * Contrast);

        /// <summary>The ramp of one channel at display value t (0…1).</summary>
        public float Apply(int channel, float t)
        {
            t = PostLook.Clamp01(t);
            double v = (Math.Pow(t, Exponent(channel)) - Mid) * Slope + Mid + Brightness;
            return PostLook.Clamp01((float)v);
        }

        public bool Equals(GammaRamp o) => Brightness == o.Brightness && Contrast == o.Contrast && GammaRed == o.GammaRed &&
                                           GammaGreen == o.GammaGreen && GammaBlue == o.GammaBlue;

        public override bool Equals(object obj) => obj is GammaRamp o && Equals(o);
        public override int GetHashCode() => (Brightness, Contrast, GammaRed, GammaGreen, GammaBlue).GetHashCode();
    }

    /// <summary>
    /// What the game camera's post-processing does for the current Video and Performance options (fix-50, fix-55; the Unity
    /// side is Myrtana.Unity <c>DisplayOptionsApplier</c> → G3.Unity <c>G3ImageProcessing</c>, Docs/controls-options.md
    /// "Post-processing"). G3 CP 1.75's chain (Materials.pak Effects/ImageProcessing/IP_*.fx, ge3.ini, Engine
    /// eCIPImageFilter*) is ported as G3's own math in one pass chain, not mapped onto URP's effects (fix-55: URP's bloom
    /// thresholds the brightest channel in linear light, its film grain is multiplicative, so it is 0 in the darks, and its
    /// Gaussian DoF swaps in a half-resolution image from coc 0.16 on, which blurred the whole vista):
    /// <list type="bullet">
    /// <item>Bloom 1: highlights <see cref="BloomMask"/> = c·max(luma − threshold, 0)·1.7 (threshold 1 − the weather's
    /// GlowIntensity, ge3.ini BloomThreshold 0.59), at quarter resolution, G3's 13-tap blur horizontally then vertically,
    /// screen-blended (<see cref="ScreenBlend"/>), all on display values.</item>
    /// <item>Bloom 2 (HDR): G3 adds an exposure/blue-shift/vignette/gamma tone curve; the port's lighting is already matched
    /// to CP shots taken in that mode, so only the glow is kept, multiplier HDRGlowIntensity 3.0.</item>
    /// <item>Depth of field: IP_DOF, 8 Poisson taps on a disc of radius coc·DOFMaxBlur pixels (2 px at any resolution:
    /// eCIPImageFilterDOF scales the taps by 1 / target size), coc = (distance − DOFStart) / (DOFEnd − DOFStart) clamped
    /// (ge3.ini 2000 / 12000 cm). Far blur only and mild: <see cref="DofCoc"/>.</item>
    /// <item>Noise: additive in the darks, per pixel and per frame: (noise − 0.5) · (1 − luma)^7 · 0.15
    /// (<see cref="NoiseWeight"/>; NoiseThreshold 7, NoiseIntensity 0.15).</item>
    /// <item>Colour grading: G3 runs a LUT (G3_Color_LUT_LDR: luma +0.019, chroma ×0.651). The reference shots and so the
    /// port's tuned look already contain it, so on (default) adds nothing; off removes it (luma −0.019, chroma ÷0.651).</item>
    /// <item>Brightness / contrast / gamma: G3's gamma ramp (<see cref="GammaRamp"/>), the last step on the display values →
    /// a URP colour-lookup texture built from <see cref="Grade"/> (display space, after everything else).</item>
    /// <item>No tonemapping and no exposure offset at all: G3's look is the port's lit image as it is.</item>
    /// </list>
    /// </summary>
    public sealed class PostLook : IEquatable<PostLook>
    {
        /// <summary>ge3.ini BloomIntensity (Bloom 1) and HDRGlowIntensity (Bloom 2): the highlight mask multiplier.</summary>
        public const float BloomMultiplierLdr = 1.7f, BloomMultiplierHdr = 3.0f;
        /// <summary>Sunny GlowIntensity (G3 threshold 0.6 ≈ ge3.ini BloomThreshold 0.59), used without a weather.</summary>
        public const float DefaultGlowIntensity = 0.4f;
        /// <summary>ge3.ini DOFStart / DOFEnd (m) and DOFMaxBlur (pixels).</summary>
        public const float DofStart = 20f, DofEnd = 120f, DofMaxBlur = 2f;
        /// <summary>ge3.ini NoiseThreshold (exponent on 1 − luma) and NoiseIntensity.</summary>
        public const float NoiseThreshold = 7f, NoiseIntensity = 0.15f;
        /// <summary>G3_Color_LUT_LDR fitted in display space (luma 0.3/0.59/0.11): luma + 0.019, chroma × 0.651.</summary>
        public const float LutLumaLift = 0.019f, LutChroma = 0.651f;

        /// <summary>0 off, 1 bloom, 2 HDR (ge3.ini Bloom).</summary>
        public int Bloom { get; private set; }
        public bool DepthOfField { get; private set; }
        public bool Noise { get; private set; }
        public bool Fxaa { get; private set; }
        public bool LensFlare { get; private set; }
        public bool ColorGrading { get; private set; } = true;
        public GammaRamp Ramp { get; private set; }

        /// <summary>The defaults: High preset, sliders at 0.</summary>
        public static PostLook Default => From(new VideoOptions(), new PerformanceOptions());

        public static PostLook From(VideoOptions video, PerformanceOptions performance) => new PostLook
        {
            Ramp = GammaRamp.From(video),
            Bloom = performance.Value(DetailSetting.Bloom),
            DepthOfField = performance.Value(DetailSetting.DepthOfField) != 0,
            Noise = performance.Value(DetailSetting.Noise) != 0,
            Fxaa = performance.Value(DetailSetting.Antialiasing) != 0,
            LensFlare = performance.Value(DetailSetting.LensFlare) != 0,
            ColorGrading = performance.Value(DetailSetting.ColorGrading) != 0,
        };

        /// <summary>The highlight mask multiplier of the bloom option (0 = no bloom).</summary>
        public float BloomMultiplier => Bloom >= 2 ? BloomMultiplierHdr : Bloom == 1 ? BloomMultiplierLdr : 0f;

        /// <summary>G3's highlight threshold for a weather GlowIntensity: 1 − GlowIntensity (ini range 0.01 … 1).</summary>
        public static float BloomThreshold(float glowIntensity) => Math.Max(0.01f, Math.Min(1f, 1f - glowIntensity));

        /// <summary>IP_MaskHighLights on one display colour: c · max(luma − threshold, 0) · multiplier, clamped.</summary>
        public static void BloomMask(ref float r, ref float g, ref float b, float threshold, float multiplier)
        {
            float k = Math.Max(0.3f * r + 0.59f * g + 0.11f * b - threshold, 0f) * multiplier;
            r = Clamp01(r * k);
            g = Clamp01(g * k);
            b = Clamp01(b * k);
        }

        /// <summary>IP_HDRI BloomFx: the blurred highlights screen-blended onto a display value, c + bloom · (1 − c).</summary>
        public static float ScreenBlend(float c, float bloom) => c + bloom * (1f - Clamp01(c));

        /// <summary>IP_Noise NoiseFx: the weight of the ±0.5 noise at a display colour, (1 − luma)^7 · 0.15.</summary>
        public static float NoiseWeight(float r, float g, float b) =>
            (float)Math.Pow(Math.Max(1f - (0.3f * r + 0.59f * g + 0.11f * b), 0.00001f), NoiseThreshold) * NoiseIntensity;

        /// <summary>IP_DOF circle of confusion at a view distance (m): 0 up to DOFStart, 1 from DOFEnd on.</summary>
        public static float DofCoc(float distance) => Clamp01((distance - DofStart) / (DofEnd - DofStart));

        /// <summary>The DoF tap disc radius in screen pixels at a view distance (at most DOFMaxBlur, any resolution).</summary>
        public static float DofRadiusPixels(float distance) => DofCoc(distance) * DofMaxBlur;

        /// <summary>True when the colour lookup changes nothing (colour grading on, sliders at 0): no LUT is used.</summary>
        public bool ColorNeutral => ColorGrading && Ramp.IsNeutral;

        /// <summary>One display-space colour (0…1) through colour grading off (when off) and the gamma ramp.</summary>
        public void Grade(ref float r, ref float g, ref float b)
        {
            if (!ColorGrading)
            {
                float luma = 0.3f * r + 0.59f * g + 0.11f * b, target = luma - LutLumaLift;
                r = Clamp01(target + (r - luma) / LutChroma);
                g = Clamp01(target + (g - luma) / LutChroma);
                b = Clamp01(target + (b - luma) / LutChroma);
            }
            r = Ramp.Apply(0, r);
            g = Ramp.Apply(1, g);
            b = Ramp.Apply(2, b);
        }

        /// <summary>
        /// The colour lookup as URP's strip (width size², height size; RGB per texel, rows bottom-up): texel (x, y) holds
        /// <see cref="Grade"/> of (x mod size, y, x div size) / (size − 1).
        /// </summary>
        public float[] BuildLut(int size)
        {
            int width = size * size;
            var rgb = new float[width * size * 3];
            float step = 1f / (size - 1);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < width; x++)
            {
                float r = (x % size) * step, g = y * step, b = (x / size) * step;
                Grade(ref r, ref g, ref b);
                int i = (y * width + x) * 3;
                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
            return rgb;
        }

        public bool Equals(PostLook o) => o != null && Bloom == o.Bloom && DepthOfField == o.DepthOfField && Noise == o.Noise &&
                                          Fxaa == o.Fxaa && LensFlare == o.LensFlare && ColorGrading == o.ColorGrading &&
                                          Ramp.Equals(o.Ramp);

        public override bool Equals(object obj) => Equals(obj as PostLook);
        public override int GetHashCode() => (Bloom, DepthOfField, Noise, Fxaa, LensFlare, ColorGrading, Ramp).GetHashCode();

        internal static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
