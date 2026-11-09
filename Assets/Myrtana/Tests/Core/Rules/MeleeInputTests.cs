using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The player's melee input against the CP 1.75 timings (gameplay-logic.md §3.7, combat_moves.json).</summary>
    public class MeleeInputTests
    {
        static readonly MeleeStance Sword = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };
        static readonly MeleeStance TwoHander = new MeleeStance { RightHand = WeaponClass.TwoHanded, CanParade = true };

        static MeleeCommand Start(CombatAction move) => MeleeCommand.Start(move);

        [Test]
        public void TapPrimary_IsAttack_HoldIsPowerAttack()
        {
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            Assert.AreEqual(MeleeCommand.None, input.Update(0.09, Sword));
            Assert.AreEqual(Start(CombatAction.Attack), input.PrimaryReleased(0.09, Sword));

            input.MoveEnded(1, Sword);
            input.PrimaryPressed(2, Sword);
            Assert.AreEqual(Start(CombatAction.PowerAttack), input.Update(2.11, Sword));
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(2.8, Sword), "released after the combo windows: the power attack runs on");
        }

        [Test]
        public void PowerAttackReleasedEarly_ComboesIntoAttackOrSimpleWhirl()
        {
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            input.Update(0.11, Sword);
            Assert.AreEqual(Start(CombatAction.Attack), input.PrimaryReleased(0.3, Sword), "≤ 0.2 s into the power attack");

            var dual = new MeleeStance { RightHand = WeaponClass.OneHanded, LeftOneHandedOrTorch = true, CanParade = true };
            input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, dual);
            input.Update(0.11, dual);
            Assert.AreEqual(Start(CombatAction.SimpleWhirl), input.PrimaryReleased(0.5, dual), "0.2–0.5 s with 1H in the left hand");

            input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            input.Update(0.11, Sword);
            Assert.AreEqual(Start(CombatAction.Attack), input.PrimaryReleased(0.5, Sword), "without a left-hand weapon the whirl falls back to Attack");
        }

        [Test]
        public void BlockHeld_TapIsQuickOrWhirl_HoldIsPierceOrHack()
        {
            var input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, Sword);
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.06, Sword));
            input.PrimaryPressed(1, Sword);
            Assert.AreEqual(Start(CombatAction.QuickAttack), input.PrimaryReleased(1.05, Sword));

            input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, TwoHander);
            input.Update(0.06, TwoHander);
            input.PrimaryPressed(1, TwoHander);
            Assert.AreEqual(Start(CombatAction.WhirlAttack), input.PrimaryReleased(1.05, TwoHander), "heavy right hand");

            input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, Sword);
            input.Update(0.06, Sword);
            input.PrimaryPressed(1, Sword);
            Assert.AreEqual(Start(CombatAction.PierceAttack), input.Update(1.11, Sword), "1H in the right hand");
            Assert.AreEqual(Start(CombatAction.QuickAttack), input.PrimaryReleased(1.7, Sword), "released within 0.7 s of the pierce");

            input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, TwoHander);
            input.Update(0.06, TwoHander);
            input.PrimaryPressed(1, TwoHander);
            Assert.AreEqual(Start(CombatAction.HackAttack), input.Update(1.11, TwoHander));
            Assert.AreEqual(Start(CombatAction.WhirlAttack), input.PrimaryReleased(1.3, TwoHander), "released within 0.2 s of the hack");
        }

        [Test]
        public void BlockHeldNearDownedEnemy_HoldIsFinishingAttack()
        {
            var stance = Sword;
            stance.FinisherInReach = true;
            var input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, stance);
            input.Update(0.06, stance);
            input.PrimaryPressed(1, stance);
            Assert.AreEqual(Start(CombatAction.FinishingAttack), input.Update(1.11, stance));
        }

        [Test]
        public void Secondary_HoldParades_QuickReleaseRipostes_ElseLowers()
        {
            var input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, Sword);
            Assert.AreEqual(MeleeCommand.None, input.Update(0.04, Sword));
            Assert.AreEqual(MeleeCommand.RaiseParade, input.Update(0.06, Sword));
            Assert.IsTrue(input.InParade);
            Assert.AreEqual(Start(CombatAction.QuickAttack), input.SecondaryReleased(0.15, Sword), "riposte within 200 ms");

            input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, Sword);
            input.Update(0.06, Sword);
            Assert.AreEqual(MeleeCommand.LowerParade, input.SecondaryReleased(1, Sword));
            Assert.IsFalse(input.InParade);

            var noBlock = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = false };
            input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, noBlock);
            Assert.AreEqual(Start(CombatAction.JumpBack), input.Update(0.06, noBlock), "cannot block → JumpBack");
        }

        [Test]
        public void Parade_CpAaiDropAfter2_5Seconds_IsOff_UnlessSetExplicitly()
        {
            // fix-68 [owner]: the game's timings never drop a held parade.
            foreach (var options in new[] { RuleOptions.Default, new RuleOptions(extendedAI: false), new RuleOptions(extendedAI: true) })
            {
                var game = new MeleeInputInterpreter(MeleeInputTiming.For(options));
                game.SecondaryPressed(0, Sword);
                Assert.AreEqual(MeleeCommand.RaiseParade, game.Update(0.06, Sword));
                Assert.AreEqual(MeleeCommand.None, game.Update(10, Sword));
                Assert.AreEqual(MeleeCommand.None, game.Update(120, Sword), "held for two minutes: still up");
                Assert.That(game.InParade, Is.True);
            }

            // The CP AAI mechanics, kept for reference.
            var cp = new MeleeInputTiming { ParadeAutoDrop = MeleeInputTiming.CpAaiParadeDrop };
            var input = new MeleeInputInterpreter(cp);
            input.SecondaryPressed(0, Sword);
            input.Update(0.06, Sword);
            Assert.AreEqual(MeleeCommand.None, input.Update(2.5, Sword));
            Assert.AreEqual(MeleeCommand.LowerParade, input.Update(2.6, Sword));
            Assert.AreEqual(MeleeCommand.None, input.Update(5, Sword), "stays down while the button is still held");

            var focusDown = Sword;
            focusDown.FocusDownOrDead = true;
            input = new MeleeInputInterpreter(cp);
            input.SecondaryPressed(0, focusDown);
            input.Update(0.06, focusDown);
            Assert.AreEqual(MeleeCommand.None, input.Update(10, focusDown));
        }

        [Test]
        public void TapDuringAMove_IsQueuedUntilTheMoveEnds()
        {
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            input.PrimaryReleased(0.05, Sword);
            input.PrimaryPressed(0.3, Sword);
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(0.35, Sword));
            Assert.AreEqual(Start(CombatAction.Attack), input.MoveEnded(0.8, Sword));
            Assert.AreEqual(MeleeCommand.None, input.MoveEnded(1.4, Sword));
            Assert.IsFalse(input.Busy);
        }

        [Test]
        public void KnockedIntoSitting_GetsUpWithAttackOrParade()
        {
            var sitting = Sword;
            sitting.SittingKnockedDown = true;
            var input = new MeleeInputInterpreter();
            Assert.AreEqual(Start(CombatAction.GetUpAttack), input.PrimaryPressed(0, sitting));
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(0.05, sitting));
            input.Reset();
            Assert.AreEqual(Start(CombatAction.GetUpParade), input.SecondaryPressed(0, sitting));
        }

        [Test]
        public void ParadeComesBackAfterAMove_WhileTheBlockIsHeld()
        {
            // G3 OnPlayerSecondaryAction queues the Parade once per press; once raised (_AI_Parade sets DAT_10118ab0) the
            // melee loop queues it again after every move while the button is held (FUN_10002130, fix-75).
            var input = new MeleeInputInterpreter();
            input.SecondaryPressed(0, Sword);
            input.Update(0.06, Sword);
            input.PrimaryPressed(1, Sword);
            input.PrimaryReleased(1.05, Sword);
            Assert.IsFalse(input.InParade);
            Assert.AreEqual(MeleeCommand.RaiseParade, input.MoveEnded(1.6, Sword));
            Assert.IsTrue(input.BlockHeld, "a next left tap is a quick attack again");
        }

        [Test]
        public void ParadeNeverRaisedThisPress_ComesAfterTheQueuedMove_WhileHeld()
        {
            // RMB held during an attack queues the Parade; a tap replaces it (newest wins) before it was ever raised.
            // Strict G3 (parade flag clear) would leave the block down; the owner's rule (fix-203): the held button raises it.
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, Sword);
            Assert.AreEqual(Start(CombatAction.Attack), input.PrimaryReleased(0.05, Sword));
            input.SecondaryPressed(0.1, Sword);
            input.Update(0.2, Sword);
            Assert.AreEqual(CombatAction.Parade, input.Queued);
            input.PrimaryPressed(0.25, Sword);
            input.PrimaryReleased(0.3, Sword);
            Assert.AreEqual(CombatAction.QuickAttack, input.Queued);
            Assert.AreEqual(Start(CombatAction.QuickAttack), input.MoveEnded(0.6, Sword));
            Assert.AreEqual(MeleeCommand.RaiseParade, input.MoveEnded(1.0, Sword));
            input.SecondaryReleased(1.2, Sword);
            Assert.AreEqual(MeleeCommand.None, input.Update(1.3, Sword), "released: nothing");
        }
    }
}
