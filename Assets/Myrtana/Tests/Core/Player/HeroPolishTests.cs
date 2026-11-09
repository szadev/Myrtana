using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Camera modes, walking while blocking and fall damage of the hero (G3 CP 1.75; Docs/hero.md).</summary>
    public class HeroPolishTests
    {
        // ---------------------------------------------------------------- acrobatic roll (polish-3)

        [Test]
        public void AcrobaticRollTravels430cmEvenly()
        {
            float total = 0, last = 0;
            for (int i = 1; i <= 10; i++)
            {
                float p = i / 10f;
                total += FallRules.RollStep(last, p);
                last = p;
            }
            Assert.That(total, Is.EqualTo(4.3f).Within(1e-4f));
            Assert.That(FallRules.RollStep(0.5f, 0.4f), Is.EqualTo(0f), "never backwards");
            Assert.That(FallRules.RollStep(0.9f, 1.3f), Is.EqualTo(0.43f).Within(1e-4f), "clamped at the end");
        }

        // ---------------------------------------------------------------- camera (PC_Camera gCCameraAI_PS)

        [Test]
        public void EveryWeaponStateUsesCamModeRanged()
        {
            Assert.That(CameraModes.OfPlayerState(false), Is.EqualTo(CameraMode.Ranged));
            Assert.That(CameraModes.OfPlayerState(true), Is.EqualTo(CameraMode.Inventory));
            var r = CameraModes.Params(CameraMode.Ranged);
            Assert.That(r.BestRange, Is.EqualTo(3f));
            Assert.That(r.MinRange, Is.EqualTo(2f));
            Assert.That(r.MaxRange, Is.EqualTo(15f));
            Assert.That(r.MinRangeOnMove, Is.EqualTo(3f));
        }

        [Test]
        public void CameraFovFollowsTheZoom()
        {
            var r = CameraModes.Ranged;
            Assert.That(r.Fov(2f), Is.EqualTo(60f));
            Assert.That(r.Fov(15f), Is.EqualTo(65f));
            Assert.That(r.Fov(3f), Is.EqualTo(60f + 5f / 13f).Within(1e-4f));
            Assert.That(r.Fov(0.5f), Is.EqualTo(60f), "clamped below MinRange");
            Assert.That(CameraModes.Inventory.Fov(5f), Is.EqualTo(45f));
        }

        [Test]
        public void CameraKeepsMinRangeOnMoveWhileMoving()
        {
            var r = CameraModes.Ranged;
            Assert.That(r.WishedDistance(2.2f, moving: false), Is.EqualTo(2.2f));
            Assert.That(r.WishedDistance(2.2f, moving: true), Is.EqualTo(3f));
            Assert.That(r.WishedDistance(6f, moving: true), Is.EqualTo(6f));
            Assert.That(r.ClampZoom(1f), Is.EqualTo(2f));
            Assert.That(r.ClampZoom(40f), Is.EqualTo(15f));
        }

        // ---------------------------------------------------------------- parade walk

        [Test]
        public void ParadeWalkUsesTheParadeMotionSpeeds()
        {
            Assert.That(ParadeMovement.Wish(0, 1, run: true), Is.EqualTo((0f, 3.5f)));
            Assert.That(ParadeMovement.Wish(0, -1, run: true), Is.EqualTo((0f, -3.5f)));
            Assert.That(ParadeMovement.Wish(1, 0, run: true), Is.EqualTo((4f, 0f)));
            Assert.That(ParadeMovement.Wish(-1, 0, run: false), Is.EqualTo((-2f, 0f)));
        }

        [Test]
        public void ShieldLocomotionIsPoseP1()
        {
            // Hero_Parade_Shield_1H_P1_Move_Walk exists, P0 does not (the block fell back to the standing walk);
            // standing P0 is the 1H fencing pose (shield flat across the back), every shield walk/run ends in P1.
            Assert.That(StancePose.For("Parade", "Shield", "P0"), Is.EqualTo("P1"));
            Assert.That(StancePose.For("Stand", "Shield", "P0"), Is.EqualTo("P1"));
            Assert.That(StancePose.For("Parade", "None", "P0"), Is.EqualTo("P0"));
            Assert.That(StancePose.For("Stand", "Torch", "P0"), Is.EqualTo("P0"));
            Assert.That(StancePose.For("Swim", "Shield", "P0"), Is.EqualTo("P0"));
        }

        // ---------------------------------------------------------------- fall damage (OnMovementModeChanged)

        [Test]
        public void FallDamageFormula()
        {
            Assert.That(FallRules.Damage(0.5f), Is.EqualTo(0));
            Assert.That(FallRules.Damage(0.9f), Is.EqualTo(0));
            Assert.That(FallRules.Damage(0.97f), Is.EqualTo(0), "floor(160·0.07²) = floor(0.78)");
            Assert.That(FallRules.Damage(1.4f), Is.EqualTo(40), "160·0.25");
            Assert.That(FallRules.Damage(1.9f), Is.EqualTo(160));
            Assert.That(FallRules.Damage(2.9f), Is.EqualTo(640));
            Assert.That(FallRules.Damage(FallRules.SecondsFor(40)), Is.EqualTo(40));
        }

        [Test]
        public void NoDamageInWater()
        {
            Assert.That(FallRules.Land(3f, WaterMode.Wade, false, false, true, 100).Damage, Is.EqualTo(0));
            Assert.That(FallRules.Land(3f, WaterMode.Swim, false, false, true, 100).Damage, Is.EqualTo(0));
            var shallow = FallRules.Land(1.4f, WaterMode.Shallow, false, false, true, 100);
            Assert.That(shallow.Damage, Is.EqualTo(40), "wet feet are not water");
        }

        [Test]
        public void HeavyLandingAndDeath()
        {
            var soft = FallRules.Land(0.8f, WaterMode.None, false, false, true, 100);
            Assert.That(soft.Kind, Is.EqualTo(LandingKind.Soft));
            Assert.That(soft.Damage, Is.EqualTo(0));

            var heavy = FallRules.Land(1.4f, WaterMode.None, false, false, true, 100);
            Assert.That(heavy.Kind, Is.EqualTo(LandingKind.Heavy));
            Assert.That(heavy.PainCry, Is.True);
            Assert.That(heavy.Killed, Is.False);

            var dead = FallRules.Land(1.9f, WaterMode.None, false, false, true, 160);
            Assert.That(dead.Killed, Is.True, "HP 160 − 160 < 1");
            Assert.That(FallRules.Land(1.9f, WaterMode.None, false, false, true, 161).Killed, Is.False);
            Assert.That(FallRules.Land(1.9f, WaterMode.None, false, false, true, 100, canBeKilled: false).Killed, Is.False);
            Assert.That(FallRules.Land(5f, WaterMode.None, false, false, true, 100, immune: true).Damage, Is.EqualTo(0));
        }

        [Test]
        public void AcrobaticHalvesAndRolls()
        {
            var roll = FallRules.Land(1.4f, WaterMode.None, acrobatic: true, knockedOut: false, alternativeBalancing: true, hitPoints: 100);
            Assert.That(roll.Damage, Is.EqualTo(20));
            Assert.That(roll.Kind, Is.EqualTo(LandingKind.Acrobatic));
            Assert.That(roll.PainCry, Is.False);
            // AB on: the halved fall can still kill.
            Assert.That(FallRules.Land(2.9f, WaterMode.None, true, false, true, 100).Killed, Is.True);
            // AB off: with the perk the fall never kills.
            Assert.That(FallRules.Land(2.9f, WaterMode.None, true, false, false, 100).Killed, Is.False);
        }

        [Test]
        public void KnockedOutKeepsStateAndTakesFullDamage()
        {
            var ko = FallRules.Land(1.4f, WaterMode.None, acrobatic: true, knockedOut: true, alternativeBalancing: true, hitPoints: 100);
            Assert.That(ko.Damage, Is.EqualTo(40));
            Assert.That(ko.Kind, Is.EqualTo(LandingKind.KeepState));
        }

        [Test]
        public void DefaultLandingMotion()
        {
            Assert.That(FallRules.DefaultKind(0.95f), Is.EqualTo(LandingKind.Soft));
            Assert.That(FallRules.DefaultKind(1.0f), Is.EqualTo(LandingKind.Heavy));
        }
    }
}
