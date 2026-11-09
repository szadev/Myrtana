using System;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>grade-1 (Docs/remaster-world.md section 27): natural saturation, no purple sky, readable night.</summary>
    [TestFixture]
    public class WorldGradeTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static float HsvS(float[] c) { float mx = Math.Max(c[0], Math.Max(c[1], c[2])), mn = Math.Min(c[0], Math.Min(c[1], c[2])); return (mx - mn) / Math.Max(mx, 1e-4f); }

        [Test]
        public void Vibrance_GreysAndMutedUntouched_VividCompressed_MaxKept()
        {
            var g = WorldRemasterLook.Vibrance(0.4f, 0.4f, 0.4f, 0, 0.5f, 0.45f);
            Assert.AreEqual(0.4f, g[0], 1e-5f); Assert.AreEqual(0.4f, g[2], 1e-5f);
            var muted = WorldRemasterLook.Vibrance(0.5f, 0.45f, 0.4f, 0, 0.5f, 0.45f);
            Assert.AreEqual(0.4f, muted[2], 1e-5f, "below the knee: as is");
            var vivid = WorldRemasterLook.Vibrance(0.9f, 0.45f, 0.1f, 0, 0.5f, 0.45f);
            Assert.AreEqual(0.9f, vivid[0], 1e-5f, "max channel kept");
            Assert.Less(HsvS(vivid), HsvS(new[] { 0.9f, 0.45f, 0.1f }));
            Assert.Greater(HsvS(vivid), 0.45f, "still above the knee: monotonic");
            Assert.Greater(vivid[1] / vivid[0], 0.45f / 0.9f - 1e-5f, "hue order kept");
            var full = WorldRemasterLook.Vibrance(1f, 0f, 0f, 0, 1f, 0.45f);
            Assert.AreEqual(0.45f + 0.55f * 0.5f, HsvS(full), 1e-4f, "full strength: s 1 -> knee + half the rest");
            var off = WorldRemasterLook.Vibrance(0.9f, 0.45f, 0.1f, 0, 0, 0.45f);
            Assert.AreEqual(0.1f, off[2], 1e-5f, "0 = off");
            var less = WorldRemasterLook.Vibrance(0.6f, 0.4f, 0.2f, -0.5f, 0, 0.45f);
            Assert.Less(HsvS(less), HsvS(new[] { 0.6f, 0.4f, 0.2f }), "negative saturation desaturates");
        }

        [Test]
        public void Defaults_Neutral_SaturationKnobsInTheJson()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(0f, d.saturation); Assert.AreEqual(0f, d.gradeSaturation); Assert.AreEqual(0f, d.v2NewSaturation);
            Assert.Greater(d.vibrance, 0f);
            var m = WorldLightNew.Grade("Myrtana", 12, true, 1, 0);
            Assert.AreEqual(0f, m.sat, 1e-5f, "Myrtana noon: neutral");
            var gold = WorldLightNew.Grade("Myrtana", 19.5f, true, 0f, 0);
            Assert.LessOrEqual(gold.sat, 0.025f, "golden hour barely richer");
            Assert.AreEqual(4, WorldRemasterLook.VibranceGlobal.Length);
            var cl = new WorldLookSettings { saturation = 5, vibrance = -1, vibranceKnee = 2, skyDePurple = 3, v2NewToe = 9, v2NewNightFloor = -1, v2RockMoss = 7 }.Clamped();
            Assert.AreEqual(1f, cl.saturation); Assert.AreEqual(0f, cl.vibrance); Assert.AreEqual(0.95f, cl.vibranceKnee);
            Assert.AreEqual(1f, cl.skyDePurple); Assert.AreEqual(1f, cl.v2NewToe); Assert.AreEqual(0f, cl.v2NewNightFloor); Assert.AreEqual(1f, cl.v2RockMoss);
            string json = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));
            foreach (var k in new[] { "\"saturation\"", "\"vibrance\"", "\"vibranceKnee\"", "\"skyDePurple\"", "\"v2NewToe\"", "\"v2NewNightFloor\"", "\"v2RockMoss\"", "\"v2NewWhite\"", "\"atmoHazeBright\"", "\"v2NewNightMaxEv\"" }) StringAssert.Contains(k, json);
            Assert.AreEqual(0.3f, WorldV2.WeatherGlobal(0)[3], 1e-5f, "natural-rock moss scaled down");
        }

        [Test]
        public void NeutralToe_KeepsHueAndSaturation_LessCrush()
        {
            var k = WorldLightNew.Neutral(0.03f, 0.05f, 0.02f);
            var t = WorldLightNew.NeutralToe(0.03f, 0.05f, 0.02f, 0.5f);
            Assert.AreEqual(0.03f / 0.05f, t.r / t.g, 1e-4f, "hue kept");
            Assert.Greater(t.b / t.g, k.b / k.g, "Khronos' per-channel toe saturated the darks");
            Assert.Greater(WorldLightNew.Lum(t.r, t.g, t.b), WorldLightNew.Lum(k.r, k.g, k.b), "darks less crushed");
            var n = WorldLightNew.NeutralToe(0.02f, 0.025f, 0.04f, 0.5f);   // a night blue
            Assert.Greater(WorldLightNew.Lum(n.r, n.g, n.b), 0.5f * WorldLightNew.Lum(0.02f, 0.025f, 0.04f));
            var z = WorldLightNew.NeutralToe(0.3f, 0.2f, 0.1f, 0);
            Assert.AreEqual(0.3f, z.r, 1e-5f, "toe 0: identity below the knee");
            var h = WorldLightNew.NeutralToe(8f, 4f, 2f, 0.5f);
            Assert.LessOrEqual(h.r, 1f);
        }

        [Test]
        public void NightFloor_OnlyInTheNight()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(WorldLightNew.Floor(0), WorldLightNew.Floor(0, 0), 1e-6f, "day: as before");
            Assert.AreEqual(d.v2NewFloor + d.v2NewNightFloor, WorldLightNew.Floor(0, 1), 1e-6f);
            Assert.Greater(WorldLightNew.Floor(0, 1), 5 * d.v2NewFloor, "night: G3's moonlit fill reads");
            Assert.AreEqual(WorldLightNew.Floor(1), WorldLightNew.Floor(1, 1), 1e-6f, "twilight keeps its own floor");
            Assert.AreEqual(0f, WorldLightNew.Night(true)); Assert.AreEqual(1f, WorldLightNew.Night(false));
            Assert.AreEqual(d.v2NewMaxEv, WorldLightNew.MaxEv(0), 1e-6f, "day cap as before");
            Assert.AreEqual(d.v2NewNightMaxEv, WorldLightNew.MaxEv(1), 1e-6f);
            Assert.Greater(WorldLightNew.MaxEv(1), d.v2NewMaxEv, "night may expose further");
        }

        [Test]
        public void DePurple_LavenderToBlueGrey_PinkToSalmon_OthersKept()
        {
            float L(float[] c) => 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];
            var lav = DesertAtmo.DePurple(0.6f, 0.55f, 0.8f, 1);
            Assert.AreEqual(L(new[] { 0.6f, 0.55f, 0.8f }), L(lav), 1e-4f, "luminance kept");
            Assert.LessOrEqual(lav[0], lav[1] + 1e-4f, "no red over green: not purple");
            Assert.Greater(lav[2], lav[1], "still blue");
            var pink = DesertAtmo.DePurple(0.9f, 0.55f, 0.6f, 1);
            Assert.Less(pink[2] - pink[1], 0.6f - 0.55f, "pink -> salmon (less blue over green)");
            Assert.Greater(pink[0], pink[2], "still warm");
            var blue = DesertAtmo.DePurple(0.3f, 0.5f, 0.9f, 1);
            Assert.AreEqual(0.3f, blue[0], 1e-5f); Assert.AreEqual(0.9f, blue[2], 1e-5f);
            var orange = DesertAtmo.DePurple(0.9f, 0.5f, 0.2f, 1);
            Assert.AreEqual(0.2f, orange[2], 1e-5f);
            var off = DesertAtmo.DePurple(0.6f, 0.55f, 0.8f, 0);
            Assert.AreEqual(0.6f, off[0], 1e-5f);
        }

        [Test]
        public void InteriorFloor_ClosedHallKeepsHalfOfG3Light_OpenGroundUntouched()
        {
            // qa-1 Bakaresh Beliar throne hall: ratio ~0.18 (the 0.12 floor + what was left), sky hidden all round
            Assert.AreEqual(0.5f, WorldV2Light.InteriorFloor(0.18f, 1f, 0.5f), 1e-5f);
            Assert.AreEqual(0.8f, WorldV2Light.InteriorFloor(0.8f, 1f, 0.5f), 1e-5f, "a lit interior is not darkened");
            Assert.AreEqual(0.2f, WorldV2Light.InteriorFloor(0.2f, 0.4f, 0.5f), 1e-5f, "open ground / a sun shadow under the sky (no ceiling): no floor");
            Assert.AreEqual(0.18f, WorldV2Light.InteriorFloor(0.18f, 1f, 0f), 1e-5f, "off");
            Assert.AreEqual(1f, new WorldLookSettings { v2InteriorFloor = 3 }.Clamped().v2InteriorFloor);
            string json = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));
            StringAssert.Contains("\"v2InteriorFloor\": 0.5", json);
        }

        static string Repo()
        {
            var d = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Assets"))) d = d.Parent;
            return d?.FullName ?? ".";
        }
    }
}
