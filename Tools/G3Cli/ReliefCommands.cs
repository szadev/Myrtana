using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.Remaster;
using G3.Formats.World;
using G3.Unity;
using V3 = System.Numerics.Vector3;
using V2 = System.Numerics.Vector2;

static partial class Program
{
    /// <summary>Loads every landscape cell of the load set (LOD0, metres, Unity winding): name, positions, normals, per element indices + material.</summary>
    static List<(string name, MeshResource mesh)> LoadLandscapeCells(G3FileSystem fs, Func<float[], bool> filter = null)
    {
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes)
        {
            var entry = fs.Get(vol + path);
            List<WorldEntity> list;
            try { list = entry == null ? null : EntityArchive.Read(entry.Read()); }
            catch (Exception) { continue; }
            if (list == null) continue;
            foreach (var e in list)
                if (e.Enabled && e.RenderingEnabled && e.Visual == VisualKind.Mesh && WorldRemasterCategories.IsTerrainMesh(e.Resource) &&
                    (filter == null || filter(e.Bounds)))
                    names.Add(e.Resource);
        }
        var cells = new List<(string, MeshResource)>();
        foreach (var res in names)
        {
            string lod0 = res;
            if (lod0.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var le = fs.GetByName(lod0);
                try { lod0 = le == null ? null : Xlmsh.Read(le.Read()).FirstOrDefault(); } catch (Exception) { lod0 = null; }
            }
            if (lod0 == null) continue;
            var me = fs.GetByName(Path.GetFileNameWithoutExtension(lod0) + ".xcmsh");
            MeshResource m = null;
            try { m = me == null ? null : Xcmsh.Read(me.Read(), lod0); } catch (Exception) { }
            if (m != null) cells.Add((Path.GetFileNameWithoutExtension(res), m));
        }
        return cells;
    }

    /// <summary><c>relief probe</c>: are the cells' vertex normals smooth, and equal on both sides of a cell border?</summary>
    static int ReliefProbe(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var cells = LoadLandscapeCells(fs);
        Console.WriteLine($"cells {cells.Count}");
        // position key (1 mm) -> (cell index, normal)
        var at = new Dictionary<(long, long, long), List<(int cell, float nx, float ny, float nz)>>();
        long verts = 0, tris = 0;
        double sumEdge = 0; long nEdge = 0;
        int splitDiff = 0, splitAll = 0;
        for (int c = 0; c < cells.Count; c++)
        {
            var m = cells[c].mesh;
            foreach (var el in m.Elements)
            {
                verts += el.VertexCount; tris += el.Indices.Length / 3;
                for (int i = 0; i < el.VertexCount; i++)
                {
                    var k = ((long)Math.Round(el.Positions[i * 3] * 10), (long)Math.Round(el.Positions[i * 3 + 1] * 10), (long)Math.Round(el.Positions[i * 3 + 2] * 10));
                    if (!at.TryGetValue(k, out var l)) at[k] = l = new List<(int, float, float, float)>();
                    l.Add((c, el.Normals[i * 3], el.Normals[i * 3 + 1], el.Normals[i * 3 + 2]));
                }
                for (int t = 0; t < el.Indices.Length; t += 3)
                    for (int j = 0; j < 3; j++)
                    {
                        int a = (int)el.Indices[t + j], b = (int)el.Indices[t + (j + 1) % 3];
                        double dx = el.Positions[a * 3] - el.Positions[b * 3], dy = el.Positions[a * 3 + 1] - el.Positions[b * 3 + 1], dz = el.Positions[a * 3 + 2] - el.Positions[b * 3 + 2];
                        sumEdge += Math.Sqrt(dx * dx + dy * dy + dz * dz) / 100; nEdge++;
                    }
            }
        }
        int shared = 0, sharedDiff = 0; double maxAng = 0;
        var hist = new int[6];
        foreach (var kv in at)
        {
            var l = kv.Value;
            if (l.Count < 2) continue;
            bool cross = l.Any(x => x.cell != l[0].cell);
            double worst = 0;
            for (int i = 1; i < l.Count; i++)
            {
                double d = l[0].nx * l[i].nx + l[0].ny * l[i].ny + l[0].nz * l[i].nz;
                double ang = Math.Acos(Math.Max(-1, Math.Min(1, d))) * 180 / Math.PI;
                worst = Math.Max(worst, ang);
            }
            if (cross)
            {
                shared++;
                if (worst > 0.5) sharedDiff++;
                maxAng = Math.Max(maxAng, worst);
                hist[worst < 0.1 ? 0 : worst < 1 ? 1 : worst < 5 ? 2 : worst < 15 ? 3 : worst < 45 ? 4 : 5]++;
            }
            else { splitAll++; if (worst > 0.5) splitDiff++; }
        }
        Console.WriteLine($"verts {verts} tris {tris} mean edge {sumEdge / nEdge:F2} m");
        long up = 0, down = 0, agree = 0, disagree = 0;
        foreach (var (_, mr) in cells)
        {
            var rm = ToRelief(mr);
            foreach (var t in rm.Sub)
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    var c = V3.Cross(rm.Pos[t[i + 1]] - rm.Pos[t[i]], rm.Pos[t[i + 2]] - rm.Pos[t[i]]);
                    if (c.Y > 0) up++; else down++;
                    if (V3.Dot(c, rm.Nrm[t[i]] + rm.Nrm[t[i + 1]] + rm.Nrm[t[i + 2]]) > 0) agree++; else disagree++;
                }
        }
        Console.WriteLine($"winding cross(p1-p0,p2-p0) (Unity order): y>0 {up}, y<=0 {down}; agrees with vertex normals {agree}, disagrees {disagree}");
        Console.WriteLine($"in-cell split positions {splitAll}, normals differ >0.5deg {splitDiff}");
        Console.WriteLine($"cross-cell positions {shared}, normals differ >0.5deg {sharedDiff}, max {maxAng:F1} deg; hist <0.1,<1,<5,<15,<45,>=45: {string.Join(",", hist)}");
        return 0;
    }

    /// <summary>A landscape cell as the runtime sees it: metres, Unity winding (i0, i2, i1), Color32 byte order, per element a submesh.</summary>
    static ReliefMesh ToRelief(MeshResource m)
    {
        int total = 0;
        foreach (var e in m.Elements) total += e.VertexCount;
        var pos = new V3[total]; var nrm = new V3[total];
        var uv0 = new V2[total]; var col = new uint[total];
        V2[] uv1 = m.Elements.Any(e => e.Uv1 != null) ? new V2[total] : null;
        var sub = new int[m.Elements.Count][]; var amp = new float[m.Elements.Count]; var pho = new float[m.Elements.Count];
        int bv = 0;
        for (int k = 0; k < m.Elements.Count; k++)
        {
            var e = m.Elements[k];
            for (int i = 0; i < e.VertexCount; i++)
            {
                pos[bv + i] = new V3(e.Positions[i * 3] * 0.01f, e.Positions[i * 3 + 1] * 0.01f, e.Positions[i * 3 + 2] * 0.01f);
                nrm[bv + i] = e.Normals != null ? new V3(e.Normals[i * 3], e.Normals[i * 3 + 1], e.Normals[i * 3 + 2]) : V3.UnitY;
                if (e.Uv0 != null) uv0[bv + i] = new V2(e.Uv0[i * 2], e.Uv0[i * 2 + 1]);
                if (uv1 != null && e.Uv1 != null) uv1[bv + i] = new V2(e.Uv1[i * 2], e.Uv1[i * 2 + 1]);
                uint c = e.Diffuse != null ? e.Diffuse[i] : 0xFFFFFFFF;
                col[bv + i] = ((c >> 16) & 0xFF) | (((c >> 8) & 0xFF) << 8) | ((c & 0xFF) << 16) | (((c >> 24) & 0xFF) << 24);
            }
            var t = new int[e.Indices.Length];
            for (int i = 0; i < t.Length; i += 3)
            {
                t[i] = (int)e.Indices[i] + bv; t[i + 1] = (int)e.Indices[i + 2] + bv; t[i + 2] = (int)e.Indices[i + 1] + bv;
            }
            sub[k] = t;
            (amp[k], pho[k]) = ReliefMaterials.Mask(Path.GetFileNameWithoutExtension(e.Material ?? ""));
            bv += e.VertexCount;
        }
        return new ReliefMesh { Pos = pos, Nrm = nrm, Uv0 = uv0, Uv1 = uv1, Col = col, Sub = sub, SubAmp = amp, SubPhong = pho };
    }

    /// <summary>World-wide facts of every terrain position (all cells at once), per cell in its weld order; <paramref name="open"/> = the open-ground field.</summary>
    static List<(string name, ReliefShared facts)> ReliefWorldFacts(List<(string name, ReliefMesh mesh)> cells, ReliefSettings s, Func<V3, byte> open = null,
                                                                   Func<V3, V3, (bool pin, byte clear)> clear = null)
    {
        var f = new ReliefFacts(s.walkNy);
        foreach (var (_, m) in cells) { if (m.Weld == null) TerrainRelief.Weld(m); f.Add(m); }
        return cells.Select(c => (c.name, f.For(c.mesh, open, clear))).ToList();
    }

    /// <summary>
    /// Open-ground field (0 = gameplay .. 255 = open): the distance on a 2 m grid to what G3 placed on its own surface and the
    /// relief must not lift or bury: path / town / architecture ground (masked materials), architecture and object meshes
    /// (entity bounds); and, counted <paramref name="placedScale"/> times closer, rocks, plants and trees (they sit on the old
    /// surface: a swell must fade before it reaches them). NPCs and items are not sources since pass 2 (Docs/remaster-world.md
    /// section 14): characters, corpses and dropped items stand on the visual ground.
    /// </summary>
    static Func<V3, byte> ReliefOpenField(G3FileSystem fs, List<(string name, ReliefMesh mesh)> cells, float nearM = 6, float farM = 20, float res = 2, float placedScale = 3f)
    {
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var (_, m) in cells)
            foreach (var p in m.Pos) { x0 = Math.Min(x0, p.X); z0 = Math.Min(z0, p.Z); x1 = Math.Max(x1, p.X); z1 = Math.Max(z1, p.Z); }
        x0 -= farM; z0 -= farM; x1 += farM; z1 += farM;
        int nx = (int)Math.Ceiling((x1 - x0) / res) + 1, nz = (int)Math.Ceiling((z1 - z0) / res) + 1;
        var d = new float[nx * nz];
        var dp = new float[nx * nz];   // placed things (rocks, plants, trees)
        for (int i = 0; i < d.Length; i++) { d[i] = 1e9f; dp[i] = 1e9f; }
        long marked = 0, placed = 0;
        void MarkIn(float[] g, float ax, float az, float bx, float bz, ref long cnt)
        {
            int i0 = Math.Max(0, (int)((ax - x0) / res)), i1 = Math.Min(nx - 1, (int)((bx - x0) / res));
            int j0 = Math.Max(0, (int)((az - z0) / res)), j1 = Math.Min(nz - 1, (int)((bz - z0) / res));
            for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) { if (g[j * nx + i] != 0) cnt++; g[j * nx + i] = 0; }
        }
        void Mark(float ax, float az, float bx, float bz) => MarkIn(d, ax, az, bx, bz, ref marked);
        void MarkPlaced(float ax, float az, float bx, float bz) => MarkIn(dp, ax, az, bx, bz, ref placed);
        // masked terrain materials (paths, town ground, architecture, dungeon, water)
        foreach (var (_, m) in cells)
            for (int sm = 0; sm < m.Sub.Length; sm++)
            {
                if (m.SubAmp[sm] > 0) continue;
                foreach (int v in m.Sub[sm]) Mark(m.Pos[v].X, m.Pos[v].Z, m.Pos[v].X, m.Pos[v].Z);
            }
        // entities: architecture / object meshes, NPCs, items
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        int ents = 0, trees = 0, rocksPlants = 0;
        foreach (var sec in layout.Sectors)
        foreach (var path in sec.Nodes)
        {
            var entry = fs.Get(vol + path);
            List<WorldEntity> list;
            try { list = entry == null ? null : EntityArchive.Read(entry.Read()); }
            catch (Exception) { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (!e.Enabled) continue;
                float ex = e.X / 100, ez = e.Z / 100;
                if (e.IsNpc || e.IsItem) continue;   // pass 2: they stand on the visual ground
                if (e.Visual == VisualKind.SpeedTree) { MarkPlaced(ex - 0.75f, ez - 0.75f, ex + 0.75f, ez + 0.75f); trees++; continue; }
                if (e.Visual != VisualKind.Mesh || string.IsNullOrEmpty(e.Resource) || WorldRemasterCategories.IsTerrainMesh(e.Resource)) continue;
                string cat = WorldRemasterCategories.Classify(e.Resource);
                bool gameplay = cat == WorldRemasterCategories.Architecture || cat == WorldRemasterCategories.Prop;
                bool onGround = cat == WorldRemasterCategories.Rock || cat == WorldRemasterCategories.Plant;
                if (!gameplay && !onGround) continue;
                var b = e.Bounds;
                float bx0 = b[0] / 100, bz0 = b[2] / 100, bx1 = b[3] / 100, bz1 = b[5] / 100;
                float cx = (bx0 + bx1) / 2, cz = (bz0 + bz1) / 2;
                bool world = bx1 >= bx0 && bz1 >= bz0 && Math.Abs(cx - ex) < 60 && Math.Abs(cz - ez) < 60 && bx1 - bx0 < 400 && bz1 - bz0 < 400;
                if (!world) { bx0 = ex - 2; bz0 = ez - 2; bx1 = ex + 2; bz1 = ez + 2; }
                if (gameplay) { Mark(bx0, bz0, bx1, bz1); ents++; }
                else { MarkPlaced(bx0, bz0, bx1, bz1); rocksPlants++; }
            }
        }
        // chamfer distance (1, sqrt 2) in grid units, two passes
        const float D1 = 1f, D2 = 1.41421356f;
        void Chamfer(float[] d)
        {
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i; float v = d[k];
                    if (i > 0) v = Math.Min(v, d[k - 1] + D1);
                    if (j > 0) { v = Math.Min(v, d[k - nx] + D1); if (i > 0) v = Math.Min(v, d[k - nx - 1] + D2); if (i < nx - 1) v = Math.Min(v, d[k - nx + 1] + D2); }
                    d[k] = v;
                }
            for (int j = nz - 1; j >= 0; j--)
                for (int i = nx - 1; i >= 0; i--)
                {
                    int k = j * nx + i; float v = d[k];
                    if (i < nx - 1) v = Math.Min(v, d[k + 1] + D1);
                    if (j < nz - 1) { v = Math.Min(v, d[k + nx] + D1); if (i < nx - 1) v = Math.Min(v, d[k + nx + 1] + D2); if (i > 0) v = Math.Min(v, d[k + nx - 1] + D2); }
                    d[k] = v;
                }
        }
        Chamfer(d); Chamfer(dp);
        for (int k = 0; k < d.Length; k++) d[k] = Math.Min(d[k], placedScale * dp[k]);
        long openCells = 0;
        for (int i = 0; i < d.Length; i++) if (d[i] * res >= farM) openCells++;
        Console.WriteLine($"open field: grid {nx}x{nz} at {res} m, {ents} gameplay entities, {rocksPlants} rocks/plants, {trees} trees, {marked} gameplay + {placed} placed cells, {100.0 * openCells / d.Length:F0} % of the grid open (>= {farM} m)");
        return p =>
        {
            int i = (int)((p.X - x0) / res), j = (int)((p.Z - z0) / res);
            if (i < 0 || j < 0 || i >= nx || j >= nz) return 0;
            float m = d[j * nx + i] * res;   // distance to gameplay (m), stored 0..OpenRange m in a byte
            return (byte)Math.Round(255 * Math.Max(0, Math.Min(1, m / ReliefFacts.OpenRange)));
        };
    }

    static string FactsFile(string repo) => Path.Combine(repo, "Assets/G3/Unity/Runtime/Remaster/Resources/G3TerrainReliefFacts.bytes");

    /// <summary><c>relief facts [--repo dir]</c>: writes the world-wide facts table into the runtime Resources (G3TerrainReliefFacts.bytes).</summary>
    static int ReliefWriteFacts(string data, string[] args)
    {
        string repo = Directory.GetCurrentDirectory();
        for (int i = 2; i < args.Length; i++) if (args[i] == "--repo") repo = args[++i];
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var s = ReliefSettings.Default;
        var cells = LoadLandscapeCells(fs).Select(c => (c.name, ToRelief(c.mesh))).ToList();
        float placedScale = float.TryParse(Environment.GetEnvironmentVariable("G3_RELIEF_PLACED"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ps) ? ps : 3f;
        var openField = ReliefOpenField(fs, cells, placedScale: placedScale);
        // fix-174: pinned open edges of the terrain union + the clearance to other sheets / placed rock and building meshes
        var clear = args.Contains("--no-clear") ? null : ClearanceFacts(BuildClearance(fs, cells), cells);
        var facts = ReliefWorldFacts(cells, s, openField, clear);
        var file = FactsFile(repo);
        using (var st = File.Create(file)) ReliefFacts.Write(st, facts);
        Console.WriteLine($"cells {cells.Count}, welded vertices {facts.Sum(f => f.facts.Count)} -> {file} ({new FileInfo(file).Length / 1048576.0:F1} MB)");
        // round trip: the runtime decodes the same normals
        var bytes = File.ReadAllBytes(file);
        var index = ReliefFacts.Index(bytes);
        int bad = 0;
        foreach (var (name, f) in facts)
        {
            var (o, n) = index[name];
            var d = ReliefFacts.Decode(bytes, o, n);
            for (int i = 0; i < n; i++) if (d.N[i] != f.N[i] || d.Flags[i] != f.Flags[i] || d.Ny1[i] != f.Ny1[i] || d.Open[i] != f.Open[i] || d.Clear[i] != f.Clear[i]) bad++;
        }
        Console.WriteLine($"round trip: {index.Count} blocks, {bad} mismatches");
        // walkable positions by distance (what the swells may use)
        long walk = 0, w2 = 0, w6 = 0, w10 = 0, w20 = 0, w50 = 0;
        foreach (var (_, f) in facts)
            for (int i = 0; i < f.Count; i++)
            {
                if ((f.Flags[i] & ReliefFacts.Walk) == 0) continue;
                walk++; float m = f.Open[i] / 255f * ReliefFacts.OpenRange;
                if (m >= 2) w2++; if (m >= 6) w6++; if (m >= 10) w10++; if (m >= 20) w20++; if (m >= 50) w50++;
            }
        Console.WriteLine($"walkable positions {walk}: >= 2 m from placed things {100.0 * w2 / Math.Max(1, walk):F0} %, >= 6 m {100.0 * w6 / Math.Max(1, walk):F0} %, >= 10 m {100.0 * w10 / Math.Max(1, walk):F0} %, >= 20 m {100.0 * w20 / Math.Max(1, walk):F0} %, >= 50 m {100.0 * w50 / Math.Max(1, walk):F0} %");
        return bad == 0 ? 0 : 2;
    }

    /// <summary>
    /// <c>relief audit [--box cx cz half] [--near x z]... [--settings json]</c> (metres): builds the far mesh of every cell (and the
    /// near part round each --near point) with the shipped settings and the seams; prints triangles, memory, build time, the
    /// visual-vs-collision gap (walkable faces: max / mean; all faces: max) and the open edges of the union inside the box
    /// (source vs relief: equal = no new cracks).
    /// </summary>
    static int ReliefAudit(string data, string[] args)
    {
        float bx = 635, bz = 585, bh = 300;
        var nearXZ = new List<(float x, float z)>();
        string json = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--box") { bx = F(args[++i]); bz = F(args[++i]); bh = F(args[++i]); }
            else if (args[i] == "--near") { float x = F(args[++i]); nearXZ.Add((x, F(args[++i]))); }
            else if (args[i] == "--settings") json = File.ReadAllText(args[++i]);
        }
        static float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);
        var s = ReliefSettings.Default;
        if (json != null) s = System.Text.Json.JsonSerializer.Deserialize<ReliefSettings>(json, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }) ?? s;
        s = s.Clamped();
        s = WithDunes(s, args);
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cells = LoadLandscapeCells(fs).Select(c => (c.name, ToRelief(c.mesh))).ToList();
        Console.WriteLine($"loaded {cells.Count} cells in {sw.Elapsed.TotalSeconds:F1} s");
        bool local = args.Contains("--local");
        var facts = local ? null : ReliefWorldFacts(cells, s, args.Contains("--no-open") ? null : ReliefOpenField(fs, cells));
        sw.Restart();
        for (int i = 0; i < cells.Count; i++) TerrainRelief.Prepare(cells[i].Item2, s, facts?[i].facts);
        double prepMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"facts {(local ? "per cell (--local)" : "world-wide")}; prepare {prepMs:F0} ms ({prepMs / cells.Count:F1} ms per cell)");

        long srcTris = 0, farTris = 0, farVerts = 0, srcVerts = 0;
        double maxAll = 0, maxWalk = 0, sumWalk = 0; long nWalk = 0;
        var hist = new long[64]; V3 maxAt = default; string maxCell = "";
        double farMs = 0, worstMs = 0; string worst = "";
        var fars = new Dictionary<int, ReliefOutput>();
        bool InBox(ReliefMesh m) => m.BoundsMin.X <= bx + bh && m.BoundsMax.X >= bx - bh && m.BoundsMin.Z <= bz + bh && m.BoundsMax.Z >= bz - bh;
        var byRegion = new SortedDictionary<string, (long src, long far)>();
        void Gap(ReliefOutput o, string cell)
        {
            for (int i = 0; i < 64; i++) hist[i] += o.WalkHistogram[i];
            if (o.MaxOffsetWalk > maxWalk) { maxAt = o.MaxWalkAt; maxCell = cell; }
            maxAll = Math.Max(maxAll, o.MaxOffsetAll); maxWalk = Math.Max(maxWalk, o.MaxOffsetWalk);
            sumWalk += o.SumOffsetWalk; nWalk += o.CountWalk;
        }
        for (int ci = 0; ci < cells.Count; ci++)
        {
            var (name, m) = cells[ci];
            var o = new ReliefOutput();
            var t0 = sw.Elapsed.TotalMilliseconds;
            TerrainRelief.BuildFar(m, s, o);
            double ms = sw.Elapsed.TotalMilliseconds - t0;
            farMs += ms; if (ms > worstMs) { worstMs = ms; worst = name; }
            srcTris += m.TriCount; srcVerts += m.Pos.Length;
            long ft = o.IndexCount / 3; farTris += ft; farVerts += o.VertexCount;
            string region = name.Contains("Nordmar") ? "Nordmar" : name.Contains("Varant") ? "Varant" : "Myrtana";
            byRegion.TryGetValue(region, out var rv); byRegion[region] = (rv.src + m.TriCount, rv.far + ft);
            Gap(o, name);
            if (InBox(m)) fars[ci] = o;
        }
        Console.WriteLine($"source: {srcTris} tris, {srcVerts} verts; far: {farTris} tris (x{(double)farTris / srcTris:F2}), {farVerts} verts; " +
                          $"far meshes {(farVerts * 68 + farTris * 12) / 1048576.0:F0} MB if every cell were loaded (68 B/vertex, 32-bit indices)");
        foreach (var kv in byRegion) Console.WriteLine($"  {kv.Key}: {kv.Value.src} -> {kv.Value.far} tris (x{(double)kv.Value.far / Math.Max(1, kv.Value.src):F2})");
        Console.WriteLine($"far build {farMs:F0} ms total, {farMs / cells.Count:F1} ms per cell, worst {worstMs:F0} ms ({worst})");

        var nears = new Dictionary<int, ReliefOutput>();
        bool firstPoint = true;
        foreach (var (x, z) in nearXZ)
        {
            float y = 0, bestD = float.MaxValue;
            foreach (var (_, m) in cells)
            {
                if (x < m.BoundsMin.X || x > m.BoundsMax.X || z < m.BoundsMin.Z || z > m.BoundsMax.Z) continue;
                foreach (var p in m.WPos)
                {
                    float d = (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z);
                    if (d < bestD) { bestD = d; y = p.Y; }
                }
            }
            var pt = new ReliefPoint(new V3(x, y + 2, z), s.nearSpacing, s.nearSlope, true, float.MaxValue, s.nearMargin);   // fix-171: with the near margin, as the runtime
            long nt = 0, nvx = 0, removed = 0; double ms = 0; int ncell = 0;
            for (int ci = 0; ci < cells.Count; ci++)
            {
                var o = new ReliefOutput();
                var t0 = sw.Elapsed.TotalMilliseconds;
                int k = TerrainRelief.BuildNear(cells[ci].Item2, new[] { pt }, 1, s, o);
                ms += sw.Elapsed.TotalMilliseconds - t0;
                if (k == 0) continue;
                ncell++; nt += o.IndexCount / 3; nvx += o.VertexCount;
                var tmpFar = new ReliefOutput(); TerrainRelief.BuildFar(cells[ci].Item2, s, tmpFar);
                for (int t = 0; t < cells[ci].Item2.TriCount; t++) if (o.Near[t]) removed += tmpFar.TriCount[t] / 3;
                Gap(o, cells[ci].Item1);
                if (firstPoint) nears[ci] = o;
            }
            firstPoint = false;
            Console.WriteLine($"near at ({pt.Position.X:F0},{pt.Position.Y:F0},{pt.Position.Z:F0}): {ncell} cells, {nt} tris, {nvx} verts (+{(nvx * 68 + nt * 12) / 1048576.0:F1} MB), " +
                              $"replaces {removed} far tris, build {ms:F0} ms (one thread, .NET)");
        }
        string Pct(double q)
        {
            long tot = hist.Sum(), acc = 0;
            for (int i = 0; i < 64; i++) { acc += hist[i]; if (acc >= q * tot) return i == 63 ? ">=63" : $"<{i + 1}"; }
            return "-";
        }
        long over5 = hist.Skip(5).Sum(), over10 = hist.Skip(10).Sum();
        Console.WriteLine($"gap visual-collision on walkable faces (G3 limit 49 deg): mean {(nWalk > 0 ? sumWalk / nWalk * 100 : 0):F2} cm, p50 {Pct(0.5)} p99 {Pct(0.99)} p99.9 {Pct(0.999)} cm, " +
                          $"> 5 cm {over5} ({100.0 * over5 / Math.Max(1, nWalk):F3} %), > 10 cm {over10}, max {maxWalk * 100:F1} cm at ({maxAt.X:F1},{maxAt.Y:F1},{maxAt.Z:F1}) {maxCell}; all faces max {maxAll * 100:F0} cm");

        var edges = new HashSet<(V3, V3)>();
        var srcEdges = new HashSet<(V3, V3)>();
        foreach (var kv in fars)
        {
            var m = cells[kv.Key].Item2; var f = kv.Value;
            nears.TryGetValue(kv.Key, out var n);
            var comb = Enumerable.Range(0, m.Sub.Length).Select(_ => new List<int>()).ToArray();
            TerrainRelief.Combine(m, f, n, comb);
            ReliefVertex Vx(int i) => i < f.VertexCount ? f.Vertices[i] : n.Vertices[i - f.VertexCount];
            foreach (var l in comb)
                for (int i = 0; i + 2 < l.Count; i += 3)
                    for (int k = 0; k < 3; k++) edges.Add((Vx(l[i + k]).Position, Vx(l[i + (k + 1) % 3]).Position));
            foreach (var tri in m.Sub)
                for (int i = 0; i + 2 < tri.Length; i += 3)
                    for (int k = 0; k < 3; k++) srcEdges.Add((m.Pos[tri[i + k]], m.Pos[tri[i + (k + 1) % 3]]));
        }
        // open edge length (m): the outer rim + G3's own holes; equal lengths = the relief opened no new gap
        double OpenLength(HashSet<(V3, V3)> set)
        {
            double len = 0;
            foreach (var e in set) if (!set.Contains((e.Item2, e.Item1))) len += V3.Distance(new V3(e.Item1.X, 0, e.Item1.Z), new V3(e.Item2.X, 0, e.Item2.Z));
            return len;
        }
        Console.WriteLine($"box ({bx},{bz}) +-{bh} m: {fars.Count} cells; open edge length (XZ) source {OpenLength(srcEdges):F0} m, relief{(nears.Count > 0 ? " far+near" : " far")} {OpenLength(edges):F0} m");
        // where are the relief's open edges that the source does not have? (sample, by owning cell)
        var srcOpenPts = new HashSet<(int, int)>();
        foreach (var e in srcEdges)
            if (!srcEdges.Contains((e.Item2, e.Item1)))
                for (int k = 0; k <= 8; k++)
                {
                    var q = V3.Lerp(e.Item1, e.Item2, k / 8f);
                    srcOpenPts.Add(((int)Math.Floor(q.X), (int)Math.Floor(q.Z)));
                }
        int extra = 0; double extraLen = 0;
        var samples = new List<string>();
        foreach (var e in edges)
        {
            if (edges.Contains((e.Item2, e.Item1))) continue;
            var mid = 0.5f * (e.Item1 + e.Item2);
            bool nearSrc = false;
            for (int dx = -1; dx <= 1 && !nearSrc; dx++)
                for (int dz = -1; dz <= 1 && !nearSrc; dz++)
                    nearSrc = srcOpenPts.Contains(((int)Math.Floor(mid.X) + dx, (int)Math.Floor(mid.Z) + dz));
            if (nearSrc) continue;
            extra++; extraLen += V3.Distance(e.Item1, e.Item2);
            if (samples.Count < 6)
            {
                // the closest other open edge (a near miss = float mismatch; none = a missing triangle)
                double best = double.MaxValue; (V3, V3) bestE = default;
                foreach (var f in edges)
                {
                    if (f.Equals(e) || edges.Contains((f.Item2, f.Item1))) continue;
                    double d = V3.Distance(f.Item1, e.Item2) + V3.Distance(f.Item2, e.Item1);
                    if (d < best) { best = d; bestE = f; }
                }
                string owner = "";
                for (int ci = 0; ci < cells.Count; ci++)
                {
                    var cm = cells[ci].Item2;
                    if (mid.X >= cm.BoundsMin.X - 0.1f && mid.X <= cm.BoundsMax.X + 0.1f && mid.Z >= cm.BoundsMin.Z - 0.1f && mid.Z <= cm.BoundsMax.Z + 0.1f) owner += cells[ci].Item1.Replace("G3_", "").Replace("_Landscape_Cell", "") + " ";
                }
                samples.Add($"({mid.X:F2},{mid.Y:F2},{mid.Z:F2}) len {V3.Distance(e.Item1, e.Item2):F3} nearest reverse {best:F4} m [{bestE.Item1} {bestE.Item2}] vs [{e.Item1} {e.Item2}] cells {owner}");
            }
        }
        Console.WriteLine($"open relief edges away from any source open edge: {extra} ({extraLen:F1} m)" + (samples.Count > 0 ? ": " + string.Join("; ", samples) : ""));
        if (Environment.GetEnvironmentVariable("RELIEF_DEBUG_TRI") is string dbg)
        {
            var q = dbg.Split(',').Select(F).ToArray();
            var probe = new V3(q[0], 0, q[1]);
            foreach (var kv in fars)
            {
                var m = cells[kv.Key].Item2;
                for (int t = 0; t < m.TriCount; t++)
                {
                    var c = m.TriSphere[t];
                    if (Math.Abs(c.X - probe.X) > 2 || Math.Abs(c.Z - probe.Z) > 2) continue;
                    var e0 = m.TriEdge[t * 3]; var e1 = m.TriEdge[t * 3 + 1]; var e2 = m.TriEdge[t * 3 + 2];
                    Console.WriteLine($"{cells[kv.Key].Item1} tri {t} sub {m.TriSub[t]} centre ({c.X:F2},{c.Y:F2},{c.Z:F2}) r {c.W:F2} splits {m.EdgeFar[e0]},{m.EdgeFar[e1]},{m.EdgeFar[e2]} " +
                                      $"emitted {kv.Value.TriCount[t] / 3} tris; verts {m.Pos[m.TriV[t * 3]]} {m.Pos[m.TriV[t * 3 + 1]]} {m.Pos[m.TriV[t * 3 + 2]]}");
                }
            }
        }
        return 0;
    }

    /// <summary><c>relief spots</c>: path points with the most relief round them (road through hills candidates), per region.</summary>
    static int ReliefSpots(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var cells = LoadLandscapeCells(fs);
        var pts = new List<(V3 p, string cell, bool path)>();
        foreach (var (name, mr) in cells)
        {
            var m = ToRelief(mr);
            for (int sm = 0; sm < m.Sub.Length; sm++)
            {
                string mat = Path.GetFileNameWithoutExtension(mr.Elements[sm].Material ?? "");
                bool path = mat.IndexOf("Path", StringComparison.OrdinalIgnoreCase) >= 0;
                var t = m.Sub[sm];
                for (int i = 0; i < t.Length; i += 9) pts.Add((m.Pos[t[i]], name, path));
            }
        }
        var grid = new Dictionary<(int, int), List<V3>>();
        foreach (var q in pts) { var k = ((int)Math.Floor(q.p.X / 20), (int)Math.Floor(q.p.Z / 20)); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<V3>(); l.Add(q.p); }
        var scored = new List<(double score, V3 p, string cell)>();
        foreach (var q in pts)
        {
            if (!q.path) continue;
            float lo = q.p.Y, hi = q.p.Y;
            int gx = (int)Math.Floor(q.p.X / 20), gz = (int)Math.Floor(q.p.Z / 20);
            for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
                if (grid.TryGetValue((gx + dx, gz + dz), out var l)) foreach (var o in l) { if (V2.Distance(new V2(o.X, o.Z), new V2(q.p.X, q.p.Z)) < 45) { lo = Math.Min(lo, o.Y); hi = Math.Max(hi, o.Y); } }
            scored.Add((hi - lo, q.p, q.cell));
        }
        foreach (var region in new[] { "Myrtana", "Nordmar", "Varant" })
        {
            Console.WriteLine(region + ":");
            var taken = new List<V3>();
            foreach (var x in scored.Where(x => x.cell.Contains(region)).OrderByDescending(x => x.score))
            {
                if (taken.Any(t => V3.Distance(t, x.p) < 150)) continue;
                taken.Add(x.p);
                Console.WriteLine($"  path ({x.p.X:F0},{x.p.Y:F0},{x.p.Z:F0}) height range {x.score:F0} m within 45 m ({x.cell})");
                if (taken.Count >= 6) break;
            }
        }
        return 0;
    }

    /// <summary><c>relief find &lt;material part&gt;</c>: centres of the biggest areas of a terrain material (shot spots).</summary>
    static int ReliefFind(string data, string part)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var area = new Dictionary<(int, int), (double a, double y)>();
        foreach (var (name, mr) in LoadLandscapeCells(fs))
            foreach (var el in mr.Elements)
            {
                if ((el.Material ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                for (int i = 0; i + 2 < el.Indices.Length; i += 3)
                {
                    int a = (int)el.Indices[i];
                    float x = el.Positions[a * 3] / 100, y = el.Positions[a * 3 + 1] / 100, z = el.Positions[a * 3 + 2] / 100;
                    var k = ((int)Math.Floor(x / 50), (int)Math.Floor(z / 50));
                    area.TryGetValue(k, out var v); area[k] = (v.a + 1, v.y + y);
                }
            }
        foreach (var kv in area.OrderByDescending(k => k.Value.a).Take(8))
            Console.WriteLine($"({kv.Key.Item1 * 50 + 25},{kv.Value.y / kv.Value.a:F0},{kv.Key.Item2 * 50 + 25}) {kv.Value.a} tris");
        return 0;
    }

    /// <summary>
    /// <c>relief ground --at x z [--at x z]... [--repo dir] [--settings json]</c> (pass 2, Docs/remaster-world.md section 14): with
    /// the camera at each point, builds far + near, rasterises the ground map (<see cref="ReliefGroundMap"/>, 512 x 0.3125 m)
    /// and prints: the visual-minus-collision delta on carrying ground (what characters, grass and corpses are moved by), the
    /// analytic query (<see cref="ReliefGround.SurfaceAt"/>) against the drawn mesh (the map), and the timings.
    /// </summary>
    static int ReliefGroundAudit(string data, string[] args)
    {
        var pts = new List<(float x, float z)>();
        string repo = Directory.GetCurrentDirectory(); string json = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--at") { float x = float.Parse(args[++i], CultureInfo.InvariantCulture); pts.Add((x, float.Parse(args[++i], CultureInfo.InvariantCulture))); }
            else if (args[i] == "--repo") repo = args[++i];
            else if (args[i] == "--settings") json = File.ReadAllText(args[++i]);
        }
        if (pts.Count == 0) pts.Add((635, 585));
        var s = ReliefSettings.Default;
        if (json != null) s = System.Text.Json.JsonSerializer.Deserialize<ReliefSettings>(json, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }) ?? s;
        s = s.Clamped();
        s = WithDunes(s, args);
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var bytes = File.ReadAllBytes(FactsFile(repo));
        var index = ReliefFacts.Index(bytes);
        var all = LoadLandscapeCells(fs).Select(c => (c.name, ToRelief(c.mesh))).ToList();
        var rnd = new Random(7);
        foreach (var (px, pz) in pts)
        {
            var cells = all.Where(c => { var m = c.Item2; float x0 = m.Pos.Min(p => p.X), x1 = m.Pos.Max(p => p.X), z0 = m.Pos.Min(p => p.Z), z1 = m.Pos.Max(p => p.Z);
                                         return x1 >= px - 150 && x0 <= px + 150 && z1 >= pz - 150 && z0 <= pz + 150; }).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float py = 0, best = float.MaxValue;
            foreach (var (name, m) in cells)
            {
                TerrainRelief.Weld(m);
                ReliefShared f = index.TryGetValue(name, out var e) && e.count == m.WeldCount ? ReliefFacts.Decode(bytes, e.offset, e.count) : null;
                TerrainRelief.Prepare(m, s, f);
                foreach (var p in m.WPos) { float d = (p.X - px) * (p.X - px) + (p.Z - pz) * (p.Z - pz); if (d < best) { best = d; py = p.Y; } }
            }
            double prep = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var cam = new[] { new ReliefPoint(new V3(px, py + 2, pz), s.nearSpacing, s.nearSlope) };
            var outs = new List<(ReliefMesh m, ReliefOutput far, ReliefOutput near)>();
            foreach (var (_, m) in cells)
            {
                var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
                var near = new ReliefOutput(); TerrainRelief.BuildNear(m, cam, 1, s, near);
                outs.Add((m, far, near));
            }
            double build = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var map = new ReliefGroundMap(512, 0.3125f);
            map.Begin(px, pz);
            ReliefMapBuild(map, outs);
            double raster = sw.Elapsed.TotalMilliseconds;
            // delta statistics on the map
            var deltas = new List<float>();
            bool Walk(int k)
            {
                int i = k % map.N, j = k / map.N;
                if (i == 0 || j == 0 || i == map.N - 1 || j == map.N - 1) return false;
                float gx = (map.Col[k + 1] - map.Col[k - 1]) / (2 * map.Texel), gz = (map.Col[k + map.N] - map.Col[k - map.N]) / (2 * map.Texel);
                return !float.IsNaN(gx) && !float.IsNaN(gz) && gx * gx + gz * gz < 1.15f * 1.15f;
            }
            for (int k = 0; k < map.Col.Length; k++) if (!float.IsNaN(map.Col[k]) && !float.IsNaN(map.Vis[k]) && Walk(k)) deltas.Add(Math.Abs(map.Vis[k] - map.Col[k]));
            deltas.Sort();
            string P(List<float> l, double q) => l.Count == 0 ? "-" : (l[Math.Min(l.Count - 1, (int)(q * l.Count))] * 100).ToString("F1");
            // the analytic query against the drawn mesh
            var err = new List<float>(); int miss = 0; sw.Restart(); int q = 0;
            for (int n = 0; n < 20000; n++)
            {
                float x = px + (float)(rnd.NextDouble() * 140 - 70), z = pz + (float)(rnd.NextDouble() * 140 - 70);
                if (!map.Sample(x, z, out float mc, out float mv)) continue;
                int kk = (int)Math.Round((z - map.Z0) / map.Texel) * map.N + (int)Math.Round((x - map.X0) / map.Texel);
                if (kk < 0 || kk >= map.Col.Length || !Walk(kk)) continue;
                bool ok = false;
                foreach (var (m, far, near) in outs)
                {
                    q++;
                    if (ReliefGround.SurfaceAt(m, x, z, mc, 0.5f, cam, 1, near.Near, s, out float ac, out float av)) { err.Add(Math.Abs(av - mv)); ok = true; break; }
                }
                if (!ok) miss++;
            }
            double qms = sw.Elapsed.TotalMilliseconds;
            err.Sort();
            Console.WriteLine($"at ({px},{py:F0},{pz}): {cells.Count} cells, prepare {prep:F0} ms, far+near {build:F0} ms, map raster {raster:F0} ms");
            // fix-181: the ground at the point itself and 1 m around it (top layer; the low layer when it differs)
            for (int a = -1; a < 8; a++)
            {
                float qx = a < 0 ? px : px + (float)Math.Sin(a * Math.PI / 4), qz = a < 0 ? pz : pz + (float)Math.Cos(a * Math.PI / 4);
                if (!map.Sample(qx, qz, out float c0, out float v0)) { Console.WriteLine($"  ({qx:F1},{qz:F1}) no ground"); continue; }
                int kq = (int)Math.Round((qz - map.Z0) / map.Texel) * map.N + (int)Math.Round((qx - map.X0) / map.Texel);
                string low = kq >= 0 && kq < map.Low.Length && Math.Abs(map.Low[kq] - map.Col[kq]) > 0.05f ? $" low col {map.Low[kq]:F2} vis {map.VisLow[kq]:F2}" : "";
                Console.WriteLine($"  ({qx:F1},{qz:F1}) col {c0:F2} vis {v0:F2} delta {v0 - c0:+0.00;-0.00}{low}");
            }
            Console.WriteLine($"  |visual - collision| on walkable ground (map, {deltas.Count} pts): p50 {P(deltas, 0.5)} p90 {P(deltas, 0.9)} p99 {P(deltas, 0.99)} max {P(deltas, 1)} cm");
            Console.WriteLine($"  analytic vs drawn mesh ({err.Count} pts, {miss} misses): p50 {P(err, 0.5)} p90 {P(err, 0.9)} p99 {P(err, 0.99)} max {P(err, 1)} cm; {1000.0 * qms / Math.Max(1, q):F1} us per cell query");
        }
        return 0;
    }

    /// <summary>The ground map from far + near outputs (the runtime's worker does the same): collision first, then the drawn triangles.</summary>
    public static void ReliefMapBuild(ReliefGroundMap map, List<(ReliefMesh m, ReliefOutput far, ReliefOutput near)> outs)
    {
        foreach (var (m, far, near) in outs) ReliefMapRaster.Collision(map, m);
        foreach (var (m, far, near) in outs) ReliefMapRaster.Visual(map, m, far, near);
        map.Ready = true;
    }

    /// <summary><c>relief open x z radius [--repo dir]</c>: walkable spots far from placed things (shots of the swells), 20 m buckets.</summary>
    static int ReliefOpenSpots(string data, string[] args)
    {
        float cx = float.Parse(args[2], CultureInfo.InvariantCulture), cz = float.Parse(args[3], CultureInfo.InvariantCulture), r = float.Parse(args[4], CultureInfo.InvariantCulture);
        string repo = Directory.GetCurrentDirectory();
        for (int i = 5; i < args.Length; i++) if (args[i] == "--repo") repo = args[++i];
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var bytes = File.ReadAllBytes(FactsFile(repo)); var index = ReliefFacts.Index(bytes);
        var best = new Dictionary<(int, int), (float open, V3 p, int n)>();
        foreach (var (name, mr) in LoadLandscapeCells(fs))
        {
            var m = ToRelief(mr); TerrainRelief.Weld(m);
            if (!index.TryGetValue(name, out var e) || e.count != m.WeldCount) continue;
            var f = ReliefFacts.Decode(bytes, e.offset, e.count);
            for (int w = 0; w < m.WeldCount; w++)
            {
                var p = m.WPos[w];
                if ((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz) > r * r || (f.Flags[w] & ReliefFacts.Walk) == 0 || f.N[w].Y < 0.85f) continue;
                float o = f.Open[w] / 255f * ReliefFacts.OpenRange;
                var k = ((int)Math.Floor(p.X / 20), (int)Math.Floor(p.Z / 20));
                best.TryGetValue(k, out var b);
                if (b.n == 0 || o < b.open) b.open = b.n == 0 ? o : Math.Min(b.open, o);
                if (b.n == 0) b.p = p;
                b.n++;
                best[k] = b;
            }
        }
        foreach (var kv in best.Where(k => k.Value.n >= 6).OrderByDescending(k => k.Value.open).Take(15))
            Console.WriteLine($"({kv.Key.Item1 * 20 + 10},{kv.Value.p.Y:F0},{kv.Key.Item2 * 20 + 10}) min open {kv.Value.open:F0} m over {kv.Value.n} flat walkable positions");
        return 0;
    }

    static int ReliefCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "";
        switch (sub)
        {
            case "probe": return ReliefProbe(data);
            case "facts": return ReliefWriteFacts(data, args);
            case "audit": return ReliefAudit(data, args);
            case "spots": return ReliefSpots(data);
            case "find": return ReliefFind(data, args.Length > 2 ? args[2] : "Dunes");
            case "ground": return ReliefGroundAudit(data, args);
            case "open": return ReliefOpenSpots(data, args);
            case "clear": return ReliefClearAudit(data, args);   // fix-174
            case "ray": return ReliefRay(data, args);   // fix-174 probe
            case "wander": return ReliefWander(data, args);
            case "pops": return ReliefPops(data, args);   // fix-171
            case "shade": return ReliefShade(data, args);
            default:
                Console.WriteLine("usage: relief probe | relief facts [--repo dir] | relief audit [--box cx cz half] [--near x z]... [--settings json] [--local]");
                return 1;
        }
    }
}
