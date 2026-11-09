using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.Remaster;
using G3.Formats.World;
using G3.Unity;
using V3 = System.Numerics.Vector3;

/// <summary>
/// fix-174: the relief's clearance facts (ReliefClearance): pinned open edges of the terrain union and the room each terrain
/// position has before it meets another sheet or a placed rock / building mesh. <c>relief clear --box cx cz half</c> audits a box.
/// </summary>
static partial class Program
{
    /// <summary>Placed rock / architecture meshes this big (bounds diagonal, m) are obstacles for the relief.</summary>
    const float ClearMeshMinSize = 1.5f;
    /// <summary>How far (m) the clearance looks (beyond it: no limit).</summary>
    const float ClearRange = 8f;

    /// <summary>All terrain triangles (+ placed rock / architecture meshes in <paramref name="inBox"/>) in one clearance grid.</summary>
    static ReliefClearance BuildClearance(G3FileSystem fs, List<(string name, ReliefMesh mesh)> cells, Func<float, float, bool> inBox = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var c = new ReliefClearance(4f);
        foreach (var (_, m) in cells)
            foreach (var tri in m.Sub)
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    V3 a = m.Pos[tri[i]], b = m.Pos[tri[i + 1]], d = m.Pos[tri[i + 2]];
                    if (inBox != null && !inBox(a.X, a.Z) && !inBox(b.X, b.Z) && !inBox(d.X, d.Z)) continue;
                    c.Add(a, b, d, true);
                }
        int terrainTris = c.Triangles;
        const string vol = "Projects_compiled/";
        using var meshFs = new G3FileSystem(Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData, "_compiledMesh", "_compiledMaterial");
        var cache = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        layout.Sectors.RemoveAll(s => WorldLayout.IsLowpolySector(s.Name));
        int ents = 0; var o = new float[3];
        foreach (var sec in layout.Sectors)
        foreach (var path in sec.Nodes)
        {
            List<WorldEntity> list;
            try { var entry = fs.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read()); }
            catch (Exception) { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (!e.Enabled || !e.RenderingEnabled || e.IsNpc || e.IsItem || e.Visual != VisualKind.Mesh || string.IsNullOrEmpty(e.Resource)) continue;
                if (WorldRemasterCategories.IsTerrainMesh(e.Resource)) continue;
                string cat = WorldRemasterCategories.Classify(e.Resource);
                // G3's dungeon / tunnel shells (G3_Myrtana_Dungeon_V09_S01_01: the Myrtana-Nordmar tunnel) classify as "other"
                bool shell = e.Resource.IndexOf("Dungeon", StringComparison.OrdinalIgnoreCase) >= 0 || e.Resource.IndexOf("_Mine_", StringComparison.OrdinalIgnoreCase) >= 0;
                if (cat != WorldRemasterCategories.Rock && cat != WorldRemasterCategories.Architecture && !shell) continue;
                var bb = e.Bounds;
                if (bb == null || bb[3] < bb[0]) continue;
                float sx = (bb[3] - bb[0]) / 100, sy = (bb[4] - bb[1]) / 100, sz = (bb[5] - bb[2]) / 100;
                if (Math.Sqrt(sx * sx + sy * sy + sz * sz) < ClearMeshMinSize) continue;
                float cx = (bb[0] + bb[3]) / 200, cz = (bb[2] + bb[5]) / 200;
                if (inBox != null && !inBox(cx, cz)) continue;
                var (res, invisible) = LoadAuditMesh(meshFs, e.Resource, cache);
                if (res == null || invisible) continue;
                ents++;
                foreach (var el in res.Elements)
                {
                    var wp = new V3[el.VertexCount];
                    for (int i = 0; i < el.VertexCount; i++)
                    {
                        Xf(e.World, el.Positions[i * 3], el.Positions[i * 3 + 1], el.Positions[i * 3 + 2], false, o);
                        wp[i] = new V3(o[0] * 0.01f, o[1] * 0.01f, o[2] * 0.01f);
                    }
                    for (int t = 0; t + 2 < el.Indices.Length; t += 3)
                        c.Add(wp[el.Indices[t]], wp[el.Indices[t + 1]], wp[el.Indices[t + 2]], false);
                }
            }
        }
        Console.WriteLine($"clearance grid: {terrainTris} terrain tris (distinct), {c.Triangles - terrainTris} mesh tris of {ents} rock/architecture entities, {sw.Elapsed.TotalSeconds:F1} s");
        return c;
    }

    /// <summary>Per world position: (pinned, clearance byte) for every welded terrain position of <paramref name="cells"/>.</summary>
    static Func<V3, V3, (bool pin, byte clear)> ClearanceFacts(ReliefClearance c, List<(string name, ReliefMesh mesh)> cells)
    {
        var open = c.OpenPositions();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // every distinct position once (cells overlap), in parallel
        var keys = new Dictionary<V3, V3>();
        foreach (var (_, m) in cells)
        {
            if (m.Weld == null) TerrainRelief.Weld(m);
            for (int i = 0; i < m.Pos.Length; i++)
            {
                keys.TryGetValue(m.Pos[i], out var n);
                keys[m.Pos[i]] = n + m.Nrm[i];
            }
        }
        var arr = keys.ToArray();
        var clear = new byte[arr.Length];
        Parallel.For(0, arr.Length, i =>
        {
            var n = arr[i].Value; float l = n.Length();
            n = l > 1e-6f ? n / l : V3.UnitY;
            clear[i] = ReliefClearance.Encode(c.At(arr[i].Key, n, ClearRange));
        });
        var map = new Dictionary<V3, byte>(arr.Length);
        for (int i = 0; i < arr.Length; i++) map[arr[i].Key] = clear[i];
        long limited = clear.LongCount(b => b != ReliefClearance.NoLimit);
        Console.WriteLine($"clearance: {arr.Length} positions, {open.Count} on open edges (pinned), {limited} limited (< {ClearRange} m), {sw.Elapsed.TotalSeconds:F1} s");
        return (p, _) => (open.Contains(p), map.TryGetValue(p, out var b) ? b : ReliefClearance.NoLimit);
    }

    /// <summary>
    /// <c>relief clear --box cx cz half [--old]</c>: prepares the cells in the box with and without the clearance facts and prints
    /// the moves that run into other geometry (vertex detail amplitude / edge bulge vs the clearance), worst spots first.
    /// </summary>
    static int ReliefClearAudit(string data, string[] args)
    {
        float bx = 470, bz = 790, bh = 80;
        var nearPts = new List<(float x, float z)>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--box") { bx = Fl(args[++i]); bz = Fl(args[++i]); bh = Fl(args[++i]); }
            else if (args[i] == "--near") { float x = Fl(args[++i]); nearPts.Add((x, Fl(args[++i]))); }
        }
        static float Fl(string v) => float.Parse(v, CultureInfo.InvariantCulture);
        bool InBox(float x, float z) => Math.Abs(x - bx) <= bh && Math.Abs(z - bz) <= bh;
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var s = ReliefSettings.Default;
        var raw = LoadLandscapeCells(fs);
        var cells = raw.Select(c => (c.name, ToRelief(c.mesh))).ToList();
        if (args.Contains("--materials"))
        {
            foreach (var (name, mesh) in raw)
            {
                var el = mesh.Elements.Where(e => Enumerable.Range(0, e.VertexCount).Any(i => InBox(e.Positions[i * 3] * 0.01f, e.Positions[i * 3 + 2] * 0.01f))).ToList();
                foreach (var e in el) Console.WriteLine($"{name} {Path.GetFileNameWithoutExtension(e.Material ?? "")} tris {e.Indices.Length / 3}");
            }
            return 0;
        }
        var open = ReliefOpenField(fs, cells);
        var clearance = BuildClearance(fs, cells, (x, z) => Math.Abs(x - bx) <= bh + 20 && Math.Abs(z - bz) <= bh + 20);
        var boxCells = cells.Where(c => c.Item2.Pos.Any(p => InBox(p.X, p.Z))).ToList();
        var cf = ClearanceFacts(clearance, boxCells);
        foreach (bool withClear in new[] { false, true })
        {
            var facts = ReliefWorldFacts(cells, s, open, withClear ? cf : null);
            var worst = new List<(float over, string what)>();
            long pinMoves = 0, clearHits = 0, verts = 0, outTris = 0, flipShade = 0, flipFace = 0, srcTris = 0, srcFlip = 0;
            var flips = new List<string>();
            for (int ci = 0; ci < cells.Count; ci++)
            {
                var (name, m) = cells[ci];
                if (!boxCells.Any(b => b.name == name)) continue;
                TerrainRelief.Prepare(m, s, facts[ci].facts);
                var f = facts[ci].facts;
                var bulge = new float[m.WeldCount];
                for (int e = 0; e < m.EdgeCount; e++)
                {
                    float b = 0.25f * m.EdgeTerm[e].Length();
                    bulge[m.EdgeA[e]] = Math.Max(bulge[m.EdgeA[e]], b); bulge[m.EdgeB[e]] = Math.Max(bulge[m.EdgeB[e]], b);
                }
                var cfun = cf;
                for (int w = 0; w < m.WeldCount; w++)
                {
                    var p = m.WPos[w];
                    if (!InBox(p.X, p.Z)) continue;
                    verts++;
                    float move = m.WAmp[w] + m.WSwell[w] + bulge[w];
                    var (pin, cb) = cfun(p, default);
                    if (pin && move > 0.02f) { pinMoves++; worst.Add((move, $"open-edge move {move:F2} m at ({p.X:F1},{p.Y:F1},{p.Z:F1}) {name}")); }
                    float cl = ReliefClearance.Decode(cb);
                    if (move > 0.5f * cl && move > 0.05f) { clearHits++; worst.Add((move - 0.5f * cl, $"move {move:F2} m vs clearance {cl:F2} m at ({p.X:F1},{p.Y:F1},{p.Z:F1}) {name}")); }
                }
                // folded output: triangles facing against their source triangle (a fold the camera sees from behind) or whose
                // shading normal points away from their own face (lit from the back: the dark mirror-like facets)
                foreach (var l in m.Sub)
                    for (int i = 0; i + 2 < l.Length; i += 3)
                    {
                        V3 p0 = m.Pos[l[i]], p1 = m.Pos[l[i + 1]], p2 = m.Pos[l[i + 2]];
                        var c = (p0 + p1 + p2) / 3;
                        if (!InBox(c.X, c.Z)) continue;
                        var fn = V3.Cross(p1 - p0, p2 - p0); if (fn.Length() < 1e-8f) continue;
                        srcTris++;
                        if (V3.Dot(fn, m.Nrm[l[i]] + m.Nrm[l[i + 1]] + m.Nrm[l[i + 2]]) < 0) srcFlip++;
                    }
                var outs = new List<(ReliefOutput o, bool near)>();
                var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far); outs.Add((far, false));
                // geometric folds of the far mesh: output triangles facing away from their source triangle (culled: a hole)
                for (int t = 0; t < m.TriCount; t++)
                {
                    V3 q0 = m.WPos[m.Weld[m.TriV[t * 3]]], q1 = m.WPos[m.Weld[m.TriV[t * 3 + 1]]], q2 = m.WPos[m.Weld[m.TriV[t * 3 + 2]]];
                    var qc = (q0 + q1 + q2) / 3;
                    if (!InBox(qc.X, qc.Z)) continue;
                    var sfn = V3.Cross(q1 - q0, q2 - q0); if (sfn.Length() < 1e-8f) continue;
                    var l = far.Indices[m.TriSub[t]];
                    for (int i = far.TriStart[t]; i < far.TriStart[t] + far.TriCount[t]; i += 3)
                    {
                        var a0 = far.Vertices[l[i]].Position; var a1 = far.Vertices[l[i + 1]].Position; var a2 = far.Vertices[l[i + 2]].Position;
                        if (V3.Dot(V3.Cross(a1 - a0, a2 - a0), sfn) < 0) { flipFace++; if (flips.Count < 8) flips.Add($"FOLD ({qc.X:F1},{qc.Y:F1},{qc.Z:F1})"); }
                    }
                }
                foreach (var (nx, nz) in nearPts)
                {
                    float ny = m.WPos.Where(q => Math.Abs(q.X - nx) < 3 && Math.Abs(q.Z - nz) < 3).Select(q => q.Y).DefaultIfEmpty(float.NaN).Max();
                    if (float.IsNaN(ny)) continue;
                    var no = new ReliefOutput();
                    if (TerrainRelief.BuildNear(m, new[] { new ReliefPoint(new V3(nx, ny + 2, nz), s.nearSpacing, s.nearSlope) }, 1, s, no) > 0) outs.Add((no, true));
                }
                foreach (var (o, isNear) in outs)
                    foreach (var l in o.Indices)
                        for (int i = 0; i + 2 < l.Count; i += 3)
                        {
                            var v0 = o.Vertices[l[i]]; var v1 = o.Vertices[l[i + 1]]; var v2 = o.Vertices[l[i + 2]];
                            var c = (v0.Position + v1.Position + v2.Position) / 3;
                            if (!InBox(c.X, c.Z)) continue;
                            var fn = V3.Cross(v1.Position - v0.Position, v2.Position - v0.Position);
                            float fl = fn.Length(); if (fl < 1e-8f) continue;
                            fn /= fl; outTris++;
                            var sn = V3.Normalize(v0.Normal + v1.Normal + v2.Normal);
                            if (V3.Dot(fn, sn) < 0) { flipShade++; if (flips.Count < 8) flips.Add($"shade-flip ({c.X:F1},{c.Y:F1},{c.Z:F1}) {(isNear ? "near" : "far")}"); }
                        }
            }
            Console.WriteLine($"  source tris {srcTris}: G3 normals against the face {srcFlip} ({100.0 * srcFlip / Math.Max(1, srcTris):F3} %)");
            Console.WriteLine($"  output tris in box {outTris}: shading normal against the face {flipShade} ({100.0 * flipShade / Math.Max(1, outTris):F3} %), far triangles folded against their source {flipFace}" + (flips.Count > 0 ? ": " + string.Join("; ", flips) : ""));
            {
            }
            Console.WriteLine($"{(withClear ? "WITH" : "WITHOUT")} clearance facts: {verts} positions in the box, open-edge positions that move {pinMoves}, moves > half the clearance {clearHits}");
            foreach (var w in worst.OrderByDescending(w => w.over).Take(12)) Console.WriteLine("  " + w.what);
        }
        return 0;
    }
}

