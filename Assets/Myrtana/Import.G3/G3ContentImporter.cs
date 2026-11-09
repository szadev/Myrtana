using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Story;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Builds the "Gothic3" content pack from a Gothic 3 installation: string tables of every language, NPCs, quests and
    /// all dialog entries (one file per owner), plus <c>import-report.txt</c>. Pure C#: runs from G3Cli and the Unity
    /// editor menu alike.
    /// </summary>
    public static class G3ContentImporter
    {
        public const string PackId = "Gothic3";
        public const string ReportFile = "import-report.txt";
        /// <summary>Speech volumes whose clips are referenced by the pack (the first one wins for clip keys).</summary>
        public static readonly string[] VoiceLanguages = { "Russian", "English" };
        /// <summary>Owner of the developers' test topics (Infos/Test_Quests), not part of the game.</summary>
        const string TestStoryHelper = "sh";

        /// <summary>The world sectors the infos switch (SetSectorStatus ID1).</summary>
        static HashSet<string> StorySectors(IEnumerable<global::G3.Formats.Story.Info> infos)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in infos)
            foreach (var line in info.Lines)
                if (string.Equals(line.Command, "SetSectorStatus", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(line.Id1))
                    set.Add(line.Id1.Trim());
            return set;
        }

        public static ImportReport Run(string dataDirectory, string packDirectory)
        {
            var report = new ImportReport();
            var install = new G3Install(dataDirectory);
            var strings = new StringsImporter(install.LoadStrings());
            var quests = new QuestImporter(install.LoadQuests(report));
            var zones = new G3ZoneImporter();
            zones.Import(install, report);
            var navigation = new G3NavImporter();
            navigation.Import(install, report);
            var infos = install.LoadInfos(report).Where(i => !string.Equals(i.Owner, TestStoryHelper, StringComparison.Ordinal)).ToList();
            var npcImporter = new NpcImporter();
            var npcs = npcImporter.Import(install, strings, report, zones.EnclaveNameOf, StorySectors(infos));
            var actorsByG3Name = ActorIndex(npcs);
            var voiceSets = npcs.ToDictionary(n => n.Id, n => n.VoiceSet);
            var items = G3ItemContent.Import(install, strings, npcImporter.Spawns, report, npcImporter.Sectors.Select(s => s.Id), npcImporter.SpawnOf,
                                             new HashSet<string>(infos.Select(i => i.Owner).Where(o => !string.IsNullOrEmpty(o) && !actorsByG3Name.ContainsKey(o) &&
                                                                 !string.Equals(o, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase));
            npcs = NpcCombatImporter.AddWeaponry(npcs, npcImporter.SpawnOf, items, report);

            var volumes = VoiceLanguages.Select(l => new SpeechVolume(install, l, strings.Exists)).Where(v => v.ClipCount > 0).ToList();
            try
            {
                var infoImporter = new InfoImporter(strings, quests, new VoiceLines(volumes), actorsByG3Name,
                                                    id => voiceSets.TryGetValue(id, out var v) ? v : null, items.Names, report,
                                                    AlignmentIndex(npcs));
                var names = infos.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
                var entries = infos.Select(i => infoImporter.Translate(i, names)).ToList();

                var writer = new ContentPackWriter(packDirectory, GameSchema.Create());
                writer.WriteDefinitions(SimulationSchema.NpcFolder, "npcs", npcs.OrderBy(n => n.Id.Name, StringComparer.Ordinal));
                npcImporter.Placement.AddListenerPoints();
                writer.WriteDefinitions(SimulationSchema.RoutinePointFolder, "routinePoints", npcImporter.Placement.Points);
                writer.WriteDefinitions(SimulationSchema.SectorFolder, "sectors", npcImporter.Sectors);
                writer.WriteDefinitions(SimulationSchema.GateGuardFolder, "gateGuards", npcImporter.GateGuards(report));
                writer.WriteDefinitions(RulesSchema.PickpocketFolder, "pickpockets", NpcCombatImporter.Pickpockets(npcImporter.SpawnOf, items, report));
                writer.WriteDefinitions(RulesSchema.NpcInventoryFolder, "npcInventories", NpcInventoryImporter.Import(npcImporter.SpawnOf, items, report));
                writer.WriteDefinitions(SimulationSchema.AnchorFolder, "anchors", npcImporter.Placement.Anchors);
                writer.WriteDefinitions(RulesSchema.TeleportFolder, "teleports", TeleportImporter.Destinations(npcImporter.Points, report));
                writer.WriteDefinitions(RulesSchema.PlaceFolder, "places",
                                        PlaceImporter.Places(npcImporter.Points, infos, quests.Sources,
                                                             n => actorsByG3Name.ContainsKey(n) || string.Equals(n, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase),
                                                             report));
                writer.WriteDefinitions(SimulationSchema.ScheduleFolder, "schedule", new[] { G3RoutineTables.Schedule() });
                writer.WriteDefinitions(RulesSchema.AttributeFolder, "attributes", G3RulesImporter.AttributeDefinitions(strings));
                writer.WriteDefinitions(RulesSchema.ProgressionFolder, "progression", new[] { G3RulesImporter.Progression() });
                writer.WriteDefinitions(RulesSchema.CombatRulesFolder, "combat", new[] { G3RulesImporter.Combat() });
                writer.WriteDefinitions(SimulationSchema.CombatAiFolder, "combatAi", new[] { G3CombatAiImporter.CombatAi() });
                writer.WriteDefinitions(SimulationSchema.PerceptionRulesFolder, "perception", new[] { G3CrimeImporter.Perception() });
                writer.WriteDefinitions(RulesSchema.AttitudeRulesFolder, "attitudes", new[] { G3CrimeImporter.Attitudes() });
                writer.WriteDefinitions(RulesSchema.CrimeRulesFolder, "crime", new[] { G3CrimeImporter.Crime() });
                writer.WriteDefinitions(RulesSchema.EnclaveFolder, "enclaves", zones.Enclaves.OrderBy(e => e.Id, StringComparer.Ordinal));
                writer.WriteDefinitions(RulesSchema.AiZoneFolder, "aiZones", zones.Zones.OrderBy(z => z.Id, StringComparer.Ordinal));
                foreach (var sector in navigation.Sectors)
                    writer.WriteDefinitions(SimulationSchema.NavigationFolder, SafeFileName(sector.Id), new[] { sector });
                items.Write(writer);
                writer.WriteDefinitions(RulesSchema.HeroStartFolder, "heroStart", new[] { G3HeroStart.Kit(items.Names, report, G3HeroStart.StartXpOf(entries)) });
                var questDefs = quests.Import(strings, report);
                writer.WriteDefinitions(RulesSchema.FameGroupFolder, "fameGroups", G3RulesImporter.FameGroups(strings, questDefs, report));
                writer.WriteDefinitions(StorySchema.QuestFolder, "quests", questDefs);
                writer.WriteDefinitions(StorySchema.TriggerFolder, "storyTriggers", new[] { G3StoryTriggersImporter.Rules() });
                writer.WriteDefinitions(SimulationSchema.GameStartFolder, "gameStart", new[] { G3GameStart.Import(install, npcImporter.Spawns,
                    new HashSet<string>(npcs.Select(n => n.Id.Name), StringComparer.Ordinal), new HashSet<string>(questDefs.Select(q => q.Id.Name), StringComparer.Ordinal), report) });
                foreach (var owner in entries.GroupBy(e => e.Owner.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
                    writer.WriteDefinitions(StorySchema.DialogFolder, SafeFileName(owner.Key), owner.OrderBy(e => e.Order));
                var languages = WriteStrings(writer, strings);

                report.Note($"{npcs.Count} NPCs, {entries.Count} dialog entries ({entries.Select(e => e.Owner).Distinct().Count()} owners), " +
                            $"languages {string.Join(", ", languages)}, speech volumes {string.Join(", ", volumes.Select(v => $"{v.Language} ({v.ClipCount} clips)"))}");
                writer.Finish(new ContentPackManifest(PackId, "Gothic 3 (Community Patch 1.75)", 1, ContentPackManifest.CurrentFormat, null, languages));
                File.WriteAllText(Path.Combine(packDirectory, ReportFile), report.ToString());
            }
            finally
            {
                foreach (var v in volumes) v.Dispose();
            }
            return report;
        }

        static List<string> WriteStrings(ContentPackWriter writer, StringsImporter strings)
        {
            var written = new List<string>();
            foreach (var language in strings.Languages)
            {
                var table = strings.Export(language);
                if (table == null) continue;
                writer.WriteStrings(language, table);
                written.Add(language);
            }
            return written;
        }

        /// <summary>The political alignment of each NPC (for the PoliticalFriend refusal line).</summary>
        static Func<ActorId, string> AlignmentIndex(IEnumerable<NpcDef> npcs)
        {
            var map = npcs.Where(n => n.Combat?.Faction != null).ToDictionary(n => n.Id, n => n.Combat.Faction);
            return id => map.TryGetValue(id, out var faction) ? faction : null;
        }

        static Dictionary<string, ActorId> ActorIndex(IEnumerable<NpcDef> npcs)
        {
            var index = new Dictionary<string, ActorId>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in npcs.Where(n => !n.Id.IsPlayer)) index[n.Id.Name] = n.Id;
            return index;
        }

        static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
