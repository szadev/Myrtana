using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>A story flag is set.</summary>
    public sealed class FlagSet : Condition
    {
        [JsonConstructor]
        public FlagSet(string flag) => Flag = flag;

        public string Flag { get; }

        public override bool Evaluate(in ConditionContext context) => context.Flags.IsSet(Flag);
    }

    /// <summary>The quest has one of the listed statuses.</summary>
    public sealed class QuestStatusIs : Condition
    {
        [JsonConstructor]
        public QuestStatusIs(Id<QuestDef> quest, IReadOnlyList<QuestStatus> statuses)
        {
            Quest = quest;
            Statuses = statuses ?? Array.Empty<QuestStatus>();
        }

        public Id<QuestDef> Quest { get; }
        public IReadOnlyList<QuestStatus> Statuses { get; }

        public override bool Evaluate(in ConditionContext context)
        {
            var status = context.Quests.StatusOf(Quest);
            foreach (var s in Statuses)
                if (s == status) return true;
            return false;
        }
    }

    /// <summary>The quest has reached a final status (<see cref="QuestStatusExtensions.IsFinished"/>).</summary>
    public sealed class QuestFinished : Condition
    {
        [JsonConstructor]
        public QuestFinished(Id<QuestDef> quest) => Quest = quest;

        public Id<QuestDef> Quest { get; }

        public override bool Evaluate(in ConditionContext context) => context.Quests.StatusOf(Quest).IsFinished();
    }

    /// <summary>The quest is Running and past its time limit (G3 condition type Overtime).</summary>
    public sealed class QuestOverdue : Condition
    {
        [JsonConstructor]
        public QuestOverdue(Id<QuestDef> quest) => Quest = quest;

        public Id<QuestDef> Quest { get; }

        public override bool Evaluate(in ConditionContext context) => context.World.Quests.IsOverdue(Quest);
    }

    /// <summary>The actor has finished at least one conversation with the player.</summary>
    public sealed class TalkedToPlayer : Condition
    {
        [JsonConstructor]
        public TalkedToPlayer(ActorRef actor) => Actor = actor;

        public ActorRef Actor { get; }

        public override bool Evaluate(in ConditionContext context) =>
            context.Actors.HasTalkedToPlayer(Actor.Resolve(context.Partner));
    }

    /// <summary>An actor is alive / unharmed / knocked out / dead (G3 CondSecondaryNPC states 0–3).</summary>
    public sealed class ActorIs : Condition
    {
        [JsonConstructor]
        public ActorIs(ActorRef actor, ActorCondition condition)
        {
            Actor = actor;
            Condition = condition;
        }

        public ActorRef Actor { get; }
        public ActorCondition Condition { get; }

        public override bool Evaluate(in ConditionContext context) =>
            context.Services.Vitals.Is(context.Resolve(Actor), Condition);
    }

    /// <summary>The conversation partner is this actor (for the hero's own topics that concern one NPC).</summary>
    public sealed class PartnerIs : Condition
    {
        [JsonConstructor]
        public PartnerIs(ActorId actor) => Actor = actor;

        public ActorId Actor { get; }

        public override bool Evaluate(in ConditionContext context) => context.Partner == Actor;
    }
}
