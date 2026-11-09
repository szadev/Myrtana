using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.World;

/// <summary>
/// Collision coverage checks (Work/handoff/collision.md):
/// <c>collision audit [top]</c> walks the world load set like the cell import and G3CollisionBuilder and lists, grouped
/// by mesh with counts and a sample position (Unity metres), every object whose G3 collision would be lost (collision-only
/// entities, invisible or missing visuals, unreadable .xnvmsh) and every collision mesh whose world bounds stick out of
/// the render bounds (offset / double-scale errors), plus large rendered meshes without any collision in the G3 data.
/// <c>collision cells &lt;cellsDir&gt; [top]</c> reads the generated cell scenes (Unity YAML) and lists rendered static
/// meshes without a collider, render-mesh colliders and disabled colliders, grouped by mesh asset.
/// </summary>
static partial class Program
{
    static int CollisionAudit(string data, string[] args)
    {
        int top = args.Length > 2 ? int.Parse(args[2]) : 15;
        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
        layout.Sectors.RemoveAll(s => WorldLayout.IsLowpolySector(s.Name));

        var report = new Dictionary<string, Dictionary<string, (int n, string at)>>();
        void Add(string cat, string mesh, float[] w)
        {
            if (!report.TryGetValue(cat, out var g)) report[cat] = g = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
            g.TryGetValue(mesh, out var v);
            g[mesh] = (v.n + 1, v.at ?? string.Format(CultureInfo.InvariantCulture, "{0:F1} {1:F1} {2:F1}", w[12] * 0.01f, w[13] * 0.01f, w[14] * 0.01f));
        }

        var meshes = new Dictionary<string, (MeshResource res, bool invisible)>(StringComparer.OrdinalIgnoreCase);
        var xnv = new Dictionary<string, (CollisionMeshResource res, string error)>(StringComparer.OrdinalIgnoreCase);
        int candidates = 0, withCollision = 0, ok = 0, oneSided = 0;
        var downFacing = new Dictionary<CollisionSubMesh, float>();

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
                if (e.Visual == VisualKind.SpeedTree) continue; // trunk capsules: G3CollisionBuilder.AddTreeCollider
                bool collides = CollisionRules.HasPlayerCollision(e);
                candidates++;
                string mesh = Path.GetFileNameWithoutExtension(e.Resource ?? "") is var m && m.Length > 0 ? m : "(no visual) " + (e.Name ?? "?");

                // Would the cell import place a renderer (and so a collider host) for it?
                string lost = null;
                (MeshResource res, bool invisible) vis = default;
                if (e.Visual != VisualKind.Mesh) lost = "no visual";
                else
                {
                    vis = LoadAuditMesh(meshFs, e.Resource, meshes);
                    if (vis.res == null) lost = "visual mesh missing";
                    else if (!e.RenderingEnabled) lost = "rendering disabled";
                    else if (vis.invisible) lost = "all materials invisible";
                }
                if (!collides)
                {
                    if (lost == null && vis.res != null && IsBlockingSize(vis.res, e.World))
                        Add(e.CollisionShapes == null ? "large mesh, no eCCollisionShape_PS in G3" : "large mesh, only non-player shapes in G3", mesh, e.World);
                    continue;
                }
                withCollision++;
                if (lost != null) { Add("collision lost: " + lost, mesh, e.World); continue; }

                // Collision meshes as G3CollisionBuilder builds them, in world space.
                var render = WorldBox(vis.res.BoundsMin, vis.res.BoundsMax, e.World, false);
                float[] col = null;
                string problem = null;
                var files = new List<(string file, int index)>();
                bool backFaces = false;
                if (e.CollisionShapes.Count == 0) files.Add((Path.GetFileNameWithoutExtension(e.Resource) + ".xnvmsh", -1));
                else foreach (var cs in e.CollisionShapes)
                    if (CollisionRules.BlocksPlayer(cs) && (cs.Type == CollisionShapeType.TriMesh || cs.Type == CollisionShapeType.ConvexHull))
                        files.Add((cs.File, cs.ResourceIndex));
                foreach (var (file, index) in files)
                {
                    var (cres, err) = LoadAuditXnv(meshFs, file, xnv);
                    if (cres == null)
                    {
                        if (index < 0) continue;  // lazy shape without a usable .xnvmsh: the builder uses the render mesh
                        problem = err != null ? "xnvmsh unreadable" : "xnvmsh missing/obsolete (no render fallback)";
                        break;
                    }
                    if (index >= cres.Meshes.Count) { problem = "xnvmsh index out of range"; break; }
                    bool pre = CollisionRules.IsPrescaled(file);
                    foreach (var sm in index >= 0 ? new[] { cres.Meshes[index] } : cres.Meshes.Where(x => !x.DisableCollision).ToArray())
                    {
                        col = Union(col, PointsBox(sm.Positions, e.World, pre));
                        if (sm.Convex) continue;
                        if (!downFacing.TryGetValue(sm, out var up)) downFacing[sm] = up = FirstHitUp(sm.Positions, sm.Indices, 12) is var f && f.hits >= 8 ? f.up : 1;
                        if (up < 0.7f) backFaces = true;
                    }
                    if (pre && !file.Contains("_SCX_") && Scale(e.World) is var s && Math.Abs(s - 1) > 0.02f) Add("uniform _SC_ prescaled mesh (was scaled twice)", mesh, e.World);
                }
                if (problem != null) { Add("collision lost: " + problem, mesh, e.World); continue; }
                if (backFaces) { Add("trimesh seen from above shows back faces (one-sided collider passes)", mesh, e.World); oneSided++; }
                if (col != null)
                {
                    float sticks = 0;
                    for (int a = 0; a < 3; a++)
                    {
                        float size = render[a + 3] - render[a];
                        float tol = 50 + 0.1f * size; // cm
                        sticks = Math.Max(sticks, Math.Max(render[a] - col[a] - tol, col[a + 3] - render[a + 3] - tol));
                    }
                    if (sticks > 0) { Add("collider outside render bounds", mesh, e.World); continue; }
                    // A walkable mesh whose collision top is far below the render top (floor collider missing / sunk).
                    float rh = render[4] - render[1];
                    if (rh > 100 && col[4] < render[4] - 0.5f * rh) Add("collider much lower than render (sunk/missing parts)", mesh, e.World);
                }
                ok++;
            }
        }

        Console.WriteLine($"entities {candidates:N0} (static, enabled), with player collision in G3 {withCollision:N0}, collider as in G3 {ok:N0}, " +
                          $"with back-facing trimesh parts {oneSided:N0} (two-sided colliders since the collision fix)");
        foreach (var cat in report.OrderBy(k => k.Key))
        {
            Console.WriteLine($"== {cat.Key}: {cat.Value.Values.Sum(v => v.n)} objects, {cat.Value.Count} meshes");
            foreach (var kv in cat.Value.OrderByDescending(k => k.Value.n).Take(top))
                Console.WriteLine($"  {kv.Value.n,5}  {kv.Key}  @ {kv.Value.at}");
        }
        return 0;
    }

    static (MeshResource, bool) LoadAuditMesh(G3FileSystem fs, string file, Dictionary<string, (MeshResource, bool)> cache)
    {
        if (string.IsNullOrEmpty(file)) return default;
        if (cache.TryGetValue(file, out var v)) return v;
        MeshResource res = null;
        bool invisible = false;
        try
        {
            string resolved = file;
            if (file.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var l = fs.GetByName(file);
                var lods = l != null ? Xlmsh.Read(l.Read()) : null;
                resolved = lods != null && lods.Count > 0 ? lods[0] : null;
            }
            var entry = resolved != null ? fs.GetByName(resolved) : null;
            res = entry != null ? Xcmsh.Read(entry.Read(), resolved) : null;
            if (res != null && res.Elements.Count == 0) res = null;
            if (res != null)
            {
                invisible = true;
                foreach (var el in res.Elements)
                {
                    var me = fs.GetByName(Path.GetFileNameWithoutExtension(el.Material ?? "") + ".xshmat");
                    var mat = me != null ? G3.Formats.Material.Xshmat.Read(me.Read(), el.Material) : null;
                    bool inv = mat != null && (mat.Blend == G3.Formats.Material.BlendMode.Invisible ||
                                               (mat.Name ?? el.Material ?? "").IndexOf("_Depth_", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!inv) { invisible = false; break; }
                }
            }
        }
        catch { res = null; }
        return cache[file] = (res, invisible);
    }

    static (CollisionMeshResource, string) LoadAuditXnv(G3FileSystem fs, string file, Dictionary<string, (CollisionMeshResource, string)> cache)
    {
        if (string.IsNullOrEmpty(file)) return (null, null);
        if (!file.EndsWith(".xnvmsh", StringComparison.OrdinalIgnoreCase)) file += ".xnvmsh";
        if (cache.TryGetValue(file, out var v)) return v;
        try
        {
            var entry = fs.GetByName(file);
            var res = entry != null ? Xnvmsh.Read(entry.Read(), file) : null;
            if (res != null && (res.Obsolete || res.Meshes.Count == 0)) res = null;
            return cache[file] = (res, null);
        }
        catch (Exception ex) { return cache[file] = (null, ex.Message); }
    }

    static float Scale(float[] w) => (float)Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]);

    /// <summary>World point (cm) of a local point; <paramref name="noScale"/> uses the normalised axes (prescaled meshes).</summary>
    static void Xf(float[] w, float x, float y, float z, bool noScale, float[] o)
    {
        float sx = 1, sy = 1, sz = 1;
        if (noScale)
        {
            sx = 1 / Math.Max(1e-6f, (float)Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]));
            sy = 1 / Math.Max(1e-6f, (float)Math.Sqrt(w[4] * w[4] + w[5] * w[5] + w[6] * w[6]));
            sz = 1 / Math.Max(1e-6f, (float)Math.Sqrt(w[8] * w[8] + w[9] * w[9] + w[10] * w[10]));
        }
        for (int i = 0; i < 3; i++) o[i] = x * sx * w[i] + y * sy * w[4 + i] + z * sz * w[8 + i] + w[12 + i];
    }

    static float[] PointsBox(float[] p, float[] w, bool noScale)
    {
        var b = new[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
        var o = new float[3];
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            Xf(w, p[i], p[i + 1], p[i + 2], noScale, o);
            for (int a = 0; a < 3; a++) { b[a] = Math.Min(b[a], o[a]); b[a + 3] = Math.Max(b[a + 3], o[a]); }
        }
        return b;
    }

    static float[] WorldBox(float[] min, float[] max, float[] w, bool noScale)
    {
        var pts = new float[24];
        for (int c = 0; c < 8; c++)
        {
            pts[c * 3] = (c & 1) != 0 ? max[0] : min[0];
            pts[c * 3 + 1] = (c & 2) != 0 ? max[1] : min[1];
            pts[c * 3 + 2] = (c & 4) != 0 ? max[2] : min[2];
        }
        return PointsBox(pts, w, noScale);
    }

    static float[] Union(float[] a, float[] b)
    {
        if (a == null) return b;
        for (int i = 0; i < 3; i++) { a[i] = Math.Min(a[i], b[i]); a[i + 3] = Math.Max(a[i + 3], b[i + 3]); }
        return a;
    }

    /// <summary>Big enough to walk on or bump into: at least 1.5 m across and 0.3 m high.</summary>
    static bool IsBlockingSize(MeshResource res, float[] w)
    {
        var b = WorldBox(res.BoundsMin, res.BoundsMax, w, false);
        return Math.Max(b[3] - b[0], b[5] - b[2]) >= 150 && b[4] - b[1] >= 30;
    }

    // ------------------------------------------------------------------ built cells (Unity YAML)

    static int CollisionCells(string[] args)
    {
        string dir = args.Length > 2 ? args[2] : @"Assets\Scenes\World\Cells";
        int top = args.Length > 3 ? int.Parse(args[3]) : 15;
        string assets = Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));
        var names = new Dictionary<string, string>();
        foreach (var sub in new[] { "G3Imported/Meshes", "G3Imported/Collision" })
        {
            var d = Path.Combine(assets, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var meta in Directory.EnumerateFiles(d, "*.asset.meta"))
                foreach (var line in File.ReadLines(meta))
                    if (line.StartsWith("guid: ")) { names[line.Substring(6).Trim()] = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(meta)); break; }
        }

        var report = new Dictionary<string, Dictionary<string, (int n, string at)>>();
        void Add(string cat, string mesh, string at)
        {
            if (!report.TryGetValue(cat, out var g)) report[cat] = g = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
            g.TryGetValue(mesh, out var v);
            g[mesh] = (v.n + 1, v.at ?? at);
        }
        int scenes = 0, rendered = 0, covered = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "Cell_*.unity"))
        {
            scenes++;
            var go = new Dictionary<string, (string name, bool active)>();
            var meshOf = new Dictionary<string, string>();          // go -> MeshFilter mesh guid
            var hasRenderer = new HashSet<string>();
            var colliders = new Dictionary<string, List<(string mesh, bool enabled)>>(); // go -> colliders
            var tf = new Dictionary<string, (string go, string father, string pos)>();
            string cls = null, id = null, owner = null, name = null, mesh = null, father = null, pos = null; bool active = true, enabled = true;
            void Flush()
            {
                if (id == null) return;
                switch (cls)
                {
                    case "1": go[id] = (name, active); break;
                    case "4": tf[id] = (owner, father, pos); break;
                    case "33": if (owner != null) meshOf[owner] = mesh; break;
                    case "23": if (owner != null) hasRenderer.Add(owner); break;
                    case "64": case "65": case "135": case "136":
                        if (owner == null) break;
                        if (!colliders.TryGetValue(owner, out var l)) colliders[owner] = l = new List<(string, bool)>();
                        l.Add((cls == "64" ? mesh : "prim", enabled));
                        break;
                }
            }
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith("--- !u!"))
                {
                    Flush();
                    int amp = line.IndexOf(" &", StringComparison.Ordinal);
                    cls = line.Substring(7, amp - 7); id = line.Substring(amp + 2).Split(' ')[0];
                    owner = name = mesh = father = pos = null; active = enabled = true;
                    continue;
                }
                var t = line.TrimStart();
                if (t.StartsWith("m_GameObject: {fileID: ")) owner = Between(t, "fileID: ", "}");
                else if (t.StartsWith("m_Name: ")) name = t.Substring(8);
                else if (t.StartsWith("m_IsActive: ")) active = t.EndsWith("1");
                else if (t.StartsWith("m_Enabled: ")) enabled = t.EndsWith("1");
                else if (t.StartsWith("m_Mesh: ") || t.StartsWith("m_Mesh: ")) mesh = t.Contains("guid: ") ? Between(t, "guid: ", ",") : null;
                else if (t.StartsWith("m_Father: {fileID: ")) father = Between(t, "fileID: ", "}");
                else if (t.StartsWith("m_LocalPosition: ")) pos = t.Substring(17);
            }
            Flush();

            var tfByGo = tf.Values.Where(v => v.go != null).GroupBy(v => v.go).ToDictionary(g => g.Key, g => g.First());
            var childCols = new HashSet<string>(); // go ids whose "G3Collision" child carries a collider
            foreach (var v in tf.Values)
                if (v.go != null && go.TryGetValue(v.go, out var g) && g.name == "G3Collision" && colliders.ContainsKey(v.go) && v.father != null && tf.TryGetValue(v.father, out var p) && p.go != null)
                    childCols.Add(p.go);
            string cell = Path.GetFileNameWithoutExtension(file);
            foreach (var kv in meshOf)
            {
                if (!hasRenderer.Contains(kv.Key) || !go.TryGetValue(kv.Key, out var g)) continue;
                rendered++;
                string mname = kv.Value != null && names.TryGetValue(kv.Value, out var nm) ? nm : g.name;
                string at = $"{cell} {(tfByGo.TryGetValue(kv.Key, out var tv) ? tv.pos : "")}";
                colliders.TryGetValue(kv.Key, out var cl);
                if ((cl == null || cl.Count == 0) && !childCols.Contains(kv.Key)) { Add("rendered mesh without collider", mname, at); continue; }
                covered++;
                if (cl != null && cl.Any(c => !c.enabled)) Add("disabled collider", mname, at);
                if (cl != null && cl.Any(c => c.mesh != null && c.mesh == kv.Value)) Add("collider = render mesh", mname, at);
            }
        }
        Console.WriteLine($"cells {scenes}, rendered static meshes {rendered:N0}, with collider {covered:N0}");
        foreach (var cat in report.OrderBy(k => k.Key))
        {
            Console.WriteLine($"== {cat.Key}: {cat.Value.Values.Sum(v => v.n)} objects, {cat.Value.Count} meshes");
            foreach (var kv in cat.Value.OrderByDescending(k => k.Value.n).Take(top))
                Console.WriteLine($"  {kv.Value.n,5}  {kv.Key}  @ {kv.Value.at}");
        }
        return 0;
    }

    static string Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return null;
        i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? s.Substring(i) : s.Substring(i, j - i);
    }
}

