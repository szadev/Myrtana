using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using G3.Formats.Pak;
using G3.Formats.World;

/// <summary>
/// <c>collision itemsupports worldItems.json [maxSupport m] [-v]</c> (fix-114): every world item whose sight point lies inside
/// a solid collision shape of another entity (the shelf, crate or barrel it lies on or in). Before fix-114 the hero's focus
/// ray (FocusLineOfSight) counted that shape as a blocker, so the item could never be focused or picked up; since fix-114 a
/// solid shape containing the item's centre (any size), or touching its box and not larger than maxSupport (default
/// FocusSight.MaxSupportM), holds the item and does not block. Prints the counts and, with -v, every such item.
/// </summary>
static partial class Program
{
    sealed class ShapeVolume
    {
        public string Entity, Shape;
        public Matrix4x4 World, Inverse;
        public CollisionShape Cs;
        public float SizeM;
        /// <summary>Convex hull: its face planes (normal outwards, d), entity units.</summary>
        public List<(Vector3 n, float d)> Planes;
    }

    static int CollisionItemSupports(string data, string[] args)
    {
        string itemsPath = args[2];
        float maxSupport = args.Length > 3 && !args[3].StartsWith("-") ? float.Parse(args[3], CultureInfo.InvariantCulture) : 4f;
        bool verbose = args.Contains("-v");
        var items = new List<(string id, string item, Vector3 p)>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(itemsPath)))
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var pos = e.GetProperty("position");
                items.Add((e.GetProperty("id").GetString(), e.GetProperty("item").GetString(),
                    new Vector3(pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle()) * 100f)); // G3 cm
            }

        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        var xnv = new Dictionary<string, (G3.Formats.Mesh.CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read(), includeDisabled: false);
        const float Cell = 800f; // cm
        var grid = new Dictionary<(int, int), List<ShapeVolume>>();
        foreach (var sector in layout.Sectors)
        foreach (var path in sector.Nodes.Concat(sector.Lrentdats))
        {
            List<WorldEntity> list;
            try { var entry = projects.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.IsItem || e.IsNpc || !e.Enabled || e.CollisionShapes == null) continue;
                var w = ToMatrix(e.World);
                if (!Matrix4x4.Invert(w, out var inv)) continue;
                float scale = new Vector3(w.M11, w.M12, w.M13).Length();
                foreach (var cs in e.CollisionShapes)
                {
                    if (cs.DisableCollision || cs.IsTrigger || cs.Type == CollisionShapeType.TriMesh || cs.Type == CollisionShapeType.Plane ||
                        cs.Type == CollisionShapeType.Point || cs.Type == CollisionShapeType.None) continue;
                    var v = new ShapeVolume { Entity = e.Name ?? Path.GetFileName(e.Resource ?? ""), Shape = cs.Type.ToString(), World = w, Inverse = inv, Cs = cs };
                    v.SizeM = SizeOf(cs) * scale / 100f;
                    if (cs.Type == CollisionShapeType.ConvexHull)
                    {
                        var (res, _) = LoadAuditXnv(meshFs, cs.File, xnv);
                        var sub = res == null ? null : res.Meshes[Math.Clamp(cs.ResourceIndex, 0, res.Meshes.Count - 1)];
                        if (sub == null || !sub.Convex || sub.Indices == null || sub.Indices.Length < 12) continue;
                        v.Planes = HullPlanes(sub.Positions, sub.Indices, out float size);
                        v.SizeM = size * scale / 100f;
                    }
                    var key = ((int)MathF.Floor(e.X / Cell), (int)MathF.Floor(e.Z / Cell));
                    if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = new List<ShapeVolume>();
                    bucket.Add(v);
                }
            }
        }

        int inside = 0, stillBlocked = 0;
        var byShape = new Dictionary<string, int>();
        foreach (var (id, item, p) in items)
        {
            var probe = p + new Vector3(0, 5f, 0); // 5 cm over the pivot: inside the item, over the board it lies on
            ShapeVolume hit = null;
            int cx = (int)MathF.Floor(p.X / Cell), cz = (int)MathF.Floor(p.Z / Cell);
            for (int dx = -1; dx <= 1 && hit == null; dx++)
            for (int dz = -1; dz <= 1 && hit == null; dz++)
                if (grid.TryGetValue((cx + dx, cz + dz), out var bucket))
                    foreach (var v in bucket)
                        if (Contains(v, probe)) { hit = v; break; }
            if (hit == null) continue;
            inside++;
            // FocusSight.HoldsItem: a solid shape containing the item's centre holds it whatever its size; the size limit is
            // for shapes that only touch the item. So none of these blocks any more; "large" counts the rocks and big boxes.
            bool large = hit.SizeM > maxSupport;
            if (large) stillBlocked++;
            byShape[hit.Shape] = byShape.TryGetValue(hit.Shape, out int n) ? n + 1 : 1;
            if (verbose)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} {1,-32} ({2:F2}, {3:F2}, {4:F2}) in {5} {6} {7:F2} m{8}{9} g{10}",
                    id, item, p.X / 100, p.Y / 100, p.Z / 100, hit.Entity, hit.Shape, hit.SizeM, hit.Cs.IgnoredByTraceRay ? " ignTrace" : "",
                    large ? " large" : "", hit.Cs.Group));
        }
        Console.WriteLine($"world items {items.Count}; inside a solid shape of another entity (blocked before fix-114): {inside} " +
                          $"({string.Join(", ", byShape.Select(kv => kv.Key + " " + kv.Value))}); blocked after: 0 (FocusSight.HoldsItem; {stillBlocked} of them in shapes larger than {maxSupport} m)");
        return 0;
    }

    /// <summary>The outward face planes of a hull mesh (oriented away from its vertex centroid) and its largest extent.</summary>
    static List<(Vector3, float)> HullPlanes(float[] pos, uint[] idx, out float size)
    {
        var c = Vector3.Zero; var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        int nv = pos.Length / 3;
        for (int i = 0; i < nv; i++) { var p = new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]); c += p; min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        c /= nv;
        var e = max - min; size = MathF.Max(e.X, MathF.Max(e.Y, e.Z));
        var planes = new List<(Vector3, float)>();
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            Vector3 P(uint k) => new Vector3(pos[3 * k], pos[3 * k + 1], pos[3 * k + 2]);
            var a = P(idx[t]); var n = Vector3.Cross(P(idx[t + 1]) - a, P(idx[t + 2]) - a);
            if (n.LengthSquared() < 1e-8f) continue;
            n = Vector3.Normalize(n);
            if (Vector3.Dot(n, c - a) > 0) n = -n;
            planes.Add((n, Vector3.Dot(n, a)));
        }
        return planes;
    }

    static Matrix4x4 ToMatrix(float[] m) => new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    /// <summary>The largest extent of a shape, entity units (cm, unscaled).</summary>
    static float SizeOf(CollisionShape cs) => cs.Type switch
    {
        CollisionShapeType.Box => 2 * MathF.Max(cs.Extent[0], MathF.Max(cs.Extent[1], cs.Extent[2])),
        CollisionShapeType.Sphere => 2 * cs.Radius,
        CollisionShapeType.Capsule => cs.Height + 2 * cs.Radius,
        _ => MathF.Max(cs.LocalBounds[3] - cs.LocalBounds[0], MathF.Max(cs.LocalBounds[4] - cs.LocalBounds[1], cs.LocalBounds[5] - cs.LocalBounds[2])),
    };

    static bool Contains(ShapeVolume v, Vector3 world)
    {
        var l = Vector3.Transform(world, v.Inverse);
        var cs = v.Cs;
        var d = l - new Vector3(cs.Center[0], cs.Center[1], cs.Center[2]);
        var ax = new Vector3(cs.Axes[0], cs.Axes[1], cs.Axes[2]);
        var ay = new Vector3(cs.Axes[3], cs.Axes[4], cs.Axes[5]);
        var az = new Vector3(cs.Axes[6], cs.Axes[7], cs.Axes[8]);
        switch (cs.Type)
        {
            case CollisionShapeType.Box:
                return MathF.Abs(Vector3.Dot(d, ax)) <= cs.Extent[0] && MathF.Abs(Vector3.Dot(d, ay)) <= cs.Extent[1] && MathF.Abs(Vector3.Dot(d, az)) <= cs.Extent[2];
            case CollisionShapeType.Sphere:
                return d.Length() <= cs.Radius;
            case CollisionShapeType.Capsule:
            {
                float y = Math.Clamp(Vector3.Dot(d, ay), -cs.Height / 2, cs.Height / 2);
                return (d - ay * y).Length() <= cs.Radius;
            }
            default: // convex hull: inside every face plane
                if (v.Planes == null) return false;
                foreach (var (n, dd) in v.Planes) if (Vector3.Dot(n, l) > dd) return false;
                return true;
        }
    }
}
