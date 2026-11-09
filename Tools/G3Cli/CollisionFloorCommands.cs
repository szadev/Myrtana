using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.World;

/// <summary>
/// <c>collision interiors [top] [--manifest path] [--all]</c> (fix-83): interior floors of every architecture object
/// (<see cref="Program.IsArchitecture"/>) that collides with the player in G3, checked against the collision the cell
/// import builds for it (G3CollisionBuilder rules: player-group TriMesh / ConvexHull / Box shapes, the lazily cooked
/// &lt;mesh&gt;.xnvmsh or the render mesh). A floor sample is the centroid of a horizontal render triangle (|n.y| ≥ 0.8,
/// ≥ 0.05 m²) that has more of the building's render mesh above it within 8 m (inside, under a ceiling or roof); it is
/// covered when a collision surface of the same object lies within −0.4…+0.65 m of it; uncovered samples are then checked
/// against all world collision (terrain, other objects): open only when nothing lies within −0.4…+1.8 m (no floor and
/// head room for the hero, and not below the terrain: buried foundation faces and gaps inside walls do not count). Objects whose uncovered floor
/// area exceeds 25 % (or which would get no collider at all: water-surface rule, unreadable shapes) are listed per mesh and
/// counted per region. <c>--old-water</c> applies the builder's pre-fix-83 water rule (before/after counts). Exit code 1 when any is found.
/// </summary>
static partial class Program
{
    static int CollisionInteriors(string data, string[] args)
    {
        var plain = args.Skip(2).TakeWhile(a => !a.StartsWith("--")).ToArray();
        int top = plain.Length > 0 ? int.Parse(plain[0]) : 40;
        bool all = args.Contains("--all");
        string holesOf = Option(args, "--holes"); // print the open holes of the first instance of this mesh
        bool oldWater = args.Contains("--old-water"); // the pre-fix-83 builder rule: any material named "*water*" → no collider
        string manifestPath = Option(args, "--manifest") ?? @"Assets\Scenes\World\G3WorldManifest.asset";
        var regions = ReadManifestRegions(manifestPath, out float ox, out float oz, out float cs);

        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
        layout.Sectors.RemoveAll(s => WorldLayout.IsLowpolySector(s.Name));
        var meshes = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var xnv = new Dictionary<string, (CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        var mats = new Dictionary<string, G3.Formats.Material.MaterialResource>(StringComparer.OrdinalIgnoreCase);

        var bad = new Dictionary<string, (int n, float worst, string at)>(StringComparer.OrdinalIgnoreCase);
        var drops = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase); // per mesh: drop to the next floor below an open hole (m)
        var perRegion = new SortedDictionary<string, int[]>(StringComparer.OrdinalIgnoreCase); // [checked, with interior floor, bad]
        int checkedN = 0, withFloor = 0, badN = 0, ownBad = 0, samplesAll = 0, samplesBad = 0;
        var suspects = new List<(string stem, string region, float x, float y, float z, float area, bool noCollider, List<(float x, float y, float z, float a)> holes)>();

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
                if (e.Visual != VisualKind.Mesh || e.Resource == null || e.IsWater || !e.RenderingEnabled) continue;
                string stem = Path.GetFileNameWithoutExtension(e.Resource);
                if (!IsArchitecture(stem) || !CollisionRules.HasPlayerCollision(e)) continue;
                var (res, invisible) = LoadAuditMesh(meshFs, e.Resource, meshes);
                if (res == null || invisible) continue;
                checkedN++;
                string region = RegionAt(regions, ox, oz, cs, e.X / 100, e.Z / 100);
                if (!perRegion.TryGetValue(region, out var rc)) perRegion[region] = rc = new int[3];
                rc[0]++;

                // Render triangles (world m); water surfaces dropped as the builder does.
                var render = new List<FloorTri>();
                var drawn = new List<(string, string)>();
                foreach (var el in res.Elements)
                {
                    string ms = Path.GetFileNameWithoutExtension(el.Material ?? "");
                    if (!mats.TryGetValue(ms, out var m))
                    {
                        try { var me = meshFs.GetByName(ms + ".xshmat"); m = me != null ? G3.Formats.Material.Xshmat.Read(me.Read(), ms) : null; } catch { m = null; }
                        mats[ms] = m;
                    }
                    if (m != null && (m.Blend == G3.Formats.Material.BlendMode.Invisible || ms.IndexOf("_Depth_", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    bool water = m != null && (m.ShaderClass == "eCShaderRiver" || m.ShaderClass == "eCShaderOcean" || m.ShaderClass == "eCShaderWater");
                    drawn.Add((ms, water ? "G3/Water" : "G3/Default"));
                    if (CollisionRules.IsWaterMaterial(ms, water ? "G3/Water" : null)) continue;
                    for (int i = 0; i + 2 < el.Indices.Length; i += 3) render.Add(WorldTri(e.World, el.Positions, el.Indices[i], el.Indices[i + 1], el.Indices[i + 2], false, stem));
                }
                bool noCollider = oldWater ? drawn.Any(d => d.Item2 == "G3/Water" || d.Item1.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0)
                                           : CollisionRules.IsWaterSurface(drawn);
                bool lost = false;
                var col = noCollider ? new List<FloorTri>() : EntityCollisionTris(e, meshFs, xnv, meshes, out lost);
                if (lost && col.Count == 0) noCollider = true;

                // Interior floor samples.
                var rgrid = Grid(render);
                var cgrid = Grid(col);
                float area = 0, uncovered = 0; int samples = 0, miss = 0;
                var holes = new List<(float x, float y, float z, float a)>();
                foreach (var t in render)
                {
                    float ux = t.X1 - t.X0, uy = t.Y1 - t.Y0, uz = t.Z1 - t.Z0, vx = t.X2 - t.X0, vy = t.Y2 - t.Y0, vz = t.Z2 - t.Z0;
                    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len < 1e-6f) continue;
                    float a = len / 2;
                    if (a < 0.05f || Math.Abs(ny / len) < 0.8f) continue;
                    float cx = (t.X0 + t.X1 + t.X2) / 3, cy = (t.Y0 + t.Y1 + t.Y2) / 3, cz = (t.Z0 + t.Z1 + t.Z2) / 3;
                    if (!AnyBetween(rgrid, cx, cz, cy + 0.5f, cy + 8f)) continue; // nothing above: outside / roof top
                    samples++; area += a;
                    if (!AnyBetween(cgrid, cx, cz, cy - 0.4f, cy + 0.65f)) { miss++; uncovered += a; holes.Add((cx, cy, cz, a)); }
                }
                if (samples < 3) continue;
                withFloor++; rc[1]++;
                samplesAll += samples; samplesBad += miss;
                float share = area > 0 ? uncovered / area : 0;
                if (!noCollider && share <= 0.25f) continue;
                ownBad++;
                suspects.Add((stem, region, e.X / 100, e.Y / 100, e.Z / 100, area, noCollider, holes));
            }
        }

        // Second pass: a floor the object's own collider misses may still be walkable on the terrain or on another
        // object's collider (G3 often leaves ground floors to the landscape). Only holes with nothing there count.
        var cells = new HashSet<(int, int)>(suspects.SelectMany(s => s.holes).Select(h => CellOf(h.x, h.z)));
        var world = LoadFloorTriangles(data, cells, out _);
        foreach (var s in suspects)
        {
            float open = 0;
            var drop = new List<float>();
            foreach (var h in s.holes)
            {
                bool any = false;
                float below = float.NegativeInfinity;
                if (world.TryGetValue(CellOf(h.x, h.z), out var l))
                    foreach (var t in l)
                    {
                        if (!HeightOn(t, h.x, h.z, out float y)) continue;
                        // Floor there, or no head room (a collider ≤ 1.8 m above: buried foundation faces, gaps inside walls).
                        if (y >= h.y - 0.4f && y <= h.y + 1.8f) { any = true; break; }
                        if (y > h.y && t.Source != null && t.Source.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase) >= 0) { any = true; break; } // under the terrain
                        if (y < h.y && y > below) below = y;
                    }
                if (!any) { open += h.a; drop.Add(float.IsInfinity(below) ? 99 : h.y - below); }
                if (!any && holesOf != null && s.stem.Equals(holesOf, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  hole ({0:F2}, {1:F2}, {2:F2}) {3:F2} m² local ({4:F2}, {5:F2}, {6:F2}) floor below {7:F2}",
                        h.x, h.y, h.z, h.a, h.x - s.x, h.y - s.y, h.z - s.z, below));
            }
            float share = s.area > 0 ? open / s.area : 0;
            if (holesOf != null && s.stem.Equals(holesOf, StringComparison.OrdinalIgnoreCase)) holesOf = null;
            if (!s.noCollider && share <= 0.25f) continue;
            badN++; perRegion[s.region][2]++;
            string key = s.stem + (s.noCollider ? " (no collider)" : "");
            bad.TryGetValue(key, out var v);
            bad[key] = (v.n + 1, Math.Max(v.worst, s.noCollider ? 1 : share),
                v.at ?? string.Format(CultureInfo.InvariantCulture, "{0:F1} {1:F1} {2:F1}", s.x, s.y, s.z));
            if (!drops.TryGetValue(key, out var dl)) drops[key] = dl = new List<float>();
            dl.AddRange(drop);
        }
        Console.WriteLine($"architecture objects with player collision in G3: {checkedN:N0}, with interior floors: {withFloor:N0}, " +
                          $"floor samples {samplesAll:N0} (not on the own collider {samplesBad:N0}); own collider misses > 25 % of the floor: {ownBad:N0}; " +
                          $"BAD (> 25 % with no terrain / other collider either, or no collider at all): {badN:N0}");
        foreach (var kv in perRegion) Console.WriteLine($"  {kv.Key,-12} checked {kv.Value[0],5}  interior floors {kv.Value[1],5}  BAD {kv.Value[2]}");
        Console.WriteLine($"== interior floor not covered by the object's collider: {bad.Values.Sum(v => v.n)} objects, {bad.Count} meshes");
        foreach (var kv in bad.OrderByDescending(k => k.Value.n).Take(all ? int.MaxValue : top))
        {
            var d = drops.TryGetValue(kv.Key, out var dl) && dl.Count > 0 ? dl.OrderBy(x => x).ToList() : null;
            string ds = d == null ? "" : string.Format(CultureInfo.InvariantCulture, "  drop to the floor below: median {0:F2} m, 90% {1:F2} m", d[d.Count / 2], d[(int)(d.Count * 0.9)]);
            Console.WriteLine($"  {kv.Value.n,5}  {kv.Key}  worst {kv.Value.worst:P0} uncovered  @ {kv.Value.at}{ds}");
        }
        return badN > 0 ? 1 : 0;
    }

    static FloorTri WorldTri(float[] w, float[] p, uint i0, uint i1, uint i2, bool pre, string src)
    {
        var o0 = new float[3]; var o1 = new float[3]; var o2 = new float[3];
        Xf(w, p[i0 * 3], p[i0 * 3 + 1], p[i0 * 3 + 2], pre, o0);
        Xf(w, p[i1 * 3], p[i1 * 3 + 1], p[i1 * 3 + 2], pre, o1);
        Xf(w, p[i2 * 3], p[i2 * 3 + 1], p[i2 * 3 + 2], pre, o2);
        return new FloorTri { X0 = o0[0] * 0.01f, Y0 = o0[1] * 0.01f, Z0 = o0[2] * 0.01f, X1 = o1[0] * 0.01f, Y1 = o1[1] * 0.01f, Z1 = o1[2] * 0.01f,
                              X2 = o2[0] * 0.01f, Y2 = o2[1] * 0.01f, Z2 = o2[2] * 0.01f, Source = src };
    }

    const float InteriorCell = 1f; // m

    static Dictionary<(int, int), List<FloorTri>> Grid(List<FloorTri> tris)
    {
        var g = new Dictionary<(int, int), List<FloorTri>>();
        foreach (var t in tris)
        {
            int x0 = (int)Math.Floor(Math.Min(t.X0, Math.Min(t.X1, t.X2)) / InteriorCell), x1 = (int)Math.Floor(Math.Max(t.X0, Math.Max(t.X1, t.X2)) / InteriorCell);
            int z0 = (int)Math.Floor(Math.Min(t.Z0, Math.Min(t.Z1, t.Z2)) / InteriorCell), z1 = (int)Math.Floor(Math.Max(t.Z0, Math.Max(t.Z1, t.Z2)) / InteriorCell);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 40000) continue;
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (!g.TryGetValue((x, z), out var l)) g[(x, z)] = l = new List<FloorTri>();
                l.Add(t);
            }
        }
        return g;
    }

    /// <summary>Some triangle of the grid has its surface at (x, z) with lo ≤ y ≤ hi.</summary>
    static bool AnyBetween(Dictionary<(int, int), List<FloorTri>> g, float x, float z, float lo, float hi)
    {
        if (!g.TryGetValue(((int)Math.Floor(x / InteriorCell), (int)Math.Floor(z / InteriorCell)), out var l)) return false;
        foreach (var t in l)
            if (HeightOn(t, x, z, out float y) && y >= lo && y <= hi) return true;
        return false;
    }

    /// <summary>
    /// The entity's collision triangles (world m) as G3CollisionBuilder builds them; <paramref name="lost"/> when a
    /// referenced shape could not be read.
    /// </summary>
    static List<FloorTri> EntityCollisionTris(WorldEntity e, G3FileSystem meshFs, Dictionary<string, (CollisionMeshResource, string)> xnv,
                                              Dictionary<string, (MeshResource, bool)> meshes, out bool lost)
    {
        var o = new List<FloorTri>();
        bool lostAny = false;
        string src = Path.GetFileNameWithoutExtension(e.Resource ?? "");
        void Sub(CollisionSubMesh sm, bool pre)
        {
            if (sm.Indices != null && sm.Indices.Length >= 3)
                for (int i = 0; i + 2 < sm.Indices.Length; i += 3) o.Add(WorldTri(e.World, sm.Positions, sm.Indices[i], sm.Indices[i + 1], sm.Indices[i + 2], pre, src));
            else if (sm.Positions != null && sm.Positions.Length >= 3)
            {
                float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i + 2 < sm.Positions.Length; i += 3)
                    for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], sm.Positions[i + a]); mx[a] = Math.Max(mx[a], sm.Positions[i + a]); }
                Box(new[] { (mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2 },
                    new[] { (mx[0] - mn[0]) / 2, (mx[1] - mn[1]) / 2, (mx[2] - mn[2]) / 2 }, new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, pre);
            }
        }
        void Box(float[] c, float[] ext, float[] axes, bool pre)
        {
            var p = new float[24];
            for (int k = 0; k < 8; k++)
            {
                float sx = (k & 1) != 0 ? ext[0] : -ext[0], sy = (k & 2) != 0 ? ext[1] : -ext[1], sz = (k & 4) != 0 ? ext[2] : -ext[2];
                for (int a = 0; a < 3; a++) p[k * 3 + a] = c[a] + sx * axes[a] + sy * axes[3 + a] + sz * axes[6 + a];
            }
            uint[] q = { 2, 3, 7, 2, 7, 6, 0, 1, 5, 0, 5, 4 }; // top and bottom faces
            for (int i = 0; i < q.Length; i += 3) o.Add(WorldTri(e.World, p, q[i], q[i + 1], q[i + 2], pre, src));
        }
        void Render()
        {
            var (res, _) = LoadAuditMesh(meshFs, e.Resource, meshes);
            if (res == null) { lostAny = true; return; }
            foreach (var el in res.Elements)
                for (int i = 0; i + 2 < el.Indices.Length; i += 3) o.Add(WorldTri(e.World, el.Positions, el.Indices[i], el.Indices[i + 1], el.Indices[i + 2], false, src));
        }

        if (e.CollisionShapes.Count == 0)
        {
            var (res, _) = LoadAuditXnv(meshFs, src, xnv);
            if (res != null) foreach (var sm in res.Meshes) { if (!sm.DisableCollision) Sub(sm, false); }
            else Render();
        }
        else
            foreach (var shape in e.CollisionShapes)
            {
                if (!CollisionRules.BlocksPlayer(shape)) continue;
                if (shape.Type == CollisionShapeType.TriMesh || shape.Type == CollisionShapeType.ConvexHull)
                {
                    var (res, _) = LoadAuditXnv(meshFs, shape.File, xnv);
                    if (res == null || shape.ResourceIndex >= res.Meshes.Count) { lostAny = true; continue; }
                    bool pre = CollisionRules.IsPrescaled(shape.File);
                    if (shape.ResourceIndex >= 0) Sub(res.Meshes[shape.ResourceIndex], pre);
                    else foreach (var sm in res.Meshes) Sub(sm, pre);
                }
                else if (shape.Type == CollisionShapeType.Box) Box(shape.Center, shape.Extent, shape.Axes, false);
            }
        lost = lostAny;
        return o;
    }

    static Dictionary<(int, int), string> ReadManifestRegions(string path, out float ox, out float oz, out float cs)
    {
        ox = oz = -4250; cs = 250;
        var d = new Dictionary<(int, int), string>();
        if (!File.Exists(path)) return d;
        int ix = 0, iz = 0;
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.StartsWith("origin: ")) { var v = new float[2]; Vec(t.Substring(8), v); ox = v[0]; oz = v[1]; }
            else if (t.StartsWith("cellSize: ")) cs = float.Parse(t.Substring(10), CultureInfo.InvariantCulture);
            else if (t.StartsWith("- ix: ")) ix = int.Parse(t.Substring(6));
            else if (t.StartsWith("iz: ")) iz = int.Parse(t.Substring(4));
            else if (t.StartsWith("region: ")) d[(ix, iz)] = t.Substring(8);
        }
        return d;
    }

    static string RegionAt(Dictionary<(int, int), string> regions, float ox, float oz, float cs, float x, float z) =>
        regions.TryGetValue(((int)Math.Floor((x - ox) / cs), (int)Math.Floor((z - oz) / cs)), out var r) ? r : "?";
}
