using G3.Player;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-39: the ways out of sitting, driven from physical presses through the default key map (G3Input reads the same
    /// KeyMap) into <see cref="HeroSitInput"/> and the real <see cref="MeleeInputInterpreter"/>, as PlayerCombat.ReadKeys
    /// and HeroObjectUse.ExitPressed do.
    /// </summary>
    public class HeroSitInputTests
    {
        static readonly KeyInput Lmb = KeyInput.Mouse(MouseOffset.Left);
        static readonly KeyInput Rmb = KeyInput.Mouse(MouseOffset.Right);
        static readonly KeyInput W = KeyInput.Key(Dik.W);

        static readonly MeleeStance Sitting = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true, SittingKnockedDown = true };
        static readonly MeleeStance Standing = new MeleeStance { RightHand = WeaponClass.OneHanded, CanParade = true };

        /// <summary>The press edges of a frame as G3Input.WasPressed(SessionKey) sees them with the default bindings.</summary>
        static HeroSitPresses Frame(params KeyInput[] pressed)
        {
            var map = KeyMap.Default();
            var p = new HeroSitPresses();
            foreach (var input in pressed)
            {
                switch (map.Owner(input, out _))
                {
                    case SessionKey.Use1: p.Use1 = true; break;
                    case SessionKey.Use2: p.Use2 = true; break;
                    case SessionKey.Forward: case SessionKey.Backward: case SessionKey.StrafeLeft: case SessionKey.StrafeRight: p.Move = true; break;
                    case SessionKey.Up: p.Jump = true; break;
                }
            }
            return p;
        }

        static readonly HeroSitState KnockedDown = new HeroSitState { SittingKnockedDown = true, CursorLocked = true };

        /// <summary>PlayerCombat.ReadKeys while sitting: the decision, then the interpreter's press (PrimaryDown/SecondaryDown).</summary>
        static MeleeCommand Press(MeleeInputInterpreter input, double t, params KeyInput[] pressed)
        {
            switch (HeroSitInput.Decide(KnockedDown, Frame(pressed)))
            {
                case HeroSitExit.GetUpAttack: return input.PrimaryPressed(t, Sitting);
                case HeroSitExit.GetUpParade: return input.SecondaryPressed(t, Sitting);
                default: return MeleeCommand.None;
            }
        }

        [Test]
        public void DefaultBindings_UseKeysAreTheMouseButtons()
        {
            var map = KeyMap.Default();
            Assert.AreEqual(SessionKey.Use1, map.Owner(Lmb, out _));
            Assert.AreEqual(SessionKey.Use2, map.Owner(Rmb, out _));
            Assert.AreEqual(SessionKey.Forward, map.Owner(W, out _));
        }

        [Test]
        public void Sitting_Lmb_GetUpAttack()
        {
            var input = new MeleeInputInterpreter();
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpAttack), Press(input, 1.0, Lmb));
            Assert.AreEqual(MeleeCommand.None, input.PrimaryReleased(1.05, Sitting), "the release is no tap attack");
        }

        [Test]
        public void Sitting_Rmb_GetUpParade()
        {
            var input = new MeleeInputInterpreter();
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpParade), Press(input, 1.0, Rmb));
            Assert.AreEqual(MeleeCommand.None, input.Update(1.3, Sitting), "no parade raise while sitting");
            Assert.AreEqual(MeleeCommand.None, input.SecondaryReleased(1.4, Sitting), "the release is no riposte");
        }

        [Test]
        public void Sitting_AfterAStaleMoveAndHeldButtons_PressStillGetsUp()
        {
            // The hit came during a swing with LMB held: the interpreter is busy and has a queued move (the fall's
            // ResetAll may come late); the press edge must still start the get-up, not queue behind the stale move.
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0.0, Standing);
            input.Update(0.2, Standing); // hold -> a power attack started
            input.SecondaryPressed(0.25, Standing);
            input.Update(0.35, Standing); // parade queued behind it
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpAttack), Press(input, 1.0, Lmb));
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpParade), Press(input, 1.5, Rmb));
        }

        [Test]
        public void Sitting_MovementOrACursorPage_DoesNotGetUp()
        {
            Assert.AreEqual(HeroSitExit.None, HeroSitInput.Decide(KnockedDown, Frame(W)), "G3: only a press edge of Use1/Use2");
            var page = new HeroSitState { SittingKnockedDown = true, CursorLocked = false };
            Assert.AreEqual(HeroSitExit.None, HeroSitInput.Decide(page, Frame(Lmb)), "a click on a page belongs to the page");
            Assert.AreEqual(HeroSitExit.GetUpAttack, HeroSitInput.Decide(KnockedDown, Frame(Lmb, Rmb)), "both: the primary wins");
        }

        [Test]
        public void KnockedOut_NoPressHelps()
        {
            var ko = new HeroSitState { KnockedOut = true, SittingKnockedDown = true, Seated = true, CursorLocked = true };
            Assert.AreEqual(HeroSitExit.None, HeroSitInput.Decide(ko, Frame(Lmb, Rmb, W)));
        }

        [Test]
        public void Seat_W_StandsUp()
        {
            var seat = new HeroSitState { Seated = true };
            Assert.AreEqual(HeroSitExit.LeaveSeat, HeroSitInput.Decide(seat, Frame(W)));
            Assert.AreEqual(HeroSitExit.LeaveSeat, HeroSitInput.Decide(seat, Frame(Lmb)), "Use1 ends the seat");
            Assert.AreEqual(HeroSitExit.LeaveSeat, HeroSitInput.Decide(seat, Frame(KeyInput.Key(Dik.LeftAlt))), "jump (G3 Up: Left Alt)");
        }

        [Test]
        public void Seat_NothingPressed_StaysSeated()
        {
            var seat = new HeroSitState { Seated = true, CursorLocked = false }; // the seat use holds the control lock: cursor free
            Assert.AreEqual(HeroSitExit.None, HeroSitInput.Decide(seat, Frame()));
            Assert.AreEqual(HeroSitExit.LeaveSeat, HeroSitInput.Decide(seat, Frame(Rmb)), "Use2 ends the seat (G3 action 0x66, fix-75)");
        }

        [Test]
        public void SeatWhileHit_LeavesTheSeat_ThenThePressGetsUp()
        {
            // A heavy hit on the bench: the combat body falls into the sitting knock-down (Disturbed). The seat ends by
            // itself, without a key; afterwards the knocked-down sit answers the presses as usual.
            var hit = new HeroSitState { Seated = true, SittingKnockedDown = true, Disturbed = true };
            Assert.AreEqual(HeroSitExit.LeaveSeat, HeroSitInput.Decide(hit, Frame()));
            var input = new MeleeInputInterpreter();
            input.Reset(); // SatDown (G3 ResetAll)
            Assert.AreEqual(MeleeCommand.Start(CombatAction.GetUpAttack), Press(input, 2.0, Lmb));
        }
    }
}
