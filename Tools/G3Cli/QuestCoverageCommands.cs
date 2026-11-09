using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Myrtana.Core;
using Myrtana.Session.Dev;
using Myrtana.Story;

static partial class Program
{
    /// <summary>One quest's row of Docs/quest-coverage.md.</summary>
    sealed class QuestCoverageRow
    {
        public string Quest, Region, Type, Giver, Prereqs, Core, Covered, Verdict;
    }

    /// <summary>
    /// "Core" quests: the main story and every quest its starters need (<see cref="StoryWalkBuilder.MainQuests"/>), the
    /// liberations (revolutions, wipe-outs, enclave raids: they gate the Innos/Adanos endings and the towns' traders) and
    /// the faction access/joining quests (Ishtar's test and gate, temple/enclave access, joining a camp).
    /// </summary>
    static string CoreReason(QuestDef q, IReadOnlyCollection<string> main, ISet<string> gates)
    {
        string id = q.Id.Name;
        if ((q.Folder ?? "").StartsWith("Hauptstory", StringComparison.OrdinalIgnoreCase)) return "main";
        if (main.Contains(id) || gates.Contains(id)) return "gates main";
        if ((q.Folder ?? "").StartsWith("TestQuests", StringComparison.OrdinalIgnoreCase)) return null;
        if (q.Objective.Kind == QuestObjectiveKind.FreeEnclave || Regex.IsMatch(id, "_(Revolution|Wipeout|WipeOut)$")) return "liberation";
        if (Regex.IsMatch(id, "(Access|_Join|^Ishtar_Test$)")) return "faction";
        return null;
    }

