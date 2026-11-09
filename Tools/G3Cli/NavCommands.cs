using System;
using System.Linq;
using G3.Formats.Pak;

static partial class Program
{
    /// <summary>G3Cli nav raw &lt;nameFilter|x,z,r&gt; [max] — raw bytes of gCNavPath_PS / gCNegZone_PS properties (format probe).</summary>
    static int NavRaw(string data, string[] args)
    {
        string filter = args.Length > 2 ? args[2] : null;
        int max = args.Length > 3 ? int.Parse(args[3]) : 5;
        using var fs = new G3FileSystem(data, "Projects_compiled");
        int shown = 0;
        foreach (var f in fs.Files.Where(f => f.Path.EndsWith(".node", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase)))
        {
            System.Collections.Generic.List<G3.Formats.World.WorldEntity> list;
            try { list = G3.Formats.World.EntityArchive.Read(f.Read(), false, keepRaw: true); } catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                var ps = e.RawPropertySets?.FirstOrDefault(p => p.ClassName == "gCNavPath_PS");
                if (ps == null || (filter != null && !Matches(e, filter))) continue;
                if (shown++ >= max) return 0;
                Console.WriteLine($"== {e.Name} {{{e.Guid}}} enabled={e.Enabled} {f.Path}");
                foreach (var p in ps.Properties)
                    if (p.Key is "Radius" or "ZoneAEntityID" or "ZoneBEntityID")
                        Console.WriteLine($"   {p.Key} [{p.Value.Value.Length}] {BitConverter.ToString(p.Value.Value).Replace("-", "")}");
            }
        }
        return 0;
    }
}

static partial class Program
{
    /// <summary>
    /// G3Cli nav stats | route &lt;from&gt; &lt;to&gt; [--points] [--snap m] | guides [--points] [--pack dir] [--data] — G3's
    /// designer-placed navigation (Docs/navigation.md), from the pack's navigation folder (or, without one or with --data,
    /// read from the game data). from / to: "x,y,z" (metres), a place id or an NPC id (its spawn) of the content pack.
    /// </summary>
    static int NavCommand(string data, string[] args)
    {
        switch (args.Length > 1 ? args[1] : "")
        {
            case "raw": return NavRaw(data, args);
            case "stats": return NavStats(data);
            case "dangling": return NavDangling(data);
            case "route" when args.Length > 3: return NavRoutes(data, args, new[] { (args[2], args[3]) });
            case "guides": return NavRoutes(data, args, KnownGuides);
            default:
                Console.WriteLine("usage: G3Cli nav stats | route <from> <to> [--points] [--snap m] | guides [--points] | raw <filter> [max]");
                return 1;
        }
    }

    /// <summary>The far guides whose NavMesh walks failed (fix-17..25): guide (spawn) → FollowNpc destination.</summary>
    static readonly (string, string)[] KnownGuides =
    {
        ("Xardas", "Adanos_Gate"), ("Mort", "FP_Wolfclan"), ("Mort", "FP_Feuerclan"), ("Mort", "FP_Mine_Valley"), ("Hauke", "FP_Hammer_Camp"),
        ("Geldern_Slave_01", "Geldern_WolfMineCheckPoint"), ("Cole", "Cole_NemoraCheckPoint"), ("Rufus", "Okara_RufusCheckPoint"),
        ("Rufus", "Montera_RufusCheckPoint"),
    };

    /// <summary>The graph from a pack's navigation folder (G3Cli content import), null when it has none.</summary>
    static Myrtana.Simulation.NavGraph LoadPackNavGraph(string pack)
    {
        string dir = System.IO.Path.Combine(pack, Myrtana.Simulation.SimulationSchema.NavigationFolder);
        if (!System.IO.Directory.Exists(dir)) return null;
        var schema = new Myrtana.Core.ContentSchema();
        Myrtana.Simulation.SimulationSchema.Register(schema);
        var options = Myrtana.Core.ContentJson.CreateOptions(schema);
        var sectors = System.IO.Directory.GetFiles(dir, "*.json")
            .SelectMany(f => System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<Myrtana.Simulation.NavSectorDef>>(System.IO.File.ReadAllText(f), options))
            .ToList();
        var graph = new Myrtana.Simulation.NavGraph(sectors);
        Console.WriteLine($"nav (pack {pack}): {graph.ZoneCount} zones, {graph.PathCount} paths, {sectors.Count} sectors, {graph.NetCount} nets, " +
                          $"{graph.DanglingPathEnds} path ends in no zone, {graph.RelinkedPathEnds} relinked");
        return graph;
    }

