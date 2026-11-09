using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Story;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Story;

static partial class Program
{
    /// <summary>
    /// G3Cli story coverage [--pack dir] [--md] [--examples n] — walks every dialog entry and quest of the content pack and
    /// reports what the story runtime cannot do yet: conditions it treats as unsupported (the entry stays hidden),
    /// commands it skips, commands without a handler, shared lines missing for the speaker's voice set and quest objectives
    /// nothing counts. Gaps are grouped by feature area; entries by region (the G3 info's editor folder, read from the
    /// install when present). Docs/story-coverage.md.
    /// </summary>
    static int StoryCoverage(string data, string[] args)
    {
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        bool markdown = args.Contains("--md");
        int examples = int.TryParse(Option(args, "--examples"), out int n) ? n : 3;

        var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
        var db = content.Database;
        var strings = content.Strings.Load("English");
        var voiceSets = new NpcVoiceSets(db);
        content.Database.TryGet(SharedLines.DefinitionId, out SharedLines sharedLines);
        var runner = CommandRunner.CreateDefault(sharedLines);
        var folders = InfoFolders(data);

        var entries = db.All<DialogEntryDef>();
        var reports = entries.Select(e => CoverageOf(e, db, runner, voiceSets, sharedLines, strings)).ToList();

        var quests = db.All<QuestDef>();
        var blockedQuests = new HashSet<Id<QuestDef>>();
        foreach (var r in reports.Where(r => !r.Full))
            foreach (var q in QuestsReferencedBy(r.Entry)) blockedQuests.Add(q);

        var w = new CoverageWriter(markdown);
        int full = reports.Count(r => r.Full), hidden = reports.Count(r => r.Hidden), degraded = reports.Count(r => !r.Full && !r.Hidden);
        w.Heading("Summary");
        w.Line($"pack {db.Packs.FirstOrDefault()?.Id} {db.Packs.FirstOrDefault()?.ContentHash?.Substring(0, 12)}: {entries.Count} dialog entries, {quests.Count} quests");
        w.Line($"fully supported entries: {full}/{entries.Count} ({Percent(full, entries.Count)}); hidden by an unsupported condition: {hidden}; " +
               $"offered but with skipped commands/lines: {degraded}");
        int trackedQuests = quests.Count(q => TrackedQuestKinds.Contains(q.Objective.Kind));
        int playableQuests = quests.Count(q => TrackedQuestKinds.Contains(q.Objective.Kind) && !blockedQuests.Contains(q.Id));
        w.Line($"quests whose objective the runtime tracks: {trackedQuests}/{quests.Count} ({Percent(trackedQuests, quests.Count)}); " +
               $"tracked and every entry referring to them fully supported: {playableQuests}/{quests.Count} ({Percent(playableQuests, quests.Count)})");

        w.Heading("Gaps by feature area");
        w.Table(new[] { "area", "gap", "kind", "entries", "uses", "examples" },
                reports.SelectMany(r => r.Gaps.Select(g => (r, g)))
                       .GroupBy(x => (Area: AreaOf(x.g.Feature), x.g.Feature, x.g.Kind))
                       .OrderBy(g => g.Key.Area).ThenByDescending(g => g.Select(x => x.r).Distinct().Count())
                       .Select(g => new[]
                       {
                           g.Key.Area, g.Key.Feature, g.Key.Kind, g.Select(x => x.r).Distinct().Count().ToString(), g.Count().ToString(),
                           string.Join(" ", g.Select(x => x.r.Entry.Id.Name).Distinct().Take(examples)),
                       }));

        w.Heading("Entries with gaps per area");
        w.Table(new[] { "area", "entries", "hidden", "degraded" },
                reports.Where(r => !r.Full)
                       .SelectMany(r => r.Gaps.Select(g => AreaOf(g.Feature)).Distinct().Select(a => (a, r)))
                       .GroupBy(x => x.a).OrderByDescending(g => g.Count())
                       .Select(g => new[] { g.Key, g.Count().ToString(), g.Count(x => x.r.Hidden).ToString(), g.Count(x => !x.r.Hidden).ToString() }));

        w.Heading("Quest objectives");
        w.Table(new[] { "kind", "quests", "tracked", "entries fully supported" },
                quests.GroupBy(q => q.Objective.Kind).OrderByDescending(g => g.Count())
                      .Select(g => new[]
                      {
                          g.Key.ToString(), g.Count().ToString(), TrackedQuestKinds.Contains(g.Key) ? "yes" : "no",
                          $"{g.Count(q => !blockedQuests.Contains(q.Id))}/{g.Count()}",
                      }));

        w.Heading("Per region (G3 editor folder)");
        w.Table(new[] { "region", "entries", "full", "%", "hidden", "degraded" },
                reports.GroupBy(r => RegionOf(folders, r.Entry)).OrderByDescending(g => g.Count())
                       .Select(g => new[]
                       {
                           g.Key, g.Count().ToString(), g.Count(r => r.Full).ToString(), Percent(g.Count(r => r.Full), g.Count()),
                           g.Count(r => r.Hidden).ToString(), g.Count(r => !r.Full && !r.Hidden).ToString(),
                       }));
        Console.Write(w);
        return 0;
    }

