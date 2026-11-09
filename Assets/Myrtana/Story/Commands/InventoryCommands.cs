using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>Up to <see cref="Amount"/> items vanish from the holder (G3 Erase).</summary>
    public sealed class RemoveItems : DialogCommand
    {
        [JsonConstructor]
        public RemoveItems(ActorRef holder, string item, int amount)
        {
            Holder = holder;
            Item = item;
            Amount = amount;
        }

        public ActorRef Holder { get; }
        public string Item { get; }
        public int Amount { get; }
    }

    /// <summary>Everything the giver carries goes to the receiver (G3 GiveAll).</summary>
    public sealed class GiveAllItems : DialogCommand
    {
        [JsonConstructor]
        public GiveAllItems(ActorRef from, ActorRef to)
        {
            From = from;
            To = to;
        }

        public ActorRef From { get; }
        public ActorRef To { get; }
    }

    /// <summary>
    /// The actor gets the item if it lacks it and uses it (G3 Use → <c>_AI_UseInventoryItem</c>). The hero's use applies the
    /// item's effects; an NPC's use refills its hit and stamina points (G3 RefreshHitPoints) and the item stays (G3 consumes
    /// only the player's). Both are shown (<see cref="ActorUsedItem"/>: the use motion with the item in the hand) and the
    /// info script waits for the motion (fix-153).
    /// </summary>
    public sealed class UseItem : DialogCommand
    {
        [JsonConstructor]
        public UseItem(ActorRef actor, string item)
        {
            Actor = actor;
            Item = item;
        }

        public ActorRef Actor { get; }
        public string Item { get; }
    }

    /// <summary>An actor uses an item in a conversation (drinks, eats, reads …): for the presentation's use motion.</summary>
    public readonly struct ActorUsedItem : IGameEvent
    {
        public ActorUsedItem(ActorId actor, string item)
        {
            Actor = actor;
            Item = item;
        }

        public ActorId Actor { get; }
        public string Item { get; }
    }

    public sealed class RemoveItemsHandler : CommandHandler<RemoveItems>
    {
        protected override CommandResult Execute(RemoveItems command, CommandContext context)
        {
            context.Services.Items.Inventories.Consume(context.Resolve(command.Holder), command.Item, command.Amount);
            return CommandResult.Continue;
        }
    }

    public sealed class GiveAllItemsHandler : CommandHandler<GiveAllItems>
    {
        protected override CommandResult Execute(GiveAllItems command, CommandContext context)
        {
            var inventories = context.Services.Items.Inventories;
            var from = context.Resolve(command.From);
            var to = context.Resolve(command.To);
            foreach (var stack in inventories.Of(from).Stacks.ToList())
                inventories.Transfer(from, to, stack.Item, stack.Amount, quality: stack.Quality);
            return CommandResult.Continue;
        }
    }

    public sealed class UseItemHandler : CommandHandler<UseItem>
    {
        /// <summary>
        /// How long a conversation waits for an NPC's item use, s: take out, use, lower, put away (the CP 1.75 motions of
        /// Orc/Hero_Stand_None_Potion|Meat|Letter_P0: 0.4 + 2.2–2.4 + 0.5–0.8 + 0.36 s plus the fades).
        /// </summary>
        public const float NpcUseSeconds = 4f;

        /// <summary>The hero's use in a talk, s: the use motion runs at double speed for the player (G3 sAIPlayAniInstr 2.0).</summary>
        public const float HeroUseSeconds = 1.6f;

        protected override CommandResult Execute(UseItem command, CommandContext context)
        {
            var items = context.Services.Items;
            var actor = context.Resolve(command.Actor);
            if (!actor.IsPlayer)
            {
                // CP _AI_UseInventoryItem, not the player: RefreshHitPoints (HP and SP to the maximum); no ConsumeItems.
                context.Services.Director.HealFully(actor);
                context.Events.Publish(new ActorUsedItem(actor, command.Item));
                // G3 _AI_UseInventoryItem is latent: the script goes on when the use motion has ended. The core does not
                // know the motion's length, so the conversation waits the typical take-out/use/put-away time [interp].
                return Wait(context, NpcUseSeconds);
            }
            if (!items.Inventories.Of(actor).Has(command.Item)) items.Inventories.Give(actor, command.Item, 1);
            HeroCollapsed? collapsed = null;
            bool used;
            using (context.Events.Subscribe<HeroCollapsed>(e => collapsed = e))
                used = items.Use.Use(command.Item);
            if (!used) context.Log.Info($"The hero cannot use '{command.Item}' here; it stays in the inventory.");
            // CP _AI_UseInventoryItem: when the hero collapses from the drink in a talk, the item's line is said (by his
            // current target, the partner, for Stollengrollen: fix-212)
            if (collapsed?.TalkLine == null || context.Conversation == null)
            {
                if (!used || collapsed != null) return CommandResult.Continue;
                context.Events.Publish(new ActorUsedItem(actor, command.Item));
                return Wait(context, HeroUseSeconds);
            }
            var speaker = collapsed.Value.PartnerSays ? context.Partner : ActorId.Player;
            var line = new DialogLine(speaker, new TextId(collapsed.Value.TalkLine), collapsed.Value.TalkVoice);
            context.Events.Publish(new DialogLineSpoken(context.Entry?.Id ?? default, context.LogQuest, line.Speaker, line.Text));
            context.Presenter.ShowLine(line);
            return CommandResult.WaitForLine;
        }

        static CommandResult Wait(CommandContext context, float seconds)
        {
            if (context.Conversation == null) return CommandResult.Continue;
            context.Conversation.RequestWait(seconds);
            return CommandResult.WaitForTime;
        }
    }
}
