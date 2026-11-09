using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// <c>collision column x z [radius] [--step 1] [--pit 5] [--render] [--line x2,z2[,step]] [--sweep x2,z2,y0,y1[,dy]]</c> (fix-99; --sweep fix-148: horizontal rays for walls): the collision surfaces the cell import builds
/// (<see cref="LoadFloorTriangles"/>: G3 shapes, lazy .xnvmsh, render fallback, the landscape from its .xnvmsh as the builder cooks it, or its render mesh with --render) in the vertical column at
/// x z (every surface, top down, with its source mesh), then a scan of the area: grid points whose highest collision
/// surface lies more than <c>pit</c> m below the highest surface of a neighbour are listed as "pits" (a hole in a hill
/// over a cave, a missing landscape piece) with the source meshes on both sides.
/// </summary>
static partial class Program
{
    static int CollisionColumn(string data, string[] args)
    {
        var plain = args.Skip(2).TakeWhile(a => !a.StartsWith("--")).Select(a => float.Parse(a, CultureInfo.InvariantCulture)).ToArray();
        float x = plain[0], z = plain[1], r = plain.Length > 2 ? plain[2] : 0;
        float step = float.Parse(Option(args, "--step") ?? "1", CultureInfo.InvariantCulture);
        float pit = float.Parse(Option(args, "--pit") ?? "5", CultureInfo.InvariantCulture);
        var cells = new HashSet<(int, int)>();
        float rr = r;
        var lineOpt = Option(args, "--line");
        if (lineOpt != null) { var q = lineOpt.Split(','); rr = Math.Max(Math.Abs(float.Parse(q[0], CultureInfo.InvariantCulture) - x), Math.Abs(float.Parse(q[1], CultureInfo.InvariantCulture) - z)); }
        for (float cx = x - rr - FloorCell; cx <= x + rr + FloorCell; cx += FloorCell)
        for (float cz = z - rr - FloorCell; cz <= z + rr + FloorCell; cz += FloorCell)
            cells.Add(CellOf(cx, cz));
        var grid = LoadFloorTriangles(data, cells, out int entities, landscapeXnv: !args.Contains("--render"));
        Console.WriteLine($"collision column: {entities} entities, {grid.Values.Sum(l => l.Count)} triangle refs");

        string line = Option(args, "--line"); // "x2,z2[,step]": every column along the segment to x2 z2
        if (line != null)
        {
            var lp = line.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            float ls = lp.Length > 2 ? lp[2] : 0.5f, len = (float)Math.Sqrt((lp[0] - x) * (lp[0] - x) + (lp[1] - z) * (lp[1] - z));
            for (float d = 0; d <= len + 1e-3f; d += ls)
            {
                float px = x + (lp[0] - x) * d / Math.Max(len, 1e-3f), pz = z + (lp[1] - z) * d / Math.Max(len, 1e-3f);
                var cs = Surfaces(grid, px, pz);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  ({0:F1}, {1:F1}): {2}", px, pz,
                    string.Join("  ", cs.Select(c => c.y.ToString("F2", CultureInfo.InvariantCulture) + " " + ShortSrc(c.src)))));
            }
            return 0;
        }
        string capsule = Option(args, "--capsule"); // fix-148: "x2,z2,r,h[,step[,feetY]]": a standing capsule walked along the floor x z -> x2 z2
        if (capsule != null) return CapsuleWalk(grid, x, z, capsule.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray());
        string sweep = Option(args, "--sweep"); // fix-148: "x2,z2,y0,y1[,dy]": horizontal rays x z -> x2 z2 at heights y0..y1
        if (sweep != null) return Sweep(grid, x, z, sweep.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray());
        if (args.Contains("--leaks")) return FoldLeaks(grid, x, z, r, step);
        if (args.Contains("--mouths")) return CaveMouths(grid, x, z, r, step);
        var col = Surfaces(grid, x, z);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "column ({0:F2}, {1:F2}): {2} surfaces", x, z, col.Count));
        foreach (var (y, src) in col) Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  y {0,8:F2}  {1}", y, src));
        if (r <= 0) return 0;

        int n = (int)Math.Ceiling(r / step);
        var top = new Dictionary<(int, int), (float y, string src)>();
        for (int i = -n; i <= n; i++)
        for (int k = -n; k <= n; k++)
        {
            float px = x + i * step, pz = z + k * step;
            if (i * i + k * k > n * n) continue;
            var s = Surfaces(grid, px, pz);
            top[(i, k)] = s.Count > 0 ? s[0] : (float.NegativeInfinity, "NONE");
        }
        int pits = 0, none = 0;
        var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in top.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
        {
            var (i, k) = kv.Key;
            float hi = float.NegativeInfinity; string hiSrc = null;
            foreach (var d in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (top.TryGetValue((i + d.Item1, k + d.Item2), out var nb) && nb.y > hi) { hi = nb.y; hiSrc = nb.src; }
            if (float.IsNegativeInfinity(kv.Value.y)) none++;
            if (float.IsNegativeInfinity(hi) || hi - kv.Value.y <= pit) continue;
            pits++;
            string key = kv.Value.src + " <- " + hiSrc;
            bySource[key] = bySource.TryGetValue(key, out int c) ? c + 1 : 1;
            if (pits <= 60)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  pit ({0:F1}, {1:F1}): top {2:F2} ({3}), neighbour {4:F2} ({5})",
                    x + i * step, z + k * step, kv.Value.y, kv.Value.src, hi, hiSrc));
        }
        Console.WriteLine($"scan r {r} m step {step} m: {top.Count} points, {none} without any surface, {pits} pit points (> {pit} m below a neighbour)");
        foreach (var kv in bySource.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        return 0;
    }

    /// <summary>
    /// <c>--leaks</c>: where the hero's fold rule (G3Player.ProbeStep, fix-49) would put him from open ground through a
    /// landscape face onto a floor under the landscape (inside the hill: a cave behind its wall). Per grid point and 8
    /// directions: a standing floor in the open, a steep landscape face within 0.6 m at 0.15-1.9 m, and a floor within
    /// step height that the floor ray (from head height down) finds 0.45-0.7 m on, under a landscape surface.
    /// </summary>
    static int FoldLeaks(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float r, float step)
    {
        int n = (int)Math.Ceiling(r / step), leaks = 0, standing = 0;
        var bySrc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        float[] heights = { 0.15f, 0.4f, 0.6f, 0.9f, 1.15f, 1.4f, 1.65f, 1.9f };
        for (int i = -n; i <= n; i++)
        for (int k = -n; k <= n; k++)
        {
            float px = x + i * step, pz = z + k * step;
            var col = Surfaces(grid, px, pz);
            if (col.Count == 0 || IsLandscapeSrc(col[0].src) == false) continue;
            float f = col[0].y; // open ground: the top surface, landscape, nothing above
            standing++;
            for (int d = 0; d < 8; d++)
            {
                float a = d * MathF.PI / 4, dx = MathF.Cos(a), dz = MathF.Sin(a);
                bool face = false;
                foreach (float h in heights)
                    if (HitsSteepLandscape(grid, px, f + h, pz, dx, dz, 0.6f)) { face = true; break; }
                if (!face) continue;
                foreach (float land in new[] { 0.45f, 0.7f })
                {
                    float lx = px + dx * land, lz = pz + dz * land, start = f + 1.75f;
                    var lc = Surfaces(grid, lx, lz);
                    var floor = lc.FirstOrDefault(c => c.y <= start);
                    if (floor.src == null || floor.y < f - 0.65f || floor.y > f + 0.65f) continue;
                    bool under = lc.Any(c => c.y > floor.y + SlopeFoldCover && IsLandscapeSrc(c.src));
                    if (!under || IsLandscapeSrc(floor.src)) continue; // a landscape niche under an overhang is still a fold
                    leaks++;
                    bySrc[floor.src] = bySrc.TryGetValue(floor.src, out int c0) ? c0 + 1 : 1;
                    if (leaks <= 40)
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  leak ({0:F1}, {1:F2}, {2:F1}) dir {3}° -> floor {4:F2} {5} under the landscape",
                            px, f, pz, d * 45, floor.y, floor.src));
                    break;
                }
            }
        }
        Console.WriteLine($"fold leaks: {standing} open standing points, {leaks} point/directions that pass a landscape face onto a floor under the landscape");
        foreach (var kv in bySrc.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        return 0;
    }

    const float SlopeFoldCover = 1.75f;

    /// <summary>
    /// <c>--mouths</c>: where a body can walk from a floor under the landscape that is no landscape (a cave or dungeon
    /// floor) straight onto open ground (a landscape floor with nothing above it) within step height, with no triangle
    /// across the way at 0.4-1.6 m: the cave's openings (its entrances, and holes in its walls), clustered per 4 m.
    /// </summary>
    static int CaveMouths(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float r, float step)
    {
        int n = (int)Math.Ceiling(r / step);
        var mouths = new Dictionary<(int, int), (int n, float x, float y, float z, string src)>();
        for (int i = -n; i <= n; i++)
        for (int k = -n; k <= n; k++)
        {
            float px = x + i * step, pz = z + k * step;
            var col = Surfaces(grid, px, pz);
            for (int s = 1; s < col.Count; s++)
            {
                var f = col[s];
                if (IsLandscapeSrc(f.src) || col[s - 1].y - f.y < 1.8f) continue; // a floor with room above it
                if (!col.Take(s).Any(c => IsLandscapeSrc(c.src))) continue; // under the landscape
                for (int d = 0; d < 8; d++)
                {
                    float a = d * MathF.PI / 4, dx = MathF.Cos(a), dz = MathF.Sin(a);
                    float qx = px + dx * step, qz = pz + dz * step;
                    var qc = Surfaces(grid, qx, qz);
                    if (qc.Count == 0 || !IsLandscapeSrc(qc[0].src) || Math.Abs(qc[0].y - f.y) > 0.65f) continue; // open ground next to it
                    bool blocked = false;
                    foreach (float h in new[] { 0.4f, 0.9f, 1.4f })
                        if (HitsAny(grid, px, f.y + h, pz, dx, dz, step)) { blocked = true; break; }
                    if (blocked) continue;
                    var key = ((int)Math.Floor(px / 4), (int)Math.Floor(pz / 4));
                    mouths[key] = mouths.TryGetValue(key, out var m) ? (m.n + 1, m.x, m.y, m.z, m.src) : (1, px, f.y, pz, f.src);
                }
            }
        }
        Console.WriteLine($"cave openings within {r} m: {mouths.Count} (4 m clusters)");
        foreach (var m in mouths.Values.OrderBy(v => v.x).ThenBy(v => v.z))
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  ({0:F1}, {1:F2}, {2:F1}) {3} edges, floor {4}", m.x, m.y, m.z, m.n, m.src));
        return 0;
    }

    /// <summary>Any triangle (steep or not) crosses the horizontal segment.</summary>
    static bool HitsAny(Dictionary<(int, int), List<FloorTri>> grid, float ox, float oy, float oz, float dx, float dz, float len)
    {
        var cells = new HashSet<(int, int)> { CellOf(ox, oz), CellOf(ox + dx * len, oz + dz * len) };
        foreach (var c in cells)
        {
            if (!grid.TryGetValue(c, out var list)) continue;
            foreach (var t in list)
            {
                float e1x = t.X1 - t.X0, e1y = t.Y1 - t.Y0, e1z = t.Z1 - t.Z0, e2x = t.X2 - t.X0, e2y = t.Y2 - t.Y0, e2z = t.Z2 - t.Z0;
                float px = dz * e2y, py = dx * e2z - dz * e2x, pz = -dx * e2y;
                float det = e1x * px + e1y * py + e1z * pz;
                if (MathF.Abs(det) < 1e-9f) continue;
                float inv = 1 / det, sx = ox - t.X0, sy = oy - t.Y0, sz = oz - t.Z0;
                float u = (sx * px + sy * py + sz * pz) * inv;
                if (u < 0 || u > 1) continue;
                float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                float v = (dx * qx + dz * qz) * inv;
                if (v < 0 || u + v > 1) continue;
                float dist = (e2x * qx + e2y * qy + e2z * qz) * inv;
                if (dist >= 0 && dist <= len) return true;
            }
        }
        return false;
    }

    static bool IsLandscapeSrc(string src) => src != null && src.IndexOf("Landscape", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>A steep (|n.y| &lt; 0.65) landscape triangle crosses the horizontal segment.</summary>
    static bool HitsSteepLandscape(Dictionary<(int, int), List<FloorTri>> grid, float ox, float oy, float oz, float dx, float dz, float len)
    {
        var cells = new HashSet<(int, int)> { CellOf(ox, oz), CellOf(ox + dx * len, oz + dz * len) };
        foreach (var c in cells)
        {
            if (!grid.TryGetValue(c, out var list)) continue;
            foreach (var t in list)
            {
                if (!IsLandscapeSrc(t.Source)) continue;
                float e1x = t.X1 - t.X0, e1y = t.Y1 - t.Y0, e1z = t.Z1 - t.Z0, e2x = t.X2 - t.X0, e2y = t.Y2 - t.Y0, e2z = t.Z2 - t.Z0;
                float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl < 1e-9f || MathF.Abs(ny / nl) >= 0.65f) continue;
                // Moller-Trumbore, direction (dx, 0, dz)
                float px = dz * e2y, py = dx * e2z - dz * e2x, pz = -dx * e2y; // p = dir x e2
                float det = e1x * px + e1y * py + e1z * pz;
                if (MathF.Abs(det) < 1e-9f) continue;
                float inv = 1 / det, sx = ox - t.X0, sy = oy - t.Y0, sz = oz - t.Z0;
                float u = (sx * px + sy * py + sz * pz) * inv;
                if (u < 0 || u > 1) continue;
                float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                float v = (dx * qx + dz * qz) * inv;
                if (v < 0 || u + v > 1) continue;
                float dist = (e2x * qx + e2y * qy + e2z * qz) * inv;
                if (dist >= 0 && dist <= len) return true;
            }
        }
        return false;
    }

    static string ShortSrc(string src) => src == null ? "?" : src.Replace("G3_Myrtana_", "").Replace("Landscape_Cell_", "L").Replace("_Level_", "_");

    /// <summary>Every collision surface at (x, z), highest first.</summary>
    static List<(float y, string src)> Surfaces(Dictionary<(int, int), List<FloorTri>> grid, float x, float z)
    {
        var o = new List<(float, string)>();
        if (grid.TryGetValue(CellOf(x, z), out var list))
            foreach (var t in list)
                if (HeightOn(t, x, z, out float y)) o.Add((y, t.Source));
        o.Sort((a, b) => b.Item1.CompareTo(a.Item1));
        // one entry per surface (two-sided / shared edges give duplicates)
        var d = new List<(float, string)>();
        foreach (var s in o)
            if (d.Count == 0 || Math.Abs(d[d.Count - 1].Item1 - s.Item1) > 0.02f || d[d.Count - 1].Item2 != s.Item2) d.Add(s);
        return d;
    }

    /// <summary>
    /// fix-148: walls the column cannot see. Horizontal rays from (x, y, z) to (x2, y, z2) for y = y0..y1 (step dy, default
    /// 0.1 m), each with its first hit (distance, height, source mesh). Box shapes contribute only their top and bottom
    /// faces here (<see cref="LoadFloorTriangles"/>), so a box wall is not seen.
    /// </summary>
    static int Sweep(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float[] p)
    {
        float x2 = p[0], z2 = p[1], y0 = p[2], y1 = p[3], dy = p.Length > 4 ? p[4] : 0.1f;
        var all = grid.Values.SelectMany(l => l).Distinct().ToList();
        float dx = x2 - x, dz = z2 - z, len = (float)Math.Sqrt(dx * dx + dz * dz);
        for (float y = y0; y <= y1 + 1e-4f; y += dy)
        {
            float best = float.MaxValue; string src = null;
            foreach (var t in all)
            {
                float d = RayTri(x, y, z, dx / len, dz / len, t);
                if (d >= 0 && d <= len && d < best) { best = d; src = t.Source; }
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  y {0,7:F2}: {1}", y,
                src == null ? "free" : string.Format(CultureInfo.InvariantCulture, "hit at {0:F2} m ({1:F2}, {2:F2}) {3}", best, x + dx / len * best, z + dz / len * best, ShortSrc(src))));
        }
        return 0;
    }

    /// <summary>Möller–Trumbore, horizontal direction (ux, 0, uz); distance or -1.</summary>
    static float RayTri(float ox, float oy, float oz, float ux, float uz, FloorTri t)
    {
        float e1x = t.X1 - t.X0, e1y = t.Y1 - t.Y0, e1z = t.Z1 - t.Z0, e2x = t.X2 - t.X0, e2y = t.Y2 - t.Y0, e2z = t.Z2 - t.Z0;
        float px = -uz * e2y, py = uz * e2x - ux * e2z, pz = ux * e2y; // d x e2, d = (ux, 0, uz)
        float det = e1x * px + e1y * py + e1z * pz;
        if (Math.Abs(det) < 1e-9f) return -1;
        float inv = 1 / det, sx = ox - t.X0, sy = oy - t.Y0, sz = oz - t.Z0;
        float u = (sx * px + sy * py + sz * pz) * inv;
        if (u < 0 || u > 1) return -1;
        float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
        float v = (ux * qx + uz * qz) * inv;
        if (v < 0 || u + v > 1) return -1;
        return (e2x * qx + e2y * qy + e2z * qz) * inv;
    }

    /// <summary>
    /// fix-148: a standing capsule (radius r, height h, feet on the highest floor within 0.65 m of the last feet) moved
    /// along x z -> x2 z2; each place where a collision triangle lies closer than r to its axis above the feet + 0.66 m (above
    /// step height: what really holds him) is printed with the source and the height of the touch.
    /// </summary>
    static int CapsuleWalk(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float[] p)
    {
        float x2 = p[0], z2 = p[1], r = p[2], h = p[3], ds = p.Length > 4 ? p[4] : 0.05f;
        var all = grid.Values.SelectMany(l => l).Distinct().ToList();
        float len = (float)Math.Sqrt((x2 - x) * (x2 - x) + (z2 - z) * (z2 - z));
        float feet = p.Length > 5 ? p[5] : float.NaN; // start feet height (else the highest surface)
        int bad = 0;
        for (float d = 0; d <= len + 1e-4f; d += ds)
        {
            float px = x + (x2 - x) * d / len, pz = z + (z2 - z) * d / len;
            var col = Surfaces(grid, px, pz);
            float f = float.NaN;
            foreach (var (y, _) in col)
                if (float.IsNaN(feet) ? y < 1e6f && (float.IsNaN(f)) : y <= feet + 0.65f) { f = y; break; }
            if (float.IsNaN(f)) { Console.WriteLine($"  {d,5:F2} m: no floor"); continue; }
            feet = f;
            float best = float.MaxValue, at = 0; string src = null;
            foreach (var t in all)
            {
                for (float yy = feet + r; yy <= feet + h - r + 1e-4f; yy += 0.05f)
                {
                    float dist = PointTri(px, yy, pz, t, out float cy);
                    if (dist < r && cy > feet + 0.66f && dist < best) { best = dist; at = cy - feet; src = t.Source; }
                }
            }
            if (src != null && bad++ < 40)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,5:F2} m ({1:F2}, {2:F2}) feet {3:F2}: touches {4} {5:F2} m above the feet, {6:F2} m from the axis", d, px, pz, feet, ShortSrc(src), at, best));
        }
        Console.WriteLine(bad == 0 ? "capsule passes" : $"capsule touches at {bad} places");
        return 0;
    }

    /// <summary>Distance from (px, py, pz) to triangle t; cy = height of the closest point.</summary>
    static float PointTri(float px, float py, float pz, FloorTri t, out float cy)
    {
        // Ericson, Real-Time Collision Detection 5.1.5
        float ax = t.X0, ay = t.Y0, az = t.Z0, bx = t.X1, by = t.Y1, bz = t.Z1, qx = t.X2, qy = t.Y2, qz = t.Z2;
        float abx = bx - ax, aby = by - ay, abz = bz - az, acx = qx - ax, acy = qy - ay, acz = qz - az;
        float apx = px - ax, apy = py - ay, apz = pz - az;
        float d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        float rx, ry, rz;
        if (d1 <= 0 && d2 <= 0) { rx = ax; ry = ay; rz = az; goto done; }
        float bpx = px - bx, bpy = py - by, bpz = pz - bz;
        float d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
        if (d3 >= 0 && d4 <= d3) { rx = bx; ry = by; rz = bz; goto done; }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); rx = ax + v * abx; ry = ay + v * aby; rz = az + v * abz; goto done; }
        float cpx = px - qx, cpy = py - qy, cpz = pz - qz;
        float d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
        if (d6 >= 0 && d5 <= d6) { rx = qx; ry = qy; rz = qz; goto done; }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); rx = ax + w * acx; ry = ay + w * acy; rz = az + w * acz; goto done; }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            rx = bx + w * (qx - bx); ry = by + w * (qy - by); rz = bz + w * (qz - bz); goto done;
        }
        {
            float den = 1 / (va + vb + vc), v = vb * den, w = vc * den;
            rx = ax + abx * v + acx * w; ry = ay + aby * v + acy * w; rz = az + abz * v + acz * w;
        }
        done:
        cy = ry;
        float ex = px - rx, ey = py - ry, ez = pz - rz;
        return (float)Math.Sqrt(ex * ex + ey * ey + ez * ez);
    }
}