    /// <summary>Quest ids a scenario asserts (<c>expect</c> / <c>wait until=</c> on <c>quest:Q</c>), per file.</summary>
    static Dictionary<string, SortedSet<string>> ScenarioQuestAsserts(string dir)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetFileNameWithoutExtension(path);
            if (file == "spots" || file == "spots.manual") continue;
            ScenarioSpec spec;
            try { spec = ScenarioParser.ParseJson(File.ReadAllText(path), file); }
            catch { continue; }
            foreach (var st in spec.Steps)
            {
                string cond = st.Op == "expect" ? st.Get("cond") : st.Op == "wait" ? st.Get("until") : null;
                if (cond == null || !ScenarioCondition.TryParse(cond, out var c, out _) || c.Kind != "quest" || c.Arg == null) continue;
                if (!map.TryGetValue(c.Arg, out var set)) map[c.Arg] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(file);
            }
        }
        return map;
    }

    static string ScenarioDir()
    {
        string dir = Path.Combine(Environment.CurrentDirectory, "Tools", "Scenarios");
        return Directory.Exists(dir) ? dir : Path.Combine(Project, "Tools", "Scenarios");
    }

    /// <summary>The coverage rows of every quest (results = QuestCheck of all quests).</summary>
    static List<QuestCoverageRow> QuestCoverageRows(IContentDatabase db, StoryWalk walk, List<QuestCheckResult> results)
    {
        var verdicts = LoadVerdicts();
        var main = StoryWalkBuilder.MainQuests(db);
        // the quests the static walk's way to each main-story quest's Success needs (Ishtar's test for Zuben, ...)
        var gates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in main)
            foreach (var rule in walk.ChainTo(WalkFacts.Quest(m, "Success")))
                foreach (var f in rule.Need.Facts().Where(f => f.StartsWith("quest:", StringComparison.Ordinal)))
                    gates.Add(f.Substring(6, f.IndexOf('=') - 6));
        var asserts = ScenarioQuestAsserts(ScenarioDir());
        var quests = db.All<QuestDef>().ToDictionary(q => q.Id.Name, StringComparer.Ordinal);
        var rows = new List<QuestCoverageRow>();
        foreach (var r in results)
        {
            var q = quests[r.Quest];
            string running = WalkFacts.Quest(r.Quest, "Running");
            var starter = walk.ProducerOf(running) ?? walk.ProducersOf(running).FirstOrDefault();
            string giver = starter == null ? (r.CountedWhileOpen ? "(counted while open)" : "-")
                : starter.Kind == "entry" ? (starter.Owner == "PC_Hero" ? "hero" : QuestCheck.BaseName(starter.Owner)) + " " + starter.Id
                : starter.Kind + " " + starter.Id;
            var pre = q.Prerequisites.Select(p => p.Name).ToList();
            if (starter != null)
                pre.AddRange(starter.Need.Facts().Where(f => f.StartsWith("quest:", StringComparison.Ordinal))
                                    .Select(f => f.Substring(6, f.IndexOf('=') - 6)).Where(f => f != r.Quest));
            pre = pre.Distinct().ToList();
            string verdict = null;
            if (!r.Ok)
            {
                var causes = r.Defects.Concat(r.Roots).ToList();
                var why = causes.Select(c => verdicts.FirstOrDefault(v => v.match == r.Quest || c.StartsWith(v.match, StringComparison.Ordinal)))
                                .Where(v => v.match != null).Select(v => v.why).FirstOrDefault();
                verdict = "not testable: " + (why ?? string.Join("; ", causes.Take(2)));
            }
            asserts.TryGetValue(r.Quest, out var files);
            rows.Add(new QuestCoverageRow
            {
                Quest = r.Quest,
                Region = r.Region,
                Type = q.Objective.Kind.ToString(),
                Giver = giver,
                Prereqs = pre.Count == 0 ? "" : string.Join(", ", pre.Take(4)) + (pre.Count > 4 ? $" (+{pre.Count - 4})" : ""),
                Core = CoreReason(q, main, gates),
                Covered = files == null ? null : string.Join(", ", files.Take(3)) + (files.Count > 3 ? $" (+{files.Count - 3})" : ""),
                Verdict = verdict,
            });
        }
        return rows;
    }

    /// <summary>
    /// <c>story walk --quests --coverage [--out Docs/quest-coverage.md]</c>: every quest with its region, giver (the entry
    /// that starts it), prerequisites, objective type, core flag, and the scenarios that assert its status; quests no
    /// scenario covers are "not testable" (with the reason: dead in CP 1.75) or left to the owner's play-test (side content).
    /// </summary>
    static int QuestCoverage(IContentDatabase db, StoryWalk walk, List<QuestCheckResult> results, string[] args)
    {
        var rows = QuestCoverageRows(db, walk, results);
        string outPath = Option(args, "--out") ?? Path.Combine(Path.GetDirectoryName(ScenarioDir()), "..", "Docs", "quest-coverage.md");
        outPath = Path.GetFullPath(outPath);
        static string Cell(string s) => (s ?? "").Replace("|", "\\|");
        string Status(QuestCoverageRow x) =>
            x.Covered != null ? "covered" : x.Verdict != null ? "not testable" : x.Core != null ? "**open (core)**" : "manual (side content)";

        var sb = new StringBuilder();
        sb.AppendLine("# Quest coverage by dev scenarios");
        sb.AppendLine();
        sb.AppendLine("Generated by `dotnet run --project Tools/G3Cli -- story walk --quests --coverage` (Tools/G3Cli/QuestCoverageCommands.cs)");
        sb.AppendLine("from the content pack and `Tools/Scenarios/*.json`. Regenerate after adding scenarios or re-importing content.");
        sb.AppendLine();
        sb.AppendLine("- **Covered**: a scenario asserts the quest's status (`expect quest:Q=…` or `wait until=quest:Q…`). Setting a status");
        sb.AppendLine("  with `quest Q S` is setup, not coverage.");
        sb.AppendLine("- **Core**: `main` (folder Hauptstory), `gates main` (a main-story starter needs it, `StoryWalkBuilder.MainQuests`),");
        sb.AppendLine("  `liberation` (revolutions, wipe-outs, enclave raids), `faction` (access/joining: Ishtar test and gate, temple and");
        sb.AppendLine("  enclave access, joining a camp). Core quests get scenarios (`chapter_*`, `sideq_*`, `core_*`).");
        sb.AppendLine("- **Not testable**: the static walk (`story walk --quests`) finds the quest dead in the CP 1.75 data too (no starter,");
        sb.AppendLine("  missing NPC, test quest); the reason comes from `Tools/Scenarios/side_quest_verdicts.tsv`.");
        sb.AppendLine("- **Manual (side content)**: not core and not covered — the owner play-tests side content himself.");
        sb.AppendLine();
        sb.AppendLine("## Totals");
        sb.AppendLine();
        sb.AppendLine("| | quests | covered | open | not testable | manual |");
        sb.AppendLine("|---|---|---|---|---|---|");
        void Total(string label, IEnumerable<QuestCoverageRow> set)
        {
            var l = set.ToList();
            sb.AppendLine($"| {label} | {l.Count} | {l.Count(x => Status(x) == "covered")} | {l.Count(x => Status(x).StartsWith("**open"))} | " +
                          $"{l.Count(x => Status(x) == "not testable")} | {l.Count(x => Status(x).StartsWith("manual"))} |");
        }
        Total("all", rows);
        Total("core", rows.Where(x => x.Core != null));
        foreach (var k in new[] { "main", "gates main", "liberation", "faction" }) Total("core: " + k, rows.Where(x => x.Core == k));
        Total("side", rows.Where(x => x.Core == null));
        sb.AppendLine();
        sb.AppendLine("## By region");
        sb.AppendLine();
        sb.AppendLine("| region | quests | core | covered | core covered | not testable |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var g in rows.GroupBy(x => x.Region).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(x => x.Core != null)} | {g.Count(x => x.Covered != null)} | " +
                          $"{g.Count(x => x.Core != null && x.Covered != null)} | {g.Count(x => x.Covered == null && x.Verdict != null)} |");
        sb.AppendLine();
        sb.AppendLine("## Quests");
        sb.AppendLine();
        sb.AppendLine("| quest | region | type | giver (starter entry) | prerequisites | core | status | scenarios / reason |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var x in rows.OrderBy(x => x.Region, StringComparer.Ordinal).ThenBy(x => x.Quest, StringComparer.Ordinal))
            sb.AppendLine($"| {Cell(x.Quest)} | {Cell(x.Region)} | {x.Type} | {Cell(x.Giver)} | {Cell(x.Prereqs)} | {x.Core ?? ""} | {Status(x)} | " +
                          $"{Cell(x.Covered ?? (x.Verdict != null ? x.Verdict.Substring("not testable: ".Length) : ""))} |");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllText(outPath, sb.ToString().Replace("\r\n", "\n"));

        var core = rows.Where(x => x.Core != null).ToList();
        Console.WriteLine($"{rows.Count} quests: {rows.Count(x => x.Covered != null)} covered, {rows.Count(x => Status(x) == "not testable")} not testable; " +
                          $"core {core.Count}: {core.Count(x => x.Covered != null)} covered, {core.Count(x => Status(x).StartsWith("**open"))} open -> {outPath}");
        foreach (var x in core.Where(x => Status(x).StartsWith("**open")))
            Console.WriteLine($"  open core [{x.Core}] {x.Region} {x.Quest} ({x.Type}, giver {x.Giver})");
        return 0;
    }

    /// <summary>Uncovered core quests that the static walk finishes (the input of <c>--scenarios --core</c>).</summary>
    static HashSet<string> OpenCoreQuests(IContentDatabase db, StoryWalk walk, List<QuestCheckResult> results) =>
        new HashSet<string>(QuestCoverageRows(db, walk, results).Where(x => x.Core != null && x.Covered == null && x.Verdict == null).Select(x => x.Quest),
                            StringComparer.Ordinal);
}
