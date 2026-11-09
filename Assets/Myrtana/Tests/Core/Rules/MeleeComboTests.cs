using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// G3 CP 1.75 combo timing (Docs/combat.md §9.3): which phases a move plays, when the next queued attack starts, the
    /// combo pose, and the input queue. Motion lengths are the hero's Fwd attack motions (G3Cli anim motion).
    /// </summary>
    public class MeleeComboTests
    {
        static readonly MeleeStance Sword = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };

        static float Speed(WeaponClass hand, CombatMotion motion = CombatMotion.Attack, bool windUp = false) =>
            MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = motion, WindUp = windUp, IsPlayer = true, RightHand = hand, Stamina = 100 },
                                    new RuleOptions());

        [Test]
        public void Shapes_AsTheG3AiCommands()
        {
            Assert.AreEqual(new CombatMoveShape(false, false), MeleeComboRules.ShapeOf(CombatAction.Attack), "_AI_Attack: Hit only");
            Assert.AreEqual(new CombatMoveShape(false, false, 0.1), MeleeComboRules.ShapeOf(CombatAction.QuickAttack), "_AI_QuickAttack: Hit, then sAIWaitInstr 100 ms");
            Assert.AreEqual(new CombatMoveShape(false, false), MeleeComboRules.ShapeOf(CombatAction.SimpleWhirl));
            Assert.AreEqual(new CombatMoveShape(false, false), MeleeComboRules.ShapeOf(CombatAction.WhirlAttack));
            Assert.AreEqual(new CombatMoveShape(false, false), MeleeComboRules.ShapeOf(CombatAction.GetUpAttack), "_AI_GetUpAttack: Hit only");
            Assert.AreEqual(new CombatMoveShape(true, false), MeleeComboRules.ShapeOf(CombatAction.PowerAttack), "Raise + Hit, Recover tail");
            Assert.AreEqual(new CombatMoveShape(true, true), MeleeComboRules.ShapeOf(CombatAction.PierceAttack), "explicit Recover");
            Assert.AreEqual(new CombatMoveShape(true, true), MeleeComboRules.ShapeOf(CombatAction.HackAttack));
            Assert.AreEqual(new CombatMoveShape(true, true), MeleeComboRules.ShapeOf(CombatAction.FinishingAttack));
        }

        // (Hit seconds of the combo poses P0, P1, P2 in order; the weapon's Attack speed factor)
        [TestCase(WeaponClass.OneHanded, new[] { 0.32, 0.36, 0.32 }, 533, 1133)]
        [TestCase(WeaponClass.TwoHanded, new[] { 0.52, 0.40, 0.52 }, 743, 1314)]
        [TestCase(WeaponClass.Staff, new[] { 0.52, 0.40, 0.52 }, 743, 1314)]
        [TestCase(WeaponClass.Fist, new[] { 0.32, 0.32, 0.32 }, 320, 640)]
        public void AttackCombo_NextStepStartsAtTheHitsEnd(WeaponClass hand, double[] hits, int step2Ms, int step3Ms)
        {
            var shape = MeleeComboRules.ShapeOf(CombatAction.Attack);
            float speed = Speed(hand);
            // Raise and Recover lengths do not matter: an Attack has no Raise and its Recover is a tail.
            var first = MeleeComboRules.Timeline(shape, raise: 0.44, hits[0], recover: 0.56, speed, speed);
            Assert.AreEqual(0, first.HitStart, "the swing starts with the click's release");
            double step2 = MeleeComboRules.NextStart(first, click: 0.05);
            var second = MeleeComboRules.Timeline(shape, 0.28, hits[1], 0.44, speed, speed);
            double step3 = step2 + MeleeComboRules.NextStart(second, click: 0.05);
            Assert.AreEqual(step2Ms, step2 * 1000, 1);
            Assert.AreEqual(step3Ms, step3 * 1000, 1);
            Assert.AreEqual(first.End + 0.2, MeleeComboRules.NextStart(first, first.End + 0.2), 1e-9, "a click after the Hit starts at once (cuts the Recover)");
        }

        [Test]
        public void PowerAttack_EndsWithItsHit_PierceWithItsRecover()
        {
            var power = MeleeComboRules.Timeline(MeleeComboRules.ShapeOf(CombatAction.PowerAttack), 0.6, 0.4, 0.8, raiseSpeed: 1.5, hitSpeed: 1);
            Assert.AreEqual(0.4, power.HitStart, 1e-9);
            Assert.AreEqual(0.8, power.End, 1e-9);
            Assert.AreEqual(1.6, power.TailEnd, 1e-9);
            var pierce = MeleeComboRules.Timeline(MeleeComboRules.ShapeOf(CombatAction.PierceAttack), 0.5, 0.4, 0.6);
            Assert.AreEqual(1.5, pierce.End, 1e-9);
        }

        [Test]
        public void ComboPose_FollowsTheHitsEndPose()
        {
            Assert.AreEqual(1, MeleeComboRules.EndPoseOf("Hero_Stand_None_1H_P0_Attack_Hit_N_Fwd_00_%_00_P1_118_R"));
            Assert.AreEqual(0, MeleeComboRules.EndPoseOf("Hero_Stand_None_2H_P1_Attack_Hit_N_Fwd_00_%_00_P0_90_L"));
            Assert.AreEqual(2, MeleeComboRules.EndPoseOf("Hero_Stand_None_1H_P0_Attack_Hit_N_Left_00_%_00_P22_94_L"), "G3 TrackCurrentPose: Pxy → y");
            Assert.AreEqual(-1, MeleeComboRules.EndPoseOf("Hero_Stand_None_1H"));
            Assert.AreEqual(1, MeleeComboRules.NextComboPose(chained: true, previousHitEndPose: 1, previousPose: 0));
            Assert.AreEqual(0, MeleeComboRules.NextComboPose(chained: true, previousHitEndPose: 0, previousPose: 1), "2H: P1's Hit ends in P0");
            Assert.AreEqual(3, MeleeComboRules.NextComboPose(chained: true, previousHitEndPose: -1, previousPose: 2), "no end pose: next");
            Assert.AreEqual(0, MeleeComboRules.NextComboPose(chained: false, previousHitEndPose: 2, previousPose: 1), "after the Recover: P0");
        }

        [Test]
        public void ClicksDuringTheHit_AreQueued_OneSlot_StartAtMoveEnd()
        {
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.Attack), input.PrimaryReleased(0.06, Sword));
            input.PrimaryPressed(0.10, Sword); // 40 ms into the swing: accepted
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(0.15, Sword));
            Assert.AreEqual(CombatAction.Attack, input.Queued);
            input.PrimaryPressed(0.30, Sword); // a second click replaces the first (one slot)
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(0.36, Sword));
            Assert.AreEqual(MeleeCommand.Start(CombatAction.Attack), input.MoveEnded(0.593, Sword), "starts at the Hit's end");
            Assert.AreEqual(CombatAction.None, input.Queued);
            Assert.AreEqual(MeleeCommand.None, input.MoveEnded(1.19, Sword), "nothing more queued");
        }

        [Test]
        public void LowFps_TapTimedByInputEvents_StaysATap()
        {
            // 25 fps hitch: the press is read in the frame at 1.000 s (event 0.990 on the input clock 10.990), the release
            // (event 11.070, an 80 ms tap) only in the frame 102 ms later, which reads the release before the hold check.
            var input = new MeleeInputInterpreter();
            double press = InputEventTime.ToGameTime(1.000, 11.000, 10.990);
            Assert.AreEqual(0.990, press, 1e-9);
            Assert.AreEqual(MeleeCommand.None, input.PrimaryPressed(press, Sword));
            double frame = 1.102, release = InputEventTime.ToGameTime(frame, 11.102, 11.070, 1, notBefore: 1.000);
            Assert.AreEqual(1.070, release, 1e-9);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.Attack), input.PrimaryReleased(release, Sword), "80 ms: a tap, not a power raise");
            Assert.AreEqual(MeleeCommand.None, input.Update(frame, Sword), "no hold after the release");

            // Held for real: the frame check fires the hold once the press is older than 100 ms.
            input = new MeleeInputInterpreter();
            input.PrimaryPressed(InputEventTime.ToGameTime(2.000, 12.000, 11.995), Sword);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.PowerAttack), input.Update(2.102, Sword));
        }

        [Test]
        public void InputEventTime_Clamps()
        {
            Assert.AreEqual(5.0, InputEventTime.ToGameTime(5, 10, 0), "no stamp: now");
            Assert.AreEqual(5.0, InputEventTime.ToGameTime(5, 10, 10.5), "future event: now");
            Assert.AreEqual(4.9, InputEventTime.ToGameTime(5, 10, 9.8, 0.5), 1e-9, "slow motion scales the age");
            Assert.AreEqual(4.95, InputEventTime.ToGameTime(5, 10, 9.0, 1, notBefore: 4.95), 1e-9, "not before the previous frame");
        }

        [Test]
        public void HoldQueuedThenQuickRelease_IsATap()
        {
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            input.PrimaryReleased(0.05, Sword); // Attack runs
            input.PrimaryPressed(0.2, Sword);
            Assert.AreEqual(MeleeCommand.None, input.Update(0.31, Sword), "the hold is queued");
            Assert.AreEqual(CombatAction.PowerAttack, input.Queued);
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(0.38, Sword));
            Assert.AreEqual(CombatAction.Attack, input.Queued, "released within 200 ms: G3 reads a tap and replaces the queue");

            input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            input.PrimaryReleased(0.05, Sword);
            input.PrimaryPressed(0.2, Sword);
            input.Update(0.31, Sword);
            Assert.AreEqual(MeleeCommand.Start(CombatAction.PowerAttack), input.MoveEnded(0.5, Sword), "still held at the move's end");
            Assert.AreEqual(MeleeCommand.Start(CombatAction.Attack), input.PrimaryReleased(0.6, Sword), "its release combos (0.1 s in)");
        }

        [Test]
        public void Trace_ReportsQueueAndStart()
        {
            var lines = new System.Collections.Generic.List<string>();
            var input = new MeleeInputInterpreter { Trace = lines.Add };
            input.PrimaryPressed(0, Sword);
            input.PrimaryReleased(0.05, Sword);
            input.PrimaryPressed(0.1, Sword);
            input.PrimaryReleased(0.15, Sword);
            input.MoveEnded(0.6, Sword);
            Assert.AreEqual(3, lines.Count);
            StringAssert.Contains("Attack starts", lines[0]);
            StringAssert.Contains("queued", lines[1]);
            StringAssert.Contains("queued Attack starts", lines[2]);
        }
    }
}
