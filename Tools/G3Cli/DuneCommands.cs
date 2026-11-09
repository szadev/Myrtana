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
    /// <summary><c>dune probe</c>: Varant landscape cells: extent, edge length, materials (dune-1).</summary>
    static int DuneProbe(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        var cells = LoadLandscapeCells(fs).Where(c => c.name.IndexOf("Varant", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var mats = new Dictionary<string, (long tris, double area, double edge, long ne, double alphaSum, long nv)>();
        float x0 = 1e9f, z0 = 1e9f, x1 = -1e9f, z1 = -1e9f;
        foreach (var (name, m) in cells)
            foreach (var el in m.Elements)
            {
                string mat = Path.GetFileNameWithoutExtension(el.Material ?? "");
                mats.TryGetValue(mat, out var v);
                for (int i = 0; i < el.VertexCount; i++)
                {
                    float x = el.Positions[i * 3] / 100, z = el.Positions[i * 3 + 2] / 100;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
                    if (el.Diffuse != null) { v.alphaSum += (el.Diffuse[i] >> 24) / 255.0; v.nv++; }
                }
                for (int t = 0; t + 2 < el.Indices.Length; t += 3)
                {
                    var p = new V3[3];
                    for (int j = 0; j < 3; j++) { int a = (int)el.Indices[t + j]; p[j] = new V3(el.Positions[a * 3], el.Positions[a * 3 + 1], el.Positions[a * 3 + 2]) / 100; }
                    v.tris++; v.area += V3.Cross(p[1] - p[0], p[2] - p[0]).Length() / 2;
                    for (int j = 0; j < 3; j++) { v.edge += V3.Distance(p[j], p[(j + 1) % 3]); v.ne++; }
                }
                mats[mat] = v;
            }
        // slope vs alpha on the sand-to-rock blend: which layer sits on steep faces
        var hist = new double[2, 4]; var cnt = new long[2, 4];
        foreach (var (name, m) in cells)
            foreach (var el in m.Elements)
            {
                if (!(el.Material ?? "").Contains("PlainSand_To_Rock")) continue;
                for (int t = 0; t + 2 < el.Indices.Length; t += 3)
                {
                    var p = new V3[3]; double al = 0;
                    for (int j = 0; j < 3; j++) { int a = (int)el.Indices[t + j]; p[j] = new V3(el.Positions[a * 3], el.Positions[a * 3 + 1], el.Positions[a * 3 + 2]); al += (el.Diffuse[a] >> 24) / 255.0 / 3; }
                    var n = V3.Normalize(V3.Cross(p[1] - p[0], p[2] - p[0]));
                    int sb = Math.Abs(n.Y) > 0.95f ? 0 : Math.Abs(n.Y) > 0.85f ? 1 : Math.Abs(n.Y) > 0.7f ? 2 : 3;
                    int ab = al > 0.5 ? 1 : 0;
                    cnt[ab, sb]++;
                }
            }
        Console.WriteLine($"PlainSand_To_Rock faces by |ny| >.95/.85/.7/steeper: alpha<0.5 {cnt[0,0]} {cnt[0,1]} {cnt[0,2]} {cnt[0,3]}; alpha>=0.5 {cnt[1,0]} {cnt[1,1]} {cnt[1,2]} {cnt[1,3]}");
        Console.WriteLine($"Varant cells {cells.Count}, extent x {x0:F0}..{x1:F0} z {z0:F0}..{z1:F0}");
        foreach (var kv in mats.OrderByDescending(k => k.Value.area))
            Console.WriteLine($"{kv.Key,-60} tris {kv.Value.tris,7} area {kv.Value.area / 1e6:F3} km2 edge {kv.Value.edge / Math.Max(1, kv.Value.ne):F2} m alpha {kv.Value.alphaSum / Math.Max(1, kv.Value.nv):F2}");
        return 0;
    }

    static int DuneCommand(string data, string[] args)
    {
        switch (args.Length > 1 ? args[1] : "")
        {
            case "probe": return DuneProbe(data);
            case "bake": return DuneBake(data, args);
        }
        Console.WriteLine("usage: dune probe");
        return 1;
    }
}
