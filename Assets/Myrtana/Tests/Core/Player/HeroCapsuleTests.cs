using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-148 (owner, Demo_2026-10-19: "по некоторым ступеням не удается идти" in Gotha's great tower, "кажется лук мешает
    /// пройти" under the smithy's shelter beam): the hero's capsule is G3's (<see cref="HeroCapsule"/>) and the step probe
    /// finds room on steep open stairs (<see cref="SlopeRules.NearLanding"/>, <see cref="SlopeRules.TopsBefore"/>).
    /// Numbers from <c>G3Cli collision column</c>: tower stairs 90.15 → 94.97 at (350.6…354, 511.2…513), edges at 45°:
    /// treads 0.57 m deep across, 0.50 m rise, 0.17 m thick (the next tread starts at the front edge, 0.33 m over the
    /// tread); smithy beam underside 70.06 over the floor 67.73 at (338.4, 473.5).
    /// </summary>
    public class HeroCapsuleTests
    {
        const float Run = 0.57f, Rise = 0.5f, Thick = 0.17f;

        [Test]
        public void TheWorldFeelsG3sCapsule()
        {
            Assert.That(HeroCapsule.EffectiveRadius, Is.EqualTo(HeroCapsule.G3Radius).Within(0.01f));
            Assert.That(HeroCapsule.EffectiveTop, Is.EqualTo(HeroCapsule.G3Top).Within(0.03f));
        }

        [Test]
        public void HeWalksUnderTheSmithyShelterBeam()
        {
            Assert.That(HeroCapsule.FitsUnder(70.06f - 67.73f), Is.True);
            Assert.That(HeroCapsule.FitsUnder(1.9f), Is.False);
        }

        /// <summary>The faces the probe rays (G3Player.ObstacleRayHeights) meet on the tower stairs, contact at the next tread's front.</summary>
        static SlopeRules.FaceHit[] StairHits(float contact, out int n)
        {
            var hits = new SlopeRules.FaceHit[8];
            n = 0;
            foreach (float h in new[] { 0.15f, 0.4f, 0.6f, 0.9f, 1.15f, 1.4f, 1.65f, 1.9f })
                for (int k = 1; k <= 4; k++) // tread k ahead: front face from k*rise - thick to k*rise, k-1 runs past the contact
                    if (h >= k * Rise - Thick && h <= k * Rise) { hits[n++] = new SlopeRules.FaceHit(h, contact + (k - 1) * Run, false); break; }
            return hits;
        }

        [Test]
        public void TheNearLandingOfTheTowerStairIsAStep()
        {
            float r = HeroCapsule.Radius, contact = HeroCapsule.EffectiveRadius;
            var d = SlopeRules.LandingDistances(contact, r);
            var hits = StairHits(contact, out int n);
            // the usual landings: the room test's capsule (radius - 0.05) reaches into the tread after next
            Assert.That(d[0] - contact + (r - 0.05f), Is.GreaterThan(Run));
            // the near landing: the centre 0.1 m past the front edge, the capsule ends before the next tread
            float near = d[SlopeRules.NearLandingIndex];
            Assert.That(near - contact + (r - 0.05f), Is.LessThan(Run));
            var (wall, fold) = SlopeRules.TopsBefore(hits, n, near);
            Assert.That(wall, Is.EqualTo(0.4f), "only the front of the tread he steps onto");
            Assert.That(fold, Is.EqualTo(-1f));
            var probe = new SlopeRules.StepProbe { FloorFound = true, FloorRise = Rise, FloorAngle = 0f, Fits = true, WallTop = wall, FoldTop = fold };
            Assert.That(SlopeRules.DecideStep(probe, SlopeRules.StepHeight, SlopeRules.WalkableAngleMax), Is.EqualTo(SlopeRules.StepKind.Step));
            // before fix-148 every face within the farthest landing counted: the tread after next (0.9 m) blocked
            var (allWall, _) = SlopeRules.TopsBefore(hits, n, d[1]);
            Assert.That(allWall, Is.GreaterThan(SlopeRules.StepHeight));
        }

        [Test]
        public void AWallRightOnTheStepStillBlocks()
        {
            // a fence 1.9 m high standing on a kerb: its face is met where the kerb is, so it lies on the way to every landing
            var hits = new[] { new SlopeRules.FaceHit(0.15f, 0.34f, false), new SlopeRules.FaceHit(1.9f, 0.36f, false) };
            var (wall, _) = SlopeRules.TopsBefore(hits, 2, 0.34f + SlopeRules.NearLanding);
            Assert.That(wall, Is.EqualTo(1.9f));
        }

        [Test]
        public void StepOnward_GoesAllTheWayOntoTheNearLanding()
        {
            Assert.That(SlopeRules.StepOnward(0.07f, 0.43f, near: true), Is.EqualTo(0.43f).Within(1e-5f));
            Assert.That(SlopeRules.StepOnward(0.07f, 0.97f, near: false), Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(SlopeRules.StepOnward(0.5f, 0.3f, near: false), Is.EqualTo(0.3f).Within(1e-5f));
        }
    }
}
