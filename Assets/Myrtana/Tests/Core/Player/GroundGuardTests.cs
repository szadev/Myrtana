using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-123 (owner, Demo_2026-10-18f: "carried into the cave ceiling", then a fall through the world): the hero is never
    /// put up through a ceiling, a ceiling top is left for the floor below, and the world catch puts him back where he stood
    /// (<see cref="GroundGuard"/>).
    /// </summary>
    public class GroundGuardTests
    {
        const float Limit = SlopeRules.WalkableAngleMax;

        [Test]
        public void SinkRecovery_LiftsOntoTheFloorHeSankThrough()
        {
            // feet were on the floor at 10 before the move, ended 0.4 m below it
            Assert.That(GroundGuard.ShouldLiftSunk(surfaceY: 10f, feetY: 9.6f, beforeY: 10f, surfaceAngle: 5f, Limit), Is.True);
            // a fall that passed through a floor in one big step (before far above)
            Assert.That(GroundGuard.ShouldLiftSunk(10f, 9f, 11.5f, 0f, Limit), Is.True);
            // two small sinks that added up
            Assert.That(GroundGuard.ShouldLiftSunk(10f, 9.7f, 9.85f, 0f, Limit), Is.True);
        }

        [Test]
        public void SinkRecovery_NeverLiftsOntoACeilingTop()
        {
            // owner case: a low cave ceiling 1.3 m over the feet, which were on the cave floor before the move too
            Assert.That(GroundGuard.ShouldLiftSunk(surfaceY: 93.2f, feetY: 91.9f, beforeY: 91.9f, surfaceAngle: 10f, Limit), Is.False);
            // pushed back during a fight (the feet slid down 5 cm): still below the ceiling before the move
            Assert.That(GroundGuard.ShouldLiftSunk(93.2f, 91.85f, 91.9f, 10f, Limit), Is.False);
            // shallow or steep: never
            Assert.That(GroundGuard.ShouldLiftSunk(10.1f, 10f, 10.2f, 0f, Limit), Is.False, "less than 0.2 m");
            Assert.That(GroundGuard.ShouldLiftSunk(10.5f, 10f, 10.6f, 60f, Limit), Is.False, "a wall");
        }

        [Test]
        public void CeilingTop_IsJudgedOnlyInsideTheRockOverANavMeshFloor()
        {
            // owner's cave at (119.4, -35.4): roof top 94.6 over the floor 91.88, the landscape at 125 above
            Assert.That(GroundGuard.OnCeiling(navHere: false, enclosed: true, dropToNavFloor: 2.72f), Is.True);
            // Reddock cave (fix-99): roof top 39.9 over the floor 33.9
            Assert.That(GroundGuard.OnCeiling(false, true, 6f), Is.True);
            Assert.That(GroundGuard.OnCeiling(true, true, 2.72f), Is.False, "NavMesh where he stands: a real floor");
            Assert.That(GroundGuard.OnCeiling(false, false, 2.72f), Is.False, "in the open (a rock top, a roof outside)");
            Assert.That(GroundGuard.OnCeiling(false, true, 1.1f), Is.False, "a crate or a ledge in the cave");
            Assert.That(GroundGuard.OnCeiling(false, true, null), Is.False, "no NavMesh floor below at all");
            Assert.That(GroundGuard.OnCeiling(false, true, 30f), Is.False, "too far down to be the floor under it");
        }

        [Test]
        public void FreeSpot_SameLevelFirst_ThenBelow_NeverAbove()
        {
            var spots = new (float, float, float)[]
            {
                (0f, 2.7f, 0f),    // the ceiling top straight above: refused
                (0.3f, 1.2f, 0f),  // a ledge above step height: refused
                (0f, -2.7f, 0f),   // the floor below
                (3f, 0.3f, 0f),    // the same level, 3 m off
                (1.5f, -0.4f, 0f), // the same level, 1.5 m off (a step down)
            };
            Assert.That(GroundGuard.Best(spots), Is.EqualTo(4));
            var noLevel = new (float, float, float)[] { (0f, 2.7f, 0f), (0f, -6f, 0f), (1f, -2.7f, 0f) };
            Assert.That(GroundGuard.Best(noLevel), Is.EqualTo(2), "the nearest floor below");
            Assert.That(GroundGuard.Best(new (float, float, float)[] { (0f, 2.7f, 0f), (0f, -40f, 0f) }), Is.EqualTo(-1));
            Assert.That(GroundGuard.Tier(0.65f), Is.EqualTo(GroundGuard.SpotTier.SameLevel));
            Assert.That(GroundGuard.Tier(0.7f), Is.EqualTo(GroundGuard.SpotTier.Refused));
        }

        [Test]
        public void WorldCatch_PrefersTheLastSafePlace()
        {
            Assert.That(GroundGuard.ChooseCatch(lastSafeDistance: 12f, surfaceAbove: true), Is.EqualTo(GroundGuard.CatchTarget.LastSafe));
            // fix-38: a teleport put him under the ground (no last safe place since): the surface he fell through
            Assert.That(GroundGuard.ChooseCatch(null, true), Is.EqualTo(GroundGuard.CatchTarget.SurfaceAbove));
            Assert.That(GroundGuard.ChooseCatch(400f, true), Is.EqualTo(GroundGuard.CatchTarget.SurfaceAbove), "far off: the surface above");
            Assert.That(GroundGuard.ChooseCatch(400f, false), Is.EqualTo(GroundGuard.CatchTarget.LastSafe));
            Assert.That(GroundGuard.ChooseCatch(null, false), Is.EqualTo(GroundGuard.CatchTarget.None));
        }

        [Test]
        public void WorldCatch_OwnCapsuleIsNoSurface()
        {
            // owner log: fell to 81.39, "put back at 83.54 (surface above)" — the top of his own capsule. With the cast
            // that skips him the lowest surface above is the cave floor (91.88; the roof 94.6, the landscape 125).
            float[] surfaces = { 125f, 94.6f, 91.88f };
            float? Cast(float from)
            {
                foreach (var y in surfaces) if (y <= from) return y;
                return null;
            }
            Assert.That(WorldCatch.LowestSurfaceAbove(81.39f, 2081.39f, Cast), Is.EqualTo(91.88f));
        }

        [Test]
        public void RisesAreCheckedForAFreePath()
        {
            // fix-130 (owner: the crates in the Montera mill, top 62.18 under the thatch at 64.19 / 64.92): a lift from the
            // crate onto the roof top rises 2.7 m and is checked; walking on a level floor or going down is not.
            Assert.That(GroundGuard.NeedsPathCheck(62.18f, 64.95f), Is.True);
            Assert.That(GroundGuard.NeedsPathCheck(62.18f, 62.19f), Is.False);
            Assert.That(GroundGuard.NeedsPathCheck(62.18f, 58.84f), Is.False);
            // the segments run through the old and new capsule's middle and head: under the roof the head (62.18 + 1.6)
            // stays below the thatch, on top of it the middle (64.95 + 0.975) lies above it, so both segments cross it
            var h = GroundGuard.PathHeights(1.95f, 0.35f);
            Assert.That(h, Is.EqualTo(new[] { 0.975f, 1.6f }).Within(1e-5));
            Assert.That(62.18f + h[1], Is.LessThan(64.19f));
            Assert.That(64.95f + h[0], Is.GreaterThan(64.92f));
            Assert.That(GroundGuard.PathHeights(0.5f, 0.35f)[1], Is.EqualTo(0.25f), "a short capsule: never below its middle");
        }
    }
}
