using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;
using G3.Player;

static partial class Program
{
    /// <summary>
    /// Water zones and swimming (Docs/hero.md "Water and swimming"):
    /// <c>water dump [nameFilter|x,z,r] [max]</c> all property sets of gCWaterZone_PS entities (every archive);
    /// <c>water stats</c> builds the zones of the load set like the editor import (plane, box, mesh footprint) and prints
    /// counts and flatness; <c>water probe x z [dx dz steps]</c> (metres) samples water surface, terrain floor, depth and
    /// the hero's water mode at a point or along a line, for picking shore test spots;
    /// <c>water dry [fraction]</c> zones whose footprint lies mostly under the landscape (terrain at or above the water
    /// surface over at least <c>fraction</c> of the sampled footprint, default 0.5): G3 water meshes run under the banks,
    /// so a shore spot inside a zone can be land (editor-runner-14 lake spot); a zone that is dry over most of its area
    /// would point at a wrong surface height or a misplaced footprint.
    /// </summary>
    static int WaterCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "dump";
        switch (sub)
        {
            case "stats": return WaterStats(data);
            case "dry": return WaterDry(data, args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0.5f);
            case "column": return WaterColumn(data, args.Skip(2).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            case "slope": return TerrainSlope(data, args.Skip(2).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            case "cliff": return TerrainCliff(data, args.Skip(2).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            case "probe": return WaterProbe(data, args.Skip(2).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            default: return WaterDump(data, args.Skip(sub == "dump" ? 2 : 1).ToArray());
        }
    }

    static int WaterDump(string data, string[] args)
    {
        string filter = args.Length > 0 && args[0] != "-" ? args[0] : null;
        int max = args.Length > 1 ? int.Parse(args[1]) : 10;
        using var fs = new G3FileSystem(data, "Projects_compiled");
        int shown = 0, total = 0;
        foreach (var f in fs.Files.Where(f => f.Path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".node", StringComparison.OrdinalIgnoreCase)))
        {
            List<WorldEntity> list;
            try { list = EntityArchive.Read(f.Read(), false, keepRaw: true); } catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Water == null) continue;
                total++;
                if (filter != null && !Matches(e, filter)) continue;
                if (shown++ >= max) continue;
                Console.WriteLine($"== {e.Name} res={e.Resource} pos ({e.World[12]:F0},{e.World[13]:F0},{e.World[14]:F0}) in {f.Path}");
                foreach (var ps in e.RawPropertySets.Where(p => p.ClassName == "gCWaterZone_PS"))
                    foreach (var p in ps.Properties)
                        Console.WriteLine($"     {p.Key} : {p.Value.Type} = {Fmt(e.Source, p.Value)}");
            }
        }
        Console.WriteLine($"total {total} water zone entities (all archives)");
        return 0;
    }

    /// <summary>Water zone entities of the load set (enabled sectors), as the editor import reads them.</summary>
    static List<WorldEntity> LoadSetWaterEntities(string data)
    {
        var result = new List<WorldEntity>();
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        foreach (var s in layout.Sectors)
        foreach (var node in s.Nodes)
        {
            var entry = fs.Get(vol + node);
            var list = entry != null ? EntityArchive.Read(entry.Read(), false) : null;
            if (list != null) result.AddRange(list.Where(e => e.Water != null));
        }
        return result;
    }

