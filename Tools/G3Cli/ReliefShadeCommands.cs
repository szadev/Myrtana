using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Unity;
using V3 = System.Numerics.Vector3;

static partial class Program
{
    /// <summary>
    /// <c>relief shade x z [--half m] [--px m] [--cam x y z] [--out prefix] [--repo dir] [--settings json]</c> (fix-178): builds far +
    /// near round the camera (default 25 m above the point, the owner's top-down view) and rasterises the drawn triangles top-down:
    /// <c>prefix_vn.pgm</c> = Lambert of the interpolated vertex normals (what the shader shades), <c>prefix_fn.pgm</c> = Lambert of
    /// the triangles' own face normals (the geometry: facets and creases), <c>prefix_d.pgm</c> = visual - collision height. Prints the
    /// area-weighted angle between face and vertex normals and the dihedral (crease) angles between neighbouring drawn triangles.
    /// </summary>
    static int ReliefShade(string data, string[] args)
    {
        float px = F(args[2]), pz = F(args[3]), half = 40, step = 0.1f;
        V3? camArg = null;
        string prefix = "shade", repo = Directory.GetCurrentDirectory(), json = null;
        for (int i = 4; i < args.Length; i++)
        {
            if (args[i] == "--half") half = F(args[++i]);
            else if (args[i] == "--px") step = F(args[++i]);
            else if (args[i] == "--cam") { float x = F(args[++i]), y = F(args[++i]); camArg = new V3(x, y, F(args[++i])); }
            else if (args[i] == "--out") prefix = args[++i];
            else if (args[i] == "--repo") repo = args[++i];
            else if (args[i] == "--settings") json = File.ReadAllText(args[++i]);
        }
        var s = ReliefSettings.Default;
        if (json != null) s = System.Text.Json.JsonSerializer.Deserialize<ReliefSettings>(json, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }) ?? s;
        s = s.Clamped();
        s = WithDunes(s, args);
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var bytes = File.ReadAllBytes(FactsFile(repo));
        var index = ReliefFacts.Index(bytes);
        float reach = half + 20;
        var cells = LoadLandscapeCells(fs).Select(c => (c.name, m: ToRelief(c.mesh))).Where(c =>
        {
            var m = c.m; float x0 = m.Pos.Min(p => p.X), x1 = m.Pos.Max(p => p.X), z0 = m.Pos.Min(p => p.Z), z1 = m.Pos.Max(p => p.Z);
            return x1 >= px - reach && x0 <= px + reach && z1 >= pz - reach && z0 <= pz + reach;
        }).ToList();
        float py = 0, best = float.MaxValue;
        foreach (var (name, m) in cells)
        {
            TerrainRelief.Weld(m);
            ReliefShared f = index.TryGetValue(name, out var e) && e.count == m.WeldCount ? ReliefFacts.Decode(bytes, e.offset, e.count) : null;
            TerrainRelief.Prepare(m, s, f);
            foreach (var p in m.WPos) { float d = (p.X - px) * (p.X - px) + (p.Z - pz) * (p.Z - pz); if (d < best) { best = d; py = p.Y; } }
        }
        var cam = new[] { new ReliefPoint(camArg ?? new V3(px, py + 25, pz), s.nearSpacing, s.nearSlope) };
        int N = (int)Math.Ceiling(2 * half / step);
        var zb = new float[N * N]; var vn = new float[N * N]; var fn = new float[N * N]; var dd = new float[N * N];
        Array.Fill(zb, float.NegativeInfinity);
        var L = V3.Normalize(new V3(-0.55f, 0.55f, 0.62f));   // a low-ish sun from the south-west
        double angSum = 0, areaSum = 0; var angHist = new double[7]; int dbg = 0; var crease = new double[8]; double creaseLen = 0;
        int tris = 0;
        void Tri(ReliefVertex a, ReliefVertex b, ReliefVertex c, bool isNear)
        {
            var A = a.Position; var B = b.Position; var C = c.Position;
            var fc = V3.Cross(B - A, C - A); float fl = fc.Length(); if (fl < 1e-9f) return;
            var fnrm = fc / fl; if (V3.Dot(fnrm, a.Normal + b.Normal + c.Normal) < 0) fnrm = -fnrm;
            bool walkT = fnrm.Y >= 0.656f;
            float x0 = Math.Min(A.X, Math.Min(B.X, C.X)), x1 = Math.Max(A.X, Math.Max(B.X, C.X));
            float z0 = Math.Min(A.Z, Math.Min(B.Z, C.Z)), z1 = Math.Max(A.Z, Math.Max(B.Z, C.Z));
            if (x1 < px - half || x0 > px + half || z1 < pz - half || z0 > pz + half) return;
            tris++;
            var avg = V3.Normalize(a.Normal + b.Normal + c.Normal);
            double area = fl * 0.5;
            double angv = Math.Acos(Math.Clamp(V3.Dot(avg, fnrm), -1, 1)) * 180 / Math.PI;
            if (walkT && isNear) { angSum += angv * area; areaSum += area; }
            int hb = angv < 2 ? 0 : angv < 5 ? 1 : angv < 10 ? 2 : angv < 20 ? 3 : angv < 45 ? 4 : angv < 90 ? 5 : 6;
            if (walkT && isNear) angHist[hb] += area;
            
            int i0 = Math.Max(0, (int)((x0 - (px - half)) / step)), i1 = Math.Min(N - 1, (int)((x1 - (px - half)) / step) + 1);
            int j0 = Math.Max(0, (int)((z0 - (pz - half)) / step)), j1 = Math.Min(N - 1, (int)((z1 - (pz - half)) / step) + 1);
            float den = (B.Z - C.Z) * (A.X - C.X) + (C.X - B.X) * (A.Z - C.Z);
            if (Math.Abs(den) < 1e-12f) return;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float x = px - half + (i + 0.5f) * step, z = pz - half + (j + 0.5f) * step;
                    float w0 = ((B.Z - C.Z) * (x - C.X) + (C.X - B.X) * (z - C.Z)) / den;
                    float w1 = ((C.Z - A.Z) * (x - C.X) + (A.X - C.X) * (z - C.Z)) / den;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    float y = w0 * A.Y + w1 * B.Y + w2 * C.Y;
                    int k = j * N + i;
                    if (y <= zb[k]) continue;
                    zb[k] = y;
                    var n = V3.Normalize(w0 * a.Normal + w1 * b.Normal + w2 * c.Normal);
                    vn[k] = Math.Max(0, V3.Dot(n, L));
                    fn[k] = Math.Max(0, V3.Dot(fnrm, L));
                    dd[k] = w0 * a.Uv3.X + w1 * b.Uv3.X + w2 * c.Uv3.X;
                }
        }
        int farT = 0, nearT = 0;
        foreach (var (_, m) in cells)
        {
            var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
            var near = new ReliefOutput(); TerrainRelief.BuildNear(m, cam, 1, s, near);
            bool[] skip = near.NearTriangles > 0 ? near.Near : null;
            var drawn = new List<(ReliefVertex, ReliefVertex, ReliefVertex, bool)>();
            for (int t = 0; t < m.TriCount; t++)
            {
                if (skip != null && skip[t]) continue;
                var l = far.Indices[m.TriSub[t]];
                for (int k = far.TriStart[t], e = k + far.TriCount[t]; k < e; k += 3) { drawn.Add((far.Vertices[l[k]], far.Vertices[l[k + 1]], far.Vertices[l[k + 2]], false)); farT++; }
            }
            if (skip != null)
                foreach (var l in near.Indices)
                    for (int k = 0; k + 2 < l.Count; k += 3) { drawn.Add((near.Vertices[l[k]], near.Vertices[l[k + 1]], near.Vertices[l[k + 2]], true)); nearT++; }
            foreach (var (a, b, c, nr) in drawn) Tri(a, b, c, nr);
            // creases: dihedral angle between drawn triangles sharing an edge (by rounded positions), weighted by edge length
            var edges = new Dictionary<(long, long), V3>();
            long Key(V3 p) => ((long)Math.Round(p.X * 500) * 73856093) ^ ((long)Math.Round(p.Y * 500) * 19349663) ^ ((long)Math.Round(p.Z * 500) * 83492791);
            foreach (var (a, b, c, nr) in drawn)
            {
                if (!nr) continue;
                var A = a.Position; var B = b.Position; var C = c.Position;
                if (Math.Abs(A.X - px) > half || Math.Abs(A.Z - pz) > half) continue;
                var fc = V3.Cross(B - A, C - A); float fl = fc.Length(); if (fl < 1e-9f) continue;
                var fnrm = fc / fl;
                if (fnrm.Y < 0.656f && -fnrm.Y < 0.656f) continue;
                foreach (var (P, Q) in new[] { (A, B), (B, C), (C, A) })
                {
                    long kp = Key(P), kq = Key(Q);
                    var key = kp < kq ? (kp, kq) : (kq, kp);
                    if (edges.Remove(key, out var other))
                    {
                        double ang = Math.Acos(Math.Clamp(V3.Dot(other, fnrm), -1, 1)) * 180 / Math.PI;
                        double len = (P - Q).Length();
                        int bin = ang < 1 ? 0 : ang < 2 ? 1 : ang < 4 ? 2 : ang < 8 ? 3 : ang < 16 ? 4 : ang < 32 ? 5 : 6;
                        crease[bin] += len; creaseLen += len;
                    }
                    else edges[key] = fnrm;
                }
            }
        }
        Console.WriteLine($"shade ({px},{py:F1},{pz}) half {half} m, {N}x{N} px of {step} m; cells {cells.Count}; drawn far {farT / 3} near {nearT / 3} tris, {tris} in box");
        Console.WriteLine($"  near walkable: face vs vertex normal (area-weighted mean): {angSum / Math.Max(areaSum, 1e-9):F2} deg");
        Console.WriteLine($"  face vs vertex normal area share <2,<5,<10,<20,<45,<90,>=90: {string.Join(" ", angHist.Select(c => (100 * c / Math.Max(areaSum, 1e-9)).ToString("F1", CultureInfo.InvariantCulture)))} %");
        Console.WriteLine($"  near walkable crease length share by dihedral <1,<2,<4,<8,<16,<32,>=32 deg: {string.Join(" ", crease.Take(7).Select(c => (100 * c / Math.Max(creaseLen, 1e-9)).ToString("F1", CultureInfo.InvariantCulture)))} %");
        void Pgm(string path, float[] v, float lo, float hi)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes($"P5\n{N} {N}\n255\n"));
            for (int j = N - 1; j >= 0; j--)   // north up
                for (int i = 0; i < N; i++)
                {
                    int k = j * N + i;
                    float t = float.IsNegativeInfinity(zb[k]) ? 0 : (v[k] - lo) / (hi - lo);
                    w.Write((byte)Math.Clamp((int)(t * 255), 0, 255));
                }
        }
        Pgm(prefix + "_vn.pgm", vn, 0, 1);
        Pgm(prefix + "_fn.pgm", fn, 0, 1);
        Pgm(prefix + "_d.pgm", dd, -1, 1);
        return 0;
    }
}
