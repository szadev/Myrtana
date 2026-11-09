using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-181 (owner, build 21a Nordmar: snow wolves spinning in circles and going into the ground while idle): the spin
    /// guard's monitor, and the floor a shown wanderer gets when its simulated height is the spawn's (a guess).
    /// </summary>
    public class SpinMonitorTests
    {
        const float Dt = 1f / 30f;

        [Test]
        public void TurningOnTheSpot_TwoFullTurns_IsASpin()
        {
            var m = new SpinMonitor();
            bool spun = false;
            float yaw = 0;
            for (int f = 0; f < 300 && !spun; f++)
            {
                yaw = (yaw + 240f * Dt) % 360f; // NpcView's turn speed, always the same way
                spun = m.Step(yaw, 10f, 20f, Dt);
            }
            Assert.That(spun, Is.True);
            Assert.That(m.LastTurn, Is.GreaterThanOrEqualTo(SpinMonitor.SpinTurn));
            Assert.That(m.LastSeconds, Is.EqualTo(3f).Within(0.1f));
        }

        [Test]
        public void CirclingATargetInAFight_WalksMetres_IsNotASpin()
        {
            var m = new SpinMonitor();
            float r = 2f, a = 0f;
            for (int f = 0; f < 600; f++)
            {
                a += 1.5f * Dt; // rad/s round the target at 3 m/s
                float yaw = a * 57.29578f + 90f;
                Assert.That(m.Step(yaw % 360f, r * (float)System.Math.Sin(a), r * (float)System.Math.Cos(a), Dt), Is.False);
            }
        }

        [Test]
        public void TurningBackAndForth_OrLookingRound_IsNotASpin()
        {
            var m = new SpinMonitor();
            for (int f = 0; f < 3000; f++)
            {
                float yaw = 100f + 170f * (float)System.Math.Sin(f * 0.05f); // ±170 deg sweeps, never a full turn one way
                Assert.That(m.Step(yaw, 0f, 0f, Dt), Is.False);
            }
        }

        [Test]
        public void ATeleportOrASlowDrift_StartsTheCountAfresh()
        {
            var m = new SpinMonitor();
            float yaw = 0;
            for (int f = 0; f < 60; f++) { yaw += 10f; m.Step(yaw % 360f, 0f, 0f, Dt); } // 600 deg
            Assert.That(m.Step((yaw + 10f) % 360f, 50f, 0f, Dt), Is.False, "teleported: afresh");
            for (int f = 0; f < 60; f++) { yaw += 10f; Assert.That(m.Step(yaw % 360f, 50f, 0f, Dt), Is.False); }
            var slow = new SpinMonitor();
            for (int f = 0; f < 3000; f++) Assert.That(slow.Step(f * 0.2f % 360f, 0f, 0f, Dt), Is.False, "720 deg take 120 s: beyond the window");
        }

        [Test]
        public void DeltaAngle_IsTheShortestSignedDifference()
        {
            Assert.That(SpinMonitor.DeltaAngle(350f, 10f), Is.EqualTo(20f).Within(1e-4f));
            Assert.That(SpinMonitor.DeltaAngle(10f, 350f), Is.EqualTo(-20f).Within(1e-4f));
            Assert.That(SpinMonitor.DeltaAngle(0f, 180f), Is.EqualTo(180f).Within(1e-4f));
        }

        /// <summary>
        /// A Near wanderer's shown pose carries its spawn's height; 4 m from the spawn on a 45 degree slope the floor lies
        /// 4.4 m above it (IceWolf#162fca67, spawn 403.0,129.4,1074.6: floor 133.8). The slope reach found nothing (the
        /// column ray starts 3 m up, inside the hill) and the body stayed at the spawn's height in the ground; with the guessed
        /// height's reach (8 m, as the Active walk's end) the floor is found.
        /// </summary>
        [Test]
        public void AGuessedHeight_FindsTheFloorUpTheSlope_TheSlopeReachDoesNot()
        {
            const float spawnY = 129.4f, floorY = 133.8f, half = 0.65f;
            // what a ray down from 3.05 m above the point sees: nothing (it starts inside the hill)
            var fromSlopeReach = new List<NpcGroundRules.Surface>();
            Assert.That(NpcGroundRules.Pick(spawnY, fromSlopeReach, half, 150f, float.NaN, out _), Is.False);
            // from 8.05 m above: the slope's face
            var fromGuessReach = new List<NpcGroundRules.Surface> { new NpcGroundRules.Surface(floorY, 45f) };
            Assert.That(NpcGroundRules.Pick(spawnY, fromGuessReach, half, 150f, float.NaN, out int i, upLimit: 8f), Is.True);
            Assert.That(fromGuessReach[i].Y, Is.EqualTo(floorY));
            // even with the face in the column, the slope reach refuses a floor 4.4 m up (UpReach caps at 3 m)
            Assert.That(NpcGroundRules.Pick(spawnY, fromGuessReach, half, 150f, float.NaN, out _), Is.False);
        }

        [Test]
        public void AGuessedHeight_PrefersTheFloorNearest_NotARoofTopFarAbove()
        {
            // a creature in a hollow: its floor 0.3 m above the guess, a rock top 6 m up
            var col = new List<NpcGroundRules.Surface> { new NpcGroundRules.Surface(106f, 10f), new NpcGroundRules.Surface(100.3f, 5f) };
            Assert.That(NpcGroundRules.Pick(100f, col, 0.5f, 150f, float.NaN, out int i, upLimit: 8f), Is.True);
            Assert.That(col[i].Y, Is.EqualTo(100.3f));
        }

        [Test]
        public void BuriedFeet_TheModelStandsOnTheDrawnGround_NeverLower()
        {
            // feet 2 m inside the slope: lifted onto the drawn ground (0.1 m above the collision)
            Assert.That(G3.Unity.ReliefFootingRules.BuriedLift(100f, 102f, 102.1f, 0f), Is.EqualTo(2.1f).Within(1e-4f));
            // on the ground, or deeper than a body can be on this layer (a cave below): unchanged
            Assert.That(G3.Unity.ReliefFootingRules.BuriedLift(102f, 102.1f, 102.3f, 0.2f), Is.EqualTo(0.2f));
            Assert.That(G3.Unity.ReliefFootingRules.BuriedLift(90f, 102f, 102f, 0f), Is.EqualTo(0f));
            // never lowers
            Assert.That(G3.Unity.ReliefFootingRules.BuriedLift(100f, 101f, 100.5f, 1.2f), Is.EqualTo(1.2f));
        }
    }
}
