using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Remaster;
using G3.Formats.World;
using G3.Unity;
using V3 = System.Numerics.Vector3;

/// <summary>dune-1: the offline dune bake (Docs/remaster-world.md section 23).</summary>
static partial class Program
{
    /// <summary>The relief settings with the baked dune field of the repo (<c>--repo</c>, else the current dir) unless <c>--no-dunes</c>.</summary>
    static ReliefSettings WithDunes(ReliefSettings s, string[] args)
    {
        if (args.Contains("--no-dunes")) { s.Dunes = null; return s; }
        string repo = Option(args, "--repo") ?? Directory.GetCurrentDirectory();
        var file = DuneFieldFile(repo);
        if (File.Exists(file)) s.Dunes = DuneField.Read(File.ReadAllBytes(file));
        Console.WriteLine(s.Dunes != null ? $"dunes: {file} ({s.Dunes.TileCount} tiles)" : "dunes: none");
        return s;
    }

    static string DuneFieldFile(string repo) => Path.Combine(repo, "Assets/G3/Unity/Runtime/Remaster/Resources/G3DuneField.bytes");

    /// <summary>The bake's inputs on a grid over Varant (shared by bake and audit).</summary>
    sealed class DuneInputs
    {
        public DuneShape.Grid Grid;
        public List<DuneProtect> Protect = new List<DuneProtect>();
        public float SeaY;
    }

