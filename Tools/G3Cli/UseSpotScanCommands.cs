using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.World;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;

/// <summary>
/// <c>collision usespots [--pack dir] [--top n]</c> (fix-103, owner locked inside a Beliar shrine): every interactive object's
/// hero use spots as HeroObjectUse picks them (its NPC slots; else the slot an object with the same mesh has, carried
/// over; else the kind's distance in front of the render bounds centre, tried in 8 directions round it) tested against
/// the object's own G3 collision (eCCollisionShape_PS meshes, hulls and boxes; a lazy shape uses the mesh's .xnvmsh): the
/// hero's core (<see cref="UseSpotClearance.CoreRadius"/> round his axis, <c>--radius</c>; from step height 0.45 m to
/// 1.95 m) touching a collision triangle or standing inside the closed volume is embedded: G3 poses NPCs with the body
/// against a table or barrel, which the character controller's own overlap recovery resolves; a core inside cannot be. Per kind: objects, spots, embedded spots, objects with one, and how many stay embedded
/// after the runtime rule (<see cref="UseSpotClearance.TryClear"/> against the same collision). Seats and beds are used on
/// the furniture (not moved out at runtime), boulders and animated activities at the object's origin.
/// </summary>
static partial class Program
{
    static float ScanRadius = UseSpotClearance.CoreRadius;
    const float ScanBottom = 0.45f, ScanTop = 1.95f;

    sealed class ScanGeometry
    {
        public readonly List<float[]> Tris = new List<float[]>(); // 9 floats each, metres, Unity axes
        public float MinX = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxZ = float.MinValue;

