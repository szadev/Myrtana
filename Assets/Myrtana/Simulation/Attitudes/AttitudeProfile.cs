using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>How an actor belongs to a party (G3 gEPartyMemberType).</summary>
    public enum PartyRole { None, Member, Mob, Slave, Controlled, Summoned }

    /// <summary>
    /// Who an actor is, as far as attitudes, crime and witnesses need it: identity, species, politics, enclave and
    /// the momentary facts (party, flight, transformation). Built by <see cref="IAttitudeFacts"/> on demand.
    /// </summary>
    public struct AttitudeProfile
    {
        public ActorId Id;
        public bool IsPlayer;
        public string Species;
        public bool Humanoid;
        public bool Evil;
        public bool AmbientCreature;
        /// <summary>Never fights; flees instead (ambient creatures, deer, sheep …).</summary>
        public bool CannotFight;
        /// <summary>The NPC's own political alignment ("Orc", "Reb" …; G3 gCNPC_PS PoliticalAlignment), for the player that
        /// of the worn armour; null = none.</summary>
        public string Alignment;
        /// <summary>The story set <see cref="Alignment"/> (G3 SetPAL, a town's switch, a liberation): it then counts over the
        /// enclave's (fix-94).</summary>
        public bool AlignmentFromStory;
        /// <summary>The enclave (village, camp, city) the NPC belongs to; null = none.</summary>
        public string Enclave;
        /// <summary>The political alignment of <see cref="Enclave"/>.</summary>
        public string EnclaveAlignment;
        public NpcRole Role;
        /// <summary>A political leader of an enclave (G3 IsBoss).</summary>
        public bool Boss;
        /// <summary>Friend-type NPC (G3 NPCType Friend): mirrors the player's side.</summary>
        public bool FriendType;
        /// <summary>The imported attitude to the player (null = decided by the rules).</summary>
        public Attitude? StoredAttitudeToPlayer;
        /// <summary>The leader of the party the actor belongs to; empty = none.</summary>
        public ActorId PartyLeader;
        public PartyRole PartyRole;
        /// <summary>A party member told to wait: it does not defend its leader (G3 PSParty.Waiting).</summary>
        public bool PartyWaiting;
        /// <summary>Fleeing from <see cref="FleeingFrom"/>.</summary>
        public bool Fleeing;
        public ActorId FleeingFrom;
        /// <summary>The player in an animal's shape (no political or crime opinion about it).</summary>
        public bool Transformed;

        /// <summary>
        /// A guard of an enclave (G3 IsGuard): type Guard, an enclave, and its own alignment that of the enclave (or Pirate).
        /// An NPC of type Guard without an alignment of its own (Phil and Seruk at Kap Dun, the Okara rangers before the hero
        /// recruits them …) is no guard in G3 (fix-94).
        /// </summary>
        public bool Guard => Role == NpcRole.Guard && Enclave != null && (Alignment == EnclaveAlignment || Alignment == PirateAlignment);

        /// <summary>G3 IsGuard's exception: a pirate guards whatever enclave it is in.</summary>
        public const string PirateAlignment = "Pirate";
        public bool InPlayerParty => PartyLeader.IsPlayer;
    }

    /// <summary>Resolves actors to their <see cref="AttitudeProfile"/> (the session builds them from content and state).</summary>
    public interface IAttitudeFacts
    {
        bool TryGetProfile(ActorId actor, out AttitudeProfile profile);
    }
}
