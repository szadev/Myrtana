using System;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>lighting-new (Docs/remaster-world.md section 21): the "New" value of the Modern lighting row.</summary>
    [TestFixture]
    public class WorldLightNewTests
    {
        [TearDown]
        public void Reset()
        {
            WorldRemasterLook.Current = WorldLookSettings.Default;
            WorldGi.ModernMode = 0;
        }

        [Test]
        public void Mode_ThreeValues_SoftKeepsTheMatchedExposure_NewHasItsOwn()
        {
            Assert.IsFalse(WorldGi.New);
            WorldGi.ModernMode = 1;
            Assert.IsTrue(WorldGi.Modern); Assert.IsTrue(WorldGi.Soft); Assert.IsFalse(WorldGi.New);
            Assert.Greater(WorldGi.ModernGlobal()[0], 0, "Soft = section 17");
            Assert.AreEqual(0f, WorldLightNew.RatioGlobal()[0]);
            WorldGi.ModernMode = 2;
            Assert.IsTrue(WorldGi.Modern, "GI + SSGI run under New too");
            Assert.IsTrue(WorldGi.Wanted);
            Assert.IsTrue(WorldSsgi.Wanted);
            Assert.IsTrue(WorldGi.New); Assert.IsFalse(WorldGi.Soft);
            Assert.AreEqual(0f, WorldGi.ModernGlobal()[0], "no matched exposure / lowered fill branch under New");
            Assert.AreEqual(1f, WorldLightNew.RatioGlobal()[0]);
            Assert.AreEqual(WorldLookSettings.Default.v2NewGain, WorldGi.Gain);
            WorldGi.ModernOption = true;
            Assert.AreEqual(2, WorldGi.ModernMode, "the bool setter keeps a chosen mode");
            WorldGi.ModernOption = false;
            Assert.AreEqual(0, WorldGi.ModernMode);
        }

        [Test]
        public void New_BelowHighQuality_FallsBackToThePlainRemasterLight()
        {
            // fix-171: Lighting quality Medium / Low set v2GiStrength = 0 -> Soft / New are off, every New uniform is 0
            WorldRemasterLook.Current = LightingQualityLevels.Apply(WorldLookSettings.Default, LightingQuality.Medium);
            WorldGi.ModernMode = 2;
            Assert.IsFalse(WorldGi.Wanted); Assert.IsFalse(WorldGi.Modern); Assert.IsFalse(WorldGi.New); Assert.IsFalse(WorldGi.Soft);
            Assert.AreEqual(0f, WorldLightNew.RatioGlobal()[0], "New ratio branch off");
            Assert.AreEqual(0f, WorldGi.ModernGlobal()[0], "no lowered fill without the GI");
            StringAssert.Contains("Высокое", RemasterOptions.ModernModeValue(2, true, "Russian", giAllowed: false));
            Assert.AreEqual("Новое", RemasterOptions.ModernModeValue(2, true, "Russian"));
            WorldRemasterLook.Current = LightingQualityLevels.Apply(WorldLookSettings.Default, LightingQuality.High);
            Assert.IsTrue(WorldGi.New, "High keeps New");
        }

        [Test]
        public void ParseMode_EnvValues()
        {
            Assert.AreEqual(2, G3WorldGiParse("new"));
            Assert.AreEqual(2, G3WorldGiParse("2"));
            Assert.AreEqual(1, G3WorldGiParse("soft"));
            Assert.AreEqual(1, G3WorldGiParse("1"));
            Assert.AreEqual(0, G3WorldGiParse("off"));
            Assert.IsNull(G3WorldGiParse(""));
        }

        // mirror of G3WorldGi.ParseMode (engine type, not in the test build)
        static int? G3WorldGiParse(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim().ToLowerInvariant();
            if (v == "2" || v == "new" || v == "neu") return 2;
            if (v == "soft") return 1;
            var f = WorldRemasterSwitch.ParseFlag(v);
            return f == null ? (int?)null : f.Value ? 1 : 0;
        }

        [Test]
        public void ModeValueTexts()
        {
            Assert.AreEqual("Выкл.", RemasterOptions.ModernModeValue(0, true, "Russian"));
            Assert.AreEqual("Мягкое", RemasterOptions.ModernModeValue(1, true, "Russian"));
            Assert.AreEqual("Новое", RemasterOptions.ModernModeValue(2, true, "Russian"));
            Assert.AreEqual("New (with Lighting)", RemasterOptions.ModernModeValue(2, false, "English"));
        }

        static float[][] UniformSky(int n, float r, float g, float b)
        {
            var f = new float[6][];
            for (int k = 0; k < 6; k++)
            {
                f[k] = new float[n * n * 3];
                for (int i = 0; i < n * n; i++) { f[k][i * 3] = r; f[k][i * 3 + 1] = g; f[k][i * 3 + 2] = b; }
            }
            return f;
        }

        [Test]
        public void SolidAngles_SumToTheSphere()
        {
            double s = 0;
            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++) s += WorldLightNew.TexelSolidAngle(i, j, 8);
            Assert.AreEqual(4 * Math.PI, 6 * s, 1e-3);
        }

        [Test]
        public void SkyIrradiance_UniformSky_UpGetsAll_SidesHalf_DownNothing()
        {
            var irr = WorldLightNew.SkyIrradiance(UniformSky(16, 0.5f, 0.6f, 0.8f), 16);
            Assert.AreEqual(0.5f, irr[2][0], 0.01f, "up face sees the whole sky: L");
            Assert.AreEqual(0.8f, irr[2][2], 0.015f);
            Assert.AreEqual(0.25f, irr[0][0], 0.01f, "a wall sees half its lobe above the horizon");
            Assert.AreEqual(irr[0][0], irr[5][0], 1e-3f);
            Assert.AreEqual(0f, irr[3][0], 1e-4f, "below the horizon is the bounce's part");
        }

        [Test]
        public void SkyIrradiance_CapsTheSunDisc()
        {
            var f = UniformSky(16, 0.4f, 0.4f, 0.4f);
            for (int c = 0; c < 3; c++) f[2][(8 * 16 + 8) * 3 + c] = 5000f;   // a sun texel straight up
            var irr = WorldLightNew.SkyIrradiance(f, 16);
            Assert.Less(irr[2][0], 0.45f, "the sun is the direct term, not sky light");
        }

        [Test]
        public void FaceDir_UpFaceAndSides()
        {
            Assert.AreEqual(1f, WorldLightNew.FaceDir(2, 0, 0).y);
            Assert.Greater(WorldLightNew.FaceDir(0, 0, -1).y, 0, "+x face: top row (v = -1) looks up");
            Assert.Less(WorldLightNew.FaceDir(4, 0, 1).y, 0);
        }

        [Test]
        public void Ambient_OpenField_IsTheSky_Cave_IsBounceAndFloor()
        {
            var s = WorldLookSettings.Default;
            float open = WorldLightNew.Ambient(0.5f, 1, 0, 1, 1, 0.05f, 0, 0, 0.4f);
            Assert.AreEqual(0.5f * s.v2NewSky + 0.05f + s.v2NewFloor * 0.4f, open, 1e-4f);
            float cave = WorldLightNew.Ambient(0.5f, 0, 1, 1, 0.6f, 0.02f, 0, 0.01f, 0.4f);
            Assert.AreEqual(0.02f * 0.6f + 0.01f * s.v2NewSsgiGain + s.v2NewFloor * 0.4f, cave, 1e-4f);
            Assert.Greater(cave, 0, "never pitch black");
            float far = WorldLightNew.Ambient(0.5f, 0, 0.5f, 0, 1, 9, 0.1f, 0, 0);
            Assert.AreEqual(0.25f * s.v2NewSky + 0.1f, far, 1e-4f, "outside the window: map occlusion + flat ground bounce");
        }

        [Test]
        public void Ratio_NotCappedAtTheMatchedModesLimit()
        {
            Assert.Greater(WorldLightNew.Ratio(1.5f, 0, 0, 0.5f, 0, 0), 1.15f);
            Assert.AreEqual(WorldLookSettings.Default.v2NewMaxRatio, WorldLightNew.Ratio(1000, 0, 0, 0.1f, 0, 0));
            Assert.AreEqual(0.5f, WorldLightNew.Ratio(0.1f, 0.4f, 0, 0.5f, 0.5f, 0), 1e-5f);
        }

        [Test]
        public void Histogram_BinsAndPercentileMean()
        {
            Assert.AreEqual(0, WorldLightNew.Bin(0));
            Assert.AreEqual(WorldLightNew.Bins - 1, WorldLightNew.Bin(1e6f));
            var bins = new float[WorldLightNew.Bins];
            int b = WorldLightNew.Bin(0.1f);
            bins[b] = 100;
            Assert.AreEqual(WorldLightNew.BinLog(b), WorldLightNew.MeanLog(bins, 0.4f, 0.95f), 1e-4f);
            bins[WorldLightNew.Bins - 1] = 3;   // a few highlights (sun, sky) beyond the high share do not move it
            bins[0] = 30;                        // dark pixels below the low share neither
            Assert.AreEqual(WorldLightNew.BinLog(b), WorldLightNew.MeanLog(bins, 0.4f, 0.95f), 0.05f);
        }

        [Test]
        public void TargetEv_KeyAndClamps()
        {
            var s = WorldLookSettings.Default;
            Assert.AreEqual(s.v2NewBiasEv, WorldLightNew.TargetEv((float)Math.Log(s.v2NewKey, 2)), 1e-4f);
            Assert.AreEqual(s.v2NewMaxEv, WorldLightNew.TargetEv(-14), "a cave is not lit to noon");
            Assert.AreEqual(s.v2NewMinEv, WorldLightNew.TargetEv(4));
            Assert.Greater(WorldLightNew.TargetEv(-6), 0, "dark scene -> brighter");
        }

        [Test]
        public void Adapt_SlowUp_FastDown_NoOvershoot()
        {
            var s = WorldLookSettings.Default;
            float up = WorldLightNew.Adapt(0, 2, 0.1f);
            Assert.That(up, Is.GreaterThan(0).And.LessThanOrEqualTo(s.v2NewSpeedUp * 0.1f + 1e-5f));
            float down = WorldLightNew.Adapt(0, -2, 0.1f);
            Assert.Greater(Math.Abs(down), Math.Abs(up) - 1e-5f);
            float e = 0;
            for (int i = 0; i < 200; i++) e = WorldLightNew.Adapt(e, 1, 0.05f);
            Assert.AreEqual(1f, e, 0.01f);
            Assert.LessOrEqual(e, 1f);
        }

        [Test]
        public void Neutral_KeepsBaseColours_CompressesHighlights()
        {
            var c = WorldLightNew.Neutral(0.3f, 0.2f, 0.1f);
            Assert.AreEqual(0.26f, c.r, 1e-4f, "below the knee: offset only");
            Assert.AreEqual((0.3f - 0.04f) / (0.1f - 0.04f), (c.r) / (c.b), 1e-3f);
            var h = WorldLightNew.Neutral(8f, 4f, 2f);
            Assert.LessOrEqual(h.r, 1f);
            Assert.Greater(h.b / h.r, 2f / 8f, "path to white: highlights desaturate");
            Assert.Less(WorldLightNew.Aces(100), 1.0001f);
        }

        [Test]
        public void Grade_Regions_GoldenHour_Night()
        {
            var m = WorldLightNew.Grade("Myrtana", 12, true, 1, 0);
            Assert.AreEqual(1f, WorldLightNew.Lum(m.r, m.g, m.b), 1e-4f, "white balance keeps luminance");
            var n = WorldLightNew.Grade("Nordmar", 12, true, 1, 0);
            var v = WorldLightNew.Grade("Varant", 12, true, 1, 0);
            Assert.Greater(n.b / n.r, m.b / m.r, "Nordmar cooler");
            Assert.Less(v.b / v.r, m.b / m.r, "Varant warmer");
            var gold = WorldLightNew.Grade("Myrtana", 19.5f, true, 0.2f, 0);
            Assert.Less(gold.b / gold.r, m.b / m.r, "low sun warms");
            Assert.Greater(WorldLightNew.Purkinje(-11), 0.5f * WorldLookSettings.Default.v2NewPurkinje);
            Assert.AreEqual(0f, WorldLightNew.Purkinje(-3), 1e-5f, "day: no rod shift");
        }

        [Test]
        public void Settings_Clamped_AndInTheShippedJson()
        {
            var cl = new WorldLookSettings { v2NewTonemap = 9, v2NewMaxEv = 99, v2NewLowPercent = 0.9f, v2NewHighPercent = 0.2f }.Clamped();
            Assert.AreEqual(2, cl.v2NewTonemap);
            Assert.AreEqual(8f, cl.v2NewMaxEv);
            Assert.Greater(cl.v2NewHighPercent, cl.v2NewLowPercent);
            string json = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));
            foreach (var k in new[] { "\"v2NewSky\"", "\"v2NewKey\"", "\"v2NewTonemap\"", "\"v2NewPurkinje\"" }) StringAssert.Contains(k, json);
        }

        static string Repo()
        {
            var d = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Assets"))) d = d.Parent;
            return d?.FullName ?? ".";
        }
    }
}
