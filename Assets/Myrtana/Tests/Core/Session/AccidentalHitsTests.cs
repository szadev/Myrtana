using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-162 [owner]: "Forgive accidental hits". The hero's stray arrows (melee and spells on a friendly or neutral NPC that
    /// fights someone else are dropped by G3's DoLogicalDamage already) on two allies fighting a wolf: with the option a
    /// complaint line and no fight up to 3 hits within 8 s, the 4th counts; without it G3 AssessAgressor in AI mode Combat
    /// (the hitter takes the target's place on its 2nd hit in a row or when the victim has no attack token).
    /// </summary>
    public class AccidentalHitsTests
    {
        static readonly ActorId Gorn = new ActorId("Gorn"), Kliff = new ActorId("Kliff"), Wolf = new ActorId("Wolf_01");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.NpcFolder, new NpcDef(Gorn, new TextId("FO_Gorn"), "Gorn", "Human", "visual", new WorldPose(2, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Kliff, new TextId("FO_Kliff"), "Kliff", "Human", "visual", new WorldPose(3, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Wolf, new TextId("FO_Wolf"), null, "Wolf", "visual", new WorldPose(4, 0, 0, 0),
                                                        combat: new NpcCombatDef(5, 20, "Wolf", humanoid: false)));

        static Combatant Npc(ActorId id, string species = "Human", bool humanoid = true) => new Combatant
        {
            Id = id, Species = species, Humanoid = humanoid, Level = 20, Strength = 100,
            Weapon = WeaponClass.OneHanded, Action = CombatAction.Attack, HitPoints = 400, Stamina = 100,
        };

        static Combatant Hero() => new Combatant
        {
            Id = ActorId.Player, IsPlayer = true, Species = "Human", Humanoid = true, Level = 10, HitPoints = 200, Stamina = 100,
            Weapon = WeaponClass.Bow,
        };

        /// <summary>The hero's arrow on <paramref name="victim"/> as the presenter builds it (HitSides over the live session).</summary>
        static HitOutcome Shoot(GameSession s, ActorId victim, string species = "Human", bool humanoid = true, HitDelivery delivery = HitDelivery.Missile)
        {
            var hit = new HitContext
            {
                Attacker = Hero(), Victim = Npc(victim, species, humanoid), Delivery = delivery, DamageAmount = 5, PlayerLevel = 10,
                DamageType = DamageType.Missile, VictimIsTarget = true,
            };
            HitSides.Fill(s, hit, ActorId.Player, victim, default, HitSides.NpcTargetOf(s, victim));
            if (delivery == HitDelivery.Spell)
            {
                hit.SpellProjectile = true;
                hit.DamageType = DamageType.Fire;
                hit.AttackerTargetsVictim = true; // an aimed spell missile: the only touch of the hero that reaches his party (fix-30)
            }
            var outcome = s.Combat.Strike(hit, byPlayerParty: true);
            Assert.That(outcome.Result, Is.Not.EqualTo(HitResult.Ignored), "the arrow lands: " + outcome);
            return outcome;
        }

        static (List<PerceptionBroadcast> sounds, List<CombatShout> shouts) Listen(GameSession s)
        {
            var sounds = new List<PerceptionBroadcast>();
            var shouts = new List<CombatShout>();
            s.Events.Subscribe<PerceptionBroadcast>(b => { if (b.Kind == BroadcastKind.FightSound && b.Source.IsPlayer) sounds.Add(b); });
            s.Events.Subscribe<CombatShout>(shouts.Add);
            return (sounds, shouts);
        }

        /// <summary>Gorn and Kliff fight the wolf, which fights Gorn.</summary>
        static GameSession AlliesVsWolf(SessionFixture f, bool forgive)
        {
            var s = f.NewGame();
            s.Combat.Port.ForgiveAccidentalHits = forgive;
            Assert.That(s.NpcCombat.Engage(Gorn, Wolf, AttackReason.Enemy), Is.True);
            Assert.That(s.NpcCombat.Engage(Kliff, Wolf, AttackReason.Enemy), Is.True);
            Assert.That(s.NpcCombat.Engage(Wolf, Gorn, AttackReason.Prey), Is.True);
            var toHero = s.Crime.Attitudes.Of(Gorn, ActorId.Player);
            Assert.That(AccidentalHitRules.SparedSide(toHero), Is.True, "Gorn is not hostile to the hero: " + toHero);
            var wolfToHero = s.Crime.Attitudes.Of(Wolf, ActorId.Player);
            Assert.That(AccidentalHitRules.CommonEnemy(wolfToHero, false, false), Is.True, "the wolf is the hero's enemy too: " + wolfToHero);
            return s;
        }

        // ------------------------------------------------------------------------------------------------ pure rules

        [Test]
        public void Rule_ForgivesUpToThreeHitsOfTheHero_InACommonFight_TheFourthCounts()
        {
            var J = new System.Func<bool, bool, bool, bool, Attitude, bool, bool, int, AccidentalHitRules.Verdict>(AccidentalHitRules.Judge);
            for (int n = 1; n <= AccidentalHitRules.ForgivenHits; n++)
                Assert.That(J(true, true, false, false, Attitude.Neutral, false, true, n), Is.EqualTo(AccidentalHitRules.Verdict.Forgiven), "hit " + n);
            Assert.That(J(true, true, false, false, Attitude.Neutral, false, true, 4), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "4th: deliberate");
            Assert.That(J(false, true, false, false, Attitude.Friendly, false, true, 1), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "option off = G3");
            Assert.That(J(true, false, false, false, Attitude.Friendly, false, true, 1), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "NPC attacker");
            Assert.That(J(true, true, true, true, Attitude.Friendly, false, true, 1), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "a knock-out / kill always counts");
            Assert.That(J(true, true, false, false, Attitude.Hostile, false, true, 1), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "a hostile victim");
            Assert.That(J(true, true, false, false, Attitude.Angry, false, true, 1), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "an angry victim");
            Assert.That(J(true, true, false, false, Attitude.Neutral, false, false, 1), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "not in a common fight");
            Assert.That(J(true, true, false, false, Attitude.Neutral, true, true, 1), Is.EqualTo(AccidentalHitRules.Verdict.Counts), "it fights the hero");
            Assert.That(J(true, true, false, true, Attitude.Neutral, false, false, 9), Is.EqualTo(AccidentalHitRules.Verdict.Forgiven), "the party: always");
        }

        [Test]
        public void Rule_CommonEnemy_HostileOrFightingTheHeroSideOrHitByHim()
        {
            Assert.That(AccidentalHitRules.CommonEnemy(Attitude.Hostile, false, false), Is.True);
            Assert.That(AccidentalHitRules.CommonEnemy(Attitude.Predator, false, false), Is.True);
            Assert.That(AccidentalHitRules.CommonEnemy(Attitude.Angry, false, false), Is.True, "a threatening wolf");
            Assert.That(AccidentalHitRules.CommonEnemy(Attitude.Neutral, true, false), Is.True, "fights the hero or his party");
            Assert.That(AccidentalHitRules.CommonEnemy(Attitude.Neutral, false, true), Is.True, "the hero hit it a moment ago");
            Assert.That(AccidentalHitRules.CommonEnemy(Attitude.Neutral, false, false), Is.False);
            Assert.That(AccidentalHitRules.CommonEnemy(Attitude.Friendly, false, false), Is.False);
        }

        [Test]
        public void Rule_G3AssessAgressorInCombat_SecondHitInARowOrNoToken_SwitchesTheTarget()
        {
            var S = new System.Func<bool, bool, bool, bool, bool, bool, bool>(AccidentalHitRules.SwitchesToHitter);
            Assert.That(S(false, true, false, false, false, true), Is.False, "an active fighter keeps its target after one stray hit");
            Assert.That(S(false, true, false, false, true, true), Is.True, "the same hitter twice in a row (LastAttacker)");
            Assert.That(S(false, true, false, false, false, false), Is.True, "a passive one (CombatState 0) turns at once");
            Assert.That(S(false, false, false, false, false, false), Is.True, "NPC hitters too");
            Assert.That(S(false, true, true, false, true, false), Is.False, "a Friend-type NPC hit by the player keeps its target");
            Assert.That(S(false, true, false, true, true, false), Is.False, "the player's party member keeps its target");
            Assert.That(S(true, true, false, false, true, false), Is.False, "the hitter is the target already");
        }

        // ------------------------------------------------------------------------------------------------ session

        [Test]
        public void Session_TwoStrayArrowsOnAnAllyFightingTheWolf_NoFight_OnlyAComplaint_DamageStays()
        {
            using (var f = Fixture())
            {
                var s = AlliesVsWolf(f, forgive: true);
                var (sounds, shouts) = Listen(s);
                int hp = s.Combat.Vitals.HitPoints(Gorn);
                Shoot(s, Gorn);
                Shoot(s, Gorn);
                Assert.That(s.NpcCombat.Get(Gorn).State.Target, Is.EqualTo(Wolf), "Gorn keeps fighting the wolf");
                Assert.That(s.NpcCombat.Get(Kliff).State.Target, Is.EqualTo(Wolf), "Kliff does not join against the hero");
                Assert.That(sounds.Where(b => b.Victim == Gorn), Is.Empty, "no fight sound: nobody is called in");
                Assert.That(shouts.Count(c => c.Speaker == Gorn && c.Line == NpcCombatSystem.ComplainLine), Is.EqualTo(1),
                            "one \"WhatAreYouDoing\" (the say gate allows one a game minute)");
                Assert.That(s.Combat.Vitals.HitPoints(Gorn), Is.LessThan(hp), "the damage is kept (G3 applies it)");
                Assert.That(s.NpcCombat.IsPlayerInCombat, Is.False);
            }
        }

        [Test]
        public void Session_FourQuickArrowsOnTheAlly_TheFourthCounts_AndHeTurnsOnTheHero()
        {
            using (var f = Fixture())
            {
                var s = AlliesVsWolf(f, forgive: true);
                var (sounds, _) = Listen(s);
                for (int i = 0; i < AccidentalHitRules.ForgivenHits; i++) Shoot(s, Gorn);
                Assert.That(s.NpcCombat.Get(Gorn).State.Target, Is.EqualTo(Wolf), "three forgiven");
                Assert.That(sounds, Is.Empty);
                Shoot(s, Gorn);
                Assert.That(s.NpcCombat.Get(Gorn).State.Target, Is.EqualTo(ActorId.Player), "the 4th within 8 s is deliberate");
                Assert.That(s.NpcCombat.Get(Gorn).State.Reason, Is.EqualTo(AttackReason.ReactToDamage));
                Assert.That(sounds.Count(b => b.Victim == Gorn), Is.EqualTo(1), "the counted hit makes its fight sound");
            }
        }

        [Test]
        public void Session_OptionOff_G3_TheFirstArrowTurnsAnAllyWithoutTheToken_AndCallsIn()
        {
            using (var f = Fixture())
            {
                var s = AlliesVsWolf(f, forgive: false);
                var (sounds, shouts) = Listen(s);
                Assert.That(s.NpcCombat.Get(Kliff).HoldsToken, Is.False, "never ticked: Kliff waits without the attack token");
                Shoot(s, Kliff);
                Assert.That(s.NpcCombat.Get(Kliff).State.Target, Is.EqualTo(ActorId.Player), "G3 AssessAgressor: CombatState 0 -> AssessTarget(hitter)");
                Assert.That(sounds.Count(b => b.Victim == Kliff), Is.EqualTo(1), "G3 SendPerceptionFightSound");
                Assert.That(shouts.Where(c => c.Line == NpcCombatSystem.ComplainLine), Is.Empty);
            }
        }

        [Test]
        public void Session_AnNpcHitterTwiceInARow_TakesTheTargetsPlace_G3()
        {
            using (var f = Fixture())
            {
                var s = AlliesVsWolf(f, forgive: true);
                var hit = new HitContext { Attacker = Npc(Kliff), Victim = Npc(Wolf, "Wolf", false), Delivery = HitDelivery.Melee, DamageAmount = 1, PlayerLevel = 10, DamageType = DamageType.Blade };
                HitSides.Fill(s, hit, Kliff, Wolf, HitSides.NpcTargetOf(s, Kliff), HitSides.NpcTargetOf(s, Wolf));
                s.Combat.Strike(hit, byPlayerParty: false);
                Assert.That(s.NpcCombat.Get(Wolf).State.Target, Is.EqualTo(Kliff), "the wolf had no token: it turns to its hitter");
            }
        }

        [Test]
        public void Session_TheFollowThroughAfterTheWolfFell_IsForgivenToo()
        {
            using (var f = Fixture())
            {
                var s = AlliesVsWolf(f, forgive: true);
                // Gorn trades a blow with the wolf, then the wolf is gone and the fight over
                var blow = new HitContext { Attacker = Npc(Gorn), Victim = Npc(Wolf, "Wolf", false), Delivery = HitDelivery.Melee, DamageAmount = 1, PlayerLevel = 10, DamageType = DamageType.Blade };
                HitSides.Fill(s, blow, Gorn, Wolf, HitSides.NpcTargetOf(s, Gorn), HitSides.NpcTargetOf(s, Wolf));
                s.Combat.Strike(blow, byPlayerParty: false);
                s.NpcCombat.Get(Gorn).Disengage();
                s.NpcCombat.Get(Kliff).Disengage();
                var (sounds, _) = Listen(s);
                Shoot(s, Gorn);
                Assert.That(s.NpcCombat.Get(Gorn).IsFighting, Is.False, "within the 5 s grace the stray arrow is forgiven");
                Assert.That(sounds, Is.Empty);
            }
        }

        [Test]
        public void Session_TheHerosOwnAttackOnAnNpcFightingAnAlly_Counts_ThenHisStrayArrowsOnTheAllyAreForgiven()
        {
            // the Montera case: rebels attack an orc that is neutral to the hero, the hero joins in
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Combat.Port.ForgiveAccidentalHits = true;
                Assert.That(s.NpcCombat.Engage(Kliff, Gorn, AttackReason.Enemy), Is.True);
                Assert.That(s.NpcCombat.Engage(Gorn, Kliff, AttackReason.Enemy), Is.True);
                var (sounds, _) = Listen(s);
                Shoot(s, Gorn);
                Assert.That(s.NpcCombat.Get(Gorn).State.Target, Is.EqualTo(ActorId.Player),
                            "the hero's first hit on Kliff's foe is no accident (Kliff is no enemy of his): G3, Gorn turns on him");
                Assert.That(sounds.Count(b => b.Victim == Gorn), Is.EqualTo(1));
                Shoot(s, Kliff);
                Assert.That(s.NpcCombat.Get(Kliff).State.Target, Is.EqualTo(Gorn), "Gorn now fights the hero: a stray arrow on Kliff is forgiven");
                Assert.That(sounds.Count(b => b.Victim == Kliff), Is.EqualTo(0));
            }
        }

        [Test]
        public void Session_AnNpcNotInACommonFight_FightsBackAsInG3()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Combat.Port.ForgiveAccidentalHits = true;
                var (sounds, _) = Listen(s);
                var toHero = s.Crime.Attitudes.Of(Kliff, ActorId.Player);
                Shoot(s, Kliff);
                Shoot(s, Kliff);
                bool fights = s.NpcCombat.Get(Kliff)?.IsFighting == true;
                // G3 AssessAgressor (Routine): Friendly complains first, then fights; others fight at once
                Assert.That(fights, Is.True, "no fight against a common enemy, no forgiveness (attitude " + toHero + ")");
                Assert.That(sounds.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void Session_TheHerosCompanion_NeverTurnsOnHim_AndNobodyIsCalledIn()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Combat.Port.ForgiveAccidentalHits = true;
                s.Party.JoinCompanion(Gorn);
                s.NpcCombat.Engage(Gorn, Wolf, AttackReason.Enemy);
                var (sounds, _) = Listen(s);
                for (int i = 0; i < 6; i++) Shoot(s, Gorn, delivery: HitDelivery.Spell);
                Assert.That(s.NpcCombat.Get(Gorn).State.Target, Is.EqualTo(Wolf), "the party member keeps fighting the wolf");
                Assert.That(sounds, Is.Empty, "no fight sound: Kliff and the others do not turn on the hero");
                s.NpcCombat.Get(Gorn).Disengage();
                for (int i = 0; i < 6; i++) Shoot(s, Gorn, delivery: HitDelivery.Spell);
                Assert.That(s.NpcCombat.Get(Gorn).IsFighting, Is.False, "out of combat too");
            }
        }

        [Test]
        public void PortOptions_ForgiveAccidentalHits_DefaultOn_RowAboveForcedRelease()
        {
            var port = new PortOptions();
            Assert.That(port.ForgiveAccidentalHits, Is.True, "[owner] default");
            Assert.That(new CombatPortOptions().ForgiveAccidentalHits, Is.False, "the rules' own default is CP 1.75");
            Assert.That(PortOptions.RowText(PortOptions.Row.ForgiveHits, "Russian"), Is.EqualTo("Прощать случайные удары"));
            Assert.That(PortOptions.RowText(PortOptions.Row.ForgiveHits, "English"), Is.EqualTo("Forgive accidental hits"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ForgiveHits, false, "English"), Is.EqualTo("No (original)"));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.ForgiveHitsTop, Is.EqualTo(196f));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.RowBottom(Myrtana.Ui.Skin.VideoPortRowsLayout.ForgiveHitsTop),
                        Is.LessThanOrEqualTo(Myrtana.Ui.Skin.VideoPortRowsLayout.ForcedReleaseTop + Myrtana.Ui.Skin.VideoPortRowsLayout.SliderOffset), "clear of Freeing by force counts");
        }
    }
}
