using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>G3 SetSectorStatus: a world sector (entity layer) is switched on or off.</summary>
    public sealed class SetSectorStatus : DialogCommand
    {
        [JsonConstructor]
        public SetSectorStatus(string sector, bool enabled)
        {
            Sector = sector;
            Enabled = enabled;
        }

        public string Sector { get; }
        public bool Enabled { get; }
    }

    /// <summary>G3 OpenDoor: the named door opens.</summary>
    public sealed class OpenDoor : DialogCommand
    {
        [JsonConstructor]
        public OpenDoor(string door) => Door = door;

        public string Door { get; }
    }

    /// <summary>G3 SetRaidEnabled / SetRevolutionEnabled on an enclave (named directly or by one of its members).</summary>
    public sealed class SetEnclaveFlag : DialogCommand
    {
        [JsonConstructor]
        public SetEnclaveFlag(string enclave, EnclaveFlag flag, bool value)
        {
            Enclave = enclave;
            Flag = flag;
            Value = value;
        }

        public string Enclave { get; }
        public EnclaveFlag Flag { get; }
        public bool Value { get; }
    }

    /// <summary>G3 StartRevolution (CP): the actor's enclave raids and goes into Attack.</summary>
    public sealed class StartRevolution : DialogCommand
    {
        [JsonConstructor]
        public StartRevolution(ActorRef member) => Member = member;

        public ActorRef Member { get; }
    }

    /// <summary>G3 SetClass: the NPC's class (Mage, Paladin, Warrior, Ranger; anything else none).</summary>
    public sealed class SetClass : DialogCommand
    {
        [JsonConstructor]
        public SetClass(ActorRef actor, string npcClass)
        {
            Actor = actor;
            NpcClass = npcClass;
        }

        public ActorRef Actor { get; }
        public string NpcClass { get; }
    }

    /// <summary>
    /// CP JoinNPC (info command 63): <see cref="Member"/> becomes a Controlled member of <see cref="Leader"/>'s party and
    /// follows it (ZS_FollowNPC) while the leader travels to its destination (ZS_TravelToDestination).
    /// </summary>
    public sealed class JoinNpc : DialogCommand
    {
        [JsonConstructor]
        public JoinNpc(ActorRef member, ActorRef leader)
        {
            Member = member;
            Leader = leader;
        }

        public ActorRef Member { get; }
        public ActorRef Leader { get; }
    }

    /// <summary>The port for NPC-led parties (implemented by the session's world director; absent = JoinNPC is logged and ignored).</summary>
    public interface INpcPartyDirector
    {
        bool JoinNpc(ActorId member, ActorId leader);
    }

    public sealed class JoinNpcHandler : CommandHandler<JoinNpc>
    {
        protected override CommandResult Execute(JoinNpc command, CommandContext context)
        {
            if (context.Services.World is INpcPartyDirector parties)
                parties.JoinNpc(context.Resolve(command.Member), context.Resolve(command.Leader));
            return CommandResult.Continue;
        }
    }

    /// <summary>G3 SetGuardStatus: "Active" → the gate guard watches again, anything else → Inactive.</summary>
    public sealed class SetGuardStatus : DialogCommand
    {
        [JsonConstructor]
        public SetGuardStatus(ActorRef guard, bool active)
        {
            Guard = guard;
            Active = active;
        }

        public ActorRef Guard { get; }
        public bool Active { get; }
    }

    /// <summary>
    /// What G3 does after a FirstWarn / SecondWarn info (gCInfo_PS OnEndInfo): the guard's status becomes FirstWarnGiven /
    /// SecondWarnGiven and it remembers how far the player is from its guard point.
    /// </summary>
    public sealed class GateGuardWarned : DialogCommand
    {
        [JsonConstructor]
        public GateGuardWarned(ActorRef guard, bool second = false)
        {
            Guard = guard;
            Second = second;
        }

        public ActorRef Guard { get; }
        public bool Second { get; }
    }

    /// <summary>The running entry remembers the game time (CP QuestRunning infos repeat every 2 game hours).</summary>
    public sealed class RememberRun : DialogCommand { }

    /// <summary>
    /// G3 Pickpocket(player, victim): the hero tries to rob the victim (Rules/Crime/Pickpocket.cs). Too strong a victim →
    /// the hero says "TooHard" / "Impossible"; caught → the victim attacks (Theft, by <see cref="IPickpocket"/>) and the
    /// dialog ends.
    /// </summary>
    public sealed class Pickpocket : DialogCommand
    {
        [JsonConstructor]
        public Pickpocket(ActorRef victim) => Victim = victim;

        public ActorRef Victim { get; }
    }

    public sealed class GateGuardWarnedHandler : CommandHandler<GateGuardWarned>
    {
        protected override CommandResult Execute(GateGuardWarned command, CommandContext context)
        {
            context.Services.World.GateGuardWarned(context.Resolve(command.Guard), command.Second);
            return CommandResult.Continue;
        }
    }

    public sealed class RememberRunHandler : CommandHandler<RememberRun>
    {
        protected override CommandResult Execute(RememberRun command, CommandContext context)
        {
            if (context.Entry != null) context.World.DialogMemory.MarkRun(context.Entry.Id, context.Quests.GameSeconds);
            return CommandResult.Continue;
        }
    }

    public sealed class PickpocketHandler : CommandHandler<Pickpocket>
    {
        protected override CommandResult Execute(Pickpocket command, CommandContext context)
        {
            var victim = context.Resolve(command.Victim);
            switch (context.Services.Pickpocket.Pickpocket(victim))
            {
                case PickpocketVerdict.TooHard:
                    context.Conversation.RunNext(new DialogCommand[] { new SayShared(ActorRef.Player, PickpocketRules.TooHardLine) });
                    break;
                case PickpocketVerdict.Impossible:
                    context.Conversation.RunNext(new DialogCommand[] { new SayShared(ActorRef.Player, PickpocketRules.ImpossibleLine) });
                    break;
                case PickpocketVerdict.Caught:
                    // G3 PickPocket: AssessTarget(victim, hero, Theft) (the pickpocket port does it), then PSInfoManager::End
                    context.Conversation.RunNext(new DialogCommand[] { new EndDialog() });
                    break;
            }
            return CommandResult.Continue;
        }
    }

    public sealed class SetSectorStatusHandler : CommandHandler<SetSectorStatus>
    {
        protected override CommandResult Execute(SetSectorStatus command, CommandContext context)
        {
            if (!string.IsNullOrEmpty(command.Sector)) context.Services.World.SetSectorEnabled(command.Sector, command.Enabled);
            return CommandResult.Continue;
        }
    }

    public sealed class OpenDoorHandler : CommandHandler<OpenDoor>
    {
        protected override CommandResult Execute(OpenDoor command, CommandContext context)
        {
            if (!context.Services.World.OpenDoor(command.Door)) context.Log.Info($"OpenDoor: '{command.Door}' is not a closed door; skipped.");
            return CommandResult.Continue;
        }
    }

    public sealed class SetEnclaveFlagHandler : CommandHandler<SetEnclaveFlag>
    {
        protected override CommandResult Execute(SetEnclaveFlag command, CommandContext context)
        {
            // G3 resolves the entity to the partner when the info names "npc"; an imported info names the enclave directly.
            string enclave = string.Equals(command.Enclave, ActorRef.Partner.Value) ? context.Partner.Name : command.Enclave;
            if (!string.IsNullOrEmpty(enclave)) context.Services.World.SetEnclaveFlag(enclave, command.Flag, command.Value);
            return CommandResult.Continue;
        }
    }

    public sealed class StartRevolutionHandler : CommandHandler<StartRevolution>
    {
        protected override CommandResult Execute(StartRevolution command, CommandContext context)
        {
            context.Services.World.StartRevolution(context.Resolve(command.Member));
            return CommandResult.Continue;
        }
    }

    public sealed class SetClassHandler : CommandHandler<SetClass>
    {
        protected override CommandResult Execute(SetClass command, CommandContext context)
        {
            context.Services.World.SetClass(context.Resolve(command.Actor), command.NpcClass);
            return CommandResult.Continue;
        }
    }

    public sealed class SetGuardStatusHandler : CommandHandler<SetGuardStatus>
    {
        protected override CommandResult Execute(SetGuardStatus command, CommandContext context)
        {
            context.Services.World.SetGuardActive(context.Resolve(command.Guard), command.Active);
            return CommandResult.Continue;
        }
    }

    /// <summary>Registers the world-state handlers (kept here so the default runner's list stays short).</summary>
    public static class WorldStateHandlers
    {
        public static void Register(CommandRunner runner)
        {
            runner.Register(new SetSectorStatusHandler());
            runner.Register(new OpenDoorHandler());
            runner.Register(new SetEnclaveFlagHandler());
            runner.Register(new StartRevolutionHandler());
            runner.Register(new SetClassHandler());
            runner.Register(new SetGuardStatusHandler());
            runner.Register(new JoinNpcHandler());
            runner.Register(new GateGuardWarnedHandler());
            runner.Register(new RememberRunHandler());
            runner.Register(new PickpocketHandler());
        }
    }
}