    static Myrtana.Simulation.NavGraph LoadNavGraph(string data, out Myrtana.Import.G3.G3NavImporter importer)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        importer = new Myrtana.Import.G3.G3NavImporter();
        using (var fs = new G3FileSystem(data, "Projects_compiled"))
        {
            var byPath = fs.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            importer.Import(byPath.Keys, p => byPath.TryGetValue(p, out var f) ? f.Read() : null, null);
        }
        var graph = new Myrtana.Simulation.NavGraph(importer.Sectors);
        Console.WriteLine($"nav: {graph.ZoneCount} zones, {graph.PathCount} paths, {importer.NegZoneCount} neg zones, {importer.Sectors.Count} sectors, " +
                          $"{graph.NetCount} nets, {graph.DanglingPathEnds} path ends in no zone, {graph.RelinkedPathEnds} relinked ({sw.Elapsed.TotalSeconds:F1} s)");
        return graph;
    }

    static int NavStats(string data)
    {
        var graph = LoadNavGraph(data, out var imp);
        var zonesWithoutPaths = 0;
        var netSizes = new System.Collections.Generic.Dictionary<int, int>();
        for (int z = 0; z < graph.ZoneCount; z++)
        {
            int net = graph.NetOf(new Myrtana.Simulation.NavArea(Myrtana.Simulation.NavAreaKind.Zone, z, 0));
            if (net < 0) zonesWithoutPaths++;
            else netSizes[net] = netSizes.TryGetValue(net, out int c) ? c + 1 : 1;
        }
        Console.WriteLine($"zones without paths {zonesWithoutPaths}; largest nets (zones): {string.Join(", ", netSizes.Values.OrderByDescending(v => v).Take(8))}");
        var places = NavPlaces(DefaultPackDir);
        int inside = 0, snapped = 0, main = 0;
        int mainNet = Enumerable.Range(0, graph.ZoneCount).Select(z => graph.NetOf(new Myrtana.Simulation.NavArea(Myrtana.Simulation.NavAreaKind.Zone, z, 0)))
                                .Where(n => n >= 0).GroupBy(n => n).OrderByDescending(g => g.Count()).First().Key;
        foreach (var p in places.Values)
        {
            var a = graph.Locate(p[0], p[1], p[2], 30f);
            if (!a.IsValid) continue;
            if (a.Snapped > 0) snapped++; else inside++;
            if (graph.NetOf(a) == mainNet) main++;
        }
        Console.WriteLine($"places + NPC spawns of the pack: {places.Count}; in a zone/path {inside}, snapped (<= 30 m) {snapped}, none {places.Count - inside - snapped}; in the main net {main}");
        foreach (var s in imp.Sectors.OrderByDescending(s => s.Zones.Length + s.Paths.Length).Take(8))
            Console.WriteLine($"  {s.Id}: {s.Zones.Length} zones, {s.Paths.Length} paths");
        return 0;
    }

    static int NavRoutes(string data, string[] args, (string, string)[] pairs)
    {
        var places = NavPlaces(Option(args, "--pack") ?? DefaultPackDir);
        float snap = float.TryParse(Option(args, "--snap"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sn) ? sn : 30f;
        var graph = (args.Contains("--data") ? null : LoadPackNavGraph(Option(args, "--pack") ?? DefaultPackDir)) ?? LoadNavGraph(data, out _);
        int failed = 0;
        foreach (var (a, b) in pairs)
        {
            if (!NavPoint(places, a, out var p) || !NavPoint(places, b, out var q))
            {
                Console.WriteLine($"{a} -> {b}: unknown name");
                failed++;
                continue;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = graph.FindRoute(p[0], p[1], p[2], q[0], q[1], q[2], snap);
            float crow = (float)Math.Sqrt((p[0] - q[0]) * (p[0] - q[0]) + (p[2] - q[2]) * (p[2] - q[2]));
            string I(float v) => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"{a} -> {b}: {(r.Found ? "ROUTE" : "NONE")} {(r.Found ? I(r.Length) + " m" : "")} (crow {I(crow)} m, {r.Points.Count} pts, " +
                              $"{sw.Elapsed.TotalMilliseconds:F0} ms) {r.Note}; start {graph.Describe(r.Start)}; goal {graph.Describe(r.Goal)}");
            if (!r.Found) failed++;
            if (args.Contains("--points"))
                foreach (var pt in r.Points)
                    Console.WriteLine($"    {I(pt.X)},{pt.Y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},{I(pt.Z)} w={pt.Width.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} {pt.On}");
        }
        return failed == 0 ? 0 : 2;
    }

    /// <summary>Place positions and NPC spawns (metres) from the pack's places / npcs files.</summary>
    static System.Collections.Generic.Dictionary<string, float[]> NavPlaces(string pack)
    {
        var map = new System.Collections.Generic.Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        string placesFile = System.IO.Path.Combine(pack, "places", "places.json");
        if (System.IO.File.Exists(placesFile))
            foreach (var e in System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(placesFile)).RootElement.EnumerateArray())
                if (e.TryGetProperty("position", out var pos) && pos.GetArrayLength() >= 3)
                    map[e.GetProperty("id").GetString()] = new[] { pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle() };
        string npcDir = System.IO.Path.Combine(pack, "npcs");
        if (System.IO.Directory.Exists(npcDir))
            foreach (var f in System.IO.Directory.GetFiles(npcDir, "*.json"))
            foreach (var e in System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f)).RootElement.EnumerateArray())
                if (e.TryGetProperty("spawn", out var sp) && sp.ValueKind == System.Text.Json.JsonValueKind.Object)
                    map.TryAdd(e.GetProperty("id").GetString(), new[] { sp.GetProperty("x").GetSingle(), sp.GetProperty("y").GetSingle(), sp.GetProperty("z").GetSingle() });
        return map;
    }

    static bool NavPoint(System.Collections.Generic.Dictionary<string, float[]> places, string name, out float[] p)
    {
        var parts = name.Split(',');
        if (parts.Length == 3 && parts.All(x => float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            p = parts.Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return true;
        }
        return places.TryGetValue(name, out p);
    }
}

