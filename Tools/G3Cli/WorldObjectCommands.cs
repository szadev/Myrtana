using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;

static partial class Program
{
    /// <summary>
    /// `objects probe &lt;substr&gt;`: where entities whose name/mesh contains substr live and how they are classified.
    /// `objects around x y z [r]`: every entity within r m of a Unity position, with its property sets (fix-150).
    /// `objects near x z [half]`: those static meshes around a Unity position (m), with their streaming cell.
    /// `objects missing`: static visuals of the .lrentdat files (beds, benches, thrones …) by type, PS set and region.
    /// </summary>
    static int WorldObjectCommand(string data, string[] args)
    {
        string mode = args.Length > 1 ? args[1] : "missing";
        string sub = args.Length > 2 ? args[2] : "Bed";
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        if (mode == "sectors") return SectorContents(fs, vol, args.Length > 2 ? args[2] : null);
        if (mode == "npcmesh") return NpcMeshes(fs, vol);
        if (mode == "swimdepth") return NpcSwimDepths(fs, vol);
        if (mode == "nocollision") return NpcsWithoutCollisionShape(fs, vol);
        if (mode == "usetypes") return UseTypeCounts(fs, vol, args.Length > 2 ? args[2] : null);
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var hist = new Dictionary<string, int>();
        void Count(string k) => hist[k] = hist.TryGetValue(k, out var c) ? c + 1 : 1;
        var nodeKeys = new HashSet<string>();
        var dyn = new List<(WorldEntity e, string sector)>();
        var centroids = new Dictionary<string, (double x, double z, int n)>();
        int animatedProps = 0;
        int shown = 0;
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes.Concat(s.Lrentdats))
        {
            bool isDyn = path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase);
            var list = EntityArchive.Read(fs.Get(vol + path)?.Read(), decodeVegetation: false, keepRaw: mode == "probe" || mode == "around" || isDyn);
            if (list == null) continue;
            foreach (var e in list)
            {
                if (mode == "around")
                {
                    // objects around <x> <y> <z> [r]: every entity (nodes + lrentdats) within r m (Unity metres), with its PS set (fix-150)
                    float ax = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), ay = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                          az = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), ar = args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 6;
                    double dx = e.X / 100 - ax, dy = e.Y / 100 - ay, dz = e.Z / 100 - az, dd = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dd > ar) continue;
                    Console.WriteLine($"{dd,5:F1} m  {e.Name} {{{e.Guid}}} | {e.Resource} | {e.X / 100:F2},{e.Y / 100:F2},{e.Z / 100:F2} | en={e.Enabled} npc={e.IsNpc} item={e.IsItem} | {path} | {string.Join(",", (e.RawPropertySets ?? new()).Select(p => p.ClassName))}");
                    continue;
                }
                if (mode == "probe")
                {
                    if ((e.Name ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (e.Resource ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string ps = string.Join(",", e.RawPropertySets.Select(p => p.ClassName));
                    Count($"{path.Substring(path.LastIndexOf('_') + 1)} en={e.Enabled} r={e.RenderingEnabled} npc={e.IsNpc} item={e.IsItem} vis={e.Visual} ps={ps}");
                    if (shown++ < 5) Console.WriteLine($"{path} | {e.Name} | {e.Resource} | {e.X:F0},{e.Z:F0}");
                    continue;
                }
                var region = RegionOf(s.Folder);
                if (region != null && e.Visual == VisualKind.Mesh)
                {
                    centroids.TryGetValue(region, out var c);
                    centroids[region] = (c.x + e.X, c.z + e.Z, c.n + 1);
                }
                if (!isDyn) { if (e.Visual == VisualKind.Mesh) nodeKeys.Add(Key(e)); continue; }
                if (e.IsNpc && !e.RawPropertySets.Any(p => p.ClassName == "gCNPC_PS") && e.Parent <= 0) animatedProps++;
                if (DynamicObjects.IsStaticMesh(e, list))
                    dyn.Add((e, s.Folder));
            }
        }
        if (mode == "near")
        {
            // objects near <x> <z> <half>: Unity metres (= G3 cm / 100), with the 250 m streaming cell (G3WorldBatch grid).
            float x = float.Parse(args[2]), z = float.Parse(args[3]), h = args.Length > 4 ? float.Parse(args[4]) : 150;
            foreach (var (e, _) in dyn.Where(d => Math.Abs(d.e.X / 100 - x) <= h && Math.Abs(d.e.Z / 100 - z) <= h).OrderBy(d => d.e.Name))
            {
                Console.WriteLine($"{e.Name,-40} {e.X / 100,8:F1} {e.Y / 100,7:F1} {e.Z / 100,8:F1}  cell {Math.Floor((e.X / 100 + 4250) / 250)},{Math.Floor((e.Z / 100 + 4250) / 250)}");
                if (args.Contains("-v"))
                    Console.WriteLine($"    parent {e.Parent} rows {string.Join(" ", e.World.Take(12).Select(v => v.ToString("F2")))}  box {string.Join(" ", e.Bounds.Select(v => (v / 100).ToString("F2")))}");
                if (args.Contains("-v"))
                    Console.WriteLine($"    local {string.Join(" ", e.Local.Select(v => v.ToString("F2")))}");
            }
            return 0;
        }
        if (mode != "probe")
        {
            var centres = centroids.ToDictionary(k => k.Key, k => (x: k.Value.x / k.Value.n, z: k.Value.z / k.Value.n));
            string Region(string folder, WorldEntity e) => RegionOf(folder) ??
                "~" + centres.OrderBy(c => (c.Value.x - e.X) * (c.Value.x - e.X) + (c.Value.z - e.Z) * (c.Value.z - e.Z)).First().Key;
            Console.WriteLine($"animated props without gCNPC_PS (eCVisualAnimation_PS, not handled here): {animatedProps}");
            Console.WriteLine($"lrentdat static meshes: {dyn.Count}, with a .node twin (same mesh, same 1 cm position): {dyn.Count(d => nodeKeys.Contains(Key(d.e)))}");
            foreach (var (e, folder) in dyn)
            {
                Count("type " + TypeOf(e.Resource));
                Count("region " + Region(folder, e));
                Count("ps " + string.Join(",", e.RawPropertySets.Select(p => p.ClassName).Where(n => n != "eCIlluminated_PS")));
            }
        }
        foreach (var kv in hist.OrderBy(k => k.Key.Split(' ')[0]).ThenByDescending(k => k.Value)) Console.WriteLine($"{kv.Value,6} {kv.Key}");
        return 0;

        static string Key(WorldEntity e) => $"{e.Resource?.ToLowerInvariant()}|{Math.Round(e.X)}|{Math.Round(e.Y)}|{Math.Round(e.Z)}";
        // "G3_Object_Interact_Bed_01.xcmsh" -> "Interact_Bed"
        static string TypeOf(string res)
        {
            var parts = System.IO.Path.GetFileNameWithoutExtension(res ?? "?").Split('_');
            var keep = parts.Skip(parts.Length > 2 && parts[0] == "G3" ? 2 : 0).Where(p => !p.All(char.IsDigit)).Take(2);
            return string.Join("_", keep);
        }
        // Region = folder below G3_World_01 (e.g. Myrtana/Faring); null for SysDyn (then "~" + nearest region centroid).
        static string RegionOf(string folder)
        {
            var p = folder.Split('/');
            if (p.Length < 2 || p[1].StartsWith("SysDyn")) return null;
            return p.Length > 2 ? $"{p[1]}/{p[2]}" : p[1];
        }
    }

    /// <summary>
    /// `objects sectors [nameFilter]`: what the story-switchable sectors (off in .wrldatasc, or *_NPC_01 / *_NSCs_01 with a
    /// _02 twin) contain besides NPCs, by kind: items, lights, interactive objects, effects, static meshes.
    /// </summary>
    static int SectorContents(G3FileSystem fs, string vol, string filter) => SectorContentsOf(fs, vol, filter);

    /// <summary>`objects npcmesh`: entities with gCNPC_PS and a static mesh (statues that talk), by name and mesh.</summary>
    static int NpcMeshes(G3FileSystem fs, string vol)
    {
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var hist = new SortedDictionary<string, int>();
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes.Concat(s.Lrentdats))
        {
            var list = EntityArchive.Read(fs.Get(vol + path)?.Read(), decodeVegetation: false, keepRaw: true);
            if (list == null) continue;
            foreach (var e in list)
                if (e.IsNpc && e.Visual == VisualKind.Mesh)
                {
                    bool dialog = e.RawPropertySets.Any(p => p.ClassName == "gCDialog_PS");
                    bool parentNpc = e.Parent >= 0 && e.Parent < list.Count && list[e.Parent].IsNpc;
                    string k = $"{e.Name} | {e.Resource} | dialog={dialog} childOfNpc={parentNpc} en={e.Enabled}";
                    hist[k] = hist.TryGetValue(k, out var c) ? c + 1 : 1;
                }
        }
        foreach (var kv in hist) Console.WriteLine($"{kv.Value,4} {kv.Key}");
        return 0;
    }

    /// <summary>
    /// `objects swimdepth`: gCCharacterMovement_PS SwimDepth / TreatWaterAsSolid of the world's NPCs by species (gCNPC_PS
    /// Species enum) and name stem — which creatures wade where (polish-3).
    /// </summary>
    static int NpcSwimDepths(G3FileSystem fs, string vol)
    {
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read(), includeDisabled: true);
        var hist = new SortedDictionary<string, int>();
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes.Concat(s.Lrentdats))
        {
            var list = EntityArchive.Read(fs.Get(vol + path)?.Read(), decodeVegetation: false, keepRaw: true);
            if (list == null) continue;
            foreach (var e in list)
            {
                if (!e.IsNpc || e.RawPropertySets == null) continue;
                var npc = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCNPC_PS");
                var mv = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCCharacterMovement_PS");
                if (npc == null) continue;
                string species = npc.Get("Species") is var sp && sp != null ? sp.AsEnum().ToString() : "?";
                string depth = mv?.Get("SwimDepth") is var d && d != null ? d.AsFloat().ToString("F0") : "-";
                string solid = mv?.Get("TreatWaterAsSolid") is var t && t != null ? (t.AsBool() ? "solid" : "") : "-";
                string stem = (e.Name ?? "?").Split('#')[0];
                int us = stem.IndexOf('_');
                if (us > 0) stem = stem.Substring(0, us);
                string k = $"species {species,3} swim {depth,4} {solid,5} {stem}";
                hist[k] = hist.TryGetValue(k, out var c) ? c + 1 : 1;
            }
        }
        foreach (var kv in hist) Console.WriteLine($"{kv.Value,5} {kv.Key}");
        return 0;
    }

    /// <summary>
    /// `objects usetypes [type]`: interactables (gCInteraction_PS, no item/NPC/inventory) of the enabled sectors by use type,
    /// split by visual / enabled; with a type name, lists those entities (name, mesh, script use func, position in m).
    /// </summary>
    static int UseTypeCounts(G3FileSystem fs, string vol, string only)
    {
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read(), includeDisabled: true);
        var hist = new SortedDictionary<string, int[]>();
        foreach (var s in layout.Sectors)
        foreach (var path in s.Lrentdats.Concat(s.Nodes))
        {
            var list = EntityArchive.Read(fs.Get(vol + path)?.Read(), false, keepRaw: true);
            if (list == null) continue;
            var inter = new List<G3.Formats.Items.WorldInteractable>();
            var cont = new List<G3.Formats.Items.WorldContainer>();
            G3.Formats.Items.WorldItemScanner.Scan(list, null, cont, inter);
            foreach (var c in cont.Where(c => c.Kind != G3.Formats.Items.ContainerKind.NpcInventory))
                inter.Add(new G3.Formats.Items.WorldInteractable { Entity = c.Entity, Interaction = c.Interaction, Visual = "(" + c.Kind + ")" });
            foreach (var i in inter)
            {
                string k = (i.Interaction?.UseType ?? G3.Formats.Items.UseType.None).ToString() + (i.Visual != null && i.Visual.StartsWith("(") ? i.Visual : "");
                if (!hist.TryGetValue(k, out var c)) hist[k] = c = new int[4];
                bool on = i.Entity.Enabled && s.Enabled;
                c[0]++; if (i.Visual != null) c[1]++; if (on) c[2]++; if (on && i.Visual != null) c[3]++;
                if (only != null && string.Equals(k, only, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"{s.Name,-24} en={on} {i.Entity.Name,-32} {i.Visual} use={i.Interaction?.ScriptUseFunc} {i.Entity.X / 100:F0},{i.Entity.Y / 100:F0},{i.Entity.Z / 100:F0}");
            }
        }
        if (only == null)
            foreach (var kv in hist) Console.WriteLine($"{kv.Key,-16} all {kv.Value[0],5} visual {kv.Value[1],5} enabled {kv.Value[2],5} enabled+visual {kv.Value[3],5}");
        return 0;
    }

    /// <summary>
    /// `objects nocollision`: NPC entities (gCNPC_PS) without a gCCollisionShape_PS — CP 1.75 AssessActivePerception lets
    /// such an entity with an important info start the talk within 2000 cm (fix-4).
    /// </summary>
    static int NpcsWithoutCollisionShape(G3FileSystem fs, string vol)
    {
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read(), includeDisabled: true);
        int npcs = 0, without = 0;
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes.Concat(s.Lrentdats))
        {
            var list = EntityArchive.Read(fs.Get(vol + path)?.Read(), decodeVegetation: false, keepRaw: true);
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.RawPropertySets == null || !e.RawPropertySets.Any(p => p.ClassName == "gCNPC_PS")) continue;
                npcs++;
                if (e.RawPropertySets.Any(p => p.ClassName == "eCCollisionShape_PS" || p.ClassName == "gCCollisionShape_PS")) continue;
                without++;
                Console.WriteLine($"{e.Name} ({s.Name}): {string.Join(",", e.RawPropertySets.Select(p => p.ClassName))}");
            }
        }
        Console.WriteLine($"{without} of {npcs} NPC entities have no collision shape");
        return 0;
    }

    static int SectorContentsOf(G3FileSystem fs, string vol, string filter)
    {
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read(), includeDisabled: true);
        var names = new HashSet<string>(layout.Sectors.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var s in layout.Sectors.OrderBy(s => s.Name))
        {
            bool twin = s.Name.EndsWith("_01") && names.Contains(s.Name.Substring(0, s.Name.Length - 3) + "_02");
            if (s.Enabled && !twin) continue;
            if (filter != null && s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var hist = new SortedDictionary<string, int>();
            var samples = new SortedDictionary<string, string>();
            foreach (var path in s.Nodes.Concat(s.Lrentdats))
            {
                var list = EntityArchive.Read(fs.Get(vol + path)?.Read(), decodeVegetation: false, keepRaw: true);
                if (list == null) continue;
                foreach (var e in list)
                {
                    var ps = e.RawPropertySets?.Select(p => p.ClassName).ToList() ?? new List<string>();
                    string kind = e.IsNpc && ps.Contains("gCNPC_PS") ? "npc"
                        : e.IsItem ? "item"
                        : e.Light != null ? "light"
                        : ps.Contains("gCInteraction_PS") ? "interact"
                        : ps.Any(p => p.Contains("Effect") || p.Contains("Particle")) ? "effect"
                        : e.Visual == VisualKind.Mesh ? "mesh"
                        : ps.Contains("gCNavPath_PS") || ps.Contains("gCNavZone_PS") ? "nav"
                        : "other";
                    if (!e.Enabled) kind += "(off)";
                    hist[kind] = hist.TryGetValue(kind, out var c) ? c + 1 : 1;
                    if (kind != "npc" && (!samples.TryGetValue(kind, out var sm) || sm.Length < 160))
                        samples[kind] = (samples.TryGetValue(kind, out sm) ? sm + ", " : "") + (e.Name ?? "?");
                }
            }
            Console.WriteLine($"{s.Name} [{(s.Enabled ? "on" : "off")}] " + string.Join(" ", hist.Select(h => h.Key + "=" + h.Value)));
            foreach (var kv in samples) Console.WriteLine($"    {kv.Key}: {kv.Value}");
        }
        return 0;
    }
}
