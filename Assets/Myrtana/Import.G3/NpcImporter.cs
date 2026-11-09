using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.World;
using Myrtana.Core;
using Myrtana.Simulation;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// NPC definitions from the world's spawn entities, with placement and routine points (<see cref="NpcPlacementImporter"/>).
    /// A unique name is the actor id ("Gnar"); spawns sharing a name ("Orc_Warrior_01") get "Name#guid8".
    /// The G3 hero ("PC_Hero") becomes the <see cref="ActorId.Player"/> actor.
    /// </summary>
    public sealed class NpcImporter
    {
        public const string G3HeroName = "PC_Hero";
        const string HeroVoiceSet = "Player";
        /// <summary>G3's AI routine of a corpse placed in the world (ZS_Dead: "already dead, force the lying pose").</summary>
        public const string DeadRoutine = "Rtn_Dead";

        public List<NpcSpawn> Spawns { get; private set; } = new List<NpcSpawn>();
        /// <summary>Routine points and anchors used by the imported NPCs (valid after <see cref="Import"/>).</summary>
        public NpcPlacementImporter Placement { get; private set; }
        /// <summary>The world's interaction points, anchors and named AI helpers read with the spawns.</summary>
        public RoutinePoints Points { get; private set; }
        /// <summary>The world entity of each imported NPC (valid after <see cref="Import"/>).</summary>
        public Dictionary<ActorId, NpcSpawn> SpawnOf { get; } = new Dictionary<ActorId, NpcSpawn>();

        /// <summary>
        /// The world sectors the story switches (SetSectorStatus, valid after <see cref="Import"/>): every sector that is off
        /// at the start, every sector an info switches, and each town's "_NPC_01" whose "_NPC_02" replaces it on liberation.
        /// </summary>
        public List<WorldSectorDef> Sectors { get; private set; } = new List<WorldSectorDef>();

        /// <param name="enclaveName">Resolves an NPC's enclave entity GUID to the enclave's name (null = unknown).</param>
        /// <param name="storySectors">Sectors named by the infos' SetSectorStatus lines (null = none).</param>
        public List<NpcDef> Import(G3Install install, StringsImporter strings, ImportReport report, Func<Guid, string> enclaveName = null,
                                   ISet<string> storySectors = null)
        {
            Spawns = LoadSpawns(install, report, storySectors, out var points, out var sectors);
            Sectors = sectors;
            var switchable = new HashSet<string>(sectors.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            Placement = new NpcPlacementImporter(points, report);
            Points = points;
            var defs = new List<NpcDef> { new NpcDef(ActorId.Player, DisplayName(strings, G3HeroName), HeroVoiceSet) };
            foreach (var group in Spawns.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool shared = group.Count() > 1;
                if (shared) report.Count("NPC spawns with a shared name (id = name#guid)", group.Key);
                // The one spawn outside the switchable sectors keeps the plain name, so the story's references stay valid
                // when a story sector adds namesakes (Kaelin_Orks: the Fireclan raiders; G3 finds the first entity by name).
                var always = group.Where(x => !switchable.Contains(x.Sector ?? "")).ToList();
                var primary = shared && always.Count == 1 ? always[0] : null;
                foreach (var spawn in group)
                {
                    if (string.Equals(spawn.Name, G3HeroName, StringComparison.OrdinalIgnoreCase))
                    {
                        // The hero's world entity: an actor for its dialog topics, but not placed (the player is).
                        defs.Add(new NpcDef(new ActorId(spawn.Name), DisplayName(strings, spawn.Name), spawn.Voice));
                        continue;
                    }
                    var id = shared && spawn != primary ? NpcPlacementImporter.SharedNameId(spawn) : new ActorId(spawn.Name);
                    SpawnOf[id] = spawn;
                    var def = Placement.Define(spawn, id, DisplayName(strings, spawn.Name)).WithCombat(NpcCombatImporter.FromSpawn(spawn, enclaveName?.Invoke(spawn.EnclaveId)));
                    if (string.Equals(spawn.ScriptRoutine, DeadRoutine, StringComparison.OrdinalIgnoreCase))
                    {
                        def = def.AsDeadAtStart();
                        report.Count("NPC dead at the start (gCScriptRoutine_PS Rtn_Dead)", id.Name);
                    }
                    defs.Add(switchable.Contains(spawn.Sector ?? "") ? def.WithSector(spawn.Sector) : def);
                }
            }
            return defs;
        }

        static List<NpcSpawn> LoadSpawns(G3Install install, ImportReport report, ISet<string> storySectors, out RoutinePoints points,
                                         out List<WorldSectorDef> sectors)
        {
            const string volume = "Projects_compiled";
            using (var fs = install.Open(volume))
            {
                var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(volume + "/" + p)?.Read(), includeDisabled: true);
                sectors = SwitchableSectors(layout, storySectors);
                foreach (var s in sectors) report.Count(s.EnabledAtStart ? "switchable world sector (on at start)" : "switchable world sector (off at start)", s.Id);
                var errors = new List<string>();
                var all = NpcSpawns.LoadWorld(layout, p => fs.Get(volume + "/" + p)?.Read(), errors);
                var guardPoints = new HashSet<Guid>(all.Select(s => s.GuardPoint).Where(g => g != Guid.Empty));
                points = RoutinePoints.LoadWorld(layout, p => fs.Get(volume + "/" + p)?.Read(), errors, guardPoints);
                foreach (var e in errors) report.Count("unreadable world entity file", e);
                var off = new HashSet<string>(layout.Sectors.Where(s => !s.Enabled).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
                // A switched-off sector's entities are all marked disabled; they enter the world when the story switches it on.
                return all.Where(s => (s.Enabled || off.Contains(s.Sector ?? "")) && !string.IsNullOrEmpty(s.Name) && !NpcSpawns.IsTestSector(s.Sector)).ToList();
            }
        }

        /// <summary>
        /// Sectors off at the start, sectors the infos switch, each "X_NPC_01" that has an "X_NPC_02", and the sectors of
        /// CP's liberation table (<see cref="EnclaveSectorSwitches"/>: also Nemora/Okara/Reddock/Vangard _NPC_01 and the
        /// Mora Sul temple's _NSCs_01).
        /// </summary>
        static List<WorldSectorDef> SwitchableSectors(WorldLayout layout, ISet<string> storySectors)
        {
            var names = new HashSet<string>(layout.Sectors.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            var liberation = new HashSet<string>(EnclaveSectorSwitches.Sectors, StringComparer.OrdinalIgnoreCase);
            var list = new List<WorldSectorDef>();
            foreach (var s in layout.Sectors)
            {
                if (NpcSpawns.IsTestSector(s.Name)) continue;
                bool liberationPair = s.Name.EndsWith("_NPC_01", StringComparison.OrdinalIgnoreCase) &&
                                      names.Contains(s.Name.Substring(0, s.Name.Length - 2) + "02");
                if (!s.Enabled || liberationPair || liberation.Contains(s.Name) || (storySectors != null && storySectors.Contains(s.Name)))
                    list.Add(new WorldSectorDef(s.Name, s.Enabled));
            }
            return list.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>G3 gEGuardStatus Inactive: the guard lets everybody pass from the start.</summary>
        const int GuardStatusInactive = 3;

        /// <summary>
        /// The gate guards (valid after <see cref="Import"/>): NPCs whose gCNPC_PS.GuardPoint names a world point (an
        /// interaction point or anchor), with the point's position and whether they watch at the start.
        /// </summary>
        public List<GateGuardDef> GateGuards(ImportReport report)
        {
            var list = new List<GateGuardDef>();
            if (Points == null) return list;
            foreach (var kv in SpawnOf.OrderBy(k => k.Key.Name, StringComparer.Ordinal))
            {
                var spawn = kv.Value;
                if (spawn.GuardPoint == Guid.Empty) continue;
                float[] world = Points.Tracked.TryGetValue(spawn.GuardPoint, out var tracked) ? tracked
                              : Points.Points.TryGetValue(spawn.GuardPoint, out var point) ? point.World : null;
                if (world == null || world.Length < 16)
                {
                    report.Count("guard point entity not found in the world (NPC is no gate guard)", kv.Key.Name);
                    continue;
                }
                const float cm = 0.01f;
                list.Add(new GateGuardDef(kv.Key, new WorldPose(world[12] * cm, world[13] * cm, world[14] * cm, 0f),
                                          spawn.GuardStatus != GuardStatusInactive));
                report.Count(spawn.GuardStatus == GuardStatusInactive ? "gate guard (inactive at start)" : "gate guard", kv.Key.Name);
            }
            return list;
        }

        static TextId DisplayName(StringsImporter strings, string name) => strings.Resolve("FO_" + name);
    }
}
