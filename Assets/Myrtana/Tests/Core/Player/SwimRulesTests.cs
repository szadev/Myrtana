using System.IO;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class SwimRulesTests
    {
        const float Swim = SwimRules.HeroSwimDepth;
        static readonly float Wade = SwimRules.WadeDepth(SwimRules.HeroSwimDepth, SlopeRules.StepHeight);

        static WaterMode Walk(float depth, WaterMode current = WaterMode.None) =>
            SwimRules.Classify(current, false, depth, depth, Swim, Wade); // feet on the floor: body = column

        [Test]
        public void G3HeroValues()
        {
            Assert.That(SwimRules.HeroSwimDepth, Is.EqualTo(1.55f));
            Assert.That(Wade, Is.EqualTo(1.05f).Within(1e-5f));
            Assert.That(SwimRules.WadeDepth(1.0f, 0.65f, useStepHeight: false), Is.EqualTo(0.85f), "85 cm floor without the step check");
            Assert.That(SwimRules.SwimWalkSpeed, Is.EqualTo(1.5f));
            Assert.That(SwimRules.SwimRunSpeed, Is.EqualTo(4f));
        }

        [TestCase(-0.5f, WaterMode.None)]
        [TestCase(0.005f, WaterMode.None)]
        [TestCase(0.02f, WaterMode.Shallow)]
        [TestCase(1.0f, WaterMode.Shallow)]
        [TestCase(1.05f, WaterMode.Wade)]
        [TestCase(1.5f, WaterMode.Wade)]
        [TestCase(1.55f, WaterMode.Swim)]
        [TestCase(3f, WaterMode.Swim)]
        public void WalkingIntoWater(float depth, WaterMode expected) => Assert.That(Walk(depth), Is.EqualTo(expected));

        [Test]
        public void SwimmerKeepsSwimmingUntilTenCentimetresShallower()
        {
            Assert.That(Walk(1.46f, WaterMode.Swim), Is.EqualTo(WaterMode.Swim));
            Assert.That(Walk(1.45f, WaterMode.Swim), Is.EqualTo(WaterMode.Swim));
            Assert.That(Walk(1.44f, WaterMode.Swim), Is.EqualTo(WaterMode.Wade));
            Assert.That(Walk(1.44f, WaterMode.Wade), Is.EqualTo(WaterMode.Wade));
        }

        [Test]
        public void FloatingSwimmerOverDeepWaterSwims()
        {
            // Feet held at the swim depth over a deep column.
            Assert.That(SwimRules.Classify(WaterMode.Swim, false, Swim, 20f, Swim, Wade), Is.EqualTo(WaterMode.Swim));
            // Column deep enough and the body past the wade depth: swim (stepping off a ledge into deep water).
            Assert.That(SwimRules.Classify(WaterMode.Wade, false, 1.1f, 5f, Swim, Wade), Is.EqualTo(WaterMode.Swim));
            Assert.That(SwimRules.Classify(WaterMode.Shallow, false, 0.9f, 5f, Swim, Wade), Is.EqualTo(WaterMode.Shallow));
        }

        [Test]
        public void FallingIntoWater()
        {
            // Into deep water: dive in at once.
            Assert.That(SwimRules.Classify(WaterMode.None, true, 0.1f, 10f, Swim, Wade), Is.EqualTo(WaterMode.Swim));
            // Into shallow water: keep falling, land on the floor.
            Assert.That(SwimRules.Classify(WaterMode.None, true, 0.1f, 1.2f, Swim, Wade), Is.EqualTo(WaterMode.None));
            // Still above the surface.
            Assert.That(SwimRules.Classify(WaterMode.None, true, -2f, 10f, Swim, Wade), Is.EqualTo(WaterMode.None));
            // No floor found (open sea): swim.
            Assert.That(SwimRules.Classify(WaterMode.None, true, 0.3f, float.PositiveInfinity, Swim, Wade), Is.EqualTo(WaterMode.Swim));
        }

        [Test]
        public void SwimmingMovesForwardAndBackOnly()
        {
            Assert.That(SwimRules.SwimWish(1, 1, walk: false), Is.EqualTo((0f, 4f)));
            Assert.That(SwimRules.SwimWish(-1, -1, walk: true), Is.EqualTo((0f, -1.5f)));
            Assert.That(SwimRules.SwimWish(1, 0, walk: false), Is.EqualTo((0f, 0f)));
        }

        [Test]
        public void FeetHangAtSwimDepthOrStandOnTheFloor()
        {
            Assert.That(SwimRules.SwimFeetY(-4.53f, Swim, -20f), Is.EqualTo(-6.08f).Within(1e-4f));
            Assert.That(SwimRules.SwimFeetY(-4.53f, Swim, -5.5f), Is.EqualTo(-5.5f));
        }

        [Test]
        public void NpcsWadeButDoNotSwim()
        {
            Assert.That(SwimRules.NpcCanWalk(0.5f), Is.True);
            Assert.That(SwimRules.NpcCanWalk(1.85f), Is.True);
            Assert.That(SwimRules.NpcCanWalk(1.95f), Is.False);
            Assert.That(SwimRules.NpcCanWalk(2.85f, SwimRules.NpcSwimDepth("Troll")), Is.True);
            Assert.That(SwimRules.NpcCanWalk(float.PositiveInfinity), Is.False);
            Assert.That(SwimRules.NpcWades(1.49f, SwimRules.NpcSwimDepth("Human")), Is.False);
            Assert.That(SwimRules.NpcWades(1.5f, SwimRules.NpcSwimDepth("Human")), Is.True, "wade from SwimDepth − 50 cm");
            Assert.That(SwimRules.NpcWades(1.9f, SwimRules.NpcSwimDepth("Ogre")), Is.False);
            Assert.That(SwimRules.NpcWades(float.NaN, 2f), Is.False);
            // editor-runner-16: Abe waded with Hero_Stand_None_None_P0_Move — the stance went into the phase field
            Assert.That(SwimRules.NpcMoveSet("Hero", true, true), Is.EqualTo("Hero_Wade_None_None_P0_Move"));
            Assert.That(SwimRules.NpcMoveSet("Hero", true, false), Is.EqualTo("Hero_Stand_None_None_P0_Move"), "no wade set: stand");
            Assert.That(SwimRules.NpcMoveSet("Hero", false, true, "None", "1H"), Is.EqualTo("Hero_Stand_None_1H_P0_Move"));
            Assert.That(SwimRules.TreatsWaterAsSolid("Bloodfly"), Is.True);
            Assert.That(SwimRules.TreatsWaterAsSolid("Wolf"), Is.False);
            Assert.That(SwimRules.AttackerKeepsWaiting(9.9f), Is.True);
            Assert.That(SwimRules.AttackerKeepsWaiting(10.1f), Is.False);
        }

        [Test]
        public void AnNpcThatReachesSwimDepthDrowns()
        {
            float human = SwimRules.NpcSwimDepth("Human"), troll = SwimRules.NpcSwimDepth("Troll");
            Assert.That(SwimRules.NpcWadeDepth(human), Is.EqualTo(1.5f).Within(1e-5f), "W = max(SwimDepth − 50, 85)");
            Assert.That(SwimRules.NpcWadeDepth(1.2f), Is.EqualTo(0.85f).Within(1e-5f));
            Assert.That(SwimRules.NpcDrowns(1.4f, 3f, human), Is.False, "above the wade depth");
            Assert.That(SwimRules.NpcDrowns(1.6f, 1.6f, human), Is.False, "wading, column shallower than SwimDepth");
            Assert.That(SwimRules.NpcDrowns(1.6f, 2.5f, human), Is.True, "wade depth over a deep column");
            Assert.That(SwimRules.NpcDrowns(2.0f, 2.0f, human), Is.True, "body at SwimDepth");
            Assert.That(SwimRules.NpcDrowns(2.4f, 2.9f, troll), Is.False, "trolls: 300 cm from the data, not 160");
            Assert.That(SwimRules.NpcDrowns(float.NaN, 5f, human), Is.False);
        }
    }

    public class WaterZoneTests
    {
        // A 10 x 10 m square of sea at y = -4.53 m, G3 units (cm), entity at the origin, two triangles.
        static WaterZone Square(float[] normal = null)
        {
            var world = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            var pos = new float[] { 0, -453, 0, 1000, -453, 0, 1000, -453, 1000, 0, -453, 1000 };
            var idx = new uint[] { 0, 1, 2, 0, 2, 3 };
            return WaterZone.FromG3("sea", normal ?? new float[] { 0, 1, 0 }, new float[] { 500, -453, 500 }, 500f * 500f * 2f,
                0, 1000, 0, 1000, world, new[] { (pos, idx) });
        }

        // An L-shaped footprint: the box covers (0..10, 0..10) m, the mesh only x < 5 or z < 5.
        static WaterZone LShape()
        {
            var world = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            var pos = new float[]
            {
                0, 0, 0, 1000, 0, 0, 1000, 0, 500, 0, 0, 500, // bottom strip z 0..5
                0, 0, 500, 500, 0, 500, 500, 0, 1000, 0, 0, 1000, // left strip x 0..5, z 5..10
            };
            var idx = new uint[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return WaterZone.FromG3("lake", new float[] { 0, 1, 0 }, new float[] { 500, 0, 500 }, 0, 0, 1000, 0, 1000, world, new[] { (pos, idx) });
        }

        [Test]
        public void SurfaceAndCover()
        {
            var z = Square();
            Assert.That(z.CY, Is.EqualTo(-4.53f).Within(1e-4f));
            Assert.That(z.SurfaceY(3, 7), Is.EqualTo(-4.53f).Within(1e-4f));
            Assert.That(z.Covers(5, 5), Is.True);
            Assert.That(z.Covers(-1, 5), Is.False);
            Assert.That(z.TriangleCount, Is.EqualTo(2));
        }

        [Test]
        public void FootprintFollowsTheMeshNotTheBox()
        {
            var z = LShape();
            Assert.That(z.Covers(2, 8), Is.True);
            Assert.That(z.Covers(8, 2), Is.True);
            Assert.That(z.Covers(8, 8), Is.False, "inside the box, outside the mesh");
            z.Rasterize();
            Assert.That(z.Triangles, Is.Null);
            Assert.That(z.Covers(2, 8), Is.True);
            Assert.That(z.Covers(8, 2), Is.True);
            Assert.That(z.Covers(8, 8), Is.False);
            Assert.That(z.Covers(7, 7), Is.False);
        }

        [Test]
        public void DegenerateSideNormalIsTreatedAsHorizontal()
        {
            var z = Square(new float[] { 1, 0, 0 });
            Assert.That(z.NY, Is.EqualTo(1f));
            Assert.That(z.SurfaceY(9, 1), Is.EqualTo(-4.53f).Within(1e-4f));
        }

        [Test]
        public void TiltedRiverPlane()
        {
            float n = 1f / System.MathF.Sqrt(1 + 0.01f);
            var z = WaterZone.FromG3("river", new[] { 0.1f * n, n, 0f }, new float[] { 0, 1000, 0 }, 0, -1000, 1000, -1000, 1000, null, null);
            // y = 10 - 0.1 * x
            Assert.That(z.SurfaceY(5, 0), Is.EqualTo(9.5f).Within(1e-3f));
            Assert.That(z.Covers(5, 5), Is.True, "no mesh: the whole box");
        }

        [Test]
        public void MapPicksTheSurfaceOverTheFeet()
        {
            var map = new WaterMap(new[] { Square() });
            Assert.That(map.Sample(5, -6.08f, 5, out float s, out var zone), Is.True);
            Assert.That(s, Is.EqualTo(-4.53f).Within(1e-4f));
            Assert.That(zone.Name, Is.EqualTo("sea"));
            Assert.That(map.Sample(5, -20f, 5, out _, out _), Is.False, "more than 10 m under the surface (a cave below)");
            Assert.That(map.Sample(5, -20f, 5, out _, out _, maxAbove: 100f), Is.True);
            Assert.That(map.Sample(15, 0, 5, out _, out _), Is.False);
            Assert.That(map.Sample(5, 50f, 5, out s, out _), Is.True, "water below the feet still reports its surface");
            Assert.That(50f - s, Is.GreaterThan(0));
        }

        [Test]
        public void FileRoundTrip()
        {
            var a = LShape();
            a.Rasterize();
            var b = Square();
            var ms = new MemoryStream();
            WaterMapFile.Write(ms, new[] { a, b });
            ms.Position = 0;
            var map = WaterMapFile.Read(ms);
            Assert.That(map.Zones.Count, Is.EqualTo(2));
            Assert.That(map.Zones[0].Covers(2, 8), Is.True);
            Assert.That(map.Zones[0].Covers(8, 8), Is.False);
            Assert.That(map.Zones[1].Covers(5, 5), Is.True);
            Assert.That(map.Zones[1].Name, Is.EqualTo("sea"));
        }
    }
}
