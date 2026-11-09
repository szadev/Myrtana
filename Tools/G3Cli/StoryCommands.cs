using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Story;

static partial class Program
{
    /// <summary>
    /// G3Cli story validate [--ogg] | npc &lt;Owner&gt; [lang] | quests [lang] | refs | strings &lt;id-prefix&gt; [lang]
    /// — dialogue / quest / localization data (Docs/formats-story.md).
    /// </summary>
    static int StoryCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "validate";
        switch (sub)
        {
            case "validate": return StoryValidate(data, args.Contains("--ogg"));
            case "npc": return StoryNpc(data, args[2], args.Length > 3 ? args[3] : "Russian");
            case "quests": return StoryQuests(data, args.Length > 2 ? args[2] : "Russian");
            case "refs": return StoryRefs(data);
            case "coverage": return StoryCoverage(data, args); // Tools/G3Cli/StoryCoverage.cs
            case "walk": return StoryWalkCommand(data, args); // Tools/G3Cli/StoryWalkCommands.cs
            case "strings":
            {
                var st = LoadStrings(data, out _);
                string lang = args.Length > 3 ? args[3] : "Russian";
                foreach (var e in st.Entries.Values.Where(e => e.Id.StartsWith(args[2], StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Id).Take(200))
                    Console.WriteLine($"{e.Id} = {Loc(st, e.Id, lang)}");
                return 0;
            }
        }
        Console.WriteLine("usage: story validate [--ogg] | npc <Owner> [lang] | quests [lang] | refs | strings <prefix> [lang] | coverage [--pack dir] [--md] [--examples n] | walk [--pack dir] [--check] [--ground] [--all]");
        return 1;
    }

    static string GameRoot(string data) => Path.GetDirectoryName(Path.GetFullPath(data).TrimEnd('\\', '/'));

    /// <summary>Strings volume stringtable.ini + (if present) UpdatePack stringtableMod.ini overlay (only source of Russian text).</summary>
    static StringTable LoadStrings(string data, out StringTable mod)
    {
        using var fs = new G3FileSystem(data, "Strings");
        var st = StringTable.ReadIni(fs.Get("Strings/stringtable.ini").Read());
        mod = null;
        var modPath = Path.Combine(GameRoot(data), "UpdatePack", "Z_UpdatePack_SupportMod", "stringtableMod.ini");
        if (File.Exists(modPath))
        {
            mod = StringTable.ReadIni(File.ReadAllBytes(modPath));
            st.Merge(mod);
        }
        return st;
    }

