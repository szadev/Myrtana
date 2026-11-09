using System;
using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-108: the floor an NPC body is put on (NpcGroundRules) — long animals on 35-45° slopes find the ground under their
    /// middle from a pose below or above it, a cave floor wins over the roof above it (fix-99 kept), bodies slide off
    /// faces steeper than 49° and do not walk up them.
    /// </summary>
    public class NpcGroundRulesTests
    {
        const float Deer = 0.8f, Wolf = 0.6f, Human = 0.35f;

        static List<NpcGroundRules.Surface> Column(params (float y, float slope)[] s)
        {
            var l = new List<NpcGroundRules.Surface>();
            foreach (var (y, slope) in s) l.Add(new NpcGroundRules.Surface(y, slope));
            return l;
        }

        static float Pick(float refY, List<NpcGroundRules.Surface> c, float half, float navY = float.NaN, float maxDown = 150f, float upLimit = -1f)
        {
            Assert.That(NpcGroundRules.Pick(refY, c, half, maxDown, navY, out int i, upLimit), Is.True, "a floor is picked");
            return c[i].Y;
        }

        [TestCase(35f, Deer)]
        [TestCase(40f, Deer)]
        [TestCase(45f, Deer)]
        [TestCase(40f, Wolf)]
        [TestCase(45f, Wolf)]
        public void LongBodyOnSteepSlopeFindsTheGroundUnderItsMiddle(float slope, float half)
        {
            // the pose is under the slope's surface by the rise over the body's front half (a wander point at the spawn's
            // height, a simulated pose): a 65 cm ray (fix-99 step probe) started inside the hill and found nothing
            float under = half * MathF.Tan(slope * MathF.PI / 180f);
            Assert.That(under, Is.GreaterThan(0.5f));
            var c = Column((100f, slope));
            Assert.That(Pick(100f - under, c, half), Is.EqualTo(100f));
            // and from above: a pose 0.3 m in the air (a NavMesh point) comes down onto it
            Assert.That(Pick(100.3f, c, half), Is.EqualTo(100f));
            Assert.That(NpcGroundRules.UpReach(half, slope), Is.GreaterThanOrEqualTo(under + SlopeRules.StepHeight * 0.5f));
        }

        [Test]
        public void SlopeReachGrowsWithBodyLengthAndSlopeButIsCapped()
        {
            Assert.That(NpcGroundRules.UpReach(Human, 0f), Is.EqualTo(SlopeRules.StepHeight).Within(1e-4f));
            Assert.That(NpcGroundRules.UpReach(Deer, 40f), Is.GreaterThan(NpcGroundRules.UpReach(Human, 40f)));
            Assert.That(NpcGroundRules.UpReach(Deer, 45f), Is.GreaterThan(NpcGroundRules.UpReach(Deer, 35f)));
            Assert.That(NpcGroundRules.UpReach(5f, 80f), Is.EqualTo(NpcGroundRules.MaxUpReach));
        }

        [Test]
        public void CaveFloorWinsOverTheRoofAboveIt()
        {
            // Reddock cave (fix-99): floor 33.9, the roof's underside (its up-facing two-sided copy) 36.2, the roof's top
            // inside the rock 39.9; Kap Dun: floor 15.29, roof top 27.64
            var reddock = Column((39.9f, 30f), (36.2f, 25f), (33.9f, 8f));
            Assert.That(Pick(33.95f, reddock, 1.0f), Is.EqualTo(33.9f), "the varan on the floor");
            Assert.That(Pick(33.95f, reddock, 1.0f, navY: 33.92f), Is.EqualTo(33.9f), "with the floor's NavMesh");
            Assert.That(Pick(34.6f, reddock, 1.0f), Is.EqualTo(33.9f), "a pose half a metre above the floor");
            Assert.That(Pick(33.2f, reddock, 1.0f), Is.EqualTo(33.9f), "a pose a little under the floor comes up onto it, not the roof");
            // a sloped roof top within the long body's reach is still not chosen over the floor right under the feet
            var low = Column((35.4f, 40f), (33.9f, 5f));
            Assert.That(Pick(33.9f, low, 1.0f), Is.EqualTo(33.9f));
            var kapDun = Column((27.64f, 20f), (15.29f, 5f));
            Assert.That(Pick(15.35f, kapDun, 0.5f), Is.EqualTo(15.29f));
        }

        [Test]
        public void NavMeshFloorDecidesWhenThePoseIsFarBelowTheSurface()
        {
            // Deer#7b66571d: pose 47.08 under a 21° slope at 48.64 (1.56 m: beyond the slope reach); the NavMesh lies on it
            var c = Column((48.64f, 21f));
            Assert.That(Pick(47.08f, c, Deer, navY: 48.84f), Is.EqualTo(48.64f));
            // without NavMesh: nothing below at all -> under the ground -> up onto the surface (within BuriedReach)
            Assert.That(Pick(47.08f, c, Deer), Is.EqualTo(48.64f));
            // a surface below the pose exists (a cave under the hill): the buried rescue does not apply, the floor below does
            var cave = Column((48.64f, 21f), (40f, 5f));
            Assert.That(Pick(47.08f, cave, Deer), Is.EqualTo(40f));
            Assert.That(Pick(47.08f, cave, Deer, navY: 48.84f), Is.EqualTo(48.64f), "the NavMesh says the slope");
        }

        [Test]
        public void ABodyInTheAirComesDownOntoTheFirstFloorBelow()
        {
            // fix-60: a Faring guard's travel pose over the street beside the round tower
            var c = Column((99.6f, 2f), (84.0f, 3f));
            Assert.That(Pick(110f, c, Human), Is.EqualTo(99.6f));
            Assert.That(Pick(98.0f, c, Human), Is.EqualTo(84.0f));
            Assert.That(NpcGroundRules.Pick(98.0f, c, Human, 5f, float.NaN, out _), Is.False, "deeper than maxDown");
        }

        [Test]
        public void WanderPointAtTheSpawnsHeightFindsItsFloorMetersUp()
        {
            // a wander point 4 m from the spawn up a 55° face: 5.7 m above the spawn's height it carries
            var c = Column((105.7f, 55f));
            Assert.That(NpcGroundRules.Pick(100f, c, Deer, 150f, float.NaN, out _), Is.False, "beyond the slope reach");
            Assert.That(Pick(100f, c, Deer, upLimit: 8f), Is.EqualTo(105.7f));
        }

        [Test]
        public void WallFacesAreNoFloor()
        {
            var c = Column((101f, 85f), (100f, 10f));
            Assert.That(Pick(100.2f, c, Human), Is.EqualTo(100f));
        }

        [Test]
        public void BodiesSlideOffFacesSteeperThanWalkable()
        {
            Assert.That(NpcGroundRules.Slides(45f), Is.False);
            Assert.That(NpcGroundRules.Slides(49f), Is.False);
            Assert.That(NpcGroundRules.Slides(60f), Is.True);
            Assert.That(NpcGroundRules.Slides(68f), Is.True, "Deer#bb7c728e's spawn face (494.96, -45.28)");
            float v = 0f, moved = 0f;
            for (int i = 0; i < 120; i++) moved += NpcGroundRules.SlideStep(ref v, 60f, 1f / 60f);
            Assert.That(v, Is.EqualTo(SlopeRules.SlideSpeedMax).Within(1e-3f), "capped at SlideSpeedMax");
            Assert.That(moved, Is.GreaterThan(1f));
            Assert.That(NpcGroundRules.SlideStep(ref v, 30f, 1f / 60f), Is.EqualTo(0f));
            Assert.That(v, Is.EqualTo(0f), "walkable floor stops the slide");
        }

        [Test]
        public void StepsDoNotClimbSlideFaces()
        {
            Assert.That(NpcGroundRules.ClimbsSteep(0.27f, 0.1f, 70f), Is.True, "up a 70° face");
            Assert.That(NpcGroundRules.ClimbsSteep(0.1f, 0.1f, 45f), Is.False, "a 45° slope is walkable");
            Assert.That(NpcGroundRules.ClimbsSteep(-0.27f, 0.1f, 70f), Is.False, "down it");
            Assert.That(NpcGroundRules.ClimbsSteep(0.05f, 0.3f, 80f), Is.False, "a gentle step onto a fold face");
        }
    }
}
