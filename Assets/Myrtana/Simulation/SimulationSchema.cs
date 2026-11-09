using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>Content kinds of the simulation module.</summary>
    public static class SimulationSchema
    {
        public const string NpcFolder = "npcs";
        public const string RoutinePointFolder = "routinePoints";
        public const string AnchorFolder = "anchors";
        public const string ScheduleFolder = "schedules";
        public const string CombatAiFolder = "combatAi";
        public const string PerceptionRulesFolder = "perceptionRules";
        public const string GameStartFolder = "gameStart";
        public const string SectorFolder = "sectors";
        public const string GateGuardFolder = "gateGuards";
        public const string NavigationFolder = "navigation";

        public static void Register(ContentSchema schema)
        {
            schema.AddKind<NpcDef>(NpcFolder);
            schema.AddKind<RoutinePointDef>(RoutinePointFolder);
            schema.AddKind<AnchorDef>(AnchorFolder);
            schema.AddKind<RoutineScheduleDef>(ScheduleFolder);
            schema.AddKind<CombatAiRules>(CombatAiFolder);
            schema.AddKind<PerceptionRules>(PerceptionRulesFolder);
            schema.AddKind<GameStartDef>(GameStartFolder);
            schema.AddKind<WorldSectorDef>(SectorFolder);
            schema.AddKind<GateGuardDef>(GateGuardFolder);
            schema.AddKind<NavSectorDef>(NavigationFolder);
        }
    }
}
