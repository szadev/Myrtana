using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class SlopeRulesTests
    {
        const float Limit = SlopeRules.WalkableAngleMax, Step = SlopeRules.StepHeight;

        static (float x, float y, float z) Normal(float deg) =>
            (System.MathF.Sin(deg * System.MathF.PI / 180f), System.MathF.Cos(deg * System.MathF.PI / 180f), 0f);

        [Test]
        public void G3HeroValues()
        {
            Assert.That(SlopeRules.WalkableAngleMax, Is.EqualTo(49f));
            Assert.That(SlopeRules.StepHeight, Is.EqualTo(0.65f));
            Assert.That(SlopeRules.SlideSpeedMax, Is.EqualTo(8f));
        }

        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(80f)]
        [TestCase(90f)]
        public void AngleFromUp_RoundTrips(float deg)
        {
            var n = Normal(deg);
            Assert.That(SlopeRules.AngleFromUp(n.x * 3, n.y * 3, n.z * 3), Is.EqualTo(deg).Within(0.01f));
        }

        [Test]
        public void Classification()
        {
            Assert.That(SlopeRules.IsWalkable(0f, Limit), Is.True);
            Assert.That(SlopeRules.IsWalkable(35f, Limit), Is.True, "gentle hill");
            Assert.That(SlopeRules.IsWalkable(49f, Limit), Is.True);
            Assert.That(SlopeRules.IsWalkable(50f, Limit), Is.False);
            Assert.That(SlopeRules.IsWalkable(80f, Limit), Is.False, "near-vertical rock");
        }

        static SlopeRules.StepProbe Probe(float rise, float angle = 0f, float wall = -1f, float fold = -1f, bool fits = true,
            bool found = true, float slope = 0f) =>
            new SlopeRules.StepProbe
            {
                FloorFound = found, FloorRise = rise, FloorAngle = angle, WallTop = wall, FoldTop = fold, Fits = fits, SlopeRise = slope,
            };

        static SlopeRules.StepKind Decide(SlopeRules.StepProbe p) => SlopeRules.DecideStep(p, Step, Limit);

        [Test]
        public void Stair_IsAStep() =>
            Assert.That(Decide(Probe(0.3f, wall: 0.15f)), Is.EqualTo(SlopeRules.StepKind.Step));

        [Test]
        public void Wall_Blocks() =>
            Assert.That(Decide(Probe(0.3f, wall: 1.9f)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        // fix-214: a doorway threshold whose jamb / arch the centre-line rays meet above step height is a step when the
        // body sweeps over the threshold untouched; a wall straight ahead stops the sweep and still blocks.
        [Test]
        public void DoorwayThreshold_JambBeside_IsAStep()
        {
            var p = Probe(0.12f, wall: 1.9f);
            p.BodyClear = true;
            Assert.That(Decide(p), Is.EqualTo(SlopeRules.StepKind.Step));
        }

        [Test]
        public void DoorwayThreshold_BodyHits_Blocks() =>
            Assert.That(Decide(Probe(0.12f, wall: 1.9f)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        [Test]
        public void BodyClear_StillNeedsFloorRoomAndStepHeight()
        {
            var tooHigh = Probe(0.7f, wall: 1.9f); tooHigh.BodyClear = true;
            var noRoom = Probe(0.12f, wall: 1.9f, fits: false); noRoom.BodyClear = true;
            var steep = Probe(0.12f, angle: 70f, wall: 1.9f); steep.BodyClear = true;
            Assert.That(Decide(tooHigh), Is.EqualTo(SlopeRules.StepKind.Blocked));
            Assert.That(Decide(noRoom), Is.EqualTo(SlopeRules.StepKind.Blocked));
            Assert.That(Decide(steep), Is.EqualTo(SlopeRules.StepKind.Blocked));
        }

        [Test]
        public void SteepTop_Blocks() =>
            Assert.That(Decide(Probe(0.4f, angle: 70f, wall: 0.15f)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        [Test]
        public void TooHigh_Blocks() =>
            Assert.That(Decide(Probe(0.7f, wall: 0.6f)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        [Test]
        public void NoFloor_Blocks() =>
            Assert.That(Decide(Probe(0f, found: false)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        [Test]
        public void NoRoom_Blocks() =>
            Assert.That(Decide(Probe(0.2f, wall: 0.15f, fits: false)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        [Test]
        public void FarDrop_Blocks() =>
            Assert.That(Decide(Probe(-0.8f, fold: 0.15f)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        // fix-49 Hammerclan path: 71-88 deg landscape triangles stick 0.3-0.9 m out of an 18-33 deg slope.
        [TestCase(0.25f, 0.9f)]
        [TestCase(0.3f, 1.15f)]
        [TestCase(0.45f, 1.4f)]
        public void LandscapeFold_OnAGentleSlope_IsPassed(float rise, float foldTop) =>
            Assert.That(Decide(Probe(rise, angle: 30f, fold: foldTop)), Is.EqualTo(SlopeRules.StepKind.PassFold));

        [Test]
        public void SteepSlopeRise_DoesNotCountAsStep()
        {
            // 0.8 m up a 40 deg slope rises 0.67 m by itself; a 0.3 m fold on it is still passed
            float slope = SlopeRules.PlaneRise(-0.643f, 0.766f, 0f, 1f, 0f, 0.8f, Limit);
            Assert.That(slope, Is.EqualTo(0.67f).Within(0.01f));
            Assert.That(Decide(Probe(0.67f, angle: 40f, fold: 1.15f, slope: slope)), Is.EqualTo(SlopeRules.StepKind.PassFold));
            // without the slope the same floor is out of step height
            Assert.That(Decide(Probe(0.67f, angle: 40f, fold: 1.15f)), Is.EqualTo(SlopeRules.StepKind.Blocked));
        }

        [Test]
        public void PlaneRise_DownhillIsNegative_AndCapped()
        {
            Assert.That(SlopeRules.PlaneRise(0.5f, 0.866f, 0f, 1f, 0f, 1f, Limit), Is.EqualTo(-0.577f).Within(0.01f));
            Assert.That(SlopeRules.PlaneRise(-0.94f, 0.34f, 0f, 1f, 0f, 1f, Limit), Is.EqualTo(1.15f).Within(0.01f), "capped at 49 deg");
            Assert.That(SlopeRules.PlaneRise(0f, 1f, 0f, 1f, 0f, 1f, Limit), Is.EqualTo(0f));
        }

        [Test]
        public void FoldWhoseLandingIsUnderTheLandscape_Blocks()
        {
            // fix-99: the hillside over a cave; the floor ray found the cave behind it
            var p = Probe(0.3f, angle: 25f, fold: 0.9f);
            p.UnderLandscape = true;
            Assert.That(Decide(p), Is.EqualTo(SlopeRules.StepKind.Blocked));
        }

        [Test]
        public void LowLandscapeLip_IsPassedAsG3Does() =>
            Assert.That(Decide(Probe(0.3f, angle: 25f, fold: 0.4f)), Is.EqualTo(SlopeRules.StepKind.PassFold));

        [Test]
        public void LandscapeCliff_Blocks()
        {
            // a cliff: the floor on top is far above (G3: no floor within step height)
            Assert.That(Decide(Probe(2.5f, fold: 1.9f)), Is.EqualTo(SlopeRules.StepKind.Blocked));
            // a ledge with the cliff going on right behind it: steep landscape up to head height
            Assert.That(Decide(Probe(0.5f, fold: 1.9f)), Is.EqualTo(SlopeRules.StepKind.Blocked));
            // a fold onto ground steeper than walkable
            Assert.That(Decide(Probe(0.3f, angle: 55f, fold: 0.9f)), Is.EqualTo(SlopeRules.StepKind.Blocked));
        }

        [Test]
        public void FoldNextToAWall_Blocks() =>
            // a house wall, fence or rock (not landscape) above step height is never passed (owner: no walking through walls)
            Assert.That(Decide(Probe(0.2f, fold: 0.9f, wall: 0.9f)), Is.EqualTo(SlopeRules.StepKind.Blocked));

        [Test]
        public void LandingDistances_StartPastTheCapsule()
        {
            var d = SlopeRules.LandingDistances(0.35f, 0.35f);
            Assert.That(d[0], Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(d[1], Is.GreaterThan(d[0]));
            Assert.That(SlopeRules.LandingDistances(-0.2f, 0.35f)[0], Is.EqualTo(0.45f).Within(1e-5f));
        }

        [Test]
        public void Stalled_OnlyWhenWalkingAndHeld()
        {
            Assert.That(SlopeRules.Stalled(4f, 0.01f, 1f / 60f), Is.True);
            Assert.That(SlopeRules.Stalled(4f, 0.06f, 1f / 60f), Is.False);
            Assert.That(SlopeRules.Stalled(0.2f, 0f, 1f / 60f), Is.False, "standing still");
        }

        [Test]
        public void StepUpLanded_Rules()
        {
            Assert.That(SlopeRules.StepUpLanded(0.7f, 0.4f, 0.25f, true, 0.4f, 0.7f), Is.True);
            Assert.That(SlopeRules.StepUpLanded(0.2f, 0.4f, 0.25f, true, 0.2f, 0.7f), Is.False, "ceiling");
            Assert.That(SlopeRules.StepUpLanded(0.7f, 0.4f, 0.02f, true, 0.4f, 0.7f), Is.False, "wall above the step");
            Assert.That(SlopeRules.StepUpLanded(0.7f, 0.4f, 0.25f, false, 0.4f, 0.7f), Is.False, "nothing below");
        }

        [Test]
        public void BlockInto_RemovesOnlyTheIntoPart()
        {
            // Wall facing -z (normal points back at the hero who runs +z), hero runs diagonally.
            var (x, z) = SlopeRules.BlockInto(3f, 3f, 0f, -2f);
            Assert.That(x, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(z, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void BlockInto_StraightIn_Stops()
        {
            var (x, z) = SlopeRules.BlockInto(0f, 4f, 0f, -1f);
            Assert.That(x * x + z * z, Is.LessThan(1e-8f));
        }

        [Test]
        public void BlockInto_KeepsMovingAway()
        {
            var (x, z) = SlopeRules.BlockInto(0f, -4f, 0f, -1f);
            Assert.That(z, Is.EqualTo(-4f));
            Assert.That(x, Is.EqualTo(0f));
        }

        [Test]
        public void SlideSpeed_AcceleratesAndCaps()
        {
            float v = 1f;
            float v1 = SlopeRules.SlideSpeed(v, 60f, 20f, 0.7f, 8f, 0.1f);
            Assert.That(v1, Is.EqualTo(1f + 20f * 0.7f * System.MathF.Sin(System.MathF.PI / 3f) * 0.1f).Within(1e-4f));
            for (int i = 0; i < 200; i++) v = SlopeRules.SlideSpeed(v, 70f, 20f, 0.7f, 8f, 0.05f);
            Assert.That(v, Is.EqualTo(8f));
        }

        [Test]
        public void SlideState_StartsOnSteep_EndsOnFlat()
        {
            var s = new SlopeRules.SlideState();
            const float dt = 0.05f;
            for (int i = 0; i < 2; i++) s.Update(true, 70f, 0f, Limit, dt);
            Assert.That(s.Sliding, Is.False, "0.15 s grace");
            for (int i = 0; i < 2; i++) s.Update(true, 70f, 0f, Limit, dt);
            Assert.That(s.Sliding, Is.True);
            s.Update(true, 47f, 0f, Limit, dt);
            for (int i = 0; i < 10; i++) s.Update(true, 47f, 0f, Limit, dt);
            Assert.That(s.Sliding, Is.True, "inside the 3° hysteresis band");
            for (int i = 0; i < 5; i++) s.Update(true, 10f, 0f, Limit, dt);
            Assert.That(s.Sliding, Is.False);
        }

        [Test]
        public void SlideState_WalkableSlope_NeverSlides()
        {
            var s = new SlopeRules.SlideState();
            for (int i = 0; i < 100; i++) s.Update(true, 45f, 0f, Limit, 0.05f);
            Assert.That(s.Sliding, Is.False);
        }

        [Test]
        public void SlideState_Jump_OnlyAfter06s()
        {
            var s = new SlopeRules.SlideState();
            for (int i = 0; i < 4; i++) s.Update(true, 70f, 0f, Limit, 0.05f);
            Assert.That(s.Sliding && !s.CanJump, Is.True);
            for (int i = 0; i < 13; i++) s.Update(true, 70f, 0f, Limit, 0.05f);
            Assert.That(s.CanJump, Is.True);
        }
    }
}
