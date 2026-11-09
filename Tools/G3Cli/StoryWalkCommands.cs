using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using Myrtana.Story;

static partial class Program
{
    /// <summary>
    /// <c>story walk [--pack dir] [--check] [--ground] [--all] [--chain fact,fact]</c> — offline main-story walk (Docs/story-walk.md).
    /// Without --check: the main-story quests in story order (fixpoint depth) with the entries that start and finish them.
    /// With --check: the risky steps — quests or main-story entries that can never happen from the start state (with the
    /// facts they miss), owners that appear only through a sector switch, items that only come from an NPC or a locked chest,
    /// conditions a static walk cannot prove (an NPC standing at a place). --ground also compares owner spawns and places
    /// with the landscape height (loads the terrain, ~1 min).
    /// </summary>
    static int StoryWalkCommand(string data, string[] args)
    {
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        string dry = Option(args, "--dry");
        if (dry != null) return StoryDryRun(pack, dry, args.Contains("-v"));
        bool check = args.Contains("--check"), ground = args.Contains("--ground"), all = args.Contains("--all");
        var db = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) }).Database;
        var builder = new StoryWalkBuilder(db);
        var walk = builder.Build().Run();
        if (args.Contains("--quests")) return QuestsCheck(db, builder, walk, args);
        var main = StoryWalkBuilder.MainQuests(db);
        var quests = db.All<QuestDef>().ToDictionary(q => q.Id.Name);
        Console.WriteLine($"walk: {walk.Rules.Count} rules, {walk.Rounds} rounds, {main.Count} main-story quests");

        // the main-story entries: they change a main quest, or set a flag a main entry reads, or are epilogue entries
        bool TouchesMain(string fact) =>
            fact.StartsWith("quest:", StringComparison.Ordinal) && main.Contains(fact.Substring(6, fact.IndexOf('=') - 6)) ||
            fact.StartsWith("flag:Epilog_", StringComparison.Ordinal) || fact.StartsWith("flag:Endgame_", StringComparison.Ordinal);
        var steps = builder.EntryRules.Values.Where(r => r.Effects.Any(TouchesMain)).ToList();
        var stepFlags = new HashSet<string>(steps.SelectMany(r => r.Need.Facts()).Where(f => f.StartsWith("flag:", StringComparison.Ordinal)));
        var feeders = builder.EntryRules.Values.Where(r => !steps.Contains(r) && r.Effects.Any(stepFlags.Contains)).ToList();
        steps.AddRange(feeders);

        string where = Option(args, "--where");
        if (where != null)
        {
            foreach (string name in where.Split(','))
                foreach (string line in WhereIs(db, name)) Console.WriteLine(line);
            return 0;
        }
        string chain = Option(args, "--chain");
        if (chain != null)
        {
            // e.g. --chain quest:Xardas_GotoGate=Success: the first-found way there, deepest step first
            foreach (string fact in chain.Split(','))
            {
                Console.WriteLine($"== {fact} (round {walk.DepthOf(fact)})");
                foreach (var r in walk.ChainTo(fact))
                    Console.WriteLine($"  r{walk.RoundOf(r),2} {r.Id}{(r.Owner != null ? "@" + r.Owner : "")} if {r.Need} -> {string.Join(" ", r.Effects.Take(6))}" +
                                      (r.Notes.Count > 0 ? "  [" + string.Join("; ", r.Notes) + "]" : ""));
            }
            return 0;
        }
        if (!check)
        {
            foreach (var q in main.OrderBy(q => Depth(walk, q)).ThenBy(q => q))
            {
                var def = quests[q];
                string run = WalkFacts.Quest(q, "Running"), ok = WalkFacts.Quest(q, "Success");
                Console.WriteLine($"[{Depth(walk, q),3}] {q} ({def.Objective.Kind}{Targets(def)}) running@{walk.DepthOf(run)} success@{walk.DepthOf(ok)}");
                foreach (var r in walk.ProducersOf(run).Where(r => r.Kind == "entry"))
                    Console.WriteLine($"      start {r.Id}@{r.Owner} r{walk.RoundOf(r)} if {r.Need}");
                foreach (var r in walk.ProducersOf(ok))
                    Console.WriteLine($"      done  {r.Id}{(r.Owner != null ? "@" + r.Owner : "")} r{walk.RoundOf(r)}" + (all ? " if " + r.Need : ""));
            }
            return 0;
        }

        int risks = 0;
        void Risk(string what) { Console.WriteLine(what); risks++; }

        Console.WriteLine("== main-story quests never started / never finished");
        foreach (var q in main.OrderBy(q => q))
        {
            string run = WalkFacts.Quest(q, "Running"), ok = WalkFacts.Quest(q, "Success");
            if (!walk.Has(run) && walk.Has(ok))
                Console.WriteLine($"quest {q}: nothing starts it, its objective is counted while Open (ok)");
            else if (!walk.Has(run))
                Risk($"QUEST {q}: never Running; starters: " + Starters(walk, run) + (walk.ProducersOf(ok).Any() ? "; finishers: " + Starters(walk, ok) : ""));
            else if (!walk.Has(ok) && !walk.ProducersOf(ok).Any() && quests[q].Objective.Kind != QuestObjectiveKind.None)
                Risk($"QUEST {q}: nothing finishes it ({quests[q].Objective.Kind} {string.Join(",", quests[q].Objective.Targets.Select(t => "'" + t.Target + "'"))})" +
                     string.Concat(builder.EntryRules.Values.SelectMany(r => r.Notes).Where(n => n.Contains("reportToQuest " + q + " ")).Select(n => "; " + n)));
            else if (!walk.Has(ok))
                Risk($"QUEST {q}: never Success; finishers: " + Starters(walk, ok));
        }

        Console.WriteLine("== main-story entries that can never be offered");
        foreach (var r in steps.Where(r => !walk.Fired(r)).OrderBy(r => r.Owner).ThenBy(r => r.Id))
            Risk($"ENTRY {r.Id}@{r.Owner}: missing {string.Join(", ", walk.Missing(r))}" + (r.Notes.Count > 0 ? " [" + string.Join("; ", r.Notes) + "]" : ""));

        Console.WriteLine("== owners that appear only through a sector switch");
        foreach (var owner in steps.Where(walk.Fired).Select(r => r.Owner).Distinct().OrderBy(o => o))
        {
            string sector = builder.SectorOf(owner);
            if (sector == null) continue;
            var by = walk.ProducerOf(WalkFacts.Sector(sector));
            Risk($"OWNER {owner}: in sector {sector}, switched on by {(by != null ? by.Id + (by.Notes.Count > 0 ? " (" + by.Notes[0] + ")" : "") : "nothing (never present)")}");
        }

        Console.WriteLine("== items the main story needs that come only from an NPC or a locked chest");
        var items = new HashSet<string>(steps.SelectMany(r => r.Need.Facts()).Where(f => f.StartsWith("item:", StringComparison.Ordinal)));
        foreach (var q in main) foreach (var t in quests[q].Objective.Targets)
            if (quests[q].Objective.Kind == QuestObjectiveKind.HasItems) items.Add(WalkFacts.Item(t.Target));
        foreach (var item in items.OrderBy(i => i))
        {
            var sources = walk.ProducersOf(item).Where(walk.Fired).ToList();
            if (sources.Count == 0) { Risk($"ITEM {item.Substring(5)}: NO SOURCE reachable ({walk.ProducersOf(item).Count()} unreachable)"); continue; }
            bool easy = sources.Any(s => s.Kind == "entry" || s.Id.StartsWith("world items", StringComparison.Ordinal) ||
                                         s.Id.StartsWith("chest ", StringComparison.Ordinal) && s.Notes.Count == 0 || s.Id.StartsWith("recipe ", StringComparison.Ordinal));
            if (!easy || all)
                Console.WriteLine($"{(easy ? "item" : "ITEM")} {item.Substring(5)}: " + string.Join("; ", sources.Take(4).Select(s => s.Id + (s.Notes.Count > 0 ? " (" + s.Notes[0] + ")" : ""))));
            if (!easy) risks++;
        }

        Console.WriteLine("== conditions a static walk cannot prove");
        foreach (var r in steps.Where(r => walk.Fired(r) && r.Notes.Count > 0).OrderBy(r => r.Owner))
            Risk($"NOTE {r.Id}@{r.Owner}: {string.Join("; ", r.Notes)}");
        foreach (var q in main)
        {
            var o = walk.Rules.FirstOrDefault(r => r.Id == "objective " + q);
            if (o != null && o.Notes.Count > 0) Risk($"NOTE {q}: {string.Join("; ", o.Notes)}");
        }

        if (ground) risks += GroundCheck(data, db, steps, main, quests);
        Console.WriteLine($"{risks} risky steps; {steps.Count} main-story entries, {steps.Count(walk.Fired)} reachable");
        return 0;
    }

    /// <summary>
    /// <c>story walk --dry chapter_01_ardea[,name…|all] [-v]</c>: each chapter scenario against a fresh headless session of the
    /// pack (Myrtana.Session.Dev.ChapterDryRun). One line per chapter, the failures below it; -v prints every step line.
    /// </summary>
    static int StoryDryRun(string pack, string names, bool verbose)
    {
        string dir = System.IO.Path.Combine(Environment.CurrentDirectory, "Tools", "Scenarios");
        if (!System.IO.Directory.Exists(dir)) dir = System.IO.Path.Combine(Project, "Tools", "Scenarios");
        ScenarioSpec Load(string name)
        {
            string path = System.IO.Path.Combine(dir, name.EndsWith(".json") ? name : name + ".json");
            return System.IO.File.Exists(path) ? ScenarioParser.ParseJson(System.IO.File.ReadAllText(path), System.IO.Path.GetFileNameWithoutExtension(path)) : null;
        }
        var list = names == "all"
            ? System.IO.Directory.GetFiles(dir, "chapter_*.json").Select(System.IO.Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList()
            : names.Split(',').ToList();
        var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
        int failed = 0;
        foreach (var name in list)
        {
            var spec = Load(name);
            if (spec == null) { Console.WriteLine($"{name}: no file in {dir}"); failed++; continue; }
            var session = GameSession.NewGame(content, new SessionPorts(new ChapterDryRun.SilentPresenter(), null), new GameSettings(new LanguageSettings("English", "English"), randomSeed: 1));
            var run = new ChapterDryRun(session, Load);
            bool ok = run.Run(spec);
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {spec.Steps.Count} steps, {run.Skipped} presentation steps skipped, {run.Failures.Count} failures");
            if (verbose) foreach (var l in run.Lines) Console.WriteLine("     " + l);
            foreach (var f in run.Failures) Console.WriteLine("   - " + f);
            if (!ok) failed++;
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Where an NPC, object, place, zone or item is (positions in metres; for items: world items, chests, holders).</summary>
    static IEnumerable<string> WhereIs(IContentDatabase db, string name)
    {
        static string P(float x, float y, float z) => $"{x:0.0},{y:0.0},{z:0.0}";
        foreach (var n in db.All<NpcDef>().Where(n => n.Id.Name == name || n.Id.Name.StartsWith(name + "#", StringComparison.Ordinal)).Take(5))
            yield return $"npc {n.Id.Name}: {(n.Spawn.HasValue ? P(n.Spawn.Value.X, n.Spawn.Value.Y, n.Spawn.Value.Z) : "no spawn")}" + (n.Sector != null ? " sector " + n.Sector : "");
        foreach (var o in db.All<InteractiveObjectDef>().Where(o => o.Name == name).Take(8))
            yield return $"object {o.Name} {o.Kind}: {P(o.Position[0], o.Position[1], o.Position[2])}" + (o.Sector != null ? " sector " + o.Sector : "") +
                         (o.Lock != null && o.Lock.Locked ? " locked" + (o.Lock.Key != null ? " key " + o.Lock.Key : "") : "");
        foreach (var p in db.All<WorldPlaceDef>().Where(p => string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase)))
            yield return $"place {p.Id}: {P(p.Position[0], p.Position[1], p.Position[2])}";
        foreach (var z in db.All<AiZoneDef>().Where(z => z.Name == name).Take(3))
            yield return StoryScenario.TryInside(z.Outline, out float x, out float zz) ? $"zone {z.Name}: inside {x:0.0},{zz:0.0} floor {z.Floor:0.0}" : $"zone {z.Name}: no outline";
        foreach (var w in db.All<WorldItemDef>().Where(w => w.Item == name).Take(15))
            yield return $"world item {w.Item}: {P(w.Position[0], w.Position[1], w.Position[2])}" + (w.Sector != null ? " sector " + w.Sector : "") + (!w.Owner.IsEmpty ? " owner " + w.Owner.Name : "");
        var sets = new HashSet<string>(db.All<TreasureSetDef>().Where(t => t.Stacks.Any(s => s.Item == name)).Select(t => t.Id));
        foreach (var o in db.All<InteractiveObjectDef>().Where(o => o.Container != null && (o.Container.Stacks.Any(s => s.Item == name) || o.Container.TreasureSets.Any(sets.Contains))).Take(10))
            yield return $"chest {o.Name} holds {name}: {P(o.Position[0], o.Position[1], o.Position[2])}" + (o.Lock != null && o.Lock.Locked ? " locked" + (o.Lock.Key != null ? " key " + o.Lock.Key : $" difficulty {o.Lock.Difficulty}") : "");
        foreach (var inv in db.All<NpcInventoryDef>().Where(i => i.Stacks.Concat(i.Gear).Any(s => s.Item == name) || i.PlunderSets.Any(sets.Contains)).Take(10))
            yield return $"npc {inv.Npc.Name} carries {name}";
        foreach (var m in db.All<MerchantDef>().Where(m => m.Stock.Any(sets.Contains)).Take(5))
            yield return $"trader {m.Actor.Name} sells {name}";
    }

    static int Depth(StoryWalk walk, string quest)
    {
        int d = walk.DepthOf(WalkFacts.Quest(quest, "Running"));
        return d < 0 ? 999 : d;
    }

    static string Targets(QuestDef q) =>
        q.Objective.Targets.Count == 0 ? "" : " " + string.Join(",", q.Objective.Targets.Select(t => t.Target)) +
        (q.Objective.Destination.IsEmpty ? "" : " -> " + q.Objective.Destination.Name);

    static string Starters(StoryWalk walk, string fact)
    {
        var list = walk.ProducersOf(fact).ToList();
        if (list.Count == 0) return "none";
        return string.Join("; ", list.Take(3).Select(r => $"{r.Id}{(r.Owner != null ? "@" + r.Owner : "")} missing {string.Join(",", walk.Missing(r))}"));
    }

    /// <summary>Owner spawns, Near places and EnterArea zones of main-story steps against the landscape height.</summary>
    static int GroundCheck(string data, IContentDatabase db, List<WalkRule> steps, IReadOnlyCollection<string> main, Dictionary<string, QuestDef> quests)
    {
        Console.WriteLine("== ground (landscape triangles only: buildings, caves and bridges show as offsets)");
        var terrain = new TerrainGrid(TerrainNear(data, 0, 0, 1e6f));
        var npcs = db.All<NpcDef>().Where(n => n.Spawn.HasValue).GroupBy(n => n.Id.Name.Split('#')[0]).ToDictionary(g => g.Key, g => g.First().Spawn.Value);
        var objects = db.All<InteractiveObjectDef>().Where(o => o.Kind == InteractiveKind.Speaker).GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First().Position);
        var places = db.All<WorldPlaceDef>().GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Position, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        void Test(string what, float x, float y, float z)
        {
            float h = terrain.Height(x, z);
            string verdict = float.IsNaN(h) ? "no landscape below" : y - h > 4f ? $"{y - h:0.0} m above landscape" : h - y > 2f ? $"{h - y:0.0} m BELOW landscape" : null;
            if (verdict == null) return;
            Console.WriteLine($"GROUND {what} ({x:0},{y:0},{z:0}): {verdict}");
            n++;
        }
        foreach (var owner in steps.Select(r => r.Owner).Where(o => o != null).Distinct().OrderBy(o => o))
        {
            if (npcs.TryGetValue(owner, out var p)) Test("npc " + owner, p.X, p.Y, p.Z);
            else if (objects.TryGetValue(owner, out var o) && o != null) Test("object " + owner, o[0], o[1], o[2]);
        }
        var near = steps.SelectMany(r => r.Notes).Where(t => t.Contains(" m of ")).Select(t => t.Substring(t.LastIndexOf(" of ", StringComparison.Ordinal) + 4)).Distinct();
        foreach (var place in near.OrderBy(p => p))
        {
            if (places.TryGetValue(place, out var p)) Test("place " + place, p[0], p[1], p[2]);
            else if (!npcs.ContainsKey(place) && !objects.ContainsKey(place)) { Console.WriteLine($"GROUND place {place}: unknown entity"); n++; }
        }
        return n;
    }
}
