using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.World;
using Myrtana.Core;
using Myrtana.Simulation;

/// <summary>
/// <c>floors [x z radius] [--pack dir] [--gap 1] [--top 60] [--all]</c> (fix-60): every routine point slot and NPC spawn of
/// the content pack (all, or within <c>radius</c> m of x z) checked against the walkable floors under it: the G3 collision
/// the cell import builds (G3CollisionBuilder: the entity's TriMesh / ConvexHull / Box shapes, the lazily cooked
/// &lt;mesh&gt;.xnvmsh or the render mesh, the landscape terrain). A slot "has no floor" when no collision triangle lies under
/// it within <c>gap</c> metres below its feet (or 0.65 m above them, the G3 step height). Such a slot puts a station NPC
/// in the air (stations are placed as authored, no ground snap) and was the cause of walkers floating (fix-60). Listed
/// worst first with the nearest floor below (gap) and above; summary per interaction.
/// </summary>
static partial class Program
{
    const float FloorStep = 0.65f;     // G3 gCCharacterMovement_PS StepHeight (m)
    const float FloorCell = 4f;        // spatial grid (m)

    sealed class FloorTri
    {
        public float X0, Y0, Z0, X1, Y1, Z1, X2, Y2, Z2;
        public string Source;
        /// <summary>One-sided as built (fix-99): +1 the Unity front face looks up, -1 down, 0 solid (convex hull, box).</summary>
        public sbyte Up;
        /// <summary>fix-254: +1 a triangle-mesh face in its stored winding (G3's front: cross(v1 - v0, v2 - v0)), -1 a render-mesh fallback face (winding unknown), 0 a solid shape (convex hull, box).</summary>
        public sbyte Side;
    }

    static int FloorCommand(string data, string[] args)
    {
        var pos = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).Select(a => float.Parse(a, CultureInfo.InvariantCulture)).ToArray();
        bool region = pos.Length >= 3;
        float cx = region ? pos[0] : 0, cz = region ? pos[1] : 0, radius = region ? pos[2] : float.MaxValue;
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        float maxGap = float.Parse(Option(args, "--gap") ?? "1", CultureInfo.InvariantCulture);
        int top = int.Parse(Option(args, "--top") ?? "60");
        bool all = args.Contains("--all");

