using System;
using System.IO;
using System.Runtime.CompilerServices;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// World remaster r3 (Docs/remaster-world.md section 9): the grade runs in the remaster's own full-screen pass (the game
    /// camera has no URP post-processing, so r2's grading Volume never applied), its maths, and the r3 aerial defaults.
    /// </summary>
    [TestFixture]
    public class WorldRemasterR3Tests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        static float Lum(float[] c) => 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];
        static float Sat(float[] c) { float mx = Math.Max(c[0], Math.Max(c[1], c[2])), mn = Math.Min(c[0], Math.Min(c[1], c[2])); return mx > 0 ? (mx - mn) / mx : 0; }
        static float Srgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * (float)Math.Pow(v, 1 / 2.4) - 0.055f;
        static float[] Display(float[] c) => new[] { Srgb(c[0]), Srgb(c[1]), Srgb(c[2]) };

        [Test]
        public void Defaults_AerialFromRunner29_VistaPassed()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(0.1f, d.aerialLift);
            Assert.AreEqual(0.04f, d.aerialMax);
        }

        [Test]
        public void Grade_Off_IsIdentity()
        {
            WorldRemasterLook.Current = new WorldLookSettings { grade = false };
            CollectionAssert.AreEqual(new float[4], WorldRemasterLook.Grade);
            var o = WorldRemasterLook.GradeColor(0.3f, 0.12f, 0.05f);
            Assert.AreEqual(0.3f, o[0], 1e-4f);
            Assert.AreEqual(0.12f, o[1], 1e-4f);
            Assert.AreEqual(0.05f, o[2], 1e-4f);
        }

        [Test]
        public void Grade_Defaults_NeutralSaturation_DeeperBlacks_MidGreyKept()
        {
            // a stone-brown in linear: saturation up by roughly gradeSaturation %, luminance near-kept
            var brown = new[] { 0.20f, 0.12f, 0.06f };
            var g = WorldRemasterLook.GradeColor(brown[0], brown[1], brown[2]);
            float s0 = Sat(Display(brown)), s1 = Sat(Display(g));
            // grade-1: gradeSaturation 0 (was +10; the oversaturated look): contrast only, the saturation stays near
            Assert.AreEqual(s0, s1, s0 * 0.1f, "neutral saturation");
            Assert.AreEqual(Lum(brown), Lum(g), Lum(brown) * 0.08f, "below mid-grey: contrast + lift darken by ~6 %, no more");

            // mid-grey: the contrast pivot, no saturation to change; only the -0.01 display lift moves it slightly down
            var grey = WorldRemasterLook.GradeColor(0.18f, 0.18f, 0.18f);
            Assert.AreEqual(0.18f, grey[1], 0.01f);
            Assert.AreEqual(grey[0], grey[2], 1e-5f);

            // black point (ab_stats p2 ~ 0.04-0.12 in display): never lifted, pure black stays black
            foreach (float disp in new[] { 0.04f, 0.08f, 0.12f })
            {
                float lin = (float)Math.Pow((disp + 0.055) / 1.055, 2.4);
                var o = WorldRemasterLook.GradeColor(lin, lin, lin);
                Assert.Less(Srgb(o[1]), disp, $"black point {disp} lifted");
            }
            var black = WorldRemasterLook.GradeColor(0, 0, 0);
            Assert.AreEqual(0f, black[0] + black[1] + black[2], 1e-6f);
        }

        [Test]
        public void Grade_Clamped_NoNegativeOrNaN()
        {
            WorldRemasterLook.Current = new WorldLookSettings { gradeContrast = 500, gradeSaturation = 500, gradeShadows = -5, gradeGain = 5 };
            foreach (var c in new[] { new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 8f, 4f, 2f }, new[] { -1f, 0.5f, 0.2f } })
                foreach (float v in WorldRemasterLook.GradeColor(c[0], c[1], c[2]))
                    Assert.IsTrue(v >= 0 && !float.IsNaN(v) && !float.IsInfinity(v), $"{v} for ({c[0]},{c[1]},{c[2]})");
        }

        [Test]
        public void Grade_IsOwnPass_NotAUrpVolume()
        {
            string shader = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterAtmosphere.shader"));
            string look = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/G3WorldRemasterLook.cs"));
            StringAssert.Contains("Name \"G3RemasterGrade\"", shader);
            StringAssert.Contains("_G3Grade", shader);
            StringAssert.Contains("_G3Grade", look);
            StringAssert.DoesNotContain("AddComponent<Volume>", look, "a URP Volume does not reach the game camera (post-processing off)");
        }

        [Test]
        public void WorldBlend_CavityAroundTheMapsMeanHeight()
        {
            string s = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldBlend.shader"));
            StringAssert.DoesNotContain("(mask.b - 0.5h)", s, "r2 cavity around 0.5 shifted whole materials (baked mean heights 0.24-0.83)");
            StringAssert.Contains("(mask.b - hMean)", s);
        }
    }
}
