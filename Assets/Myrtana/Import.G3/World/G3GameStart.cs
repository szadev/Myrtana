using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Genome;
using G3.Formats.World;
using Myrtana.Simulation;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The G3 (CP 1.75) new-game state as content (<see cref="GameStartDef"/>, Docs/game-start.md):
    /// <list type="bullet">
    /// <item>hero pose: the world's <c>PC_Hero</c> entity (SysDyn; ge3.ini <c>PlayerStart=PC_Hero</c>,
    /// gCSession::GotoStartPosition) — in Ardea next to Diego, Milten and Gorn;</item>
    /// <item>clock: <c>World_MCP</c> gCClock_PS Day/Hour/Minute (day 0, 12:00);</item>
    /// <item>the script facts of OnGameStartUp (Script_Game.dll; no data file): chapter 1, Larson's routine "Start",
    /// enclave Ardea Orc with Raid and Revolution, NotifyEnclave in raid mode by Ardea_Orkboss, quest Xardas_FindXardas.
    /// The script also spawns <c>Ardea_Spawn_Point</c> (a CP helper template whose only info gives 500 XP: carried by the
    /// start kit, <see cref="G3HeroStart.StartXpOf"/>) and sets the hero's attributes
    /// (ProgressionRules.HeroStart) and kit (<see cref="G3HeroStart"/>).</item>
    /// </list>
    /// </summary>
    public static class G3GameStart
    {
        const string ClockEntity = "World_MCP";
        const float Cm = 0.01f;

        const int Chapter = 1;
        static readonly string[] Quests = { "Xardas_FindXardas" };
        static readonly (string Npc, string Routine)[] Routines = { ("Larson", "Start") };
        static readonly (string Enclave, string Alignment, bool Raid, bool Revolution, string AlarmBy)[] Enclaves =
        {
            ("Ardea", "Orc", true, true, "Ardea_Orkboss"),
        };
        /// <summary>The four friends of the intro, Friend-type NPCs placed around the start (Lester further down at the shore).</summary>
        static readonly string[] Companions = { "Diego", "Milten", "Gorn", "Lester" };

        /// <param name="spawns">The world's NPC spawns (NpcImporter.Spawns, with PC_Hero).</param>
        /// <param name="knownNpcs">Actor ids of the imported NPCs (to check the script's names).</param>
        public static GameStartDef Import(G3Install install, IEnumerable<NpcSpawn> spawns, ICollection<string> knownNpcs,
                                          ICollection<string> knownQuests, ImportReport report)
        {
            var hero = spawns.FirstOrDefault(s => string.Equals(s.Name, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase));
            var pose = default(WorldPose);
            if (hero?.World == null || hero.World.Length < 16) report.Count("game start: no PC_Hero spawn (hero pose 0,0,0)");
            else pose = HeroPose(hero.World);

            int day = 0;
            float hour = 12;
            if (TryReadClock(install, out int d, out int h, out int m)) { day = d; hour = h + m / 60f; }
            else report.Count("game start: World_MCP clock not found (12:00 assumed)");

            foreach (var n in Routines.Select(r => r.Npc).Concat(Enclaves.Select(e => e.AlarmBy)).Concat(Companions))
                if (!knownNpcs.Contains(n)) report.Count("game start: unknown NPC", n);
            foreach (var q in Quests)
                if (!knownQuests.Contains(q)) report.Count("game start: unknown quest", q);

            report.Note($"game start: hero at ({pose.X:0.0}, {pose.Y:0.0}, {pose.Z:0.0}) yaw {pose.Yaw:0}, day {day} {hour:0.##} h");
            return new GameStartDef(GameStartDef.DefinitionId, pose, day, hour, Chapter, Quests,
                                    Routines.Select(r => new RoutineChange(r.Npc, r.Routine)).ToList(),
                                    Enclaves.Select(e => new EnclaveStart(e.Enclave, e.Alignment, e.Raid, e.Revolution, e.AlarmBy)).ToList(),
                                    Companions);
        }

        /// <summary>Position and heading of a G3 world matrix (row-major, cm; the entity faces its +Z row).</summary>
        public static WorldPose HeroPose(float[] m) =>
            new WorldPose(m[12] * Cm, m[13] * Cm, m[14] * Cm, (float)(Math.Atan2(m[8], m[10]) * 180 / Math.PI));

        /// <summary>World_MCP's gCClock_PS in the SysDyn entity files.</summary>
        static bool TryReadClock(G3Install install, out int day, out int hour, out int minute)
        {
            day = hour = minute = 0;
            using (var fs = install.Open("Projects_compiled"))
            {
                foreach (var f in fs.Files.Where(f => f.Path.IndexOf("SysDyn", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                     f.Path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase)))
                {
                    List<WorldEntity> entities;
                    try { entities = EntityArchive.Read(f.Read(), false, keepRaw: true); }
                    catch (Exception) { continue; }
                    var clock = entities?.Where(e => e.Name == ClockEntity)
                                         .SelectMany(e => e.RawPropertySets ?? new List<PropertyObject>())
                                         .FirstOrDefault(ps => ps.ClassName == "gCClock_PS");
                    if (clock == null) continue;
                    day = clock.Get("Day")?.AsInt() ?? 0;
                    hour = clock.Get("Hour")?.AsInt() ?? 12;
                    minute = clock.Get("Minute")?.AsInt() ?? 0;
                    return true;
                }
            }
            return false;
        }
    }
}
