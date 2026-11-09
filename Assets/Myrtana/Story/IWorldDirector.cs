using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>The enclave flags the story switches (G3 SetRaidEnabled / SetRevolutionEnabled).</summary>
    public enum EnclaveFlag { Raid, Revolution }

    /// <summary>
    /// What story commands ask of the world beyond single actors' fights and routines (<see cref="IActorDirector"/>):
    /// world sectors, doors, enclave flags, NPC classes and gate guards. Implemented by the session
    /// (Session/WorldStateWiring.cs); tests pass fakes.
    /// </summary>
    public interface IWorldDirector
    {
        /// <summary>G3 SetSectorStatus: a world sector (an entity layer, e.g. "G3_Myrtana_01_Gotha_NPC_02") is switched on or off.</summary>
        void SetSectorEnabled(string sector, bool enabled);

        /// <summary>G3 OpenDoor: the named door opens; false when the entity is not a door.</summary>
        bool OpenDoor(string door);

        /// <summary>G3 SetRaidEnabled / SetRevolutionEnabled: <paramref name="enclaveOrMember"/> names the enclave (or one of its members).</summary>
        void SetEnclaveFlag(string enclaveOrMember, EnclaveFlag flag, bool value);

        /// <summary>G3 StartRevolution (CP): the member's enclave raids and goes into Attack.</summary>
        void StartRevolution(ActorId member);

        /// <summary>G3 SetClass: the NPC's class becomes "Mage", "Paladin", "Warrior", "Ranger" (anything else: none).</summary>
        void SetClass(ActorId npc, string npcClass);

        /// <summary>CP Humanize: the NPC's Immortal flag (cannot be killed) is set or cleared from now on.</summary>
        void SetImmortal(ActorId npc, bool immortal);

        /// <summary>
        /// G3 Wear: the NPC equips the item from now on (a weapon replaces its weapon of that kind). Returns whether the
        /// template is an item of the content (false: a body/armour template that only a view can show).
        /// </summary>
        bool Wear(ActorId npc, string item);

        /// <summary>G3 SetGuardStatus: the gate guard is Active, or Inactive (lets everybody pass).</summary>
        void SetGuardActive(ActorId guard, bool active);

        /// <summary>
        /// G3 condition types FirstWarn / SecondWarn: the gate guard may give its first warning (guard point, Active) or
        /// its second one (FirstWarnGiven and the player 80 closer to the guard point than at the first warning).
        /// </summary>
        bool GateGuardMayWarn(ActorId guard, bool second);

        /// <summary>A warning was given (G3 OnEndInfo): FirstWarnGiven / SecondWarnGiven and the player's distance noted.</summary>
        void GateGuardWarned(ActorId guard, bool second);
    }

    /// <summary>A world without sectors, doors or enclaves (tools, isolated tests): every request is logged and ignored.</summary>
    public sealed class NoWorldDirector : IWorldDirector
    {
        readonly IGameLog _log;

        public NoWorldDirector(IGameLog log = null) => _log = log;

        public void SetSectorEnabled(string sector, bool enabled) => Ignored($"sector {sector} {(enabled ? "on" : "off")}");
        public bool OpenDoor(string door) { Ignored($"door {door} opens"); return false; }
        public void SetEnclaveFlag(string enclaveOrMember, EnclaveFlag flag, bool value) => Ignored($"{enclaveOrMember} {flag} {value}");
        public void StartRevolution(ActorId member) => Ignored($"revolution of {member}'s enclave");
        public void SetClass(ActorId npc, string npcClass) => Ignored($"{npc} class {npcClass}");
        public void SetImmortal(ActorId npc, bool immortal) => Ignored($"{npc} immortal {immortal}");
        public bool Wear(ActorId npc, string item) { Ignored($"{npc} wears {item}"); return false; }
        public void SetGuardActive(ActorId guard, bool active) => Ignored($"{guard} guard {(active ? "active" : "inactive")}");
        public bool GateGuardMayWarn(ActorId guard, bool second) => false;
        public void GateGuardWarned(ActorId guard, bool second) => Ignored($"{guard} warned");

        void Ignored(string what) => _log?.Info($"No world director: '{what}' ignored.");
    }
}
