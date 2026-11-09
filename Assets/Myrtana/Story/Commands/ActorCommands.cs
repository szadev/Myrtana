using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>The attacker starts a fight against the target for the given reason (G3 info command Attack).</summary>
    public sealed class StartFight : DialogCommand
    {
        [JsonConstructor]
        public StartFight(ActorRef attacker, ActorRef target, AttackReason reason)
        {
            Attacker = attacker;
            Target = target;
            Reason = reason;
        }

        public ActorRef Attacker { get; }
        public ActorRef Target { get; }
        /// <summary>Duel, Enemy, ReactToWeapon, GateGuard, Revolution or Kill (anything else in G3).</summary>
        public AttackReason Reason { get; }
    }

    /// <summary>The NPC switches to another named routine (G3 SetRoutine).</summary>
    public sealed class SetRoutine : DialogCommand
    {
        [JsonConstructor]
        public SetRoutine(ActorRef actor, string routine)
        {
            Actor = actor;
            Routine = routine;
        }

        public ActorRef Actor { get; }
        public string Routine { get; }
    }

    /// <summary>The NPC's political alignment changes (G3 SetPAL: "Orc", "Nrd", "Reb", "Mid", "Ass", "Nom", "Out"; null = none).</summary>
    public sealed class SetAlignment : DialogCommand
    {
        [JsonConstructor]
        public SetAlignment(ActorRef actor, string alignment)
        {
            Actor = actor;
            Alignment = alignment;
        }

        public ActorRef Actor { get; }
        public string Alignment { get; }
    }

    /// <summary>The actor's hit points are restored (G3 Heal; the healer's gesture is presentation).</summary>
    public sealed class HealActor : DialogCommand
    {
        [JsonConstructor]
        public HealActor(ActorRef actor) => Actor = actor;

        public ActorRef Actor { get; }
    }

    /// <summary>The hero sleeps until the hour, then the conversation ends (G3 Sleep).</summary>
    public sealed class Sleep : DialogCommand
    {
        [JsonConstructor]
        public Sleep(int untilHour) => UntilHour = untilHour;

        public int UntilHour { get; }
    }

    public sealed class StartFightHandler : CommandHandler<StartFight>
    {
        protected override CommandResult Execute(StartFight command, CommandContext context)
        {
            var attacker = context.Resolve(command.Attacker);
            var target = context.Resolve(command.Target);
            if (!context.Services.Director.Attack(attacker, target, command.Reason))
                context.Log.Info($"{attacker} cannot attack {target}; Attack skipped.");
            return CommandResult.Continue;
        }
    }

    public sealed class SetRoutineHandler : CommandHandler<SetRoutine>
    {
        protected override CommandResult Execute(SetRoutine command, CommandContext context)
        {
            var npc = context.Resolve(command.Actor);
            if (!context.Services.Director.SetRoutine(npc, command.Routine))
                context.Log.Info($"Routine '{command.Routine}' of {npc} not set: no such NPC in the world.");
            return CommandResult.Continue;
        }
    }

    public sealed class SetAlignmentHandler : CommandHandler<SetAlignment>
    {
        protected override CommandResult Execute(SetAlignment command, CommandContext context)
        {
            var npc = context.Resolve(command.Actor);
            context.Services.Director.SetAlignment(npc, command.Alignment);
            // CP 1.75 SetPAL Reb on Wenzel also sets Kap Dun's Raid flag (fix-94, StoryAlignmentRules)
            string raid = StoryAlignmentRules.RaidEnclaveOf(npc.Name, command.Alignment);
            if (raid != null) context.Services.World.SetEnclaveFlag(raid, EnclaveFlag.Raid, true);
            return CommandResult.Continue;
        }
    }

    public sealed class HealActorHandler : CommandHandler<HealActor>
    {
        protected override CommandResult Execute(HealActor command, CommandContext context)
        {
            context.Services.Director.HealFully(context.Resolve(command.Actor));
            return CommandResult.Continue;
        }
    }

    public sealed class SleepHandler : CommandHandler<Sleep>
    {
        protected override CommandResult Execute(Sleep command, CommandContext context)
        {
            context.Services.Director.PlayerSleepsUntil(command.UntilHour);
            context.Conversation.RequestEnd();
            return CommandResult.Continue;
        }
    }
}
