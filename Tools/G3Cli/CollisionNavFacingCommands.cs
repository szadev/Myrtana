using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.World;
using G3.Player;

/// <summary>
/// <c>collision navfacing [filter] [top]</c> (fix-99): how <see cref="NavFacingRules.OneSided"/> splits the two-sided
/// TriMesh colliders of the world for the NPC NavMesh: per (render mesh, collision sub-mesh) of every placed entity, the
/// near-horizontal triangle pairs that keep the up copy (floors), the down copy (ceilings: no NavMesh on their back),
/// stay two-sided (no render surface there) and the steep ones. With a filter: the matching meshes one by one.
/// </summary>
static partial class Program
{
    static int CollisionNavFacing(string data, string[] args)
    {
        string filter = args.Length > 2 && !args[2].StartsWith("-") ? args[2] : null;
        int top = args.Length > 3 ? int.Parse(args[3]) : 30;
        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
        var meshes = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var xnv = new Dictionary<string, (CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string key, NavFacingRules.Result r)>();
        long pairs = 0, steep = 0, up = 0, down = 0, un = 0;
        foreach (var sector in layout.Sectors)
        foreach (var node in sector.Nodes.Concat(sector.Lrentdats))
        {
            List<WorldEntity> list;
            try { var entry = projects.Get(vol + node); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual != VisualKind.Mesh || e.Resource == null || e.CollisionShapes == null) continue;
                string stem = Path.GetFileNameWithoutExtension(e.Resource);
                if (filter != null && stem.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var shapes = e.CollisionShapes.Count == 0
                    ? new List<(string file, int index)> { (stem, -1) }
                    : e.CollisionShapes.Where(s => s.Type == CollisionShapeType.TriMesh && CollisionRules.BlocksPlayer(s)).Select(s => (s.File, s.ResourceIndex)).ToList();
                foreach (var (file, index) in shapes)
                {
                    string key = stem + " / " + file + "#" + index;
                    if (file == null || !done.Add(key)) continue;
                    var (res, _) = LoadAuditXnv(meshFs, file, xnv);
                    if (res == null || CollisionRules.IsPrescaled(file)) continue;
                    var (render, _) = LoadAuditMesh(meshFs, e.Resource, meshes);
                    if (render == null) continue;
                    var parts = index >= 0 && index < res.Meshes.Count ? new[] { res.Meshes[index] } : res.Meshes.Where(m => !m.DisableCollision).ToArray();
                    parts = parts.Where(m => !m.Convex && m.Indices != null).ToArray();
                    if (parts.Length == 0) continue;
                    // collider as G3CollisionBuilder.BuildMesh builds it (metres, indices as stored, two-sided)
                    var cp = new List<float>(); var ci = new List<int>();
                    foreach (var m in parts)
                    {
                        int vb = cp.Count / 3;
                        foreach (var v in m.Positions) cp.Add(v * 0.01f);
                        foreach (var i in m.Indices) ci.Add((int)i + vb);
                    }
                    var cpa = cp.ToArray();
                    var two = CollisionRules.TwoSided(cpa, ci);
                    // render as G3AssetCache builds it (metres, winding 0 2 1)
                    var rp = new List<float>(); var ri = new List<int>();
                    foreach (var el in render.Elements)
                    {
                        int vb = rp.Count / 3;
                        foreach (var v in el.Positions) rp.Add(v * 0.01f);
                        for (int t = 0; t + 2 < el.Indices.Length; t += 3) { ri.Add((int)el.Indices[t] + vb); ri.Add((int)el.Indices[t + 2] + vb); ri.Add((int)el.Indices[t + 1] + vb); }
                    }
                    var r = NavFacingRules.OneSided(cpa, two, rp.ToArray(), ri);
                    rows.Add((key, r));
                    pairs += r.Pairs; steep += r.Steep; up += r.KeptUp; down += r.KeptDown; un += r.Unmatched;
                }
            }
        }
        Console.WriteLine($"navfacing: {rows.Count} collider meshes, {pairs:N0} triangle pairs: steep {steep:N0}, floors kept up {up:N0}, " +
                          $"ceilings kept down {down:N0}, no render match (two-sided) {un:N0}");
        foreach (var (key, r) in rows.OrderByDescending(x => x.r.KeptDown).Take(top))
            Console.WriteLine($"  {key,-90} pairs {r.Pairs,6} steep {r.Steep,6} up {r.KeptUp,6} down {r.KeptDown,6} two-sided {r.Unmatched,6}");
        return 0;
    }
}
