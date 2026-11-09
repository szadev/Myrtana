namespace Myrtana.Core
{
    /// <summary>
    /// An NPC was killed or knocked out. Published by whatever resolves fights (the combat module); experience,
    /// quests (Kill/Defeat objectives) and crime listen to it. The publisher fills in what it knows about the victim.
    /// </summary>
    public readonly struct ActorDefeated : IGameEvent
    {
        public ActorDefeated(ActorId victim, bool byPlayerParty, bool killed, int victimLevel, bool ambientCreature, string species,
                             bool hurtByPlayer = false)
        {
            Victim = victim;
            ByPlayerParty = byPlayerParty;
            Killed = killed;
            VictimLevel = victimLevel;
            AmbientCreature = ambientCreature;
            Species = species;
            HurtByPlayer = hurtByPlayer;
        }

        /// <summary>The NPC's unique id (a spawned creature's instance id when it has no unique name).</summary>
        public ActorId Victim { get; }
        /// <summary>Defeated by the player or a party member the player leads (not by a summoned creature).</summary>
        public bool ByPlayerParty { get; }
        /// <summary>True = killed, false = knocked out.</summary>
        public bool Killed { get; }
        /// <summary>The victim's Level property.</summary>
        public int VictimLevel { get; }
        public bool AmbientCreature { get; }
        public string Species { get; }
        /// <summary>The player's own hits took HP from the victim before it fell (any killer) [owner, fix-93].</summary>
        public bool HurtByPlayer { get; }
    }
}
