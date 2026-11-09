using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// G3 FullStop (E1): the NPC drops what it is doing right now (a walk, an animation) and goes on with its routine
    /// (PSRoutine::FullStop). The script does not wait.
    /// </summary>
    public sealed class FullStop : DialogCommand
    {
        [JsonConstructor]
        public FullStop(ActorRef actor) => Actor = actor;

        public ActorRef Actor { get; }
    }

    /// <summary>
    /// CP Humanize (E1, ID1): the NPC's Immortal flag becomes the opposite of <see cref="Humanize"/> — true makes it
    /// mortal, false immortal (CP 1.75 sets PSNpc PropertyImmortal to the negated flag; [interp] the flag's source).
    /// </summary>
    public sealed class HumanizeActor : DialogCommand
    {
        [JsonConstructor]
        public HumanizeActor(ActorRef actor, bool humanize)
        {
            Actor = actor;
            Humanize = humanize;
        }

        public ActorRef Actor { get; }
        public bool Humanize { get; }
    }

    /// <summary>
    /// G3 Wear (E1, ID1): the NPC equips the item (CP creates it in the inventory first when missing) — a weapon is
    /// carried and fought with from now on, visible on the body. The script does not wait.
    /// </summary>
    public sealed class WearItem : DialogCommand
    {
        [JsonConstructor]
        public WearItem(ActorRef actor, string item)
        {
            Actor = actor;
            Item = item;
        }

        public ActorRef Actor { get; }
        /// <summary>Item template (an item id, or a body template such as <c>Fat_Body_NrdElite</c> that is no item).</summary>
        public string Item { get; }
    }

    /// <summary>An NPC put on an item through the story (<see cref="WearItem"/>): views refresh its carried equipment.</summary>
    public readonly struct ActorWoreItem : IGameEvent
    {
        public ActorWoreItem(ActorId actor, string item, bool isItem)
        {
            Actor = actor;
            Item = item;
            IsItem = isItem;
        }

        public ActorId Actor { get; }
        public string Item { get; }
        /// <summary>False for a template that is no item of the content (a body/armour mesh): only a view can show it.</summary>
        public bool IsItem { get; }
    }

    public sealed class FullStopHandler : CommandHandler<FullStop>
    {
        protected override CommandResult Execute(FullStop command, CommandContext context)
        {
            var npc = context.Resolve(command.Actor);
            if (!context.Services.Director.FullStop(npc)) context.Log.Info($"FullStop of {npc}: no such NPC in the world.");
            return CommandResult.Continue;
        }
    }

    public sealed class HumanizeActorHandler : CommandHandler<HumanizeActor>
    {
        protected override CommandResult Execute(HumanizeActor command, CommandContext context)
        {
            context.Services.World.SetImmortal(context.Resolve(command.Actor), !command.Humanize);
            return CommandResult.Continue;
        }
    }

    public sealed class WearItemHandler : CommandHandler<WearItem>
    {
        protected override CommandResult Execute(WearItem command, CommandContext context)
        {
            var npc = context.Resolve(command.Actor);
            if (npc.IsEmpty || npc.IsPlayer || string.IsNullOrEmpty(command.Item)) return CommandResult.Continue;
            bool isItem = context.Services.World.Wear(npc, command.Item);
            var inventories = context.Services.Items.Inventories;
            if (isItem && !inventories.Of(npc).Has(command.Item)) inventories.Give(npc, command.Item, 1);
            context.Events.Publish(new ActorWoreItem(npc, command.Item, isItem));
            return CommandResult.Continue;
        }
    }

    /// <summary>Registers the NPC control handlers (FullStop, Humanize, Wear).</summary>
    public static class NpcControlHandlers
    {
        public static void Register(CommandRunner runner)
        {
            runner.Register(new FullStopHandler());
            runner.Register(new HumanizeActorHandler());
            runner.Register(new WearItemHandler());
        }
    }
}
