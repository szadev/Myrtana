using System;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Where the finisher stands (Docs/combat.md §5.5 "Placement"): reach per motion set, spot, detours, slide.</summary>
    public class FinishingPlacementTests
    {
        const float Eps = 1e-3f;

        static float Dist(float ax, float az, float bx, float bz) => (float)Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        [Test]
        public void Reach_PerMotionSet()
        {
            Assert.That(FinishingPlacement.ReachOf("Hero", "1H").Value.Strike, Is.EqualTo(1.95f).Within(Eps));
            Assert.That(FinishingPlacement.ReachOf("Hero", "1H").Value.G3Distance, Is.EqualTo(2f).Within(Eps));
            Assert.That(FinishingPlacement.ReachOf("Hero", "2H").Value.Strike, Is.EqualTo(1f).Within(Eps));
            Assert.That(FinishingPlacement.ReachOf("Hero", "Staff").Value.Strike, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(FinishingPlacement.ReachOf("orc", "2H").Value.Strike, Is.EqualTo(2f).Within(Eps));
            Assert.That(FinishingPlacement.ReachOf("Orc", "2H").Value.G3Distance, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(FinishingPlacement.ReachOf("Orc", "Staff").Value.Strike, Is.EqualTo(0.55f).Within(Eps));
            Assert.That(FinishingPlacement.ReachOf("Hero", "Fist"), Is.Null);
            Assert.That(FinishingPlacement.ReachOf("Orc", "1H"), Is.Null);
        }

        [Test]
        public void Spot_TooClose_StepsBackToTheReachFacingTheTorso()
        {
            // Hero 0.8 m south of the torso, 1H: ends 1.95 m south of it, facing north.
            var s = FinishingPlacement.Choose(0f, -0.8f, 90f, 0f, 0f, 1.95f, (x, z) => true);
            Assert.That(s.Moved, Is.True);
            Assert.That(s.X, Is.EqualTo(0f).Within(Eps));
            Assert.That(s.Z, Is.EqualTo(-1.95f).Within(Eps));
            Assert.That(s.Yaw, Is.EqualTo(0f).Within(Eps));
            Assert.That(s.Detour, Is.EqualTo(0f));
        }

        [Test]
        public void Spot_FarAway_ComesCloserAlongTheLine()
        {
            // 3 m east of the torso, 2H (1 m): ends 1 m east, facing west (270°).
            var s = FinishingPlacement.Choose(3f, 0f, 0f, 0f, 0f, 1f, null);
            Assert.That(s.X, Is.EqualTo(1f).Within(Eps));
            Assert.That(s.Z, Is.EqualTo(0f).Within(Eps));
            Assert.That(s.Yaw, Is.EqualTo(270f).Within(Eps));
        }

        [Test]
        public void Spot_OnTopOfTheTorso_BacksOffBehindItsOwnFacing()
        {
            var s = FinishingPlacement.Choose(5f, 5f, 90f, 5f, 5f, 1f, null); // facing east, standing on the torso
            Assert.That(s.X, Is.EqualTo(4f).Within(Eps));
            Assert.That(s.Z, Is.EqualTo(5f).Within(Eps));
            Assert.That(s.Yaw, Is.EqualTo(90f).Within(Eps));
        }

        [Test]
        public void Spot_Blocked_TurnsAroundTheBodyClockwiseFirst()
        {
            // Straight spot (0,-2) is blocked by a wall south of z = -1.5: ±30° are still behind it; the first free detour is +60° (clockwise seen
            // from above, the approach direction south → south-west), then the mirror.
            Func<float, float, bool> free = (x, z) => z > -1.5f;
            var s = FinishingPlacement.Choose(0f, -1f, 0f, 0f, 0f, 2f, free);
            Assert.That(s.Moved, Is.True);
            Assert.That(s.Detour, Is.EqualTo(60f));
            Assert.That(Dist(s.X, s.Z, 0f, 0f), Is.EqualTo(2f).Within(Eps));
            Assert.That(free(s.X, s.Z), Is.True);
            // Facing the torso from the new spot.
            Assert.That(s.Yaw, Is.EqualTo(FinishingPlacement.YawTowards(s.X, s.Z, 0f, 0f, 0f)).Within(Eps));
        }

        [Test]
        public void Spot_DetourOrder_PlusThirtyIsClockwise()
        {
            // Only the +30° spot is free. Approach from the south (direction (0,-1)); +30° clockwise seen from above
            // turns that direction towards the west: x < 0.
            int calls = 0;
            var s = FinishingPlacement.Choose(0f, -1f, 0f, 0f, 0f, 1f, (x, z) => ++calls == 2);
            Assert.That(s.Detour, Is.EqualTo(30f));
            Assert.That(s.X, Is.EqualTo(-0.5f).Within(Eps));
            Assert.That(s.Z, Is.EqualTo(-(float)Math.Sqrt(3) / 2).Within(Eps));
        }

        [Test]
        public void Spot_AllBlocked_SlidesStraightAnyway()
        {
            var s = FinishingPlacement.Choose(1f, 1f, 0f, 0f, 0f, 2f, (x, z) => false);
            Assert.That(s.Moved, Is.True);
            Assert.That(s.Forced, Is.True);
            Assert.That(s.Detour, Is.EqualTo(0f));
            Assert.That(s.X, Is.EqualTo((float)Math.Sqrt(2)).Within(Eps)); // 2 m from the torso on the attacker's side
            Assert.That(s.Z, Is.EqualTo((float)Math.Sqrt(2)).Within(Eps));
            Assert.That(s.Yaw, Is.EqualTo(225f).Within(Eps)); // facing south-west to the torso
        }

        [Test]
        public void Follow_KeepsTheApproachForAMovedTorso()
        {
            var s = FinishingPlacement.Choose(0f, -3f, 0f, 0f, 0f, 1f, null);
            var f = FinishingPlacement.Follow(s, 0.5f, 0.2f, 1f);
            Assert.That(f.X, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(f.Z, Is.EqualTo(-0.8f).Within(Eps));
            Assert.That(f.Yaw, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void Slide_LinearUntilTheStabFrame()
        {
            Assert.That(FinishingPlacement.SlideFraction(0f, 0.8f), Is.EqualTo(0f));
            Assert.That(FinishingPlacement.SlideFraction(0.4f, 0.8f), Is.EqualTo(0.5f).Within(Eps));
            Assert.That(FinishingPlacement.SlideFraction(0.8f, 0.8f), Is.EqualTo(1f));
            Assert.That(FinishingPlacement.SlideFraction(1f, 0.8f), Is.EqualTo(1f));
            var spot = FinishingPlacement.Choose(0f, -0.5f, 0f, 0f, 0f, 2f, null);
            var p = FinishingPlacement.Along(0f, -0.5f, spot, 0.5f);
            Assert.That(p.x, Is.EqualTo(0f).Within(Eps));
            Assert.That(p.z, Is.EqualTo(-1.25f).Within(Eps));
        }

        [Test]
        public void TurnYaw_ShortWayRound()
        {
            Assert.That(FinishingPlacement.TurnYaw(350f, 10f, 0.5f), Is.EqualTo(0f).Within(Eps));
            Assert.That(FinishingPlacement.TurnYaw(10f, 350f, 0.5f), Is.EqualTo(0f).Within(Eps));
            Assert.That(FinishingPlacement.TurnYaw(0f, 90f, 1.5f), Is.EqualTo(90f).Within(Eps));
            Assert.That(FinishingPlacement.TurnYaw(0f, 90f, 0f), Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void G3Placement_1HTipLandsOnTheTorso()
        {
            // The measured 1H tip (1.95 m ahead at the stab) lands within 5 cm of the torso from the chosen spot,
            // whatever the start distance.
            var reach = FinishingPlacement.ReachOf("Hero", "1H").Value;
            foreach (float start in new[] { 0.3f, 1f, 2f, 3f })
            {
                var s = FinishingPlacement.Choose(start, 0f, 0f, 0f, 0f, reach.Strike, null);
                double r = s.Yaw * Math.PI / 180.0;
                float tipX = s.X + (float)Math.Sin(r) * 1.95f, tipZ = s.Z + (float)Math.Cos(r) * 1.95f;
                Assert.That(Dist(tipX, tipZ, 0f, 0f), Is.LessThan(0.05f), "start " + start);
            }
        }

        [Test]
        public void HeightMiss_LevelBand()
        {
            Assert.That(FinishingPlacement.HeightMiss(0f), Is.EqualTo(0f));
            Assert.That(FinishingPlacement.HeightMiss(0.1f), Is.EqualTo(0f));
            Assert.That(FinishingPlacement.HeightMiss(-0.3f), Is.EqualTo(0f));
            Assert.That(FinishingPlacement.HeightMiss(0.4f), Is.EqualTo(0.3f).Within(Eps));   // uphill: blade above the body
            Assert.That(FinishingPlacement.HeightMiss(-0.5f), Is.EqualTo(0.2f).Within(Eps));  // downhill: under it
        }

        [Test]
        public void Level_UphillSpot_TakesTheFirstLevelApproach()
        {
            // Victim on ground 0; ground rises to the south (z < 0) by 0.2 m/m: the straight spot (z = -1.95) is 0.39 m up.
            Func<float, float, float?> ground = (x, z) => z < 0 ? -0.2f * z : 0f;
            var s = FinishingPlacement.ChooseLevel(0f, -0.8f, 0f, 0f, 0f, 1.95f, ground, 0f);
            Assert.That(s.Moved, Is.True);
            Assert.That(FinishingPlacement.HeightMiss(ground(s.X, s.Z).Value), Is.EqualTo(0f));
            Assert.That(Math.Abs(s.Detour), Is.EqualTo(90f), "first approach with level ground (z >= -0.5)");
            Assert.That(Dist(s.X, s.Z, 0f, 0f), Is.EqualTo(1.95f).Within(Eps));
        }

        [Test]
        public void Level_NoLevelSpot_TakesTheSmallestMiss_BlockedSkipped()
        {
            // Everything uphill; the least steep spot is east (x > 0), but east itself is blocked: next best wins.
            Func<float, float, float?> ground = (x, z) => x > 1.9f ? (float?)null : 0.5f + 0.1f * (z + 2f) - 0.05f * x;
            var s = FinishingPlacement.ChooseLevel(0f, -0.8f, 0f, 0f, 0f, 1.95f, ground, 0f);
            Assert.That(s.Moved, Is.True);
            Assert.That(ground(s.X, s.Z).HasValue, Is.True);
            float miss = FinishingPlacement.HeightMiss(ground(s.X, s.Z).Value);
            foreach (float d in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f })
            {
                double r = d * Math.PI / 180.0;
                float x = (float)(-Math.Sin(r) * 1.95), z = (float)(-Math.Cos(r) * 1.95);
                var g = ground(x, z);
                if (g.HasValue) Assert.That(miss, Is.LessThanOrEqualTo(FinishingPlacement.HeightMiss(g.Value) + Eps), "detour " + d);
            }
        }

        [Test]
        public void Level_AllBlocked_BacksOffToTheReach()
        {
            // The owner's case: the hero stands 0.8 m from the torso and nothing around is free: he still backs off to the
            // reach (the 1H stab lands 1.95 m ahead), instead of stabbing 1.15 m past the body.
            var s = FinishingPlacement.ChooseLevel(0f, -0.8f, 0f, 0f, 0f, 1.95f, (x, z) => null, 0f);
            Assert.That(s.Moved, Is.True);
            Assert.That(s.Forced, Is.True);
            Assert.That(s.Z, Is.EqualTo(-1.95f).Within(Eps));
        }

        [Test]
        public void Level_FlatGround_SameAsChoose()
        {
            var a = FinishingPlacement.ChooseLevel(0.3f, -1f, 0f, 0f, 0f, 1.95f, (x, z) => 0.05f, 0f);
            var b = FinishingPlacement.Choose(0.3f, -1f, 0f, 0f, 0f, 1.95f, null);
            Assert.That(a.X, Is.EqualTo(b.X).Within(Eps));
            Assert.That(a.Z, Is.EqualTo(b.Z).Within(Eps));
            Assert.That(a.Detour, Is.EqualTo(0f));
        }
    }
}