    static DuneInputs DuneRaster(G3FileSystem fs, float res = 1f)
    {
        var all = LoadLandscapeCells(fs);
        var varant = all.Where(c => c.name.IndexOf("Varant", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        float x0 = 1e9f, z0 = 1e9f, x1 = -1e9f, z1 = -1e9f;
        foreach (var (_, m) in varant)
            foreach (var el in m.Elements)
                for (int i = 0; i < el.VertexCount; i++)
                {
                    float x = el.Positions[i * 3] / 100, z = el.Positions[i * 3 + 2] / 100;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
                }
        x0 = (float)Math.Floor(x0); z0 = (float)Math.Floor(z0);
        int nx = (int)Math.Ceiling((x1 - x0) / res) + 1, nz = (int)Math.Ceiling((z1 - z0) / res) + 1;
        int n = nx * nz;
        var top = new float[n]; var low = new float[n]; var S = new float[n]; var ny = new float[n];
        var masked = new bool[n];
        for (int k = 0; k < n; k++) { top[k] = float.NaN; low[k] = float.NaN; ny[k] = 1; }
        long tris = 0;
        foreach (var (name, m) in all)
        {
            var e0 = m.Elements.FirstOrDefault(e => e.VertexCount > 0);
            if (e0 == null) continue;
            float fx = e0.Positions[0] / 100, fz = e0.Positions[2] / 100;
            if (fx < x0 - 400 || fx > x1 + 400 || fz < z0 - 400 || fz > z1 + 400) continue;
            foreach (var el in m.Elements)
            {
                string mat = Path.GetFileNameWithoutExtension(el.Material ?? "");
                bool mask = ReliefMaterials.Mask(mat).amp <= 0;
                for (int t = 0; t + 2 < el.Indices.Length; t += 3)
                {
                    int a = (int)el.Indices[t], b = (int)el.Indices[t + 1], c = (int)el.Indices[t + 2];
                    var pa = new V3(el.Positions[a * 3], el.Positions[a * 3 + 1], el.Positions[a * 3 + 2]) / 100;
                    var pb = new V3(el.Positions[b * 3], el.Positions[b * 3 + 1], el.Positions[b * 3 + 2]) / 100;
                    var pc = new V3(el.Positions[c * 3], el.Positions[c * 3 + 1], el.Positions[c * 3 + 2]) / 100;
                    float aa = el.Diffuse != null ? (el.Diffuse[a] >> 24) / 255f : 0, ab = el.Diffuse != null ? (el.Diffuse[b] >> 24) / 255f : 0;
                    float ac = el.Diffuse != null ? (el.Diffuse[c] >> 24) / 255f : 0;
                    var fn = V3.Cross(pb - pa, pc - pa);
                    float fl = fn.Length();
                    if (fl < 1e-6f) continue;
                    float fny = Math.Abs(fn.Y) / fl;
                    float det = (pb.X - pa.X) * (pc.Z - pa.Z) - (pc.X - pa.X) * (pb.Z - pa.Z);
                    if (Math.Abs(det) < 1e-6f) continue;   // vertical face
                    int i0 = Math.Max(0, (int)Math.Ceiling((Math.Min(pa.X, Math.Min(pb.X, pc.X)) - x0) / res));
                    int i1 = Math.Min(nx - 1, (int)Math.Floor((Math.Max(pa.X, Math.Max(pb.X, pc.X)) - x0) / res));
                    int j0 = Math.Max(0, (int)Math.Ceiling((Math.Min(pa.Z, Math.Min(pb.Z, pc.Z)) - z0) / res));
                    int j1 = Math.Min(nz - 1, (int)Math.Floor((Math.Max(pa.Z, Math.Max(pb.Z, pc.Z)) - z0) / res));
                    if (i0 > i1 || j0 > j1) continue;
                    tris++;
                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                        {
                            float px = x0 + i * res, pz = z0 + j * res;
                            float wb = ((px - pa.X) * (pc.Z - pa.Z) - (pc.X - pa.X) * (pz - pa.Z)) / det;
                            float wc = ((pb.X - pa.X) * (pz - pa.Z) - (px - pa.X) * (pb.Z - pa.Z)) / det;
                            float wa = 1 - wb - wc;
                            const float eps = -1e-5f;
                            if (wa < eps || wb < eps || wc < eps) continue;
                            float y = wa * pa.Y + wb * pb.Y + wc * pc.Y;
                            int k = j * nx + i;
                            if (float.IsNaN(low[k]) || y < low[k]) low[k] = y;
                            if (float.IsNaN(top[k]) || y > top[k])
                            {
                                top[k] = y;
                                S[k] = DuneShape.SandWeight(mat, wa * aa + wb * ab + wc * ac);
                                ny[k] = fny;
                                masked[k] = mask;
                            }
                        }
                }
            }
        }
        var inp = new DuneInputs { Grid = new DuneShape.Grid { Nx = nx, Nz = nz, X0 = x0, Z0 = z0, Res = res, H = top, S = S } };
        var maskedG = new bool[n]; var nonSand = new bool[n]; var steep = new bool[n]; var over = new bool[n];
        long cover = 0;
        for (int k = 0; k < n; k++)
        {
            if (float.IsNaN(top[k])) continue;
            cover++;
            maskedG[k] = masked[k];
            nonSand[k] = S[k] < 0.25f;
            steep[k] = ny[k] < 0.72f;
            over[k] = top[k] - low[k] > 2.5f;
        }
        Console.WriteLine($"raster: grid {nx}x{nz} at {res} m from ({x0},{z0}), {tris} triangles, terrain cover {100.0 * cover / n:F0} %");

        // entities: every enabled placed thing (nodes and usable objects), by kind
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var build = new bool[n]; var rock = new bool[n]; var plant = new bool[n]; var spot = new bool[n]; var water = new bool[n];
        int nBuild = 0, nRock = 0, nPlant = 0, nSpot = 0, nWater = 0;
        float seaY = float.NaN;
        void Mark(bool[] g, float ax, float az, float bx, float bz)
        {
            int ia = Math.Max(0, (int)Math.Floor((ax - x0) / res)), ib = Math.Min(nx - 1, (int)Math.Ceiling((bx - x0) / res));
            int ja = Math.Max(0, (int)Math.Floor((az - z0) / res)), jb = Math.Min(nz - 1, (int)Math.Ceiling((bz - z0) / res));
            for (int j = ja; j <= jb; j++) for (int i = ia; i <= ib; i++) g[j * nx + i] = true;
        }
        var seen = new HashSet<Guid>();
        foreach (var sec in layout.Sectors)
        foreach (var path in sec.Nodes.Concat(sec.Lrentdats))
        {
            List<WorldEntity> list;
            try { var entry = fs.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read()); }
            catch (Exception) { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (!e.Enabled) continue;
                if (e.Guid != Guid.Empty && !seen.Add(e.Guid)) continue;
                float ex = e.X / 100, ez = e.Z / 100;
                if (ex < x0 - 20 || ex > x1 + 20 || ez < z0 - 20 || ez > z1 + 20) continue;
                if (e.Visual == VisualKind.Mesh && WorldRemasterCategories.IsTerrainMesh(e.Resource)) continue;
                var b = e.Bounds;
                float bx0 = b[0] / 100, bz0 = b[2] / 100, bx1 = b[3] / 100, bz1 = b[5] / 100;
                bool boundsOk = bx1 >= bx0 && bz1 >= bz0 && Math.Abs((bx0 + bx1) / 2 - ex) < 60 && Math.Abs((bz0 + bz1) / 2 - ez) < 60 && bx1 - bx0 < 400 && bz1 - bz0 < 400;
                string cat = e.Visual == VisualKind.Mesh && !string.IsNullOrEmpty(e.Resource) ? WorldRemasterCategories.Classify(e.Resource) : null;
                if (e.IsWater || cat == WorldRemasterCategories.Water)
                {
                    if ((e.Resource ?? "").IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0) seaY = float.IsNaN(seaY) ? e.Y / 100 : Math.Max(seaY, e.Y / 100);
                    if (boundsOk) { Mark(water, bx0, bz0, bx1, bz1); nWater++; }
                    continue;
                }
                if (e.IsNpc || e.IsItem || e.Visual == VisualKind.None || string.IsNullOrEmpty(e.Resource))
                {
                    Mark(spot, ex - 0.5f, ez - 0.5f, ex + 0.5f, ez + 0.5f); nSpot++;
                    continue;
                }
                if (e.Visual == VisualKind.SpeedTree) { Mark(plant, ex - 0.75f, ez - 0.75f, ex + 0.75f, ez + 0.75f); nPlant++; continue; }
                if (!boundsOk) { bx0 = ex - 2; bz0 = ez - 2; bx1 = ex + 2; bz1 = ez + 2; }
                if (cat == WorldRemasterCategories.Rock) { Mark(rock, bx0, bz0, bx1, bz1); nRock++; }
                else if (cat == WorldRemasterCategories.Plant || cat == WorldRemasterCategories.Grass) { Mark(plant, bx0, bz0, bx1, bz1); nPlant++; }
                else { Mark(build, bx0, bz0, bx1, bz1); nBuild++; }
                Mark(spot, ex - 0.5f, ez - 0.5f, ex + 0.5f, ez + 0.5f);   // its origin (use spots, ladders, chests) as well
            }
        }
        if (float.IsNaN(seaY)) seaY = 0;
        var shore = new bool[n];
        for (int k = 0; k < n; k++) shore[k] = !float.IsNaN(top[k]) && top[k] < seaY + 1.5f;
        Console.WriteLine($"entities: buildings/props {nBuild}, rocks {nRock}, plants/trees {nPlant}, npc/items/spots {nSpot}, water {nWater}; sea level {seaY:F1} m");
        inp.SeaY = seaY;
        inp.Protect.Add(new DuneProtect("masked ground", maskedG, 4, 14));
        inp.Protect.Add(new DuneProtect("non-sand", nonSand, 1, 6));
        inp.Protect.Add(new DuneProtect("steep", steep, 1, 5));
        inp.Protect.Add(new DuneProtect("overhang", over, 3, 10));
        inp.Protect.Add(new DuneProtect("buildings/props", build, 3, 12));
        inp.Protect.Add(new DuneProtect("rocks", rock, 1.5f, 6));
        inp.Protect.Add(new DuneProtect("plants/trees", plant, 0.75f, 3.5f));
        inp.Protect.Add(new DuneProtect("npc/items/origins", spot, 2.5f, 8));
        inp.Protect.Add(new DuneProtect("water", water, 4, 14));
        inp.Protect.Add(new DuneProtect("shore", shore, 2, 10));
        return inp;
    }

    /// <summary><c>dune bake [--repo dir] [--png dir]</c>: Varant's dune field into the runtime Resources (G3DuneField.bytes).</summary>
    static int DuneBake(string data, string[] args)
    {
        string repo = Option(args, "--repo") ?? Directory.GetCurrentDirectory();
        string png = Option(args, "--png");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var inp = DuneRaster(fs);
        Console.WriteLine($"inputs {sw.ElapsedMilliseconds} ms");
        var g = inp.Grid;
        var s = new DuneBakeSettings();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--set")
            {
                var kv = args[i + 1].Split('=');
                var fld = typeof(DuneBakeSettings).GetField(kv[0]);
                if (fld == null) { Console.WriteLine("unknown setting " + kv[0]); return 1; }
                fld.SetValue(s, Convert.ChangeType(kv[1], fld.FieldType, System.Globalization.CultureInfo.InvariantCulture));
            }
        var d = new float[g.Count];
        var field = DuneShape.Build(g, inp.Protect, s, out var stats, d);
        Console.WriteLine($"shape {sw.ElapsedMilliseconds} ms: {stats}");
        var file = DuneFieldFile(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        using (var st = File.Create(file)) field.Write(st);
        Console.WriteLine($"-> {file}: {field.TileCount} tiles of {field.TilesX * field.TilesZ}, {new FileInfo(file).Length / 1048576.0:F1} MB file, {field.Bytes / 1048576.0:F1} MB in memory");
        var back = DuneField.Read(File.ReadAllBytes(file));
        double err = 0;
        var rnd = new Random(1);
        for (int q = 0; q < 200000; q++)
        {
            float x = g.X0 + (float)rnd.NextDouble() * g.Nx, z = g.Z0 + (float)rnd.NextDouble() * g.Nz;
            field.Sample(x, z, out var s1); back.Sample(x, z, out var s2);
            err = Math.Max(err, Math.Abs(s1.C + s1.B - s2.C - s2.B) + Math.Abs(s1.Gx - s2.Gx) + Math.Abs(s1.W - s2.W));
        }
        Console.WriteLine($"round trip max diff {err:E2}");
        if (png != null) DunePngs(png, g, d, Option(args, "--box"), field);
        var line = Option(args, "--line");
        if (line != null)
        {
            var v = line.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            double az = s.WindAzimuth * Math.PI / 180;
            float wx = (float)Math.Sin(az), wz = (float)Math.Cos(az);
            using var csv = new StreamWriter(Path.Combine(png ?? ".", $"line_{v[0]:F0}_{v[1]:F0}.csv"));
            csv.WriteLine("t,h,d");
            for (float t = -v[2]; t <= v[2]; t += 0.5f)
            {
                float x = v[0] + t * wx, z = v[1] + t * wz;
                float fi = (x - g.X0) / g.Res, fj = (z - g.Z0) / g.Res;
                float h = DuneShape.Bilinear(g.H, g.Nx, g.Nz, fi, fj), dd = DuneShape.Bilinear(d, g.Nx, g.Nz, fi, fj);
                csv.WriteLine(FormattableString.Invariant($"{t},{h},{dd}"));
            }
        }
        return 0;
    }

    /// <summary>Overview images: hillshade of G3 (A) and of G3 + dunes (B), low sun from the west; the change (red up, blue down).</summary>
    static void DunePngs(string dir, DuneShape.Grid g, float[] d, string box, DuneField field)
    {
        Directory.CreateDirectory(dir);
        int step = 4, ia = 0, ja = 0, ib = g.Nx, jb = g.Nz;
        string tag = "";
        if (box != null)
        {
            var v = box.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            ia = Math.Max(0, (int)((v[0] - v[2] - g.X0) / g.Res)); ib = Math.Min(g.Nx, (int)((v[0] + v[2] - g.X0) / g.Res));
            ja = Math.Max(0, (int)((v[1] - v[2] - g.Z0) / g.Res)); jb = Math.Min(g.Nz, (int)((v[1] + v[2] - g.Z0) / g.Res));
            step = 1; tag = $"_{v[0]:F0}_{v[1]:F0}";
        }
        int w = (ib - ia) / step, h = (jb - ja) / step;
        var a = new byte[w * h * 4]; var b = new byte[w * h * 4]; var c = new byte[w * h * 4];
        float Hs(int i, int j, bool dunes)
        {
            i = Math.Max(0, Math.Min(g.Nx - 1, i)); j = Math.Max(0, Math.Min(g.Nz - 1, j));
            float y = g.H[j * g.Nx + i];
            if (float.IsNaN(y)) return 0;
            return dunes ? y + d[j * g.Nx + i] : y;
        }
        var sun = V3.Normalize(new V3(-0.8f, 0.35f, -0.3f));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = ia + x * step, j = ja + (h - 1 - y) * step;   // north (+z) up
                float sand = g.S[j * g.Nx + i];
                for (int pass = 0; pass < 2; pass++)
                {
                    bool du = pass == 1;
                    float dx = (Hs(i + 1, j, du) - Hs(i - 1, j, du)) / (2 * g.Res), dz = (Hs(i, j + 1, du) - Hs(i, j - 1, du)) / (2 * g.Res);
                    // B: the shading slope the relief uses on the dunes (blended by W)
                    if (du && field.Sample(g.X0 + i * g.Res, g.Z0 + j * g.Res, out var fs1)) { dx += (fs1.Gx - dx) * fs1.W; dz += (fs1.Gz - dz) * fs1.W; }
                    var nrm = V3.Normalize(new V3(-dx, 1, -dz));
                    float l = Math.Max(0, V3.Dot(nrm, sun));
                    byte v = (byte)Math.Min(255, 30 + 225 * l);
                    var img = du ? b : a;
                    int o = (y * w + x) * 4;
                    img[o] = (byte)(v * (0.75 + 0.25 * sand)); img[o + 1] = (byte)(v * (0.72 + 0.12 * sand)); img[o + 2] = (byte)(v * (0.75 - 0.3 * sand)); img[o + 3] = 255;
                }
                float dv = d[j * g.Nx + i];
                int oc = (y * w + x) * 4;
                c[oc] = (byte)Math.Min(255, 128 + 127 * Math.Min(1, Math.Max(0, dv) / 1.5)); c[oc + 1] = (byte)(128 - Math.Min(127, 85 * Math.Abs(dv)));
                c[oc + 2] = (byte)Math.Min(255, 128 + 127 * Math.Min(1, Math.Max(0, -dv) / 1.5)); c[oc + 3] = 255;
            }
        WritePng(Path.Combine(dir, $"bake{tag}_A_g3.png"), a, w, h);
        WritePng(Path.Combine(dir, $"bake{tag}_B_dunes.png"), b, w, h);
        WritePng(Path.Combine(dir, $"bake{tag}_change.png"), c, w, h);
        Console.WriteLine($"pngs -> {dir} ({w}x{h}, {step * g.Res} m per pixel, north up)");
    }
}