        var db = Myrtana.Session.GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) }).Database;
        var probes = new List<(string kind, string name, string what, float x, float y, float z)>();
        foreach (var p in db.All<RoutinePointDef>())
            for (int i = 0; i < p.Slots.Count; i++)
            {
                var s = p.Slots[i];
                if (In(s.X, s.Z)) probes.Add(("point", p.Id.ToString() + (p.Slots.Count > 1 ? "#" + i : ""), p.Interaction + (p.Anchor.IsEmpty ? "" : " @" + p.Anchor), s.X, s.Y, s.Z));
            }
        foreach (var n in db.All<NpcDef>())
            if (n.Spawn is WorldPose sp && !n.Id.IsPlayer && In(sp.X, sp.Z)) probes.Add(("spawn", n.Id.Name, n.Species ?? "", sp.X, sp.Y, sp.Z));
        Console.WriteLine($"floors: {probes.Count} probes ({probes.Count(p => p.kind == "point")} routine slots, {probes.Count(p => p.kind == "spawn")} spawns)" +
                          (region ? $" within {radius} m of ({cx}, {cz})" : ""));

        var cells = new HashSet<(int, int)>(probes.Select(p => CellOf(p.x, p.z)));
        var grid = LoadFloorTriangles(data, cells, out int entities);
        Console.WriteLine($"collision: {entities} entities, {grid.Values.Sum(l => l.Count)} triangle refs in {grid.Count} cells");

        var rows = new List<(float gap, string line, string what)>();
        int ok = 0, none = 0, high = 0;
        bool winding = args.Contains("--winding");
        int fUp = 0, fDown = 0, fSolid = 0;
        var downBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in probes)
        {
            if (winding)
            {
                // fix-99: the floor triangle under the slot, as a one-sided (original winding) NavMesh source would see it
                var ft = FloorTriBelow(grid, p.x, p.z, p.y + FloorStep, maxGap + FloorStep);
                if (ft != null)
                {
                    if (ft.Up > 0) fUp++;
                    else if (ft.Up == 0) fSolid++;
                    else { fDown++; downBy[ft.Source] = downBy.TryGetValue(ft.Source, out int dn) ? dn + 1 : 1; }
                }
            }
            Column(grid, p.x, p.z, p.y + FloorStep, out float below, out string belowSrc, out float above);
            float gap = float.IsNaN(below) ? float.PositiveInfinity : p.y - below;
            if (gap <= maxGap) { ok++; continue; }
            if (float.IsInfinity(gap)) none++; else high++;
            string line = string.Format(CultureInfo.InvariantCulture, "{0,-5} {1,-48} {2,-28} ({3:F1}, {4:F2}, {5:F1})  {6}  {7}",
                p.kind, p.name, p.what, p.x, p.y, p.z,
                float.IsInfinity(gap) ? "NO FLOOR below" : $"floor {gap:F1} m below ({belowSrc})",
                float.IsNaN(above) ? "" : $"next floor {above - p.y:F1} m above");
            rows.Add((gap, line, p.what.Split(' ')[0]));
        }
        Console.WriteLine($"on a floor (gap <= {maxGap} m): {ok}; floating: {high}; no floor at all under them: {none}");
        if (winding)
        {
            Console.WriteLine($"floor winding (one-sided as built): front up {fUp}, solid (convex/box) {fSolid}, FRONT DOWN {fDown}");
            foreach (var kv in downBy.OrderByDescending(k => k.Value).Take(top)) Console.WriteLine($"  down {kv.Value,5}  {kv.Key}");
        }
        foreach (var r in rows.OrderByDescending(r => r.gap).Take(all ? int.MaxValue : top)) Console.WriteLine("  " + r.line);
        foreach (var g in rows.GroupBy(r => r.what).OrderByDescending(g => g.Count()).Take(20))
            Console.WriteLine($"  by interaction: {g.Key,-24} {g.Count()}");
        return 0;

        bool In(float x, float z) => !region || (x - cx) * (x - cx) + (z - cz) * (z - cz) <= radius * radius;
    }

    static (int, int) CellOf(float x, float z) => ((int)Math.Floor(x / FloorCell), (int)Math.Floor(z / FloorCell));

    /// <summary>Highest floor at or below <paramref name="top"/> and lowest floor above it in the column at (x, z).</summary>
    static void Column(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float top, out float below, out string belowSrc, out float above)
    {
        below = float.NaN; above = float.NaN; belowSrc = null;
        if (!grid.TryGetValue(CellOf(x, z), out var list)) return;
        foreach (var t in list)
        {
            if (!HeightOn(t, x, z, out float y)) continue;
            if (y <= top) { if (float.IsNaN(below) || y > below) { below = y; belowSrc = t.Source; } }
            else if (float.IsNaN(above) || y < above) above = y;
        }
    }

    /// <summary>The highest triangle at or below <paramref name="top"/> within <paramref name="reach"/> m in the column at (x, z).</summary>
    static FloorTri FloorTriBelow(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float top, float reach)
    {
        if (!grid.TryGetValue(CellOf(x, z), out var list)) return null;
        FloorTri best = null;
        float by = float.NegativeInfinity;
        foreach (var t in list)
            if (HeightOn(t, x, z, out float y) && y <= top && y >= top - reach && (y > by + 0.01f || (Math.Abs(y - by) <= 0.01f && t.Up > best.Up)))
            {
                best = t;
                by = y;
            }
        return best;
    }

    /// <summary>Height of the triangle's plane at (x, z) when the point lies inside its xz projection (vertical faces skipped).</summary>
    static bool HeightOn(FloorTri t, float x, float z, out float y)
    {
        y = 0;
        float d = (t.Z1 - t.Z2) * (t.X0 - t.X2) + (t.X2 - t.X1) * (t.Z0 - t.Z2);
        if (Math.Abs(d) < 1e-6f) return false;
        float a = ((t.Z1 - t.Z2) * (x - t.X2) + (t.X2 - t.X1) * (z - t.Z2)) / d;
        float b = ((t.Z2 - t.Z0) * (x - t.X2) + (t.X0 - t.X2) * (z - t.Z2)) / d;
        float c = 1 - a - b;
        const float e = -1e-4f;
        if (a < e || b < e || c < e) return false;
        y = a * t.Y0 + b * t.Y1 + c * t.Y2;
        return true;
    }

    /// <summary>
    /// World collision triangles (metres) of every entity whose bounds reach one of <paramref name="cells"/>, built as the
    /// cell import builds colliders (G3CollisionBuilder): explicit TriMesh/ConvexHull/Box shapes of player groups, else the
    /// lazily cooked .xnvmsh or the render mesh; entities without eCCollisionShape_PS have no collider.
    /// </summary>
    static Dictionary<(int, int), List<FloorTri>> LoadFloorTriangles(string data, HashSet<(int, int)> cells, out int entities, bool landscapeXnv = false, bool fullBoxes = false)
    {
        var grid = new Dictionary<(int, int), List<FloorTri>>();
        entities = 0;
        if (cells.Count == 0) return grid;
        int minCx = cells.Min(c => c.Item1), maxCx = cells.Max(c => c.Item1), minCz = cells.Min(c => c.Item2), maxCz = cells.Max(c => c.Item2);
        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
        layout.Sectors.RemoveAll(s => WorldLayout.IsLowpolySector(s.Name));
        var meshes = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var xnv = new Dictionary<string, (CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        var o0 = new float[3]; var o1 = new float[3]; var o2 = new float[3];
        int side = 0; // winding of the triangles being added: +1 raw (.xnvmsh, collider indices as stored), -1 swapped (render), 0 solid

        foreach (var sector in layout.Sectors)
        foreach (var (path, dynamic) in sector.Nodes.Select(n => (n, false)).Concat(sector.Lrentdats.Select(n => (n, true))))
        {
            List<WorldEntity> list;
            try { var entry = projects.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (dynamic ? !(DynamicObjects.IsStaticMesh(e, list) || (e.Enabled && !e.IsItem && !e.IsNpc && e.Visual == VisualKind.Mesh && CollisionRules.HasPlayerCollision(e)))
                            : !e.Enabled || e.IsNpc || e.IsItem) continue;
                if (e.Visual != VisualKind.Mesh || e.Resource == null || e.Water != null) continue;
                bool landscape = e.Resource.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!landscape && !CollisionRules.HasPlayerCollision(e)) continue;
                var b = e.Bounds;
                int bx0 = (int)Math.Floor(b[0] * 0.01f / FloorCell), bx1 = (int)Math.Floor(b[3] * 0.01f / FloorCell);
                int bz0 = (int)Math.Floor(b[2] * 0.01f / FloorCell), bz1 = (int)Math.Floor(b[5] * 0.01f / FloorCell);
                if (bx1 < minCx || bx0 > maxCx || bz1 < minCz || bz0 > maxCz) continue;
                bool any = false;
                for (int x = Math.Max(bx0, minCx); x <= Math.Min(bx1, maxCx) && !any; x++)
                for (int z = Math.Max(bz0, minCz); z <= Math.Min(bz1, maxCz) && !any; z++)
                    any = cells.Contains((x, z));
                if (!any) continue;
                entities++;
                string src = Path.GetFileNameWithoutExtension(e.Resource);
                var w = e.World;

                void AddTri(float[] p, uint i0, uint i1, uint i2, bool pre)
                {
                    Xf(w, p[i0 * 3], p[i0 * 3 + 1], p[i0 * 3 + 2], pre, o0);
                    Xf(w, p[i1 * 3], p[i1 * 3 + 1], p[i1 * 3 + 2], pre, o1);
                    Xf(w, p[i2 * 3], p[i2 * 3 + 1], p[i2 * 3 + 2], pre, o2);
                    var t = new FloorTri { X0 = o0[0] * 0.01f, Y0 = o0[1] * 0.01f, Z0 = o0[2] * 0.01f, X1 = o1[0] * 0.01f, Y1 = o1[1] * 0.01f, Z1 = o1[2] * 0.01f,
                                           X2 = o2[0] * 0.01f, Y2 = o2[1] * 0.01f, Z2 = o2[2] * 0.01f, Source = src, Side = (sbyte)side };
                    if (side != 0)
                    {
                        float ny = (t.Z1 - t.Z0) * (t.X2 - t.X0) - (t.X1 - t.X0) * (t.Z2 - t.Z0); // (v1-v0) x (v2-v0), y
                        t.Up = (sbyte)(ny * side > 0 ? 1 : -1);
                    }
                    int tx0 = (int)Math.Floor(Math.Min(t.X0, Math.Min(t.X1, t.X2)) / FloorCell), tx1 = (int)Math.Floor(Math.Max(t.X0, Math.Max(t.X1, t.X2)) / FloorCell);
                    int tz0 = (int)Math.Floor(Math.Min(t.Z0, Math.Min(t.Z1, t.Z2)) / FloorCell), tz1 = (int)Math.Floor(Math.Max(t.Z0, Math.Max(t.Z1, t.Z2)) / FloorCell);
                    for (int x = tx0; x <= tx1; x++)
                    for (int z = tz0; z <= tz1; z++)
                    {
                        if (!cells.Contains((x, z))) continue;
                        if (!grid.TryGetValue((x, z), out var l)) grid[(x, z)] = l = new List<FloorTri>();
                        l.Add(t);
                    }
                }
                void AddBox(float[] c, float[] ext, float[] axes, bool pre)
                {
                    var p = new float[24];
                    for (int k = 0; k < 8; k++)
                    {
                        float sx = (k & 1) != 0 ? ext[0] : -ext[0], sy = (k & 2) != 0 ? ext[1] : -ext[1], sz = (k & 4) != 0 ? ext[2] : -ext[2];
                        for (int a = 0; a < 3; a++) p[k * 3 + a] = c[a] + sx * axes[a] + sy * axes[3 + a] + sz * axes[6 + a];
                    }
                    uint[] q = fullBoxes // fix-254: all six faces (sight rays); else top and bottom faces (floors only)
                        ? new uint[] { 2, 3, 7, 2, 7, 6, 0, 1, 5, 0, 5, 4, 0, 2, 6, 0, 6, 4, 1, 3, 7, 1, 7, 5, 0, 1, 3, 0, 3, 2, 4, 5, 7, 4, 7, 6 }
                        : new uint[] { 2, 3, 7, 2, 7, 6, 0, 1, 5, 0, 5, 4 };
                    int was = side;
                    side = 0;
                    for (int i = 0; i < q.Length; i += 3) AddTri(p, q[i], q[i + 1], q[i + 2], pre);
                    side = was;
                }
                void AddSub(CollisionSubMesh sm, bool pre)
                {
                    side = sm.Convex ? 0 : 1;
                    if (sm.Indices != null && sm.Indices.Length >= 3)
                        for (int i = 0; i + 2 < sm.Indices.Length; i += 3) AddTri(sm.Positions, sm.Indices[i], sm.Indices[i + 1], sm.Indices[i + 2], pre);
                    else if (sm.Positions != null && sm.Positions.Length >= 3)
                    {
                        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
                        for (int i = 0; i + 2 < sm.Positions.Length; i += 3)
                            for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], sm.Positions[i + a]); mx[a] = Math.Max(mx[a], sm.Positions[i + a]); }
                        AddBox(new[] { (mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2 },
                               new[] { (mx[0] - mn[0]) / 2, (mx[1] - mn[1]) / 2, (mx[2] - mn[2]) / 2 }, new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, pre);
                    }
                }
                void AddRender()
                {
                    var (res, _) = LoadAuditMesh(meshFs, e.Resource, meshes);
                    if (res == null) return;
                    side = -1;
                    foreach (var el in res.Elements)
                        for (int i = 0; i + 2 < el.Indices.Length; i += 3) AddTri(el.Positions, el.Indices[i], el.Indices[i + 1], el.Indices[i + 2], false);
                }

                if (landscape && (e.CollisionShapes == null || e.CollisionShapes.Count == 0))
                {
                    // the builder cooks the landscape from its lazily generated .xnvmsh when there is one (fix-99: landscapeXnv)
                    var (lres, _) = landscapeXnv ? LoadAuditXnv(meshFs, Path.GetFileNameWithoutExtension(e.Resource), xnv) : default;
                    if (lres != null) foreach (var sm in lres.Meshes) { if (!sm.DisableCollision) AddSub(sm, false); }
                    else AddRender();
                    continue;
                }
                if (e.CollisionShapes.Count == 0)
                {
                    string stem = Path.GetFileNameWithoutExtension(e.Resource);
                    var (res, _) = LoadAuditXnv(meshFs, stem, xnv);
                    if (res != null) foreach (var sm in res.Meshes) { if (!sm.DisableCollision) AddSub(sm, false); }
                    else AddRender();
                    continue;
                }
                foreach (var cs in e.CollisionShapes)
                {
                    if (!CollisionRules.BlocksPlayer(cs)) continue;
                    if (cs.Type == CollisionShapeType.TriMesh || cs.Type == CollisionShapeType.ConvexHull)
                    {
                        var (res, _) = LoadAuditXnv(meshFs, cs.File, xnv);
                        if (res == null || cs.ResourceIndex >= res.Meshes.Count) continue;
                        bool pre = CollisionRules.IsPrescaled(cs.File);
                        if (cs.ResourceIndex >= 0) AddSub(res.Meshes[cs.ResourceIndex], pre);
                        else foreach (var sm in res.Meshes) AddSub(sm, pre);
                    }
                    else if (cs.Type == CollisionShapeType.Box) AddBox(cs.Center, cs.Extent, cs.Axes, false);
                }
            }
        }
        return grid;
    }
}
