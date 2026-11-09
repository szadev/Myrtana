using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// How a new game begins (content id "gameStart"; Docs/game-start.md): where the hero stands, the clock, the chapter,
    /// and what the G3 new-game script (OnGameStartUp) changes in the world — quests it runs, routines it sets and the
    /// enclaves it stirs up (Ardea's orc raid that becomes the first fight). The hero's attributes and kit are
    /// <c>ProgressionRules.HeroStart</c> and <c>HeroStartKit</c>.
    /// </summary>
    public sealed class GameStartDef : IDefinition
    {
        public const string DefinitionId = "gameStart";

        [JsonConstructor]
        public GameStartDef(string id, WorldPose heroPose, int day, float hour, int chapter, IReadOnlyList<string> quests,
                            IReadOnlyList<RoutineChange> routines, IReadOnlyList<EnclaveStart> enclaves, IReadOnlyList<string> companions)
        {
            Id = id;
            HeroPose = heroPose;
            Day = Math.Max(0, day);
            Hour = hour < 0 || hour >= 24 ? 0 : hour;
            Chapter = Math.Max(1, chapter);
            Quests = quests ?? Array.Empty<string>();
            Routines = routines ?? Array.Empty<RoutineChange>();
            Enclaves = enclaves ?? Array.Empty<EnclaveStart>();
            Companions = companions ?? Array.Empty<string>();
        }

        public string Id { get; }
        /// <summary>Where the hero stands and looks (G3: the PC_Hero entity of the world).</summary>
        public WorldPose HeroPose { get; }
        /// <summary>Clock at the start (G3: World_MCP gCClock_PS Day/Hour/Minute).</summary>
        public int Day { get; }
        /// <summary>0 ≤ hour &lt; 24 (minutes as a fraction).</summary>
        public float Hour { get; }
        public int Chapter { get; }
        /// <summary>Quests the start runs (Running).</summary>
        public IReadOnlyList<string> Quests { get; }
        public IReadOnlyList<RoutineChange> Routines { get; }
        public IReadOnlyList<EnclaveStart> Enclaves { get; }
        /// <summary>The hero's old friends who arrive with him (information for views and the party; not party members).</summary>
        public IReadOnlyList<string> Companions { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>An NPC switched to a named routine at the start.</summary>
    public sealed class RoutineChange
    {
        [JsonConstructor]
        public RoutineChange(string npc, string routine)
        {
            Npc = npc;
            Routine = routine;
        }

        public string Npc { get; }
        public string Routine { get; }
    }

    /// <summary>
    /// An enclave stirred up at the start (G3 OnGameStartUp: alignment, Raid, Revolution, then NotifyEnclave in raid mode
    /// by <see cref="AlarmBy"/>, which sends the enclave's nearest defenders at the player and its status to Attack).
    /// </summary>
    public sealed class EnclaveStart
    {
        [JsonConstructor]
        public EnclaveStart(string enclave, string alignment, bool raid, bool revolution, string alarmBy)
        {
            Enclave = enclave;
            Alignment = alignment;
            Raid = raid;
            Revolution = revolution;
            AlarmBy = alarmBy;
        }

        public string Enclave { get; }
        /// <summary>Political alignment the script gives the enclave (null = keep).</summary>
        public string Alignment { get; }
        public bool Raid { get; }
        public bool Revolution { get; }
        /// <summary>The member whose enclave is alarmed against the player (null = no alarm).</summary>
        public string AlarmBy { get; }
    }
}
