using System;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-50/55: the options → post-processing mapping (PostLook, GammaRamp), its neutral defaults and G3's filter math.</summary>
    public class PostLookTests
    {
        [Test]
        public void Defaults_AreG3HighPreset_AndColourNeutral()
        {
            var post = PostLook.Default;
            Assert.That(post.Bloom, Is.EqualTo(1));
            Assert.That(post.BloomMultiplier, Is.EqualTo(PostLook.BloomMultiplierLdr));
            Assert.That(post.DepthOfField, Is.True);
            Assert.That(post.Noise, Is.False);
            Assert.That(post.Fxaa, Is.False);
            Assert.That(post.ColorGrading, Is.True);
            Assert.That(post.Ramp.IsNeutral, Is.True);
            Assert.That(post.ColorNeutral, Is.True, "default options add no colour lookup");
        }

        [Test]
        public void NeutralLut_IsIdentity()
        {
            const int size = 32;
            var lut = PostLook.Default.BuildLut(size);
            Assert.That(lut.Length, Is.EqualTo(size * size * size * 3));
            float step = 1f / (size - 1);
            for (int y = 0; y < size; y += 5)
            for (int x = 0; x < size * size; x += 37)
            {
                int i = (y * size * size + x) * 3;
                Assert.That(lut[i], Is.EqualTo((x % size) * step).Within(1e-5f));
                Assert.That(lut[i + 1], Is.EqualTo(y * step).Within(1e-5f));
                Assert.That(lut[i + 2], Is.EqualTo((x / size) * step).Within(1e-5f));
            }
        }

        [Test]
        public void GammaRamp_IsG3Formula()
        {
            // eCGfxMixerAdmin::UpdateGammaRamp: (t^exp(-3g) - 127/255) * exp(3c) + 127/255 + b, clamped
            var ramp = new GammaRamp(0.1f, 0.05f, 0.2f, -0.1f, 0f);
            float t = 0.3f;
            double expected = (Math.Pow(t, Math.Exp(-0.6)) - 127.0 / 255) * Math.Exp(0.15) + 127.0 / 255 + 0.1;
            Assert.That(ramp.Apply(0, t), Is.EqualTo((float)expected).Within(1e-5f));
            Assert.That(ramp.Exponent(1), Is.EqualTo((float)Math.Exp(0.3)).Within(1e-5f));
            Assert.That(ramp.Apply(2, 0.9f), Is.EqualTo(1f), "clamped");
        }

        [Test]
        public void VideoSliders_MapThroughG3Scaling()
        {
            var video = new VideoOptions();
            video.SetSlider(VideoColor.Brightness, 100);
            video.SetSlider(VideoColor.Contrast, -100);
            video.SetSlider(VideoColor.GammaGreen, 50);
            var ramp = GammaRamp.From(video);
            Assert.That(ramp.Brightness, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(ramp.Contrast, Is.EqualTo(-0.075f).Within(1e-6f));
            Assert.That(ramp.GammaGreen, Is.EqualTo(0.15f).Within(1e-6f));
            Assert.That(ramp.GammaRed, Is.EqualTo(0f));
            Assert.That(ramp.IsNeutral, Is.False);
        }

        [Test]
        public void Brightness_And_Gamma_RaiseMidGrey_Contrast_Spreads()
        {
            Assert.That(new GammaRamp(0.2f, 0, 0, 0, 0).Apply(0, 0.5f), Is.EqualTo(0.7f).Within(1e-5f));
            Assert.That(new GammaRamp(-0.2f, 0, 0, 0, 0).Apply(0, 0.1f), Is.EqualTo(0f), "black clips");
            Assert.That(new GammaRamp(0, 0, 0.3f, 0, 0).Apply(0, 0.5f), Is.GreaterThan(0.7f));
            Assert.That(new GammaRamp(0, 0, -0.3f, 0, 0).Apply(0, 0.5f), Is.LessThan(0.3f));
            var contrast = new GammaRamp(0, 0.075f, 0, 0, 0);
            Assert.That(contrast.Apply(0, 0.8f), Is.GreaterThan(0.8f));
            Assert.That(contrast.Apply(0, 0.2f), Is.LessThan(0.2f));
            Assert.That(contrast.Apply(0, GammaRamp.Mid), Is.EqualTo(GammaRamp.Mid).Within(1e-5f));
        }

        [Test]
        public void ColourGradingOff_RemovesG3Lut_Saturates()
        {
            var perf = new PerformanceOptions();
            perf.Set(DetailSetting.ColorGrading, 0);
            var post = PostLook.From(new VideoOptions(), perf);
            Assert.That(post.ColorNeutral, Is.False);
            float r = 0.6f, g = 0.4f, b = 0.3f;
            float luma = 0.3f * r + 0.59f * g + 0.11f * b;
            post.Grade(ref r, ref g, ref b);
            Assert.That(r - g, Is.EqualTo(0.2f / PostLook.LutChroma).Within(1e-4f));
            Assert.That(0.3f * r + 0.59f * g + 0.11f * b, Is.EqualTo(luma - PostLook.LutLumaLift).Within(1e-4f));
            // grey stays grey
            float k = 0.5f, l = 0.5f, m = 0.5f;
            post.Grade(ref k, ref l, ref m);
            Assert.That(k, Is.EqualTo(l).Within(1e-6f));
            Assert.That(l, Is.EqualTo(m).Within(1e-6f));
        }

        [TestCase(0, 0f)]
        [TestCase(1, 1.7f)]
        [TestCase(2, 3.0f)]
        public void Bloom_OffOnHdr_AreIniMultipliers(int bloom, float multiplier)
        {
            var perf = new PerformanceOptions();
            perf.Set(DetailSetting.Bloom, bloom);
            Assert.That(PostLook.From(new VideoOptions(), perf).BloomMultiplier, Is.EqualTo(multiplier).Within(1e-6f));
        }

        [Test]
        public void BloomThreshold_IsOneMinusWeatherGlow()
        {
            Assert.That(PostLook.BloomThreshold(PostLook.DefaultGlowIntensity), Is.EqualTo(0.6f).Within(1e-6f), "sunny ≈ ini 0.59");
            Assert.That(PostLook.BloomThreshold(0.05f), Is.EqualTo(0.95f).Within(1e-6f), "rain: almost no glow");
            Assert.That(PostLook.BloomThreshold(5f), Is.EqualTo(0.01f), "ini minimum");
            Assert.That(PostLook.BloomThreshold(-5f), Is.EqualTo(1f));
        }

        [Test]
        public void BloomMask_IsG3Formula_OnlyAboveThreshold()
        {
            // IP_MaskHighLights: c * max(dot(c, (0.3, 0.59, 0.11)) - threshold, 0) * multiplier, saturated
            float r = 0.6f, g = 0.75f, b = 0.95f;   // a bright noon sky, luma 0.7275
            PostLook.BloomMask(ref r, ref g, ref b, 0.6f, 1.7f);
            float k = (0.3f * 0.6f + 0.59f * 0.75f + 0.11f * 0.95f - 0.6f) * 1.7f;
            Assert.That(r, Is.EqualTo(0.6f * k).Within(1e-5f));
            Assert.That(b, Is.EqualTo(0.95f * k).Within(1e-5f));
            Assert.That(g, Is.GreaterThan(0.15f), "the sky glows visibly (G3 Bloom 1)");
            float dr = 0.5f, dg = 0.5f, db = 0.5f;
            PostLook.BloomMask(ref dr, ref dg, ref db, 0.6f, 1.7f);
            Assert.That(dr + dg + db, Is.EqualTo(0f), "mid grey stays dark");
            float wr = 1f, wg = 1f, wb = 1f;
            PostLook.BloomMask(ref wr, ref wg, ref wb, 0.01f, 3f);
            Assert.That(wr, Is.EqualTo(1f), "saturated");
        }

        [Test]
        public void ScreenBlend_NeverDarkens_KeepsWhite()
        {
            Assert.That(PostLook.ScreenBlend(0.5f, 0.2f), Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(PostLook.ScreenBlend(1f, 0.8f), Is.EqualTo(1f));
            Assert.That(PostLook.ScreenBlend(0.3f, 0f), Is.EqualTo(0.3f));
        }

        [Test]
        public void Noise_IsStrongInTheDarks_GoneInTheBrights()
        {
            Assert.That(PostLook.NoiseWeight(0, 0, 0), Is.EqualTo(0.15f).Within(1e-6f), "black: ±0.075 = ±19/255");
            Assert.That(PostLook.NoiseWeight(0.1f, 0.1f, 0.1f), Is.EqualTo((float)Math.Pow(0.9, 7) * 0.15f).Within(1e-6f));
            Assert.That(PostLook.NoiseWeight(0.1f, 0.1f, 0.1f) * 0.5f * 255f, Is.GreaterThan(5f), "visible grain at night");
            Assert.That(PostLook.NoiseWeight(0.6f, 0.6f, 0.6f) * 0.5f * 255f, Is.LessThan(0.5f), "nothing on bright surfaces");
        }

        [Test]
        public void DepthOfField_IsMildFarBlur_20To120m_2Pixels()
        {
            Assert.That(PostLook.DofCoc(10f), Is.EqualTo(0f));
            Assert.That(PostLook.DofCoc(20f), Is.EqualTo(0f));
            Assert.That(PostLook.DofCoc(70f), Is.EqualTo(0.5f).Within(1e-6f), "linear ramp, not URP's sqrt(2π·coc) blend");
            Assert.That(PostLook.DofCoc(120f), Is.EqualTo(1f));
            Assert.That(PostLook.DofRadiusPixels(500f), Is.EqualTo(2f), "a vista seen from 110 m up: 2 px at most");
            Assert.That(PostLook.DofRadiusPixels(45f), Is.EqualTo(0.5f).Within(1e-6f));
        }

        [Test]
        public void Presets_DriveEffects_LowIsAllOff()
        {
            var perf = new PerformanceOptions();
            perf.SelectPreset(DetailPreset.Low);
            var low = PostLook.From(new VideoOptions(), perf);
            Assert.That(low.Bloom, Is.EqualTo(0));
            Assert.That(low.DepthOfField || low.Noise || low.Fxaa, Is.False);
            perf.SelectPreset(DetailPreset.VeryHigh);
            var veryHigh = PostLook.From(new VideoOptions(), perf);
            Assert.That(veryHigh.Bloom, Is.EqualTo(2));
            Assert.That(veryHigh.DepthOfField && veryHigh.Noise && veryHigh.Fxaa, Is.True);
            Assert.That(veryHigh.ColorNeutral, Is.True, "a preset keeps colour grading on");
            Assert.That(veryHigh, Is.Not.EqualTo(low));
            Assert.That(PostLook.From(new VideoOptions(), perf), Is.EqualTo(veryHigh));
        }
    }
}
