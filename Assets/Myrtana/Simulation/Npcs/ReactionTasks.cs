using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The numbers of the creature reaction tasks, CP 1.75 Script_Game (Docs/npc-simulation.md §animals). Centimetres and
    /// real seconds.
    /// </summary>
    public static class AnimalRules
    {
        /// <summary>ZS_Threaten_Loop: a target closer than this is attacked (AssessTarget Angry) [700].</summary>
        public const float ThreatenAttackRange = 700f;
        /// <summary>ZS_Threaten_Loop: a target farther than this ends the threat (ContinueRoutine) [1000].</summary>
        public const float ThreatenGiveUpRange = 1000f;
        /// <summary>ZS_Threaten_Loop: after this task time the creature attacks anyway [5.0 s].</summary>
        public const double ThreatenSeconds = 5.0;
        /// <summary>The threat motion (G3 action Warn, 121; creatures play their Say clip) [interp: the rigs have no Warn clip].</summary>
        public const string ThreatenInteraction = "Warn";
        /// <summary>ZS_EatBody: the eater eats when the goto ends this close to the body [222].</summary>
        public const float EatRange = 222f;
        /// <summary>ZS_EatBody_Loop: _AI_Action(EatGround, (2 + Random(3)) · 60) [unit seconds (?)].</summary>
        public const int EatMinutesMin = 2, EatMinutesSpread = 3;
        public const string EatInteraction = "EatGround";
        /// <summary>OnEatBody_Perception: anything within this of the body is driven off (AssessTarget DriveAway) [500].</summary>
        public const float DriveOffRange = 500f;
        /// <summary>OnAttack: a DriveAway fight farther than this from the body goes back to eating [800].</summary>
        public const float BackToBodyRange = 800f;
        /// <summary>The walk to the body gives up after this long (real s) [interp: G3's goto has its own stuck handling].</summary>
        public const double EatGotoTimeout = 30;

        /// <summary>ZS_EatBody_Loop's eating time in real seconds.</summary>
        public static double EatSeconds(IRandom random) => (EatMinutesMin + (random?.Next(EatMinutesSpread) ?? 0)) * 60.0;
    }

    public enum ReactionTaskKind
    {
        None,
        /// <summary>ZS_Threaten: face the target, threat motion; attack it close or after 5 s, give up beyond 10 m.</summary>
        Threaten,
        /// <summary>ZS_EatBody: walk to the body, eat; drive off whoever comes near.</summary>
        EatBody,
        /// <summary>ZS_ReactToFootsteps → ZS_ObserveSuspect: a woken sleeper watches the player.</summary>
        ObserveWaker,
    }

    /// <summary>One running reaction task (not saved: a loaded game starts with the routine again, as after a reaction).</summary>
    public sealed class ReactionTask
    {
        public ReactionTaskKind Kind { get; internal set; }
        public ActorId Target { get; internal set; }
        /// <summary>Real seconds when the task (its current phase) started.</summary>
        public double Started { get; internal set; }
        /// <summary>EatBody: eating (else still walking to the body); the end of the meal (real s).</summary>
        public bool Eating { get; internal set; }
        public double EatUntil { get; internal set; }
        /// <summary>ObserveWaker: the next 4 s keep-observing check (real s).</summary>
        public double NextCheck { get; internal set; }

        public override string ToString() => $"{Kind} {Target}{(Eating ? " eating" : "")}";
    }

    /// <summary>What the body of an NPC in a reaction task does this frame.</summary>
    public enum TaskStep { Continue, Attack, End, Eat }

    /// <summary>
    /// The creature and sleeper reaction tasks of the shown NPCs (G3 ZS_Threaten, ZS_EatBody, ZS_ReactToFootsteps /
    /// ZS_ObserveSuspect): who runs which, and the pure loop rules. The presentation moves the body; the perception driver
    /// starts the tasks. A task holds the NPC in AI mode Reaction: no routine and, for the creature tasks, no active
    /// perception except the eater's own drive-off scan; the woken sleeper's ZS_ObserveSuspect keeps it (its callback
    /// OnGoto is AssessActivePerception: an enemy is attacked, fix-217).
    /// </summary>
    public sealed class ReactionTasks : IActorStore
    {
        readonly Dictionary<ActorId, ReactionTask> _tasks = new Dictionary<ActorId, ReactionTask>();
        readonly Dictionary<ActorId, ActorId> _meals = new Dictionary<ActorId, ActorId>();

        public bool TryGet(ActorId npc, out ReactionTask task) => _tasks.TryGetValue(npc, out task);

        public bool Has(ActorId npc) => _tasks.ContainsKey(npc);

        public ReactionTaskKind KindOf(ActorId npc) => _tasks.TryGetValue(npc, out var t) ? t.Kind : ReactionTaskKind.None;

        public int Count => _tasks.Count;

        public void StartThreaten(ActorId npc, ActorId target, double now) => Start(npc, ReactionTaskKind.Threaten, target, now);

        public void StartEatBody(ActorId npc, ActorId body, double now)
        {
            Start(npc, ReactionTaskKind.EatBody, body, now);
            _meals[npc] = body;
        }

        public void StartObserveWaker(ActorId npc, ActorId waker, double now)
        {
            var t = Start(npc, ReactionTaskKind.ObserveWaker, waker, now);
            t.NextCheck = now + SleeperRules.ObserveCheckSeconds;
        }

        ReactionTask Start(ActorId npc, ReactionTaskKind kind, ActorId target, double now)
        {
            var t = new ReactionTask { Kind = kind, Target = target, Started = now };
            _tasks[npc] = t;
            return t;
        }

        /// <summary>The task ends (back to the routine: G3 ContinueRoutine). The eater's body is remembered for DriveAway fights.</summary>
        public void End(ActorId npc) => _tasks.Remove(npc);

        /// <summary>The body the creature was eating (a DriveAway fight returns to it); empty = none.</summary>
        public ActorId MealOf(ActorId npc) => _meals.TryGetValue(npc, out var body) ? body : default;

        public void ForgetMeal(ActorId npc) => _meals.Remove(npc);

        /// <summary>Ends every task (a teleport, a cutscene, loading).</summary>
        public void Clear()
        {
            _tasks.Clear();
            _meals.Clear();
        }

        public void Forget(ActorId actor)
        {
            _tasks.Remove(actor);
            _meals.Remove(actor);
            List<ActorId> stale = null;
            foreach (var pair in _tasks)
                if (pair.Value.Target == actor) (stale ??= new List<ActorId>()).Add(pair.Key);
            foreach (var pair in _meals)
                if (pair.Value == actor) (stale ??= new List<ActorId>()).Add(pair.Key);
            if (stale == null) return;
            foreach (var npc in stale)
            {
                if (_tasks.TryGetValue(npc, out var t) && t.Target == actor) _tasks.Remove(npc);
                if (_meals.TryGetValue(npc, out var m) && m == actor) _meals.Remove(npc);
            }
        }

        public bool Holds(ActorId actor)
        {
            if (_tasks.ContainsKey(actor) || _meals.ContainsKey(actor)) return true;
            foreach (var t in _tasks.Values) if (t.Target == actor) return true;
            foreach (var m in _meals.Values) if (m == actor) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------- loop rules

        /// <summary>
        /// ZS_Threaten_Loop, every iteration: the target closer than 700 → attack (Angry); farther than 1000 → end; more than
        /// 5 s of task time → attack; else play the threat. A target that is gone or down ends it.
        /// </summary>
        public static TaskStep Threaten(float distance, double taskSeconds, bool targetAlive)
        {
            if (!targetAlive) return TaskStep.End;
            if (distance < AnimalRules.ThreatenAttackRange) return TaskStep.Attack;
            if (distance > AnimalRules.ThreatenGiveUpRange) return TaskStep.End;
            if (taskSeconds > AnimalRules.ThreatenSeconds) return TaskStep.Attack;
            return TaskStep.Continue;
        }

        /// <summary>
        /// ZS_EatBody: the goto ends at the body → eat when within 222, else back to the routine; the meal ends after its
        /// time (ZS_EatBody_Loop → ContinueRoutine). <paramref name="arrived"/>: the walk is over (reached or stuck).
        /// </summary>
        public static TaskStep EatBody(ReactionTask task, float distanceToBody, bool arrived, bool bodyExists, double now)
        {
            if (!bodyExists) return TaskStep.End;
            if (task.Eating) return now >= task.EatUntil ? TaskStep.End : TaskStep.Continue;
            if (distanceToBody <= AnimalRules.EatRange) return TaskStep.Eat;
            if (arrived || now - task.Started > AnimalRules.EatGotoTimeout) return TaskStep.End;
            return TaskStep.Continue;
        }

        /// <summary>ZS_ObserveSuspect_Loop's 4 s check: whether it is due now (and the next one is scheduled when so).</summary>
        public static bool ObserveCheckDue(ReactionTask task, double now)
        {
            if (now < task.NextCheck) return false;
            task.NextCheck = now + SleeperRules.ObserveCheckSeconds;
            return true;
        }

        /// <summary>The eater reached the body: the meal starts (EatGround for (2 + Random(3)) · 60 s).</summary>
        public static void BeginMeal(ReactionTask task, double now, IRandom random)
        {
            task.Eating = true;
            task.Started = now;
            task.EatUntil = now + AnimalRules.EatSeconds(random);
        }

        /// <summary>
        /// OnAttack with reason DriveAway: the creature is farther than 800 from its body → FullStop and back to eating
        /// (ZS_EatBody).
        /// </summary>
        public static bool BackToBody(AttackReason reason, float distanceToBody) =>
            reason == AttackReason.DriveAway && distanceToBody > AnimalRules.BackToBodyRange;
    }
}
