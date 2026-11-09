using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.World;
using G3.Player;
using Myrtana.Core;
using Myrtana.Simulation;

/// <summary>
/// <c>collision caveroofs [x z radius] [--pack dir] [--list 40] [--facing]</c> (fix-128): walkable NavMesh sources and NPC places on the
/// tops of cave sheets inside the hill (<see cref="CaveFloorRules"/>). Per placed cave (G3 *_Dungeon_*) collider: its
/// walkable triangles as the NavMesh got them before fix-99 (two-sided: the top of every near-horizontal sheet), with
/// fix-99 (<see cref="NavFacingRules.OneSided"/>) and with fix-128 (CellNavMeshes drops what the rules call a roof top),
/// each judged against the collision column above it (cave ceiling / landscape). Then every NPC spawn and routine slot
/// inside a cave's bounds: true floor, roof top, or no floor (in the rock). Also the cave floor-to-ceiling heights
/// (calibrates <see cref="CaveFloorRules.MaxCaveHeight"/>).
/// </summary>
static partial class Program
{
    static int CaveRoofs(string data, string[] args)
    {
        var pos = args.Skip(2).TakeWhile(a => !a.StartsWith("--")).Select(a => float.Parse(a, CultureInfo.InvariantCulture)).ToArray();
        bool region = pos.Length >= 3;
        float rx = region ? pos[0] : 0, rz = region ? pos[1] : 0, rr = region ? pos[2] : float.MaxValue;
        int list = int.Parse(Option(args, "--list") ?? "40");
        bool facing = args.Contains("--facing"); // cave sheets' facing from fix-99's render match (the runtime cannot tell: ceiling)
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        float walkNy = MathF.Cos(SlopeRules.WalkableAngleMax * MathF.PI / 180f);

        // pass 1: the placed cave entities
        var caves = new List<(string stem, string resource, float[] w, float[] b, List<CollisionShape> shapes)>();
        using (var projects = new G3FileSystem(data, "Projects_compiled"))
        {
            const string vol = "Projects_compiled/";
            var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
            layout.Sectors.RemoveAll(s => WorldLayout.IsLowpolySector(s.Name));
            foreach (var sector in layout.Sectors)
            foreach (var node in sector.Nodes)
            {
                List<WorldEntity> l;
                try { var entry = projects.Get(vol + node); l = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
                catch { continue; }
                if (l == null) continue;
                foreach (var e in l)
                {
                    if (!e.Enabled || e.IsNpc || e.IsItem || e.Visual != VisualKind.Mesh || e.Resource == null || e.CollisionShapes == null) continue;
                    string stem = Path.GetFileNameWithoutExtension(e.Resource);
                    if (!CaveFloorRules.IsCaveName(stem) || !CollisionRules.HasPlayerCollision(e)) continue;
                    float cx = (e.Bounds[0] + e.Bounds[3]) * 0.005f, cz = (e.Bounds[2] + e.Bounds[5]) * 0.005f;
                    if (region && MathF.Sqrt((cx - rx) * (cx - rx) + (cz - rz) * (cz - rz)) > rr + 60) continue;
                    caves.Add((stem, e.Resource, e.World, e.Bounds, e.CollisionShapes));
                }
            }
        }
        var cells = new HashSet<(int, int)>();
        foreach (var c in caves)
            for (float x = c.b[0] * 0.01f - FloorCell; x <= c.b[3] * 0.01f + FloorCell; x += FloorCell)
            for (float z = c.b[2] * 0.01f - FloorCell; z <= c.b[5] * 0.01f + FloorCell; z += FloorCell)
                cells.Add(CellOf(x, z));
        var grid = LoadFloorTriangles(data, cells, out int entities, landscapeXnv: true);
        Console.WriteLine($"caveroofs: {caves.Count} placed cave meshes, collision {entities} entities in {cells.Count} cells");

        // pass 2: each cave collider's triangles in world space: the two-sided walkable tops (before fix-99) and the
        // up-facing triangles fix-99 keeps (its NavMesh side; also tells which way a sheet faces in a column)
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        var meshes = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var xnv = new Dictionary<string, (CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        var walkTris = new[] { new List<(float[] v, string src)>(), new List<(float[] v, string src)>() };
        var upGrid = new Dictionary<(int, int), List<FloorTri>>();
        int noRender = 0;
        var o = new float[3];
        foreach (var c in caves)
        {
            var shapes = c.shapes.Count == 0
                ? new List<(string file, int index)> { (c.stem, -1) }
                : c.shapes.Where(s => s.Type == CollisionShapeType.TriMesh && CollisionRules.BlocksPlayer(s)).Select(s => (s.File, s.ResourceIndex)).ToList();
            foreach (var (file, index) in shapes)
            {
                var (res, _) = LoadAuditXnv(meshFs, file, xnv);
                if (res == null || CollisionRules.IsPrescaled(file)) continue;
                var parts = index >= 0 && index < res.Meshes.Count ? new[] { res.Meshes[index] } : res.Meshes.Where(m => !m.DisableCollision).ToArray();
                parts = parts.Where(m => !m.Convex && m.Indices != null).ToArray();
                if (parts.Length == 0) continue;
                var cp = new List<float>(); var ci = new List<int>();
                foreach (var m in parts)
                {
                    int vb = cp.Count / 3;
                    foreach (var v in m.Positions) cp.Add(v * 0.01f);
                    foreach (var i in m.Indices) ci.Add((int)i + vb);
                }
                var cpa = cp.ToArray();
                var two = CollisionRules.TwoSided(cpa, ci);
                var (render, _) = LoadAuditMesh(meshFs, c.resource, meshes);
                int[] one = null;
                if (render != null)
                {
                    var rp = new List<float>(); var ri = new List<int>();
                    foreach (var el in render.Elements)
                    {
                        int vb = rp.Count / 3;
                        foreach (var v in el.Positions) rp.Add(v * 0.01f);
                        for (int t = 0; t + 2 < el.Indices.Length; t += 3) { ri.Add((int)el.Indices[t] + vb); ri.Add((int)el.Indices[t + 2] + vb); ri.Add((int)el.Indices[t + 1] + vb); }
                    }
                    var r = NavFacingRules.OneSided(cpa, two, rp.ToArray(), ri);
                    if (r.KeptUp + r.KeptDown + r.Steep > 0) one = r.Triangles;
                }
                if (one == null) noRender++;
                for (int pass = 0; pass < 2; pass++)
                {
                    var tris = pass == 0 ? two : one ?? two;
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        var wv = new float[9];
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = tris[t + k];
                            Xf(c.w, cpa[vi * 3] * 100f, cpa[vi * 3 + 1] * 100f, cpa[vi * 3 + 2] * 100f, false, o);
                            wv[k * 3] = o[0] * 0.01f; wv[k * 3 + 1] = o[1] * 0.01f; wv[k * 3 + 2] = o[2] * 0.01f;
                        }
                        // G3CollisionBuilder keeps the stored winding (+ its reversed copy); Unity front face = cross(v1-v0, v2-v0)
                        float ny = NormalY(wv, out _);
                        if (pass == 1 && ny > 0.3f) AddToGrid(upGrid, wv, c.stem);
                        if (ny < walkNy) continue;
                        float cx = (wv[0] + wv[3] + wv[6]) / 3, cz = (wv[2] + wv[5] + wv[8]) / 3;
                        if (region && MathF.Sqrt((cx - rx) * (cx - rx) + (cz - rz) * (cz - rz)) > rr) continue;
                        walkTris[pass].Add((wv, c.stem));
                    }
                }
            }
        }

        // which way a column surface faces: cave sheets by fix-99's kept up copy, the landscape by its order from the top
        // (the hill's top faces up, an overhang's underside below it down, ...)
        var buf = new List<CaveFloorRules.Above>();
        List<CaveFloorRules.Above> AboveOf(float x, float y, float z)
        {
            buf.Clear();
            var col = Surfaces(grid, x, z);
            var landUp = new bool[col.Count];
            int landSeen = 0;
            for (int s = 0; s < col.Count; s++)
                if (CaveFloorRules.KindOf(col[s].src) == CaveFloorRules.Kind.Landscape) landUp[s] = landSeen++ % 2 == 0;
            for (int s = col.Count - 1; s >= 0; s--)
            {
                if (col[s].y <= y + CaveFloorRules.SameSheet) continue;
                var kind = CaveFloorRules.KindOf(col[s].src);
                bool up = kind == CaveFloorRules.Kind.Landscape ? landUp[s] : facing && kind == CaveFloorRules.Kind.Cave && FacesUpAt(upGrid, x, col[s].y, z);
                buf.Add(new CaveFloorRules.Above(col[s].y - y, kind, up));
            }
            return buf;
        }
        string Desc(List<CaveFloorRules.Above> a) =>
            string.Join(", ", a.Take(3).Select(v => v.Dy.ToString("F1", CultureInfo.InvariantCulture) + " " + v.Kind + (v.FacesUp ? " up" : " down")));

        long[] roof = new long[2], cramped = new long[2];
        double[] roofArea = new double[2];
        var heights = new SortedDictionary<int, int>();
        var roofBySrc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<string>();
        for (int pass = 0; pass < 2; pass++)
            foreach (var (wv, src) in walkTris[pass])
            {
                float cx = (wv[0] + wv[3] + wv[6]) / 3, cy = (wv[1] + wv[4] + wv[7]) / 3, cz = (wv[2] + wv[5] + wv[8]) / 3;
                var above = AboveOf(cx, cy, cz);
                // Recast keeps no NavMesh where a body does not fit under the next surface (agent height 1.8 m)
                if (above.Count > 0 && above[0].Dy < 1.8f) { cramped[pass]++; continue; }
                var st = CaveFloorRules.Classify(0f, CaveFloorRules.Kind.Cave, true, above);
                if (pass == 1 && st == CaveFloorRules.Stand.TrueFloor)
                {
                    var first = above.FirstOrDefault(a => a.Kind != CaveFloorRules.Kind.Other);
                    if (first.Kind != CaveFloorRules.Kind.Other)
                    {
                        int bin = Math.Min(60, (int)MathF.Floor(first.Dy / 4f) * 4);
                        heights[bin] = heights.TryGetValue(bin, out int hc) ? hc + 1 : 1;
                    }
                }
                if (st != CaveFloorRules.Stand.RoofTop) continue;
                roof[pass]++;
                NormalY(wv, out float area);
                roofArea[pass] += area;
                if (pass == 1)
                {
                    roofBySrc[src] = roofBySrc.TryGetValue(src, out int rc) ? rc + 1 : 1;
                    if (samples.Count < list)
                        samples.Add(string.Format(CultureInfo.InvariantCulture, "  roof top ({0:F1}, {1:F2}, {2:F1}) {3}: above {4}", cx, cy, cz, src, Desc(above)));
                }
            }
        Console.WriteLine($"walkable cave triangles (NavMesh sources): before fix-99 (two-sided) {walkTris[0].Count:N0}, on roof/wall tops {roof[0]:N0} ({roofArea[0]:N0} m2), no headroom {cramped[0]:N0}");
        Console.WriteLine($"                                           fix-99 (one-sided)  {walkTris[1].Count:N0}, on roof/wall tops {roof[1]:N0} ({roofArea[1]:N0} m2), no headroom {cramped[1]:N0}; {noRender} colliders without a render match");
        Console.WriteLine("cave floor -> first ceiling (m, fix-99 walkable true floors): " + string.Join("  ", heights.Select(h => $"{h.Key}+:{h.Value}")));
        foreach (var kv in roofBySrc.OrderByDescending(k => k.Value).Take(15)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        foreach (var s in samples) Console.WriteLine(s);

        // NPC places inside cave bounds
        var db = Myrtana.Session.GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) }).Database;
        var probes = new List<(string kind, string name, float x, float y, float z)>();
        foreach (var p in db.All<RoutinePointDef>())
            for (int i = 0; i < p.Slots.Count; i++) probes.Add(("point", p.Id + (p.Slots.Count > 1 ? "#" + i : ""), p.Slots[i].X, p.Slots[i].Y, p.Slots[i].Z));
        foreach (var n in db.All<NpcDef>())
            if (n.Spawn is WorldPose sp && !n.Id.IsPlayer) probes.Add(("spawn", n.Id.Name, sp.X, sp.Y, sp.Z));
        var counts = new Dictionary<(string, CaveFloorRules.Stand), int>();
        int inCave = 0, shown = 0;
        foreach (var p in probes)
        {
            if (region && MathF.Sqrt((p.x - rx) * (p.x - rx) + (p.z - rz) * (p.z - rz)) > rr) continue;
            bool inside = caves.Any(c => p.x >= c.b[0] * 0.01f - 2 && p.x <= c.b[3] * 0.01f + 2 && p.z >= c.b[2] * 0.01f - 2 && p.z <= c.b[5] * 0.01f + 2 &&
                                         p.y >= c.b[1] * 0.01f - 2 && p.y <= c.b[4] * 0.01f + 2);
            if (!inside) continue;
            inCave++;
            var col = Surfaces(grid, p.x, p.z);
            var floor = col.FirstOrDefault(s => s.y <= p.y + FloorStep);
            float? drop = floor.src != null ? p.y - floor.y : (float?)null;
            var above = floor.src != null ? AboveOf(p.x, floor.y, p.z) : new List<CaveFloorRules.Above>();
            var st = CaveFloorRules.Classify(drop, CaveFloorRules.KindOf(floor.src), true, above);
            counts[(p.kind, st)] = counts.TryGetValue((p.kind, st), out int cc) ? cc + 1 : 1;
            if (!CaveFloorRules.MayStand(st) && shown++ < list)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0} {1} {2} at ({3:F2}, {4:F2}, {5:F2}): floor {6} {7}, above {8}", st, p.kind, p.name,
                    p.x, p.y, p.z, floor.src == null ? "none" : floor.y.ToString("F2", CultureInfo.InvariantCulture), floor.src ?? "", Desc(above)));
        }
        Console.WriteLine($"NPC places inside cave bounds: {inCave} of {probes.Count}");
        foreach (var kv in counts.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2)) Console.WriteLine($"  {kv.Key.Item1,-6} {kv.Key.Item2,-10} {kv.Value}");
        return 0;
    }

    /// <summary>The unit normal's y of a world triangle (x y z * 3; Unity front face = cross(v1-v0, v2-v0)) and its area.</summary>
    static float NormalY(float[] v, out float area)
    {
        float ux = v[3] - v[0], uy = v[4] - v[1], uz = v[5] - v[2], vx = v[6] - v[0], vy = v[7] - v[1], vz = v[8] - v[2];
        float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        area = len * 0.5f;
        return len < 1e-8f ? 0f : ny / len;
    }

    static void AddToGrid(Dictionary<(int, int), List<FloorTri>> g, float[] v, string src)
    {
        var t = new FloorTri { X0 = v[0], Y0 = v[1], Z0 = v[2], X1 = v[3], Y1 = v[4], Z1 = v[5], X2 = v[6], Y2 = v[7], Z2 = v[8], Source = src, Up = 1 };
        int x0 = (int)Math.Floor(Math.Min(v[0], Math.Min(v[3], v[6])) / FloorCell), x1 = (int)Math.Floor(Math.Max(v[0], Math.Max(v[3], v[6])) / FloorCell);
        int z0 = (int)Math.Floor(Math.Min(v[2], Math.Min(v[5], v[8])) / FloorCell), z1 = (int)Math.Floor(Math.Max(v[2], Math.Max(v[5], v[8])) / FloorCell);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            if (!g.TryGetValue((x, z), out var l)) g[(x, z)] = l = new List<FloorTri>();
            l.Add(t);
        }
    }

    /// <summary>A fix-99 up-facing cave triangle passes through (x, y, z): the sheet there faces up (a floor).</summary>
    static bool FacesUpAt(Dictionary<(int, int), List<FloorTri>> g, float x, float y, float z)
    {
        if (!g.TryGetValue(CellOf(x, z), out var l)) return false;
        foreach (var t in l)
            if (HeightOn(t, x, z, out float h) && Math.Abs(h - y) < 0.06f) return true;
        return false;
    }
}
