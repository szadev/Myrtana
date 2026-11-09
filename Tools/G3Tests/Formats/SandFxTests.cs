using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fx-1 (Docs/remaster-world.md 22.11): blown-sand streamers only on open loose sand (the relief's sand mask), footstep dust
    /// by speed, the near / low-sun mega-ripple cut, knobs.
    /// </summary>
    [TestFixture]
    public class SandFxTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        static ReliefGroundMap FlatMap(out int mid)
        {
            var g = new ReliefGroundMap(33, 0.5f);
            g.Begin(0, 0);
            // a 20 m square of ground at y 0 (Unity winding: front = cross(b - a, c - a) up)
            var a = new Vector3(-10, 0, -10); var b = new Vector3(-10, 0, 10); var c = new Vector3(10, 0, -10); var d = new Vector3(10, 0, 10);
            g.Collision(a, b, c); g.Collision(c, b, d);
            mid = 16 * 33 + 16;
            return g;
        }

        [Test]
        public void SandMask_LooseFlatGroundIsSand_RockIsNot()
        {
            var g = FlatMap(out int mid);
            var a = new Vector3(-10, 0.1f, -10); var b = new Vector3(-10, 0.1f, 10); var c = new Vector3(10, 0.1f, -10); var d = new Vector3(10, 0.1f, 10);
            g.Visual(a, b, c, 1, 1, 1); g.Visual(c, b, d, 1, 1, 1);
            Assert.AreEqual(1f, g.Sand[mid], 1e-4f, "loose flat sand");
            g = FlatMap(out mid);
            g.Visual(a, b, c); g.Visual(c, b, d);
            Assert.AreEqual(0f, g.Sand[mid], "a layer that is not sand (rock, grass, path)");
            var bytes = new byte[33 * 33];
            g = FlatMap(out mid);
            g.Visual(a, b, c, 0.5f, 0.5f, 0.5f); g.Visual(c, b, d, 0.5f, 0.5f, 0.5f);
            g.SandBytes(bytes);
            Assert.AreEqual(128, bytes[mid], 1);
        }

        [Test]
        public void DustGate_OnlyOnTheTerrainSandUnderTheFoot()
        {
            var g = FlatMap(out int mid);
            var a = new Vector3(-10, 0.1f, -10); var b = new Vector3(-10, 0.1f, 10); var c = new Vector3(10, 0.1f, -10); var d = new Vector3(10, 0.1f, 10);
            Assert.AreEqual(-1f, g.SandAt(0, 0, 0, 0.12f), "map not ready");
            g.Visual(a, b, c, 1, 1, 1); g.Visual(c, b, d, 1, 1, 1);
            g.Ready = true;
            Assert.AreEqual(1f, g.SandAt(0, 0.02f, 0, 0.12f), 1e-4f, "on the sand");
            Assert.AreEqual(0f, g.SandAt(0, 0.3f, 0, 0.12f), "on a carpet / floor / ledge above the terrain");
            Assert.AreEqual(0f, g.SandAt(0, -3f, 0, 0.12f), "in a cellar under it");
            Assert.AreEqual(-1f, g.SandAt(500, 0, 0, 0.12f), "off the map");
        }

        [Test]
        public void CoveredMap_RoofsCoverAndGrowByOne()
        {
            int n = 5;
            var top = new float[n * n]; var ground = new float[n * n];
            for (int k = 0; k < top.Length; k++) { top[k] = 0.05f; ground[k] = 0; }
            top[2 * n + 2] = 3f;   // a roof over the middle point
            top[0] = 1.2f;         // a person / crate / low wall: not a roof
            var m = new byte[n * n];
            CoveredMap.Build(top, ground, n, m);
            Assert.AreEqual(255, m[2 * n + 2]);
            Assert.AreEqual(255, m[1 * n + 1], "eaves: grown by one");
            Assert.AreEqual(0, m[0], "lower than a roof");
            Assert.AreEqual(0, m[4 * n + 4]);
            Assert.AreEqual(1f, CoveredMap.At(m, n, -2, -2, 1, 0, 0));
            Assert.AreEqual(0f, CoveredMap.At(m, n, -2, -2, 1, 50, 0), "off the grid: open");
            Assert.AreEqual(0f, CoveredMap.Covered(float.NaN, 0));
        }

        [Test]
        public void SandMask_SteepSlopesAndCliffsAreNot()
        {
            Assert.AreEqual(1f, ReliefGroundMap.SandFlatness(1f));
            Assert.AreEqual(1f, ReliefGroundMap.SandFlatness(0.95f), "~18 deg");
            Assert.That(ReliefGroundMap.SandFlatness(0.85f), Is.InRange(0.1f, 0.6f), "~32 deg: lee faces near the angle of repose take a few");
            Assert.AreEqual(0f, ReliefGroundMap.SandFlatness(0.7f), "~45 deg");
            Assert.AreEqual(0f, ReliefGroundMap.SandFlatness(0.1f), "cliff");
        }

        [Test]
        public void SandMask_VertexBlendPicksTheLayer()
        {
            // G3: lerp(A, B, VC.a): alpha 0 = layer A
            Assert.AreEqual(1f, ReliefMapRaster.Loose(1, 0, 0x00FFFFFFu), 1e-4f);
            Assert.AreEqual(0f, ReliefMapRaster.Loose(1, 0, 0xFF000000u), 1e-4f);
            Assert.AreEqual(0.5f, ReliefMapRaster.Loose(1, 0, 0x80000000u), 0.01f);
            Assert.AreEqual(1f, ReliefMapRaster.Loose(1, 1, 0x80000000u), 1e-4f);
        }

        [Test]
        public void Dust_WalkBarelySprintFull()
        {
            Assert.AreEqual(0f, SandV2.DustBySpeed(0f, 0.3f), "standing / dancing in place: no dust");
            Assert.AreEqual(0f, SandV2.DustBySpeed(0.5f, 0.3f), "shuffling on the spot: no dust");
            Assert.AreEqual(0.3f, SandV2.DustBySpeed(SandV2.DustWalkSpeed, 0.3f), 1e-4f, "walk");
            float run = SandV2.DustBySpeed(4f, 0.3f);
            Assert.That(run, Is.InRange(0.45f, 0.75f), "run");
            Assert.AreEqual(1f, SandV2.DustBySpeed(SandV2.DustSprintSpeed, 0.3f), 1e-4f, "sprint");
            Assert.AreEqual(1f, SandV2.DustBySpeed(20f, 0.3f), 1e-4f);
        }

        [Test]
        public void MegaRipples_CutNearTheCameraMoreAtLowSun()
        {
            Assert.AreEqual(1f, SandV2.MegaNear(30f, 0.9f, 0.45f, 0.5f), 1e-4f, "mid range keeps the look");
            Assert.AreEqual(1f, SandV2.MegaNear(30f, 0.1f, 0.45f, 0.5f), 1e-4f, "mid range at low sun too");
            float noon = SandV2.MegaNear(3f, 0.9f, 0.45f, 0.5f), low = SandV2.MegaNear(3f, 0.1f, 0.45f, 0.5f);
            Assert.AreEqual(0.45f, noon, 1e-4f);
            Assert.AreEqual(0.225f, low, 1e-4f);
            Assert.That(SandV2.MegaNear(8f, 0.1f, 0.45f, 0.5f), Is.GreaterThan(low));
        }

        [Test]
        public void Knobs_InJsonAndClamped()
        {
            string json = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));
            foreach (var k in new[] { "sandWispHeight", "sandWispCrest", "sandWispNear", "sandWispMask", "sandWispOpacity", "sandDustHeight", "sandDustLife",
                                      "sandDustOpacity", "sandDustWalk", "archSandWispOpacity", "sandMegaNear", "sandMegaLowSun", "sandRippleWarp", "sandRippleBreak" })
                StringAssert.Contains("\"" + k + "\"", json, k);
            var d = WorldLookSettings.Default;
            Assert.That(d.sandDustHeight, Is.InRange(0.25f, 0.4f), "dust stays low");
            Assert.That(d.sandDustLife, Is.InRange(0.6f, 1f), "dust settles quickly");
            Assert.That(d.sandWispHeight, Is.LessThanOrEqualTo(0.5f), "streamers hug the ground");
            Assert.That(d.sandWispOpacity, Is.LessThanOrEqualTo(0.5f));
            string sh = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3SandWisps.shader"));
            StringAssert.Contains("G3StreakSand", sh);
            StringAssert.Contains("G3StreakMedium", sh);
        }
    }
}