    /// <summary>
    /// Objective kinds something in the runtime counts (QuestItemCounter, QuestDefeatCounter, Delivery entries; BringNpc and
    /// FollowNpc by their Delivery entries' party and position tests and CompleteDelivery; EnterArea, Arena and FreeEnclave by
    /// QuestAreaCounter, QuestArenaCounter and QuestEnclaveCounter).
    /// </summary>
    static readonly HashSet<QuestObjectiveKind> TrackedQuestKinds = new HashSet<QuestObjectiveKind>
    {
        QuestObjectiveKind.None, QuestObjectiveKind.Report, QuestObjectiveKind.HasItems, QuestObjectiveKind.Plunder,
        QuestObjectiveKind.Kill, QuestObjectiveKind.Defeat, QuestObjectiveKind.DriveAway,
        QuestObjectiveKind.BringNpc, QuestObjectiveKind.FollowNpc,
        QuestObjectiveKind.EnterArea, QuestObjectiveKind.Arena, QuestObjectiveKind.FreeEnclave, // story-2: QuestPlaceCounters
    };

    sealed class EntryCoverage
    {
        public DialogEntryDef Entry;
        public readonly List<(string Kind, string Feature)> Gaps = new List<(string, string)>();
        public bool Hidden;
        public bool Full => Gaps.Count == 0;
    }

    static EntryCoverage CoverageOf(DialogEntryDef entry, IContentDatabase db, CommandRunner runner, NpcVoiceSets voiceSets, SharedLines sharedLines,
                                    Myrtana.Core.StringTable strings)
    {
        var r = new EntryCoverage { Entry = entry };
        foreach (var feature in UnsupportedIn(entry.Condition))
        {
            r.Gaps.Add(("condition", feature));
            r.Hidden = true;
        }
        var commands = entry.Commands.Concat((entry.PreChecks ?? Array.Empty<PreCheck>()).SelectMany(p => p.Refusal));
        foreach (var c in commands)
        {
            if (c is NotSupported ns) r.Gaps.Add(("command", ns.Command));
            else if (!runner.Handles(c.GetType())) r.Gaps.Add(("command", "no handler: " + c.GetType().Name));
            else if (c is SayShared shared && !SharedLineExists(shared, entry, db, voiceSets, sharedLines, strings))
                r.Gaps.Add(("line", "shared line missing for the voice set: " + shared.Line));
            else if (c is SetRoutine routine && !RoutineExists(routine, entry, db))
                r.Gaps.Add(("data", "SetRoutine of an NPC that is not in the world"));
        }
        return r;
    }

