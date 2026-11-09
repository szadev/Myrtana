using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Story;

static partial class Program
{
    /// <summary>
    /// <c>story walk --quests [--region filter] [--quest name] [-v] [--top N] [--class a|b|c|?]</c>: every quest of the pack
    /// against the static walk (Myrtana.Session.Dev.QuestCheck). Per region: quests, ok, blocked, and the classes of their
    /// blockers; then the root causes (facts nothing produces, quest-definition defects) with the quests they block.
    /// Classes come from Tools/Scenarios/side_quest_verdicts.tsv (match prefix → a port bug / b as in CP 1.75 / c editor
    /// check); unmatched causes print as "?". -v lists every blocked quest with its causes.
    /// </summary>
    static int QuestsCheck(IContentDatabase db, StoryWalkBuilder builder, StoryWalk walk, string[] args)
    {
        string region = Option(args, "--region"), only = Option(args, "--quest"), cls = Option(args, "--class");
        bool verbose = args.Contains("-v");
        int top = int.TryParse(Option(args, "--top"), out int t) ? t : 25;
        var verdicts = LoadVerdicts();
        var check = new QuestCheck(db, builder, walk);
        var results = check.All().Where(r => (region == null || r.Region.IndexOf(region, StringComparison.OrdinalIgnoreCase) >= 0) &&
                                             (only == null || r.Quest == only)).ToList();
        if (args.Contains("--coverage")) return QuestCoverage(db, walk, results, args);
        if (args.Contains("--scenarios")) return QuestScenarios(walk, args.Contains("--core") ? CoreOnly(db, walk, results) : results, args);
        if (args.Contains("--play")) return QuestsPlay(db, walk, results, verbose, top, args.Contains("--steps"));

        (string cls, string why) Classify(string quest, string cause)
        {
            foreach (var v in verdicts)
                if (v.match == quest || cause.StartsWith(v.match, StringComparison.Ordinal)) return (v.cls, v.why);
            return ("?", null);
        }
        IEnumerable<string> Causes(QuestCheckResult r) => r.Defects.Concat(r.Roots);
        string QuestClass(QuestCheckResult r)
        {
            var c = Causes(r).Select(x => Classify(r.Quest, x).cls).ToList();
            if (c.Count == 0) return r.Notes.Count > 0 ? "c" : "ok";
            foreach (var k in new[] { "a", "?", "c", "b" }) if (c.Contains(k)) return k;
            return c[0];
        }

        Console.WriteLine($"{results.Count} quests: {results.Count(r => r.Ok)} ok, {results.Count(r => !r.Starts)} never start, " +
                          $"{results.Count(r => r.Starts && !r.Succeeds)} never succeed, {results.Count(r => r.Defects.Count > 0)} with defects, " +
                          $"{results.Count(r => r.Ok && r.Notes.Count > 0)} ok with unprovable conditions");
        Console.WriteLine("region                     quests  ok  blocked  a  b  c  ?   (c also counts ok quests with unprovable conditions)");
        foreach (var g in results.GroupBy(r => r.Region).OrderBy(g => g.Key))
        {
            var k = g.Select(QuestClass).ToList();
            Console.WriteLine($"{g.Key,-26} {g.Count(),6} {g.Count(r => r.Ok),3} {g.Count(r => !r.Ok),8} {k.Count(x => x == "a"),2} {k.Count(x => x == "b"),2} {k.Count(x => x == "c"),2} {k.Count(x => x == "?"),2}");
        }

        Console.WriteLine("== causes (quests blocked)");
        var causes = results.SelectMany(r => Causes(r).Select(c => (c, r))).GroupBy(x => x.c)
                            .Select(g => (cause: g.Key, quests: g.Select(x => x.r.Quest).Distinct().ToList(), v: Classify(g.First().r.Quest, g.Key)))
                            .Where(x => cls == null || x.v.cls == cls)
                            .OrderByDescending(x => x.quests.Count).ThenBy(x => x.cause).ToList();
        foreach (var (cause, quests, v) in causes.Take(top))
            Console.WriteLine($"{quests.Count,3} [{v.cls}] {cause}: {string.Join(",", quests.Take(4))}{(quests.Count > 4 ? ",…" : "")}" + (v.why != null ? "  — " + v.why : ""));
        if (causes.Count > top) Console.WriteLine($"… {causes.Count - top} more causes (--top N)");

        Console.WriteLine($"== dead entries of quests that start ({results.Sum(r => r.DeadEntries.Count)} entries, by cause)");
        var dead = results.SelectMany(r => r.DeadEntries.SelectMany(d =>
                              d.Substring(d.IndexOf(": ", StringComparison.Ordinal) + 2).Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(c => (c, entry: d.Substring(0, d.IndexOf(": ", StringComparison.Ordinal)), r))))
                          .GroupBy(x => x.c)
                          .Select(g => (cause: g.Key, entries: g.Select(x => x.entry + "(" + x.r.Quest + ")").Distinct().ToList(), v: Classify(g.First().r.Quest, g.Key)))
                          .Where(x => cls == null || x.v.cls == cls)
                          .OrderByDescending(x => x.entries.Count).ThenBy(x => x.cause).ToList();
        foreach (var (cause, entries, v) in dead.Take(top))
            Console.WriteLine($"{entries.Count,3} [{v.cls}] {cause}: {string.Join(",", entries.Take(3))}{(entries.Count > 3 ? ",…" : "")}" + (v.why != null ? "  — " + v.why : ""));
        if (dead.Count > top) Console.WriteLine($"… {dead.Count - top} more causes (--top N)");

        if (verbose)
        {
            Console.WriteLine("== blocked quests");
            foreach (var r in results.Where(r => !r.Ok && (cls == null || QuestClass(r) == cls)))
                Console.WriteLine($"[{QuestClass(r)}] {r.Region} {r.Quest}: {(r.Starts ? "starts" : "NEVER STARTS")}, {(r.Succeeds ? "succeeds" : "NEVER SUCCEEDS")}" +
                                  (r.CountedWhileOpen ? " (no starter)" : "") + "; " + string.Join("; ", Causes(r)));
            foreach (var r in results.Where(r => r.Ok && r.Notes.Count > 0 && (cls == null || cls == "c")))
                Console.WriteLine($"[c] {r.Region} {r.Quest}: {string.Join("; ", r.Notes)}");
        }
        return 0;
    }

