using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;

static partial class Program
{
    /// <summary>G3Cli npc tree &lt;name&gt; | npc stats — NPC entity exploration (Docs/formats-animation.md §8).</summary>
    static int NpcCommand(string data, string[] args)
    {
        if (args[1] == "mesh")
        {
            using var mfs = new G3FileSystem(data, "_compiledMesh");
            foreach (var n in args.Skip(2))
            {
                var m = G3.Formats.Mesh.Xcmsh.Read(mfs.GetByName(n).Read(), n);
                Console.WriteLine($"{n}: bounds ({string.Join(",", m.BoundsMin.Select(v => v.ToString("F1")))}) .. ({string.Join(",", m.BoundsMax.Select(v => v.ToString("F1")))}) " +
                                  string.Join(" ", m.Elements.Select(el => $"[{el.VertexCount}v {el.Material}]")));
            }
            return 0;
        }
        if (args[1] == "summary") return NpcSummary(data, args);
        if (args[1] == "heads") return NpcHeads(data, args.Length > 2 && args[2] == "-v"); // NpcHeadCommands.cs
        if (args[1] == "routines") return NpcRoutines(data, args); // RoutineCommands.cs
        if (args[1] == "anidir") return NpcAniDirections(data, args[2]); // RoutineCommands.cs
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var stats = new Dictionary<string, int>();
        void Inc(string k) => stats[k] = stats.TryGetValue(k, out var c) ? c + 1 : 1;
        foreach (var s in layout.Sectors)
        foreach (var path in s.Lrentdats)
        {
            var entry = fs.Get(vol + path);
            List<WorldEntity> list;
            try { list = entry == null ? null : EntityArchive.Read(entry.Read(), false, keepRaw: true); } catch (Exception ex) { Console.WriteLine($"FAIL {path} {ex.Message}"); continue; }
            if (list == null) continue;
            var children = new Dictionary<int, List<int>>();
            for (int i = 0; i < list.Count; i++) { int p = list[i].Parent; if (!children.TryGetValue(p, out var l)) children[p] = l = new List<int>(); l.Add(i); }
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                e.RawPropertySets ??= new List<G3.Formats.Genome.PropertyObject>();
                bool npc = e.RawPropertySets.Any(p => p.ClassName == "gCNPC_PS");
                bool va = e.RawPropertySets.Any(p => p.ClassName == "eCVisualAnimation_PS");
                if (args[1] == "stats")
                {
                    if (npc) { Inc("npc"); Inc("npc in " + (path.Contains("SysDyn") ? "SysDyn" : "sector")); if (va) Inc("npc with VA"); }
                    if (!npc && e.Parent >= 0 && list[e.Parent].RawPropertySets?.Any(p => p.ClassName == "gCNPC_PS") == true)
                    {
                        var vps = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "eCVisualAnimation_PS");
                        var mps = e.RawPropertySets.FirstOrDefault(p => p.ClassName is "eCVisualMeshDynamic_PS" or "eCVisualMeshStatic_PS");
                        string r = vps != null ? e.Source.StrFrom(vps.Get("ResourceFilePath")?.Value) : mps != null ? "mesh " + e.Source.StrFrom((mps.Get("ResourceFileName") ?? mps.Get("ResourceFilePath"))?.Value) : "nothing:" + e.Name;
                        string k = System.Text.RegularExpressions.Regex.Replace(r, @"[0-9]+", "#");
                        Inc("child " + k);
                    }
                    if (va) { Inc("VA"); if (!npc) Inc("VA part parent " + (list[Math.Max(0, e.Parent)].RawPropertySets?.Any(p => p.ClassName == "gCNPC_PS") == true ? "npc" : "other")); }
                    if (npc && va)
                    {
                        var ps = e.RawPropertySets.First(p => p.ClassName == "eCVisualAnimation_PS");
                        Inc("res " + e.Source.StrFrom(ps.Get("ResourceFilePath")?.Value));
                    }
                    continue;
                }
                if (args[1] == "region")
                {
                    float x = float.Parse(args[2]) * 100, z = float.Parse(args[3]) * 100, h = float.Parse(args[4]) * 100;
                    if (!npc || Math.Abs(e.X - x) > h || Math.Abs(e.Z - z) > h) continue;
                    string Va(WorldEntity w) { var v = w.RawPropertySets?.FirstOrDefault(p => p.ClassName == "eCVisualAnimation_PS"); return v == null ? null : w.Source.StrFrom(v.Get("ResourceFilePath")?.Value) + "/S" + v.Get("MaterialSwitch")?.AsInt(); }
                    string Mesh(WorldEntity w) => w.Resource != null ? w.Resource + "/S" + w.MaterialSwitch : null;
                    var sr = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCScriptRoutine_PS");
                    var nps = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCNPC_PS");
                    var parts = children.TryGetValue(i, out var kk) ? string.Join(" ", kk.Select(k => Va(list[k]) ?? Mesh(list[k]) ?? "?" + list[k].Name)) : "";
                    Console.WriteLine($"{(e.Enabled ? " " : "-")} {e.Name,-34} ({e.X / 100:F0},{e.Y / 100:F0},{e.Z / 100:F0}) {Va(e)} sp{nps?.Get("Species")?.AsEnum()} ani{sr?.Get("AniState")?.AsEnum()} act{sr?.Get("Action")?.AsEnum()} amb{sr?.Get("AmbientAction")?.AsEnum()} rtn={e.Source.StrFrom(sr?.Get("Routine")?.Value)} | {parts} @{System.IO.Path.GetFileNameWithoutExtension(path)}");
                    continue;
                }
                if (e.Name == null || e.Name.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) < 0 || !(npc || va)) continue;
                Dump(list, children, i, 0, path);
            }
        }
        foreach (var kv in stats.OrderByDescending(k => k.Value).Take(400)) Console.WriteLine($"{kv.Value,7} {kv.Key}");
        return 0;
    }

    /// <summary>anim npc summary [x z half (m)]: spawn counts, unique visuals, idle-clip resolution.</summary>
    static int NpcSummary(string data, string[] args)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        using var afs = new G3FileSystem(data, "_compiledAnimation");
        const string vol = "Projects_compiled/";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var errors = new List<string>();
        var all = G3.Formats.Animation.NpcSpawns.LoadWorld(layout, p => fs.Get(vol + p)?.Read(), errors);
        Console.WriteLine($"{all.Count} spawns in {sw.ElapsedMilliseconds} ms, errors {errors.Count}");
        var motions = afs.Files.Where(f => f.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase))
            .Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path)).ToList();
        IEnumerable<G3.Formats.Animation.NpcSpawn> sel = all;
        if (args.Length > 4)
        {
            float x = float.Parse(args[2]) * 100, z = float.Parse(args[3]) * 100, h = float.Parse(args[4]) * 100;
            sel = all.Where(n => Math.Abs(n.X - x) <= h && Math.Abs(n.Z - z) <= h);
        }
        var list = sel.ToList();
        var live = list.Where(n => n.Enabled && !G3.Formats.Animation.NpcSpawns.IsTestSector(n.Sector)).ToList();
        Console.WriteLine($"selected {list.Count}, enabled & non-test {live.Count}, test-sector {list.Count(n => G3.Formats.Animation.NpcSpawns.IsTestSector(n.Sector))}, disabled {list.Count(n => !n.Enabled)}");
        var keys = live.GroupBy(n => n.VisualKey).OrderByDescending(g => g.Count()).ToList();
        Console.WriteLine($"unique visuals {keys.Count}");
        var noParts = live.Where(n => n.Parts.Count == 0 && n.ActorStem.EndsWith("_Skeleton", StringComparison.OrdinalIgnoreCase) && n.ActorStem.IndexOf("Body", StringComparison.OrdinalIgnoreCase) < 0).ToList();
        Console.WriteLine($"skeleton without parts: {noParts.Count} {string.Join(", ", noParts.Take(5).Select(n => n.Name))}");
        foreach (var g in live.GroupBy(n => (string.Join("/", n.RigCandidates()), n.PickIdle(motions))).OrderByDescending(g => g.Count()))
            Console.WriteLine($"{g.Count(),6} {g.Key.Item1,-20} {g.Key.Item2 ?? "<NO IDLE>"}");
        foreach (var g in keys.Take(int.Parse(Environment.GetEnvironmentVariable("G3_TOP") ?? "15"))) Console.WriteLine($"{g.Count(),6} {g.Key}");
        foreach (var e in errors.Take(10)) Console.WriteLine("ERR " + e);
        return 0;
    }

    static bool Verbose = Environment.GetEnvironmentVariable("G3_VERBOSE") == "1";

    static void Dump(List<WorldEntity> list, Dictionary<int, List<int>> ch, int i, int depth, string path)
    {
        var e = list[i];
        string ind = new string(' ', depth * 2);
        Console.WriteLine($"{ind}== [{i}] {e.Name} parent={e.Parent} creator={e.Creator} en={e.Enabled}/{e.RenderingEnabled} pos ({e.X:F0},{e.Y:F0},{e.Z:F0}) {(depth == 0 ? path : "")}");
        foreach (var ps in e.RawPropertySets ?? new())
        {
            if (depth > 0 || ps.ClassName is "eCVisualAnimation_PS" or "gCItem_PS" or "gCNPC_PS" or "gCInventory_PS" or "gCScriptRoutine_PS" or "gCNavigation_PS")
            {
                Console.WriteLine($"{ind}  {ps.ClassName}");
                foreach (var p in ps.Properties)
                    if (Verbose || p.Key is "ResourceFilePath" or "FacialAnimFilePath" or "MaterialSwitch" or "ResourceFileName" or "Category" or "Species" or "Gender" or "Bearing" or "Class" or "Type" or "AniState" or "Action" or "AmbientAction" or "Routine" or "TreasureSet1" or "Robe" or "Voice")
                        Console.WriteLine($"{ind}     {p.Key} : {p.Value.Type} = {Fmt(e.Source, p.Value)}");
            }
        }
        if (ch.TryGetValue(i, out var kids)) foreach (var k in kids) Dump(list, ch, k, depth + 1, path);
    }
}
