using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    public enum ReactionKind
    {
        None,
        /// <summary>Go and watch the fight from 550–650 and cheer (ZS_WatchFight).</summary>
        Watch,
        /// <summary>Run away from <see cref="Reaction.Target"/>.</summary>
        Flee,
        /// <summary>Say something about it (a friend of the winner).</summary>
        Comment,
        /// <summary>Keep an eye on <see cref="Reaction.Target"/> (ZS_ObserveSuspect).</summary>
        Observe,
        /// <summary>Warn <see cref="Reaction.Target"/>; attack after the second warning (ZS_ReactToWeapon/Zone).</summary>
        Warn,
        /// <summary>Fight <see cref="Reaction.Target"/> for <see cref="Reaction.Reason"/>.</summary>
        Attack,
        /// <summary>Walk to the intruder in one's home (ZS_GotoPlayerZone).</summary>
        GoToIntruder,
        /// <summary>
        /// Start a conversation with <see cref="Reaction.Target"/> where both stand (G3 AssessTalk: an important info;
        /// FullStop + ZS_Talk, the NPC turns in place — no walk-up).
        /// </summary>
        Talk,
    }

    /// <summary>What an NPC does about something it perceived.</summary>
    public readonly struct Reaction
    {
        public static readonly Reaction None = default;

        Reaction(ReactionKind kind, ActorId target, AttackReason reason, string line = null, ActorId other = default)
        {
            Kind = kind;
            Target = target;
            Reason = reason;
            Line = line;
            Other = other;
        }

        public ReactionKind Kind { get; }
        public ActorId Target { get; }
        public AttackReason Reason { get; }
        /// <summary>The shared line the NPC says with it ("WeaponDown", "WiseMove" …); null = the presentation picks one.</summary>
        public string Line { get; }
        /// <summary>
        /// The second actor of the event: the fight's victim for <see cref="ReactionKind.Watch"/> (G3 ZS_WatchFight targets
        /// both fighters), the loser for <see cref="ReactionKind.Comment"/> (the winner is <see cref="Target"/>).
        /// </summary>
        public ActorId Other { get; }

        public static Reaction Attack(ActorId target, AttackReason reason) => new Reaction(ReactionKind.Attack, target, reason);
        public static Reaction Of(ReactionKind kind, ActorId target) => new Reaction(kind, target, AttackReason.None);

        /// <summary>A reaction with the reason behind it (a warning about a weapon or an intruder; the presentation picks the words).</summary>
        public static Reaction Of(ReactionKind kind, ActorId target, AttackReason reason) => new Reaction(kind, target, reason);

        /// <summary>A reaction that comes with a given shared line (G3 SVM line name, e.g. "ISaidWeaponDown").</summary>
        public static Reaction Of(ReactionKind kind, ActorId target, AttackReason reason, string line) => new Reaction(kind, target, reason, line);

        /// <summary>Watch the fight of <paramref name="attacker"/> against <paramref name="victim"/>.</summary>
        public static Reaction Watch(ActorId attacker, ActorId victim) => new Reaction(ReactionKind.Watch, attacker, AttackReason.None, other: victim);

        /// <summary>Comment on <paramref name="winner"/>'s defeat (or killing) of <paramref name="loser"/> (StartSayDefeatComment).</summary>
        public static Reaction Comment(ActorId winner, ActorId loser) => new Reaction(ReactionKind.Comment, winner, AttackReason.None, other: loser);

        public override string ToString() => Kind == ReactionKind.Attack ? $"Attack {Target} ({Reason})" : $"{Kind} {Target}";
    }

    /// <summary>An NPC reacted to a perception (for the presentation: voice lines, walking to watch, fleeing).</summary>
    public readonly struct NpcReacted : IGameEvent
    {
        public NpcReacted(ActorId npc, Reaction reaction, BroadcastKind? cause)
        {
            Npc = npc;
            Reaction = reaction;
            Cause = cause;
        }

        public ActorId Npc { get; }
        public Reaction Reaction { get; }
        /// <summary>The broadcast reacted to; null = the NPC's own active perception.</summary>
        public BroadcastKind? Cause { get; }
    }
}
