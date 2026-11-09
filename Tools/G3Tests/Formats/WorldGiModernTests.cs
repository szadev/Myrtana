using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>remaster-gi2 (Docs/remaster-world.md section 17): Modern lighting (lower fill, traced visibility, bounce, matched exposure).</summary>
    [TestFixture]
    public class WorldGiModernTests
    {
        [TearDown]
        public void Reset()
        {
            WorldRemasterLook.Current = WorldLookSettings.Default;
            WorldGi.ModernOption = false;
        }

        [Test]
        public void Option_DefaultOff_AndTurnsTheGiOnWithIt()
        {
            Assert.IsFalse(WorldGi.ModernOption);
            Assert.IsFalse(WorldGi.Modern);
            Assert.IsFalse(WorldGi.Wanted, "v2Gi stays off by default");
            WorldGi.ModernOption = true;
            Assert.IsTrue(WorldGi.Modern);
            Assert.IsTrue(WorldGi.Wanted, "Modern lighting needs the probes");
            Assert.IsTrue(WorldV2Light.PassWanted);
            var s = WorldLookSettings.Default; s.v2 = false; WorldRemasterLook.Current = s;
            Assert.IsFalse(WorldGi.Modern, "json v2 false = r3 look, nothing of v2");
        }

        [Test]
        public void Modern_UsesItsOwnGainTint_AndNoKeepFloor()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(d.v2GiGain, WorldGi.Gain);
            var gi = WorldLookSettings.Default; gi.v2Gi = true; WorldRemasterLook.Current = gi;
            Assert.AreEqual(d.v2GiKeep, WorldGi.GiGlobal(0)[3]);
            WorldGi.ModernOption = true;
            Assert.AreEqual(d.v2ModernGain, WorldGi.Gain);
            Assert.AreEqual(d.v2ModernTint, WorldGi.Tint);
            Assert.AreEqual(0f, WorldGi.GiGlobal(0)[3], "the keep floor would hide the occlusion");
            var m = WorldGi.ModernGlobal();
            Assert.AreEqual(d.v2ModernAmbient, m[0]); Assert.AreEqual(d.v2ModernOcclusion, m[1]);
            WorldGi.ModernOption = false;
            Assert.AreEqual(0f, WorldGi.ModernGlobal()[0], "off: the shader branch is not taken");
        }

        [Test]
        public void OpenField_KeepsTheFill_OnlyRealOccludersTakeIt()
        {
            // a side face in an open meadow sees half the sphere's sky: relative visibility 1 (G3's cube holds the horizon)
            Assert.AreEqual(1f, WorldGi.RelativeVisibility(0.5f, 0f), 1e-5f);
            Assert.AreEqual(1f, WorldGi.RelativeVisibility(1f, 1f), 1e-5f);
            Assert.AreEqual(0.5f, WorldGi.RelativeVisibility(0.5f, 1f), 1e-5f, "a floor under half a roof");
            Assert.AreEqual(0.25f, WorldGi.OpenVisibility(-1f), "down faces: floor of the divisor");
            WorldGi.ModernOption = true;
            float k = WorldLookSettings.Default.v2ModernAmbient;
            float up = k * WorldGi.SkyDirection(1f), side = k * WorldGi.SkyDirection(0f);
            Assert.AreEqual(k, side, 1e-6f, "side faces keep the scaled fill");
            Assert.Greater(up, side, "directional sky: tops lit");
            Assert.Less(WorldGi.SkyDirection(-1f), 1f, "undersides in shade");
            // open (vis 1 up), no bounce: just the scaled fill
            Assert.AreEqual(up * (1f + 0.2f), WorldGi.ModernAmbient(1f, 0.2f, 0, 1f, 1f, 0, 0, 1), 1e-5f);
            // cave (vis 0): only the scaled ground lobe + the bounce
            Assert.AreEqual(up * 0.2f + 0.05f, WorldGi.ModernAmbient(1f, 0.2f, 0, 0f, 1f, 0.05f, 0, 1), 1e-5f);
            // outside the probe window: the map's occlusion at full strength, the screen bounce scaled like the fill
            Assert.AreEqual(up * (1f * 0.4f + 0.2f) + k * 0.1f, WorldGi.ModernAmbient(1f, 0.2f, 0.6f, 1f, 1f, 9f, 0.1f, 0), 1e-5f);
        }

        [Test]
        public void Exposure_RestoresTheMean_WithinLimits()
        {
            Assert.AreEqual((float)System.Math.Pow(1.5, 1.43), WorldGi.Exposure(0.3f, 0.2f), 1e-4f);
            Assert.AreEqual(0f, WorldGi.DisplayLum(0f)); Assert.AreEqual(0.99f, WorldGi.DisplayLum(1f), 1e-4f);
            Assert.AreEqual(WorldLookSettings.Default.v2ModernMaxExposure, WorldGi.Exposure(1f, 0.01f), 1e-5f);
            Assert.AreEqual(WorldLookSettings.Default.v2ModernMinExposure, WorldGi.Exposure(0.1f, 1f), 1e-5f);
            Assert.AreEqual(1f, WorldGi.Exposure(0, 0), "empty frame (all sky)");
            var s = WorldLookSettings.Default; s.v2ModernExposure = 0; WorldRemasterLook.Current = s;
            Assert.AreEqual(1f, WorldGi.Exposure(0.3f, 0.2f), 1e-5f, "match 0 = no compensation");
        }

        [Test]
        public void Settings_Clamped()
        {
            var s = WorldLookSettings.Default;
            s.v2ModernAmbient = -1; s.v2ModernMaxExposure = 100; s.v2ModernMinExposure = 0; s.v2ModernGain = float.NaN;
            var c = s.Clamped();
            Assert.AreEqual(0.05f, c.v2ModernAmbient); Assert.AreEqual(6f, c.v2ModernMaxExposure);
            Assert.AreEqual(0.25f, c.v2ModernMinExposure); Assert.AreEqual(0f, c.v2ModernGain);
        }

        [Test]
        public void OptionTexts()
        {
            Assert.AreEqual("Современное освещение", RemasterOptions.ModernName("Russian"));
            Assert.AreEqual("Modern lighting", RemasterOptions.ModernName("English"));
            Assert.AreEqual("Вкл. (с Освещением)", RemasterOptions.ModernValue(true, false, "Russian"));
            Assert.AreEqual("Off", RemasterOptions.ModernValue(false, false, "English"));
        }
    }
}
