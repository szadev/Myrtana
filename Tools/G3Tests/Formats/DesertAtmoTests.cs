using System;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>atmo-1 (Docs/remaster-world.md section 23): desert atmosphere and light maths.</summary>
    [TestFixture]
    public class DesertAtmoTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static float Luma(float[] c) => 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];

        [Test]
        public void Off_EverythingNeutral()
        {
            var s = WorldLookSettings.Default;
            s.atmo = false;
            WorldRemasterLook.Current = s;
            Assert.AreEqual(0f, DesertAtmo.Weight(1));
            Assert.AreEqual(1f, DesertAtmo.SunGain(0, 45, 0, 0));
            Assert.AreEqual((0f, 0f, 0f), DesertAtmo.Ambient(0, 45, 0, 0));
            Assert.AreEqual(1f, DesertAtmo.FogPush(0, 0));
            Assert.AreEqual(0f, DesertAtmo.GradeGlobal(0, 45, 0)[0]);
            Assert.AreEqual(0, DesertAtmo.MoteCount(0, 45, 0));
            Assert.AreEqual(90f, DesertAtmo.ShadowDistance(90, 0));
        }

        [Test]
        public void Weight_FollowsTheDesertShare()
        {
            Assert.AreEqual(0f, DesertAtmo.Weight(0));
            Assert.AreEqual(0.5f, DesertAtmo.Weight(0.5f), 1e-6);
            Assert.AreEqual(1f, DesertAtmo.Weight(3), 1e-6, "clamped");
        }

        [Test]
        public void Sun_StrongerInClearDaylight_DimmedInAStorm_NoneAtNight()
        {
            Assert.Greater(DesertAtmo.SunGain(1, 50, 0, 0), 1.2f);
            Assert.AreEqual(1f, DesertAtmo.SunGain(1, -20, 0, 0), 1e-4, "night: G3's moon as is");
            Assert.Less(DesertAtmo.SunGain(1, 50, 0, 1), 0.4f, "storm");
            Assert.Less(DesertAtmo.SunGain(1, 50, 0.9f, 0), DesertAtmo.SunGain(1, 50, 0, 0), "overcast softens");
        }

        [Test]
        public void SunTint_WarmerAtLowSun_LuminanceKept()
        {
            var noon = DesertAtmo.SunTint(1, 70);
            var low = DesertAtmo.SunTint(1, 5);
            Assert.AreEqual(1f, Luma(noon), 1e-4);
            Assert.AreEqual(1f, Luma(low), 1e-4);
            Assert.Greater(low[0] / low[2], noon[0] / noon[2], "redder at low sun");
            Assert.Greater(noon[0], noon[2], "dust reddens a little even at noon");
            Assert.AreEqual(new[] { 1f, 1f, 1f }, DesertAtmo.SunTint(0, 5));
        }

        [Test]
        public void Ambient_ClearDaylightOnly()
        {
            var (cut, sky, bounce) = DesertAtmo.Ambient(1, 50, 0, 0);
            Assert.Greater(cut, 0.2f); Assert.Greater(sky, 0.03f); Assert.Greater(bounce, 0.2f);
            Assert.AreEqual((0f, 0f, 0f), DesertAtmo.Ambient(1, -10, 0, 0), "night");
            Assert.AreEqual(0f, DesertAtmo.Ambient(1, 50, 0, 1).cut, 1e-6, "storm: G3's soft light");
            Assert.Less(cut, 0.9f, "never removes the fill entirely");
        }

        [Test]
        public void Fog_PushedOutInClearDesertOnly()
        {
            Assert.Greater(DesertAtmo.FogPush(1, 0), 1.5f);
            Assert.AreEqual(1f, DesertAtmo.FogPush(1, 1), 1e-6, "storm: G3's sandstorm fog distance stays");
        }

        [Test]
        public void Shadows_LongerInTheDesert_NeverShorter()
        {
            Assert.AreEqual(140f, DesertAtmo.ShadowDistance(90, 1), 1e-4);
            Assert.AreEqual(115f, DesertAtmo.ShadowDistance(90, 0.5f), 1e-4);
            Assert.AreEqual(200f, DesertAtmo.ShadowDistance(200, 1), 1e-4, "a longer v2 distance is kept");
        }

        [Test]
        public void Medium_StormDenserAndCappedHigher()
        {
            var calm = DesertAtmo.DustGlobal(1, 0);
            var storm = DesertAtmo.DustGlobal(1, 1);
            Assert.Greater(storm[0], calm[0]);
            Assert.AreEqual(1f / WorldLookSettings.Default.atmoDustHeight, calm[1], 1e-7);
            Assert.AreEqual(0f, DesertAtmo.StormGlobal(1, 0)[0]);
            Assert.Greater(DesertAtmo.StormGlobal(1, 1)[0], 0.005f);
            Assert.AreEqual(WorldLookSettings.Default.atmoMax, DesertAtmo.HazeCap(0), 1e-6);
            Assert.Greater(DesertAtmo.HazeCap(1), 0.95f);
            // visibility in a full storm: ~3 optical depths within 150-250 m at the ground
            float od200 = DesertAtmo.StormGlobal(1, 1)[0] * 200;
            Assert.That(od200, Is.InRange(2.0f, 5.0f));
            // clear desert: distant mountains (10 km at the ground) clearly hazed, 1 km only a little
            float k = calm[1];
            float od10 = DesertAtmo.LayerOpticalDepth(10000, 0, 0, calm[0], k);
            float od1 = DesertAtmo.LayerOpticalDepth(1000, 0, 0, calm[0], k);
            Assert.That(1 - Math.Exp(-od10), Is.InRange(0.6, 0.95));
            Assert.That(1 - Math.Exp(-od1), Is.InRange(0.1, 0.35));
            // up the layer: a 30-degree look at the sky crosses far less dust than the horizon
            Assert.Less(DesertAtmo.LayerOpticalDepth(20000, 0, 10000, calm[0], k), od10 * 0.3f);
        }

        [Test]
        public void Phase_ForwardPeaked_Normalised()
        {
            float g = 0.72f;
            Assert.Greater(DesertAtmo.PhaseHG(1, g), 10 * DesertAtmo.PhaseHG(0, g));
            // integral over the sphere = 1
            double sum = 0; int n = 20000;
            for (int i = 0; i < n; i++) { double c = -1 + (i + 0.5) * 2.0 / n; sum += DesertAtmo.PhaseHG((float)c, g) * 2 * Math.PI * 2.0 / n; }
            Assert.AreEqual(1.0, sum, 0.01);
        }

        [Test]
        public void Filmic_MidGreyKept_WhiteToOne_Monotonic_ContrastInTheToe()
        {
            float a = 1.35f, white = 4f;
            var (b0, c0) = DesertAtmo.LottesBC(a, DesertAtmo.Shoulder, white);
            Assert.AreEqual(0.18f, DesertAtmo.Filmic(0.18f, a, DesertAtmo.Shoulder, b0, c0), 1e-3, "mid grey kept");
            var (b, c) = DesertAtmo.LottesBC(a, DesertAtmo.Shoulder, white, 0.22f);
            Assert.AreEqual(0.22f, DesertAtmo.Filmic(0.18f, a, DesertAtmo.Shoulder, b, c), 1e-3, "mid grey shown brighter");
            Assert.AreEqual(1f, DesertAtmo.Filmic(white, a, DesertAtmo.Shoulder, b, c), 1e-3);
            float prev = 0;
            for (float x = 0.001f; x < 8; x *= 1.1f)
            {
                float y = DesertAtmo.Filmic(x, a, DesertAtmo.Shoulder, b, c);
                Assert.GreaterOrEqual(y, prev - 1e-6f, $"monotonic at {x}");
                prev = y;
            }
            Assert.Less(DesertAtmo.Filmic(0.02f, a, DesertAtmo.Shoulder, b, c), 0.02f, "deeper shade (toe)");
            Assert.Less(DesertAtmo.Filmic(1.5f, a, DesertAtmo.Shoulder, b, c), 1f, "highlights roll off before white");
            Assert.Greater(DesertAtmo.Filmic(0.6f, a, DesertAtmo.Shoulder, b, c), 0.55f, "sunlit sand stays bright");
        }

        [Test]
        public void Grade_GoldenHourRicher_StormFlat_SplitTintsKeepLuminance()
        {
            var noon = DesertAtmo.GradeGlobal(1, 60, 0);
            var gold = DesertAtmo.GradeGlobal(1, 10, 0);
            var storm = DesertAtmo.GradeGlobal(1, 60, 1);
            Assert.AreEqual(1f, noon[0]);
            Assert.AreEqual(0f, DesertAtmo.GradeGlobal(1, -20, 0)[0], "night: G3's own");
            Assert.Greater(gold[1], noon[1], "more contrast at golden hour");
            Assert.Less(storm[1], noon[1], "a storm is flat");
            Assert.Greater(DesertAtmo.Grade2Global(1, 60, 0)[0], 1.05f, "noon high key");
            Assert.AreEqual(1f, DesertAtmo.Grade2Global(1, 10, 0)[0], 1e-6, "no lift at low sun");
            Assert.Greater(DesertAtmo.Grade2Global(1, 10, 0)[1], DesertAtmo.Grade2Global(1, 60, 0)[1], "richer golden hour");
            var (sh, hi) = DesertAtmo.SplitTints(1, 10, 0);
            Assert.AreEqual(1f, Luma(sh), 1e-4); Assert.AreEqual(1f, Luma(hi), 1e-4);
            Assert.Greater(sh[2], sh[0], "cool shade");
            Assert.Greater(hi[0], hi[2], "warm light");
            var (sh0, hi0) = DesertAtmo.SplitTints(0, 10, 0);
            Assert.AreEqual(1f, sh0[0], 1e-6); Assert.AreEqual(1f, hi0[2], 1e-6);
        }

        [Test]
        public void SkyAndMotes()
        {
            Assert.AreEqual(WorldLookSettings.Default.atmoSkyGlow, DesertAtmo.SkyGlobal(1, 30, 0)[0], 1e-5);
            Assert.AreEqual(0f, DesertAtmo.SkyGlobal(1, -10, 0)[0], "night");
            Assert.LessOrEqual(DesertAtmo.SkyGlobal(1, 30, 0)[2], 0f, "never more clouds over the desert");
            Assert.AreEqual(WorldLookSettings.Default.atmoMoteCount, DesertAtmo.MoteCount(1, 40, 0));
            Assert.AreEqual(0, DesertAtmo.MoteCount(1, -20, 0), "night");
            Assert.Greater(DesertAtmo.MoteCount(1, -20, 1), DesertAtmo.MoteCount(1, 40, 0), "storm grains, any hour");
            Assert.AreEqual(0, DesertAtmo.MoteCount(0, 40, 0));
        }

        [Test]
        public void SkyModel_BlueZenith_WarmLowSunHorizon_BrighterTowardsTheSun()
        {
            // noon: zenith blue
            var z = DesertAtmo.SkyRadiance(0, 1, 0, 0, 0.9, 0.436, 2.2, 0.76);
            Assert.Greater(z[2], z[0] * 1.5, "blue zenith");
            // low sun (5 degrees, towards +x): the horizon at the sun is warm and brighter than the opposite horizon
            double sy = Math.Sin(5 * Math.PI / 180), sx = Math.Cos(5 * Math.PI / 180);
            var toward = DesertAtmo.SkyRadiance(1, 0.05, 0, sx, sy, 0, 2.2, 0.76);
            var away = DesertAtmo.SkyRadiance(-1, 0.05, 0, sx, sy, 0, 2.2, 0.76);
            Assert.Greater(toward[0], toward[2], "warm towards the low sun");
            Assert.Greater(toward[0] + toward[1], 3 * (away[0] + away[1]), "Mie glow towards the sun");
            // the sun below the horizon: dark
            var night = DesertAtmo.SkyRadiance(0, 1, 0, 0, -0.5, 0.866, 2.2, 0.76);
            Assert.Less(night[2], z[2] * 0.01);
        }

        [Test]
        public void SkyModel_ScaledToG3Luminance()
        {
            var g = DesertAtmo.SkyPBGlobal(1, 40, 0, 0, 0.64f, 0.77f, 0.2f, 0.6f);
            Assert.Greater(g[0], 0.5f);
            // the model's zenith / side-horizon mean x scale = G3's mean
            var zen = DesertAtmo.SkyRadiance(0, 1, 0, 0, 0.64, 0.77, g[1], 0.76);
            Assert.AreEqual((0.25f * 0.2f + 0.75f * 0.6f) * WorldLookSettings.Default.atmoSkyPBCap, g[3], 1e-4, "cap x G3's mix");
            Assert.Greater(g[2], 0.05f);
            Assert.AreEqual(0f, DesertAtmo.SkyPBGlobal(0, 40, 0, 0, 0.64f, 0.77f, 0.2f, 0.6f)[0], "off the desert");
            Assert.AreEqual(0f, DesertAtmo.SkyPBGlobal(1, -20, 0, 0, 0.64f, 0.77f, 0.2f, 0.6f)[0], "night: G3's dome");
            Assert.AreEqual(0f, DesertAtmo.SkyPBGlobal(1, 40, 1, 0, 0.64f, 0.77f, 0.2f, 0.6f)[0], "storm: G3's dome");
            Assert.Greater(zen[2], 0f);
        }

        [Test]
        public void Settings_Clamped()
        {
            var s = WorldLookSettings.Default;
            s.atmoDust = 5; s.atmoContrast = 9; s.atmoMoteCount = 100000; s.atmoDustHeight = 0;
            var c = s.Clamped();
            Assert.LessOrEqual(c.atmoDust, 0.01f);
            Assert.LessOrEqual(c.atmoContrast, 2f);
            Assert.LessOrEqual(c.atmoMoteCount, 8192);
            Assert.GreaterOrEqual(c.atmoDustHeight, 10f);
        }
    }
}
