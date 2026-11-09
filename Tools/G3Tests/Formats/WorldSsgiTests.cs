using System;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>remaster-ssgi (Docs/remaster-world.md section 19): visibility-bitmask screen-space GI of Modern lighting.</summary>
    [TestFixture]
    public class WorldSsgiTests
    {
        [TearDown]
        public void Reset()
        {
            WorldRemasterLook.Current = WorldLookSettings.Default;
            WorldGi.ModernOption = false;
        }

        [Test]
        public void Wanted_OnlyWithModernLighting()
        {
            Assert.IsFalse(WorldSsgi.Wanted, "Modern lighting is off by default");
            Assert.AreEqual(0, WorldSsgi.Global()[0]);
            Assert.AreEqual(0, WorldSsgi.CostMs());
            WorldGi.ModernOption = true;
            Assert.IsTrue(WorldSsgi.Wanted);
            Assert.AreEqual(WorldLookSettings.Default.v2SsgiRadius, WorldSsgi.Global()[0]);
            var s = WorldLookSettings.Default; s.v2Ssgi = false; WorldRemasterLook.Current = s;
            Assert.IsFalse(WorldSsgi.Wanted, "json switch");
        }

        [Test]
        public void SectorMask_CoversTouchedSectors()
        {
            Assert.AreEqual(0xFFFFFFFFu, WorldSsgi.SectorMask(0, 1));
            Assert.AreEqual(1u, WorldSsgi.SectorMask(0, 0.01f));
            Assert.AreEqual(1u << 31, WorldSsgi.SectorMask(0.99f, 1));
            Assert.AreEqual(WorldSsgi.SectorMask(0.2f, 0.4f), WorldSsgi.SectorMask(0.4f, 0.2f), "order free");
            Assert.AreEqual(0u, WorldSsgi.SectorMask(0.5f, 0.5f), "empty interval");
            Assert.AreEqual(0u, WorldSsgi.SectorMask(1.2f, 1.5f), "beyond the horizon");
            Assert.AreEqual(16, WorldSsgi.CountBits(WorldSsgi.SectorMask(0, 0.5f)));
        }

        [Test]
        public void CountBits_MatchesNaive()
        {
            var r = new Random(3);
            for (int k = 0; k < 200; k++)
            {
                uint v = (uint)r.Next() ^ ((uint)r.Next() << 16);
                int n = 0; for (int i = 0; i < 32; i++) n += (int)((v >> i) & 1);
                Assert.AreEqual(n, WorldSsgi.CountBits(v));
            }
        }

        [Test]
        public void CosShare_FullHemisphereIsOne()
        {
            // the 32 sectors of a slice, each at its centre angle from the normal: sum of cos-weighted shares = 1
            double sum = 0;
            for (int i = 0; i < 32; i++) sum += WorldSsgi.CosShare(1, (float)((i + 0.5) / 32 * Math.PI - Math.PI / 2));
            Assert.AreEqual(1.0, sum, 0.01);
            Assert.AreEqual(0, WorldSsgi.CosShare(4, (float)Math.PI), "behind the hemisphere: nothing");
        }

        [Test]
        public void HemiUnit_HorizonsAndNormal()
        {
            Assert.AreEqual(0.5f, WorldSsgi.HemiUnit(0.3f, 0.3f), 1e-5f, "along the normal = middle");
            Assert.AreEqual(0f, WorldSsgi.HemiUnit(-(float)Math.PI / 2, 0), 1e-5f);
            Assert.AreEqual(1f, WorldSsgi.HemiUnit((float)Math.PI, 0), 1e-5f, "clamped");
        }

        [Test]
        public void Thickness_RadiusAndFade()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(d.v2SsgiThickness, WorldSsgi.Thickness(5));
            Assert.AreEqual(d.v2SsgiThickness * 2, WorldSsgi.Thickness(30), 1e-5f, "grows past 15 m");
            Assert.AreEqual(d.v2SsgiMaxPixels, WorldSsgi.RadiusPixels(0.5f, 500), "capped near the camera");
            Assert.Less(WorldSsgi.RadiusPixels(200, 500), 20);
            Assert.AreEqual(1, WorldSsgi.Fade(10));
            Assert.AreEqual(0, WorldSsgi.Fade(d.v2SsgiFade));
        }

        [Test]
        public void Temporal_BlendAndHistory()
        {
            Assert.AreEqual(1, WorldSsgi.Blend(0), "fresh = this frame");
            Assert.AreEqual(0.5f, WorldSsgi.Blend(1), 1e-5f);
            Assert.AreEqual(1 - WorldLookSettings.Default.v2SsgiHistory, WorldSsgi.Blend(30), 1e-5f, "steady blend");
            Assert.IsTrue(WorldSsgi.HistoryValid(10, 10.3f));
            Assert.IsFalse(WorldSsgi.HistoryValid(10, 12));
            Assert.IsFalse(WorldSsgi.HistoryValid(10, 0), "no history");
        }

        [Test]
        public void FillVisibility_MultiBounceNeverDarkerThanVisibility()
        {
            for (float v = 0; v <= 1; v += 0.1f) Assert.GreaterOrEqual(WorldSsgi.MultiBounce(v), v - 1e-6f);
            Assert.AreEqual(1, WorldSsgi.MultiBounce(1), 0.01f);
            Assert.AreEqual(1, WorldSsgi.FillVisibility(0.2f, 10000), "faded out far away");
            Assert.Less(WorldSsgi.FillVisibility(0.2f, 5), 0.5f);
            var p1 = WorldLookSettings.Default; p1.v2SsgiAoPower = 1; p1.v2SsgiMultiBounce = 1; WorldRemasterLook.Current = p1;
            Assert.AreEqual(WorldSsgi.MultiBounce(0.5f), WorldSsgi.FillVisibility(0.5f, 5), 1e-5f, "power 1, full multi-bounce");
            p1.v2SsgiMultiBounce = 0; WorldRemasterLook.Current = p1;
            Assert.AreEqual(0.5f, WorldSsgi.FillVisibility(0.5f, 5), 1e-5f, "physical");
            var s = WorldLookSettings.Default; s.v2SsgiAo = 0; WorldRemasterLook.Current = s;
            Assert.AreEqual(1, WorldSsgi.FillVisibility(0.2f, 5), "AO strength 0");
        }

        [Test]
        public void Settings_Clamped()
        {
            var s = WorldLookSettings.Default;
            s.v2SsgiSlices = 99; s.v2SsgiSteps = 0; s.v2SsgiHistory = 1.5f; s.v2SsgiAo = -1;
            s = s.Clamped();
            Assert.AreEqual(8, s.v2SsgiSlices);
            Assert.AreEqual(2, s.v2SsgiSteps);
            Assert.AreEqual(0.98f, s.v2SsgiHistory, 1e-5f);
            Assert.AreEqual(0, s.v2SsgiAo);
        }
    }
}
