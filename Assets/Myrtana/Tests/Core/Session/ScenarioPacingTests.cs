using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The dev runner's timing rules (fix-41, editor-runner-28).</summary>
    public sealed class ScenarioPacingTests
    {
        [Test]
        public void GetUp_PressesAtOnce_AgainWhileStillSitting_ThenForces()
        {
            Assert.That(ScenarioPacing.NextGetUp(false, true, false, 0f, 0), Is.EqualTo(GetUpAction.None), "not sitting");
            Assert.That(ScenarioPacing.NextGetUp(true, true, false, 0f, 0), Is.EqualTo(GetUpAction.Press), "a new sit: press at once");
            Assert.That(ScenarioPacing.NextGetUp(true, false, true, 10f, 1), Is.EqualTo(GetUpAction.None), "a get-up is pending (the fall plays)");
            Assert.That(ScenarioPacing.NextGetUp(true, false, false, 0.5f, 1), Is.EqualTo(GetUpAction.None), "just pressed");
            Assert.That(ScenarioPacing.NextGetUp(true, false, false, 2f, 1), Is.EqualTo(GetUpAction.Press),
                        "the press was lost (r28 vangard_01: a draw cut by the fall): press again");
            Assert.That(ScenarioPacing.NextGetUp(true, false, false, 2f, ScenarioPacing.GetUpPressesBeforeForce), Is.EqualTo(GetUpAction.Force));
        }

        [Test]
        public void AfterAFight_KeepsTheWeaponDrawnWhileFoesAreNear()
        {
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 2, "fight"), Is.False, "two goblins of the pack still attack: keep it drawn");
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 1, null), Is.False, "an escort's defend with another attacker near");
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 0, "fight"), Is.True, "the area is clear");
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 3, "talk"), Is.True, "the next step needs empty hands");
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 3, "TP"), Is.True, "leaving the place");
            Assert.That(ScenarioPacing.SheatheAfterFight("1", 3, "fight"), Is.True, "sheathe=1 forces it");
            Assert.That(ScenarioPacing.SheatheAfterFight("0", 0, "talk"), Is.False, "sheathe=0 forbids it");
        }

        [Test]
        public void Draw_WaitsForTheGetUp_WithinACap()
        {
            Assert.That(ScenarioPacing.DrawLimit(0f), Is.EqualTo(ScenarioPacing.DrawSeconds));
            Assert.That(ScenarioPacing.DrawLimit(8f), Is.EqualTo(ScenarioPacing.DrawSeconds + 8f));
            Assert.That(ScenarioPacing.DrawLimit(500f), Is.EqualTo(ScenarioPacing.DrawSeconds + ScenarioPacing.DrawWaitDownSeconds));
        }

        [Test]
        public void Fight_StaggerTimeIsNotFightTime_UpToTheFightAgain()
        {
            Assert.That(ScenarioPacing.FightLimit(30f, 0f), Is.EqualTo(30f));
            Assert.That(ScenarioPacing.FightLimit(30f, 26.3f), Is.EqualTo(56.3f).Within(1e-3), "r28 Rhobar: busy 26 of 30 s");
            Assert.That(ScenarioPacing.FightLimit(30f, 100f), Is.EqualTo(60f));
        }

        [Test]
        public void Fight_HpClampHoldsAfterARefill()
        {
            Assert.That(ScenarioPacing.Reclamp(1000, 5), Is.True, "r28 Gotha_Demon_01 5 -> 1000 when its cell streamed in");
            Assert.That(ScenarioPacing.Reclamp(5, 5), Is.False);
            Assert.That(ScenarioPacing.Reclamp(3, 5), Is.False);
        }

        [Test]
        public void ScenarioTimeout_GrowsByPausedStepTime()
        {
            Assert.That(ScenarioPacing.TimedOut(1145f, 900f, 0f), Is.True);
            Assert.That(ScenarioPacing.TimedOut(1145f, 900f, 702f), Is.False, "r28 nemora_02: 702 s paused by the escort");
            Assert.That(ScenarioPacing.TimedOut(3000f, 900f, 5000f), Is.True, "credit capped at 2 x the timeout");
        }
    
        [Test]
        public void EscortWatch_NamesTheLastHitter_AndTopsUpALowGuideAFewTimes()
        {
            // fix-46: r28 vangard_01 "Georg is dead (attacked by nobody)": the killer had died in the same exchange.
            var w = new EscortWatch();
            Assert.That(w.Cause(10f), Does.StartWith("no hit recorded"));
            w.OnHit("Orc_Raider_Scout_02#e9bff45d", 0, false, 1f);
            Assert.That(w.Hits, Is.EqualTo(0), "a parried hit is no hit");
            w.OnHit("Orc_Raider_Scout_02#e9bff45d", 40, true, 2f);
            Assert.That(w.Cause(4.5f), Does.Contain("Orc_Raider_Scout_02#e9bff45d").And.Contain("lethal").And.Contain("2.5 s"));

            Assert.That(w.TopUp(300, 1000), Is.EqualTo(-1), "30 % is not low");
            Assert.That(w.TopUp(13, 1000), Is.EqualTo(500), "under 25 %: to half");
            Assert.That(w.TopUp(0, 1000), Is.EqualTo(-1), "dead stays dead");
            Assert.That(w.TopUp(100, 1000), Is.EqualTo(500));
            Assert.That(w.TopUp(100, 1000), Is.EqualTo(500));
            Assert.That(w.TopUps, Is.EqualTo(EscortWatch.MaxTopUps));
            Assert.That(w.TopUp(100, 1000), Is.EqualTo(-1), "after the cap the game decides");
        }
        [Test]
        public void Draw_WaitsForTheGround_OnlyBriefly_G3HasNoGroundRule()
        {
            Assert.That(ScenarioPacing.DrawReady(true, false, false, 0f), Is.True, "standing");
            Assert.That(ScenarioPacing.DrawReady(true, false, true, 0f), Is.False, "a combat motion runs");
            Assert.That(ScenarioPacing.DrawReady(false, false, false, 0.5f), Is.False, "landing after a tp: wait");
            Assert.That(ScenarioPacing.DrawReady(true, true, false, 0.5f), Is.False, "a short slide: wait");
            Assert.That(ScenarioPacing.DrawReady(false, false, false, ScenarioPacing.DrawGroundGraceSeconds), Is.True,
                        "r30 hammerclan_04: 'not grounded' for the whole budget; after the grace the draw is tried (G3 draws anywhere)");
            Assert.That(ScenarioPacing.DrawReady(false, false, true, 60f), Is.False, "busy still wins");
            Assert.That(ScenarioPacing.DrawGroundGraceSeconds, Is.LessThan(ScenarioPacing.DrawSeconds));
        }

        [Test]
        public void UnaskedTalk_PassesTheGuard_ElseEnds_ElseAborts()
        {
            var end = new TalkChoice(true, false, false);
            var topic = new TalkChoice(false, false, false);
            var friend = new TalkChoice(false, true, false);
            var foe = new TalkChoice(false, true, true);
            Assert.That(UnaskedTalk.Pick(new[] { foe, friend }), Is.EqualTo(1), "Sivert: Friend!, not Foe! (a duel)");
            Assert.That(UnaskedTalk.Pick(new[] { topic, friend, end }), Is.EqualTo(1), "Garik: 'Why are you stopping me?'");
            Assert.That(UnaskedTalk.Pick(new[] { topic, end }), Is.EqualTo(1), "nothing passes a guard: End");
            Assert.That(UnaskedTalk.Pick(new[] { topic, foe }), Is.EqualTo(-1), "no End, only a fight: abort");
            Assert.That(UnaskedTalk.Pick(new TalkChoice[0]), Is.EqualTo(-1));
            Assert.That(UnaskedTalk.Pick(null), Is.EqualTo(-1));
        }

        /// <summary>
        /// r30 hammerclan_01: Sivert's FirstWarn ("Welcome! Friend or foe?") waits at two sub-choices without End; the
        /// runner's answer is "Friend!", which switches the guard off and ends the talk - the hero walks on.
        /// </summary>
        [Test]
        public void UnaskedTalk_GateGuardWarning_FriendSwitchesTheGuardOff_AndTheTalkEnds()
        {
            var world = new RecordingWorldDirector();
            var friendCommands = new DialogCommand[] { StoryFixture.PlayerSays("Friend"), new SetGuardStatus(ActorRef.Partner, false) };
            var foeCommands = new DialogCommand[]
            {
                StoryFixture.PlayerSays("Foe"), new StartFight(ActorRef.Partner, ActorRef.Player, AttackReason.Duel), new EndDialog(),
                new SetGuardStatus(ActorRef.Partner, false),
            };
            var f = new StoryFixture { WorldDirector = world }
                .Entry("WARN", DialogEntryKind.Important, condition: new GateGuardMayWarn(ActorRef.Partner),
                       commands: new DialogCommand[] { StoryFixture.NpcSays("Welcome"), StoryFixture.NpcSays("FriendOrFoe"), new GateGuardWarned(ActorRef.Partner) })
                .Entry("FRIEND", order: 1, parent: "WARN", closesSiblings: true, commands: friendCommands)
                .Entry("FOE", order: 2, parent: "WARN", closesSiblings: true, commands: foeCommands);
            var commandsOf = new Dictionary<string, DialogCommand[]> { ["FRIEND"] = friendCommands, ["FOE"] = foeCommands };

            world.MayWarn.Add("Gnar 1");
            Assert.That(f.Dialog.TalkFirst(StoryFixture.Npc), Is.True, "the guard stops the hero");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "Welcome", "FriendOrFoe" }));
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingChoice), "the hero's controls stay locked until he answers");
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "FRIEND", "FOE" }));

            var choices = f.Dialog.Current.Options
                .Select(o => new TalkChoice(o.IsExit, commandsOf[o.Entry.Name].Any(c => c is SetGuardStatus g && !g.Active),
                                            commandsOf[o.Entry.Name].Any(c => c is StartFight)))
                .ToList();
            int pick = UnaskedTalk.Pick(choices);
            Assert.That(f.Dialog.Current.Options[pick].Entry.Name, Is.EqualTo("FRIEND"));
            f.Dialog.Choose(f.Dialog.Current.Options[pick].Entry);
            f.FinishLines();
            Assert.That(world.Calls, Does.Contain("warned Gnar 1").And.Contain("guard Gnar False"));
            Assert.That(f.Director.Calls.Any(c => c.StartsWith("attack")), Is.False, "no duel");
            Assert.That(f.Dialog.IsActive, Is.False, "Foe! is closed with it, nothing else to say: the hero is free again");
        }

        // fix-58 (runner-31 sideq_okara_01): Fraser fought the hunt's boars 4.6 m from the checkpoint; the escort ended there
        [Test]
        public void EscortArrived_AGuideInAFightAtTheTargetHasNotArrived()
        {
            Assert.That(ScenarioPacing.EscortArrived(4.6f, 5f, guide: true, fighting: true), Is.False, "the fight may draw it off");
            Assert.That(ScenarioPacing.EscortArrived(4.6f, 5f, guide: true, fighting: false), Is.True);
            Assert.That(ScenarioPacing.EscortArrived(4.6f, 5f, guide: false, fighting: true), Is.True, "a follower the hero brings counts where it is");
            Assert.That(ScenarioPacing.EscortArrived(5.1f, 5f, guide: true, fighting: false), Is.False);
        }

        // ------------------------------------------------------------------------------------------- fix-64

        [Test]
        public void GetUp_WithAShield_IsTheParade()
        {
            Assert.That(ScenarioPacing.GetUpWithParade(blockHeld: false, shieldInHand: true), Is.True,
                        "r31 hammerclan_02 [shield]: 15 get-up attacks into a BlackGoblin's next hit");
            Assert.That(ScenarioPacing.GetUpWithParade(blockHeld: true, shieldInHand: false), Is.True, "the block held (as before)");
            Assert.That(ScenarioPacing.GetUpWithParade(blockHeld: false, shieldInHand: false), Is.False, "else the get-up attack");
        }

        [Test]
        public void EscortDownTime_IsOneSpell_NotSummedOverADefend()
        {
            // down at 10 s, seen every frame until 10.8 s: the same spell
            Assert.That(ScenarioPacing.DownStart(10f, 10.78f, 10.8f), Is.EqualTo(10f));
            // r31 hammerclan_02: down at 100 s, a 22 s defend ran (not watched by the escort loop), down again at 123 s
            Assert.That(ScenarioPacing.DownStart(100f, 100.1f, 123f), Is.EqualTo(123f), "a new spell, not 'stays down 23 s'");
            Assert.That(ScenarioPacing.DownStart(-1f, -1f, 5f), Is.EqualTo(5f), "the first down");
        }

        /// <summary>
        /// One frame of the hero's Attack against a target <paramref name="ahead"/> m in front, in the target's frame: the
        /// blade (hand 0.4 m, tip <see cref="ScenarioPacing.BladeReach"/> ahead) sweeps from <paramref name="fromX"/> to
        /// <paramref name="toX"/> at 1.15 m. The dual wielder's left blade sweeps the other way (MeleeHitGeometryTests).
        /// </summary>
        static bool Swing(WeaponTrigger victim, float ahead, float fromX, float toX)
        {
            const float y = 1.15f, Blade = 0.08f;
            float z = -ahead, tip = ScenarioPacing.BladeReach;
            return MeleeHitGeometry.SweepTouches(victim,
                new HitPoint(fromX, y, z + 0.4f), new HitPoint(fromX, y, z + tip),
                new HitPoint(toX, y, z + 0.4f), new HitPoint(toX, y, z + tip), Blade);
        }

        [Test]
        public void Dual_VsAMeatbug_BothBladesTouchFromTheFightOpsStop()
        {
            // r31 melee_small [dual]: 8 swings, touched 0 - the op stopped at 2.0 m and swung from 2.4 m
            var meatbug = MeleeHitGeometry.Trigger(0.35f, 0.3f, 0.5f, 0f);
            float half = System.Math.Min(meatbug.HalfX, meatbug.HalfZ);
            Assert.That(DualWieldRules.Blades(CombatAction.Attack, 0, dual: true), Is.EqualTo(MeleeBlades.Both), "the dual Attack strikes with both blades");
            Assert.That(DualWieldRules.Blades(CombatAction.Attack, 1, dual: true), Is.EqualTo(MeleeBlades.Both));
            float stop = ScenarioPacing.MeleeStop(half), reach = ScenarioPacing.MeleeReach(half);
            Assert.That(Swing(meatbug, stop, 0.9f, -0.9f), "right blade from the stop distance");
            Assert.That(Swing(meatbug, stop, -0.9f, 0.9f), "left blade from the stop distance");
            Assert.That(Swing(meatbug, reach, 0.9f, -0.9f), "right blade from the farthest swing distance");
            Assert.That(Swing(meatbug, reach, -0.9f, 0.9f), "left blade from the farthest swing distance");
            Assert.That(Swing(meatbug, 2.0f, 0.9f, -0.9f), Is.False, "the old 2.0 m stop was out of reach");
            Assert.That(reach, Is.LessThan(2.0f));
        }

        [Test]
        public void MeleeReach_KeepsLargeTargets_ClosesInOnSmallOnes()
        {
            Assert.That(ScenarioPacing.MeleeReach(1.5f), Is.EqualTo(ScenarioPacing.MaxMeleeReach), "a troll: as before");
            Assert.That(ScenarioPacing.MeleeReach(0f), Is.EqualTo(ScenarioPacing.BladeReach));
            var goblin = MeleeHitGeometry.Trigger(0.7f, 1.3f, 0.5f, 0f);
            float g = System.Math.Min(goblin.HalfX, goblin.HalfZ);
            Assert.That(Swing(goblin, ScenarioPacing.MeleeReach(g), 0.9f, -0.9f), "a goblin from the swing distance");
            Assert.That(ScenarioPacing.MeleeStop(g), Is.GreaterThan(1.0f), "not inside its body");
        }
    }
}