    /// <summary>Text in the requested language, falling back to English (marked "[en]") and German.</summary>
    static string Loc(StringTable st, string id, string lang)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var t = st.Get(id, lang);
        if (t != null) return t;
        t = st.Get(id, "English");
        if (t != null) return "[en] " + t;
        t = st.Get(id, "German");
        return t != null ? "[de] " + t : $"<missing {id}>";
    }

    static List<Info> LoadInfos(G3FileSystem fs, List<string> errors)
    {
        var list = new List<Info>();
        foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".info", StringComparison.OrdinalIgnoreCase)))
        {
            try { list.Add(InfoFile.ReadIni(e.Read())); }
            catch (Exception ex) { errors?.Add($"{e.Path}: {ex.Message}"); }
        }
        return list;
    }

    static List<Quest> LoadQuests(G3FileSystem fs, List<string> errors)
    {
        var list = new List<Quest>();
        foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".quest", StringComparison.OrdinalIgnoreCase)))
        {
            try { list.Add(QuestFile.ReadIni(e.Read())); }
            catch (Exception ex) { errors?.Add($"{e.Path}: {ex.Message}"); }
        }
        return list;
    }

    static int StoryValidate(string data, bool checkOgg)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var errors = new List<string>();

        // ---- 1. strings
        using (var sfs = new G3FileSystem(data, "Strings"))
        {
            var ini = StringTable.ReadIni(sfs.Get("Strings/stringtable.ini").Read());
            Console.WriteLine($"stringtable.ini: {ini.Entries.Count} ids, languages {string.Join(",", ini.Languages)} (current {ini.CurrentLanguage}), " +
                              $"{ini.Irregular.Count} irregular lines [{string.Join(" ", ini.Irregular.Take(5))} …], other sections: {string.Join(",", ini.OtherSections)}");
            foreach (var g in ini.Entries.Keys.GroupBy(k => k.Split('_')[0]).OrderByDescending(g => g.Count()).Take(12))
                Console.Write($"{g.Key}:{g.Count()} ");
            Console.WriteLine();
            for (int l = 0; l < ini.Languages.Count; l++)
                Console.Write($"{ini.Languages[l]}:{ini.Entries.Values.Count(e => !string.IsNullOrEmpty(e.Text[l]))} ");
            Console.WriteLine("(non-empty texts per language)");
        }
        var modPath = Path.Combine(GameRoot(data), "UpdatePack", "Z_UpdatePack_SupportMod", "stringtableMod.ini");
        if (File.Exists(modPath))
        {
            var mod = StringTable.ReadIni(File.ReadAllBytes(modPath));
            Console.WriteLine($"stringtableMod.ini (UTF-16): {mod.Entries.Count} ids, {mod.Languages.Count} columns, irregular {mod.Irregular.Count}; " +
                              $"e.g. {mod.Entries.Keys.First()} ru=\"{mod.Get(mod.Entries.Keys.First(), "Russian")}\"");
        }
        foreach (var vol in new[] { "Projects_compiled.pak", "Projects_compiled.p00", "Projects_compiled.p01" })
        {
            var path = Path.Combine(data, vol);
            if (!File.Exists(path)) continue;
            using var pa = new PakArchive(path);
            foreach (var e in pa.Entries.Where(e => e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
            {
                if (e.Size == 0) { Console.WriteLine($"{vol}/{e.Path}: 0-byte deletion marker"); continue; }
                try
                {
                    if (e.Path.StartsWith("stringtable", StringComparison.OrdinalIgnoreCase))
                    {
                        var b = StringTable.ReadBin(e.Read());
                        Console.WriteLine($"{vol}/{e.Path}: OK to end, {b.Entries.Count} ids, languages {string.Join(",", b.Languages)}, current {b.CurrentLanguage}");
                    }
                    else
                    {
                        var ci = InfoFile.ReadCompiled(e.Read());
                        Console.WriteLine($"{vol}/{e.Path}: OK to end, {ci.Count} gCInfo_PS, {ci.Sum(i => i.Lines.Count)} script lines");
                    }
                }
                catch (Exception ex) { errors.Add($"{vol}/{e.Path}: {ex.Message}"); }
            }
        }
        var st = LoadStrings(data, out _);

        // ---- 2. infos
        using var ifs = new G3FileSystem(data, "Infos");
        var infos = LoadInfos(ifs, errors);
        var unknownKeys = infos.SelectMany(i => i.Raw.Keys).Where(k => !InfoFile.KnownKeys.Contains(k)).Distinct().ToList();
        var lines = infos.SelectMany(i => i.Lines.Select(l => (i, l))).ToList();
        Console.WriteLine($"infos: {infos.Count} parsed ({ifs.Files.Count()} files), {infos.Select(i => i.Owner).Distinct().Count()} owners, " +
                          $"{infos.Count(i => !string.IsNullOrEmpty(i.Parent))} children, {lines.Count} script lines, " +
                          $"{infos.Count(i => i.ScriptArrayMismatch)} with ragged InfoScript_* arrays, unknown keys: [{string.Join(",", unknownKeys)}]");
        var names = new HashSet<string>(infos.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"  duplicate names {infos.Count - names.Count}, dangling parents {infos.Count(i => !string.IsNullOrEmpty(i.Parent) && !names.Contains(i.Parent))}");
        Console.WriteLine("  condition types: " + string.Join(" ", infos.GroupBy(i => i.CondTypeEnum).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}")));
        Console.WriteLine("  info types: " + string.Join(" ", infos.GroupBy(i => i.TypeEnum).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}")));
        var textLines = lines.Where(x => !string.IsNullOrEmpty(x.l.TextId)).ToList();
        var missing = textLines.Where(x => !st.Entries.ContainsKey(x.l.TextId)).ToList();
        Console.WriteLine($"  lines with text id: {textLines.Count} ({textLines.Select(x => x.l.TextId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct), " +
                          $"missing in stringtable: {missing.Count} [{string.Join(" ", missing.Take(5).Select(x => x.l.TextId))}]");

        // compiled vs INI
        {
            var path = Path.Combine(data, "Projects_compiled.p00");
            if (!File.Exists(path)) path = Path.Combine(data, "Projects_compiled.pak");
            using var pa = new PakArchive(path);
            var ce = pa.Entries.FirstOrDefault(e => e.Path.StartsWith("compiledinfos", StringComparison.OrdinalIgnoreCase) && e.Size > 0);
            if (ce != null)
            {
                var ci = InfoFile.ReadCompiled(ce.Read()).ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
                var byName = infos.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                int same = 0, diff = 0;
                string firstDiff = null;
                foreach (var kv in ci)
                {
                    if (!byName.TryGetValue(kv.Key, out var ii)) continue;
                    var a = kv.Value;
                    bool eq = a.Owner == ii.Owner && (a.Parent ?? "") == (ii.Parent ?? "") && a.ConditionType == ii.ConditionType && a.Type == ii.Type &&
                              a.Lines.Count == ii.Lines.Count && a.Lines.Zip(ii.Lines, (x, y) => x.Command == y.Command && x.TextId == y.TextId && x.Entity1 == y.Entity1 && x.Id1 == y.Id1 && x.Id2 == y.Id2).All(b => b);
                    if (eq) same++; else { diff++; firstDiff ??= kv.Key; }
                }
                Console.WriteLine($"  compiled ({Path.GetFileName(path)}): {ci.Count} infos; vs .info: {ci.Keys.Count(byName.ContainsKey)} common " +
                                  $"({same} identical, {diff} differ e.g. {firstDiff}), {byName.Keys.Count(k => !ci.ContainsKey(k))} only in .info, {ci.Keys.Count(k => !byName.ContainsKey(k))} only compiled");
            }
        }

        // ---- 3. quests
        using var qfs = new G3FileSystem(data, "Quests");
        var quests = LoadQuests(qfs, errors);
        var qnames = new HashSet<string>(quests.Select(q => q.Name), StringComparer.OrdinalIgnoreCase);
        var qUnknown = quests.SelectMany(q => q.Raw.Keys).Where(k => !QuestFile.KnownKeys.Contains(k)).Distinct().ToList();
        Console.WriteLine($"quests: {quests.Count} parsed ({qfs.Files.Count()} files), unknown keys: [{string.Join(",", qUnknown)}], " +
                          $"topic missing in stringtable: {quests.Count(q => !st.Entries.ContainsKey(q.LogTopic ?? ""))}, non-empty LogText: {quests.Count(q => q.LogText.Length > 0)}, " +
                          $"dangling FinishedQuests: {quests.SelectMany(q => q.FinishedQuests).Count(n => !qnames.Contains(n))}, " +
                          $"infos referencing unknown quest: {infos.Count(i => !string.IsNullOrEmpty(i.Quest) && !qnames.Contains(i.Quest))}");
        Console.WriteLine("  types: " + string.Join(" ", quests.GroupBy(q => q.TypeEnum).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}")));

        // ---- 4. speech
        foreach (var lang in new[] { "Russian", "English", "German" })
        {
            using var sp = new G3FileSystem(data, "Speech_" + lang);
            var paths = sp.Files.Select(f => f.Path).ToList();
            if (paths.Count == 0) continue;
            var vi = new VoiceIndex(paths, lang, st.Entries.ContainsKey);
            var say = textLines.Where(x => x.l.Command.StartsWith("Say", StringComparison.OrdinalIgnoreCase)).ToList();
            int voiced = say.Count(x => vi.Find(x.l.TextId).Count > 0);
            int xlipOk = 0, xlipBad = 0, xlipGenome = 0; uint maxVis = 0; int oggOk = 0, oggBad = 0;
            foreach (var e in sp.Files)
            {
                if (e.Path.EndsWith(".xlip", StringComparison.OrdinalIgnoreCase))
                {
                    try { var x = Xlip.Read(e.Read()); xlipOk++; if (x.Genome) xlipGenome++; if (x.Visemes.Length > 0) maxVis = Math.Max(maxVis, x.Visemes.Max()); } catch (Exception ex) { xlipBad++; if (xlipBad < 5) errors.Add($"{e.Path}: {ex.Message}"); }
                }
                else if (checkOgg && e.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    var b = e.Read();
                    if (b.Length > 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S') oggOk++; else oggBad++;
                }
            }
            Console.WriteLine($"Speech_{lang}: {vi.Files.Count} ogg ({vi.Files.Count(f => f.Kind == "INFO")} INFO, {vi.Files.Count(f => f.Kind == "SVM")} SVM, " +
                              $"{vi.Files.Count(f => f.XlipPath == null)} without xlip), INFO unresolved {vi.Unresolved.Count}; Say lines voiced {voiced}/{say.Count}; " +
                              $"xlip parsed to end {xlipOk} ({xlipGenome} GENOMFLE, bad {xlipBad}, max viseme {maxVis})" + (checkOgg ? $"; OggS magic {oggOk} (bad {oggBad})" : ""));
            foreach (var u in vi.Unresolved.Take(3)) Console.WriteLine("    unresolved: " + u);
        }

        Console.WriteLine($"errors: {errors.Count}");
        foreach (var e in errors.Take(30)) Console.WriteLine("  " + e);
        Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F1}s");
        return errors.Count == 0 ? 0 : 2;
    }

    static string Speaker(Info i, InfoLine l)
    {
        string e = l.Entity1;
        if (string.IsNullOrEmpty(e) || e.Equals("npc", StringComparison.OrdinalIgnoreCase)) return i.Owner;
        if (e.Equals("player", StringComparison.OrdinalIgnoreCase)) return "PC_Hero";
        return e;
    }

    static int StoryNpc(string data, string owner, string lang)
    {
        var st = LoadStrings(data, out _);
        using var ifs = new G3FileSystem(data, "Infos");
        var infos = LoadInfos(ifs, null);
        var mine = infos.Where(i => string.Equals(i.Owner, owner, StringComparison.OrdinalIgnoreCase)).ToList();
        using var sp = new G3FileSystem(data, "Speech_" + lang);
        var vi = new VoiceIndex(sp.Files.Select(f => f.Path), lang, st.Entries.ContainsKey);
        Console.WriteLine($"{mine.Count} infos owned by {owner}; text language {lang} (fallback [en]); voice files from Speech_{lang} ({vi.Files.Count} ogg)");
        var children = mine.Where(i => !string.IsNullOrEmpty(i.Parent)).ToLookup(i => i.Parent, StringComparer.OrdinalIgnoreCase);
        var own = new HashSet<string>(mine.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        void Dump(Info i, int depth)
        {
            string ind = new string(' ', depth * 4);
            var cond = new List<string>();
            if (i.CondPlayerKnows.Length > 0) cond.Add("knows " + string.Join("+", i.CondPlayerKnows));
            if (i.CondPlayerKnowsNot.Length > 0) cond.Add("!knows " + string.Join("+", i.CondPlayerKnowsNot));
            if (i.CondItems.Length > 0) cond.Add($"items {string.Join("+", i.CondItems.Select((x, n) => x + "x" + (n < i.CondItemAmounts.Length ? i.CondItemAmounts[n] : "?")))} in {i.CondItemContainer}");
            if (i.CondSecondaryNPC.Length > 0) cond.Add($"npc {string.Join("+", i.CondSecondaryNPC)} state {string.Join("+", i.CondSecondaryNPCStates)}");
            if (!string.IsNullOrEmpty(i.CondOwnerNearEntity)) cond.Add("near " + i.CondOwnerNearEntity);
            if (i.GoldCost != 0) cond.Add("gold " + i.GoldCost);
            Console.WriteLine($"{ind}[{i.Name}] sort={i.SortID} cond={i.CondTypeEnum} type={i.TypeEnum}" +
                              (string.IsNullOrEmpty(i.Quest) ? "" : $" quest={i.Quest}") + (i.Permanent ? " permanent" : "") +
                              (cond.Count > 0 ? " if " + string.Join(", ", cond) : ""));
            foreach (var l in i.Lines)
            {
                if (l.Command == "Description")
                    Console.WriteLine($"{ind}  > choice: \"{Loc(st, l.TextId, lang)}\"");
                else if (l.Command.StartsWith("Say") && !string.IsNullOrEmpty(l.TextId))
                {
                    string who = Speaker(i, l);
                    var v = vi.Find(l.TextId, who);
                    string stage = st.GetStage(l.TextId, lang);
                    if (string.IsNullOrEmpty(stage)) stage = st.GetStage(l.TextId, "English");
                    Console.WriteLine($"{ind}  {who}: \"{Loc(st, l.TextId, lang)}\"" + (string.IsNullOrEmpty(stage) ? "" : $" ({stage})") +
                                      $"  [{l.TextId} -> {(v.Count > 0 ? string.Join(" | ", v.Select(f => Path.GetFileName(f.OggPath))) : "no voice")}]");
                }
                else if (l.Command == "ShowSubtitle")
                    Console.WriteLine($"{ind}  (subtitle) \"{Loc(st, l.TextId, lang)}\"");
                else
                    Console.WriteLine($"{ind}  * {l.Command} {string.Join(" ", new[] { l.Entity1, l.Entity2, l.Id1, l.Id2, l.TextId }.Where(s => !string.IsNullOrEmpty(s)))}");
            }
            foreach (var c in children[i.Name].OrderBy(c => c.SortID)) Dump(c, depth + 1);
        }
        foreach (var r in mine.Where(i => string.IsNullOrEmpty(i.Parent) || !own.Contains(i.Parent)).OrderBy(i => i.SortID)) Dump(r, 0);
        return 0;
    }

    static int StoryQuests(string data, string lang)
    {
        var st = LoadStrings(data, out _);
        using var qfs = new G3FileSystem(data, "Quests");
        var quests = LoadQuests(qfs, null);
        using var ifs = new G3FileSystem(data, "Infos");
        var infos = LoadInfos(ifs, null);
        // quest giver = owner of an info with Quest=q and ConditionType Activator/Open, or of an info running "RunQuest q"
        var givers = infos.Where(i => !string.IsNullOrEmpty(i.Quest) && (i.CondTypeEnum == InfoCondType.Activator || i.CondTypeEnum == InfoCondType.Open)).Select(i => (q: i.Quest, o: i.Owner))
            .Concat(infos.SelectMany(i => i.Lines.Where(l => l.Command == "RunQuest").Select(l => (q: l.Id1, o: i.Owner))))
            .ToLookup(x => x.q, x => x.o, StringComparer.OrdinalIgnoreCase);
        foreach (var q in quests.OrderBy(q => q.Folder).ThenBy(q => q.Name))
        {
            string targets = q.DeliveryEntities.Length == 0 ? "" :
                " targets " + string.Join(",", q.DeliveryEntities.Select((d, n) => n < q.DeliveryAmounts.Length && q.DeliveryAmounts[n] > 1 ? $"{d}x{q.DeliveryAmounts[n]}" : d));
            Console.WriteLine($"{q.Name,-40} {q.TypeEnum,-11} folder={q.Folder} \"{Loc(st, q.LogTopic, lang)}\" giver={string.Join("/", givers[q.Name].Distinct())} " +
                              $"dest={q.DestinationEntity} xp={q.ExperiencePoints}{targets}" +
                              (q.FinishedQuests.Length > 0 ? " after " + string.Join(",", q.FinishedQuests) : "") +
                              (string.IsNullOrEmpty(q.EnclaveSuccess) ? "" : $" enclave {q.EnclaveSuccess}+{q.EnclaveSuccessAmount}") +
                              (q.PoliticalSuccess != 0 ? $" faction {(PoliticalAlignment)q.PoliticalSuccess}+{q.PoliticalSuccessAmount}" : ""));
        }
        Console.WriteLine($"{quests.Count} quests");
        return 0;
    }

    /// <summary>Everything infos/quests reference outside themselves: commands (engine-side), routines, events, effects, items…</summary>
    static int StoryRefs(string data)
    {
        using var ifs = new G3FileSystem(data, "Infos");
        var infos = LoadInfos(ifs, null);
        var lines = infos.SelectMany(i => i.Lines).ToList();
        void Top(string title, IEnumerable<string> items, int n = 40)
        {
            var g = items.Where(s => !string.IsNullOrEmpty(s)).GroupBy(s => s).OrderByDescending(x => x.Count()).ToList();
            Console.WriteLine($"== {title}: {g.Count} distinct, {g.Sum(x => x.Count())} uses");
            Console.WriteLine("   " + string.Join("  ", g.Take(n).Select(x => $"{x.Key}:{x.Count()}")));
        }
        Top("info commands", lines.Select(l => l.Command), 100);
        Top("SetRoutine routines (Id1)", lines.Where(l => l.Command == "SetRoutine").Select(l => l.Id1));
        Top("game events Set/Clear (Id1)", lines.Where(l => l.Command.EndsWith("GameEvent")).Select(l => l.Id1), 20);
        Top("CondPlayerKnows / KnowsNot", infos.SelectMany(i => i.CondPlayerKnows.Concat(i.CondPlayerKnowsNot)), 20);
        Top("StartEffect", lines.Where(l => l.Command == "StartEffect").Select(l => l.Id1));
        Top("SaySVM ids", lines.Where(l => l.Command == "SaySVM").Select(l => l.Id1));
        Top("Give items", lines.Where(l => l.Command.Equals("Give", StringComparison.OrdinalIgnoreCase)).Select(l => l.Id1));
        Top("Use / Wear / PlayMusic / ShowPicture / SetChapter / SetPAL / SetClass / SetGuardStatus",
            lines.Where(l => new[] { "Use", "Wear", "PlayMusic", "ShowPicture", "SetChapter", "SetPAL", "SetClass", "SetGuardStatus" }.Contains(l.Command)).Select(l => l.Command + ":" + l.Id1));
        return 0;
    }
}
