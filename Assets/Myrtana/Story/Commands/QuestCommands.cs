using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>The quest ends successfully (rewards are given).</summary>
    public sealed class SucceedQuest : DialogCommand
    {
        [JsonConstructor]
        public SucceedQuest(Id<QuestDef> quest) => Quest = quest;

        public Id<QuestDef> Quest { get; }
    }

    /// <summary>The quest is given up: Open → Obsolete, Running → Cancelled.</summary>
    public sealed class CloseQuest : DialogCommand
    {
        [JsonConstructor]
        public CloseQuest(Id<QuestDef> quest) => Quest = quest;

        public Id<QuestDef> Quest { get; }
    }

    /// <summary>The quest fails: Open → Obsolete, otherwise Failed.</summary>
    public sealed class FailQuest : DialogCommand
    {
        [JsonConstructor]
        public FailQuest(Id<QuestDef> quest) => Quest = quest;

        public Id<QuestDef> Quest { get; }
    }

    /// <summary>
    /// The actor reports to the quest: its target counter goes up by one (a quest whose objective is to talk to
    /// someone; G3 Delivery entries of Report/Bring/Follow quests).
    /// </summary>
    public sealed class ReportToQuest : DialogCommand
    {
        [JsonConstructor]
        public ReportToQuest(Id<QuestDef> quest, ActorRef reporter)
        {
            Quest = quest;
            Reporter = reporter;
        }

        public Id<QuestDef> Quest { get; }
        public ActorRef Reporter { get; }
    }

    /// <summary>
    /// The hero hands over what a HasItems quest still lacks (G3 gCInfo_PS::OnDelivery, built into Delivery and
    /// PartDelivery entries): per item, the amount minus the delivery counter, as far as the hero carries it, goes to
    /// <see cref="Receiver"/>; the quest's item counter counts it.
    /// </summary>
    public sealed class DeliverQuestItems : DialogCommand
    {
        [JsonConstructor]
        public DeliverQuestItems(Id<QuestDef> quest, ActorRef receiver)
        {
            Quest = quest;
            Receiver = receiver;
        }

        public Id<QuestDef> Quest { get; }
        public ActorRef Receiver { get; }
    }

    /// <summary>Adds a note to a quest's log.</summary>
    public sealed class AddQuestLog : DialogCommand
    {
        [JsonConstructor]
        public AddQuestLog(Id<QuestDef> quest, TextId text)
        {
            Quest = quest;
            Text = text;
        }

        public Id<QuestDef> Quest { get; }
        public TextId Text { get; }
    }

    public sealed class SucceedQuestHandler : CommandHandler<SucceedQuest>
    {
        protected override CommandResult Execute(SucceedQuest command, CommandContext context)
        {
            context.Quests.TrySetStatus(command.Quest, QuestStatus.Success);
            return CommandResult.Continue;
        }
    }

    public sealed class CloseQuestHandler : CommandHandler<CloseQuest>
    {
        protected override CommandResult Execute(CloseQuest command, CommandContext context)
        {
            var quests = context.Quests;
            quests.TrySetStatus(command.Quest, QuestTransitions.CloseTarget(quests.StatusOf(command.Quest)));
            return CommandResult.Continue;
        }
    }

    public sealed class FailQuestHandler : CommandHandler<FailQuest>
    {
        protected override CommandResult Execute(FailQuest command, CommandContext context)
        {
            var quests = context.Quests;
            quests.TrySetStatus(command.Quest, QuestTransitions.FailTarget(quests.StatusOf(command.Quest)));
            return CommandResult.Continue;
        }
    }

    public sealed class ReportToQuestHandler : CommandHandler<ReportToQuest>
    {
        protected override CommandResult Execute(ReportToQuest command, CommandContext context)
        {
            context.Quests.Count(command.Quest, context.Resolve(command.Reporter).Name, 1);
            return CommandResult.Continue;
        }
    }

    public sealed class DeliverQuestItemsHandler : CommandHandler<DeliverQuestItems>
    {
        protected override CommandResult Execute(DeliverQuestItems command, CommandContext context)
        {
            var quests = context.Quests;
            if (!quests.TryGetDefinition(command.Quest, out var quest) || quest.Objective.Kind != QuestObjectiveKind.HasItems)
                return CommandResult.Continue;
            var receiver = context.Resolve(command.Receiver);
            var counters = quests.ProgressOf(command.Quest)?.Counters;
            var inventories = context.Services.Items.Inventories;
            var targets = quest.Objective.Targets;
            for (int i = 0; i < targets.Count; i++)
            {
                int counted = counters != null && i < counters.Length ? counters[i] : 0;
                int missing = targets[i].Amount - counted;
                if (missing > 0) inventories.Transfer(ActorId.Player, receiver, targets[i].Target, missing);
            }
            return CommandResult.Continue;
        }
    }

    public sealed class AddQuestLogHandler : CommandHandler<AddQuestLog>
    {
        protected override CommandResult Execute(AddQuestLog command, CommandContext context)
        {
            context.World.QuestLog.Add(command.Quest, new QuestLogEntry(default, command.Text));
            return CommandResult.Continue;
        }
    }
}
