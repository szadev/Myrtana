using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-244 (owner: after a pierce the enemy is up again too soon): G3 CP 1.75 sAICombatMoveStart picks the Back
    /// variant of a stumble or fall when the hitter stands in front, so a frontal pierce plays PierceStumble_Hit_N_Back
    /// (hero rig 2.8 s) and the attacker lands his next blow inside it; _AI_PierceAttack turns a pierce whose target is
    /// still in ZS_PierceStumble into a QuickAttack after the Raise. Motion lengths: G3Cli anim motion (hero 1H, Fwd).
    /// </summary>
    public class PierceReactionTests
    {
        // Hero rig, _compiledAnimation (G3Cli anim motion …)
        const double PierceStumbleHitBack = 2.8;   // Hero_Stand_None_1H_P0_PierceStumble_Hit_N_Back_00_%_00_P0_100
        const double PierceStumbleHitFwd = 1.2;    // Hero_Stand_None_1H_P0_PierceStumble_Hit_N_Fwd_00_%_00_P0_120
        const double PierceRaise = 0.84;           // Hero_Parade_None_1H_P0_PierceAttack_Raise_N_Fwd_00_%_00_P0_0
        const double PierceHit = 0.52;             // Hero_Parade_None_1H_P0_PierceAttack_Hit_N_Fwd_00_%_00_P1_120
        const double PierceRecover = 0.8;          // Hero_Parade_None_1H_P1_PierceAttack_Recover_N_Fwd_00_%_00_P0_0
        const double AttackHit = 0.32;             // Hero_Stand_None_1H_P0_Attack_Hit_N_Fwd_00_%_00_P1_118_R
        const double QuickAttackHit = 0.44;        // Hero_Stand_None_1H_P0_QuickAttackL_Hit_N_Fwd_00_%_00_P0_100_L

        [Test]
        public void Frontal_hit_plays_the_Back_variant_of_stumbles_and_falls()
        {
            foreach (var r in new[]
                     {
                         HitReaction.QuickStumble, HitReaction.Stumble, HitReaction.PierceStumble, HitReaction.SitKnockDown,
                         HitReaction.LieKnockDown, HitReaction.Unconscious, HitReaction.PiercedKnockOut, HitReaction.Dead,
                         HitReaction.PiercedDead,
                     })
            {
                Assert.IsTrue(HitReactionRules.PlaysBackVariant(r, hitterInFront: true), $"{r}: hitter in front -> Back");
                Assert.IsFalse(HitReactionRules.PlaysBackVariant(r, hitterInFront: false), $"{r}: hitter behind -> Fwd");
            }
        }

        [Test]
        public void Parade_stumbles_always_play_Fwd()
        {
            foreach (var r in new[] { HitReaction.ParadeStumble, HitReaction.HeavyParadeStumble, HitReaction.Flinch, HitReaction.None })
            {
                Assert.IsFalse(HitReactionRules.PlaysBackVariant(r, true), r.ToString());
                Assert.IsFalse(HitReactionRules.PlaysBackVariant(r, false), r.ToString());
            }
        }

        [Test]
        public void Hitter_in_front_is_a_positive_dot_as_in_G3()
        {
            Assert.IsTrue(HitReactionRules.HitterInFront(0.5f));
            Assert.IsTrue(HitReactionRules.HitterInFront(1e-4f));
            Assert.IsFalse(HitReactionRules.HitterInFront(0f), "exactly sideways: G3 compares > 0");
            Assert.IsFalse(HitReactionRules.HitterInFront(-0.7f));
        }

        [Test]
        public void Pierce_reaction_lasts_its_Hit_at_speed_one()
        {
            var opts = new RuleOptions();
            var motion = MotionSpeedRules.MotionOf(HitReaction.PierceStumble);
            Assert.AreEqual(CombatMotion.PierceStumble, motion);
            float npc = MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = motion, Species = "Human" }, opts);
            float orc = MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = motion, Species = "Orc" }, opts);
            Assert.AreEqual(1f, npc, 1e-6f, "G3 FUN_100042a0(PierceStumble) = 1.0");
            Assert.AreEqual(1f, orc, 1e-6f);
            // the reaction is over with its Hit (the Recover is a tail): 2.8 s from the front
            Assert.AreEqual(PierceStumbleHitBack, PierceStumbleHitBack / npc, 1e-9);
        }

        [Test]
        public void A_frontal_pierce_leaves_time_for_the_next_blow()
        {
            // Hero 1H pierce (Raise, Hit, Recover in the move), then a queued attack; the pierce lands at the latest at its Hit end.
            var pierce = MeleeComboRules.Timeline(MeleeComboRules.ShapeOf(CombatAction.PierceAttack), PierceRaise, PierceHit, PierceRecover);
            double contact = pierce.HitStart + PierceHit;
            var opts = new RuleOptions();
            double attackSpeed = MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = CombatMotion.Attack, IsPlayer = true, RightHand = WeaponClass.OneHanded, Stamina = 100 }, opts);
            double next = MeleeComboRules.NextStart(pierce, click: pierce.HitStart); // clicked during the pierce
            double nextHitEnd = next + AttackHit / attackSpeed;
            Assert.Greater(contact + PierceStumbleHitBack, nextHitEnd, "G3 (Back, 2.8 s): the follow-up's whole Hit falls inside the reaction");
            Assert.Less(contact + PierceStumbleHitFwd, nextHitEnd, "the port before (Fwd, 1.2 s): the victim was free before the swing ended");
        }

        [Test]
        public void Pierce_on_a_target_still_in_PierceStumble_becomes_a_QuickAttack()
        {
            Assert.AreEqual(CombatAction.QuickAttack, MeleeComboRules.AfterRaise(CombatAction.PierceAttack, targetInPierceStumble: true));
            Assert.AreEqual(CombatAction.PierceAttack, MeleeComboRules.AfterRaise(CombatAction.PierceAttack, targetInPierceStumble: false));
            foreach (var a in new[] { CombatAction.PowerAttack, CombatAction.HackAttack, CombatAction.FinishingAttack, CombatAction.Attack })
                Assert.AreEqual(a, MeleeComboRules.AfterRaise(a, true), a.ToString());
        }

        [Test]
        public void Second_pierce_reaches_the_frontal_stumble_and_turns_into_a_QuickAttack()
        {
            // Pierce, the next pierce queued: its Raise ends while the first one's Back stumble still runs (G3 stage 2 fires).
            var pierce = MeleeComboRules.Timeline(MeleeComboRules.ShapeOf(CombatAction.PierceAttack), PierceRaise, PierceHit, PierceRecover);
            double contact = pierce.HitStart + PierceHit;
            double secondRaiseEnd = pierce.End + PierceRaise;
            Assert.Less(secondRaiseEnd, contact + PierceStumbleHitBack, "Back: still stumbling at the Raise's end");
            Assert.Greater(secondRaiseEnd, contact + PierceStumbleHitFwd, "Fwd would already be over");
            var quick = MeleeComboRules.ShapeOf(MeleeComboRules.AfterRaise(CombatAction.PierceAttack, true));
            Assert.IsFalse(quick.Raise, "the quick attack plays its Hit at once");
            Assert.Less(secondRaiseEnd + QuickAttackHit, contact + PierceStumbleHitBack, "and lands inside the stumble");
        }
    }
}
