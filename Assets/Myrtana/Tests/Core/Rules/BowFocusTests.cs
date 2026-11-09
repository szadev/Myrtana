using System;
using System.Collections.Generic;
using System.Numerics;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-90 (owner: "aiming feels strange": the focus name sat on a scavenger 30° left of the crosshair; a boar under the
    /// crosshair was never focused, a bandit beside it was). G3 PS_Ranged focus: camera heading, from the hero, ±45°, no
    /// range rating; the hero's CurrentTarget is not updated by the ranged states; no camera shoulder offset; armed → no
    /// item/object focus.
    /// </summary>
    public class BowFocusTests
    {
        static FocusCandidate C(float azi, float distanceM, int priority = CombatFocusPriority.Low) => new FocusCandidate
        {
            AzimuthDeg = azi, ElevationDeg = 0, DistanceCm = distanceM * 100f, HalfExtentCm = 40f, FreeLineOfSight = true, Priority = priority,
        };

        [Test]
        public void Ranged_EqualPriority_NearestTheCrosshairWins_NotTheNearestFoe()
        {
            // a scavenger 30° left at 6 m, another 2° right at 20 m (under the crosshair)
            var list = new List<FocusCandidate> { C(-30, 6), C(2, 20) };
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Ranged, false, rangeRating: false, reachCm: 150), Is.EqualTo(1));
            // reversed walk order: same result
            list.Reverse();
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Ranged, false, false, 150), Is.EqualTo(0));
        }

        [Test]
        public void Ranged_ScanIs35mAnd45Degrees()
        {
            Assert.That(CombatFocus.Pick(new[] { C(50, 5) }, FocusScanMode.Ranged, false, false, 150), Is.EqualTo(-1));
            Assert.That(CombatFocus.Pick(new[] { C(44, 34) }, FocusScanMode.Ranged, false, false, 150), Is.EqualTo(0));
            Assert.That(CombatFocus.Pick(new[] { C(0, 36) }, FocusScanMode.Ranged, false, false, 150), Is.EqualTo(-1));
        }

        [Test]
        public void HeroTarget_OnlyMeleeAndMagicLoopsSetIt()
        {
            Assert.That(CombatFocus.SetsHeroTarget(FocusScanMode.Ranged), Is.False);
            Assert.That(CombatFocus.SetsHeroTarget(FocusScanMode.Melee), Is.True);
            Assert.That(CombatFocus.SetsHeroTarget(FocusScanMode.Magic), Is.True);
        }

        [Test]
        public void Boar_FightingTheFocusedBandit_StaysFocusableWithABow()
        {
            // bandit: hostile, not fighting -> 1. Boar: fights the bandit. With a bow the hero's target is not the focus,
            // so the boar is not the bandit's "ally" (TargetsHerosTarget false) -> Hostile fighting -> 2 and wins.
            var bandit = new FocusFacts { IsNpc = true, State = VitalState.Alive, AttitudeToHero = Attitude.Hostile };
            var boar = new FocusFacts { IsNpc = true, State = VitalState.Alive, AttitudeToHero = Attitude.Hostile, InCombat = true };
            Assert.That(CombatFocusPriority.Cast(bandit), Is.EqualTo(CombatFocusPriority.Low));
            Assert.That(CombatFocusPriority.Cast(boar), Is.EqualTo(CombatFocusPriority.Medium));
            var list = new List<FocusCandidate> { C(-8, 12, CombatFocusPriority.Cast(bandit)), C(1, 8, CombatFocusPriority.Cast(boar)) };
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Ranged, false, false, 150), Is.EqualTo(1));
            // the old self-lock (the focus counted as the hero's target): the boar dropped out
            boar.TargetsHerosTarget = true;
            Assert.That(CombatFocusPriority.Cast(boar), Is.EqualTo(CombatFocusPriority.Unfocusable));
        }

        [Test]
        public void Boar_AndBandit_BothIdle_TheOneUnderTheCrosshairWins()
        {
            var list = new List<FocusCandidate> { C(-8, 12), C(1, 8) };
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Ranged, false, false, 150), Is.EqualTo(1));
        }

        [Test]
        public void Armed_NoItemOrObjectFocus()
        {
            Assert.That(CombatFocus.InteractionFocus(weaponDrawn: true), Is.False);
            Assert.That(CombatFocus.InteractionFocus(weaponDrawn: false), Is.True);
        }

        [Test]
        public void Camera_G3HasNoShoulderOffset()
        {
            Assert.That(CameraModes.Shoulder(g3Modes: true, rigShoulder: 0.35f), Is.EqualTo(0f));
            Assert.That(CameraModes.Shoulder(false, 0.35f), Is.EqualTo(0.35f));
        }

        /// <summary>
        /// Reticle/focus agreement: with G3's camera (no shoulder) the target the crosshair is put on lies straight ahead
        /// of the hero along the camera heading — focus azimuth 0 — at every distance and bearing.
        /// </summary>
        [TestCase(4f, 30f), TestCase(12f, -50f), TestCase(30f, 170f)]
        public void Crosshair_OnTarget_MeansFocusAzimuthZero(float distance, float bearing)
        {
            var pivot = new Vector3(0, 1.7f, 0);
            float b = bearing * (float)Math.PI / 180f;
            var target = new Vector3(distance * (float)Math.Sin(b), 0.6f, distance * (float)Math.Cos(b));
            float boom = CameraModes.Ranged.BestRange;
            bool ok = ThirdPersonAim.Solve(pivot, target, CameraModes.ShoulderOffset, boom, CameraModes.Ranged.TargetOffsetY(boom),
                                           -60f, 70f, out float yaw, out _);
            Assert.That(ok, Is.True);
            float azimuth = CombatFocus.DeltaAngle(yaw, (float)(Math.Atan2(target.X, target.Z) * 180 / Math.PI));
            Assert.That(Math.Abs(azimuth), Is.LessThan(0.01f));
            // with the old 0.35 m shoulder the same aim was off by asin(0.35 / r)
            ThirdPersonAim.Solve(pivot, target, 0.35f, boom, CameraModes.Ranged.TargetOffsetY(boom), -60f, 70f, out float yawOld, out _);
            float off = Math.Abs(CombatFocus.DeltaAngle(yawOld, (float)(Math.Atan2(target.X, target.Z) * 180 / Math.PI)));
            Assert.That(off, Is.EqualTo(Math.Asin(0.35 / Math.Sqrt(target.X * target.X + target.Z * target.Z)) * 180 / Math.PI).Within(0.05));
        }

        [Test]
        public void AimCamera_WishedDistance_NeverBelowG3MinRange()
        {
            var m = CameraModes.Ranged;
            Assert.That(m.WishedDistance(m.ClampZoom(0.5f), moving: false), Is.EqualTo(2f));
            Assert.That(m.WishedDistance(m.ClampZoom(0.5f), moving: true), Is.EqualTo(3f));
            Assert.That(CameraModes.Inventory.BestRange, Is.EqualTo(1.5f), "PS_HUD / PS_Interact (loot): CamModeInventory 150 cm");
            Assert.That(CameraModes.Inventory.Fov(1.5f), Is.EqualTo(45f));
        }
    }
}
