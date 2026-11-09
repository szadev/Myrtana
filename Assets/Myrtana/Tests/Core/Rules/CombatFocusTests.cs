using System.Collections.Generic;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The hero's combat focus and alignment as in G3 (Docs/combat.md §9.2).</summary>
    public class CombatFocusTests
    {
        static FocusCandidate C(float azi, float distCm, float halfCm = 40, bool los = true, float elev = 0, int priority = 0) =>
            new FocusCandidate { AzimuthDeg = azi, DistanceCm = distCm, HalfExtentCm = halfCm, FreeLineOfSight = los, ElevationDeg = elev, Priority = priority };

        [Test]
        public void Reference_FirstPersonCamera_BlockHero_ElseKeys()
        {
            Assert.That(CombatFocus.MeleeReference(firstPerson: true, blockHeld: true), Is.EqualTo(FocusReference.Camera));
            Assert.That(CombatFocus.MeleeReference(false, true), Is.EqualTo(FocusReference.Hero));
            Assert.That(CombatFocus.MeleeReference(false, false), Is.EqualTo(FocusReference.Keys));
        }

        [Test]
        public void ReferenceYaw_KeysInCameraFrame_ElseHeroFacing()
        {
            Assert.That(CombatFocus.ReferenceYaw(FocusReference.Keys, 10, 70, 0, 0), Is.EqualTo(70), "no key: hero facing");
            Assert.That(CombatFocus.ReferenceYaw(FocusReference.Keys, 10, 70, 1, 0), Is.EqualTo(100).Within(1e-3), "right");
            Assert.That(CombatFocus.ReferenceYaw(FocusReference.Keys, 10, 70, 0, -1), Is.EqualTo(190).Within(1e-3), "back");
            Assert.That(CombatFocus.ReferenceYaw(FocusReference.Camera, 10, 70, 1, 0), Is.EqualTo(10));
            Assert.That(CombatFocus.ReferenceYaw(FocusReference.Hero, 10, 70, 1, 0), Is.EqualTo(70));
        }

        [Test]
        public void DeltaAngle_Wraps()
        {
            Assert.That(CombatFocus.DeltaAngle(350, 10), Is.EqualTo(20).Within(1e-4));
            Assert.That(CombatFocus.DeltaAngle(10, 350), Is.EqualTo(-20).Within(1e-4));
            Assert.That(CombatFocus.DeltaAngle(0, 180), Is.EqualTo(180).Within(1e-4));
        }

        [Test]
        public void Scan_Melee5mLessTheTargetsSize_And90DegreesEachSide()
        {
            Assert.That(CombatFocus.InScan(C(0, 505, halfCm: 100), FocusScanMode.Melee, false), Is.True, "d² − h² ≤ 500²");
            Assert.That(CombatFocus.InScan(C(0, 515, halfCm: 100), FocusScanMode.Melee, false), Is.False);
            Assert.That(CombatFocus.InScan(C(90, 200), FocusScanMode.Melee, false), Is.True);
            Assert.That(CombatFocus.InScan(C(-91, 200), FocusScanMode.Melee, false), Is.False, "behind the side");
            Assert.That(CombatFocus.InScan(C(0, 200, los: false), FocusScanMode.Melee, false), Is.False, "line of sight");
        }

        [Test]
        public void Scan_HardFocusIsNarrow_MagicIsLongAndNarrower()
        {
            Assert.That(CombatFocus.InScan(C(9, 300), FocusScanMode.Melee, hard: true), Is.True);
            Assert.That(CombatFocus.InScan(C(11, 300), FocusScanMode.Melee, hard: true), Is.False);
            Assert.That(CombatFocus.InScan(C(0, 300, elev: 25), FocusScanMode.Melee, hard: true), Is.False);
            Assert.That(CombatFocus.InScan(C(40, 3000), FocusScanMode.Magic, false), Is.True);
            Assert.That(CombatFocus.InScan(C(50, 1000), FocusScanMode.Magic, false), Is.False);
        }

        [Test]
        public void Pick_SmallestAzimuth_ThenRangeRatingPrefersANearChainMember()
        {
            // Walk order: the near one aside first, then the straight ones (each taken over in turn: a chain).
            var list = new List<FocusCandidate> { C(-60, 120), C(30, 450), C(5, 480) };
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Melee, false, rangeRating: false, reachCm: 150), Is.EqualTo(2));
            // reach 150: far/straight 8·480/150 + 2·5/90 = 25.7; near/aside 8·120/150 + 2·60/90 = 7.7 → the near one.
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Melee, false, rangeRating: true, reachCm: 150), Is.EqualTo(0));
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Melee, hard: true, rangeRating: true, reachCm: 150), Is.EqualTo(2),
                "hard focus: no range rating");
            Assert.That(CombatFocus.Pick(new List<FocusCandidate> { C(120, 100) }, FocusScanMode.Melee, false, true, 150), Is.EqualTo(-1));
        }

        [Test]
        public void Pick_RangeRating_OnlyFallsBackOnANearOneThatWasBestDuringTheWalk()
        {
            // G3 FindFocusEntity (fix-68): the near one aside comes after the straight one, so it never was the best of the
            // walk and the hero turned towards the farther enemy keeps that one.
            var list = new List<FocusCandidate> { C(5, 480), C(-60, 120) };
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Melee, false, rangeRating: true, reachCm: 150), Is.EqualTo(0));
        }

        [Test]
        public void Pick_PriorityBeforeAngle_MinusOneNeverFocused()
        {
            var list = new List<FocusCandidate> { C(0, 200, priority: CombatFocusPriority.Unfocusable), C(40, 300, priority: 1) };
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Melee, false, false, 150), Is.EqualTo(1), "-1 is skipped even dead ahead");
            list = new List<FocusCandidate> { C(5, 300, priority: 1), C(60, 300, priority: 3), C(2, 300, priority: 1) };
            Assert.That(CombatFocus.Pick(list, FocusScanMode.Melee, false, false, 150), Is.EqualTo(1), "a foe (3) before a bystander (1)");
            Assert.That(CombatFocus.Pick(new List<FocusCandidate> { C(0, 100, priority: -1) }, FocusScanMode.Melee, false, true, 150), Is.EqualTo(-1));
        }

        static FocusFacts Npc(Attitude attitude, bool combat = false, bool targetsHero = false, bool targetsHerosTarget = false,
                              bool party = false, VitalState state = VitalState.Alive, bool parade = false) =>
            new FocusFacts { IsNpc = true, State = state, AttitudeToHero = attitude, InCombat = combat, TargetsHero = targetsHero,
                             TargetsHerosTarget = targetsHerosTarget, HerosParty = party, HeroInParade = parade };

        [Test]
        public void Priority_Melee_CompanionsFriendsAndAlliesAreNeverFocused()
        {
            // CP GetAttitudeToPlayer: a party member is Friendly to the hero.
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Friendly, combat: true, targetsHerosTarget: true, party: true)), Is.EqualTo(-1), "companion fighting the foe");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Friendly, party: true)), Is.EqualTo(-1), "companion standing by");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Friendly)), Is.EqualTo(-1), "a friend");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Friendly, combat: true, targetsHero: true)), Is.EqualTo(3), "a friend that attacks the hero");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Neutral, combat: true, targetsHerosTarget: true)), Is.EqualTo(-1), "an ally fighting the foe");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Neutral, combat: true, party: true)), Is.EqualTo(-1), "the party");
        }

        [Test]
        public void Priority_Melee_FoesFirst_BystandersLow_DownedOnlyWhileBlocking_DeadNever()
        {
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Hostile, combat: true, targetsHero: true)), Is.EqualTo(3));
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Hostile, combat: true)), Is.EqualTo(3));
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Angry, combat: true)), Is.EqualTo(2));
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Neutral, combat: true)), Is.EqualTo(1));
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Neutral)), Is.EqualTo(1), "not fighting: low");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Hostile)), Is.EqualTo(1), "not fighting: low");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Predator, combat: true)), Is.EqualTo(-1), "a predator hunting another");
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Hostile, state: VitalState.Down)), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Hostile, state: VitalState.Down, parade: true)), Is.EqualTo(3));
            Assert.That(CombatFocusPriority.Melee(Npc(Attitude.Hostile, state: VitalState.Dead, parade: true)), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Melee(new FocusFacts()), Is.EqualTo(-1), "not an NPC");
        }

        [Test]
        public void Priority_MagicAndRanged()
        {
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Friendly, party: true)), Is.EqualTo(1), "an idle companion (healing)");
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Friendly, combat: true, targetsHerosTarget: true, party: true)), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Friendly, combat: true, party: true)), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Hostile, combat: true, targetsHero: true)), Is.EqualTo(3));
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Hostile, combat: true)), Is.EqualTo(2));
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Neutral, combat: true)), Is.EqualTo(1));
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Friendly, combat: true)), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Cast(Npc(Attitude.Hostile, state: VitalState.Down)), Is.EqualTo(-1));
        }

        [Test]
        public void Previous_KeptWithin5m_DownedOnlyWhileBlocking_NeverDead()
        {
            Assert.That(CombatFocus.KeepsPrevious(VitalState.Alive, 500, blockHeld: false), Is.True);
            Assert.That(CombatFocus.KeepsPrevious(VitalState.Alive, 501, blockHeld: true), Is.False);
            Assert.That(CombatFocus.KeepsPrevious(VitalState.Down, 300, blockHeld: false), Is.False);
            Assert.That(CombatFocus.KeepsPrevious(VitalState.Down, 300, blockHeld: true), Is.True);
            Assert.That(CombatFocus.KeepsPrevious(VitalState.Dead, 100, blockHeld: true), Is.False);
        }

        [Test]
        public void Alignment_StartsWithAttacks_NotWithWhirlPierceHack()
        {
            Assert.That(CombatAlignment.AlignsOnStart(CombatAction.Attack), Is.True);
            Assert.That(CombatAlignment.AlignsOnStart(CombatAction.PowerAttack), Is.True);
            Assert.That(CombatAlignment.AlignsOnStart(CombatAction.QuickAttackLeft), Is.True);
            Assert.That(CombatAlignment.AlignsOnStart(CombatAction.SimpleWhirl), Is.True);
            Assert.That(CombatAlignment.AlignsOnStart(CombatAction.WhirlAttack), Is.False);
            Assert.That(CombatAlignment.AlignsOnStart(CombatAction.HackAttack), Is.False);
            Assert.That(CombatAlignment.AlignsOnStart(CombatAction.JumpBack), Is.False);
        }

        [Test]
        public void Alignment_Loop_BlockAligns_MoveKeyReleases_IdleKeeps()
        {
            Assert.That(CombatAlignment.MeleeLoop(blockHeld: true, hasFocus: true, moveKeyHeld: true), Is.EqualTo(CombatAlignment.LoopCommand.AlignToFocus));
            Assert.That(CombatAlignment.MeleeLoop(true, false, true), Is.EqualTo(CombatAlignment.LoopCommand.Release));
            Assert.That(CombatAlignment.MeleeLoop(false, true, true), Is.EqualTo(CombatAlignment.LoopCommand.Release));
            Assert.That(CombatAlignment.MeleeLoop(false, true, false), Is.EqualTo(CombatAlignment.LoopCommand.Keep));
            Assert.That(CombatAlignment.MagicLoop(true), Is.EqualTo(CombatAlignment.LoopCommand.Release));
            Assert.That(CombatAlignment.MagicLoop(false), Is.EqualTo(CombatAlignment.LoopCommand.Keep));
        }

        [Test]
        public void Turn_200DegreesPerSecond_HalfWhileSprinting()
        {
            Assert.That(CombatAlignment.Turn(0, 90, 0.1f, false), Is.EqualTo(20).Within(1e-4));
            Assert.That(CombatAlignment.Turn(0, -90, 0.1f, true), Is.EqualTo(-10).Within(1e-4));
            Assert.That(CombatAlignment.Turn(350, 5, 0.1f, false), Is.EqualTo(5), "short way, arrives");
        }
    }
}