    static WaterMap BuildWaterMap(string data, bool report)
    {
        var entities = LoadSetWaterEntities(data);
        using var mfs = new G3FileSystem(data, "_compiledMesh");
        var map = new WaterMap();
        int missing = 0, tris = 0;
        float worstTilt = 0, worstDev = 0;
        string worstDevName = null;
        var kinds = new Dictionary<string, int>();
        foreach (var e in entities)
        {
            G3.Formats.Mesh.MeshResource mesh = null;
            try
            {
                string res = e.Resource;
                if (res != null && res.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
                    res = G3.Formats.Mesh.Xlmsh.Read(mfs.GetByName(res).Read()).FirstOrDefault();
                var entry = res != null ? mfs.GetByName(res) : null;
                if (entry != null) mesh = G3.Formats.Mesh.Xcmsh.Read(entry.Read(), res);
            }
            catch { }
            if (mesh == null) missing++;
            var w = e.Water;
            var zone = WaterZone.FromG3(e.Name, w.Normal, w.Centre, w.SquareRadius, w.MinX, w.MaxX, w.MinZ, w.MaxZ, e.World,
                mesh?.Elements.Select(el => (el.Positions, el.Indices)));
            tris += zone.TriangleCount;
            zone.Rasterize();
            map.Add(zone);
            string kind = e.Name.Contains("Ocean") || e.Name.Contains("Sea") ? "sea" : e.Name.Contains("River") ? "river" : "other";
            kinds[kind] = kinds.TryGetValue(kind, out int n) ? n + 1 : 1;
            worstTilt = Math.Max(worstTilt, MathF.Acos(Math.Clamp(zone.NY, -1, 1)) * 180 / MathF.PI);
            if (mesh != null)
                foreach (var el in mesh.Elements)
                    for (int i = 0; i + 2 < el.Positions.Length; i += 3)
                    {
                        float px = el.Positions[i], py = el.Positions[i + 1], pz = el.Positions[i + 2];
                        var m = e.World;
                        float wx = (px * m[0] + py * m[4] + pz * m[8] + m[12]) * 0.01f;
                        float wy = (px * m[1] + py * m[5] + pz * m[9] + m[13]) * 0.01f;
                        float wz = (px * m[2] + py * m[6] + pz * m[10] + m[14]) * 0.01f;
                        float dev = MathF.Abs(wy - zone.SurfaceY(wx, wz));
                        if (dev > worstDev) { worstDev = dev; worstDevName = e.Name; }
                    }
        }
        if (report)
        {
            Console.WriteLine($"zones {map.Zones.Count} ({string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}"))}), meshes missing {missing}, footprint triangles {tris}");
            Console.WriteLine($"max surface tilt {worstTilt:F2} deg; max vertex distance from the zone plane {worstDev:F2} m ({worstDevName})");
            var ys = map.Zones.Select(z => z.CY).OrderBy(y => y).ToList();
            Console.WriteLine($"surface heights {ys.First():F2} .. {ys.Last():F2} m; sea level {map.Zones.Where(z => z.Name.Contains("Ocean")).Select(z => z.CY).DefaultIfEmpty(float.NaN).First():F2} m");
            var ms = new System.IO.MemoryStream();
            WaterMapFile.Write(ms, map.Zones);
            ms.Position = 0;
            var back = WaterMapFile.Read(ms);
            Console.WriteLine($"water file {ms.Length / 1024} KiB (raster), read back {back.Zones.Count} zones");
        }
        return map;
    }

    static int WaterStats(string data)
    {
        BuildWaterMap(data, true);
        return 0;
    }

    /// <summary>Landscape terrain triangles near a point (world metres), for floor heights.</summary>
    static List<float[]> TerrainNear(string data, float x, float z, float radius) =>
        Terrain(data, x - radius, x + radius, z - radius, z + radius);

    /// <summary>Landscape terrain triangles (world metres, 9 floats each) overlapping a box; the whole landscape with infinite bounds.</summary>
    static List<float[]> Terrain(string data, float minX, float maxX, float minZ, float maxZ)
    {
        var tris = new List<float[]>();
        using var fs = new G3FileSystem(data, "Projects_compiled");
        using var mfs = new G3FileSystem(data, "_compiledMesh");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        foreach (var s in layout.Sectors)
        foreach (var node in s.Nodes)
        {
            var list = EntityArchive.Read(fs.Get(vol + node).Read(), false);
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual != VisualKind.Mesh || e.Resource == null || e.Water != null) continue;
                if (e.Resource.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var b = e.Bounds;
                if (b[0] * 0.01f > maxX || b[3] * 0.01f < minX || b[2] * 0.01f > maxZ || b[5] * 0.01f < minZ) continue;
                var entry = mfs.GetByName(e.Resource);
                if (entry == null) continue;
                var mesh = G3.Formats.Mesh.Xcmsh.Read(entry.Read(), e.Resource);
                var m = e.World;
                foreach (var el in mesh.Elements)
                    for (int i = 0; i + 2 < el.Indices.Length; i += 3)
                    {
                        var t = new float[9];
                        for (int k = 0; k < 3; k++)
                        {
                            long v = el.Indices[i + k] * 3L;
                            float px = el.Positions[v], py = el.Positions[v + 1], pz = el.Positions[v + 2];
                            t[k * 3] = (px * m[0] + py * m[4] + pz * m[8] + m[12]) * 0.01f;
                            t[k * 3 + 1] = (px * m[1] + py * m[5] + pz * m[9] + m[13]) * 0.01f;
                            t[k * 3 + 2] = (px * m[2] + py * m[6] + pz * m[10] + m[14]) * 0.01f;
                        }
                        float tx0 = Math.Min(t[0], Math.Min(t[3], t[6])), tx1 = Math.Max(t[0], Math.Max(t[3], t[6]));
                        float tz0 = Math.Min(t[2], Math.Min(t[5], t[8])), tz1 = Math.Max(t[2], Math.Max(t[5], t[8]));
                        if (tx0 > maxX || tx1 < minX || tz0 > maxZ || tz1 < minZ) continue;
                        tris.Add(t);
                    }
            }
        }
        return tris;
    }

    /// <summary>Highest terrain height under (x, z), NaN when no terrain triangle covers it.</summary>
    /// <summary><c>water column x z [x z ...]</c>: every landscape surface height at each point (overhangs, caves).</summary>
    static int WaterColumn(string data, float[] a)
    {
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            var terrain = TerrainNear(data, a[i], a[i + 1], 3);
            var ys = new List<float>();
            foreach (var t in terrain)
            {
                float y = TerrainHeight(new List<float[]> { t }, a[i], a[i + 1]);
                if (!float.IsNaN(y)) ys.Add(y);
            }
            ys.Sort();
            Console.WriteLine($"{a[i],8:F1} {a[i + 1],8:F1}: {string.Join(" ", ys.Select(y => y.ToString("F1")))}");
        }
        return 0;
    }

    /// <summary>
    /// <c>water cliff x z [radius]</c> (fix-49): landscape cliffs near a point for "must not climb" checks: on a 1 m grid
    /// within radius (default 30 m), the rise of the top surface over 1.5 m in 8 directions; lists the 8 nearest places (one per
    /// 3 m cell) rising 2.5-8 m (about 60 deg and more) with the foot point, the direction and the rise.
    /// </summary>
    static int TerrainCliff(string data, float[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("water cliff x z [radius]"); return 1; }
        float x0 = a[0], z0 = a[1], r = a.Length > 2 ? a[2] : 30f;
        var tris = TerrainNear(data, x0, z0, r + 3f);
        const float cell = 8f;
        var grid = new Dictionary<(int, int), List<float[]>>();
        foreach (var t in tris)
        {
            int gx0 = (int)MathF.Floor(Math.Min(t[0], Math.Min(t[3], t[6])) / cell), gx1 = (int)MathF.Floor(Math.Max(t[0], Math.Max(t[3], t[6])) / cell);
            int gz0 = (int)MathF.Floor(Math.Min(t[2], Math.Min(t[5], t[8])) / cell), gz1 = (int)MathF.Floor(Math.Max(t[2], Math.Max(t[5], t[8])) / cell);
            for (int gx = gx0; gx <= gx1; gx++)
            for (int gz = gz0; gz <= gz1; gz++)
            {
                if (!grid.TryGetValue((gx, gz), out var l)) grid[(gx, gz)] = l = new List<float[]>();
                l.Add(t);
            }
        }
        float Top(float x, float z) =>
            grid.TryGetValue(((int)MathF.Floor(x / cell), (int)MathF.Floor(z / cell)), out var l) ? TerrainHeight(l, x, z) : float.NaN;
        var found = new List<(float rise, float x, float y, float z, float dx, float dz)>();
        for (float x = x0 - r; x <= x0 + r; x += 1f)
        for (float z = z0 - r; z <= z0 + r; z += 1f)
        {
            float y = Top(x, z);
            if (float.IsNaN(y)) continue;
            for (int k = 0; k < 8; k++)
            {
                float dx = MathF.Cos(k * MathF.PI / 4f), dz = MathF.Sin(k * MathF.PI / 4f);
                float y2 = Top(x + dx * 1.5f, z + dz * 1.5f);
                if (!float.IsNaN(y2) && y2 - y >= 2.5f) found.Add((y2 - y, x, y, z, dx, dz));
            }
        }
        // nearest first, one per 3 m cell; drops of more than 8 m are mostly cave mouths and overhangs (the top surface jumps)
        var seen = new HashSet<(int, int)>();
        foreach (var f in found.Where(f => f.rise <= 8f).OrderBy(f => (f.x - x0) * (f.x - x0) + (f.z - z0) * (f.z - z0))
                     .Where(f => seen.Add(((int)MathF.Floor(f.x / 3f), (int)MathF.Floor(f.z / 3f)))).Take(8))
            Console.WriteLine($"foot {f.x:F1} {f.y:F2} {f.z:F1} dir {f.dx:F2} {f.dz:F2}: rises {f.rise:F2} m over 1.5 m");
        Console.WriteLine($"{found.Count} cliff samples within {r:F0} m of {x0:F1} {z0:F1}");
        return 0;
    }

    /// <summary>
    /// <c>water slope x z [x z …]</c> (fix-48): the landscape faces within 1.5 m of each point that lie within 1 m of the top
    /// surface there (what the hero's capsule stands on): their count, steepest angle and how many exceed the controller's
    /// <see cref="SlopeRules.WalkableAngleMax"/> (a face steeper than that is a wall for the CharacterController).
    /// </summary>
    static int TerrainSlope(string data, float[] a)
    {
        const float r = 1.5f;
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            float x = a[i], z = a[i + 1];
            var tris = TerrainNear(data, x, z, r + 2f);
            float top = TerrainHeight(tris, x, z);
            int n = 0, steep = 0;
            float max = 0f;
            foreach (var t in tris)
            {
                float cx = (t[0] + t[3] + t[6]) / 3f, cy = (t[1] + t[4] + t[7]) / 3f, cz = (t[2] + t[5] + t[8]) / 3f;
                if ((cx - x) * (cx - x) + (cz - z) * (cz - z) > r * r || float.IsNaN(top) || MathF.Abs(cy - top) > 1f) continue;
                float ux = t[3] - t[0], uy = t[4] - t[1], uz = t[5] - t[2], vx = t[6] - t[0], vy = t[7] - t[1], vz = t[8] - t[2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-9f) continue;
                float angle = MathF.Acos(MathF.Min(1f, MathF.Abs(ny) / len)) * 180f / MathF.PI;
                n++;
                max = MathF.Max(max, angle);
                if (angle > SlopeRules.WalkableAngleMax)
                {
                    steep++;
                    float lo = MathF.Min(t[1], MathF.Min(t[4], t[7])), hi = MathF.Max(t[1], MathF.Max(t[4], t[7]));
                    Console.WriteLine($"    steep face {angle:F0} deg, y {lo:F2}..{hi:F2} (top {top:F2}), centre {cx:F1} {cz:F1}, " +
                                      $"v ({t[0]:F2} {t[1]:F2} {t[2]:F2}) ({t[3]:F2} {t[4]:F2} {t[5]:F2}) ({t[6]:F2} {t[7]:F2} {t[8]:F2})");
                }
            }
            Console.WriteLine($"{x,8:F1} {z,8:F1}: top {top:F2}, {n} faces, steepest {max:F0} deg, {steep} over {SlopeRules.WalkableAngleMax:F0} deg");
        }
        return 0;
    }

    static float TerrainHeight(List<float[]> tris, float x, float z)
    {
        float best = float.NaN;
        foreach (var t in tris)
        {
            float x0 = t[0], z0 = t[2], x1 = t[3], z1 = t[5], x2 = t[6], z2 = t[8];
            float d = (z1 - z2) * (x0 - x2) + (x2 - x1) * (z0 - z2);
            if (MathF.Abs(d) < 1e-9f) continue;
            float a = ((z1 - z2) * (x - x2) + (x2 - x1) * (z - z2)) / d;
            float b = ((z2 - z0) * (x - x2) + (x0 - x2) * (z - z2)) / d;
            float c = 1 - a - b;
            if (a < -1e-4f || b < -1e-4f || c < -1e-4f) continue;
            float y = a * t[1] + b * t[4] + c * t[7];
            if (float.IsNaN(best) || y > best) best = y;
        }
        return best;
    }

    /// <summary>
    /// <c>water dry</c>: per zone, the footprint raster sampled (at most ~4000 points) against the landscape (the Unity
    /// landscape collider is the same render mesh, G3CollisionBuilder): the share of points where the terrain is at or above
    /// the surface (dry), and where the water is deep enough to swim. Lists zones dry over at least <paramref name="fraction"/>.
    /// </summary>
    static int WaterDry(string data, float fraction)
    {
        var map = BuildWaterMap(data, false);
        var tris = Terrain(data, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity);
        const float cell = 16f;
        var grid = new Dictionary<(int, int), List<float[]>>();
        foreach (var t in tris)
        {
            int x0 = (int)MathF.Floor(Math.Min(t[0], Math.Min(t[3], t[6])) / cell), x1 = (int)MathF.Floor(Math.Max(t[0], Math.Max(t[3], t[6])) / cell);
            int z0 = (int)MathF.Floor(Math.Min(t[2], Math.Min(t[5], t[8])) / cell), z1 = (int)MathF.Floor(Math.Max(t[2], Math.Max(t[5], t[8])) / cell);
            for (int gx = x0; gx <= x1; gx++)
            for (int gz = z0; gz <= z1; gz++)
            {
                if (!grid.TryGetValue((gx, gz), out var l)) grid[(gx, gz)] = l = new List<float[]>();
                l.Add(t);
            }
        }
        float Floor(float x, float z) =>
            grid.TryGetValue(((int)MathF.Floor(x / cell), (int)MathF.Floor(z / cell)), out var l) ? TerrainHeight(l, x, z) : float.NaN;
        Console.WriteLine($"terrain triangles {tris.Count}; zones {map.Zones.Count}; listing zones dry over >= {fraction:P0} of the footprint");
        Console.WriteLine("zone                                   surface  samples   dry%  swim%  noTerrain%  centre");
        int listed = 0;
        foreach (var zone in map.Zones)
        {
            float w = zone.MaxX - zone.MinX, h = zone.MaxZ - zone.MinZ;
            float step = Math.Max(zone.MaskCell > 0 ? zone.MaskCell : 1f, MathF.Sqrt(Math.Max(w * h, 1f) / 4000f));
            int samples = 0, dry = 0, swim = 0, open = 0;
            for (float z = zone.MinZ + step * 0.5f; z < zone.MaxZ; z += step)
            for (float x = zone.MinX + step * 0.5f; x < zone.MaxX; x += step)
            {
                if (!zone.Covers(x, z)) continue;
                samples++;
                float floor = Floor(x, z), surface = zone.SurfaceY(x, z);
                if (float.IsNaN(floor)) { open++; swim++; continue; }
                if (floor >= surface) dry++;
                else if (surface - floor >= SwimRules.HeroSwimDepth) swim++;
            }
            if (samples == 0 || dry < fraction * samples) continue;
            listed++;
            Console.WriteLine($"{zone.Name,-38} {zone.CY,7:F2} {samples,8} {100f * dry / samples,6:F1} {100f * swim / samples,6:F1} {100f * open / samples,10:F1}  ({zone.CX:F0},{zone.CZ:F0})");
        }
        Console.WriteLine($"{listed} of {map.Zones.Count} zones dry over >= {fraction:P0}");
        return 0;
    }

    static int WaterProbe(string data, float[] a)
    {
        if (a.Length < 2) { Console.WriteLine("water probe x z [dx dz steps]  (metres)"); return 1; }
        float x = a[0], z = a[1], dx = a.Length > 3 ? a[2] : 0, dz = a.Length > 3 ? a[3] : 0;
        int steps = a.Length > 4 ? (int)a[4] : 1;
        var map = BuildWaterMap(data, false);
        float reach = MathF.Sqrt(dx * dx + dz * dz) * steps + 5;
        var terrain = TerrainNear(data, x + dx * steps * 0.5f, z + dz * steps * 0.5f, reach * 0.5f + 5);
        float wade = SwimRules.WadeDepth(SwimRules.HeroSwimDepth, SlopeRules.StepHeight);
        Console.WriteLine($"terrain triangles {terrain.Count}; hero wade from {wade:F2} m, swim from {SwimRules.HeroSwimDepth:F2} m");
        Console.WriteLine("      x        z    floor  surface  column  mode   zone");
        var mode = WaterMode.None;
        for (int i = 0; i < steps; i++)
        {
            float px = x + dx * i, pz = z + dz * i;
            float floor = TerrainHeight(terrain, px, pz);
            float feet = float.IsNaN(floor) ? 0 : floor;
            bool wet = map.Sample(px, feet, pz, out float surface, out var zone, 1000f);
            float column = wet ? (float.IsNaN(floor) ? float.PositiveInfinity : surface - floor) : float.NaN;
            float body = wet ? Math.Min(column, SwimRules.HeroSwimDepth) : float.NaN;
            mode = wet ? SwimRules.Classify(mode, false, float.IsInfinity(column) ? SwimRules.HeroSwimDepth : column, column, SwimRules.HeroSwimDepth, wade) : WaterMode.None;
            Console.WriteLine($"{px,8:F1} {pz,8:F1} {floor,8:F2} {(wet ? surface.ToString("F2") : "-"),8} {(wet ? column.ToString("F2") : "-"),7}  {mode,-6} {zone?.Name}");
        }
        return 0;
    }
}