    static IEnumerable<string> UnsupportedIn(Condition c) => c switch
    {
        null => Enumerable.Empty<string>(),
        Unsupported u => new[] { u.Feature },
        AllOf all => all.Conditions.SelectMany(UnsupportedIn),
        AnyOf any => any.Conditions.SelectMany(UnsupportedIn),
        Not not => UnsupportedIn(not.Condition),
        _ => Enumerable.Empty<string>(),
    };

    /// <summary>The SVM text exists for the speaker's voice set (exactly or as the first random variant "_00").</summary>
    static bool SharedLineExists(SayShared line, DialogEntryDef entry, IContentDatabase db, NpcVoiceSets voiceSets, SharedLines sharedLines,
                                 Myrtana.Core.StringTable strings)
    {
        if (sharedLines == null || strings == null) return false;
        var speaker = line.Speaker.Resolve(entry.Owner);
        if (speaker.IsPlayer && line.Speaker.Equals(ActorRef.Partner)) return true; // the hero's own topic: partner known at runtime
        string voiceSet = voiceSets.VoiceSetOf(speaker);
        if (!db.TryGet(speaker.Name, out Myrtana.Simulation.NpcDef _)) return true; // an interactive object speaks no shared lines in G3 either
        if (string.IsNullOrEmpty(voiceSet)) return false;
        string name = line.LineFor(voiceSet);
        if (line.Voiced != null && name == line.Line) return true; // recorded for this voice set (no text: PartyWait)
        var text = sharedLines.TextOf(voiceSet, name).Value;
        return strings.TryGetText(text, out _) || strings.TryGetText(text + "_00", out _);
    }

