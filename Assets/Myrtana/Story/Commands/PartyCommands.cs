using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>
    /// The actor joins the hero's party as <see cref="Type"/> (G3 JoinPlayerParty / JoinPlayerMob / JoinPlayerSlavery after
    /// a Join / MobJoin / SlaveJoin info; the info command JoinPlayer true joins as a slave).
    /// </summary>
    public sealed class JoinParty : DialogCommand
    {
        [JsonConstructor]
        public JoinParty(ActorRef actor, PartyMemberType type)
        {
            Actor = actor;
            Type = type;
        }

        public ActorRef Actor { get; }
        public PartyMemberType Type { get; }
    }

    /// <summary>The member waits where it is (G3 WaitPlayerParty after a Wait info).</summary>
    public sealed class WaitParty : DialogCommand
    {
        [JsonConstructor]
        public WaitParty(ActorRef actor) => Actor = actor;

        public ActorRef Actor { get; }
    }

    /// <summary>
    /// The member leaves the party (G3 DismissPlayerParty / DismissPlayerMob after a Dismiss / MobDismiss info); with
    /// <see cref="StopBringNpc"/> its PartyEnabled flag is cleared first (G3 StopBringNPC, info command JoinPlayer false).
    /// </summary>
    public sealed class LeaveParty : DialogCommand
    {
        [JsonConstructor]
        public LeaveParty(ActorRef actor, bool stopBringNpc)
        {
            Actor = actor;
            StopBringNpc = stopBringNpc;
        }

        public ActorRef Actor { get; }
        public bool StopBringNpc { get; }
    }

    /// <summary>
    /// End of a Delivery info of a BringNpc or FollowNpc quest (gCInfo_PS, CP 1.75): the quest succeeds; the brought NPCs
    /// are released (StopBringNPC, Party/Slavery/Mob flags off), the guide of a FollowNpc quest stops guiding.
    /// </summary>
    public sealed class CompleteDelivery : DialogCommand
    {
        [JsonConstructor]
        public CompleteDelivery(Id<QuestDef> quest) => Quest = quest;

        public Id<QuestDef> Quest { get; }
    }

    /// <summary>
    /// The Join pre-check refused (G3 IS_Running_NoParty): the screen message GO_NoParty naming the companion who is with
    /// the hero now. The partner's line (SVM JoinRefuse) follows as a shared line.
    /// </summary>
    public sealed class PartyRefusal : DialogCommand
    {
        [JsonConstructor]
        public PartyRefusal() { }
    }

    public sealed class JoinPartyHandler : CommandHandler<JoinParty>
    {
        protected override CommandResult Execute(JoinParty command, CommandContext context)
        {
            context.Services.Party?.Join(context.Resolve(command.Actor), command.Type);
            return CommandResult.Continue;
        }
    }

    public sealed class WaitPartyHandler : CommandHandler<WaitParty>
    {
        protected override CommandResult Execute(WaitParty command, CommandContext context)
        {
            context.Services.Party?.Wait(context.Resolve(command.Actor));
            return CommandResult.Continue;
        }
    }

    public sealed class LeavePartyHandler : CommandHandler<LeaveParty>
    {
        protected override CommandResult Execute(LeaveParty command, CommandContext context)
        {
            var party = context.Services.Party;
            var actor = context.Resolve(command.Actor);
            if (party == null) return CommandResult.Continue;
            if (command.StopBringNpc) party.StopBringNpc(actor);
            else party.Dismiss(actor);
            return CommandResult.Continue;
        }
    }

    public sealed class CompleteDeliveryHandler : CommandHandler<CompleteDelivery>
    {
        protected override CommandResult Execute(CompleteDelivery command, CommandContext context)
        {
            var quests = context.Quests;
            if (!quests.TryGetDefinition(command.Quest, out var quest)) return CommandResult.Continue;
            quests.TrySetStatus(command.Quest, QuestStatus.Success);
            var party = context.Services.Party;
            if (party == null) return CommandResult.Continue;
            var objective = quest.Objective;
            if (objective.Kind == QuestObjectiveKind.FollowNpc)
            {
                if (objective.Targets.Count > 0) party.StopGuide(new ActorId(objective.Targets[0].Target));
                else party.StopGuide();
            }
            else
                foreach (var target in objective.Targets) party.Delivered(new ActorId(target.Target));
            return CommandResult.Continue;
        }
    }

    public sealed class PartyRefusalHandler : CommandHandler<PartyRefusal>
    {
        protected override CommandResult Execute(PartyRefusal command, CommandContext context)
        {
            var companion = context.Services.Party?.ActiveCompanionOtherThan(context.Partner) ?? default;
            context.Events.Publish(new GameMessage(PlayerParty.NoPartyMessage, companion));
            return CommandResult.Continue;
        }
    }
}
