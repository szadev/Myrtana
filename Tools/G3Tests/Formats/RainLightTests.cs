using System;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-255 (Demo_2026-10-26c smoke shot, Montera forest in rain: the whole frame teal): G3's Rainy keys are cyan; the
    /// remaster pulls the overcast light to a neutral-cool grey, luminance kept, and leaves clear weather alone.
    /// </summary>
    [TestFixture]
    public class RainLightTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static float Lum(float[] c) => 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];
        static float Lin(float g) => (float)Math.Pow(g, 2.2);

        [Test]
        public void Overcast_Weight_RainFull_SunnyNone()
        {
            // G3Environment: overcast = (CloudThickness - 0.6) / 2.4; Rainy noon 1.664, Sunny noon 0.42
            Assert.AreEqual(0f, WorldV2.RainNeutralK(0f));
            Assert.AreEqual(WorldLookSettings.Default.v2RainNeutral, WorldV2.RainNeutralK((1.664f - 0.6f) / 2.4f), 1e-5f);
            Assert.Less(WorldV2.RainNeutralK(0.1f), WorldV2.RainNeutralK(0.3f), "eased in over a weather fade");
            WorldRemasterLook.Current = new WorldLookSettings { v2Sun = false };
            Assert.AreEqual(0f, WorldV2.RainNeutralK(0.44f), "v2 sun off: G3's colours");
        }

        [Test]
        public void RainyAmbient_CyanBecomesCoolGrey_LuminanceKept()
        {
            // G3 Rainy AmbientGeneral (0, 0.54, 0.71) after G3Environment's cap (~0.37 HSV saturation): (0.45, 0.54, 0.71)
            float[] c = { Lin(0.45f), Lin(0.54f), Lin(0.71f) };
            var n = WorldV2.RainNeutral(c[0], c[1], c[2], WorldV2.RainNeutralK(0.44f));
            Assert.AreEqual(Lum(c), Lum(n), 1e-4f, "luminance kept");
            Assert.Greater(n[0] / n[1], 0.85f, "red no longer missing (was " + (c[0] / c[1]).ToString("0.00") + ")");
            Assert.Less(n[2] / n[1], 1.3f, "cool, not cyan-blue");
            Assert.Greater(n[2], n[0], "still on the cool side");
            var grey = WorldV2.RainNeutral(0.2f, 0.2f, 0.2f, 1f);
            Assert.AreEqual(0.2f * WorldV2.RainGrey[2], grey[2], 1e-5f);
            Assert.AreEqual(1f, Lum(WorldV2.RainGrey), 1e-4f, "the target chroma has luminance 1");
        }

        [Test]
        public void ProbeScale_MovesMeanColour_Clamped()
        {
            var f = WorldV2.RainProbeScale(0.02f, 0.25f, 0.46f, 0.8f);
            var t = WorldV2.RainNeutral(0.02f, 0.25f, 0.46f, 0.8f);
            Assert.AreEqual(5f, f[0], 1e-5f, "near-zero red: clamped");
            Assert.AreEqual(t[1] / 0.25f, f[1], 1e-5f);
            Assert.AreEqual(t[2] / 0.46f, f[2], 1e-5f);
            var none = WorldV2.RainProbeScale(0.1f, 0.2f, 0.3f, 0f);
            Assert.AreEqual(1f, none[0], 1e-6f); Assert.AreEqual(1f, none[2], 1e-6f);
        }

        [Test]
        public void Purkinje_NightOnly()
        {
            float p = WorldLookSettings.Default.v2NewPurkinje;
            Assert.AreEqual(0f, WorldLightNew.PurkinjeStrength(0f, 0f), "rainy noon: no rod shift");
            Assert.AreEqual(p, WorldLightNew.PurkinjeStrength(0f, 1f), 1e-6f, "night");
            Assert.AreEqual(0f, WorldLightNew.PurkinjeStrength(1f, 1f), "twilight (fix-239)");
        }

        [Test]
        public void Json_HasRainNeutral()
        {
            var d = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Assets"))) d = d.Parent;
            var json = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));
            StringAssert.Contains("\"v2RainNeutral\"", json);
            Assert.AreEqual(1f, new WorldLookSettings { v2RainNeutral = 3 }.Clamped().v2RainNeutral);
        }
    }
}
