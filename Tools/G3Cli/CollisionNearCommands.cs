using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;

/// <summary>
/// <c>collision near x z [radius]</c> (fix-83): every world entity with a visual within radius m (Unity metres) of x z, with
/// its sector, enabled/rendering flags and its eCCollisionShape_PS shapes as G3CollisionBuilder reads them.
/// </summary>
static partial class Program
{
    static int CollisionNear(string data, string[] args)
    {
        float x = float.Parse(args[2], CultureInfo.InvariantCulture), z = float.Parse(args[3], CultureInfo.InvariantCulture);
        float r = args.Length > 4 ? float.Parse(args[4], CultureInfo.InvariantCulture) : 15;
        bool verbose = args.Contains("-v");
        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        var xnv = new Dictionary<string, (G3.Formats.Mesh.CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read(), includeDisabled: true);
        foreach (var sector in layout.Sectors)
        foreach (var (path, dynamic) in sector.Nodes.Select(n => (n, false)).Concat(sector.Lrentdats.Select(n => (n, true))))
        {
            List<WorldEntity> list;
            try { var entry = projects.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual == VisualKind.None && (e.Name ?? "").IndexOf("Chest", StringComparison.OrdinalIgnoreCase) < 0) continue;
                float ex = e.X / 100, ez = e.Z / 100;
                if ((ex - x) * (ex - x) + (ez - z) * (ez - z) > r * r) continue;
                string shapes = e.CollisionShapes == null ? "no collision PS" : e.CollisionShapes.Count == 0 ? "lazy" :
                    string.Join("; ", e.CollisionShapes.Select(s => $"{s.Type} g{s.Group}{(s.DisableCollision ? " off" : "")}{(s.IsTrigger ? " trig" : "")}{(s.IgnoredByTraceRay ? " ignTrace" : "")}{(s.DisableResponse ? " noResp" : "")} m{s.Material} {s.File}#{s.ResourceIndex}"));
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-36} {1,-44} ({2:F2}, {3:F2}, {4:F2}) s{5:F2} {6}{7}{8} {9} [{10}{11}]",
                    e.Name, Path.GetFileName(e.Resource ?? ""), ex, e.Y / 100, ez, Scale(e.World), e.Enabled ? "" : "DISABLED ",
                    e.RenderingEnabled ? "" : "NORENDER ", e.IsItem ? "item " : e.IsNpc ? "npc " : "", shapes,
                    sector.Name + (sector.Enabled ? "" : " (off)"), dynamic ? " lrentdat" : ""));
                if (!verbose || e.CollisionShapes == null) continue;
                var files = e.CollisionShapes.Count == 0 ? new[] { Path.GetFileNameWithoutExtension(e.Resource ?? "") } : e.CollisionShapes.Where(s => s.File != null).Select(s => s.File).Distinct().ToArray();
                foreach (var f in files)
                {
                    var (res, err) = LoadAuditXnv(meshFs, f, xnv);
                    Console.WriteLine(res == null ? $"    {f}: {err ?? "missing/obsolete"}" :
                        $"    {f}: v{res.ClassVersion} {res.Meshes.Count} sub-meshes: " + string.Join(", ", res.Meshes.Select(m =>
                            $"{(m.Convex ? "cvx" : "mesh")} {m.VertexCount}v {m.Indices?.Length / 3 ?? 0}t{(m.DisableCollision ? " off" : "")} mat{m.ShapeMaterial}")));
                }
            }
        }
        return 0;
    }
}
