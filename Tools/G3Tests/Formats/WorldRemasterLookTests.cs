using System;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>Remaster look (Docs/remaster-world.md section 11): sky occlusion, contact shadows, ratio relight, path to white.</summary>
    [TestFixture]
    public class WorldRemasterLookTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        [Test]
        public void Ratio_OpenSkyAndLit_IsIdentity()
        {
            Assert.AreEqual(1f, WorldV2Light.Ratio(0.3f, 1.2f, 0f, 1f, 1f), 1e-6);
            Assert.AreEqual(1f, WorldV2Light.Ratio(0, 0, 0, 0, 0), 1e-6, "black pixel: untouched");
        }

        [Test]
        public void Ratio_ShadeUnderCover_LosesOnlyTheSkyLight()
        {
            // in the shade (D = 0) the factor is the visibility itself
            Assert.AreEqual(0.4f, WorldV2Light.Ratio(0.3f, 0f, 0f, 0.4f, 1f), 1e-5);
            // sunlit ground under a gap in the crowns: the sun keeps most of the pixel
            float r = WorldV2Light.Ratio(0.3f, 1.2f, 0f, 0.4f, 1f);
            Assert.AreEqual((0.3f * 0.4f + 1.2f) / 1.5f, r, 1e-5);
            Assert.Greater(r, 0.85f);
        }

        [Test]
        public void Ratio_TorchLightIsKept_InACave()
        {
            // cave floor: no sky (v = 0), a torch L dominates -> barely darker; without the torch the floor goes to the floor value
            float withTorch = WorldV2Light.Ratio(0.1f, 0f, 1.0f, 0f, 1f);
            Assert.Greater(withTorch, 0.9f);
            Assert.AreEqual(0.05f, WorldV2Light.Ratio(0.1f, 0f, 0f, 0f, 1f, 0.05f), 1e-6, "clamped at the floor");
        }

        [Test]
        public void Ratio_ContactShadow_RemovesTheSunOnly()
        {
            float r = WorldV2Light.Ratio(0.25f, 1f, 0f, 1f, 0f);
            Assert.AreEqual(0.2f, r, 1e-5, "a contact-shadowed pixel keeps its ambient only");
            Assert.LessOrEqual(WorldV2Light.Ratio(0.25f, 1f, 0.5f, 2f, 3f), 1f, "never brightens");
        }

        [Test]
        public void Occlusion_RoofCaveCrownValley()
        {
            Func<float, float, float> flat = (x, z) => 0f, none = (x, z) => -1e5f;
            Assert.AreEqual(0f, WorldV2Light.Occlusion(flat, none, 0, 0, 0, 0.6f), 1e-6, "open meadow");
            // a house: roof 5 m above the floor over a 6 m square
            Func<float, float, float> house = (x, z) => Math.Abs(x) < 3 && Math.Abs(z) < 3 ? 5f : 0f;
            float inHouse = WorldV2Light.Occlusion(house, none, 0, 0, 0, 0.6f);
            Assert.Greater(inHouse, 0.5f, "under a roof the sky is mostly hidden");
            Assert.Less(WorldV2Light.Occlusion(house, none, 12, 0, 0, 0.6f), 0.2f, "outside, 9 m from the wall: nearly open");
            // cave: rock 20 m above everywhere
            Assert.AreEqual(1f, WorldV2Light.Occlusion((x, z) => 20f, none, 0, 0, 0, 0.6f), 1e-6);
            // forest: crowns everywhere, top 15 m -> the crown density
            Assert.AreEqual(0.6f, WorldV2Light.Occlusion(flat, (x, z) => 15f, 0, 0, 0, 0.6f), 1e-5);
            // on a roof top (the pixel is the roof itself) nothing above
            Assert.AreEqual(0f, WorldV2Light.Occlusion(house, none, 0, 5, 0, 0.6f), 1e-6);
            // narrow valley: walls 30 m high 6 m away -> rings occluded, straight up open
            float valley = WorldV2Light.Occlusion((x, z) => Math.Abs(x) > 6 ? 30f : 0f, none, 0, 0, 0, 0.6f);
            Assert.Greater(valley, 0.15f); Assert.Less(valley, 0.66f);
        }

        [Test]
        public void Occlusion_RockFaceIsNotUnderItsOwnTop()
        {
            // a boulder 3 m high, 4 m wide; a point on its vertical face (x = 2, y = 1) facing +x
            Func<float, float, float> rock = (x, z) => Math.Abs(x) < 2 && Math.Abs(z) < 2 ? 3f : 0f, none = (x, z) => -1e5f;
            float raw = WorldV2Light.Occlusion(rock, none, 1.9f, 1f, 0f, 0.6f);
            var q = WorldV2Light.LookupPoint(1.9f, 1f, 0f, 1f, 0f, 0f);
            float fixedOcc = WorldV2Light.Occlusion(rock, none, q.x, q.y, q.z, 0.6f);
            Assert.Greater(raw, 0.1f, "without the offset the face reads its own top");
            Assert.Less(fixedOcc, 0.05f);
        }

        [Test]
        public void TapWeights_SumToOne()
        {
            float s = 0;
            foreach (var t in WorldV2Light.Taps) s += t.weight;
            Assert.AreEqual(1f, s, 1e-5);
        }

        [Test]
        public void Crown_FromTreeBounds()
        {
            var c = WorldV2Light.Crown(-3, 10, -2, 3, 30, 2);
            Assert.AreEqual(0f, c.x, 1e-6); Assert.AreEqual(0f, c.z, 1e-6);
            Assert.AreEqual(30f, c.top, 1e-6);
            Assert.AreEqual(16f, c.bottom, 1e-5, "crown from 30 % of the height");
            Assert.AreEqual(3f * 0.85f, c.radius, 1e-5);
            Assert.AreEqual(10.2f, WorldV2Light.Crown(0, 10, 0, 1, 12, 1).bottom, 1e-5, "a bush is crown from the ground");
        }

        [Test]
        public void Window_RecentreAndSnap()
        {
            Assert.IsTrue(WorldV2Light.NeedsRecenter(0, 0, 0, 0, 320, false), "never built");
            Assert.IsFalse(WorldV2Light.NeedsRecenter(70, -70, 0, 0, 320, true));
            Assert.IsTrue(WorldV2Light.NeedsRecenter(81, 0, 0, 0, 320, true));
            Assert.AreEqual(12.5f, WorldV2Light.Snap(12.9f, 1.25f), 1e-5);
        }

        [Test]
        public void Contact_LengthGrowsWithDistance_FadesOut()
        {
            var s = WorldRemasterLook.Current;
            Assert.AreEqual(s.v2ContactLength, WorldV2Light.ContactLength(0), 1e-6);
            Assert.Greater(WorldV2Light.ContactLength(30), WorldV2Light.ContactLength(5));
            Assert.LessOrEqual(WorldV2Light.ContactLength(44), s.v2ContactLength * 4);
            Assert.AreEqual(0f, WorldV2Light.ContactLength(s.v2ContactFade + 1));
            WorldRemasterLook.Current = new WorldLookSettings { v2Contact = false };
            Assert.AreEqual(0f, WorldV2Light.ContactLength(1));
        }

        [Test]
        public void PathToWhite_OnlyAboveTheShoulder()
        {
            Assert.AreEqual(0f, WorldV2Light.DesatAmount(0.5f, 0.5f, 0.15f));
            float a = WorldV2Light.DesatAmount(3f, 0.98f, 0.15f);
            Assert.Greater(a, 0.15f); Assert.Less(a, 0.4f);
            Assert.AreEqual(0f, WorldV2Light.DesatAmount(3f, 0.98f, 0f), "0 = hue kept (before)");
            Assert.AreEqual(WorldRemasterLook.Current.v2HighlightDesat, WorldV2.ShoulderParams[2], 1e-6, "reaches the grade pass");
        }

        [Test]
        public void Globals_OffWithV2Off_AndPassFollowsToggles()
        {
            Assert.IsTrue(WorldV2Light.PassWanted);
            var g = WorldV2Light.RatioGlobal;
            Assert.Greater(g[0], 0); Assert.Greater(g[2], 0);
            WorldRemasterLook.Current = new WorldLookSettings { v2 = false };
            Assert.IsFalse(WorldV2Light.PassWanted);
            CollectionAssert.AreEqual(new float[4], WorldV2Light.RatioGlobal);
            WorldRemasterLook.Current = new WorldLookSettings { v2SkyOcclusion = false, v2Contact = false, v2Gi = false, v2CloudShadows = false };
            Assert.IsFalse(WorldV2Light.PassWanted);
        }

        [Test]
        public void Budget_LightPassUnderHalfAMillisecond_AtHalfRes()
        {
            float ms = WorldV2Light.CostMs();
            Assert.Greater(ms, 0.1f); Assert.Less(ms, 0.5f);
            Assert.IsTrue(WorldV2.BudgetMs().ContainsKey("light pass (sky occlusion, contact shadows)"));
            WorldRemasterLook.Current = new WorldLookSettings { v2LightDownsample = 1 };
            Assert.Greater(WorldV2Light.CostMs(), ms * 2);
        }

        [Test]
        public void Clouds2_DetailAndGlow_OffWithTheClouds()
        {
            var g = WorldV2.Clouds2Global;
            Assert.Greater(g[0], 0); Assert.Greater(g[1], 0);
            WorldRemasterLook.Current = new WorldLookSettings { v2Clouds = false };
            CollectionAssert.AreEqual(new float[4], WorldV2.Clouds2Global);
            WorldRemasterLook.Current = new WorldLookSettings { v2CloudDetail = 0, v2CloudGlow = 0 };
            CollectionAssert.AreEqual(new float[4], WorldV2.Clouds2Global, "0/0 = the v2 blobs as before");
        }

        [Test]
        public void Clamped_RejectsBadValues()
        {
            var s = new WorldLookSettings { v2SkyOccStrength = 5, v2ContactSteps = 1000, v2SkyOccResolution = 5, v2LightDownsample = 7, v2HighlightDesat = float.NaN }.Clamped();
            Assert.AreEqual(1f, s.v2SkyOccStrength); Assert.AreEqual(48, s.v2ContactSteps); Assert.AreEqual(64, s.v2SkyOccResolution);
            Assert.AreEqual(2, s.v2LightDownsample); Assert.AreEqual(0f, s.v2HighlightDesat);
        }

        [Test]
        public void Bounce_TintsTheOccludedShade_NeverAboveG3()
        {
            // under a closed canopy: v = 0.2, ambient 1, no sun; green surroundings
            float plain = WorldV2Light.Ratio(1, 0, 0, 0.2f, 1, 0.12f);
            float green = WorldV2Light.Ratio(1, 0, 0, 0.2f, 1, 0.12f, WorldV2Light.Bounce(0.5f, 0.2f, 0.6f));
            float red = WorldV2Light.Ratio(1, 0, 0, 0.2f, 1, 0.12f, WorldV2Light.Bounce(0.1f, 0.2f, 0.6f));
            Assert.Greater(green, plain); Assert.Greater(green, red, "the channel of the surroundings' colour comes back more");
            Assert.AreEqual(0f, WorldV2Light.Bounce(0.5f, 1f, 0.6f), "open sky: no bounce term");
            Assert.AreEqual(1f, WorldV2Light.Ratio(1, 0, 0, 0.2f, 1, 0.12f, 50f), "clamped: never brighter than G3's frame");
        }

        [Test]
        public void Taa_DefaultsAndClamp()
        {
            Assert.AreEqual(2, WorldV2Light.TaaMode);
            WorldRemasterLook.Current = new WorldLookSettings { v2 = false };
            Assert.AreEqual(0, WorldV2Light.TaaMode);
            var s = new WorldLookSettings { v2Taa = 9, v2TaaBlend = 2, v2SkyReflectionSize = 3, v2Bounce = -1 }.Clamped();
            Assert.AreEqual(2, s.v2Taa); Assert.AreEqual(0.99f, s.v2TaaBlend, 1e-6); Assert.AreEqual(16, s.v2SkyReflectionSize); Assert.AreEqual(0f, s.v2Bounce);
        }
    }
}
