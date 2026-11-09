using System.IO;
using System.Runtime.CompilerServices;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>arch-2 (Docs/remaster-world.md 25.7): storm sand deposit on Varant cloth / ledges - rise, decay, rest, clock.</summary>
    [TestFixture]
    public class ArchSandTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        static float Run(float d, float storm, float hours, float step = 0.05f)
        {
            for (float t = 0; t < hours - 1e-4f; t += step) d = ArchSand.Step(d, storm, step);
            return d;
        }

        [Test]
        public void Rise_FullAfterAboutOneHourOfStorm()
        {
            float d = Run(0.25f, 1f, 0.5f);
            Assert.That(d, Is.InRange(0.7f, 0.8f), "half an hour: 0.25 + 0.5");
            Assert.AreEqual(1f, Run(0.25f, 1f, 1f), 1e-4f);
            Assert.AreEqual(1f, Run(0f, 1f, 1.05f), 1e-4f, "from bare cloth within ~1 h");
            Assert.That(Run(0.25f, 0.5f, 0.5f), Is.LessThan(d), "a half storm builds slower");
            Assert.AreEqual(1f, Run(0.9f, 1f, 5f), "capped at 1");
        }

        [Test]
        public void Decay_BackToRestOverOneToTwoDays()
        {
            float rest = WorldLookSettings.Default.archSandRest;
            Assert.AreEqual(0.25f, rest, 1e-6f);
            float d6 = Run(1f, 0f, 6f, 0.25f), d24 = Run(1f, 0f, 24f, 0.25f), d36 = Run(1f, 0f, 36f, 0.25f), d72 = Run(1f, 0f, 72f, 0.5f);
            Assert.That(d6, Is.GreaterThan(0.6f), "slowly blown off: most still there after 6 h");
            Assert.That(d24, Is.InRange(0.3f, 0.45f));
            Assert.That(d36 - rest, Is.LessThan(0.06f * (1 - rest)), "~5 % of the excess after archSandDecay");
            Assert.That(d72, Is.GreaterThanOrEqualTo(rest).And.LessThan(rest + 0.01f), "never below rest");
            Assert.That(d6, Is.GreaterThan(d24).And.GreaterThan(d36));
        }

        [Test]
        public void Step_IsDeterministicAndStepSizeIndependent()
        {
            Assert.AreEqual(Run(1f, 0f, 12f, 0.1f), Run(1f, 0f, 12f, 0.5f), 1e-3f, "exponential decay: same for any step");
            Assert.AreEqual(Run(0.25f, 1f, 0.6f, 0.01f), Run(0.25f, 1f, 0.6f, 0.2f), 1e-3f, "linear rise: same for any step");
            Assert.AreEqual(ArchSand.Step(0.5f, 0f, 3f), ArchSand.Step(0.5f, 0f, 3f));
            Assert.AreEqual(0.5f, ArchSand.Step(0.5f, 1f, 0f), "no time, no change");
            Assert.AreEqual(0.5f, ArchSand.Step(0.5f, 1f, float.NaN));
        }

        [Test]
        public void BelowRest_FillsUp_CalmThreshold()
        {
            Assert.AreEqual(0.25f, Run(0f, 0f, 2f), 1e-4f, "bare cloth gathers dust up to rest");
            Assert.AreEqual(Run(0.8f, 0f, 2f), Run(0.8f, 0.04f, 2f), 1e-5f, "a trace of storm counts as calm");
        }

        [Test]
        public void Clock_WrapsAndJumpsReinitialise()
        {
            Assert.AreEqual(0.5f, ArchSand.HoursBetween(10f, 10.5f), 1e-5f);
            Assert.AreEqual(0.2f, ArchSand.HoursBetween(23.9f, 0.1f), 1e-4f, "past midnight");
            Assert.IsNaN(ArchSand.HoursBetween(11f, 18f), "a set hour / sleep is a jump");
            Assert.IsNaN(ArchSand.HoursBetween(12f, 11.5f), "backwards");
            Assert.IsNaN(ArchSand.HoursBetween(float.NaN, 3f));
            Assert.AreEqual(1f, ArchSand.Initial(1f));
            Assert.AreEqual(0.25f, ArchSand.Initial(0.2f));
        }

        [Test]
        public void Knobs_PinGlobalAndWisps()
        {
            Assert.AreEqual(0.6f, ArchSand.Shown(0.6f));
            WorldRemasterLook.Current = new WorldLookSettings { archSandDeposit = 1f };
            Assert.AreEqual(1f, ArchSand.Shown(0.25f), "pinned (shots)");
            CollectionAssert.AreEqual(new[] { 1f, 1f, 0.25f, 1f }, ArchSand.Global(0.3f));
            WorldRemasterLook.Current = new WorldLookSettings { arch = false };
            CollectionAssert.AreEqual(new float[4], ArchSand.Global(0.3f), "arch off: sand-3's sand as before");
            WorldRemasterLook.Current = WorldLookSettings.Default;
            Assert.AreEqual(0f, ArchSand.WispAmount(0.35f, 1f, 1f), "no wisps under 0.4");
            Assert.AreEqual(0f, ArchSand.WispAmount(1f, 0.2f, 0f), "no wisps in calm air");
            Assert.That(ArchSand.WispAmount(1f, 0.9f, 0f), Is.GreaterThan(0.9f));
            WorldRemasterLook.Current = new WorldLookSettings { archSandWisps = 0 };
            Assert.AreEqual(0f, ArchSand.WispAmount(1f, 1f, 1f));
        }

        [Test]
        public void Shader_And_Runtime_Wired()
        {
            string sh = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader"));
            StringAssert.Contains("archSandCover", sh);
            StringAssert.Contains("_G3V2ArchSand", File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.hlsl")));
            string rt = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/G3WorldV2.cs"));
            StringAssert.Contains("_G3V2ArchSand", rt);
            StringAssert.Contains("G3ArchSandWisps.Tick", rt);
            string json = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));
            foreach (var k in new[] { "archClothSand", "archStoneSand", "archSandRise", "archSandDecay", "archSandRest", "archSandDeposit", "archSandWisps" })
                StringAssert.Contains("\"" + k + "\"", json, k);
        }
    }
}
