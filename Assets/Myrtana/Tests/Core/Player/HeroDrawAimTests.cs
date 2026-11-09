using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-42: G3's draw/sheathe motion pair around HoldStacks (<see cref="HoldMotionRules"/>) and the hero's facing and
    /// gait per player state (<see cref="HeroMoveRules"/>: the body faces the camera, aiming walks).
    /// </summary>
    public class HeroDrawAimTests
    {
        // ------------------------------------------------------------------ draw / sheathe

        [Test]
        public void Draw_ReachesBack_ThenTheHandChanges_ThenBringsTheWeaponForward()
        {
            var parts = HoldMotionRules.Parts(draw: true);
            Assert.AreEqual(2, parts.Length);
            Assert.AreEqual(HoldPhase.Begin, parts[0].Phase);
            Assert.IsFalse(parts[0].Reverse);
            Assert.IsTrue(parts[0].SwitchAfter, "the weapon is in the hand after the Begin motion (HoldStacks)");
            Assert.AreEqual(HoldPhase.End, parts[1].Phase);
            Assert.IsFalse(parts[1].Reverse);
            Assert.IsFalse(parts[1].SwitchAfter);
        }

        [Test]
        public void Sheathe_PlaysTheDrawPairBackwards_TheHandChangesBetween()
        {
            var parts = HoldMotionRules.Parts(draw: false);
            Assert.AreEqual(HoldPhase.End, parts[0].Phase);
            Assert.IsTrue(parts[0].Reverse);
            Assert.IsTrue(parts[0].SwitchAfter, "the weapon goes onto its carry slot after the reversed End motion");
            Assert.AreEqual(HoldPhase.Begin, parts[1].Phase);
            Assert.IsTrue(parts[1].Reverse);
            Assert.AreEqual("End", parts[0].Word);
            Assert.AreEqual("Begin", parts[1].Word);
        }

        [TestCase(0.2f, 0.56f, 0.2632f)] // hero 1H: HoldRight_Begin 0.20 s, HoldRight_End 0.56 s
        [TestCase(0.4f, 0.36f, 0.5263f)] // bow: HoldLeft_Begin 0.40 s, HoldLeft_End 0.36 s
        [TestCase(0f, 0.5f, 0f)]          // no first motion: the hand changes at once
        [TestCase(0.4f, 0f, 1f)]          // no second motion: at the end
        [TestCase(0f, 0f, 1f)]
        public void SlotSwitch_HappensAfterTheFirstMotion(float first, float second, float expected)
        {
            Assert.AreEqual(expected, HoldMotionRules.SwitchFraction(first, second), 1e-3);
        }

        [Test]
        public void Variant_Overlay_OnlyWhileMoving()
        {
            Assert.IsFalse(HoldMotionRules.UseOverlay(0f), "standing: the full-body _N_ motion");
            Assert.IsFalse(HoldMotionRules.UseOverlay(0.01f), "collision jitter is no movement");
            Assert.IsTrue(HoldMotionRules.UseOverlay(1.5f), "walking: the _O_ overlay, the legs keep walking");
        }

        [Test]
        public void OverlayFades_TheOpeningMotionFadesIn_TheClosingOneFadesOut()
        {
            // sAIPlayAniStart for an overlay: Begin forwards / End backwards intro 0.1, End forwards / Begin backwards extro 0.1.
            Assert.AreEqual(0.1f, HoldMotionRules.OverlayIntro(HoldPhase.Begin, false), 1e-6);
            Assert.AreEqual(0f, HoldMotionRules.OverlayExtro(HoldPhase.Begin, false), 1e-6);
            Assert.AreEqual(0f, HoldMotionRules.OverlayIntro(HoldPhase.End, false), 1e-6);
            Assert.AreEqual(0.1f, HoldMotionRules.OverlayExtro(HoldPhase.End, false), 1e-6);
            Assert.AreEqual(0.1f, HoldMotionRules.OverlayIntro(HoldPhase.End, true), 1e-6);
            Assert.AreEqual(0f, HoldMotionRules.OverlayExtro(HoldPhase.End, true), 1e-6);
            Assert.AreEqual(0f, HoldMotionRules.OverlayIntro(HoldPhase.Begin, true), 1e-6);
            Assert.AreEqual(0.1f, HoldMotionRules.OverlayExtro(HoldPhase.Begin, true), 1e-6);
        }

        // ------------------------------------------------------------------ facing

        [TestCase(HeroMoveState.Normal, HeroFacing.Camera)]
        [TestCase(HeroMoveState.Ranged, HeroFacing.Camera)]
        [TestCase(HeroMoveState.RangedAim, HeroFacing.Camera)]
        [TestCase(HeroMoveState.RangedSneak, HeroFacing.Camera)]
        [TestCase(HeroMoveState.MeleeSneak, HeroFacing.Camera)]
        [TestCase(HeroMoveState.Melee, HeroFacing.AlignTarget)]
        [TestCase(HeroMoveState.MeleeParade, HeroFacing.AlignTarget)]
        [TestCase(HeroMoveState.MeleeMove, HeroFacing.AlignTarget)]
        [TestCase(HeroMoveState.Magic, HeroFacing.AlignTarget)]
        [TestCase(HeroMoveState.MagicCast, HeroFacing.AlignTarget)]
        public void Facing_WithAnAlignment(HeroMoveState state, HeroFacing expected)
        {
            Assert.AreEqual(expected, HeroMoveRules.Facing(state, aligned: true));
        }

        [Test]
        public void Facing_WithoutAnAlignment_IsAlwaysTheCamera()
        {
            foreach (HeroMoveState state in System.Enum.GetValues(typeof(HeroMoveState)))
                Assert.AreEqual(HeroFacing.Camera, HeroMoveRules.Facing(state, aligned: false), state.ToString());
        }

        [Test]
        public void AimingBackLeft_KeepsFacingTheCamera_AndStrafesBackAndLeft()
        {
            // Owner report: bow drawn, back + left → G3 keeps the bow straight and steps; the body never turns to the keys.
            Assert.AreEqual(30f, HeroMoveRules.BodyYaw(HeroMoveState.RangedAim, 30f, alignYaw: 120f), 1e-6);
            var (along, alongShare, side, sideShare) = HeroMoveRules.Motions(-1f, -1f);
            Assert.AreEqual("Back", along);
            Assert.AreEqual("Left", side);
            Assert.AreEqual(0.5f, alongShare, 1e-6);
            Assert.AreEqual(0.5f, sideShare, 1e-6);
        }

        [TestCase(0f, 1f, "Fwd", null)]
        [TestCase(0f, -1f, "Back", null)]
        [TestCase(-1f, 0f, null, "Left")]
        [TestCase(1f, 0f, null, "Right")]
        [TestCase(0.7f, 0.7f, "Fwd", "Right")]
        public void Motions_PerKeyDirection(float x, float y, string along, string side)
        {
            var m = HeroMoveRules.Motions(x, y);
            Assert.AreEqual(along, m.along);
            Assert.AreEqual(side, m.side);
            Assert.AreEqual(1f, m.alongShare + m.sideShare, 1e-6);
        }

        [Test]
        public void Motions_NoKeys_None()
        {
            var m = HeroMoveRules.Motions(0f, 0f);
            Assert.IsNull(m.along);
            Assert.IsNull(m.side);
        }

        // ------------------------------------------------------------------ gait

        [TestCase(HeroGait.Run, HeroGait.Walk)]
        [TestCase(HeroGait.Sprint, HeroGait.Walk)]
        [TestCase(HeroGait.Walk, HeroGait.Walk)]
        [TestCase(HeroGait.Sneak, HeroGait.Sneak)]
        public void Aiming_Walks(HeroGait wanted, HeroGait expected)
        {
            Assert.IsTrue(HeroMoveRules.ForcesWalk(HeroMoveState.RangedAim));
            Assert.AreEqual(expected, HeroMoveRules.Gait(HeroMoveState.RangedAim, wanted));
        }

        [TestCase(HeroMoveState.Ranged)]
        [TestCase(HeroMoveState.Normal)]
        [TestCase(HeroMoveState.Melee)]
        [TestCase(HeroMoveState.Magic)]
        [TestCase(HeroMoveState.MagicCast)]
        public void OtherStates_KeepTheWantedGait(HeroMoveState state)
        {
            Assert.IsFalse(HeroMoveRules.ForcesWalk(state));
            Assert.AreEqual(HeroGait.Run, HeroMoveRules.Gait(state, HeroGait.Run));
            Assert.AreEqual(HeroGait.Sprint, HeroMoveRules.Gait(state, HeroGait.Sprint));
        }
    }
}
