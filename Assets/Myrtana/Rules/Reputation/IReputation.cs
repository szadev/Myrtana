using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>The player's standing (fame, 0–100) with groups of NPCs.</summary>
    public interface IReputation
    {
        /// <summary>Fame with the enclave (village, camp, city) the actor belongs to.</summary>
        int EnclaveFameOf(ActorId member);

        /// <summary>Fame with the political faction (Orc, Nrd, Reb …) the actor is aligned with.</summary>
        int PoliticalFameOf(ActorId member);
    }

    /// <summary>
    /// Placeholder until enclaves, fame and crime are implemented (slice 8): the player has no fame anywhere, so every
    /// fame gate refuses, as in a new G3 game.
    /// </summary>
    public sealed class NoReputation : IReputation
    {
        public int EnclaveFameOf(ActorId member) => 0;

        public int PoliticalFameOf(ActorId member) => 0;
    }
}
