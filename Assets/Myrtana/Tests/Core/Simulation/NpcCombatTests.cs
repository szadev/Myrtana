using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The NPC combat AI against combat_ai.json (CP 1.75, extended AI on unless stated): Docs/combat.md §6.</summary>
    public class NpcCombatTests
    {
        static readonly CombatAiRules Ai = TestRules.CombatAi();
        static readonly CombatRules Rules = TestRules.Combat();
        static readonly ActorId Self = new ActorId("Orc_Warrior"), Other = new ActorId("Orc_Warrior_02"), Bandit = new ActorId("Bandit");

        static readonly NpcCombatDef Orc = new NpcCombatDef(15, 35, "Orc", humanoid: true, role: NpcRole.Guard, @class: NpcClass.Warrior, hasEnclave: true);
        static readonly NpcCombatDef Sabertooth = new NpcCombatDef(0, 30, "Sabertooth", humanoid: false, animal: true);
        static readonly NpcCombatDef Wolf = new NpcCombatDef(0, 21, "Wolf", humanoid: false, animal: true);
        static readonly NpcCombatDef Deer = new NpcCombatDef(0, 10, "Deer", humanoid: false, animal: true, gameAnimal: true);
        static readonly NpcCombatDef Gargoyle = new NpcCombatDef(0, 30, "Gargoyle", humanoid: false, evil: true);

        static readonly CombatLoadout Sword = CombatLoadout.From(new[] { Item("It_1H_Sword_01", "OneHanded") });
        static readonly CombatLoadout Axe = CombatLoadout.From(new[] { Item("It_Axe_OrcWaraxe_01", "Axe") });
        static readonly CombatLoadout SwordAndShield = CombatLoadout.From(new[] { Item("It_1H_Sword_01", "OneHanded"), Item("It_Shield_01", "Shield") });

        static ItemDef Item(string id, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, null, default);

        /// <summary>The player 250 away with a sword, doing nothing; free space all around.</summary>
        static CombatSituation Situation(float distance = 250, FighterAction target = FighterAction.None, double now = 0, double gameNow = 0) =>
            new CombatSituation
            {
                Now = now, GameNow = gameNow, Distance = distance, LineOfSight = true, FreeBack = true, FreeBackLeft = true, FreeBackRight = true,
                TargetIsPlayer = true, TargetState = VitalState.Alive, TargetKillable = true, TargetHumanoid = true, TargetSpecies = "Human",
                TargetHoldsMelee = true, TargetAction = target, TargetPhase = MovePhase.Raise, TargetEngagedWithSelf = true,
                AttitudeToTarget = Attitude.Hostile, PlayerLevel = 10, HitPointsPercent = 100,
            };

        static MoveContext Context(NpcCombatDef self, CombatLoadout loadout, CombatSituation s, CombatMove current = CombatMove.Wait,
                                   bool attackReady = true, bool hitRecently = false, bool group = false) =>
            new MoveContext { Self = self, Loadout = loadout, Situation = s, Current = current, AttackReady = attackReady, HitRecently = hitRecently, Group = group };

        static CombatMoveSelector Selector(IRandom random = null, RuleOptions options = null) =>
            new CombatMoveSelector(Ai, options ?? RuleOptions.Default, random ?? new SeededRandom(3));

        static NpcCombatBrain Brain(NpcCombatDef def, CombatLoadout loadout, CombatCoordinator coordinator = null, IRandom random = null,
                                    RuleOptions options = null, ActorId? self = null)
        {
            options = options ?? RuleOptions.Default;
            return new NpcCombatBrain(self ?? Self, def, loadout, Ai, Rules, options, random ?? new SeededRandom(5),
                                      coordinator ?? new CombatCoordinator(Ai.Token, options));
        }

        /// <summary>Plays back fixed numbers: <see cref="Next"/> from the int queue (modulo the bound), floats from the float queue.</summary>
        sealed class ScriptedRandom : IRandom
        {
            readonly Queue<int> _ints;
            readonly Queue<float> _floats;

            public ScriptedRandom(IEnumerable<int> ints, IEnumerable<float> floats = null)
            {
                _ints = new Queue<int>(ints);
                _floats = new Queue<float>(floats ?? new[] { 0.99f });
            }

            public int Next(int maxExclusive) => (_ints.Count > 0 ? _ints.Dequeue() : 0) % maxExclusive;
            public float NextFloat() => _floats.Count > 0 ? _floats.Dequeue() : 0.99f;
            public RandomState Save() => default;
            public void Load(RandomState state) { }
        }

        // ------------------------------------------------------------------------------------------- error value, reach

        [Test]
        public void ErrorValue_FollowsDifficultyPlayerLevelArenaAndMage()
        {
            var s = Situation();
            Assert.That(Selector().ErrorValue(Context(Orc, Sword, s)), Is.EqualTo(15), "5 + Medium 10");
            s.PlayerLevel = 1;
            Assert.That(Selector(options: new RuleOptions(difficulty: Difficulty.Easy)).ErrorValue(Context(Orc, Sword, s)), Is.EqualTo(30), "5 + 20 + 5");
            Assert.That(Selector().ErrorValue(Context(Orc, Sword, s)), Is.EqualTo(20), "at least 20 below player level 2");
            s.PlayerLevel = 75;
            Assert.That(Selector(options: new RuleOptions(difficulty: Difficulty.Hard)).ErrorValue(Context(Orc, Sword, s)), Is.EqualTo(0), "clamped at 0");
            s.PlayerLevel = 10;
            s.Arena = true;
            var mage = new NpcCombatDef(0, 20, "Human", humanoid: true, mage: true, @class: NpcClass.Mage);
            Assert.That(Selector().ErrorValue(Context(mage, Sword, s)), Is.EqualTo(0), "15 − arena 10 − mage 5");
        }

        [Test]
        public void Reach_IsSpeciesBasePlusMoveLength()
        {
            var r = Ai.Reach;
            Assert.That(r.ReachOf("Wolf", true, FighterAction.Attack, true), Is.EqualTo(290));
            Assert.That(r.ReachOf("Human", false, FighterAction.Attack, true), Is.EqualTo(320));
            Assert.That(r.ReachOf("Human", true, FighterAction.Attack, true), Is.EqualTo(170), "fists 50");
            Assert.That(r.ReachOf("Human", false, FighterAction.PowerAttack, true), Is.EqualTo(380));
            Assert.That(r.ReachOf("Lurker", true, FighterAction.QuickAttack, true), Is.EqualTo(260));
            Assert.That(r.ReachOf("Lurker", true, FighterAction.Attack, true), Is.EqualTo(270));
            Assert.That(r.ReachOf("Zombie", false, FighterAction.Attack, true), Is.EqualTo(350));
            Assert.That(r.ReachOf("Troll", false, FighterAction.Attack, false), Is.EqualTo(420), "vanilla: Troll 300");
            Assert.That(r.ReachOf("Human", true, FighterAction.Attack, false), Is.EqualTo(320), "vanilla: no fists base");
        }

        // ------------------------------------------------------------------------------------------- availability

        [Test]
        public void Availability_SimpleWhirlOnlyForDualWielders()
        {
            // G3 availability: SimpleWhirl for dual 1H; EquipWeaponry splits a one-handed stack of two (fix-32).
            var dual = CombatLoadout.From(new[] { Item("It_1H_AssSword_01", "OneHanded"), Item("It_1H_AssSword_01", "OneHanded") });
            var s = Situation();
            Assert.IsTrue(dual.DualWield);
            Assert.IsTrue(Selector().IsAvailable(CombatMove.SimpleWhirl, Context(Orc, dual, s)));
            Assert.IsFalse(Selector().IsAvailable(CombatMove.SimpleWhirl, Context(Orc, Sword, s)));
            Assert.IsTrue(Selector().IsAvailable(CombatMove.PierceAttack, Context(Orc, dual, s)), "1H in the right hand");
        }

        [Test]
        public void Availability_FollowsSpeciesWeaponAndParryRules()
        {
            var sel = Selector();
            var s = Situation(target: FighterAction.PierceAttack);
            Assert.IsTrue(sel.IsAvailable(CombatMove.PierceAttack, Context(Orc, Sword, s)));
            Assert.IsFalse(sel.IsAvailable(CombatMove.HackAttack, Context(Orc, Sword, s)), "hack needs a heavy weapon");
            Assert.IsTrue(sel.IsAvailable(CombatMove.HackAttack, Context(Orc, Axe, s)));
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Orc, Sword, s)), "a pierce needs shield + 1H");
            Assert.IsTrue(sel.IsAvailable(CombatMove.Parade, Context(Orc, SwordAndShield, s)));
            Assert.IsFalse(sel.IsAvailable(CombatMove.JumpBack, Context(Orc, Sword, s)), "humanoids do not jump back");
            Assert.IsFalse(sel.IsAvailable(CombatMove.Move, Context(Orc, Sword, s)), "no CP flank run for orcs");
            Assert.IsTrue(sel.IsAvailable(CombatMove.Move, Context(Sabertooth, CombatLoadout.Fists, s)));
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Wolf, CombatLoadout.Fists, s)));
            // fix-81: IsInFistMode is false for a Minecrawler: it parries a sword's attack (not a pierce without a shield).
            var crawler = new NpcCombatDef(0, 25, "Minecrawler", humanoid: false, animal: true);
            Assert.IsTrue(sel.IsAvailable(CombatMove.Parade, Context(crawler, CombatLoadout.Fists, Situation(target: FighterAction.Attack))));
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(crawler, CombatLoadout.Fists, s)));
            Assert.IsFalse(sel.IsAvailable(CombatMove.Attack, Context(Orc, Sword, s, attackReady: false)), "hit cooldown");
            Assert.IsFalse(sel.IsAvailable(CombatMove.Attack, Context(new NpcCombatDef(0, 20, "Lurker", false), CombatLoadout.Fists, s)),
                           "extended AI: lurkers have no plain attack");

            var fists = Situation(target: FighterAction.Attack);
            fists.TargetFists = true;
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Orc, Sword, fists)), "a weapon does not parry fists");
            Assert.IsTrue(sel.IsAvailable(CombatMove.Parade, Context(Orc, CombatLoadout.Fists, fists)), "fists parry fists between humanoids");
        }

        /// <summary>fix-109 [owner] option "Shield protects from beasts": NPC shield bearers parry animals; off = CP 1.75.</summary>
        [Test]
        public void Availability_ShieldBeastsOption_LetsAShieldParryAnimals()
        {
            var wolf = Situation(target: FighterAction.Attack);
            wolf.TargetFists = true;
            wolf.TargetHumanoid = false;
            wolf.TargetSpecies = "Wolf";
            var sel = Selector();
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Orc, SwordAndShield, wolf)), "CP 1.75: no parade against a wolf");
            sel.Port.ShieldBlocksCreatures = true;
            Assert.IsTrue(sel.IsAvailable(CombatMove.Parade, Context(Orc, SwordAndShield, wolf)));
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Orc, Sword, wolf)), "a weapon alone still does not");
            var troll = Situation(target: FighterAction.Attack);
            troll.TargetFists = true;
            troll.TargetSpecies = "Troll";
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Orc, SwordAndShield, troll)), "trolls go through the shield");
            var crawler = Situation(target: FighterAction.Attack);
            crawler.TargetSpecies = "Minecrawler";
            crawler.TargetFists = true;
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Orc, Sword, crawler)), "UP: no weapon parade against a Minecrawler");
            Assert.IsTrue(sel.IsAvailable(CombatMove.Parade, Context(Orc, SwordAndShield, crawler)));
        }

        // ------------------------------------------------------------------------------------------- unified pick

        [Test]
        public void Classify_FollowsTheScoreTableOrder()
        {
            var sel = Selector();
            Assert.That(sel.Classify(Context(Orc, Sword, Situation(250, FighterAction.Attack))), Is.EqualTo(FightSituation.DuelThreatened));
            Assert.That(sel.Classify(Context(Orc, Sword, Situation(500))), Is.EqualTo(FightSituation.Approach));
            Assert.That(sel.Classify(Context(Orc, Sword, Situation(250, FighterAction.Parade))), Is.EqualTo(FightSituation.DuelTargetParading));
            Assert.That(sel.Classify(Context(Orc, Sword, Situation(250, FighterAction.Stumble))), Is.EqualTo(FightSituation.DuelTargetStaggered));
            Assert.That(sel.Classify(Context(Orc, Sword, Situation(250, FighterAction.Fwd))), Is.EqualTo(FightSituation.DuelTargetMoving));
            Assert.That(sel.Classify(Context(Orc, Sword, Situation(250), group: true)), Is.EqualTo(FightSituation.GroupInReach));
            var fists = Situation(250, FighterAction.Attack);
            fists.TargetFists = true;
            Assert.That(sel.Classify(Context(Orc, Sword, fists)), Is.EqualTo(FightSituation.DuelAttackedLight), "the fist does not reach 250");
        }

        [Test]
        public void Scores_TargetParading_PierceBest_OtherAttacksAndTurns45PlusE()
        {
            var scores = Selector().ScoreCandidates(Context(Orc, Sword, Situation(250, FighterAction.Parade)), 15)
                                   .ToDictionary(p => p.Key, p => p.Value);
            Assert.That(scores, Is.EquivalentTo(new Dictionary<CombatMove, int>
            {
                [CombatMove.Fwd] = 15, [CombatMove.TurnLeft] = 60, [CombatMove.TurnRight] = 60, [CombatMove.PierceAttack] = 75,
                [CombatMove.PowerAttack] = 60, [CombatMove.Attack] = 60, [CombatMove.QuickAttack] = 60,
            }));
        }

        [Test]
        public void Scores_PenaliseAttacksAfterBeingHit_AndRepeatingTheCurrentMove()
        {
            var scores = Selector().ScoreCandidates(Context(Orc, Sword, Situation(250, FighterAction.Parade), current: CombatMove.TurnLeft, hitRecently: true), 15)
                                   .ToDictionary(p => p.Key, p => p.Value);
            Assert.That(scores[CombatMove.PierceAttack], Is.EqualTo(40));
            Assert.That(scores[CombatMove.TurnLeft], Is.EqualTo(25));
            Assert.That(scores[CombatMove.TurnRight], Is.EqualTo(60));
            Assert.That(scores[CombatMove.Attack], Is.EqualTo(25));
        }

        [Test]
        public void UnifiedPick_RanksTheTopThree_AndMixesInMistakes()
        {
            var c = Context(Orc, Sword, Situation(250, FighterAction.Parade));
            // Ranked: Pierce 75 (weight 3), TurnLeft 60 (1), TurnRight 60 (1); mistake pool: Fwd 15 (E = 15).
            Assert.That(Selector(new ScriptedRandom(new[] { 0 }, new[] { 0.9f })).SelectUnified(c), Is.EqualTo(CombatMove.PierceAttack));
            Assert.That(Selector(new ScriptedRandom(new[] { 3 }, new[] { 0.9f })).SelectUnified(c), Is.EqualTo(CombatMove.TurnLeft));
            Assert.That(Selector(new ScriptedRandom(new[] { 4 }, new[] { 0.9f })).SelectUnified(c), Is.EqualTo(CombatMove.TurnRight));
            Assert.That(Selector(new ScriptedRandom(new[] { 0 }, new[] { 0.2f })).SelectUnified(c), Is.EqualTo(CombatMove.Fwd), "0.2 < 15/55: a mistake");
        }

        // ------------------------------------------------------------------------------------------- monsters, vanilla, passive

        [Test]
        public void Monsters_ChargeASpammingPlayer_DodgeOnTheAttackPhase_AndBite()
        {
            var sel = Selector();
            var spam = Situation(600);
            spam.PlayerAttackPressure = 150;
            Assert.That(sel.SelectActive(Context(Sabertooth, CombatLoadout.Fists, spam)), Is.EqualTo(CombatMove.Move), "sprint at the player");
            spam.Distance = 300;
            Assert.That(sel.SelectActive(Context(Sabertooth, CombatLoadout.Fists, spam)), Is.EqualTo(CombatMove.PowerAttack));
            Assert.That(sel.SelectActive(Context(Sabertooth, CombatLoadout.Fists, spam, current: CombatMove.Move)), Is.EqualTo(CombatMove.SprintAttack));

            var swing = Situation(330, FighterAction.PowerAttack);
            swing.TargetPhase = MovePhase.Hit;
            Assert.That(sel.SelectActive(Context(Sabertooth, CombatLoadout.Fists, swing)), Is.EqualTo(CombatMove.Fwd), "beyond 380 − 180 + 120");
            swing.Distance = 250;
            Assert.That(sel.SelectActive(Context(Sabertooth, CombatLoadout.Fists, swing)), Is.EqualTo(CombatMove.QuickAttack), "in own reach: bite first");
            Assert.That(sel.SelectActive(Context(Sabertooth, CombatLoadout.Fists, swing, attackReady: false)), Is.EqualTo(CombatMove.JumpBack),
                        "within 380 − 180 + 75");

            Assert.That(sel.SelectActive(Context(Wolf, CombatLoadout.Fists, Situation(250))), Is.EqualTo(CombatMove.Attack), "wolves have no quick attack");
            Assert.That(sel.SelectActive(Context(Wolf, CombatLoadout.Fists, Situation(600))), Is.EqualTo(CombatMove.Fwd), "wolves have no flank run");
        }

        [Test]
        public void Vanilla_SkillAndScores_GargoylesKeepTheVanillaPick()
        {
            var sel = Selector();
            Assert.That(sel.VanillaSkill(new NpcCombatDef(0, 30, "Human", true)), Is.EqualTo(30));
            Assert.That(sel.VanillaSkill(new NpcCombatDef(0, 30, "Zombie", false)), Is.EqualTo(85), "zombies fixed at 85");
            Assert.That(Selector(options: new RuleOptions(difficulty: Difficulty.Hard)).VanillaSkill(new NpcCombatDef(0, 30, "Human", true)), Is.EqualTo(50));
            Assert.That(sel.VanillaScore(CombatMove.PowerAttack, Context(Orc, Sword, Situation(250, FighterAction.Parade))), Is.EqualTo(80));
            Assert.That(sel.VanillaScore(CombatMove.Attack, Context(Orc, Sword, Situation(250))), Is.EqualTo(80));
            Assert.That(sel.VanillaScore(CombatMove.QuickAttack, Context(Orc, Sword, Situation(250))), Is.EqualTo(55));

            // A Gargoyle in reach: the vanilla offensive pick between Attack (80) and PowerAttack (55); the skill roll (0 < 30) takes the best.
            Assert.That(Selector(new ScriptedRandom(new[] { 0, 0 })).SelectActive(Context(Gargoyle, CombatLoadout.Fists, Situation(250))), Is.EqualTo(CombatMove.Attack));
        }

        [Test]
        public void Passive_KeepsItsDistance_AndAnswersAttacksOnlyWithTheFix()
        {
            var sel = Selector(new ScriptedRandom(new[] { 0 }));
            Assert.That(sel.SelectPassive(Context(Orc, Sword, Situation(300))), Is.EqualTo(CombatMove.Back));
            Assert.That(sel.SelectPassive(Context(Orc, Sword, Situation(500))), Is.EqualTo(CombatMove.Fwd));
            Assert.That(sel.SelectPassive(Context(Orc, Sword, Situation(400))), Is.EqualTo(CombatMove.TurnLeft));

            var attacked = Context(Orc, SwordAndShield, Situation(300, FighterAction.Attack));
            var cp = new RuleOptions(fixPassiveExtendedAi: false);
            Assert.That(Selector(options: cp).SelectPassive(attacked), Is.EqualTo(CombatMove.Back), "CP 1.75: the extended-AI pick returns nothing");
            // With the fix: DuelThreatened, light attacks need own reach (295 < 300) → TurnLeft 65, TurnRight 65, Parade 65.
            Assert.That(Selector(new ScriptedRandom(new[] { 6 }, new[] { 0.9f })).SelectPassive(attacked), Is.EqualTo(CombatMove.Parade));
        }

        // ------------------------------------------------------------------------------------------- attack token

        [Test]
        public void Token_OneHolderVsNpcs_OneTwoThreeVsThePlayer()
        {
            var t = new CombatCoordinator(Ai.Token, RuleOptions.Default);
            Assert.IsTrue(t.Update(Self, Bandit, 300, 0, 0));
            Assert.IsFalse(t.Update(Other, Bandit, 280, 500, 0), "only one holder against an NPC");

            Assert.That(t.MaxHolders(ActorId.Player), Is.EqualTo(2));
            Assert.That(new CombatCoordinator(Ai.Token, new RuleOptions(difficulty: Difficulty.Easy)).MaxHolders(ActorId.Player), Is.EqualTo(1));
            Assert.That(new CombatCoordinator(Ai.Token, new RuleOptions(difficulty: Difficulty.Hard)).MaxHolders(ActorId.Player), Is.EqualTo(3));
            Assert.That(new CombatCoordinator(Ai.Token, new RuleOptions(false, false, difficulty: Difficulty.Hard)).MaxHolders(ActorId.Player),
                        Is.EqualTo(1), "without AB and extended AI");
        }

        [Test]
        public void Token_ACloserFighterAwayFromTheHolderTakesIt_LeavingFreesIt()
        {
            var t = new CombatCoordinator(Ai.Token, RuleOptions.Default);
            t.Update(Self, Bandit, 400, 0, 0);
            Assert.IsFalse(t.Update(Other, Bandit, 350, 300, 0), "only 50 closer");
            Assert.IsFalse(t.Update(Other, Bandit, 200, 50, 0), "standing next to the holder");
            Assert.IsTrue(t.Update(Other, Bandit, 200, 300, 0), "> 100 closer and > 75 away from the holder");
            Assert.IsFalse(t.Holds(Self));
            t.Leave(Other);
            Assert.IsTrue(t.Update(Self, Bandit, 400, 0, 0));
            Assert.That(t.FightersOn(Bandit), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------- brain

        [Test]
        public void Brain_ChasesFiveSeconds_WaitsForFlee_ThenGivesUpBeyond2000()
        {
            var brain = Brain(Orc, Sword);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            var order = brain.Tick(Situation(1000, now: 0));
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Fight));
            Assert.That(order.Move, Is.EqualTo(CombatMove.Fwd));
            for (int t = 1; t <= 5; t++) Assert.That(brain.Tick(Situation(1000, now: t)).Intent, Is.EqualTo(CombatIntent.Fight));
            order = brain.Tick(Situation(1000, now: 6));
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.WaitForFlee));
            Assert.That(order.Shout, Is.EqualTo(NpcCombatBrain.ShoutStandStill));
            Assert.IsTrue(brain.IsAttacking, "still in the fight while waiting");

            Assert.That(brain.Tick(Situation(1500, now: 6.6)).Intent, Is.EqualTo(CombatIntent.WaitForFlee), "still getting away");
            order = brain.Tick(Situation(2100, now: 7.2));
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Holster));
            Assert.IsFalse(brain.IsFighting);
        }

        [Test]
        public void Brain_ShotFromBeyond2000_ComesForTheShooter_GivesUpOnlyAfterTheChase()
        {
            // G3: no distance gate on a hit; the 2000 give-up comes from ZS_Attack_WaitForFlee after the chase time (combat-6).
            var brain = Brain(Orc, Sword);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            var order = brain.Tick(Situation(3000, now: 0));
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Fight));
            Assert.That(order.Move, Is.EqualTo(CombatMove.Fwd), "runs at the shooter");
            for (int t = 1; t <= 5; t++) Assert.That(brain.Tick(Situation(2800, now: t)).Intent, Is.EqualTo(CombatIntent.Fight));
            Assert.That(brain.Tick(Situation(2800, now: 6)).Intent, Is.EqualTo(CombatIntent.WaitForFlee));
            Assert.That(brain.Tick(Situation(2800, now: 6.6)).Intent, Is.EqualTo(CombatIntent.Holster), "beyond 2000 after the chase: gives up");
        }

        [Test]
        public void Brain_ResumesTheFight_WhenTheTargetStopsFleeing()
        {
            var brain = Brain(Orc, Sword);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            for (int t = 0; t <= 6; t++) brain.Tick(Situation(1000, now: t));
            Assert.That(brain.State.Phase, Is.EqualTo(CombatPhase.WaitForFlee));
            Assert.That(brain.Tick(Situation(900, now: 6.6)).Intent, Is.EqualTo(CombatIntent.Fight));
            Assert.That(brain.State.Phase, Is.EqualTo(CombatPhase.Fight));
        }

        [Test]
        public void Brain_UnreachableTarget_Retreats_ThenGivesUp_AndLeavesTheTargetAlone()
        {
            // fix-142: no way into melee reach (the ogre on the ravine's rim, the hero below with a bow): not WaitForFlee's
            // endless stand, but a retreat that ends the fight as a give-up; hits and perception do not restart it.
            var brain = Brain(Orc, Sword);
            ActorId gaveUpOn = default;
            brain.GaveUp = (t, _) => gaveUpOn = t;
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            Assert.That(brain.Tick(Situation(800, now: 0)).Move, Is.EqualTo(CombatMove.Fwd));
            var cut = Situation(800, now: 2);
            cut.TargetUnreachable = true;
            var order = brain.Tick(cut);
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Flee));
            Assert.That(order.Shout, Is.EqualTo(NpcCombatBrain.ShoutStandStill), "a humanoid says the WaitForFlee line");
            Assert.IsTrue(brain.Retreating);
            Assert.IsFalse(brain.IsAttacking);

            brain.Engage(ActorId.Player, AttackReason.ReactToDamage, 3); // an arrow from up there
            Assert.That(brain.State.Phase, Is.EqualTo(CombatPhase.Flee), "a hit does not bring it back to the edge");
            Assert.That(brain.Tick(Situation(1200, now: 4)).Intent, Is.EqualTo(CombatIntent.Flee));
            var away = Situation(1500, now: 6);
            away.ReachedFleePoint = true;
            Assert.That(brain.Tick(away).Intent, Is.EqualTo(CombatIntent.Holster));
            Assert.That(gaveUpOn, Is.EqualTo(ActorId.Player), "ends as CancelAttack (FightGivenUp)");
            Assert.IsFalse(brain.IsFighting);

            brain.Engage(ActorId.Player, AttackReason.Enemy, 10);
            Assert.IsFalse(brain.IsFighting, "perception leaves the unreachable target alone for a while");
            Assert.IsTrue(brain.IgnoresUnreachable(ActorId.Player, 10));
            gaveUpOn = default;
            brain.Engage(ActorId.Player, AttackReason.ReactToDamage, 11); // shot again from up there
            Assert.IsTrue(brain.Retreating, "a hit makes it back off again, it does not stand under the arrows");
            Assert.That(brain.Tick(Situation(1300, now: 11.5)).Intent, Is.EqualTo(CombatIntent.Flee));
            Assert.That(brain.Tick(Situation(2100, now: 13)).Intent, Is.EqualTo(CombatIntent.Holster), "beyond 2000: over");
            Assert.That(gaveUpOn, Is.EqualTo(ActorId.Player));
            brain.Engage(ActorId.Player, AttackReason.Enemy, 11 + NpcCombatBrain.UnreachableMemorySeconds + 1);
            Assert.IsTrue(brain.IsAttacking, "then it may come again");
        }

        [Test]
        public void Brain_UnreachableTarget_OrdersAndTheArenaStillFight()
        {
            var brain = Brain(Orc, Sword);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            var cut = Situation(800, now: 1);
            cut.TargetUnreachable = true;
            brain.Tick(cut);
            Assert.IsTrue(brain.Retreating);
            brain.Engage(ActorId.Player, AttackReason.PlayerCommand, 2);
            Assert.IsTrue(brain.IsAttacking, "an order is no perception");

            var arena = Brain(Orc, Sword);
            arena.Engage(ActorId.Player, AttackReason.Arena, 0);
            var a = Situation(800, now: 1);
            a.TargetUnreachable = true;
            a.Arena = true;
            Assert.That(arena.Tick(a).Intent, Is.EqualTo(CombatIntent.Fight), "the arena fight goes on");
        }

        [Test]
        public void Brain_FleesOnPanic_AndSpeciesThatCannotFightAlwaysFlee()
        {
            var deer = Brain(Deer, CombatLoadout.Fists);
            deer.Engage(ActorId.Player, AttackReason.ReactToDamage, 0);
            var order = deer.Tick(Situation(300));
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Flee));
            Assert.That(order.Shout, Is.Null, "animals do not shout");
            Assert.IsFalse(deer.IsAttacking);
            var reached = Situation(900, now: 3);
            reached.ReachedFleePoint = true;
            Assert.That(deer.Tick(reached).Intent, Is.EqualTo(CombatIntent.Holster));

            var human = Brain(new NpcCombatDef(0, 10, "Human", true), Sword);
            human.Engage(ActorId.Player, AttackReason.ReactToDamage, 0);
            var panic = Situation(300);
            panic.AttitudeToTarget = Attitude.Panic;
            Assert.That(human.Tick(panic).Shout, Is.EqualTo(NpcCombatBrain.ShoutRunAway));
            Assert.That(human.Tick(Situation(2500, now: 1)).Intent, Is.EqualTo(CombatIntent.Holster), "the pursuer is beyond 2000");
        }

        [Test]
        public void System_PreyHitOrEngaged_FleesAtOnce_NeverFights()
        {
            // fix-46 (r28 main_02 / hammerclan_01 "Deer fights Xardas / Mort"): G3 AssessTarget gives ZS_Flee, not ZS_Attack,
            // to a victim in Panic toward its attacker or one that cannot attack; ours engaged it for a brain tick (longer
            // while frozen) and the runner saw a deer attacking the guide.
            var deer = new ActorId("Deer_01");
            var deer2 = new ActorId("Deer_02");
            var xardas = new ActorId("Xardas");
            var orc = new ActorId("Orc_01");
            var events = new EventBus();
            var combat = new NpcCombatSystem(Ai, Rules, RuleOptions.Default, new SeededRandom(1),
                                             id => id.Name.StartsWith("Deer") ? Deer : Orc, null, () => 0, () => 0, events);
            combat.AttitudeOf = (self, other) => self == deer && other == xardas ? Attitude.Panic : Attitude.Neutral;

            events.Publish(new HitResolved(xardas, deer, Hit(10)));
            var b = combat.Get(deer);
            Assert.That(b.State.Phase, Is.EqualTo(CombatPhase.Flee), "hit by the one it panics at: flees");
            Assert.That(b.State.Target, Is.EqualTo(xardas));
            Assert.IsFalse(b.IsAttacking);

            Assert.IsTrue(combat.Engage(deer2, orc, AttackReason.Enemy));
            Assert.That(combat.Get(deer2).State.Phase, Is.EqualTo(CombatPhase.Flee), "a species that cannot attack (CannotAttack) flees");

            events.Publish(new HitResolved(xardas, orc, Hit(10)));
            Assert.That(combat.Get(orc).State.Phase, Is.EqualTo(CombatPhase.Fight), "a fighter still fights back");
            combat.Dispose();
        }

        static HitOutcome Hit(int damage)
        {
            var o = HitOutcome.Ignored(HitGate.None);
            typeof(HitOutcome).GetProperty(nameof(HitOutcome.Result)).SetValue(o, HitResult.Hit);
            typeof(HitOutcome).GetProperty(nameof(HitOutcome.Damage)).SetValue(o, damage);
            return o;
        }

        [Test]
        public void Brain_TheHolderFinishesADownedEnemy_WhenTheReasonIsToKill()
        {
            var brain = Brain(Orc, Axe);
            brain.Engage(Bandit, AttackReason.Enemy, 0);
            var s = Situation(250);
            s.TargetIsPlayer = false;
            brain.Tick(s);
            Assert.IsTrue(brain.HoldsToken);

            s.Now = 1;
            s.TargetState = VitalState.Down;
            var order = brain.Tick(s);
            Assert.That(order.Move, Is.EqualTo(CombatMove.FinishingAttack));
            Assert.That(order.Shout, Is.EqualTo(CombatLines.FinishingMove), "a humanoid shouts as the move starts");
            Assert.That(brain.State.Phase, Is.EqualTo(CombatPhase.Finishing));
            s.Now = 1.5;
            order = brain.Tick(s);
            Assert.That(order.Move, Is.EqualTo(CombatMove.FinishingAttack));
            Assert.That(order.Shout, Is.Null, "only once");
            brain.OnMoveFinished(2);
            s.Now = 2;
            s.TargetState = VitalState.Dead;
            order = brain.Tick(s);
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Holster));
            Assert.That(order.Shout, Does.StartWith("Victory_"), "StartSayAttackEnd: a dead humanoid target");
        }

        [Test]
        public void System_ARunningFinisherKeepsItsVictimDown()
        {
            double now = 0;
            var sys = new NpcCombatSystem(Ai, Rules, RuleOptions.Default, new SeededRandom(5), id => id == Self ? Orc : null, _ => Axe,
                                          () => now, () => 0, null);
            sys.Engage(Self, Bandit, AttackReason.Enemy);
            var brain = sys.Get(Self);
            var s = Situation(250);
            s.TargetIsPlayer = false;
            brain.Tick(s);
            Assert.That(sys.IsBeingFinished(Bandit), Is.False, "fighting, not finishing");
            now = s.Now = 1;
            s.TargetState = VitalState.Down;
            Assert.That(brain.Tick(s).Move, Is.EqualTo(CombatMove.FinishingAttack));
            Assert.That(sys.IsBeingFinished(Bandit), Is.True);
            Assert.That(sys.IsBeingFinished(Other), Is.False);
            now = 1 + NpcCombatSystem.FinishingHoldSeconds + 0.1;
            Assert.That(sys.IsBeingFinished(Bandit), Is.False, "capped: a finisher that never lands lets the victim up");
            now = 2;
            brain.OnMoveFinished(2);
            Assert.That(sys.IsBeingFinished(Bandit), Is.False, "the move is over");
        }

        [Test]
        public void Brain_OtherReasonsEndInRansacking_FriendsDoNotLootThePlayer()
        {
            var brain = Brain(Orc, Axe);
            brain.Engage(Bandit, AttackReason.ReactToDamage, 0);
            var s = Situation(250);
            s.TargetIsPlayer = false;
            brain.Tick(s);
            s.Now = 1;
            s.TargetState = VitalState.Down;
            var order = brain.Tick(s);
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Ransack));
            Assert.That(order.Shout, Is.EqualTo("NeverHitMeAgain"), "StartSayAttackEnd: ReactToDamage, the target down");
            s.Now = 2;
            Assert.That(brain.Tick(s).Intent, Is.EqualTo(CombatIntent.Ransack), "until the body is done");
            brain.OnMoveFinished(3);
            s.Now = 3;
            Assert.That(brain.Tick(s).Intent, Is.EqualTo(CombatIntent.Holster));

            var friend = Brain(new NpcCombatDef(5, 20, "Human", true, friendType: true, role: NpcRole.Friend), Sword);
            friend.Engage(ActorId.Player, AttackReason.Duel, 0);
            friend.Tick(Situation(250));
            var down = Situation(250, now: 1);
            down.TargetState = VitalState.Down;
            Assert.That(friend.Tick(down).Intent, Is.EqualTo(CombatIntent.Holster));
        }

        [Test]
        public void Brain_AnimalsNeverRansackTheDownedHero()
        {
            var wolf = Brain(Wolf, CombatLoadout.From(new ItemDef[0]));
            wolf.Engage(ActorId.Player, AttackReason.Enemy, 0);
            wolf.Tick(Situation(250));
            var down = Situation(250, now: 1);
            down.TargetState = VitalState.Down;
            down.TargetKillable = false; // a protected hero (Easy/Medium, level < 3) is only knocked out
            var order = wolf.Tick(down);
            Assert.That(order.Intent, Is.EqualTo(CombatIntent.Holster), "G3 WantToRansack needs a humanoid looter");
            Assert.That(wolf.State.Phase, Is.Not.EqualTo(CombatPhase.Ransacking));

            var mage = Brain(new NpcCombatDef(5, 20, "Human", true, @class: NpcClass.Mage), Sword);
            mage.Engage(Bandit, AttackReason.ReactToDamage, 0);
            var s = Situation(250);
            s.TargetIsPlayer = false;
            mage.Tick(s);
            s.Now = 1;
            s.TargetState = VitalState.Down;
            Assert.That(mage.Tick(s).Intent, Is.EqualTo(CombatIntent.Holster), "mages loot nobody");
        }

        [Test]
        public void Brain_ShootsFromAfar_WithoutTheHitCooldown()
        {
            var archer = CombatLoadout.From(new[] { Item("It_1H_Sword_01", "OneHanded"), Item("It_Bow_01", "Bow"), Item("It_Arrow", "Arrow") });
            Assert.IsTrue(archer.CanShoot);
            var brain = Brain(Orc, archer);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            var order = brain.Tick(Situation(1500, now: 0, gameNow: 0));
            Assert.That(order.Weapon, Is.EqualTo(WeaponMode.Ranged));
            Assert.That(order.Move, Is.EqualTo(CombatMove.Shoot));
            brain.OnMoveFinished(1);
            brain.OnHitLanded(12);
            // FAI_Ranged has no CanHit: the next shot follows at once (fix-47, audit #6).
            Assert.That(brain.Tick(Situation(1500, now: 1, gameNow: 15)).Move, Is.EqualTo(CombatMove.Shoot), "no hit cooldown for shots");
            Assert.That(brain.State.LastAttackEnd, Is.EqualTo(double.NegativeInfinity), "ZS_Attack_Shoot does not call SetLastHit");
            Assert.That(brain.Tick(Situation(1500, now: 30, gameNow: 360)).Intent, Is.EqualTo(CombatIntent.Fight), "no chase while in shooting range");
        }

        [Test]
        public void Brain_ParadeIsUpAfter0_3Seconds_AndDropsAfter2Seconds()
        {
            var coordinator = new CombatCoordinator(Ai.Token, RuleOptions.Default);
            coordinator.Update(Other, ActorId.Player, 100, 500, 500);
            coordinator.Update(new ActorId("Third"), ActorId.Player, 100, -500, 500);
            var brain = Brain(Orc, SwordAndShield, coordinator, new ScriptedRandom(new[] { 6 }, new[] { 0.9f }));
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            var order = brain.Tick(Situation(300, FighterAction.Attack, now: 10));
            Assert.IsFalse(brain.HoldsToken);
            Assert.That(order.Move, Is.EqualTo(CombatMove.Parade));
            Assert.IsFalse(brain.IsParrying(10.2));
            Assert.IsTrue(brain.IsParrying(10.35));
            Assert.That(brain.Tick(Situation(300, FighterAction.Attack, now: 11)).Move, Is.EqualTo(CombatMove.Parade), "held");
            Assert.That(brain.Tick(Situation(300, FighterAction.Attack, now: 12.1)).Move, Is.Not.EqualTo(CombatMove.Parade), "no parade twice in a row");
            Assert.That(brain.State.MoveStart, Is.EqualTo(12.1).Within(1e-6), "a new move started after 2 s");
        }

        [Test]
        public void Brain_EngagingTheSameTargetOnlyEscalatesTheReason()
        {
            var brain = Brain(Orc, Sword);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            brain.Engage(ActorId.Player, AttackReason.ReactToDamage, 1);
            Assert.That(brain.State.Reason, Is.EqualTo(AttackReason.Enemy));
            brain.Engage(ActorId.Player, AttackReason.Murder, 2);
            Assert.That(brain.State.Reason, Is.EqualTo(AttackReason.Murder));
        }

        // ------------------------------------------------------------------------------------------- species that never fight

        /// <summary>Attitudes come from the AttitudeService (CrimeTests); the combat rules only say who never fights.</summary>
        [Test]
        public void NeverFights_AmbientCreaturesAndPeacefulSpecies()
        {
            Assert.IsTrue(Ai.NeverFights(Deer));
            Assert.IsFalse(Ai.NeverFights(Wolf));
            Assert.IsFalse(Ai.NeverFights(Orc));
        }

        // ------------------------------------------------------------------------------------------- system and session

        static NpcCombatDef NpcDefOf(GameSession session, ActorId npc)
        {
            session.Actors.TryGet(npc, out var actor);
            return CombatWiring.CombatDefOf(actor.Definition);
        }

        [Test]
        public void System_AVictimFightsBack_TheDefeatedLeave()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                session.Events.Publish(new HitResolved(ActorId.Player, gnar, HitOutcome.Ignored(HitGate.None)));
                Assert.That(session.NpcCombat.Get(gnar), Is.Null, "an ignored hit is no attack");

                session.Combat.Strike(new HitContext
                {
                    Attacker = session.Combat.PlayerCombatant(session.Hero),
                    Victim = session.Combat.NpcCombatant(gnar, NpcDefOf(session, gnar), 1),
                    Delivery = HitDelivery.Melee, DamageType = DamageType.Blade, DamageAmount = 5, PlayerLevel = 1,
                }, byPlayerParty: true);
                var brain = session.NpcCombat.Get(gnar);
                Assert.That(brain, Is.Not.Null);
                Assert.That(brain.State.Target, Is.EqualTo(ActorId.Player));
                Assert.That(brain.State.Reason, Is.EqualTo(AttackReason.ReactToDamage));

                session.Combat.Vitals.KnockOut(gnar, byPlayerParty: true);
                Assert.IsFalse(brain.IsFighting);

                // The knock-out hit itself: ActorDefeated comes before HitResolved, and the downed victim stays out.
                session.Combat.Vitals.Tick(60f); // gets up
                session.Combat.Vitals.RestoreFully(gnar);
                var outcome = session.Combat.Strike(new HitContext
                {
                    Attacker = session.Combat.PlayerCombatant(session.Hero),
                    Victim = session.Combat.NpcCombatant(gnar, NpcDefOf(session, gnar), 1),
                    Delivery = HitDelivery.Melee, DamageType = DamageType.Blade, DamageAmount = 100000, PlayerLevel = 1,
                    AttackerToVictim = Attitude.Neutral, VictimToAttacker = Attitude.Neutral,
                }, byPlayerParty: true);
                Assert.IsTrue(outcome.KnockedOut, outcome.ToString());
                Assert.IsFalse(brain.IsFighting, "a knocked-out victim does not engage its attacker");
            }
        }

        [Test]
        public void ReactionToHit_AFriendComplainsFirst_ThePartyBondNeverFights()
        {
            Assert.That(NpcCombatSystem.ReactionToHit(Attitude.Neutral, false, false), Is.EqualTo(NpcCombatSystem.HitResponse.FightBack));
            Assert.That(NpcCombatSystem.ReactionToHit(Attitude.Friendly, false, false), Is.EqualTo(NpcCombatSystem.HitResponse.Complain),
                        "G3 AssessAgressor: Routine + Friendly -> ZS_ReactToDamage");
            Assert.That(NpcCombatSystem.ReactionToHit(Attitude.Friendly, true, false), Is.EqualTo(NpcCombatSystem.HitResponse.FightBack),
                        "hit again while complaining (AIMode Reaction) -> AssessTarget");
            Assert.That(NpcCombatSystem.ReactionToHit(Attitude.Friendly, true, true), Is.EqualTo(NpcCombatSystem.HitResponse.Complain),
                        "the guide / a companion forgives");
        }

        [Test]
        public void Session_TheGuideHitByTheHero_ComplainsAndDoesNotFight()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                session.Party.Members.StartGuide(gnar, "FP_Somewhere", "Some_Guide"); // editor-runner-26 hammerclan_04: Mort
                var reacted = new List<NpcReacted>();
                session.Events.Subscribe<NpcReacted>(reacted.Add);
                for (int i = 0; i < 3; i++)
                {
                    session.Combat.Strike(new HitContext
                    {
                        Attacker = session.Combat.PlayerCombatant(session.Hero),
                        Victim = session.Combat.NpcCombatant(gnar, NpcDefOf(session, gnar), 1),
                        Delivery = HitDelivery.Melee, DamageType = DamageType.Blade, DamageAmount = 5, PlayerLevel = 1,
                        AttackerToVictim = Attitude.Friendly, VictimToAttacker = Attitude.Friendly,
                    }, byPlayerParty: true);
                }
                Assert.That(session.NpcCombat.Get(gnar)?.IsFighting ?? false, Is.False, "no ReactToDamage fight against the hero it leads");
                Assert.That(reacted.Count(r => r.Npc == gnar && r.Reaction.Line == NpcCombatSystem.ComplainLine), Is.EqualTo(1),
                            "one complaint (ZS_ReactToDamage) for the hits within its 10 s");

                session.Party.Members.StopGuide();
                session.NpcCombat.Engage(gnar, ActorId.Player, AttackReason.ReactToDamage);
                session.Party.Members.StartGuide(gnar, "FP_Somewhere", "Some_Guide");
                session.Crime.Crime.EndWarningFight(gnar);
                Assert.IsFalse(session.NpcCombat.Get(gnar).IsFighting, "a grudge fight it already had ends once it leads the hero");
            }
        }

        [Test]
        public void Session_ACompanionHitByTheHero_TakesNothing_OnceOrRepeatedly_AndNeverFights()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gorn = SessionFixture.Npc;
                session.Party.JoinCompanion(gorn);
                var reacted = new List<NpcReacted>();
                var resolved = new List<HitResolved>();
                var broadcasts = new List<PerceptionBroadcast>();
                session.Events.Subscribe<NpcReacted>(reacted.Add);
                session.Events.Subscribe<HitResolved>(resolved.Add);
                session.Events.Subscribe<PerceptionBroadcast>(broadcasts.Add);
                int hp = session.Combat.Vitals.HitPoints(gorn);
                var actions = new[] { CombatAction.Attack, CombatAction.PowerAttack, CombatAction.WhirlAttack, CombatAction.QuickAttack };
                for (int i = 0; i < 12; i++)
                {
                    var hero = session.Combat.PlayerCombatant(session.Hero);
                    hero.Action = actions[i % actions.Length];
                    var delivery = i % 3 == 0 ? HitDelivery.Missile : i % 3 == 1 ? HitDelivery.Melee : HitDelivery.Spell;
                    var outcome = session.Combat.Strike(new HitContext
                    {
                        Attacker = hero,
                        Victim = session.Combat.NpcCombatant(gorn, NpcDefOf(session, gorn), 1),
                        Delivery = delivery, AreaSpell = delivery == HitDelivery.Spell, DamageType = DamageType.Blade,
                        DamageAmount = 50, PlayerLevel = 1, AttackerToVictim = Attitude.Friendly, VictimToAttacker = Attitude.Friendly,
                        VictimInAttackersParty = session.Party.Members.IsLedByPlayer(gorn),
                        AttackerTargetsVictim = delivery == HitDelivery.Melee, // the hero's melee focus is on him
                    }, byPlayerParty: true);
                    Assert.That(outcome.Gate, Is.EqualTo(HitGate.PartyMember), $"hit {i} ({delivery})");
                }
                Assert.That(session.Combat.Vitals.HitPoints(gorn), Is.EqualTo(hp), "no damage");
                Assert.That(resolved, Is.Empty, "no hit lands: no stumble, no fight sound");
                Assert.That(broadcasts, Is.Empty);
                Assert.That(reacted.Where(r => r.Npc == gorn), Is.Empty, "no complaint either");
                Assert.That(session.NpcCombat.Get(gorn)?.IsFighting ?? false, Is.False, "no fight against the hero");
            }
        }

        [Test]
        public void Session_CannotSaveWhileAnNpcAttacksThePlayer_NpcFightsAreSaved()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                session.NpcCombat.Engage(gnar, ActorId.Player, AttackReason.Enemy);
                Assert.That(session.SaveBlockReason, Is.EqualTo(CombatWiring.BlockedByCombat));
                session.NpcCombat.Get(gnar).Disengage();
                Assert.That(session.SaveBlockReason, Is.Null);

                session.NpcCombat.Engage(gnar, Bandit, AttackReason.Murder);
                Assert.That(session.SaveBlockReason, Is.Null, "a fight between NPCs does not block saving");
                var loaded = f.Reload(session);
                var brain = loaded.NpcCombat.Get(gnar);
                Assert.That(brain, Is.Not.Null);
                Assert.That(brain.State.Phase, Is.EqualTo(CombatPhase.Fight));
                Assert.That(brain.State.Target, Is.EqualTo(Bandit));
                Assert.That(brain.State.Reason, Is.EqualTo(AttackReason.Murder));
                Assert.That(SessionFixture.Save(loaded), Is.EqualTo(SessionFixture.Save(f.Reload(loaded))));
            }
        }

        [Test]
        public void TakenWeapon_LeavesTheLoadout_AndIsSaved()
        {
            var sword = Item("It_1H_Sword_01", "OneHanded");
            var withShield = CombatLoadout.From(new[] { sword, Item("It_Shield_01", "Shield") });
            var without = withShield.Without(new HashSet<string> { "It_1H_Sword_01" });
            Assert.That(without.MeleeItem, Is.Null);
            Assert.That(without.Melee, Is.EqualTo(WeaponClass.Fist));
            Assert.IsTrue(without.Shield, "the shield stays");

            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                session.NpcCombat.Engage(gnar, Bandit, AttackReason.Murder);
                session.NpcCombat.TakeItem(gnar, "It_Axe_OrcWaraxe_01");
                var loaded = f.Reload(session);
                Assert.That(loaded.NpcCombat.LostItems.Single().Value, Is.EquivalentTo(new[] { "It_Axe_OrcWaraxe_01" }));
                Assert.That(loaded.NpcCombat.LoadoutOf(gnar).MeleeItem?.Id, Is.Not.EqualTo("It_Axe_OrcWaraxe_01"));
                Assert.That(SessionFixture.Save(loaded), Is.EqualTo(SessionFixture.Save(f.Reload(loaded))));
            }
        }

        // ------------------------------------------------------------------------------------------- fix-47 (combat audit, NPC side)

        static readonly CombatLoadout DualSwords =
            CombatLoadout.From(new[] { Item("It_1H_Sword_01", "OneHanded"), Item("It_1H_Sword_01", "OneHanded") }); // EquipWeaponry: a pair of one blade

        /// <summary>Ticks a token holder in reach until it starts a melee attack; returns that tick's game time.</summary>
        static double TickUntilAttack(NpcCombatBrain brain, ref double now, double gameNow)
        {
            for (int i = 0; i < 200; i++, now += 0.7)
            {
                var order = brain.Tick(Situation(150, now: now, gameNow: gameNow));
                if (order.Move.IsMeleeAttack()) return gameNow;
                brain.OnMoveFinished(now);
            }
            Assert.Fail("no attack in 200 decisions");
            return 0;
        }

        [Test]
        public void AttackClock_RunsFromTheEndOfTheAttack_NotItsStartOrHits()
        {
            var brain = Brain(Orc, Sword);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            double now = 0;
            TickUntilAttack(brain, ref now, 100);
            Assert.That(brain.State.LastAttackEnd, Is.EqualTo(double.NegativeInfinity), "fix-81: ZS_Attack_* calls SetLastHit after its _AI_ command");
            brain.OnHitLanded(103);
            brain.OnHitTaken(104);
            Assert.That(brain.State.LastAttackEnd, Is.EqualTo(double.NegativeInfinity), "landing or taking a hit does not move the clock");
            brain.OnMoveFinished(now); // the swing is over at game second 104
            now += 0.7;
            Assert.IsFalse(brain.Tick(Situation(150, now: now, gameNow: 104)).Move.IsMeleeAttack());
            Assert.That(brain.State.LastAttackEnd, Is.EqualTo(104), "stamped when the move is done");
            brain.OnMoveFinished(now);
            // MinHitDuration 6 game s from the END: no attack before 110, attacks again after.
            for (int i = 0; i < 30; i++)
            {
                now += 0.7;
                var order = brain.Tick(Situation(150, now: now, gameNow: 109));
                Assert.IsFalse(order.Move.IsMeleeAttack(), $"no attack before CanHit(6): {order.Move}");
                brain.OnMoveFinished(now);
            }
            now += 0.7;
            Assert.That(TickUntilAttack(brain, ref now, 110), Is.EqualTo(110));
        }

        [Test]
        public void AttackClock_AnAttackCutShortByAHitReaction_DoesNotStampIt()
        {
            var brain = Brain(Orc, Sword);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            double now = 0;
            TickUntilAttack(brain, ref now, 100);
            brain.OnInterrupted(now); // ZS_Stumble replaces ZS_Attack_*: its SetLastHit step never runs
            brain.OnMoveFinished(now);
            brain.Tick(Situation(150, now: now + 0.1, gameNow: 101));
            Assert.That(brain.State.LastAttackEnd, Is.EqualTo(double.NegativeInfinity));
            brain.OnMoveFinished(now, completed: false);
            Assert.That(brain.State.LastAttackEnd, Is.EqualTo(double.NegativeInfinity), "a move that never started stamps nothing");
        }

        [Test]
        public void ScavengerBites_AtMostOnePerSecond_HitPlusWaitPlusMinHitDuration()
        {
            // fix-81 (owner: "a scavenger bit very rapidly"): Scavenger QuickAttack = Hit 0.40 s, no Raise; _AI_QuickAttack
            // then waits 0.1 s, SetLastHit, and the next attack needs MinHitDuration 6 game s (0.5 s). Was: 0.5 s from the start.
            var scavenger = new NpcCombatDef(0, 10, "Scavenger", humanoid: false, animal: true);
            var brain = Brain(scavenger, CombatLoadout.Fists, random: new SeededRandom(3));
            brain.Engage(ActorId.Player, AttackReason.Angry, 0);
            const double Hit = 0.4, Frame = 0.02;
            var starts = new List<(double at, CombatMove move)>();
            double busyUntil = -1, lastStart = double.NaN;
            for (double now = 0; now < 30; now += Frame)
            {
                if (busyUntil >= 0 && now >= busyUntil)
                {
                    brain.OnMoveFinished(now);
                    busyUntil = -1;
                }
                var order = brain.Tick(Situation(80, now: now, gameNow: now * 12)); // in its own reach (115 − 25)
                if (brain.State.MoveStart.Equals(lastStart)) continue;
                lastStart = brain.State.MoveStart;
                if (order.Move.IsMeleeAttack())
                {
                    starts.Add((now, order.Move));
                    busyUntil = now + Hit;
                }
                else if (brain.State.MoveRunning) busyUntil = now + Frame;
            }
            Assert.That(starts.Count(x => x.move == CombatMove.QuickAttack), Is.GreaterThan(3), "it does bite");
            for (int i = 1; i < starts.Count; i++)
            {
                double wait = starts[i - 1].move == CombatMove.QuickAttack ? NpcCombatBrain.QuickAttackWaitSeconds : 0;
                Assert.That(starts[i].at - starts[i - 1].at, Is.GreaterThanOrEqualTo(Hit + wait + 0.5 - 2 * Frame),
                            $"{starts[i].move} after {starts[i - 1].move}: no earlier than Hit (+ 0.1 s) + MinHitDuration");
            }
        }

        [Test]
        public void AttackClock_TheCastReleaseDoesNotMoveIt()
        {
            var brain = Brain(Orc, Sword);
            brain.OnSpellCast(5, 60);
            Assert.That(brain.State.LastAttackEnd, Is.EqualTo(double.NegativeInfinity), "the release alone stamps nothing (the move's end does)");
            Assert.That(brain.State.LastCastAt, Is.EqualTo(5));
        }

        [Test]
        public void Scores_OneMinus35_ForARecentAttackOrTheCurrentMove_NotBoth()
        {
            var scores = Selector().ScoreCandidates(Context(Orc, Sword, Situation(250, FighterAction.Parade), current: CombatMove.Attack, hitRecently: true), 15)
                                   .ToDictionary(p => p.Key, p => p.Value);
            Assert.That(scores[CombatMove.Attack], Is.EqualTo(25), "60 - 35 once (CP 0x100057d0: attack && !CanHit(30) || current)");
            Assert.That(scores[CombatMove.PierceAttack], Is.EqualTo(40));
            Assert.That(scores[CombatMove.TurnLeft], Is.EqualTo(60));
        }

        [Test]
        public void Passive_CirclesLeftFirst_ThenRight_ThenWaits()
        {
            // These rolls would pick the right turn or the wait under the old 1/3 choice: FAI_Passive rolls nothing.
            var sel = Selector(new ScriptedRandom(new[] { 1, 2, 1, 2 }));
            Assert.That(sel.SelectPassive(Context(Orc, Sword, Situation(400))), Is.EqualTo(CombatMove.TurnLeft));
            var noLeft = Situation(400);
            noLeft.FreeBackLeft = false;
            Assert.That(sel.SelectPassive(Context(Orc, Sword, noLeft)), Is.EqualTo(CombatMove.TurnRight));
            noLeft.FreeBackRight = false;
            Assert.That(sel.SelectPassive(Context(Orc, Sword, noLeft)), Is.EqualTo(CombatMove.Wait));
        }

        [Test]
        public void Brain_TurnsAreFootworkOf0_6Seconds()
        {
            var coordinator = new CombatCoordinator(Ai.Token, RuleOptions.Default);
            coordinator.Update(Other, ActorId.Player, 100, 500, 500);
            coordinator.Update(new ActorId("Third"), ActorId.Player, 100, -500, 500);
            var brain = Brain(Orc, Sword, coordinator);
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            Assert.That(brain.Tick(Situation(400, now: 10)).Move, Is.EqualTo(CombatMove.TurnLeft));
            Assert.IsFalse(brain.HoldsToken);
            Assert.That(brain.State.MoveUntil, Is.EqualTo(10.6).Within(1e-6), "OnAttack_Turn: 0.6 s state time");
            Assert.That(brain.Tick(Situation(400, now: 10.5)).Move, Is.EqualTo(CombatMove.TurnLeft), "still running");
        }

        [Test]
        public void Brain_AHitReactionEndsTheParade()
        {
            var coordinator = new CombatCoordinator(Ai.Token, RuleOptions.Default);
            coordinator.Update(Other, ActorId.Player, 100, 500, 500);
            coordinator.Update(new ActorId("Third"), ActorId.Player, 100, -500, 500);
            var brain = Brain(Orc, SwordAndShield, coordinator, new ScriptedRandom(new[] { 6 }, new[] { 0.9f }));
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            Assert.That(brain.Tick(Situation(300, FighterAction.Attack, now: 10)).Move, Is.EqualTo(CombatMove.Parade));
            Assert.IsTrue(brain.IsParrying(10.4));
            brain.OnInterrupted(10.4);
            Assert.IsFalse(brain.IsParrying(10.5), "no invisible parade after the stumble");
            brain.Tick(Situation(300, FighterAction.None, now: 11));
            Assert.That(brain.State.MoveStart, Is.EqualTo(11).Within(1e-6), "ZS_Attack_Loop picks a new move after the reaction");

            Assert.IsTrue(NpcCombatBrain.Interrupts(HitReaction.ParadeStumble));
            Assert.IsTrue(NpcCombatBrain.Interrupts(HitReaction.QuickStumble));
            Assert.IsTrue(NpcCombatBrain.Interrupts(HitReaction.SitKnockDown));
            Assert.IsFalse(NpcCombatBrain.Interrupts(HitReaction.Flinch), "PipiStumble is no task");
            Assert.IsFalse(NpcCombatBrain.Interrupts(HitReaction.None));
        }

        [Test]
        public void CanParry_ADualWieldersPowerAttack_OnlyWithShieldAndOneHanded()
        {
            var power = Situation(250, FighterAction.PowerAttack);
            power.TargetDualWield = true;
            var sel = Selector();
            Assert.IsFalse(sel.IsAvailable(CombatMove.Parade, Context(Orc, Sword, power)), "CanParadeMoveOf (AAI): needs a shield");
            Assert.IsTrue(sel.IsAvailable(CombatMove.Parade, Context(Orc, SwordAndShield, power)));
            power.TargetDualWield = false;
            Assert.IsTrue(sel.IsAvailable(CombatMove.Parade, Context(Orc, Sword, power)), "one blade: parried as ever");
            power.TargetDualWield = true;
            var vanilla = new RuleOptions(extendedAI: false);
            Assert.IsTrue(Selector(options: vanilla).IsAvailable(CombatMove.Parade, Context(Orc, Sword, power)), "the rule is AAI only");
            Assert.IsTrue(DualSwords.DualWield);
        }

        [Test]
        public void Footwork_CirclesAtTheRadius_LeftIsSelfsLeft()
        {
            // Self at the origin facing +z (target 4 m ahead): its left is -x in Unity's frame.
            CombatFootwork.Side(0, 0, 0, 4, left: true, out float lx, out float lz);
            Assert.That(lx, Is.EqualTo(-1).Within(1e-5));
            Assert.That(lz, Is.EqualTo(0).Within(1e-5));
            CombatFootwork.Side(0, 0, 0, 4, left: false, out float rx, out _);
            Assert.That(rx, Is.EqualTo(1).Within(1e-5));

            float x = 0, z = 0;
            for (int i = 0; i < 36; i++) // 0.6 s at 60 fps, 2.8 m/s
                CombatFootwork.CircleStep(x, z, 0, 4, true, CombatFootwork.CircleSpeed(0) / 60f, out x, out z);
            float r = (float)Math.Sqrt(x * x + (z - 4) * (z - 4));
            Assert.That(r, Is.EqualTo(4).Within(1e-3), "the distance holds");
            Assert.That(x, Is.LessThan(-1.5f), "went round to the left");
            Assert.That(CombatFootwork.CircleSpeed(3.6f), Is.EqualTo(2.8f), "StrafeSpeedMax caps it");
            Assert.That(CombatFootwork.SprintSpeed(0, 4f), Is.EqualTo(5.6f).Within(1e-5));
            Assert.That(CombatFootwork.SprintSpeed(6.2f, 4f), Is.EqualTo(6.2f));
        }

        [Test]
        public void Footwork_TurnSpeedIsG3s()
        {
            Assert.That(CombatFootwork.TurnSpeed(false), Is.EqualTo(200f), "gCCharacterMovement_PS.TurnSpeedMax");
            Assert.That(CombatFootwork.TurnSpeed(true), Is.EqualTo(100f), "TurnSpeedModifier 0.5 sprinting");
        }

        [Test]
        public void StepWatchdog_CutsALoopAfterOneCycle_AndAnyStepAfter10Seconds()
        {
            Assert.That(G3.Player.CombatStepWatchdog.Check(false, true, 0.95f, 0.02f, 0.8f), Is.EqualTo(G3.Player.CombatStepWatchdog.Verdict.LoopWrapped));
            Assert.That(G3.Player.CombatStepWatchdog.Check(false, true, 0.4f, 0.5f, 0.8f), Is.EqualTo(G3.Player.CombatStepWatchdog.Verdict.Running));
            Assert.That(G3.Player.CombatStepWatchdog.Check(false, false, 0.4f, 0.5f, 10.5f), Is.EqualTo(G3.Player.CombatStepWatchdog.Verdict.TimedOut));
            Assert.That(G3.Player.CombatStepWatchdog.Check(true, true, 0.9f, 0.1f, 60f), Is.EqualTo(G3.Player.CombatStepWatchdog.Verdict.Running), "holds are never cut");
        }
    }
}