        public void Add(float[] a, float[] b, float[] c)
        {
            Tris.Add(new[] { a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2] });
            foreach (var p in new[] { a, b, c })
            {
                MinX = Math.Min(MinX, p[0]); MaxX = Math.Max(MaxX, p[0]);
                MinZ = Math.Min(MinZ, p[2]); MaxZ = Math.Max(MaxZ, p[2]);
            }
        }
    }

    static int CollisionUseSpots(string data, string[] args)
    {
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        int top = int.TryParse(Option(args, "--top"), out var t) ? t : 12;
        ScanRadius = float.TryParse(Option(args, "--radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rr) ? rr : UseSpotClearance.CoreRadius;
        var db = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) }).Database;
        var objects = db.All<InteractiveObjectDef>().ToList();
        var points = db.All<RoutinePointDef>().ToDictionary(p => p.Id.Name, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<Guid>(objects.Select(o => Guid.TryParse(o.Id, out var g) ? g : Guid.Empty));

        using var projects = new G3FileSystem(data, "Projects_compiled");
        using var meshFs = new G3FileSystem(data, "_compiledMesh", "_compiledMaterial", "_compiledPhysic");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read(), includeDisabled: true);
        var entities = new Dictionary<Guid, WorldEntity>();
        foreach (var sector in layout.Sectors)
        foreach (var path in sector.Nodes.Concat(sector.Lrentdats))
        {
            List<WorldEntity> list;
            try { var entry = projects.Get(vol + path); list = entry == null ? null : EntityArchive.Read(entry.Read(), false); }
            catch { continue; }
            if (list == null) continue;
            foreach (var e in list) if (wanted.Contains(e.Guid) && !entities.ContainsKey(e.Guid)) entities[e.Guid] = e;
        }

        var meshes = new Dictionary<string, (MeshResource, bool)>(StringComparer.OrdinalIgnoreCase);
        var xnv = new Dictionary<string, (CollisionMeshResource, string)>(StringComparer.OrdinalIgnoreCase);
        // mesh offsets carried over from objects with routine slots (HeroUseSpots)
        var offsets = new Dictionary<string, (float r, float u, float f)>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objects)
            if (o.Visual != null && points.TryGetValue(o.Id, out var rp) && rp.Slots.Count > 0 && !offsets.ContainsKey(o.Visual))
            {
                Frame(o, out var org, out var rt, out var up, out var fw);
                var s = rp.Slots[0];
                var d = new[] { s.X - org[0], s.Y - org[1], s.Z - org[2] };
                offsets[o.Visual] = (Dot(d, rt), Dot(d, up), Dot(d, fw));
            }

        var rows = new SortedDictionary<string, int[]>(StringComparer.Ordinal); // objects, with collision, spots, embedded, objects embedded, still embedded after the rule
        var samples = new Dictionary<string, List<string>>();
        int noEntity = 0;
        foreach (var o in objects)
        {
            string kind = $"{o.Kind}/{o.UseType}";
            if (!rows.TryGetValue(kind, out var row)) rows[kind] = row = new int[6];
            row[0]++;
            if (!Guid.TryParse(o.Id, out var guid) || !entities.TryGetValue(guid, out var e)) { noEntity++; continue; }
            var geo = Geometry(e, meshFs, xnv);
            if (geo.Tris.Count == 0) continue;
            row[1]++;
            var spots = new List<(float x, float y, float z, string how)>();
            if (points.TryGetValue(o.Id, out var rp) && rp.Slots.Count > 0)
                for (int i = 0; i < rp.Slots.Count; i++) spots.Add((rp.Slots[i].X, rp.Slots[i].Y, rp.Slots[i].Z, "slot" + i));
            else if (o.Visual != null && offsets.TryGetValue(o.Visual, out var off))
            {
                Frame(o, out var org, out var rt, out var up, out var fw);
                spots.Add((org[0] + rt[0] * off.r + up[0] * off.u + fw[0] * off.f, org[1] + rt[1] * off.r + up[1] * off.u + fw[1] * off.f,
                           org[2] + rt[2] * off.r + up[2] * off.u + fw[2] * off.f, "mesh offset"));
            }
            else
            {
                float dist = FallbackDistance(o.Kind);
                if (dist > 0f)
                {
                    var c = Centre(o, e, meshFs, meshes);
                    for (int k = 0; k < 8; k++)
                    {
                        double a = k * Math.PI / 4;
                        spots.Add((c[0] + (float)Math.Sin(a) * dist, o.Position[1], c[2] + (float)Math.Cos(a) * dist, $"{dist:0.00} m dir {k * 45}"));
                    }
                }
            }
            bool any = false, stays = false;
            foreach (var s in spots)
            {
                row[2]++;
                if (!Embedded(geo, s.x, s.y, s.z)) continue;
                row[3]++;
                any = true;
                var cc = Centre(o, e, meshFs, meshes);
                if (!UseSpotClearance.TryClear(s.x, s.z, cc[0], cc[2], (x, z) => !Embedded(geo, x, s.y, z), null, out _, out _, out _)) stays = true;
                if (!samples.TryGetValue(kind, out var list)) samples[kind] = list = new List<string>();
                if (list.Count < top) list.Add(string.Format(CultureInfo.InvariantCulture, "{0} ({1:F1}, {2:F1}, {3:F1}) {4}", o.Name, s.x, s.y, s.z, s.how));
            }
            if (any) row[4]++;
            if (stays) row[5]++;
        }

        Console.WriteLine($"interactive objects {objects.Count}, without a world entity {noEntity}; hero capsule r {ScanRadius} m, {ScanBottom}..{ScanTop} m");
        Console.WriteLine($"{"kind/useType",-28} {"objects",7} {"w/coll",7} {"spots",6} {"embedded",8} {"obj emb",7} {"after rule",10}");
        foreach (var kv in rows)
            Console.WriteLine($"{kv.Key,-28} {kv.Value[0],7} {kv.Value[1],7} {kv.Value[2],6} {kv.Value[3],8} {kv.Value[4],7} {kv.Value[5],10}");
        foreach (var kv in samples)
        {
            Console.WriteLine($"== {kv.Key}");
            foreach (var line in kv.Value) Console.WriteLine("  " + line);
        }
        return 0;
    }

    /// <summary>The distance HeroObjectUse.Place asks for when the object has neither slots nor a mesh offset (0: he stays).</summary>
    static float FallbackDistance(InteractiveKind kind) => kind switch
    {
        InteractiveKind.Speaker => 0.9f,
        InteractiveKind.Activity => 0.9f,
        InteractiveKind.OreVein => 1.2f,
        InteractiveKind.Treasure => 1.0f,
        InteractiveKind.WaterBarrel => 0.7f,
        InteractiveKind.Chest => 0.85f,
        _ => 0f,
    };

    /// <summary>The render bounds centre (InteractiveObjectView.FocusPoint), metres; the origin + 0.5 m without a mesh.</summary>
    static float[] Centre(InteractiveObjectDef o, WorldEntity e, G3FileSystem fs, Dictionary<string, (MeshResource, bool)> meshes)
    {
        var (res, _) = LoadAuditMesh(fs, e.Resource, meshes);
        if (res == null) return new[] { o.Position[0], o.Position[1] + 0.5f, o.Position[2] };
        var b = WorldBox(res.BoundsMin, res.BoundsMax, e.World, false);
        return new[] { (b[0] + b[3]) * 0.005f, (b[1] + b[4]) * 0.005f, (b[2] + b[5]) * 0.005f };
    }

    static ScanGeometry Geometry(WorldEntity e, G3FileSystem fs, Dictionary<string, (CollisionMeshResource, string)> xnv)
    {
        var g = new ScanGeometry();
        if (!CollisionRules.HasPlayerCollision(e)) return g;
        var o1 = new float[3];
        float[] W(float[] p, int i, bool pre)
        {
            Xf(e.World, p[i], p[i + 1], p[i + 2], pre, o1);
            return new[] { o1[0] / 100, o1[1] / 100, o1[2] / 100 };
        }
        void Mesh(CollisionSubMesh sm, bool pre)
        {
            if (sm.Indices != null && sm.Indices.Length >= 3 && !sm.Convex)
            {
                for (int i = 0; i + 2 < sm.Indices.Length; i += 3)
                    g.Add(W(sm.Positions, (int)sm.Indices[i] * 3, pre), W(sm.Positions, (int)sm.Indices[i + 1] * 3, pre), W(sm.Positions, (int)sm.Indices[i + 2] * 3, pre));
                return;
            }
            // a hull: its local box (the scan's approximation)
            var mn = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var mx = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i + 2 < sm.Positions.Length; i += 3)
                for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], sm.Positions[i + a]); mx[a] = Math.Max(mx[a], sm.Positions[i + a]); }
            var c = new[] { (mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2 };
            Box(g, c, new[] { (mx[0] - mn[0]) / 2, (mx[1] - mn[1]) / 2, (mx[2] - mn[2]) / 2 }, new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, p => W(p, 0, pre));
        }
        if (e.CollisionShapes.Count == 0)
        {
            var (res, _) = LoadAuditXnv(fs, Path.GetFileNameWithoutExtension(e.Resource ?? ""), xnv);
            if (res != null) foreach (var sm in res.Meshes) if (!sm.DisableCollision) Mesh(sm, CollisionRules.IsPrescaled(Path.GetFileNameWithoutExtension(e.Resource ?? "")));
            return g;
        }
        foreach (var cs in e.CollisionShapes)
        {
            if (!CollisionRules.BlocksPlayer(cs)) continue;
            switch (cs.Type)
            {
                case CollisionShapeType.TriMesh:
                case CollisionShapeType.ConvexHull:
                {
                    var (res, _) = LoadAuditXnv(fs, cs.File, xnv);
                    if (res == null) break;
                    bool pre = CollisionRules.IsPrescaled(cs.File);
                    if (cs.ResourceIndex >= 0 && cs.ResourceIndex < res.Meshes.Count) Mesh(res.Meshes[cs.ResourceIndex], pre);
                    else foreach (var sm in res.Meshes) if (!sm.DisableCollision) Mesh(sm, pre);
                    break;
                }
                case CollisionShapeType.Box:
                    Box(g, cs.Center, cs.Extent, cs.Axes, p => W(p, 0, false));
                    break;
                case CollisionShapeType.Sphere:
                    Box(g, cs.Center, new[] { cs.Radius, cs.Radius, cs.Radius }, cs.Axes, p => W(p, 0, false));
                    break;
                case CollisionShapeType.Capsule:
                    Box(g, cs.Center, new[] { cs.Radius, cs.Height / 2 + cs.Radius, cs.Radius }, cs.Axes, p => W(p, 0, false));
                    break;
            }
        }
        return g;
    }

    /// <summary>A box (entity-local centre, half extents along the 3 axis rows) as 12 world triangles.</summary>
    static void Box(ScanGeometry g, float[] c, float[] ext, float[] axes, Func<float[], float[]> world)
    {
        var v = new float[8][];
        for (int k = 0; k < 8; k++)
        {
            var p = new float[3];
            for (int a = 0; a < 3; a++)
            {
                float s = ((k >> a) & 1) != 0 ? ext[a] : -ext[a];
                for (int j = 0; j < 3; j++) p[j] += axes[a * 3 + j] * s;
            }
            for (int j = 0; j < 3; j++) p[j] += c[j];
            v[k] = world(p);
        }
        int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 } };
        foreach (var f in faces)
        {
            g.Add(v[f[0]], v[f[1]], v[f[2]]);
            g.Add(v[f[0]], v[f[2]], v[f[3]]);
        }
    }

    /// <summary>The hero's capsule standing at (x, y, z) touches a triangle, or its axis lies inside the closed volume.</summary>
    static bool Embedded(ScanGeometry g, float x, float y, float z)
    {
        if (x < g.MinX - ScanRadius || x > g.MaxX + ScanRadius || z < g.MinZ - ScanRadius || z > g.MaxZ + ScanRadius) return false;
        float y0 = y + ScanBottom + ScanRadius, y1 = y + ScanTop - ScanRadius;
        foreach (var tri in g.Tris)
            for (float py = y0; py <= y1 + 1e-4f; py += 0.1f)
                if (PointTriangleDistanceSq(x, py, z, tri) < ScanRadius * ScanRadius) return true;
        // inside: odd crossings both up and down from the capsule's middle
        float my = (y0 + y1) / 2;
        int up = 0, down = 0;
        foreach (var tri in g.Tris)
            if (VerticalCrossing(x, z, tri, out float hy))
            {
                if (hy > my) up++;
                else down++;
            }
        return (up & 1) == 1 && (down & 1) == 1;
    }

    static bool VerticalCrossing(float x, float z, float[] t, out float y)
    {
        y = 0;
        float ax = t[0], az = t[2], bx = t[3], bz = t[5], cx = t[6], cz = t[8];
        float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
        if (Math.Abs(d) < 1e-9f) return false;
        float l1 = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d;
        float l2 = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d;
        float l3 = 1 - l1 - l2;
        if (l1 < 0 || l2 < 0 || l3 < 0) return false;
        y = l1 * t[1] + l2 * t[4] + l3 * t[7];
        return true;
    }

    /// <summary>Squared distance from a point to a triangle (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    static float PointTriangleDistanceSq(float px, float py, float pz, float[] t)
    {
        float ax = t[0], ay = t[1], az = t[2];
        float abx = t[3] - ax, aby = t[4] - ay, abz = t[5] - az, acx = t[6] - ax, acy = t[7] - ay, acz = t[8] - az;
        float apx = px - ax, apy = py - ay, apz = pz - az;
        float d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        float qx, qy, qz;
        if (d1 <= 0 && d2 <= 0) { qx = ax; qy = ay; qz = az; return Sq(px - qx, py - qy, pz - qz); }
        float bpx = px - t[3], bpy = py - t[4], bpz = pz - t[5];
        float d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
        if (d3 >= 0 && d4 <= d3) return Sq(bpx, bpy, bpz);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v = d1 / (d1 - d3);
            return Sq(apx - v * abx, apy - v * aby, apz - v * abz);
        }
        float cpx = px - t[6], cpy = py - t[7], cpz = pz - t[8];
        float d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
        if (d6 >= 0 && d5 <= d6) return Sq(cpx, cpy, cpz);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w = d2 / (d2 - d6);
            return Sq(apx - w * acx, apy - w * acy, apz - w * acz);
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return Sq(bpx - w * (t[6] - t[3]), bpy - w * (t[7] - t[4]), bpz - w * (t[8] - t[5]));
        }
        float denom = 1 / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return Sq(apx - abx * vv - acx * ww, apy - aby * vv - acy * ww, apz - abz * vv - acz * ww);
    }

    static float Sq(float x, float y, float z) => x * x + y * y + z * z;

    static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    static void Frame(InteractiveObjectDef o, out float[] origin, out float[] right, out float[] up, out float[] forward)
    {
        origin = o.Position;
        forward = Norm(o.Forward);
        up = Norm(o.Up);
        right = Norm(new[] { up[1] * forward[2] - up[2] * forward[1], up[2] * forward[0] - up[0] * forward[2], up[0] * forward[1] - up[1] * forward[0] });
    }

    static float[] Norm(float[] v)
    {
        float l = (float)Math.Sqrt(Dot(v, v));
        return l < 1e-6f ? new float[] { 0, 0, 1 } : new[] { v[0] / l, v[1] / l, v[2] / l };
    }
}