static partial class Program
{
    /// <summary>
    /// <c>collision winding [filter] [top]</c>: orientation of every cooked triangle mesh (.xnvmsh "MESH" sub-meshes) as
    /// the collider is built (indices as stored, Unity front face = cross(v1-v0, v2-v0)). A grid of vertical rays from
    /// above: the first triangle each ray hits is seen from outside, so its front normal must face up. One-sided
    /// MeshColliders let the CharacterController pass through back faces (floors, walls). With "render" as the filter
    /// prefix the .xcmsh render meshes are checked the same way (as a reference for the convention).
    /// </summary>
    static int CollisionWinding(string data, string[] args)
    {
        string filter = args.Length > 2 && args[2] != "-" ? args[2] : null;
        int top = args.Length > 3 ? int.Parse(args[3]) : 20;
        bool render = filter != null && filter.StartsWith("render", StringComparison.OrdinalIgnoreCase);
        if (render) filter = filter.Length > 7 ? filter.Substring(7) : null;
        using var fs = new G3FileSystem(data, render ? "_compiledMesh" : "_compiledPhysic");
        var rows = new List<(string name, float up, int hits)>();
        int meshes = 0;
        foreach (var e in fs.Files)
        {
            string ext = render ? ".xcmsh" : ".xnvmsh";
            if (!e.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (filter != null && e.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var parts = new List<(float[] p, uint[] i)>();
            try
            {
                if (render)
                {
                    var r = Xcmsh.Read(e.Read(), e.Path);
                    if (r != null) foreach (var el in r.Elements) parts.Add((el.Positions, el.Indices));
                }
                else
                {
                    var r = Xnvmsh.Read(e.Read(), e.Path);
                    if (r != null) foreach (var sm in r.Meshes) if (!sm.Convex) parts.Add((sm.Positions, sm.Indices));
                }
            }
            catch { continue; }
            for (int k = 0; k < parts.Count; k++)
            {
                var (p, idx) = parts[k];
                var (up, hits) = FirstHitUp(p, idx, 12);
                if (hits < 8) continue;
                meshes++;
                rows.Add(($"{Path.GetFileName(e.Path)}{(parts.Count > 1 ? "#" + k : "")}", up, hits));
            }
        }
        int inverted = rows.Count(r => r.up < 0.3f), mixed = rows.Count(r => r.up >= 0.3f && r.up < 0.7f);
        Console.WriteLine($"meshes {meshes}: facing up (ok) {rows.Count(r => r.up >= 0.7f)}, mixed {mixed}, inverted {inverted}");
        foreach (var r in rows.OrderBy(r => r.up).Take(top)) Console.WriteLine($"  up {r.up:P0} of {r.hits,3} rays  {r.name}");
        return 0;
    }

    /// <summary>Share of downward grid rays whose first hit has an upward front normal (cross(v1-v0, v2-v0).y &gt; 0).</summary>
    static (float up, int hits) FirstHitUp(float[] p, uint[] idx, int grid)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            minX = Math.Min(minX, p[i]); maxX = Math.Max(maxX, p[i]);
            minZ = Math.Min(minZ, p[i + 2]); maxZ = Math.Max(maxZ, p[i + 2]);
        }
        int up = 0, hits = 0;
        for (int gx = 0; gx < grid; gx++)
        for (int gz = 0; gz < grid; gz++)
        {
            float x = minX + (maxX - minX) * (gx + 0.5f) / grid, z = minZ + (maxZ - minZ) * (gz + 0.5f) / grid;
            float bestY = float.MinValue, bestNy = 0;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = (int)idx[t] * 3, b = (int)idx[t + 1] * 3, c = (int)idx[t + 2] * 3;
                // 2D barycentric test in XZ.
                float ax = p[a], az = p[a + 2], bx = p[b], bz = p[b + 2], cx = p[c], cz = p[c + 2];
                float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                if (Math.Abs(d) < 1e-6f) continue;
                float l1 = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d;
                float l2 = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d;
                float l3 = 1 - l1 - l2;
                if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                float y = l1 * p[a + 1] + l2 * p[b + 1] + l3 * p[c + 1];
                if (y <= bestY) continue;
                // front normal y of cross(v1-v0, v2-v0)
                float ux = bx - ax, uz = bz - az, vx = cx - ax, vz = cz - az;
                bestY = y; bestNy = uz * vx - ux * vz;
            }
            if (bestY == float.MinValue) continue;
            hits++;
            if (bestNy > 0) up++;
        }
        return (hits > 0 ? (float)up / hits : 1, hits);
    }
}
