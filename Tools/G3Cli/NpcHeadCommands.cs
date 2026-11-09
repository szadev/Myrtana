using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;
using G3.Formats.World;

static partial class Program
{
    /// <summary>
    /// anim npc heads [-v]: every human/orc look (visual key) of the world, switched-off story sectors included, whose
    /// head cannot be built the way G3NpcImporter builds it — no head part, head actor missing or unreadable, no
    /// renderable head mesh, head skin bones the body skeleton lacks (bound to the root: head at the feet). Warnings:
    /// facial (talking head) actor missing or unreadable. -v also lists every look checked. Exit code 1 when a look is headless.
    /// </summary>
    static int NpcHeads(string data, bool verbose)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        using var afs = new G3FileSystem(data, "_compiledAnimation");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read(), includeDisabled: true);
        layout.Sectors.RemoveAll(sec => NpcSpawns.IsTestSector(sec.Name));
        var errors = new List<string>();
        var spawns = NpcSpawns.LoadWorld(layout, p => fs.Get(vol + p)?.Read(), errors)
            .Where(s => s.Parts.Any(p => !p.IsMesh)).ToList();

        var actors = new Dictionary<string, (ActorResource res, string error)>(StringComparer.OrdinalIgnoreCase);
        (ActorResource res, string error) Actor(string resource)
        {
            string file = Path.GetFileNameWithoutExtension(resource) + ".xact";
            if (actors.TryGetValue(file, out var hit)) return hit;
            var e = afs.GetByName(file);
            if (e == null) return actors[file] = (null, $"{file} not in _compiledAnimation");
            try { return actors[file] = (Xact.Read(e.Read(), file), null); }
            catch (Exception ex) { return actors[file] = (null, $"{file} unreadable: {ex.Message}"); }
        }

        int looks = 0, headless = 0, warned = 0;
        foreach (var look in spawns.GroupBy(s => s.VisualKey).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            looks++;
            var s = look.First();
            var problems = new List<string>();
            var warnings = new List<string>();
            var heads = s.Parts.Where(p => !p.IsMesh && !p.IsBody).ToList();
            string skelName = s.Parts.FirstOrDefault(p => p.IsBody)?.Resource ?? s.Parts.First(p => !p.IsMesh).Resource;
            var skel = Actor(skelName).res?.Actor;
            if (heads.Count == 0) problems.Add("no head part (body only)");
            foreach (var h in heads)
            {
                var (res, err) = Actor(h.Resource);
                if (res == null) { problems.Add("head " + err); continue; }
                var meshes = res.Actor.Meshes.Where(m => !m.IsCollisionMesh && m.Indices.Length > 0).ToList();
                if (meshes.Count == 0) { problems.Add($"head {h.Stem}: no renderable mesh"); continue; }
                if (skel == null) continue;
                // G3CharacterImporter.AttachMeshes: a skin bone binds to the same-named skeleton bone or its nearest
                // ancestor the skeleton has; with none it falls back to the skeleton root (the head lies at the feet).
                var act = res.Actor;
                var lost = meshes.SelectMany(m => m.Skin == null ? new[] { m.Node } : m.Skin.SelectMany(i => i).Select(i => (int)i.Node))
                    .Distinct().Where(n =>
                    {
                        for (int p = n; p >= 0; p = act.Nodes[p].Parent)
                            if (skel.FindNode(act.Nodes[p].Name) >= 0) return false;
                        return true;
                    }).Select(n => act.Nodes[n].Name).ToList();
                if (lost.Count > 0) problems.Add($"head {h.Stem}: bones {string.Join(",", lost.Take(3))} not on skeleton {Path.GetFileNameWithoutExtension(skelName)} (bound to root)");
                if (h.FacialResource != null && Actor(h.FacialResource).res == null)
                    warnings.Add("talking head " + Actor(h.FacialResource).error);
            }
            string who = $"{look.Count()} spawn(s): {string.Join(", ", look.Select(x => x.Name).Distinct().Take(4))}" +
                         $" [{string.Join(",", look.Select(x => x.Sector).Distinct().Take(2))}{(look.Any(x => !x.Enabled) ? ", switched off" : "")}]";
            if (problems.Count > 0) { headless++; Console.WriteLine($"HEADLESS {look.Key}\n    {string.Join("; ", problems)}\n    {who}"); }
            if (warnings.Count > 0) { warned++; Console.WriteLine($"WARN     {look.Key}\n    {string.Join("; ", warnings)}\n    {who}"); }
            if (verbose && problems.Count == 0 && warnings.Count == 0) Console.WriteLine($"ok       {look.Key}");
        }
        Console.WriteLine($"{looks} human/orc looks ({spawns.Count} spawns): {headless} headless, {warned} with warnings, {errors.Count} load errors");
        return headless > 0 ? 1 : 0;
    }
}
