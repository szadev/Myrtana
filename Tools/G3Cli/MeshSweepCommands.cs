using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.World;

/// <summary>
/// <c>collision meshsweep x y z stem [range]</c> (fix-254): the collision shell and the drawn mesh of the entities with the
/// visual <c>stem</c> within range m of x z, compared by 32 horizontal rays from (x, y, z): the distance to the first
/// collision triangle (front = its stored winding faces the ray, back otherwise) and to the first render triangle. Shows
/// where the drawn mesh is open but the collision is closed (or the other way round).
/// </summary>
static partial class Program
{
    static int MeshSweep(string data, string[] args)
    {
        if (args[2] == "--cmp") return CompareXnv(data, args[3], args[4], float.Parse(args[5], CultureInfo.InvariantCulture));
        float x = float.Parse(args[2], CultureInfo.InvariantCulture), y = float.Parse(args[3], CultureInfo.InvariantCulture),
              z = float.Parse(args[4], CultureInfo.InvariantCulture);
        string stem = args[5];
        float range = args.Length > 6 ? float.Parse(args[6], CultureInfo.InvariantCulture) : 15f;
        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
        var meshes = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var xnv = new Dictionary<string, (CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        var col = new List<float[]>(); var ren = new List<float[]>();
        var o = new float[3];
        float[] Tri(float[] w, float[] p, uint a, uint b, uint c, bool pre)
        {
            var t = new float[9];
            int k = 0;
            foreach (var i in new[] { a, b, c }) { Xf(w, p[i * 3], p[i * 3 + 1], p[i * 3 + 2], pre, o); t[k++] = o[0] * 0.01f; t[k++] = o[1] * 0.01f; t[k++] = o[2] * 0.01f; }
            return t;
        }
        foreach (var sector in layout.Sectors)
        foreach (var path in sector.Nodes.Concat(sector.Lrentdats))
        {
            List<WorldEntity> list;
            try { var entry = projects.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Resource == null || !Path.GetFileNameWithoutExtension(e.Resource).Equals(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (MathF.Abs(e.X / 100 - x) > range || MathF.Abs(e.Z / 100 - z) > range) continue;
                Console.WriteLine($"entity {e.Name} {e.Resource} at ({e.X / 100:F2}, {e.Y / 100:F2}, {e.Z / 100:F2})");
                var (rm, _) = LoadAuditMesh(meshFs, e.Resource, meshes);
                if (rm != null) foreach (var el in rm.Elements) for (int i = 0; i + 2 < el.Indices.Length; i += 3) ren.Add(Tri(e.World, el.Positions, el.Indices[i], el.Indices[i + 1], el.Indices[i + 2], false));
                foreach (var cs in e.CollisionShapes ?? new List<CollisionShape>())
                {
                    if (cs.Type != CollisionShapeType.TriMesh && cs.Type != CollisionShapeType.ConvexHull) continue;
                    var (res, _) = LoadAuditXnv(meshFs, cs.File, xnv);
                    if (res == null) continue;
                    bool pre = CollisionRules.IsPrescaled(cs.File);
                    foreach (var sm in cs.ResourceIndex >= 0 && cs.ResourceIndex < res.Meshes.Count ? new[] { res.Meshes[cs.ResourceIndex] } : res.Meshes.ToArray())
                        for (int i = 0; i + 2 < sm.Indices.Length; i += 3) col.Add(Tri(e.World, sm.Positions, sm.Indices[i], sm.Indices[i + 1], sm.Indices[i + 2], pre));
                }
            }
        }
        Console.WriteLine($"collision triangles {col.Count}, render triangles {ren.Count}");
        foreach (var (nm, l) in new[] { ("collision", col), ("render", ren) })
        {
            if (l.Count == 0) continue;
            float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
            foreach (var t in l) for (int i = 0; i < 9; i++) { mn[i % 3] = Math.Min(mn[i % 3], t[i]); mx[i % 3] = Math.Max(mx[i % 3], t[i]); }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0} box ({1:F2}, {2:F2}, {3:F2}) .. ({4:F2}, {5:F2}, {6:F2})", nm, mn[0], mn[1], mn[2], mx[0], mx[1], mx[2]));
        }
        for (int k = 0; k < 32; k++)
        {
            float a = k * MathF.PI / 16, ux = MathF.Cos(a), uz = MathF.Sin(a);
            var (dc, front) = First(col, x, y, z, ux, 0, uz);
            var (dr, rf) = First(ren, x, y, z, ux, 0, uz);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  dir {0,5:F0}°: collision {1} {2}   render {3} {4}", k * 11.25f,
                dc < 0 ? "  -  " : dc.ToString("F2", CultureInfo.InvariantCulture), dc < 0 ? "" : front ? "front" : "back ",
                dr < 0 ? "  -  " : dr.ToString("F2", CultureInfo.InvariantCulture), dr < 0 ? "" : rf ? "front" : "back"));
        }
        return 0;
    }

    /// <summary><c>--cmp a b scale</c>: does .xnvmsh b hold a's triangles scaled by scale (same order and winding)?</summary>
    static int CompareXnv(string data, string a, string b, float scale)
    {
        using var fs = new G3FileSystem(data, "_compiledPhysic");
        var ma = Xnvmsh.Read(fs.GetByName(a).Read(), a).Meshes[0];
        var mb = Xnvmsh.Read(fs.GetByName(b).Read(), b).Meshes[0];
        Console.WriteLine($"{a}: {ma.VertexCount} v {ma.Indices.Length / 3} t; {b}: {mb.VertexCount} v {mb.Indices.Length / 3} t");
        float maxErr = 0; int flipped = 0, same = 0;
        int n = Math.Min(ma.Indices.Length, mb.Indices.Length) / 3;
        for (int t = 0; t < n; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                uint ia = ma.Indices[t * 3 + k], ib = mb.Indices[t * 3 + k];
                for (int c = 0; c < 3; c++) maxErr = Math.Max(maxErr, Math.Abs(ma.Positions[ia * 3 + c] * scale - mb.Positions[ib * 3 + c]));
            }
            float[] Nrm(CollisionSubMesh m, int tt)
            {
                uint i0 = m.Indices[tt * 3], i1 = m.Indices[tt * 3 + 1], i2 = m.Indices[tt * 3 + 2];
                float e1x = m.Positions[i1 * 3] - m.Positions[i0 * 3], e1y = m.Positions[i1 * 3 + 1] - m.Positions[i0 * 3 + 1], e1z = m.Positions[i1 * 3 + 2] - m.Positions[i0 * 3 + 2];
                float e2x = m.Positions[i2 * 3] - m.Positions[i0 * 3], e2y = m.Positions[i2 * 3 + 1] - m.Positions[i0 * 3 + 1], e2z = m.Positions[i2 * 3 + 2] - m.Positions[i0 * 3 + 2];
                return new[] { e1y * e2z - e1z * e2y, e1z * e2x - e1x * e2z, e1x * e2y - e1y * e2x };
            }
            var na = Nrm(ma, t); var nb = Nrm(mb, t);
            if (na[0] * nb[0] + na[1] * nb[1] + na[2] * nb[2] < 0) flipped++; else same++;
        }
        Console.WriteLine($"max vertex error {maxErr:F2} (units), triangles same winding {same}, flipped {flipped}");
        return 0;
    }

    /// <summary>The nearest triangle along the ray (distance, -1 for none) and whether its stored winding faces the ray.</summary>
    static (float d, bool front) First(List<float[]> tris, float ox, float oy, float oz, float dx, float dy, float dz)
    {
        float best = float.MaxValue; bool bf = false;
        foreach (var t in tris)
        {
            float e1x = t[3] - t[0], e1y = t[4] - t[1], e1z = t[5] - t[2], e2x = t[6] - t[0], e2y = t[7] - t[1], e2z = t[8] - t[2];
            float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            float det = e1x * px + e1y * py + e1z * pz;
            if (MathF.Abs(det) < 1e-12f) continue;
            float inv = 1 / det, sx = ox - t[0], sy = oy - t[1], sz = oz - t[2];
            float u = (sx * px + sy * py + sz * pz) * inv;
            if (u < 0 || u > 1) continue;
            float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            float v = (dx * qx + dy * qy + dz * qz) * inv;
            if (v < 0 || u + v > 1) continue;
            float d = (e2x * qx + e2y * qy + e2z * qz) * inv;
            if (d > 0 && d < best) { best = d; bf = det > 0; }
        }
        return (best == float.MaxValue ? -1 : best, bf);
    }
}
