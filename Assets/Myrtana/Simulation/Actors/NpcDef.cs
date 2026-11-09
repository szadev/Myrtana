using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// An actor known to the content: identity, shown name and voice set, plus what the world simulation needs to place
    /// and run it: species (selects the daily schedule), visual (the presentation's key for the model), the pose the
    /// level editor gave it and its routine points, and its combat facts (level, flags, weaponry).
    /// </summary>
    public sealed class NpcDef : IDefinition
    {
        [JsonConstructor]
        public NpcDef(ActorId id, TextId displayName, string voiceSet, string species = null, string visual = null,
                      WorldPose? spawn = null, NpcRoutineDef routine = null, NpcCombatDef combat = null,
                      IReadOnlyDictionary<string, NpcRoutineDef> routines = null, string sector = null, bool deadAtStart = false)
        {
            Id = id;
            DisplayName = displayName;
            VoiceSet = voiceSet;
            Species = species;
            Visual = visual;
            Spawn = spawn;
            Routine = routine;
            Combat = combat;
            Routines = routines;
            Sector = sector;
            DeadAtStart = deadAtStart;
        }

        public ActorId Id { get; }
        public TextId DisplayName { get; }
        /// <summary>Voice actor / ambient line set ("Orc_Warrior_Hard"); informational, clip keys are resolved at import.</summary>
        public string VoiceSet { get; }
        /// <summary>"Human", "Orc", "Wolf" … (selects the daily schedule).</summary>
        public string Species { get; }
        /// <summary>Key of the model in the presentation layer (for G3: the NPC prefab's visual key).</summary>
        public string Visual { get; }
        /// <summary>Where the world editor placed the actor; null = not placed in the world (e.g. the hero).</summary>
        public WorldPose? Spawn { get; }
        /// <summary>Working, relaxing and sleeping points; null = no routine (animals that roam, statues …).</summary>
        public NpcRoutineDef Routine { get; }
        /// <summary>Level, flags and weaponry for combat; null = not known (a level-0 fighter of its species).</summary>
        public NpcCombatDef Combat { get; }
        /// <summary>
        /// Further routines by name (G3 gCNavigation_PS RoutineNames with their points), chosen by the story's SetRoutine;
        /// null = none.
        /// </summary>
        public IReadOnlyDictionary<string, NpcRoutineDef> Routines { get; }
        /// <summary>
        /// The switchable world sector the NPC lives in (<see cref="WorldSectorDef"/>, G3 SetSectorStatus); null = it is
        /// always in the world.
        /// </summary>
        public string Sector { get; }
        /// <summary>
        /// A corpse from the start (G3 gCScriptRoutine_PS Routine "Rtn_Dead" → ZS_Dead: already dead, lying pose). fix-261:
        /// Ramirez, whom Ningal's quest sends the hero to find dead in his pit near the Mora Sul temple, stood there alive.
        /// </summary>
        public bool DeadAtStart { get; }

        /// <summary>The named routine's points; null when the NPC has no routine of that name.</summary>
        public NpcRoutineDef RoutineNamed(string name) =>
            name != null && Routines != null && Routines.TryGetValue(name, out var routine) ? routine : null;

        /// <summary>The same NPC with other combat facts (the importer adds the weaponry once the items are known).</summary>
        public NpcDef WithCombat(NpcCombatDef combat) =>
            new NpcDef(Id, DisplayName, VoiceSet, Species, Visual, Spawn, Routine, combat, Routines, Sector, DeadAtStart);

        /// <summary>The same NPC living in a switchable world sector.</summary>
        public NpcDef WithSector(string sector) =>
            new NpcDef(Id, DisplayName, VoiceSet, Species, Visual, Spawn, Routine, Combat, Routines, sector, DeadAtStart);

        /// <summary>The same NPC as a corpse from the start (<see cref="DeadAtStart"/>).</summary>
        public NpcDef AsDeadAtStart() =>
            DeadAtStart ? this : new NpcDef(Id, DisplayName, VoiceSet, Species, Visual, Spawn, Routine, Combat, Routines, Sector, true);

        string IDefinition.Key => Id.Name;
    }
}