    /// <summary>
    /// <c>story walk --quests --play [--region r] [--quest q] [-v] [--steps]</c>: every quest the static walk finishes, played
    /// headless (QuestDryPlan → ChapterDryRun on a fresh session of the pack). Per region: passed / failed / no plan; then the
    /// failure causes. --steps prints the plan of each failing quest (with --quest: of that quest).
    /// </summary>
    static int QuestsPlay(IContentDatabase db, StoryWalk walk, List<QuestCheckResult> results, bool verbose, int top, bool showSteps)
    {
        var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(Option(Environment.GetCommandLineArgs(), "--pack") ?? DefaultPackDir) });
        var plan = new QuestDryPlan(content.Database, walk);
        var quests = content.Database.All<QuestDef>().ToDictionary(q => q.Id.Name);
        var rows = new List<(string region, string quest, string outcome, string why, List<string> steps)>();
        foreach (var r in results)
        {
            var steps = plan.Steps(quests[r.Quest], out string skip);
            if (steps == null) { rows.Add((r.Region, r.Quest, "skip", skip, null)); continue; }
            var session = GameSession.NewGame(content, new SessionPorts(new ChapterDryRun.SilentPresenter(), null), new GameSettings(new LanguageSettings("English", "English"), randomSeed: 1));
            var run = new ChapterDryRun(session);
            string why = null;
            try { run.Run(ScenarioParser.ParseInline(string.Join("\n", steps), r.Quest)); }
            catch (Exception e) { why = "exception " + e.GetType().Name + ": " + e.Message; }
            var status = session.Quests.StatusOf(new Id<QuestDef>(r.Quest));
            bool ok = status == QuestStatus.Success || status == QuestStatus.Won;
            why ??= run.Failures.FirstOrDefault() ?? (ok ? null : "ended " + status);
            rows.Add((r.Region, r.Quest, ok ? "pass" : "FAIL", ok ? null : why, steps));
            if (verbose && results.Count == 1) foreach (var l in run.Lines) Console.WriteLine("   | " + l);
        }
        Console.WriteLine($"{rows.Count} quests played: {rows.Count(x => x.outcome == "pass")} pass, {rows.Count(x => x.outcome == "FAIL")} fail, {rows.Count(x => x.outcome == "skip")} no plan");
        Console.WriteLine("region                     quests pass fail skip");
        foreach (var g in rows.GroupBy(x => x.region).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key,-26} {g.Count(),6} {g.Count(x => x.outcome == "pass"),4} {g.Count(x => x.outcome == "FAIL"),4} {g.Count(x => x.outcome == "skip"),4}");
        string Kind(string why) => System.Text.RegularExpressions.Regex.Replace(why ?? "", @"'[^']*': ", "").Split('(')[0].Trim();
        Console.WriteLine("== failure causes");
        foreach (var g in rows.Where(x => x.outcome != "pass").GroupBy(x => x.outcome + " " + Kind(x.why)).OrderByDescending(g => g.Count()).Take(top))
            Console.WriteLine($"{g.Count(),3} {g.Key}: {string.Join(",", g.Select(x => x.quest).Take(5))}{(g.Count() > 5 ? ",…" : "")}");
        if (verbose)
            foreach (var x in rows.Where(x => x.outcome != "pass"))
            {
                Console.WriteLine($"{x.outcome} {x.region} {x.quest}: {x.why}");
                if (showSteps && x.steps != null) Console.WriteLine("     " + string.Join("; ", x.steps));
            }
        else if (showSteps)
            foreach (var x in rows.Where(x => x.steps != null)) Console.WriteLine($"{x.outcome} {x.quest}: {string.Join("; ", x.steps)}");
        return 0;
    }

    /// <summary>
    /// <c>story walk --quests --scenarios [--region r] [--out dir] [--per 10] [--minutes 12]</c>: editor scenarios for the
    /// class-c quests (ok in the static walk only through conditions it cannot prove: escort/guide arrivals, enclave
    /// liberations, gold/fame pre-checks), Myrtana.Session.Dev.QuestScenarioWriter. Batched by region into
    /// <c>sideq_&lt;region&gt;_NN.json</c> (at most --per quests and --minutes of estimated editor time each, liberations last);
    /// every file is checked headless in one session as it will run, a quest that breaks the sequence moves to a later file.
    /// Existing sideq_*.json files in the output folder are replaced.
    /// </summary>
    static int QuestScenarios(StoryWalk walk, List<QuestCheckResult> results, string[] args)
    {
        var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(Option(args, "--pack") ?? DefaultPackDir) });
        var writer = new QuestScenarioWriter(content, walk);
        string outDir = Option(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "Tools", "Scenarios");
        int per = int.TryParse(Option(args, "--per"), out int n) ? n : 10;
        float budget = (float.TryParse(Option(args, "--minutes"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m) ? m : 12f) * 60f;
        const float StartSeconds = 60f; // fresh Play, world load, header
        // safe 0: end fights and hide tips without the Orc fame of the chapter runs (it changes which topics NPCs offer)
        var header = new[] { "god", "notips", "safe 0" };
        bool verbose = args.Contains("-v");
        // --core: the uncovered core quests (Docs/quest-coverage.md), every one the walk finishes, into core_*.json
        bool core = args.Contains("--core");
        string prefix = core ? "core" : "sideq";
        var blocks = new List<QuestScenarioBlock>();
        foreach (var r in results.Where(r => r.Ok && (core || r.Notes.Count > 0)))
        {
            var b = writer.Build(r.Quest, r.Region, r.Notes.Count > 0 ? string.Join("; ", r.Notes) : "core quest");
            if (b.Failure == null && writer.Check(new[] { b }, header, out string alone) >= 0) b.Failure = "as written: " + alone;
            if (b.Failure != null) Console.WriteLine($"FAIL {r.Quest}: {b.Failure}" + (verbose ? Environment.NewLine + "     " + string.Join("; ", b.Steps) : ""));
            else blocks.Add(b);
        }
        foreach (var old in Directory.GetFiles(outDir, prefix + "_*.json")) File.Delete(old);
        var files = new List<(string name, List<QuestScenarioBlock> blocks, float seconds)>();
        foreach (var g in blocks.GroupBy(b => b.Region).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // first fit: a quest joins the first file of its region that still has room and still passes headless with it
            var batches = new List<List<QuestScenarioBlock>>();
            foreach (var b in g.OrderBy(b => b.Liberates).ThenBy(b => b.Quest, StringComparer.Ordinal))
            {
                List<QuestScenarioBlock> home = null;
                foreach (var batch in batches)
                {
                    if (batch.Count >= per || StartSeconds + batch.Sum(x => x.Seconds) + b.Seconds > budget) continue;
                    var trial = batch.Concat(new[] { b }).ToList();
                    int bad = writer.Check(trial, header, out string why);
                    if (bad < 0) { home = batch; break; }
                    if (verbose) Console.WriteLine($"  {b.Quest} not after {string.Join(",", batch.Select(x => x.Quest))}: {trial[bad].Quest} {why}");
                }
                if (home == null) batches.Add(home = new List<QuestScenarioBlock>());
                home.Add(b);
            }
            int number = 0;
            foreach (var batch in batches)
                files.Add(($"{prefix}_{RegionSlug(g.Key)}_{++number:00}", batch, StartSeconds + batch.Sum(b => b.Seconds)));
        }
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (var (name, batch, seconds) in files)
        {
            var steps = new List<string> { string.Join("; ", header) };
            foreach (var b in batch) steps.AddRange(b.Steps);
            var doc = new
            {
                name,
                verifies = $"{(core ? "core" : "class-c")} quests of {batch[0].Region} played on their real path (Docs/story-walk.md §5): " + string.Join(", ", batch.Select(b => b.Quest)),
                fresh = true,
                timeout = 900,
                notes = System.FormattableString.Invariant($"Generated by `G3Cli story walk --quests --scenarios{(core ? " --core" : "")}`. Estimated {seconds / 60f:0.0} min of editor time. ") +
                        "Escorts walk the hero along the NavMesh with the guide/follower (escort op); liberations fight the first defenders in melee and kill the rest; gold/fame pre-checks are given; " +
                        "rewards are checked as lower bounds (xp, fame:Group political, efame:Group enclave) from the headless run.",
                steps,
            };
            File.WriteAllText(Path.Combine(outDir, name + ".json"), System.Text.Json.JsonSerializer.Serialize(doc, options) + Environment.NewLine);
            Console.WriteLine(System.FormattableString.Invariant($"{name}: {batch.Count} quests, ~{seconds / 60f:0.0} min, {batch.Sum(b => b.Escorts)} escorts, {batch.Count(b => b.Liberates)} liberations, {batch.Sum(b => b.Fights)} melee fights"));
        }
        Console.WriteLine($"{files.Count} files, {files.Sum(f => f.blocks.Count)} quests ({blocks.Count} built, {results.Count(r => r.Ok && r.Notes.Count > 0)} class c) in {outDir}");
        return 0;
    }

    /// <summary>The QuestCheck results of the uncovered core quests (Docs/quest-coverage.md).</summary>
    static List<QuestCheckResult> CoreOnly(IContentDatabase db, StoryWalk walk, List<QuestCheckResult> results)
    {
        var open = OpenCoreQuests(db, walk, results);
        return results.Where(r => open.Contains(r.Quest)).ToList();
    }

    /// <summary>"Myrtana/Silden_City" → "silden", "Hauptstory" → "main", "Varant/Varant_Outdoor" → "varant_outdoor".</summary>
    static string RegionSlug(string region)
    {
        string last = region.Substring(region.LastIndexOf('/') + 1).ToLowerInvariant();
        if (last == "hauptstory") return "main";
        if (last.EndsWith("_city", StringComparison.Ordinal)) last = last.Substring(0, last.Length - 5);
        return last;
    }

    /// <summary>Tools/Scenarios/side_quest_verdicts.tsv: "match &lt;tab&gt; class &lt;tab&gt; reason" per line, # comments.</summary>
    static List<(string match, string cls, string why)> LoadVerdicts()
    {
        string path = Path.Combine(Environment.CurrentDirectory, "Tools", "Scenarios", "side_quest_verdicts.tsv");
        if (!File.Exists(path)) path = Path.Combine(Project, "Tools", "Scenarios", "side_quest_verdicts.tsv");
        var list = new List<(string, string, string)>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('\t');
            if (p.Length >= 2) list.Add((p[0], p[1].Trim(), p.Length > 2 ? p[2].Trim() : null));
        }
        return list;
    }
}
