using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// An NPC stopped fighting because its target got away (G3 CancelAttack: chase beyond the give-up distance). Against
    /// the player this leaves a grudge (Docs/perception-crime.md §6.3).
    /// </summary>
    public readonly struct FightGivenUp : IGameEvent
    {
        public FightGivenUp(ActorId npc, ActorId target, AttackReason reason)
        {
            Npc = npc;
            Target = target;
            Reason = reason;
        }

        public ActorId Npc { get; }
        public ActorId Target { get; }
        public AttackReason Reason { get; }
    }
}
