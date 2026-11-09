using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>Items change hands; a giver lacking some first gets them (G3 "Give").</summary>
    public sealed class GiveItems : DialogCommand
    {
        [JsonConstructor]
        public GiveItems(ActorRef from, ActorRef to, string item, int amount)
        {
            From = from;
            To = to;
            Item = item;
            Amount = amount;
        }

        public ActorRef From { get; }
        public ActorRef To { get; }
        public string Item { get; }
        public int Amount { get; }
    }

    /// <summary>Switches an actor's trading or teaching on or off.</summary>
    public sealed class SetRole : DialogCommand
    {
        [JsonConstructor]
        public SetRole(ActorRef actor, ActorRole role, bool enabled)
        {
            Actor = actor;
            Role = role;
            Enabled = enabled;
        }

        public ActorRef Actor { get; }
        public ActorRole Role { get; }
        public bool Enabled { get; }
    }

    /// <summary>The trade screen with the partner opens when the conversation has ended.</summary>
    public sealed class OpenTrade : DialogCommand { }

    /// <summary>The partner teaches the lesson; <see cref="Gold"/> (before surcharges) is paid to the partner.</summary>
    public sealed class TeachLesson : DialogCommand
    {
        [JsonConstructor]
        public TeachLesson(Lesson lesson, int gold)
        {
            Lesson = lesson;
            Gold = gold;
        }

        public Lesson Lesson { get; }
        public int Gold { get; }
    }

    /// <summary>
    /// Reports which requirements of <see cref="Lesson"/> the hero lacks (the refusal of a teach entry, before
    /// GO_NoLearnRequirements): G3 gCInventory_PS::CanLearnSkill prints one HUD message per missing skill/attribute.
    /// </summary>
    public sealed class ReportLearnShortfalls : DialogCommand
    {
        [JsonConstructor]
        public ReportLearnShortfalls(Lesson lesson) => Lesson = lesson;

        public Lesson Lesson { get; }
    }

    /// <summary>Published by <see cref="ReportLearnShortfalls"/>; the HUD prints one line per shortfall.</summary>
    public readonly struct LearnRequirementsMissing : IGameEvent
    {
        public LearnRequirementsMissing(IReadOnlyList<LearnShortfall> shortfalls) => Shortfalls = shortfalls;
        public IReadOnlyList<LearnShortfall> Shortfalls { get; }
    }

    /// <summary>The trade screen should open with <see cref="Merchant"/> once the dialog is closed.</summary>
    public readonly struct TradeRequested : IGameEvent
    {
        public TradeRequested(ActorId merchant) => Merchant = merchant;
        public ActorId Merchant { get; }
    }

    public sealed class GiveItemsHandler : CommandHandler<GiveItems>
    {
        protected override CommandResult Execute(GiveItems command, CommandContext context)
        {
            context.Services.Items.Inventories.Transfer(context.Resolve(command.From), context.Resolve(command.To), command.Item,
                                                        command.Amount, createMissing: true);
            return CommandResult.Continue;
        }
    }

    public sealed class SetRoleHandler : CommandHandler<SetRole>
    {
        protected override CommandResult Execute(SetRole command, CommandContext context)
        {
            context.Services.Items.Roles.Set(context.Resolve(command.Actor), command.Role, command.Enabled);
            return CommandResult.Continue;
        }
    }

    public sealed class OpenTradeHandler : CommandHandler<OpenTrade>
    {
        protected override CommandResult Execute(OpenTrade command, CommandContext context)
        {
            context.Events.Publish(new TradeRequested(context.Partner));
            return CommandResult.Continue;
        }
    }

    public sealed class ReportLearnShortfallsHandler : CommandHandler<ReportLearnShortfalls>
    {
        protected override CommandResult Execute(ReportLearnShortfalls command, CommandContext context)
        {
            var shortfalls = context.Services.Items.Teaching.Shortfalls(command.Lesson);
            if (shortfalls.Count > 0) context.Events.Publish(new LearnRequirementsMissing(shortfalls));
            return CommandResult.Continue;
        }
    }

    public sealed class TeachLessonHandler : CommandHandler<TeachLesson>
    {
        protected override CommandResult Execute(TeachLesson command, CommandContext context)
        {
            var items = context.Services.Items;
            int gold = items.Teaching.GoldCost(command.Lesson, command.Gold);
            if (items.Teaching.Teach(command.Lesson) == TeachResult.Done && gold > 0)
                items.Inventories.Transfer(ActorId.Player, context.Partner, items.GoldItem, gold);
            return CommandResult.Continue;
        }
    }
}
