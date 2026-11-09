using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// par-npc: NPC-vs-NPC perception, creature reactions (threaten, prey, eating bodies, panic), sleepers, battle shouts,
    /// witness flight and comments, healing after a fight, corpse removal and respawn (CP 1.75 Script_Game).
    /// </summary>
    public class NpcLifeTests
    {
        static readonly PerceptionRules Perception = G3CrimeImporter.Perception();
        static readonly CombatAiRules Ai = TestRules.CombatAi();
        static readonly CombatRules Rules = TestRules.Combat();
        static readonly ActorId Wolf = new ActorId("Wolf#01"), Hunter = new ActorId("Hunter"), Scavenger = new ActorId("Scavenger#01"),
                                Deer = new ActorId("Deer#01"), Orc = new ActorId("Orc#01");

        sealed class FixedRandom : IRandom
        {
            readonly Queue<int> _ints;
            public FixedRandom(params int[] ints) => _ints = new Queue<int>(ints);
            public int Next(int maxExclusive) => (_ints.Count > 0 ? _ints.Dequeue() : 0) % maxExclusive;
            public float NextFloat() => 0;
            public RandomState Save() => default;
            public void Load(RandomState state) { }
        }

        sealed class Wall : ILineOfSight
        {
            public bool IsClear(WorldPose from, WorldPose to) => false;
        }

        static SenseObserver Observer(ActorId id, bool humanoid, float x = 0, float z = 0) =>
            new SenseObserver { Id = id, Pose = new WorldPose(x, 0, z, 0), Humanoid = humanoid };

        static PerceptionCandidate Npc(ActorId id, float x, float z, Attitude attitude, bool body = false, bool moving = false) =>
            new PerceptionCandidate { Target = new SenseTarget { Id = id, Pose = new WorldPose(x, 0, z, 0), Moving = moving }, Attitude = attitude, Body = body };

        static PerceptionCandidate Player(float x, float z, Attitude attitude, bool sneaking = false) =>
            new PerceptionCandidate
            {
                Target = new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(x, 0, z, 0), Sneaking = sneaking }, IsPlayer = true, Attitude = attitude,
            };

        static PerceptionSnapshot Scan(SenseObserver o, params PerceptionCandidate[] c) =>
            PerceptionScan.Scan(o, c, Perception, OpenLineOfSight.Instance, wanted: o.Humanoid ? ActivePerception.HumanoidSlots : ActivePerception.CreatureSlots);

        // ------------------------------------------------------------------------------------------- active perception

        [Test]
        public void Perception_NpcSlotsDriveTheSteps_InG3Order()
        {
            var wolf = Observer(Wolf, humanoid: false);
            var hunter = Observer(Hunter, humanoid: true);

            // A wolf: the hunter is Angry (species table) within 10 m → threaten; farther it is not noticed.
            Assert.That(ActivePerception.Decide(Scan(wolf, Npc(Hunter, 0, 8, Attitude.Angry)), new ActiveObserver()).Step,
                        Is.EqualTo(PerceptionStep.Threaten));
            Assert.That(ActivePerception.Decide(Scan(wolf, Npc(Hunter, 0, 15, Attitude.Angry)), new ActiveObserver()).Step,
                        Is.EqualTo(PerceptionStep.None), "the angry slot reaches 1000");
            // Prey before angry; a prey's body before the prey.
            var d = ActivePerception.Decide(Scan(wolf, Npc(Hunter, 0, 5, Attitude.Angry), Npc(Scavenger, 0, 18, Attitude.Predator)), new ActiveObserver());
            Assert.That((d.Step, d.Target, d.Reason), Is.EqualTo((PerceptionStep.Attack, Scavenger, AttackReason.Prey)));
            d = ActivePerception.Decide(Scan(wolf, Npc(Scavenger, 0, 9, Attitude.Predator, body: true), Npc(Scavenger, 0, 18, Attitude.Predator)),
                                        new ActiveObserver());
            Assert.That(d.Step, Is.EqualTo(PerceptionStep.EatBody), "a predator's body is food");
            // Panic before everything.
            d = ActivePerception.Decide(Scan(Observer(Deer, false), Player(0, 15, Attitude.Panic), Npc(Hunter, 0, 3, Attitude.Panic)),
                                        new ActiveObserver());
            Assert.That((d.Step, d.Target), Is.EqualTo((PerceptionStep.Flee, Hunter)), "the nearest of the panic slot");

            // A hunter (humanoid): a wolf in the hostile slot is attacked (Enemy) before the player's crime steps.
            d = ActivePerception.Decide(Scan(hunter, Npc(Wolf, 0, 18, Attitude.Hostile), Player(0, 3, Attitude.Neutral)),
                                        new ActiveObserver { Humanoid = true });
            Assert.That((d.Step, d.Target, d.Reason), Is.EqualTo((PerceptionStep.Attack, Wolf, AttackReason.Enemy)));
            Assert.That(ActivePerception.Decide(Scan(hunter, Npc(Wolf, 0, -18, Attitude.Hostile)), new ActiveObserver { Humanoid = true }).Step,
                        Is.EqualTo(PerceptionStep.AssessPlayer), "a silent creature behind a humanoid is not noticed");
        }

        [Test]
        public void Perception_HumanoidsSeeOrHear_PlayerStepsGoToTheCrimeCode_PartySkipsHostiles()
        {
            var hunter = Observer(Hunter, humanoid: true);
            var behindStill = PerceptionScan.Scan(hunter, new[] { Npc(Wolf, 0, -5, Attitude.Hostile) }, Perception, new Wall(), wanted: ActivePerception.HumanoidSlots);
            Assert.That(behindStill.Has(PerceptionSlot.Hostile), Is.False, "out of sight and silent");
            var behindMoving = PerceptionScan.Scan(hunter, new[] { Npc(Wolf, 0, -5, Attitude.Hostile, moving: true) }, Perception, new Wall(),
                                                   wanted: ActivePerception.HumanoidSlots);
            Assert.That(behindMoving.Has(PerceptionSlot.Hostile), Is.True, "heard within 1000");

            var hostilePlayer = Scan(hunter, Player(0, 5, Attitude.Hostile));
            Assert.That(ActivePerception.Decide(hostilePlayer, new ActiveObserver { Humanoid = true }).Step, Is.EqualTo(PerceptionStep.AssessPlayer));
            Assert.That(ActivePerception.Decide(Scan(hunter), new ActiveObserver { Humanoid = true }).Step, Is.EqualTo(PerceptionStep.AssessPlayer),
                        "nothing else: the crime steps (warnings, sneaking, talk)");
            var upset = new ActiveObserver { Humanoid = true, UpsetWithPlayer = true, UpsetReason = AttackReason.Theft };
            Assert.That(ActivePerception.Decide(Scan(hunter, Player(0, 5, Attitude.Angry), Npc(Wolf, 0, 6, Attitude.Hostile)), upset).Step,
                        Is.EqualTo(PerceptionStep.AssessPlayer), "the grudge comes before the hostile step");

            var companion = new ActiveObserver { Humanoid = true, InPlayerParty = true };
            Assert.That(ActivePerception.Decide(Scan(hunter, Npc(Wolf, 0, 6, Attitude.Hostile)), companion).Step, Is.EqualTo(PerceptionStep.AssessPlayer),
                        "the player's party leaves hostiles to the party driver");

            // A creature attacks a hostile player itself (a boar), and an upset one with the grudge's reason.
            var boar = Observer(new ActorId("Boar#01"), humanoid: false);
            var p = ActivePerception.Decide(Scan(boar, Player(0, 15, Attitude.Hostile)), new ActiveObserver());
            Assert.That((p.Step, p.Target, p.Reason), Is.EqualTo((PerceptionStep.Attack, ActorId.Player, AttackReason.Enemy)));
            p = ActivePerception.Decide(Scan(boar, Player(0, 5, Attitude.Neutral)),
                                        new ActiveObserver { UpsetWithPlayer = true, UpsetReason = AttackReason.ReactToDamage });
            Assert.That((p.Step, p.Reason), Is.EqualTo((PerceptionStep.Attack, AttackReason.ReactToDamage)));
            // A sneaking player is not noticed by a creature beyond 1000.
            Assert.That(Scan(boar, Player(0, 15, Attitude.Hostile, sneaking: true)).Has(PerceptionSlot.Hostile), Is.False);
        }

        [Test]
        public void Scan_SkipsTheSlotsNobodyReads()
        {
            var hunter = Observer(Hunter, humanoid: true);
            var s = Scan(hunter, Npc(Orc, 0, 3, Attitude.Friendly), Npc(Scavenger, 0, 4, Attitude.Neutral), Npc(Wolf, 0, 5, Attitude.Angry));
            Assert.That(s.Has(PerceptionSlot.Friendly) || s.Has(PerceptionSlot.Neutral) || s.Has(PerceptionSlot.Angry), Is.False);
            var all = PerceptionScan.Scan(hunter, new[] { Npc(Orc, 0, 3, Attitude.Friendly) }, Perception, OpenLineOfSight.Instance);
            Assert.That(all.Has(PerceptionSlot.Friendly), Is.True, "without a filter every slot is filled");
        }

        [Test]
        public void Eater_DrivesOffWhatComesNearTheBody_MeasuredFromTheBody()
        {
            var wolf = Observer(Wolf, humanoid: false);
            var snapshot = PerceptionScan.Scan(wolf, new[] { Npc(Hunter, 0, 8, Attitude.Angry), Npc(Deer, 0, 6, Attitude.Neutral) }, Perception,
                                               OpenLineOfSight.Instance, wanted: ActivePerception.EaterSlots);
            var body = new WorldPose(0, 0, 7, 0);
            float From(ActorId id) => Senses.Distance(body, id == Hunter ? new WorldPose(0, 0, 8, 0) : new WorldPose(0, 0, 6, 0));
            var d = ActivePerception.DriveOff(snapshot, Scavenger, From);
            Assert.That((d.Step, d.Target, d.Reason), Is.EqualTo((PerceptionStep.Attack, Hunter, AttackReason.DriveAway)),
                        "angry before neutral, both within 5 m of the body");
            var far = new WorldPose(0, 0, 30, 0);
            Assert.That(ActivePerception.DriveOff(snapshot, Scavenger, id => Senses.Distance(far, new WorldPose(0, 0, 6, 0))).Step,
                        Is.EqualTo(PerceptionStep.None));
            var panic = PerceptionScan.Scan(wolf, new[] { Npc(Hunter, 0, 3, Attitude.Panic) }, Perception, OpenLineOfSight.Instance,
                                            wanted: ActivePerception.EaterSlots);
            Assert.That(ActivePerception.DriveOff(panic, Scavenger, _ => 100).Step, Is.EqualTo(PerceptionStep.Flee));
        }

        // ------------------------------------------------------------------------------------------- reaction tasks

        [Test]
        public void Threaten_AttacksWithin7mOrAfter5s_GivesUpBeyond10m()
        {
            Assert.That(ReactionTasks.Threaten(800, 1, true), Is.EqualTo(TaskStep.Continue));
            Assert.That(ReactionTasks.Threaten(690, 1, true), Is.EqualTo(TaskStep.Attack));
            Assert.That(ReactionTasks.Threaten(900, 5.1, true), Is.EqualTo(TaskStep.Attack));
            Assert.That(ReactionTasks.Threaten(1010, 1, true), Is.EqualTo(TaskStep.End));
            Assert.That(ReactionTasks.Threaten(500, 1, false), Is.EqualTo(TaskStep.End), "the target is down or gone");
        }

        [Test]
        public void EatBody_WalksEatsTwoToFourMinutes_ReturnsToTheBodyFromADriveAwayFight()
        {
            var tasks = new ReactionTasks();
            tasks.StartEatBody(Wolf, Scavenger, 10);
            Assert.That(tasks.TryGet(Wolf, out var task), Is.True);
            Assert.That(ReactionTasks.EatBody(task, 600, false, true, 11), Is.EqualTo(TaskStep.Continue), "walking");
            Assert.That(ReactionTasks.EatBody(task, 600, true, true, 12), Is.EqualTo(TaskStep.End), "the goto ended beyond 222");
            Assert.That(ReactionTasks.EatBody(task, 200, false, true, 12), Is.EqualTo(TaskStep.Eat));
            ReactionTasks.BeginMeal(task, 12, new FixedRandom(2));
            Assert.That(task.EatUntil, Is.EqualTo(12 + 240));
            Assert.That(ReactionTasks.EatBody(task, 200, false, true, 200), Is.EqualTo(TaskStep.Continue));
            Assert.That(ReactionTasks.EatBody(task, 200, false, true, 252), Is.EqualTo(TaskStep.End));
            Assert.That(ReactionTasks.EatBody(task, 200, false, false, 20), Is.EqualTo(TaskStep.End), "the body is gone");
            Assert.That(AnimalRules.EatSeconds(new FixedRandom(0)), Is.EqualTo(120));

            tasks.End(Wolf);
            Assert.That(tasks.Has(Wolf), Is.False);
            Assert.That(tasks.MealOf(Wolf), Is.EqualTo(Scavenger), "a DriveAway fight remembers the meal");
            Assert.That(ReactionTasks.BackToBody(AttackReason.DriveAway, 850), Is.True);
            Assert.That(ReactionTasks.BackToBody(AttackReason.DriveAway, 700), Is.False);
            Assert.That(ReactionTasks.BackToBody(AttackReason.Angry, 2000), Is.False);
            tasks.Forget(Scavenger);
            Assert.That(tasks.MealOf(Wolf).IsEmpty && !tasks.Holds(Scavenger), Is.True, "a removed body leaves no meal behind");
        }

        // ------------------------------------------------------------------------------------------- sleepers

        [Test]
        public void HumanSleeper_WakesForAMovingPlayer_InItsHouseOrWithin5mOutdoors_WithSight()
        {
            var sleeper = new SenseObserver { Id = Hunter, Pose = new WorldPose(0, 0, 0, 0), Humanoid = true, IndoorZone = "Hut" };
            var player = new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(0, 0, 8, 0), Moving = true, IndoorZone = "Hut" };
            Assert.That(Senses.WakesSleeper(sleeper, player, Perception), Is.True);
            player.IndoorZone = null;
            player.Pose = new WorldPose(0, 0, 3, 0);
            Assert.That(Senses.WakesSleeper(sleeper, player, Perception), Is.False, "an indoor sleeper only wakes for footsteps in its house");
            sleeper.IndoorZone = null;
            Assert.That(Senses.WakesSleeper(sleeper, player, Perception), Is.True);
            Assert.That(Senses.WakesSleeper(sleeper, player, Perception, new Wall()), Is.False, "OnSleep needs a free line of sight");
            player.Parade = true;
            Assert.That(Senses.WakesSleeper(sleeper, player, Perception), Is.False, "the parade stance is silent");
        }

        [Test]
        public void AnimalSleeper_WakesWithin10m_5mSneaking_NeverWithTheSecondSneakPerk()
        {
            var at = new WorldPose(0, 0, 0, 0);
            var player = new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(0, 0, 8, 0) };
            Assert.That(Senses.WakesAnimal(at, player, false, false, Perception), Is.True, "standing still is enough");
            player.Sneaking = true;
            Assert.That(Senses.WakesAnimal(at, player, false, false, Perception), Is.False);
            player.Pose = new WorldPose(0, 0, 4, 0);
            Assert.That(Senses.WakesAnimal(at, player, false, false, Perception), Is.True);
            Assert.That(Senses.WakesAnimal(at, player, true, false, Perception), Is.False, "Perk_Sneak_2");
            player.Sneaking = false;
            Assert.That(Senses.WakesAnimal(at, player, true, true, Perception), Is.False, "Perk_Sneak_2 in a ranged state");
            Assert.That(Senses.WakesAnimal(at, player, true, false, Perception), Is.True);
        }

        [Test]
        public void WokenSleeper_ObservesWhileThePlayerStaysAtItsBed()
        {
            Assert.That(SleeperRules.KeepsObserving(0.5, false, 900, false, false, true, false), Is.True, "the first second");
            Assert.That(SleeperRules.KeepsObserving(2, false, 900, false, false, true, false), Is.False, "beyond 700");
            Assert.That(SleeperRules.KeepsObserving(4, true, 400, false, false, true, false), Is.True, "at its bed");
            Assert.That(SleeperRules.KeepsObserving(4, true, 600, false, false, true, false), Is.False);
            Assert.That(SleeperRules.KeepsObserving(4, true, 600, true, false, true, false), Is.True, "in its house");
            Assert.That(SleeperRules.KeepsObserving(4, true, 600, true, true, true, false), Is.False, "a friend may be there");
            var tasks = new ReactionTasks();
            tasks.StartObserveWaker(Hunter, ActorId.Player, 10);
            tasks.TryGet(Hunter, out var t);
            Assert.That(ReactionTasks.ObserveCheckDue(t, 12), Is.False);
            Assert.That(ReactionTasks.ObserveCheckDue(t, 14), Is.True);
            Assert.That(ReactionTasks.ObserveCheckDue(t, 15), Is.False, "the next one 4 s later");
        }

        // ------------------------------------------------------------------------------------------- battle lines

        [Test]
        public void AttackReasonLines_ByReasonAndTarget()
        {
            var r = new FixedRandom(1);
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Theft, true, true, "Human", true, r), Is.EqualTo("IGetYouStill"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Enemy, false, false, "Wolf", false, new FixedRandom(1)), Is.EqualTo("DieMonster_01"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Enemy, false, false, "Troll", false, r), Is.EqualTo("DieBigMonster"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.ReactToDamage, false, false, "Wolf", false, r), Is.EqualTo("YouAskedForIt"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Prey, false, false, "Wolf", false, r), Is.Null);
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Enemy, false, true, "Orc", true, new FixedRandom(2)), Is.EqualTo("DieEnemy_02"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Theft, false, true, "Human", true, r), Is.EqualTo("DirtyThief"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Intruder, false, true, "Human", true, r), Is.EqualTo("Intruder"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.ReactToDamage, false, true, "Human", true, r), Is.EqualTo("YouDareHitMe"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.ReactToDamage, false, true, "Human", false, r), Is.EqualTo("DieEnemy_00"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.Arena, false, true, "Human", true, r), Is.EqualTo("ArenaAttack"));
            Assert.That(BattleLines.AttackReasonLine(AttackReason.StopFight, false, true, "Human", true, r), Is.EqualTo("WillYouStopFighting"));
        }

        [Test]
        public void AttackEndAndDefeatCommentLines()
        {
            var r = new FixedRandom(0);
            Assert.That(BattleLines.AttackEndLine(AttackReason.Enemy, false, "Wolf", VitalState.Dead, false, r), Is.EqualTo("MonsterKilled_00"));
            Assert.That(BattleLines.AttackEndLine(AttackReason.Enemy, false, "Dragon", VitalState.Dead, false, r), Is.EqualTo("BigMonsterKilled"));
            Assert.That(BattleLines.AttackEndLine(AttackReason.Enemy, true, "Human", VitalState.Dead, false, new FixedRandom(2)), Is.EqualTo("Victory_02"));
            Assert.That(BattleLines.AttackEndLine(AttackReason.Theft, true, "Human", VitalState.Down, false, r), Is.EqualTo("ThiefDown"));
            Assert.That(BattleLines.AttackEndLine(AttackReason.Intruder, true, "Human", VitalState.Down, true, r), Is.EqualTo("AndNowGetOutOfHere"));
            Assert.That(BattleLines.AttackEndLine(AttackReason.Intruder, true, "Human", VitalState.Down, false, r), Is.EqualTo("NeverEnterAreaAgain"));
            Assert.That(BattleLines.AttackEndLine(AttackReason.Enemy, true, "Human", VitalState.Down, false, r), Is.Null);

            Assert.That(BattleLines.DefeatCommentLine(false, true, Attitude.Neutral, Attitude.Friendly, new FixedRandom(0)), Is.Null,
                        "only a watcher of the fight speaks");
            Assert.That(BattleLines.DefeatCommentLine(true, true, Attitude.Neutral, Attitude.Friendly, new FixedRandom(8)), Is.Null, "20 % keep quiet");
            Assert.That(BattleLines.DefeatCommentLine(true, true, Attitude.Neutral, Attitude.Friendly, new FixedRandom(7)), Is.EqualTo("FriendWon"));
            Assert.That(BattleLines.DefeatCommentLine(true, true, Attitude.Friendly, Attitude.Neutral, new FixedRandom(0)), Is.EqualTo("FriendLost"));
            Assert.That(BattleLines.DefeatCommentLine(true, true, Attitude.Friendly, Attitude.Friendly, new FixedRandom(0)), Is.EqualTo("NotBad"));
            Assert.That(BattleLines.DefeatCommentLine(true, true, Attitude.Neutral, Attitude.Hostile, new FixedRandom(0)), Is.EqualTo("NotBad"));
            Assert.That(BattleLines.DefeatCommentLine(true, false, Attitude.Neutral, Attitude.Hostile, new FixedRandom(0)), Is.EqualTo("StupidMotherFucker"));
        }

        [TestCase(8.0, 8.00)]
        [TestCase(8.0 + 59.9 / 3600, 8.00)]
        [TestCase(8.5, 8.30)]
        [TestCase(8.0 + 59 / 60.0 + 59.0 / 3600, 8.59)]
        [TestCase(30 * 24 + 23.999, 30 * 24 + 23.59)]
        public void SpeechGate_PlayingTime_IsG3sHoursAndWholeMinutes(double hours, double playing) =>
            Assert.That(SpeechGate.PlayingTime(hours), Is.EqualTo(playing).Within(1e-9));

        [Test]
        public void SpeechGate_LongKind_TwelveMinutes_LessAcrossTheHour()
        {
            double hours = 8 + 10 / 60.0;
            var gate = new SpeechGate(() => hours);
            Assert.That(gate.TrySay("DefeatComment", SpeechGate.LongCooldownHours), Is.True);
            hours = 8 + 21 / 60.0;
            Assert.That(gate.TrySay("DefeatComment", SpeechGate.LongCooldownHours), Is.False, "11 minutes");
            hours = 8 + 22 / 60.0;
            Assert.That(gate.TrySay("DefeatComment", SpeechGate.LongCooldownHours), Is.True, "12 minutes");
            hours = 8 + 58 / 60.0;
            Assert.That(gate.TrySay("DefeatComment", SpeechGate.LongCooldownHours), Is.True);
            hours = 9;
            Assert.That(gate.TrySay("DefeatComment", SpeechGate.LongCooldownHours), Is.True, "8.58 -> 9.00 is 0.42 in HH.MM");
        }

        [Test]
        public void SpeechGate_OneLineOfAKindPerCooldown()
        {
            double hours = 0;
            var gate = new SpeechGate(() => hours);
            Assert.That(gate.TrySay("AttackReason"), Is.True);
            Assert.That(gate.TrySay("AttackReason"), Is.False);
            Assert.That(gate.TrySay("AttackEnd"), Is.True, "another kind");
            // fix-156: G3's clock is HH.MM in whole minutes, so 0.0018 blocks for the rest of the game minute
            hours = SpeechGate.BattleCooldownHours + 1e-6; // 6.5 game seconds: the same minute
            Assert.That(gate.TrySay("AttackReason"), Is.False, "the same game minute");
            hours = 59.5 / 3600; // 59.5 game seconds
            Assert.That(gate.TrySay("AttackReason"), Is.False, "still minute 0");
            hours = 60.0 / 3600;
            Assert.That(gate.TrySay("AttackReason"), Is.True, "the next game minute");
            Assert.That(NpcCombatBrain.SayAttackEnd, Is.EqualTo(NpcCombatBrain.SayAttackReason), "G3 kind 3 for both");

            double now = 0;
            var talk = new BattleTalk(() => now, () => 0);
            talk.Watch(Hunter, Orc, Wolf);
            Assert.That(talk.Watches(Hunter, Wolf) && talk.Watches(Hunter, Orc), Is.True);
            Assert.That(talk.Watches(Hunter, Deer), Is.False);
            now = BattleTalk.WatchMemorySeconds + 1;
            Assert.That(talk.Watches(Hunter, Wolf), Is.False, "the memory of the fight fades");
        }

        static NpcCombatBrain Brain(NpcCombatDef def, IRandom random = null)
        {
            var options = RuleOptions.Default;
            return new NpcCombatBrain(Hunter, def, CombatLoadout.Fists, Ai, Rules, options, random ?? new FixedRandom(), new CombatCoordinator(Ai.Token, options));
        }

        static CombatSituation Fight(double now, bool targetHumanoid = false, string species = "Wolf") => new CombatSituation
        {
            Now = now, Distance = 600, LineOfSight = true, FreeBack = true, TargetState = VitalState.Alive, TargetHumanoid = targetHumanoid,
            TargetSpecies = species, AttitudeToTarget = Attitude.Hostile, HitPointsPercent = 100,
        };

        [Test]
        public void Brain_ShoutsTheAttackReasonOnceAsTheFightStarts_AndRunAwayAsAFlightStarts()
        {
            var human = new NpcCombatDef(5, 20, "Human", humanoid: true, @class: NpcClass.Warrior);
            var brain = Brain(human, new FixedRandom(0));
            brain.Engage(Wolf, AttackReason.Enemy, 0);
            Assert.That(brain.Tick(Fight(0)).Shout, Is.EqualTo("DieMonster_00"));
            Assert.That(brain.Tick(Fight(0.1)).Shout, Is.Null, "once");
            brain.Engage(Wolf, AttackReason.Murder, 0.2);
            Assert.That(brain.Tick(Fight(0.2)).Shout, Is.Null, "already fighting: no new reason line");

            var gated = Brain(human);
            double hours = 0;
            gated.Speech = new SpeechGate(() => hours);
            Assert.That(gated.Speech.TrySay(NpcCombatBrain.SayAttackReason), Is.True, "somebody else just said one");
            gated.Engage(Wolf, AttackReason.Enemy, 0);
            Assert.That(gated.Tick(Fight(0)).Shout, Is.Null, "the say gate");

            var flee = Brain(human);
            flee.Flee(Wolf, 0);
            Assert.That(flee.Tick(Fight(0)).Shout, Is.EqualTo(NpcCombatBrain.ShoutRunAway));
            Assert.That(flee.Tick(Fight(0.1)).Shout, Is.Null);

            var wolf = Brain(new NpcCombatDef(0, 21, "Wolf", humanoid: false, animal: true));
            wolf.Engage(Hunter, AttackReason.Enemy, 0);
            Assert.That(wolf.Tick(Fight(0, true, "Human")).Shout, Is.Null, "creatures do not speak");
        }

        // ------------------------------------------------------------------------------------------- witnesses

        [Test]
        public void Witnesses_WatchAFightWithBothFighters_CommentOnADefeatWithTheLoser()
        {
            var w = Reaction.Watch(Orc, Hunter);
            Assert.That((w.Kind, w.Target, w.Other), Is.EqualTo((ReactionKind.Watch, Orc, Hunter)));
            var c = Reaction.Comment(Orc, Hunter);
            Assert.That((c.Kind, c.Target, c.Other), Is.EqualTo((ReactionKind.Comment, Orc, Hunter)));
        }

        // ------------------------------------------------------------------------------------------- healing

        [Test]
        public void HealSelf_Below61Percent_PotionOrSpell()
        {
            Assert.That(HealSelfRules.Choose(70, NpcClass.Warrior, true, false), Is.EqualTo(HealSelfMethod.None));
            Assert.That(HealSelfRules.Choose(60, NpcClass.Warrior, true, false), Is.EqualTo(HealSelfMethod.Potion));
            Assert.That(HealSelfRules.Choose(60, NpcClass.Warrior, false, false), Is.EqualTo(HealSelfMethod.None));
            Assert.That(HealSelfRules.Choose(60, NpcClass.Warrior, false, true), Is.EqualTo(HealSelfMethod.Potion), "party members always try");
            Assert.That(HealSelfRules.Choose(60, NpcClass.Mage, false, false), Is.EqualTo(HealSelfMethod.Spell));
            Assert.That(HealSelfRules.Choose(60, NpcClass.Paladin, true, false), Is.EqualTo(HealSelfMethod.Spell));
            Assert.That(HealSelfRules.Percent(61, 100), Is.EqualTo(61));
            Assert.That(HealSelfRules.Percent(5, 0), Is.EqualTo(100));
        }

        // ------------------------------------------------------------------------------------------- corpses

        [Test]
        public void Corpses_GoAfter120Hours_BigOnesAfter240_CreaturesMayComeBack()
        {
            var human = new CorpseFacts { Template = "Hunter", Species = "Human", DiedAt = 10, PlayerDistance = 5000 };
            Assert.That(CorpseRules.Judge(human, 129, 0), Is.EqualTo(CorpseVerdict.Keep));
            Assert.That(CorpseRules.Judge(human, 130, 0), Is.EqualTo(CorpseVerdict.Remove), "humans never come back");
            human.CarriesMissionItem = true;
            Assert.That(CorpseRules.Judge(human, 500, 0), Is.EqualTo(CorpseVerdict.Keep), "a mission item keeps the body");
            Assert.That(CorpseRules.Judge(new CorpseFacts { Species = "Human" }, 500, 0), Is.EqualTo(CorpseVerdict.Stamp));

            var troll = new CorpseFacts { Template = "BlackTroll", Species = "Troll", DiedAt = 0, PlayerDistance = 5000 };
            Assert.That(CorpseRules.Judge(troll, 200, 0), Is.EqualTo(CorpseVerdict.Keep), "big: 240 h");
            Assert.That(CorpseRules.Judge(troll, 240, 0), Is.EqualTo(CorpseVerdict.Remove));

            var wolf = new CorpseFacts { Template = "Geldern_Wolf_01", Species = "Wolf", DiedAt = 0, PlayerDistance = 5000 };
            Assert.That(CorpseRules.Judge(wolf, 121, 39), Is.EqualTo(CorpseVerdict.Revive), "wolves 40 %");
            Assert.That(CorpseRules.Judge(wolf, 121, 40), Is.EqualTo(CorpseVerdict.Remove));
            wolf.PlayerDistance = 2000;
            Assert.That(CorpseRules.Judge(wolf, 121, 0), Is.EqualTo(CorpseVerdict.Keep), "the player is too close: next time");
            wolf.PlayerDistance = 5000;
            wolf.HumanoidNear = true;
            Assert.That(CorpseRules.Judge(wolf, 121, 0), Is.EqualTo(CorpseVerdict.Keep));
            wolf.HumanoidNear = false;
            wolf.BurningOrBarrier = true;
            Assert.That(CorpseRules.Judge(wolf, 121, 0), Is.EqualTo(CorpseVerdict.Remove));

            Assert.That(CorpseRules.ReviveChance("Scavenger", "Scavenger", false), Is.EqualTo(60));
            Assert.That(CorpseRules.ReviveChance("Chicken", "Chicken", true), Is.EqualTo(80));
            Assert.That(CorpseRules.ReviveChance("Varant_Snapper_01", "Snapper", false), Is.EqualTo(60));
            Assert.That(CorpseRules.ReviveChance("IceWolf", "Wolf", false), Is.EqualTo(40));
            Assert.That(CorpseRules.ReviveChance("Gorn", "Human", false), Is.EqualTo(0));
            Assert.That(CorpseRules.TemplateOf(new ActorId("Geldern_Wolf_01#1a2b")), Is.EqualTo("Geldern_Wolf_01"));
            Assert.That(CorpseRules.TemplateOf(new ActorId("Gorn")), Is.EqualTo("Gorn"));
        }

        // ------------------------------------------------------------------------------------------- session

        static readonly ActorId Corpse = new ActorId("Ranger"), Wolfie = new ActorId("Wolf#a1");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.NpcFolder,
                 new NpcDef(Corpse, default, null, "Human", spawn: new WorldPose(100, 0, 100, 0),
                            combat: new NpcCombatDef(5, 20, "Human", humanoid: true, @class: NpcClass.Warrior)),
                 new NpcDef(Wolfie, default, null, "Wolf", spawn: new WorldPose(150, 0, 100, 0),
                            combat: new NpcCombatDef(0, 21, "Wolf", humanoid: false, animal: true)));

        [Test]
        public void Session_EnteringTheRange_RefreshesTheLiving_JudgesTheDead_AndTheClockIsSaved()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.AddHitPoints(Wolfie, -10);
            Assert.That(s.Combat.Vitals.HitPoints(Wolfie), Is.LessThan(s.Combat.Vitals.HitPointsMax(Wolfie)));
            Assert.That(s.Life.EnterProcessingRange(Wolfie), Is.EqualTo(CorpseVerdict.Keep));
            Assert.That(s.Combat.Vitals.HitPoints(Wolfie), Is.EqualTo(s.Combat.Vitals.HitPointsMax(Wolfie)), "RefreshHitPoints");

            s.Combat.Vitals.Kill(Corpse, byPlayerParty: false);
            double died = s.Clock.GameSeconds / 3600.0;
            Assert.That(s.Life.Corpses.DiedAt(Corpse), Is.EqualTo(died).Within(1e-6), "stamped at death");
            Assert.That(s.Life.EnterProcessingRange(Corpse), Is.EqualTo(CorpseVerdict.Keep), "fresh");

            var loaded = f.Reload(s);
            Assert.That(loaded.Life.Corpses.DiedAt(Corpse), Is.EqualTo(died).Within(1e-6), "the corpse clock is saved");

            loaded.Life.Corpses.Stamp(Corpse, died - CorpseRules.DelayHours - 1);
            loaded.Items.Inventories.Of(Corpse).Add("It_NotAMissionItem", 1);
            Assert.That(loaded.Life.EnterProcessingRange(Corpse), Is.EqualTo(CorpseVerdict.Remove));
            Assert.That(loaded.Npcs.TryGet(Corpse, out _), Is.False, "the corpse is gone");
            Assert.That(loaded.Life.Corpses.IsGone(Corpse), Is.True);
            var again = f.Reload(loaded);
            again.Tick(0.1f);
            Assert.That(again.Npcs.TryGet(Corpse, out _), Is.False, "a loaded game does not bring it back");

            loaded.Life.Corpses.Forget(Wolfie);
            loaded.Combat.Vitals.Kill(Wolfie, false);
            loaded.Life.Corpses.Forget(Wolfie);
            Assert.That(loaded.Life.EnterProcessingRange(Wolfie), Is.EqualTo(CorpseVerdict.Stamp), "an unstamped corpse starts its clock");
            Assert.That(loaded.Life.Corpses.DiedAt(Wolfie).HasValue, Is.True);
        }

        [Test]
        public void Session_EnteredRangeEvents_AreHandledOnTheNextTick()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Npcs.SetDetail(Wolfie, DetailLevel.Abstract);
            s.Combat.Vitals.AddHitPoints(Wolfie, -10);
            s.Npcs.SetDetail(Wolfie, DetailLevel.Active);
            Assert.That(s.Combat.Vitals.HitPoints(Wolfie), Is.LessThan(s.Combat.Vitals.HitPointsMax(Wolfie)), "not inside SetDetail");
            s.Tick(0.1f);
            Assert.That(s.Combat.Vitals.HitPoints(Wolfie), Is.EqualTo(s.Combat.Vitals.HitPointsMax(Wolfie)));
        }

        [Test]
        public void Session_DamageIsSaved_AndRefilledOnlyWhenTheNpcEntersTheRange_KnockedOutToo()
        {
            // fix-175: the save keeps an NPC's lost hit points; G3 refills them when the NPC (re-)enters the processing
            // range, which after a load is every NPC around the hero (the view manager binds them again).
            using var f = Fixture();
            var s = f.NewGame();
            var v = s.Combat.Vitals;
            int max = v.HitPointsMax(Wolfie);
            s.Npcs.SetDetail(Wolfie, DetailLevel.Active);
            s.Tick(0.1f);
            v.AddHitPoints(Wolfie, -(max * 7 / 10));
            v.AddStamina(Wolfie, -5);
            int hurt = v.HitPoints(Wolfie);
            Assert.That(hurt, Is.LessThan(max));

            var loaded = f.Reload(s);
            loaded.Tick(0.1f);
            var lv = loaded.Combat.Vitals;
            Assert.That(lv.HitPoints(Wolfie), Is.EqualTo(hurt), "the save keeps the damage");
            loaded.Npcs.SetDetail(Wolfie, DetailLevel.Abstract);
            loaded.Npcs.SetDetail(Wolfie, DetailLevel.Active);
            loaded.Tick(0.1f);
            Assert.That(lv.HitPoints(Wolfie), Is.EqualTo(max), "RefreshHitPoints on entering the range");
            Assert.That(lv.Stamina(Wolfie), Is.EqualTo(lv.StaminaMax(Wolfie)));

            // a knocked-out NPC is refilled too and keeps lying
            lv.AddHitPoints(Wolfie, -(max - 1));
            lv.KnockOut(Wolfie, byPlayerParty: true);
            Assert.That(loaded.Life.EnterProcessingRange(Wolfie), Is.EqualTo(CorpseVerdict.Keep));
            Assert.That(lv.HitPoints(Wolfie), Is.EqualTo(max));
            Assert.That(lv.IsDown(Wolfie), Is.True, "still down");
            Assert.That(lv.Is(Wolfie, ActorCondition.Defeated), Is.True, "the defeat is remembered");
        }

        [Test]
        public void Session_KeepWoundsAfterLoad_OnlyTheFirstEntryOfNpcsWoundedInRange_OffIsG3()
        {
            // fix-175 port option [owner]: the wounded NPC in range at the save keeps its HP on its first entry after the load
            foreach (bool keep in new[] { true, false })
            {
                using var f = Fixture();
                var s = f.NewGame();
                var v = s.Combat.Vitals;
                int max = v.HitPointsMax(Wolfie), cmax = v.HitPointsMax(Corpse);
                s.Npcs.SetDetail(Wolfie, DetailLevel.Active);   // in range at the save
                s.Npcs.SetDetail(Corpse, DetailLevel.Abstract); // out of range at the save
                s.Tick(0.1f);
                v.AddHitPoints(Wolfie, -(max * 7 / 10));
                v.AddHitPoints(Corpse, -(cmax / 2));
                int hurt = v.HitPoints(Wolfie), churt = v.HitPoints(Corpse);

                var loaded = f.Reload(s);
                loaded.Life.KeepWoundsAfterLoad = keep;
                var lv = loaded.Combat.Vitals;
                loaded.Npcs.SetDetail(Wolfie, DetailLevel.Active); // the views bind again after the load
                loaded.Npcs.SetDetail(Corpse, DetailLevel.Near);   // the hero walks up to the other one
                loaded.Tick(0.1f);
                Assert.That(lv.HitPoints(Wolfie), Is.EqualTo(keep ? hurt : max), keep ? "kept over the load" : "G3: refilled");
                Assert.That(lv.HitPoints(Corpse), Is.EqualTo(cmax), "out of range at the save: refilled as in G3 (" + keep + ")");
                Assert.That(churt, Is.LessThan(cmax));

                loaded.Npcs.SetDetail(Wolfie, DetailLevel.Abstract);
                loaded.Npcs.SetDetail(Wolfie, DetailLevel.Active);
                loaded.Tick(0.1f);
                Assert.That(lv.HitPoints(Wolfie), Is.EqualTo(max), "streaming back in during play refills as in G3 (" + keep + ")");
            }
        }

        [Test]
        public void PortOptions_KeepWoundsAfterLoad_DefaultOn_RowAboveShowFps()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.KeepWoundsAfterLoad, Is.True, "[owner] default");
            Assert.That(PortOptions.RowText(PortOptions.Row.KeepWounds, "Russian"), Is.EqualTo("Сохранять раны после загрузки"));
            Assert.That(PortOptions.RowText(PortOptions.Row.KeepWounds, "English"), Is.EqualTo("Keep wounds after load"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.KeepWounds, false, "English"), Is.EqualTo("No (original)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.KeepWounds, true, "Russian"), Is.EqualTo("Да"));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.KeepWoundsTop, Is.EqualTo(140f));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.RowBottom(Myrtana.Ui.Skin.VideoPortRowsLayout.KeepWoundsTop),
                        Is.LessThanOrEqualTo(Myrtana.Ui.Skin.VideoPortRowsLayout.ShowFpsTop + Myrtana.Ui.Skin.VideoPortRowsLayout.SliderOffset));
            using var f = Fixture();
            Assert.That(f.NewGame().Life.KeepWoundsAfterLoad, Is.False, "the session's own default is G3");
        }

        [Test]
        public void Session_FleeingWitness_RunsFromTheKiller()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Crime.Attitudes.Facts.TryGetProfile(Corpse, out var witness), Is.True);
            s.Crime.Witnesses.Apply(witness, Reaction.Of(ReactionKind.Flee, Wolfie), BroadcastKind.Murder);
            Assert.That(s.NpcCombat.Get(Corpse)?.State.Phase, Is.EqualTo(CombatPhase.Flee));
            Assert.That(s.Crime.Attitudes.Of(Corpse, Wolfie), Is.EqualTo(Attitude.Panic), "fleeing from it");
            Assert.That(s.Crime.World.ModeOf(Corpse), Is.EqualTo(ReceiverMode.Flee));
        }
    }
}