    /// <summary>
    /// The NPC exists (as itself or as instances <c>Name#guid</c> of a shared name). A routine name it has no points for
    /// is not a gap: G3 clears the NPC's points and it stays where it stands, which NpcSimulation.SetRoutine now does.
    /// </summary>
    static bool RoutineExists(SetRoutine command, DialogEntryDef entry, IContentDatabase db)
    {
        if (string.Equals(command.Routine, "Start", StringComparison.OrdinalIgnoreCase)) return true;
        var npc = command.Actor.Resolve(entry.Owner);
        if (db.TryGet(npc.Name, out Myrtana.Simulation.NpcDef _)) return true;
        string prefix = npc.Name + "#";
        return db.All<Myrtana.Simulation.NpcDef>().Any(d => d.Id.Name.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Quests an entry's condition, commands or quest log refer to (any property of type Id&lt;QuestDef&gt;).</summary>
    static IEnumerable<Id<QuestDef>> QuestsReferencedBy(DialogEntryDef entry)
    {
        var found = new HashSet<Id<QuestDef>>();
        if (!entry.LogQuest.IsEmpty) found.Add(entry.LogQuest);
        void Visit(object node)
        {
            if (node == null) return;
            foreach (var p in node.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var value = p.GetValue(node);
                if (value is Id<QuestDef> q && !q.IsEmpty) found.Add(q);
                else if (value is Condition || value is DialogCommand) Visit(value);
                else if (value is System.Collections.IEnumerable list && !(value is string))
                    foreach (var item in list)
                        if (item is Condition || item is DialogCommand) Visit(item);
            }
        }
        Visit(entry.Condition);
        foreach (var c in entry.Commands) Visit(c);
        return found;
    }

    /// <summary>Gap name → feature area (first keyword that occurs in the name).</summary>
    static readonly (string Keyword, string Area)[] Areas =
    {
        ("unknown", "data"), ("no handler", "engine"),
        ("InfoWait", "cutscene / presentation"), ("ShowPicture", "cutscene / presentation"), ("ShowSubtitle", "cutscene / presentation"),
        ("PlayMusic", "cutscene / presentation"), ("EndGame", "cutscene / presentation"), ("StartEffect", "cutscene / presentation"),
        ("Attack", "combat"), ("Humanize", "combat"), ("Animal", "combat"), ("DriveAway", "combat"), ("Arena", "combat"),
        ("Warn", "crime / guards"), ("GuardStatus", "crime / guards"), ("PickPocket", "crime / theft"), ("Pickpocket", "crime / theft"),
        ("Party", "party"), ("Join", "party"), ("Dismiss", "party"), ("BringNpc", "party"), ("Mob", "party"), ("Slave", "party"),
        ("Wait", "party"), ("Companion", "party"),
        ("SectorStatus", "world state"), ("OpenDoor", "world state"), ("Spawn", "world state"),
        ("Revolution", "enclaves / revolution"), ("Raid", "enclaves / revolution"), ("RevolCount", "enclaves / revolution"),
        ("SetRoutine", "npc control"), ("FullStop", "npc control"), ("SetPAL", "factions / attitudes"), ("SetClass", "factions / attitudes"),
        ("PoliticalFriend", "reputation"), ("Reput", "reputation"), ("CondPAL", "reputation"),
        ("Sleep", "time"), ("Overtime", "time"), ("QuestRunning", "time"),
        ("near entity", "positions"), ("FollowNpc", "positions"),
        ("Use", "items"), ("Wear", "items"), ("Erase", "items"), ("GiveAll", "items"), ("Grant", "items"), ("item", "items"),
        ("Skill", "teach / skills"), ("Heal", "vitals"),
        ("shared line", "lines"), ("Say", "lines"), ("GameMessage", "lines"),
    };

    static string AreaOf(string feature)
    {
        foreach (var (keyword, area) in Areas)
            if (feature.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return area;
        return "other";
    }

    /// <summary>Info name → G3 editor folder (e.g. "Myrtana/Silden_City/"); empty when the install is not available.</summary>
    static Dictionary<string, string> InfoFolders(string data)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = new G3FileSystem(data, "Infos");
            foreach (var info in LoadInfos(fs, null)) map[info.Name] = info.Folder ?? "";
        }
        catch (Exception ex) when (ex is IOException || ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("G3 infos not readable (" + ex.Message + "); regions unknown.");
        }
        return map;
    }

    /// <summary>The folder below "Myrtana/" ("Silden_City"), the first folder otherwise; "PC_Hero" for the hero's own topics.</summary>
    static string RegionOf(Dictionary<string, string> folders, DialogEntryDef entry)
    {
        if (!folders.TryGetValue(entry.Id.Name, out var folder) || string.IsNullOrEmpty(folder)) return entry.Owner.IsPlayer ? "PC_Hero" : "?";
        var parts = folder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return parts.Length > 1 && parts[0].Equals("Myrtana", StringComparison.OrdinalIgnoreCase) ? parts[1] : parts[0];
    }

    static string Percent(int part, int total) => total == 0 ? "-" : $"{100.0 * part / total:F1}%";

    /// <summary>Plain text or Markdown tables.</summary>
    sealed class CoverageWriter
    {
        readonly bool _markdown;
        readonly System.Text.StringBuilder _out = new System.Text.StringBuilder();

        public CoverageWriter(bool markdown) => _markdown = markdown;

        public void Heading(string text) => _out.AppendLine().AppendLine(_markdown ? "### " + text : "== " + text);

        public void Line(string text) => _out.AppendLine(_markdown ? "* " + text : text);

        public void Table(string[] header, IEnumerable<string[]> rows)
        {
            var all = rows.ToList();
            if (_markdown)
            {
                _out.AppendLine().AppendLine("| " + string.Join(" | ", header) + " |");
                _out.AppendLine("|" + string.Concat(header.Select(_ => "---|")));
                foreach (var row in all) _out.AppendLine("| " + string.Join(" | ", row) + " |");
                return;
            }
            var widths = header.Select((h, i) => Math.Min(48, all.Select(r => r[i].Length).Append(h.Length).Max())).ToArray();
            _out.AppendLine(string.Join("  ", header.Select((h, i) => h.PadRight(widths[i]))));
            foreach (var row in all) _out.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))));
        }

        public override string ToString() => _out.ToString();
    }
}
