using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// G3 condition types FirstWarn / SecondWarn: the gate guard may give its first warning (it has a guard point and
    /// watches), or its second one (it warned once and the player came 80 closer to the guard point since).
    /// </summary>
    public sealed class GateGuardMayWarn : Condition
    {
        [JsonConstructor]
        public GateGuardMayWarn(ActorRef guard, bool second = false)
        {
            Guard = guard;
            Second = second;
        }

        public ActorRef Guard { get; }
        public bool Second { get; }

        public override bool Evaluate(in ConditionContext context) =>
            context.Services.World.GateGuardMayWarn(context.Resolve(Guard), Second);
    }

    /// <summary>
    /// G3 condition type Heal (script HasHealInfo): the actor is led by the player, is humanoid, and the player carries a
    /// health potion to give it.
    /// </summary>
    public sealed class CanHeal : Condition
    {
        /// <summary>The potion the hero hands over.</summary>
        public const string Potion = "It_Potion_Health";

        [JsonConstructor]
        public CanHeal(ActorRef actor) => Actor = actor;

        public ActorRef Actor { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            var actor = context.Resolve(Actor);
            var services = context.Services;
            if (services.Party == null || !services.Party.IsLedByPlayer(actor)) return false;
            if (services.Items == null || !services.Items.Inventories.Of(ActorId.Player).Has(Potion)) return false;
            return services.Traits.IsHumanoid(actor);
        }
    }

    /// <summary>The actor can fight (G3 CanAttack); a cow, a sheep or a chicken cannot.</summary>
    public sealed class CanFight : Condition
    {
        [JsonConstructor]
        public CanFight(ActorRef actor) => Actor = actor;

        public ActorRef Actor { get; }

        public override bool Evaluate(in ConditionContext context) => context.Services.Traits.CanFight(context.Resolve(Actor));
    }

    /// <summary>
    /// The actor's last conversation with the player ended at least <see cref="Hours"/> game hours ago, or there was none
    /// (G3 EndDialogTimestamp; the CP condition type Animal waits 48 hours).
    /// </summary>
    public sealed class DialogEndedHoursAgo : Condition
    {
        [JsonConstructor]
        public DialogEndedHoursAgo(ActorRef actor, double hours)
        {
            Actor = actor;
            Hours = hours;
        }

        public ActorRef Actor { get; }
        public double Hours { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            if (!context.World.DialogMemory.TryGetDialogEnd(context.Resolve(Actor), out double endedAt)) return true;
            return context.World.Quests.GameSeconds - endedAt >= Hours * 3600.0;
        }
    }

    /// <summary>
    /// CP info type QuestRunning: the quest is Running and the entry last ran at least <see cref="Hours"/> game hours ago
    /// (before its first run: since the quest started).
    /// </summary>
    public sealed class QuestRepeatDue : Condition
    {
        [JsonConstructor]
        public QuestRepeatDue(Id<DialogEntryDef> entry, Id<QuestDef> quest, double hours)
        {
            Entry = entry;
            Quest = quest;
            Hours = hours;
        }

        public Id<DialogEntryDef> Entry { get; }
        public Id<QuestDef> Quest { get; }
        public double Hours { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            var quests = context.World.Quests;
            if (quests.StatusOf(Quest) != QuestStatus.Running) return false;
            double since = context.World.DialogMemory.TryGetLastRun(Entry, out double ran) ? ran : quests.ProgressOf(Quest)?.StartedAt ?? 0;
            return quests.GameSeconds - since >= Hours * 3600.0;
        }
    }

    /// <summary>
    /// G3 condition type PickPocket: the hero knows Perk_PickPocket_1, the actor carries pickpocket loot and has not been
    /// robbed yet.
    /// </summary>
    public sealed class MayPickpocket : Condition
    {
        [JsonConstructor]
        public MayPickpocket(ActorRef victim) => Victim = victim;

        public ActorRef Victim { get; }

        public override bool Evaluate(in ConditionContext context) => context.Services.Pickpocket.MayPickpocket(context.Resolve(Victim));
    }
}
