using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using G3.Formats.Animation;
using G3.Formats.Pak;
using G3.Formats.Remaster;
using G3.Formats.World;

static partial class Program
{
    /// <summary>
    /// Offline Source export (no Unity; Docs/remaster-pipeline.md §12) — the same glbs G3RemasterExporter writes:
    ///   remaster export &lt;actor|all&gt; [--bodies] [--heads] [--hair] [--out dir] [--pack npcs.json] [--no-textures] [--list] [--prefer a,b]
    ///       all human bodies / static heads + facial heads / hair and beard meshes (no kind flag = every kind), each from
    ///       the first spawn wearing it (--prefer: these spawns first; default PC_Hero,Trano,Roland), parts no spawn wears on
    ///       the hero's rig, + manifest.json; &lt;actor&gt; = one part stem (a head also writes its facial actor)
    ///   remaster export --spawn &lt;name&gt; [--out dir]   one world NPC as worn + &lt;name&gt;__all.glb (ExportNpc)
    ///   remaster export --hero [--out dir]            PC_Hero + talking head (ExportHero: body also as &lt;stem&gt;__talk.glb)
    ///   remaster compare &lt;a.glb&gt; &lt;b.glb&gt;             numerical equivalence of two Source glbs (exit 0 = equivalent, 2 = not)
    /// </summary>
    static int RemasterExportCommand(string data, string[] args)
    {
        if (args[1] == "compare")
        {
            if (args.Length < 4) { Console.WriteLine("remaster compare <a.glb> <b.glb>"); return 1; }
            var report = SourceCompare.Compare(Glb.Read(args[2]), Glb.Read(args[3]));
            bool same = File.ReadAllBytes(args[2]).AsSpan().SequenceEqual(File.ReadAllBytes(args[3]));
            Console.WriteLine((same ? "BYTE-IDENTICAL; " : "") + report.ToText());
            return report.Passed ? 0 : 2;
        }

        var rest = args.Skip(2).ToList();
        bool Flag(string f) { int i = rest.IndexOf(f); if (i < 0) return false; rest.RemoveAt(i); return true; }
        string Opt(string f) { int i = rest.IndexOf(f); if (i < 0 || i + 1 >= rest.Count) return null; var v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
        string outDir = Opt("--out") ?? RemasterPaths.SourceDir(Project);
        string pack = Opt("--pack") ?? Path.Combine(Project, "Assets", "StreamingAssets", "Content", "Gothic3", "npcs", "npcs.json");
        string spawnName = Opt("--spawn");
        string preferOpt = Opt("--prefer");
        bool hero = Flag("--hero"), textures = !Flag("--no-textures"), listOnly = Flag("--list");
        var kinds = new HashSet<SourceKind>();
        if (Flag("--bodies")) kinds.Add(SourceKind.Body);
        if (Flag("--heads")) { kinds.Add(SourceKind.Head); kinds.Add(SourceKind.Facial); }
        if (Flag("--hair")) { kinds.Add(SourceKind.Hair); kinds.Add(SourceKind.Beard); }
        if (kinds.Count == 0) kinds.UnionWith((SourceKind[])Enum.GetValues(typeof(SourceKind)));
        string target = rest.FirstOrDefault();
        if (spawnName == null && !hero && target == null)
        {
            Console.WriteLine("remaster export <actor|all> [--bodies] [--heads] [--hair] [--out dir] [--pack npcs.json] [--no-textures] [--list] | --spawn <name> | --hero");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var spawns = LoadAllSpawns(data);
        using var src = new PakSourceData(data);
        var builder = new SourceRigBuilder(src);
        var png = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Func<string, byte[]> images = textures ? image => png.TryGetValue(image, out var p) ? p : png[image] = SourceExport.BaseColorPng(src.Image(image)) : (Func<string, byte[]>)null;
        bool ActorExists(string stem) { try { src.Actor(stem); return true; } catch { return false; } }

        if (spawnName != null || hero)
        {
            var s = hero ? HeroSpawn(spawns) : spawns.FirstOrDefault(x => string.Equals(x.Name, spawnName, StringComparison.OrdinalIgnoreCase));
            if (s == null) { Console.WriteLine($"no spawn {spawnName}"); return 1; }
            ExportSpawnFiles(builder, s, outDir, images, null, true);
            if (hero)
            {
                // ExportHero: the talking-head rig writes the body as <stem>__talk.glb and the facial head under its own name.
                var head = s.Parts.FirstOrDefault(p => p.IsHead);
                var body = s.Parts.FirstOrDefault(p => p.IsBody);
                string facial = head?.FacialResource != null ? Path.GetFileNameWithoutExtension(head.FacialResource) : "G3_Head_Hero_Hero_Animated_01";
                if (body != null)
                {
                    var talk = SourcePlan.TalkSpawn(s, facial);
                    ExportSpawnFiles(builder, talk, outDir, images, stem => string.Equals(stem, body.Stem, StringComparison.OrdinalIgnoreCase) ? RemasterPaths.TalkStem(stem) : stem, true);
                }
            }
            Console.WriteLine($"{sw.Elapsed.TotalSeconds:0.0} s");
            return 0;
        }

        // The rigs the pipeline already used come first (PC_Hero; Trano: Myrtana_05 S29, Roland: Myrtana_03 S21 — R1 texture sets).
        var prefer = (preferOpt ?? "PC_Hero,Trano,Roland").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var jobs = SourcePlan.Make(SourcePlan.Preferred(spawns, prefer), kinds, ActorExists);
        // The hero's body: from his talking-head rig (body + facial head, 22 face nodes more) — the R2/R2b reference the
        // editor exported (Source/G3_Hero_Body_Player.glb, checked by r2run against Characters/); also as <stem>__talk.glb.
        var heroSpawn = HeroSpawn(spawns);
        string heroFacial = SourcePlan.FacialOf(heroSpawn.Parts.FirstOrDefault(p => p.IsHead) ?? new NpcPart { Resource = "G3_Head_Hero_Hero_01.xact" }, ActorExists);
        foreach (var j in jobs.Where(j => j.Kind == SourceKind.Body && j.Wearers.Count > 0 && j.Spawn.Name == heroSpawn.Name && heroFacial != null))
            j.Spawn = SourcePlan.TalkSpawn(j.Spawn, heroFacial);
        // Human parts no spawn wears (player armours, spare hair/beard variants), on the hero's rig.
        var candidates = src.FileNames.Where(f => f.EndsWith(".xact", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xcmsh", StringComparison.OrdinalIgnoreCase))
                            .Select(f => (Path.GetFileNameWithoutExtension(f), f.EndsWith(".xcmsh", StringComparison.OrdinalIgnoreCase)));
        SourcePlan.AddUnworn(jobs, candidates, HeroSpawn(spawns), kinds);
        if (!target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var one = jobs.FirstOrDefault(j => j.Stem.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (one == null) { Console.WriteLine($"{target}: no human NPC wears it (kinds {string.Join(",", kinds)})"); return 1; }
            jobs = jobs.Where(j => j == one || (one.Kind == SourceKind.Head && j.Stem.Equals(one.Partner, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        if (listOnly)
        {
            foreach (var j in jobs) Console.WriteLine($"{SourcePlan.Name(j.Kind),-6} {j.FileStem,-48} {j.Spawn.Name,-28} {j.Wearers.Count,4} spawns{(j.Wearers.Count == 0 ? " (unworn, hero rig)" : "")}");
            Console.WriteLine(string.Join(", ", jobs.GroupBy(j => j.Kind).Select(g => $"{g.Count()} {SourcePlan.Name(g.Key)}")));
            var other = spawns.Where(x => !SourcePlan.IsHuman(x)).SelectMany(x => x.Parts.Where(p => !p.IsMesh).Select(p => p.Stem))
                              .Concat(spawns.Where(x => x.Parts.Count == 0).Select(x => x.ActorStem))
                              .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("not human (skipped): " + string.Join(", ", other.Select(g => $"{g.Key} ({g.Count()})")));
            return 0;
        }

        var npcsByVisual = LoadNpcIds(pack);
        Directory.CreateDirectory(outDir);
        var entries = new List<SourceManifestEntry>();
        var rigs = new Dictionary<NpcSpawn, SourceRig>();
        int failed = 0;
        foreach (var job in jobs)
        {
            var e = new SourceManifestEntry
            {
                Actor = job.Stem, Kind = SourcePlan.Name(job.Kind), File = job.FileStem + RemasterPaths.Extension, Spawn = job.Spawn.Name,
                Partner = job.Partner, LipSync = job.Kind == SourceKind.Facial && src.HasMotion(job.Stem + "_LipSync"),
            };
            var part = job.Spawn.Parts.FirstOrDefault(p => p.Stem.Equals(job.Stem, StringComparison.OrdinalIgnoreCase));
            e.Switch = part?.Switch ?? 0;
            try
            {
                if (!rigs.TryGetValue(job.Spawn, out var rig))
                {
                    if (rigs.Count > 8) rigs.Clear();
                    rig = rigs[job.Spawn] = builder.Build(job.Spawn, job.Spawn.Name);
                }
                var mine = SourceExport.PartRenderers(rig, job.Stem, job.IsMesh);
                if (mine.Count == 0) throw new InvalidDataException("no mesh");
                var model = SourceExport.FromRig(rig, mine, images);
                // A spawn named like its body's skeleton root (Xardas, Zuben, Rhobar: node "Xardas") gives two nodes one
                // name, which the contract check cannot tell apart: then (only then) the rig root gets a suffix.
                if (!RigCheck.Compare(model, model).Passed && rig.Nodes.Skip(1).Any(n => string.Equals(n.Name, rig.Nodes[0].Name, StringComparison.OrdinalIgnoreCase)))
                {
                    rig.Nodes[0].Name += "_Spawn";
                    model = SourceExport.FromRig(rig, mine, images);
                }
                string file = Path.Combine(outDir, e.File);
                Glb.Write(model, file);
                e.Bytes = new FileInfo(file).Length;
                if (job.FileStem != job.Stem + RemasterPaths.TalkSuffix && job.Kind == SourceKind.Body && job.Spawn.Name == heroFacial)
                {
                    // The same file as <stem>__talk.glb (ExportHero writes the talking-head rig's body there; face kits prefer it).
                    string tf = Path.Combine(outDir, RemasterPaths.TalkStem(job.Stem) + RemasterPaths.Extension);
                    File.Copy(file, tf, true);
                    Console.WriteLine($"  {e.File} from the talking-head rig, also as {Path.GetFileName(tf)}");
                }
                e.Vertices = model.Meshes.Sum(m => m.VertexCount);
                e.Triangles = model.Meshes.Sum(m => m.TriangleCount);
                e.Nodes = model.Nodes.Count;
                foreach (var r in mine)
                    foreach (var m in r.Materials)
                        if (!e.Materials.Any(x => x.material == m.Name)) e.Materials.Add((m.Name, m.BaseImage));
            }
            catch (Exception ex) { e.Error = ex.Message; failed++; Console.WriteLine($"FAIL {job.FileStem}: {ex.Message}"); }

            // Texture sets over every switch the wearers use; NPC ids from the content pack.
            foreach (var w in job.Wearers)
            {
                int s = job.Kind == SourceKind.Facial ? 0 : w.Parts.FirstOrDefault(p => p.Stem.Equals(job.Stem, StringComparison.OrdinalIgnoreCase))?.Switch ?? 0;
                if (!e.Switches.Contains(s)) e.Switches.Add(s);
                if (npcsByVisual.TryGetValue(w.VisualKey, out var ids))
                    foreach (var id in ids) if (!e.Npcs.Contains(id)) e.Npcs.Add(id);
            }
            e.Spawns = job.Wearers.Count;
            if (e.Switches.Count == 0) e.Switches.Add(e.Switch);
            e.Switches.Sort();
            foreach (int s in e.Switches)
                foreach (var img in PartImages(builder, src, job, s))
                    if (!e.TextureSets.Contains(img, StringComparer.OrdinalIgnoreCase)) e.TextureSets.Add(img);
            e.Npcs.Sort(StringComparer.OrdinalIgnoreCase);
            entries.Add(e);
            if (entries.Count % 25 == 0) Console.WriteLine($"  {entries.Count}/{jobs.Count} ({sw.Elapsed.TotalSeconds:0} s)");
        }
        string manifest = Path.Combine(outDir, target.Equals("all", StringComparison.OrdinalIgnoreCase) ? "manifest.json" : $"manifest_{target}.json");
        File.WriteAllText(manifest, SourceManifest.ToJson(entries, "G3Cli remaster export"));
        long bytes = entries.Sum(x => x.Bytes);
        Console.WriteLine($"{entries.Count - failed} files ({string.Join(", ", entries.Where(x => x.Error == null).GroupBy(x => x.Kind).Select(g => $"{g.Count()} {g.Key}"))}), " +
                          $"{failed} failed, {bytes / 1048576.0:0.0} MB, {sw.Elapsed.TotalSeconds:0} s -> {outDir}; {manifest}");
        foreach (var w in src.Warnings.Distinct().Take(10)) Console.WriteLine("  " + w);
        return failed > 0 ? 2 : 0;
    }

    /// <summary>Base colour images a part's materials show under switch <paramref name="sw"/>.</summary>
    static IEnumerable<string> PartImages(SourceRigBuilder b, PakSourceData src, SourceJob job, int sw)
    {
        var mats = new List<SourceMaterial>();
        if (job.IsMesh)
        {
            var mesh = src.Mesh(job.Stem + ".xcmsh");
            if (mesh != null) mats.AddRange(mesh.Elements.Select(e => b.Materials.World(e.Material)));
        }
        else
        {
            EmfxActor act;
            try { act = src.Actor(job.Stem); } catch { yield break; }
            foreach (var m in act.Meshes.Where(m => !m.IsCollisionMesh && m.Indices.Length > 0))
                foreach (var s in m.SubMeshes) mats.Add(b.Materials.ForActor(act, s.MaterialId, job.Stem));
        }
        foreach (var m in mats)
        {
            var img = b.Materials.Variant(m, sw).BaseImage;
            if (img != null) yield return img;
        }
    }

    /// <summary>Writes every part of the spawn's rig plus &lt;name&gt;__all.glb (G3RemasterExporter.ExportSpawn).</summary>
    static void ExportSpawnFiles(SourceRigBuilder builder, NpcSpawn s, string outDir, Func<string, byte[]> images, Func<string, string> fileStem, bool all)
    {
        var rig = builder.Build(s, s.Name);
        foreach (var part in s.Parts.Count > 0 ? s.Parts : new List<NpcPart> { new NpcPart { Resource = s.Actor } })
        {
            var mine = SourceExport.PartRenderers(rig, part.Stem, part.IsMesh);
            if (mine.Count == 0) { Console.WriteLine($"{part.Stem}: no mesh"); continue; }
            var model = SourceExport.FromRig(rig, mine, images);
            string file = Path.Combine(outDir, (fileStem != null ? fileStem(part.Stem) : part.Stem) + RemasterPaths.Extension);
            Glb.Write(model, file);
            Console.WriteLine($"{file}: {SourceExport.Summary(model)}");
        }
        if (all)
        {
            var model = SourceExport.FromRig(rig, rig.Renderers(), images);
            string name = s.Name;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string file = Path.Combine(outDir, name + "__all.glb");
            Glb.Write(model, file);
            Console.WriteLine($"{file}: {SourceExport.Summary(model)}");
        }
        foreach (var l in rig.Log.Distinct().Take(10)) Console.WriteLine("  " + l);
    }

    /// <summary>G3HeroImporter.FindHeroSpawn: PC_Hero of the world, else the built-in parts.</summary>
    static NpcSpawn HeroSpawn(List<NpcSpawn> spawns)
    {
        var s = spawns.FirstOrDefault(x => string.Equals(x.Name, "PC_Hero", StringComparison.OrdinalIgnoreCase));
        if (s != null && s.Parts.Count > 0) return s;
        var f = new NpcSpawn { Name = "PC_Hero", Actor = "G3_Hero_Skeleton.FXA", Enabled = true, World = SourcePlan.Identity() };
        f.Parts.Add(new NpcPart { Resource = "G3_Head_Hero_Hero_01.FXA", EntityName = "Head_Player" });
        f.Parts.Add(new NpcPart { Resource = "G3_Hero_Body_Player.FXA", EntityName = "Body_Player" });
        return f;
    }

    /// <summary>G3NpcImporter.AllSpawns in its order: enabled spawns of the enabled sectors, then the named spawns of the switched-off sectors (test sectors excluded).</summary>
    static List<NpcSpawn> LoadAllSpawns(string data)
    {
        using var projects = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        Func<string, byte[]> read = p => projects.Get(vol + p)?.Read();
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), read);
        var list = NpcSpawns.LoadWorld(layout, read).Where(s => s.Enabled && !NpcSpawns.IsTestSector(s.Sector)).ToList();
        var off = WorldLayout.Build(projects.Files.Select(f => f.Path), read, includeDisabled: true);
        off.Sectors.RemoveAll(sec => sec.Enabled || NpcSpawns.IsTestSector(sec.Name));
        list.AddRange(NpcSpawns.LoadWorld(off, read).Where(s => !string.IsNullOrEmpty(s.Name)));
        return list;
    }

    /// <summary>Content-pack NPC ids by visual key (npcs.json "visual" = NpcSpawn.VisualKey).</summary>
    static Dictionary<string, List<string>> LoadNpcIds(string npcsJson)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(npcsJson)) { Console.WriteLine($"no content pack npcs at {npcsJson}: manifest without NPC ids"); return map; }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(npcsJson));
        foreach (var n in doc.RootElement.EnumerateArray())
        {
            if (!n.TryGetProperty("visual", out var v) || v.ValueKind != JsonValueKind.String) continue;
            string id = n.GetProperty("id").GetString();
            if (!map.TryGetValue(v.GetString(), out var ids)) map[v.GetString()] = ids = new List<string>();
            ids.Add(id);
        }
        return map;
    }
}
