using System;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>G3's combat movement (Docs/combat.md §5.6): the motion name's number is cm over the motion, towards or away from the other.</summary>
    public class CombatLungeTests
    {
        const float Eps = 1e-3f;

        [Test]
        public void Attack_GoesTheNamesDistanceTowardsTheTarget_TurningToFaceIt()
        {
            // Hero 1H Attack Hit "_118": target 3 m east, hero facing north.
            var plan = CombatLunge.Plan("Attack", "Hit", 1.18f, 0f, 0f, 0f, true, 3f, 0f, turn: true);
            Assert.That(plan.Distance, Is.EqualTo(1.18f).Within(Eps));
            Assert.That(plan.DirX, Is.EqualTo(1f).Within(Eps));
            Assert.That(plan.DirZ, Is.EqualTo(0f).Within(Eps));
            Assert.That(plan.Towards, Is.True);
            Assert.That(plan.StopsAtContact, Is.True);
            Assert.That(plan.Turns, Is.True);
            Assert.That(plan.FaceYaw, Is.EqualTo(90f).Within(Eps));
        }

        [Test]
        public void Travel_IsLinearOverTheMotion_NotASpeed()
        {
            // 0.32 s motion: the whole 1.18 m by its end, half of it half-way (not 1.18 m/s × 0.32 s = 0.38 m).
            var plan = CombatLunge.Plan("Attack", "Hit", 1.18f, 0f, 0f, 0f, false, 0f, 0f, turn: true);
            Assert.That(plan.At(1f), Is.EqualTo(1.18f).Within(Eps));
            Assert.That(plan.At(0.5f), Is.EqualTo(0.59f).Within(Eps));
            Assert.That(plan.At(2f), Is.EqualTo(1.18f).Within(Eps));
            Assert.That(plan.At(-1f), Is.EqualTo(0f));
        }

        [Test]
        public void Attack_WithoutTarget_GoesForward_NoTurn_NoContactStop()
        {
            var plan = CombatLunge.Plan("PowerAttack", "Hit", 1.8f, 0f, 0f, 90f, false, 0f, 0f, turn: true);
            Assert.That(plan.DirX, Is.EqualTo(1f).Within(Eps));
            Assert.That(plan.DirZ, Is.EqualTo(0f).Within(Eps));
            Assert.That(plan.Distance, Is.EqualTo(1.8f).Within(Eps));
            Assert.That(plan.Turns, Is.False);
            Assert.That(plan.StopsAtContact, Is.False);
        }

        [Test]
        public void JumpBack_GoesAwayFromTheTarget_FacingIt()
        {
            // Wolf JumpBack "_150", target north of it, wolf facing east.
            var plan = CombatLunge.Plan("JumpBack", "Hit", 1.5f, 0f, 0f, 90f, true, 0f, 2f, turn: true);
            Assert.That(plan.DirX, Is.EqualTo(0f).Within(Eps));
            Assert.That(plan.DirZ, Is.EqualTo(-1f).Within(Eps));
            Assert.That(plan.Towards, Is.False);
            Assert.That(plan.StopsAtContact, Is.False);
            Assert.That(plan.FaceYaw, Is.EqualTo(0f).Within(Eps));
            Assert.That(plan.Turns, Is.True);
        }

        [Test]
        public void JumpBack_WithoutTarget_GoesBackward()
        {
            var plan = CombatLunge.Plan("JumpBack", "Hit", 1.5f, 0f, 0f, 0f, false, 0f, 0f, turn: true);
            Assert.That(plan.DirZ, Is.EqualTo(-1f).Within(Eps));
        }

        [Test]
        public void Stumble_And_Fall_GoAwayFromTheHitter_WithoutTurning()
        {
            var stumble = CombatLunge.Plan("StumbleR", "Hit", 1.5f, 0f, 0f, 0f, true, -1f, 0f, turn: false);
            Assert.That(stumble.DirX, Is.EqualTo(1f).Within(Eps));
            Assert.That(stumble.Turns, Is.False);
            Assert.That(stumble.StopsAtContact, Is.False);
            var fall = CombatLunge.Plan("LieKnockDown", "Begin", 1.5f, 0f, 0f, 0f, true, 0f, 1f, turn: false);
            Assert.That(fall.DirZ, Is.EqualTo(-1f).Within(Eps));
            Assert.That(fall.Distance, Is.EqualTo(1.5f).Within(Eps));
        }

        [Test]
        public void QuickStumble_NeverTurns()
        {
            var plan = CombatLunge.Plan("QuickStumble", "Hit", 0.9f, 0f, 0f, 0f, true, 1f, 0f, turn: true);
            Assert.That(plan.Turns, Is.False);
        }

        [Test]
        public void Recover_DoesNotMove()
        {
            var plan = CombatLunge.Plan("Attack", "Recover", 1f, 0f, 0f, 0f, true, 1f, 0f, turn: true);
            Assert.That(plan.Distance, Is.EqualTo(0f));
            Assert.That(plan.Turns, Is.False);
        }

        [Test]
        public void Raise_OnlyTurns()
        {
            var plan = CombatLunge.Plan("Attack", "Raise", 0f, 0f, 0f, 0f, true, 0f, -2f, turn: true);
            Assert.That(plan.Distance, Is.EqualTo(0f));
            Assert.That(plan.Turns, Is.True);
            Assert.That(plan.FaceYaw, Is.EqualTo(180f).Within(Eps));
        }

        [Test]
        public void FinishingHit_EndsTheNamesDistanceFromTheTarget()
        {
            // 1H "_200": 1.0 m from the victim → 1.0 m back; 2.8 m → 0.8 m forward; no victim → stays.
            var close = CombatLunge.Plan("FinishingAttack", "Hit", 2f, 0f, 0f, 0f, true, 0f, 1f, turn: true);
            Assert.That(close.Distance, Is.EqualTo(1f).Within(Eps));
            Assert.That(close.DirZ, Is.EqualTo(-1f).Within(Eps));
            Assert.That(close.StopsAtContact, Is.False);
            var far = CombatLunge.Plan("FinishingAttack", "Hit", 2f, 0f, 0f, 0f, true, 0f, 2.8f, turn: true);
            Assert.That(far.Distance, Is.EqualTo(0.8f).Within(Eps));
            Assert.That(far.DirZ, Is.EqualTo(1f).Within(Eps));
            Assert.That(CombatLunge.Plan("FinishingAttack", "Hit", 2f, 0f, 0f, 0f, false, 0f, 0f, turn: true).Distance, Is.EqualTo(0f));
        }

        [Test]
        public void Attacks_GoTowards_EverythingElseAway()
        {
            foreach (var a in new[] { "Attack", "PowerAttack", "QuickAttackL", "QuickAttackR", "SimpleWhirl", "SprintAttack", "WhirlAttack", "PierceAttack", "HackAttack", "FinishingAttack", "GetUpAttack" })
                Assert.That(CombatLunge.GoesTowards(a), Is.True, a);
            foreach (var a in new[] { "JumpBack", "Parade", "StumbleR", "ParadeStumbleL", "PierceStumble", "LieKnockDown", "LieDead", "GetUpParade" })
                Assert.That(CombatLunge.GoesTowards(a), Is.False, a);
        }

        [Test]
        public void Allowed_StopsAtTheBodiesContact()
        {
            // Target 2 m ahead, footprints 0.4 + 0.4: contact after 1.2 m.
            Assert.That(CombatLunge.Allowed(0, 0, 0, 1, 0.5f, 0, 2, 0.8f), Is.EqualTo(0.5f).Within(Eps));
            Assert.That(CombatLunge.Allowed(0, 0, 0, 1, 1.5f, 0, 2, 0.8f), Is.EqualTo(1.2f).Within(Eps));
            // Passing by at 1 m to the side: free.
            Assert.That(CombatLunge.Allowed(0, 0, 0, 1, 3f, 1f, 2, 0.8f), Is.EqualTo(3f).Within(Eps));
            // Moving away: free; already inside and moving in: blocked; inside moving out: free.
            Assert.That(CombatLunge.Allowed(0, 0, 0, -1, 1f, 0, 2, 0.8f), Is.EqualTo(1f).Within(Eps));
            Assert.That(CombatLunge.Allowed(0, 0, 0, 1, 1f, 0, 0.5f, 0.8f), Is.EqualTo(0f));
            Assert.That(CombatLunge.Allowed(0, 0, 0, -1, 1f, 0, 0.5f, 0.8f), Is.EqualTo(1f).Within(Eps));
        }

        [Test]
        public void GroundAhead_LedgesStop_SlopesDoNot()
        {
            Assert.That(CombatLunge.LookAhead(0.06f, 0.016f), Is.EqualTo(0.5625f).Within(Eps));
            Assert.That(CombatLunge.GroundAhead(10f, true, 10.3f, 0.5f), Is.True);
            Assert.That(CombatLunge.GroundAhead(10f, true, 9f, 0.5f), Is.True);   // 49° slope down over 0.5 m + step
            Assert.That(CombatLunge.GroundAhead(10f, true, 8f, 0.5f), Is.False);  // a 2 m drop
            Assert.That(CombatLunge.GroundAhead(10f, false, 0f, 0.5f), Is.False);
        }
    }
}