static partial class Program
{
    /// <summary>G3Cli nav dangling — what the path zone ids that name no imported zone point at (format probe).</summary>
    static int NavDangling(string data)
    {
        var graph = LoadNavGraph(data, out var imp);
        var zoneIds = new System.Collections.Generic.HashSet<string>(imp.Sectors.SelectMany(s => s.Zones).Select(z => z.Id), StringComparer.OrdinalIgnoreCase);
        var dangling = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in imp.Sectors.SelectMany(s => s.Paths))
            foreach (var id in new[] { p.ZoneA, p.ZoneB })
                if (id != null && !zoneIds.Contains(id)) dangling[id] = dangling.TryGetValue(id, out int c) ? c + 1 : 1;
        Console.WriteLine($"{dangling.Count} distinct dangling ids; top: {string.Join(", ", dangling.OrderByDescending(k => k.Value).Take(5).Select(k => k.Key + " x" + k.Value))}");
        var found = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var fs = new G3FileSystem(data, "Projects_compiled");
        foreach (var f in fs.Files.Where(f => f.Path.EndsWith(".node", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase)))
        {
            System.Collections.Generic.List<G3.Formats.World.WorldEntity> list;
            try { list = G3.Formats.World.EntityArchive.Read(f.Read(), false, keepRaw: true); } catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
                if (dangling.ContainsKey(e.Guid.ToString("D")))
                    found[e.Guid.ToString("D")] = $"{e.Name} enabled={e.Enabled} [{string.Join("+", (e.RawPropertySets ?? new()).Select(p => p.ClassName))}] {f.Path}";
        }
        Console.WriteLine($"{found.Count} of them are entities:");
        foreach (var kv in found.Take(10)) Console.WriteLine($"  {kv.Key} x{dangling[kv.Key]}: {kv.Value}");
        return 0;
    }
}
