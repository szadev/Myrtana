using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;

/// <summary>
/// <c>collision missing [cellsDir] [top] [--filter substr] [--all]</c> (fix-83): the built cell scenes (Unity YAML) checked
/// against the G3 source world. Every rendered static mesh object without a collider is matched back to its world entity
/// (origin in whole cm ±1, name / mesh scored like G3CollisionBuilder.FindEntity); the ones whose entity collides with the
/// player in G3 (<see cref="CollisionRules.HasPlayerCollision"/>) are "missing" — listed by mesh (count, sample cell and
/// position) and counted per region (manifest) and per mesh class (architecture / objects / other). Exit code 1 when an
/// architecture mesh is missing its collider.
/// </summary>
static partial class Program
{
    sealed class CellGo
    {
        public string Name, Father, MeshGuid, Tf;
        public bool Renderer, Collider, ColliderChild;
        public float[] Pos = new float[3], Rot = { 0, 0, 0, 1 }, Scl = { 1, 1, 1 };
    }

    static int CollisionMissing(string data, string[] args)
    {
        var plain = args.Skip(2).TakeWhile(a => !a.StartsWith("--")).ToArray();
        string dir = plain.Length > 0 ? plain[0] : @"Assets\Scenes\World\Cells";
        int top = plain.Length > 1 ? int.Parse(plain[1]) : 40;
        string filter = Option(args, "--filter");
        bool all = args.Contains("--all");
        string assets = Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));

        // Mesh asset names by guid.
        var names = new Dictionary<string, string>();
        foreach (var sub in new[] { "G3Imported/Meshes" })
        {
            var d = Path.Combine(assets, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var meta in Directory.EnumerateFiles(d, "*.asset.meta"))
                foreach (var line in File.ReadLines(meta))
                    if (line.StartsWith("guid: ")) { names[line.Substring(6).Trim()] = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(meta)); break; }
        }
        // Region per cell scene (manifest).
        var region = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = Path.Combine(dir, "..", "G3WorldManifest.asset");
        if (File.Exists(manifest))
        {
            string reg = null;
            foreach (var line in File.ReadLines(manifest))
            {
                var t = line.Trim();
                if (t.StartsWith("region: ")) reg = t.Substring(8);
                else if (t.StartsWith("scenePath: ")) region[Path.GetFileNameWithoutExtension(t.Substring(11))] = reg ?? "?";
            }
        }

        // Source entities with a visual, keyed by origin in whole cm (as G3CollisionBuilder.Entities, all sectors).
        var index = new Dictionary<(int, int, int), List<WorldEntity>>();
        using (var projects = new G3FileSystem(data, "Projects_compiled"))
        {
            const string vol = "Projects_compiled/";
            var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read(), includeDisabled: true);
            foreach (var sector in layout.Sectors)
            foreach (var node in sector.Nodes.Concat(sector.Lrentdats))
            {
                List<WorldEntity> list;
                try { var entry = projects.Get(vol + node); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
                catch { continue; }
                if (list == null) continue;
                foreach (var e in list)
                {
                    if (e.Visual == VisualKind.None) continue;
                    var k = ((int)Math.Round(e.X), (int)Math.Round(e.Y), (int)Math.Round(e.Z));
                    if (!index.TryGetValue(k, out var l)) index[k] = l = new List<WorldEntity>(1);
                    l.Add(e);
                }
            }
        }

        var missing = new Dictionary<string, (int n, string at)>(StringComparer.OrdinalIgnoreCase);
        var byRegion = new SortedDictionary<string, int[]>(StringComparer.OrdinalIgnoreCase); // [arch missing, arch total, all missing, all collide]
        int scenes = 0, rendered = 0, withCol = 0, noColNoG3 = 0, unmatched = 0, miss = 0, archMiss = 0, archTotal = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "Cell_*.unity"))
        {
            string cell = Path.GetFileNameWithoutExtension(file);
            if (filter != null && cell.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && !filter.StartsWith("mesh:")) continue;
            scenes++;
            var gos = ParseCellGos(file, out var tfOwner);
            string reg = region.TryGetValue(cell, out var rg) ? rg : "?";
            if (!byRegion.TryGetValue(reg, out var rc)) byRegion[reg] = rc = new int[4];
            // G3Collision children: collider on the parent object.
            foreach (var g in gos.Values)
                if (g.Name == "G3Collision" && g.Collider && g.Father != null && tfOwner.TryGetValue(g.Father, out var pid) && gos.TryGetValue(pid, out var pg))
                    pg.ColliderChild = true;
            foreach (var kv in gos)
            {
                var g = kv.Value;
                if (!g.Renderer || g.MeshGuid == null || UnderTrees(g, gos, tfOwner)) continue; // trees: trunk capsule on the root
                rendered++;
                string mesh = names.TryGetValue(g.MeshGuid, out var nm) ? nm : g.Name;
                bool arch = IsArchitecture(mesh);
                var p = WorldPos(g, gos, tfOwner);
                var rec = MatchEntity(index, p[0] * 100, p[1] * 100, p[2] * 100, g.Name, mesh);
                bool g3 = rec != null && CollisionRules.HasPlayerCollision(rec);
                if (arch && g3) { archTotal++; rc[1]++; }
                if (g3) rc[3]++;
                if (g.Collider || g.ColliderChild) { withCol++; continue; }
                if (rec == null) { unmatched++; continue; }
                if (!g3) { noColNoG3++; continue; }
                miss++; rc[2]++;
                if (arch) { archMiss++; rc[0]++; }
                if (filter != null && filter.StartsWith("mesh:") && mesh.IndexOf(filter.Substring(5), StringComparison.OrdinalIgnoreCase) < 0) continue;
                missing.TryGetValue(mesh, out var v);
                missing[mesh] = (v.n + 1, v.at ?? string.Format(CultureInfo.InvariantCulture, "{0} ({1:F1}, {2:F1}, {3:F1})", cell, p[0], p[1], p[2]));
            }
        }

        Console.WriteLine($"cells {scenes}, rendered static meshes {rendered:N0}, with collider {withCol:N0}, without collider: none in G3 {noColNoG3:N0}, " +
                          $"unmatched {unmatched:N0}, MISSING (G3 collides) {miss:N0} — architecture {archMiss:N0} of {archTotal:N0}");
        Console.WriteLine("per region: architecture missing/total, all missing/total colliding in G3");
        foreach (var kv in byRegion) Console.WriteLine($"  {kv.Key,-12} arch {kv.Value[0],5}/{kv.Value[1],-6} all {kv.Value[2],6}/{kv.Value[3]}");
        Console.WriteLine($"== missing collider (G3 collides): {missing.Values.Sum(v => v.n)} objects, {missing.Count} meshes");
        foreach (var kv in missing.OrderByDescending(k => IsArchitecture(k.Key)).ThenByDescending(k => k.Value.n).Take(all ? int.MaxValue : top))
            Console.WriteLine($"  {kv.Value.n,5}  {kv.Key}  @ {kv.Value.at}");
        return archMiss > 0 ? 1 : 0;
    }

    /// <summary>Buildings and their parts: walls, floors, stairs, towers, bridges, ruins, huts, castles, caves, mines.</summary>
    static bool IsArchitecture(string mesh)
    {
        if (string.IsNullOrEmpty(mesh)) return false;
        string m = mesh.ToLowerInvariant();
        if (m.Contains("_architecture_") || m.Contains("_house") || m.Contains("_castle") || m.Contains("_city_") || m.Contains("_ruin") ||
            m.Contains("_tower") || m.Contains("_bridge") || m.Contains("_stair") || m.Contains("_wall") || m.Contains("_floor") ||
            m.Contains("_hut") || m.Contains("_palisade") || m.Contains("_gate") || m.Contains("_temple") || m.Contains("_mine_") ||
            m.Contains("_cave") || m.Contains("_dungeon") || m.Contains("_harbor") || m.Contains("_jetty") || m.Contains("_pier"))
            return !m.Contains("_plant_") && !m.Contains("spiderweb") && !m.Contains("wallhang") && !m.Contains("sheetwall") && !m.Contains("carpet");
        return false;
    }

    static WorldEntity MatchEntity(Dictionary<(int, int, int), List<WorldEntity>> index, float x, float y, float z, string goName, string mesh)
    {
        int kx = (int)Math.Round(x), ky = (int)Math.Round(y), kz = (int)Math.Round(z);
        WorldEntity best = null; int bestScore = -1;
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (!index.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
            foreach (var r in list)
            {
                string stem = Path.GetFileNameWithoutExtension(r.Resource ?? "");
                int score = 0;
                if (!string.IsNullOrEmpty(r.Name) && r.Name == goName) score += 2;
                if (stem.Equals(mesh, StringComparison.OrdinalIgnoreCase)) score += 1;
                if (stem.Equals(goName, StringComparison.OrdinalIgnoreCase)) score += 1;
                if (dx == 0 && dy == 0 && dz == 0) score += 1;
                if (score > bestScore) { bestScore = score; best = r; }
            }
        }
        return best;
    }

    /// <summary>World position (m) of a cell object: its local TRS composed up the father chain.</summary>
    static float[] WorldPos(CellGo g, Dictionary<string, CellGo> gos, Dictionary<string, string> tfOwner)
    {
        var p = (float[])g.Pos.Clone();
        var cur = g;
        for (int guard = 0; guard < 64 && cur.Father != null && cur.Father != "0"; guard++)
        {
            if (!tfOwner.TryGetValue(cur.Father, out var pid) || !gos.TryGetValue(pid, out var par)) break;
            for (int a = 0; a < 3; a++) p[a] *= par.Scl[a];
            p = Rotate(par.Rot, p);
            for (int a = 0; a < 3; a++) p[a] += par.Pos[a];
            cur = par;
        }
        return p;
    }

    static bool UnderTrees(CellGo g, Dictionary<string, CellGo> gos, Dictionary<string, string> tfOwner)
    {
        var cur = g;
        for (int guard = 0; guard < 64 && cur.Father != null && cur.Father != "0"; guard++)
        {
            if (!tfOwner.TryGetValue(cur.Father, out var pid) || !gos.TryGetValue(pid, out var par)) return false;
            if (par.Name == "Trees") return true;
            cur = par;
        }
        return false;
    }

    static float[] Rotate(float[] q, float[] v)
    {
        float qx = q[0], qy = q[1], qz = q[2], qw = q[3];
        float tx = 2 * (qy * v[2] - qz * v[1]), ty = 2 * (qz * v[0] - qx * v[2]), tz = 2 * (qx * v[1] - qy * v[0]);
        return new[] { v[0] + qw * tx + (qy * tz - qz * ty), v[1] + qw * ty + (qz * tx - qx * tz), v[2] + qw * tz + (qx * ty - qy * tx) };
    }

    /// <summary>GameObjects of a cell scene by fileID with transform, mesh, renderer and collider flags; tfOwner maps transform id → GameObject id.</summary>
    static Dictionary<string, CellGo> ParseCellGos(string file, out Dictionary<string, string> tfOwner)
    {
        var gos = new Dictionary<string, CellGo>();
        tfOwner = new Dictionary<string, string>();
        var owner2 = tfOwner;
        string cls = null, id = null, owner = null, name = null, mesh = null, father = null;
        string pos = null, rot = null, scl = null;
        bool enabled = true;
        CellGo Get(string gid) { if (!gos.TryGetValue(gid, out var g)) gos[gid] = g = new CellGo(); return g; }
        void Flush()
        {
            if (id == null) return;
            switch (cls)
            {
                case "1": Get(id).Name = name; break;
                case "4":
                    if (owner == null) break;
                    owner2[id] = owner;
                    var t = Get(owner);
                    t.Tf = id; t.Father = father;
                    Vec(pos, t.Pos); Vec(rot, t.Rot); Vec(scl, t.Scl);
                    break;
                case "33": if (owner != null) Get(owner).MeshGuid = mesh; break;
                case "23": if (owner != null && enabled) Get(owner).Renderer = true; break;
                case "64": case "65": case "135": case "136":
                    if (owner != null && enabled) Get(owner).Collider = true;
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
                owner = name = mesh = father = pos = rot = scl = null; enabled = true;
                continue;
            }
            var s = line.TrimStart();
            if (s.StartsWith("m_GameObject: {fileID: ")) owner = Between(s, "fileID: ", "}");
            else if (s.StartsWith("m_Name: ")) name = s.Substring(8);
            else if (s.StartsWith("m_Enabled: ")) enabled = s.EndsWith("1");
            else if (s.StartsWith("m_Mesh: ")) mesh = s.Contains("guid: ") ? Between(s, "guid: ", ",") : null;
            else if (s.StartsWith("m_Father: {fileID: ")) father = Between(s, "fileID: ", "}");
            else if (s.StartsWith("m_LocalPosition: ")) pos = s.Substring(17);
            else if (s.StartsWith("m_LocalRotation: ")) rot = s.Substring(17);
            else if (s.StartsWith("m_LocalScale: ")) scl = s.Substring(14);
        }
        Flush();
        return gos;
    }

    static void Vec(string yaml, float[] o)
    {
        if (yaml == null) return;
        string[] keys = { "x: ", "y: ", "z: ", "w: " };
        for (int i = 0; i < o.Length; i++)
        {
            var v = Between(yaml, keys[i], i + 1 < o.Length ? "," : "}");
            if (v != null && float.TryParse(v.Trim().TrimEnd('}'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) o[i] = f;
        }
    }
}

static partial class Program
{
    /// <summary>
    /// <c>collision water [top]</c> (fix-83): world meshes whose water test changes between the old builder rule (any
    /// material named "*water*" or drawn with a water shader → no collider) and <see cref="CollisionRules.IsWaterSurface"/>
    /// (every drawn material is water). Lists the meshes that get their G3 collision back, and any that newly lose it.
    /// </summary>
    static int CollisionWater(string data, string[] args)
    {
        int top = args.Length > 2 ? int.Parse(args[2]) : 40;
        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read(), includeDisabled: true);
        var mats = new Dictionary<string, G3.Formats.Material.MaterialResource>(StringComparer.OrdinalIgnoreCase);
        var verdict = new Dictionary<string, (bool oldSkip, bool newSkip, string names)>(StringComparer.OrdinalIgnoreCase);
        var count = new Dictionary<string, (int all, int collide, string at)>(StringComparer.OrdinalIgnoreCase);
        var meshes = new Dictionary<string, (G3.Formats.Mesh.MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var cellsHit = new SortedSet<string>(StringComparer.Ordinal); // streaming cells (G3WorldBatch 250 m grid) to rebuild
        foreach (var sector in layout.Sectors)
        foreach (var node in sector.Nodes.Concat(sector.Lrentdats))
        {
            List<WorldEntity> list;
            try { var entry = projects.Get(vol + node); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual != VisualKind.Mesh || string.IsNullOrEmpty(e.Resource)) continue;
                if (!verdict.TryGetValue(e.Resource, out var v))
                {
                    var (res, _) = LoadAuditMesh(meshFs, e.Resource, meshes);
                    var list2 = new List<(string, string)>();
                    bool old = false;
                    if (res != null)
                        foreach (var el in res.Elements)
                        {
                            string stem = Path.GetFileNameWithoutExtension(el.Material ?? "");
                            if (!mats.TryGetValue(stem, out var m))
                            {
                                try { var me = meshFs.GetByName(stem + ".xshmat"); m = me != null ? G3.Formats.Material.Xshmat.Read(me.Read(), stem) : null; }
                                catch { m = null; }
                                mats[stem] = m;
                            }
                            bool invisible = m != null && (m.Blend == G3.Formats.Material.BlendMode.Invisible || stem.IndexOf("_Depth_", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (invisible) continue;
                            bool waterShader = m != null && (m.ShaderClass == "eCShaderRiver" || m.ShaderClass == "eCShaderOcean" || m.ShaderClass == "eCShaderWater" ||
                                                             stem.IndexOf("_Water_", StringComparison.OrdinalIgnoreCase) >= 0);
                            string shader = waterShader ? "G3/Water" : "G3/Default";
                            if (waterShader || stem.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0) old = true;
                            list2.Add((stem, shader));
                        }
                    verdict[e.Resource] = v = (old, CollisionRules.IsWaterSurface(list2), string.Join(" ", list2.Select(t => t.Item1)));
                }
                if (v.oldSkip == v.newSkip) continue;
                cellsHit.Add($"Cell_{(int)Math.Floor((e.X / 100 + 4250) / 250)}_{(int)Math.Floor((e.Z / 100 + 4250) / 250)}");
                count.TryGetValue(e.Resource, out var c);
                count[e.Resource] = (c.all + 1, c.collide + (CollisionRules.HasPlayerCollision(e) ? 1 : 0),
                    c.at ?? string.Format(CultureInfo.InvariantCulture, "{0:F1} {1:F1} {2:F1}", e.X / 100, e.Y / 100, e.Z / 100));
            }
        }
        foreach (var grp in count.GroupBy(k => verdict[k.Key].newSkip))
        {
            Console.WriteLine(grp.Key ? "== newly treated as water (no collider):" : "== no longer treated as water (G3 collision restored):");
            Console.WriteLine($"   {grp.Count()} meshes, {grp.Sum(k => k.Value.all)} objects, {grp.Sum(k => k.Value.collide)} with player collision in G3");
            foreach (var kv in grp.OrderByDescending(k => k.Value.collide).Take(top))
                Console.WriteLine($"  {kv.Value.all,5} ({kv.Value.collide} collide)  {Path.GetFileNameWithoutExtension(kv.Key)}  @ {kv.Value.at}  [{verdict[kv.Key].names}]");
        }
        Console.WriteLine($"cells to rebuild ({cellsHit.Count}): {string.Join(",", cellsHit)}");
        return 0;
    }
}
