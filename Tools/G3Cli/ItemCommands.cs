using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Templates;
using G3.Formats.Items;

static partial class Program
{
    /// <summary>G3Cli items &lt;sub&gt; — templates, items, containers (Docs/formats-items.md).</summary>
    static int ItemCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "validate";
        switch (sub)
        {
            case "validate": return ValidateTemplates(data);
            case "tpl": return DumpTemplate(data, args[2]);
            case "survey": return SurveyClass(data, args[2]);
            case "list": return ListItems(data, args.Length > 2 ? args[2] : null);
            case "stats": return ItemStats(data);
            case "world": return WorldItemsCommand(data, args);
            case "ts": return TreasureSets(data, args.Length > 2 ? args[2] : null);
            case "tail":return PsTail(data, args[2], args.Length > 3 ? int.Parse(args[3]) : 48);
            case "npcinv": return NpcTemplateInventory(data, args[2]);
        }
        Console.WriteLine("usage: items validate | tpl <name|guid> | survey <PSclass> | list [filter] | stats | ts [filter] | world [x z half_m] | tail <PSclass> [n] | npcinv <template>");
        return 1;
    }

    static TemplateArchive LoadTemplates(string data)
    {
        using var fs = new G3FileSystem(data, "Templates");
        return TemplateArchive.Load(fs);
    }

    static int ValidateTemplates(string data)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = LoadTemplates(data);
        foreach (var f in a.Failures.Where(f => !f.StartsWith("_deleted/")).Take(30)) Console.WriteLine("FAIL " + f);
        foreach (var gr in a.Failures.GroupBy(f => (f.StartsWith("_deleted/") ? "_deleted: " : "live: ") + f.Substring(f.IndexOf(": ") + 2)))
            Console.WriteLine($"  failures {gr.Key}: {gr.Count()}");
        int headers = a.Files.Sum(f => f.Headers.Count), ps = a.Files.Sum(f => f.Headers.Sum(h => h.PropertySets.Count));
        Console.WriteLine($"tple parsed to exact end: {a.Files.Count}, failed {a.Failures.Count}, headers {headers}, property sets {ps}, {sw.Elapsed.TotalSeconds:F1}s");
        var hc = a.Files.GroupBy(f => f.Headers.Count).OrderBy(g => g.Key).Select(g => $"{g.Key} headers: {g.Count()}");
        Console.WriteLine("  " + string.Join(", ", hc));
        Console.WriteLine($"  deleted (_deleted/) {a.Files.Count(f => f.IsDeleted)}, with RefTemplate headers {a.Files.Count(f => f.Headers.Any(h => h.RefTemplate != Guid.Empty))}");
        int listed = 0, listedFound = 0, listedNameOk = 0;
        foreach (var kv in a.Listed)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            listed++;
            var f = a.Get(kv.Key);
            if (f == null) continue;
            listedFound++;
            if (f.ItemHeader.Guid == kv.Key && f.Path.EndsWith("/" + kv.Value, StringComparison.OrdinalIgnoreCase)) listedNameOk++;
        }
        var missing = a.Listed.Where(kv => !string.IsNullOrEmpty(kv.Value) && a.Get(kv.Key) == null).Select(kv => kv.Value).ToList();
        var failedNames = new HashSet<string>(a.Failures.Select(f => System.IO.Path.GetFileName(f.Substring(0, f.IndexOf(": ")))), StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"  listed but not parsed: {missing.Count} (of which failed-to-parse files {missing.Count(failedNames.Contains)}), e.g. {string.Join(", ", missing.Take(5))}");
        Console.WriteLine($"  .lrtpldatasc entries {listed}: GUID found {listedFound}, = item-header GUID and file name match {listedNameOk}");
        var classes = new Dictionary<string, int>();
        foreach (var f in a.Files) foreach (var p in f.Main.PropertySets) classes[p.ClassName] = classes.TryGetValue(p.ClassName, out var c) ? c + 1 : 1;
        foreach (var kv in classes.OrderByDescending(k => k.Value).Take(60)) Console.WriteLine($"{kv.Value,7} {kv.Key}");
        return a.Failures.Count == 0 ? 0 : 2;
    }

    static string ItemLine(ItemInfo i, ItemCatalog cat = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{i.Name,-34} {i.Category,-9} {i.UseType,-11} {i.GoldValue,6}g");
        if (i.DamageAmount != 0 || i.DamageType != DamageType.None) sb.Append($" dmg {i.DamageType} {i.DamageAmount}");
        if (i.Requirements.Count > 0) sb.Append(" req " + string.Join(",", i.Requirements));
        if (i.Modifiers.Count > 0) sb.Append(" mod " + string.Join(",", i.Modifiers));
        if (i.Permanent) sb.Append(" PERMANENT");
        if (i.MissionItem) sb.Append(" mission");
        if (i.Robe) sb.Append(" robe");
        if (i.Spell != Guid.Empty) sb.Append(" spell");
        if (i.Skill != Guid.Empty) sb.Append(" skill");
        if (!string.IsNullOrEmpty(i.ScriptUseFunc)) sb.Append($" use={i.ScriptUseFunc}");
        sb.Append($" | {(i.Actor ?? i.Mesh ?? "-")}{(i.MaterialSwitch != 0 ? $" ms{i.MaterialSwitch}" : "")} icon {(i.Icon == "" ? "-" : i.Icon)} mass {i.Mass:G3}");
        if (i.Slot != EquipSlot.None) sb.Append($" slot {i.Slot}");
        return sb.ToString();
    }

    /// <summary>items list [filter]: filter = category (Weapon, Armor…), use type (Potion, OneHanded…) or name substring.</summary>
    static int ListItems(string data, string filter)
    {
        var cat = ItemCatalog.Build(LoadTemplates(data));
        IEnumerable<ItemInfo> q = cat.Items;
        if (filter != null)
            q = q.Where(i => i.Category.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                             i.UseType.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                             i.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        int n = 0;
        foreach (var i in q.OrderBy(i => i.Category).ThenBy(i => i.UseType).ThenBy(i => i.Name)) { Console.WriteLine(ItemLine(i, cat) + $" \"{DisplayName(data, i.Name)}\""); n++; }
        Console.WriteLine($"{n} items");
        return 0;
    }

    /// <summary>Counts by category/use type and resolution checks of icons, meshes, name strings.</summary>
    static int ItemStats(string data)
    {
        var cat = ItemCatalog.Build(LoadTemplates(data));
        using var img = new G3FileSystem(data, "_compiledImage", "_compiledMesh", "_compiledAnimation", "Strings");
        var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var st = img.GetByName("stringtable.ini");
        if (st != null)
            foreach (var line in System.Text.Encoding.GetEncoding(28591).GetString(st.Read()).Split('\n'))
            { int eq = line.IndexOf('='); if (eq > 0) strings.Add(line.Substring(0, eq).Trim()); }
        Console.WriteLine($"item templates (live, with gCItem_PS): {cat.Items.Count}");
        foreach (var g in cat.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-10} {g.Count(),5}: " + string.Join(", ", g.GroupBy(i => i.UseType).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {x.Count()}")));
        var real = cat.Items.Where(i => i.Category != ItemCategory.None).ToList();
        int icon = real.Count(i => !string.IsNullOrEmpty(i.Icon)), iconOk = real.Count(i => i.IconImage != null && img.GetByName(i.IconImage) != null);
        int mesh = real.Count(i => i.Mesh != null), meshOk = real.Count(i => i.Mesh != null && img.GetByName(i.Mesh) != null);
        int nameOk = real.Count(i => strings.Contains(i.NameKey)), descOk = real.Count(i => strings.Contains(i.DescriptionKey));
        Console.WriteLine($"categorised items {real.Count}: icon set {icon} (ximg found {iconOk}), mesh {mesh} (found {meshOk}), " +
                          $"actor {real.Count(i => i.Actor != null)}, FO_ name key {nameOk}, ITEMDESC_ key {descOk}, post data ok {cat.Items.Count(i => i.PostDataOk)}/{cat.Items.Count}");
        foreach (var i in real.Where(i => i.IconImage != null && img.GetByName(i.IconImage) == null).Take(10)) Console.WriteLine($"  icon missing: {i.Name} {i.Icon}");
        foreach (var i in real.Where(i => !strings.Contains(i.NameKey)).Take(10)) Console.WriteLine($"  name key missing: {i.NameKey}");
        var none = cat.Items.Where(i => i.Category == ItemCategory.None).ToList();
        Console.WriteLine($"category None {none.Count}: " + string.Join(", ", none.GroupBy(i => i.UseType).OrderByDescending(x => x.Count()).Take(15).Select(x => $"{x.Key} {x.Count()}")));
        Console.WriteLine("  samples: " + string.Join(", ", none.Where(i => i.UseType == UseType.None).Take(15).Select(i => i.Name)));
        return 0;
    }

    sealed class WorldScan
    {
        public readonly List<WorldItem> Items = new List<WorldItem>();
        public readonly List<WorldContainer> Containers = new List<WorldContainer>();
        public readonly List<WorldInteractable> Interactables = new List<WorldInteractable>();
        public readonly Dictionary<object, string> FileOf = new Dictionary<object, string>();
        public readonly HashSet<string> DisabledSectorFiles = new HashSet<string>();
    }

    /// <summary>Scan every .lrentdat and .node of the load set (WorldLayout) with keepRaw.</summary>
    static WorldScan ScanWorld(string data)
    {
        var scan = new WorldScan();
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var layout = G3.Formats.World.WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        foreach (var s in layout.Sectors)
        foreach (var path in s.Lrentdats.Concat(s.Nodes))
        {
            var entry = fs.Get(vol + path);
            List<G3.Formats.World.WorldEntity> list;
            try { list = entry == null ? null : G3.Formats.World.EntityArchive.Read(entry.Read(), false, keepRaw: true); }
            catch (Exception ex) { Console.WriteLine($"FAIL {path}: {ex.Message}"); continue; }
            if (list == null) continue;
            int i0 = scan.Items.Count, c0 = scan.Containers.Count, n0 = scan.Interactables.Count;
            WorldItemScanner.Scan(list, scan.Items, scan.Containers, scan.Interactables);
            foreach (var x in scan.Items.Skip(i0)) scan.FileOf[x] = path;
            foreach (var x in scan.Containers.Skip(c0)) scan.FileOf[x] = path;
            foreach (var x in scan.Interactables.Skip(n0)) scan.FileOf[x] = path;
        }
        return scan;
    }

    static string TplName(TemplateArchive a, Guid g) => g == Guid.Empty ? "-" : a.Get(g)?.Name ?? $"?{g.ToString().Substring(0, 8)}";

    /// <summary>items world [x z half] — classify world gCItem_PS entities, containers and interactables (coords in metres).</summary>
    static int WorldItemsCommand(string data, string[] args)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tpl = LoadTemplates(data);
        var scan = ScanWorld(data);
        bool region = args.Length > 4;
        float cx = 0, cz = 0, half = 0;
        if (region) { cx = float.Parse(args[2]) * 100; cz = float.Parse(args[3]) * 100; half = float.Parse(args[4]) * 100; }
        bool In(G3.Formats.World.WorldEntity e) => !region || (Math.Abs(e.X - cx) <= half && Math.Abs(e.Z - cz) <= half);
        string Pos(G3.Formats.World.WorldEntity e) => $"({e.X / 100:F1}, {e.Y / 100:F1}, {e.Z / 100:F1}) m";

        if (!region)
        {
            Console.WriteLine($"scan {sw.Elapsed.TotalSeconds:F1}s: gCItem_PS entities {scan.Items.Count:N0} " +
                              $"(.lrentdat {scan.Items.Count(i => scan.FileOf[i].EndsWith(".lrentdat")):N0}, .node {scan.Items.Count(i => scan.FileOf[i].EndsWith(".node"))})");
            foreach (var g in scan.Items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
            {
                var l = g.ToList();
                Console.WriteLine($"  {g.Key,-13} {l.Count,6:N0}  enabled {l.Count(i => i.Entity.Enabled),6:N0}  creator→template {l.Count(i => tpl.Get(i.Template) != null),6:N0}" +
                                  $" (= item header {l.Count(i => tpl.Get(i.Template)?.Guid == i.Template):N0})  in SysDyn {l.Count(i => scan.FileOf[i].Contains("SysDyn")):N0}");
                Console.WriteLine("      by use type: " + string.Join(", ", l.GroupBy(i => i.Item.UseType).OrderByDescending(x => x.Count()).Take(12).Select(x => $"{x.Key} {x.Count()}")));
                Console.WriteLine("      by slot: " + string.Join(", ", l.GroupBy(i => i.Item.Slot).OrderByDescending(x => x.Count()).Take(8).Select(x => $"{x.Key} {x.Count()}")) +
                                  $"; visible {l.Count(i => i.Item.Visible)}; post data ok {l.Count(i => i.Item.PostDataOk)}");
                Console.WriteLine("      parents: " + string.Join(", ", l.GroupBy(i => $"{i.ParentName ?? "-"}@{i.Depth}").OrderByDescending(x => x.Count()).Take(8).Select(x => $"{x.Key} {x.Count()}")));
                if (g.Key == WorldItemKind.Loose || g.Key == WorldItemKind.Plant)
                {
                    Console.WriteLine("      top: " + string.Join(", ", l.GroupBy(i => i.Entity.Name).OrderByDescending(x => x.Count()).Take(14).Select(x => $"{x.Key} {x.Count()}")));
                    Console.WriteLine($"      amount>1 {l.Count(i => i.Item.Amount > 1)}, scale≠1 {l.Count(i => Math.Abs(Math.Sqrt(i.Entity.World[0] * i.Entity.World[0] + i.Entity.World[1] * i.Entity.World[1] + i.Entity.World[2] * i.Entity.World[2]) - 1) > 0.01)}, " +
                                      $"name≠template {l.Count(i => tpl.Get(i.Template) != null && !string.Equals(tpl.Get(i.Template).Name, i.Entity.Name, StringComparison.OrdinalIgnoreCase))}, " +
                                      $"mesh≠template {l.Count(i => tpl.Get(i.Template) is TemplateFile t && ItemInfo.From(t) is ItemInfo ti && !string.Equals(ti.Mesh, i.Item.Mesh, StringComparison.OrdinalIgnoreCase))}, " +
                                      $"no mesh {l.Count(i => i.Item.Mesh == null && i.Item.Actor == null)}");
                }
            }
            Console.WriteLine($"containers (gCInventory_PS) {scan.Containers.Count:N0}: " + string.Join(", ", scan.Containers.GroupBy(c => c.Kind).Select(x =>
                $"{x.Key} {x.Count():N0} (with stacks {x.Count(c => c.Inventory?.Stacks.Count > 0):N0}, stacks {x.Sum(c => c.Inventory?.Stacks.Count ?? 0):N0}, slots {x.Sum(c => c.Inventory?.Slots.Count ?? 0)}, treasure set {x.Count(c => c.Inventory?.TreasureSets.Any(t => t != "") == true):N0}, locked {x.Count(c => c.Lock?.Locked == true)}, parsed exactly {x.Count(c => c.Inventory?.ParsedExactly == true):N0})")));
            var stacks = scan.Containers.Where(c => c.Inventory != null).SelectMany(c => c.Inventory.Stacks).ToList();
            Console.WriteLine($"  stacks → template resolved {stacks.Count(s => tpl.Get(s.Template) != null)}/{stacks.Count}; type: " +
                              string.Join(", ", stacks.GroupBy(s => s.Type).Select(x => $"{(StackType)x.Key} {x.Count()}")));
            var sample = stacks.FirstOrDefault();
            if (sample != null) Console.WriteLine("  stack properties: " + string.Join(", ", sample.Raw.Properties.Select(p => $"{p.Key}:{p.Value.Type}")));
            var chests = scan.Containers.Where(c => c.Kind == ContainerKind.Chest).ToList();
            Console.WriteLine($"  chests: lock difficulty " + string.Join(", ", chests.GroupBy(c => c.Lock == null ? "no lock" : c.Lock.Locked ? $"locked d{c.Lock.Difficulty}" : $"unlocked d{c.Lock.Difficulty}").OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}")) +
                              $"; key {chests.Count(c => c.Lock?.Key != Guid.Empty && c.Lock != null)}; treasure sets " +
                              string.Join(", ", chests.SelectMany(c => c.Inventory?.TreasureSets ?? new string[0]).Where(t => t != "").GroupBy(t => t).OrderByDescending(x => x.Count()).Take(8).Select(x => $"{x.Key} {x.Count()}")));
            Console.WriteLine($"interactables {scan.Interactables.Count:N0} (.node {scan.Interactables.Count(i => scan.FileOf[i].EndsWith(".node")):N0}): " +
                              string.Join(", ", scan.Interactables.GroupBy(i => i.Interaction?.UseType.ToString() ?? (i.Door != null ? "door-only" : "lock-only")).OrderByDescending(x => x.Count()).Take(30).Select(x => $"{x.Key} {x.Count()}")));
            Console.WriteLine($"  doors (gCDoor_PS) {scan.Interactables.Count(i => i.Door != null)} (closed {scan.Interactables.Count(i => i.Door?.Closed == true)}, locked {scan.Interactables.Count(i => i.Door != null && i.Lock?.Locked == true)}), " +
                              $"locks on non-containers {scan.Interactables.Count(i => i.Lock != null)}; door anims " +
                              string.Join(", ", scan.Interactables.Where(i => i.Door != null).GroupBy(i => i.Door.OpenAni).OrderByDescending(x => x.Count()).Take(6).Select(x => $"{x.Key} {x.Count()}")));
            return 0;
        }

        Console.WriteLine($"== loose items within {half / 100} m of ({cx / 100}, {cz / 100}) m");
        foreach (var i in scan.Items.Where(i => (i.Kind == WorldItemKind.Loose || i.Kind == WorldItemKind.Plant) && In(i.Entity)).OrderBy(i => i.Entity.Name))
            Console.WriteLine($"  {i.Kind,-6} {i.Entity.Name,-28} \"{DisplayName(data, TplName(tpl, i.Template))}\" x{i.Item.Amount,-3} {Pos(i.Entity)} tpl {TplName(tpl, i.Template)} {(i.Entity.Enabled ? "" : "DISABLED ")}{i.Item.Mesh ?? i.Item.Actor} [{System.IO.Path.GetFileName(scan.FileOf[i])}]");
        Console.WriteLine($"== containers");
        foreach (var c in scan.Containers.Where(c => c.Kind != ContainerKind.NpcInventory && In(c.Entity)))
        {
            Console.WriteLine($"  {c.Kind} {c.Entity.Name} {Pos(c.Entity)} lock {(c.Lock == null ? "-" : (c.Lock.Locked ? "locked" : "open") + $" d{c.Lock.Difficulty} key {TplName(tpl, c.Lock.Key)}")} sets [{string.Join(",", c.Inventory?.TreasureSets.Where(t => t != "") ?? new string[0])}]");
            foreach (var s in c.Inventory?.Stacks ?? new List<InventoryStack>())
                Console.WriteLine($"      {s.Amount,4} × {TplName(tpl, s.Template)} \"{DisplayName(data, TplName(tpl, s.Template))}\" ({(StackType)s.Type}{(s.Quality != 0 ? $", q{s.Quality}" : "")})");
        }
        var npcs = scan.Containers.Where(c => c.Kind == ContainerKind.NpcInventory && In(c.Entity)).ToList();
        Console.WriteLine($"== NPC inventories: {npcs.Count}, with stacks {npcs.Count(c => c.Inventory?.Stacks.Count > 0)}");
        foreach (var c in npcs.Take(15))
            Console.WriteLine($"  {c.Entity.Name,-24} sets [{string.Join(",", c.Inventory?.TreasureSets.Where(t => t != "") ?? new string[0])}] stacks {c.Inventory?.Stacks.Count}: " +
                              string.Join(", ", (c.Inventory?.Stacks ?? new List<InventoryStack>()).Take(8).Select(s => $"{s.Amount}×{TplName(tpl, s.Template)}")));
        Console.WriteLine($"== interactables");
        foreach (var g in scan.Interactables.Where(i => In(i.Entity)).GroupBy(i => i.Interaction?.UseType.ToString() ?? "door/lock").OrderByDescending(x => x.Count()))
            Console.WriteLine($"  {g.Key,-12} {g.Count(),4}: " + string.Join(", ", g.GroupBy(i => i.Visual ?? i.Entity.Name).Take(5).Select(x => $"{x.Key} {x.Count()}")));
        return 0;
    }

    /// <summary>items npcinv &lt;template&gt;: gCInventory_PS of every header of an NPC template (sets, stacks, equipped slots).</summary>
    static int NpcTemplateInventory(string data, string name)
    {
        var a = LoadTemplates(data);
        foreach (var f in a.Files.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            foreach (var h in f.Headers)
                for (int i = 0; i < h.PropertySets.Count; i++)
                {
                    if (h.PropertySets[i].ClassName != "gCInventory_PS") continue;
                    var inv = Inventory.Read(h.Source, h.PropertySets[i], h.PostDataStart[i]);
                    Console.WriteLine($"== {f.Path} header {h.Guid} exact {inv.ParsedExactly} sets [{string.Join(",", inv.TreasureSets.Where(t => t != ""))}] stacks {inv.Stacks.Count}");
                    foreach (var s in inv.Stacks)
                        Console.WriteLine($"   {s.Amount,4} x {TplName(a, s.Template)} q{s.Quality} type {s.Type} quick {s.QuickSlot}");
                    foreach (var sl in inv.Slots.Where(sl => sl.Stack >= 0))
                        Console.WriteLine($"   slot {sl.Index} {sl.Slot} -> stack {sl.Stack}");
                }
        return 0;
    }

    /// <summary>items ts [filter]: treasure set templates with their candidate stacks.</summary>
    static int TreasureSets(string data, string filter)
    {
        var a = LoadTemplates(data);
        var sets = a.Files.Where(f => !f.IsDeleted).Select(TreasureSet.From).Where(t => t != null).ToList();
        Console.WriteLine($"treasure sets {sets.Count}, inventory parsed exactly {sets.Count(t => t.Inventory?.ParsedExactly == true)}, stacks {sets.Sum(t => t.Inventory?.Stacks.Count ?? 0)}, " +
                          $"stack templates resolved {sets.SelectMany(t => t.Inventory?.Stacks ?? new List<InventoryStack>()).Count(s => a.Get(s.Template) != null)}");
        foreach (var t in sets.Where(t => filter == null || t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Take(filter == null ? 10 : 50))
        {
            Console.WriteLine($"  {t.Name} dist {t.Distribution} stacks {t.MinStacks}..{t.MaxStacks}:");
            foreach (var s in t.Inventory?.Stacks ?? new List<InventoryStack>())
                Console.WriteLine($"      {s.Amount,4} × {TplName(a, s.Template)}{(s.Quality != 0 ? $" q{s.Quality}" : "")}");
        }
        return 0;
    }

    /// <summary>Debug: hex of the last bytes of a PS class in a few templates and world entities.</summary>
    static int PsTail(string data, string cls, int len)
    {
        var a = LoadTemplates(data);
        foreach (var f in a.Files.Where(f => !f.IsDeleted && f.Get(cls) != null).Take(4))
        {
            var ps = f.Main.Get(cls) ?? f.Get(cls);
            var d = f.Main.Source.Data;
            Console.WriteLine($"tpl {f.Name}: " + BitConverter.ToString(d, (int)ps.End - len, len));
        }
        using var fs = new G3FileSystem(data, "Projects_compiled");
        int n = 0;
        foreach (var e in fs.Files.Where(e => e.Path.EndsWith(".lrentdat")))
        {
            var list = G3.Formats.World.EntityArchive.Read(e.Read(), false, true);
            if (list == null) continue;
            foreach (var w in list)
            {
                var ps = w.RawPropertySets?.FirstOrDefault(p => p.ClassName == cls);
                if (ps == null) continue;
                Console.WriteLine($"world {w.Name}: " + BitConverter.ToString(w.Source.Data, (int)ps.End - len, len));
                if (++n >= 6) return 0;
            }
        }
        return 0;
    }

    /// <summary>Property names/types/value histogram of one PS class over all live templates.</summary>
    static int SurveyClass(string data, string cls)
    {
        var a = LoadTemplates(data);
        var stats = new SortedDictionary<string, Dictionary<string, int>>();
        int n = 0;
        foreach (var f in a.Files.Where(f => !f.IsDeleted))
        foreach (var h in f.Headers.Where(h => !h.HelperParent))
        foreach (var ps in h.PropertySets.Where(p => p.ClassName == cls))
        {
            n++;
            foreach (var p in ps.Properties)
            {
                var k = $"{p.Key} : {p.Value.Type}";
                if (!stats.TryGetValue(k, out var d)) stats[k] = d = new Dictionary<string, int>();
                var v = Fmt(h.Source, p.Value);
                if (v.StartsWith("<")) v = $"<{p.Value.Value.Length} bytes>";
                d[v] = d.TryGetValue(v, out var c) ? c + 1 : 1;
            }
        }
        Console.WriteLine($"{cls}: {n} instances");
        foreach (var kv in stats)
            Console.WriteLine($"  {kv.Key}  [{kv.Value.Values.Sum()}] " +
                              string.Join(", ", kv.Value.OrderByDescending(x => x.Value).Take(12).Select(x => $"{x.Key}×{x.Value}")));
        return 0;
    }

    static int DumpTemplate(string data, string key)
    {
        var a = LoadTemplates(data);
        var hits = Guid.TryParse(key.Trim('{', '}'), out var guid)
            ? new[] { a.Get(guid) }.Where(f => f != null).ToList()
            : a.Files.Where(f => f.Name != null && f.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).Take(20).ToList();
        foreach (var f in hits)
        {
            Console.WriteLine($"== {f.Name} {f.Path} guid {f.Guid}");
            foreach (var h in f.Headers)
            {
                Console.WriteLine($" -- header '{h.Name}' guid {h.Guid} helper {h.HelperParent} parent {h.Parent} ref {(h.RefTemplate == Guid.Empty ? "-" : h.RefTemplate.ToString())} file {h.FileName}");
                for (int i = 0; i < h.PropertySets.Count; i++)
                {
                    var ps = h.PropertySets[i];
                    Console.WriteLine($"  {ps.ClassName} v{ps.ClassVersion}");
                    foreach (var p in ps.Properties)
                        Console.WriteLine($"     {p.Key} : {p.Value.Type} = {Fmt(h.Source, p.Value)}");
                    if (ps.ClassName == "eCCollisionShape_PS" && i < h.PostDataStart.Count && h.Source != null)
                    {
                        h.Source.Position = h.PostDataStart[i];
                        foreach (var s in G3.Formats.World.EntityArchive.ReadCollisionShapes(h.Source, ps))
                            Console.WriteLine($"     shape {s.Type} group {s.Group} mat {s.Material} r {s.Radius:0.#} h {s.Height:0.#} " +
                                              $"centre ({string.Join(", ", s.Center.Select(v => v.ToString("0.#")))}) extent ({string.Join(", ", s.Extent.Select(v => v.ToString("0.#")))}) " +
                                              $"bounds ({string.Join(", ", s.LocalBounds.Select(v => v.ToString("0.#")))}) noColl {s.DisableCollision}");
                    }
                }
            }
        }
        return 0;
    }
}

static partial class Program
{
    static G3.Formats.Story.StringTable _itemStrings;

    /// <summary>English display name of an item via its FO_ key (Strings stringtable.ini), or null.</summary>
    static string DisplayName(string data, string templateName)
    {
        if (_itemStrings == null)
        {
            using var fs = new G3FileSystem(data, "Strings");
            var e = fs.GetByName("stringtable.ini");
            _itemStrings = e != null ? G3.Formats.Story.StringTable.ReadIni(e.Read()) : new G3.Formats.Story.StringTable();
        }
        return _itemStrings.Get("FO_" + templateName, "English");
    }
}