static partial class Program
{
    /// <summary><c>relief ray x y z tx ty tz [radius]</c> (fix-174 probe): every terrain (LOD0) and placed-mesh triangle the ray hits, front or back.</summary>
    static int ReliefRay(string data, string[] args)
    {
        float F(int i) => float.Parse(args[i], CultureInfo.InvariantCulture);
        var o = new V3(F(2), F(3), F(4)); var t = new V3(F(5), F(6), F(7));
        var d = V3.Normalize(t - o); float len = V3.Distance(o, t) * 4;
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var hits = new List<(float t, string what)>();
        void Test(V3 a, V3 b, V3 c, string what)
        {
            var e1 = b - a; var e2 = c - a; var pv = V3.Cross(d, e2); float det = V3.Dot(e1, pv);
            if (Math.Abs(det) < 1e-12f) return;
            float inv = 1 / det; var tv = o - a; float u = V3.Dot(tv, pv) * inv; if (u < 0 || u > 1) return;
            var qv = V3.Cross(tv, e1); float v = V3.Dot(d, qv) * inv; if (v < 0 || u + v > 1) return;
            float tt = V3.Dot(e2, qv) * inv; if (tt <= 0 || tt > len) return;
            // Unity front face: cross(p1 - p0, p2 - p0) towards the viewer
            bool front = V3.Dot(V3.Cross(b - a, c - a), d) < 0;
            hits.Add((tt, what + (front ? " FRONT" : " back")));
        }
        foreach (var (name, mesh) in LoadLandscapeCells(fs))
        {
            var m = ToRelief(mesh);
            for (int s = 0; s < m.Sub.Length; s++)
                for (int i = 0; i + 2 < m.Sub[s].Length; i += 3)
                    Test(m.Pos[m.Sub[s][i]], m.Pos[m.Sub[s][i + 1]], m.Pos[m.Sub[s][i + 2]], $"{name} {Path.GetFileNameWithoutExtension(mesh.Elements[s].Material ?? "")}");
        }
        const string vol = "Projects_compiled/";
        var cache = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var ob = new float[3];
        foreach (var sec in layout.Sectors)
        foreach (var path in sec.Nodes)
        {
            List<WorldEntity> list;
            try { var entry = fs.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read()); } catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual != VisualKind.Mesh || string.IsNullOrEmpty(e.Resource) || WorldRemasterCategories.IsTerrainMesh(e.Resource)) continue;
                var bb = e.Bounds; if (bb == null) continue;
                var lo = new V3(bb[0], bb[1], bb[2]) / 100; var hi = new V3(bb[3], bb[4], bb[5]) / 100;
                // segment vs box (slab)
                float t0 = 0, t1 = len; bool miss = false;
                for (int k = 0; k < 3 && !miss; k++)
                {
                    float oo = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dd = k == 0 ? d.X : k == 1 ? d.Y : d.Z, l = k == 0 ? lo.X : k == 1 ? lo.Y : lo.Z, h = k == 0 ? hi.X : k == 1 ? hi.Y : hi.Z;
                    if (Math.Abs(dd) < 1e-9f) { if (oo < l || oo > h) miss = true; continue; }
                    float a1 = (l - oo) / dd, a2 = (h - oo) / dd; if (a1 > a2) (a1, a2) = (a2, a1);
                    t0 = Math.Max(t0, a1); t1 = Math.Min(t1, a2); if (t0 > t1) miss = true;
                }
                if (miss) continue;
                var (res, _) = LoadAuditMesh(fs, e.Resource, cache);
                if (res == null) continue;
                foreach (var el in res.Elements)
                {
                    var wp = new V3[el.VertexCount];
                    for (int i = 0; i < el.VertexCount; i++) { Xf(e.World, el.Positions[i * 3], el.Positions[i * 3 + 1], el.Positions[i * 3 + 2], false, ob); wp[i] = new V3(ob[0], ob[1], ob[2]) * 0.01f; }
                    // Unity winding of G3 meshes: (i0, i2, i1)
                    for (int i = 0; i + 2 < el.Indices.Length; i += 3)
                        Test(wp[el.Indices[i]], wp[el.Indices[i + 2]], wp[el.Indices[i + 1]], $"{Path.GetFileNameWithoutExtension(e.Resource)}{(e.Enabled ? "" : " DISABLED")}{(e.RenderingEnabled ? "" : " NORENDER")} {Path.GetFileNameWithoutExtension(el.Material ?? "")} [{sec.Name}]");
                }
            }
        }
        foreach (var h in hits.OrderBy(h => h.t)) Console.WriteLine($"{h.t,7:F2} m  {h.what}");
        return 0;
    }
}
