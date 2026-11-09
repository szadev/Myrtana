using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using G3.Formats.Items;

/// <summary>
/// <c>collision itemsight [x z radius] [-v] [--list N]</c> (fix-254): can the hero see each world item from somewhere he can
/// stand within pick-up reach? Per item: stand points on 16 directions × 0.6..2.2 m, feet on a collision floor near the
/// item's height with 1.8 m of headroom, sight point 1.35 m up (G3 IsFreeLOS: 3/4 of a man's box); one ray to the item
/// (pivot + 0.1 m) against the collision the cell import builds (<see cref="LoadFloorTriangles"/>, boxes with all faces).
/// Two verdicts: "two-sided" (every triangle blocks: the focus ray before fix-254, our colliders are two-sided) and
/// "G3" (PhysX 2 traces triangle meshes one-sided: a ray meets only faces whose stored winding faces it; solid shapes and
/// render-mesh fallbacks always block). Prints the items G3 can focus but the two-sided ray cannot, with their blockers.
/// </summary>
static partial class Program
{
    static int CollisionItemSight(string data, string[] args)
    {
        var pos = args.Skip(2).TakeWhile(a => float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _)).Select(a => float.Parse(a, CultureInfo.InvariantCulture)).ToArray();
        bool region = pos.Length >= 3;
        float rx = region ? pos[0] : 0, rz = region ? pos[1] : 0, rr = region ? pos[2] : float.MaxValue;
        bool verbose = args.Contains("-v");
        int list = int.Parse(Option(args, "--list") ?? "60");

        var scan = ScanWorld(data);
        var items = scan.Items.Where(i => (i.Kind == WorldItemKind.Loose || i.Kind == WorldItemKind.Plant) && i.Entity.Enabled)
            .Select(i => (name: i.Entity.Name, x: i.Entity.X / 100f, y: i.Entity.Y / 100f, z: i.Entity.Z / 100f))
            .Where(i => !region || (i.x - rx) * (i.x - rx) + (i.z - rz) * (i.z - rz) <= rr * rr).ToList();
        var cells = new HashSet<(int, int)>();
        foreach (var i in items)
            for (float dx = -3; dx <= 3; dx += FloorCell / 2)
            for (float dz = -3; dz <= 3; dz += FloorCell / 2)
                cells.Add(CellOf(i.x + dx, i.z + dz));
        bool map = (args.Contains("--map") || args.Contains("--reach")) && region;
        if (map)
            for (float dx = -rr - FloorCell; dx <= rr + FloorCell; dx += FloorCell / 2)
            for (float dz = -rr - FloorCell; dz <= rr + FloorCell; dz += FloorCell / 2)
                cells.Add(CellOf(rx + dx, rz + dz));
        var grid = LoadFloorTriangles(data, cells, out int entities, landscapeXnv: true, fullBoxes: true);
        Console.WriteLine($"items {items.Count}, collision entities {entities}");
        _reversedWinding = args.Contains("--reversed");
        _ignorePlants = args.Contains("--noplants");
        _floorOnly = args.Contains("--flooronly");
        _head = float.Parse(Option(args, "--head") ?? "1.8", CultureInfo.InvariantCulture);
        var reach = Option(args, "--reach");
        if (reach != null && region)
        {
            var q = reach.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            return ReachMap(grid, rx, rz, rr, q[0], q[1], items);
        }
        if (map) return StandMap(grid, rx, rz, rr, pos.Length > 3 ? pos[3] : items.Count > 0 ? items.Average(i => i.y) : 0);

        int both = 0, none = 0, onlyG3 = 0, onlyTwo = 0, noStand = 0, plantOnly = 0;
        var plantLines = new List<string>();
        var enclosed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shellLines = new List<string>();
        var plantAround = new List<string>();
        var blockers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var lk = new object();
        System.Threading.Tasks.Parallel.ForEach(items, it =>
        {
            var (g3, two, stands, block, noPlant) = ItemSight(grid, it.x, it.y, it.z, verbose && region);
            string shell = EnclosingShell(grid, it.x, it.y + 0.3f, it.z);
            string plant = PlantAround(grid, it.x, it.y + 0.3f, it.z);
            lock (lk)
            {
                if (plant != null) plantAround.Add(string.Format(CultureInfo.InvariantCulture, "  in plant {4}: {0,-28} ({1:F2}, {2:F2}, {3:F2})", it.name, it.x, it.y, it.z, plant));
                if (shell != null) { enclosed[shell] = enclosed.TryGetValue(shell, out int e) ? e + 1 : 1;
                    shellLines.Add(string.Format(CultureInfo.InvariantCulture, "  inside {4}: {0,-28} ({1:F2}, {2:F2}, {3:F2})", it.name, it.x, it.y, it.z, shell)); }
                if (stands == 0) { noStand++; return; }
                if (!two && noPlant) { plantOnly++; plantLines.Add(string.Format(CultureInfo.InvariantCulture, "  plant-blocked {0,-28} ({1:F2}, {2:F2}, {3:F2})", it.name, it.x, it.y, it.z)); }
                if (g3 && two) both++;
                else if (!g3 && !two) none++;
                else if (g3) { onlyG3++; foreach (var b in block) blockers[b] = blockers.TryGetValue(b, out int n) ? n + 1 : 1;
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0,-28} ({1:F2}, {2:F2}, {3:F2}) blocked two-sided by {4}", it.name, it.x, it.y, it.z, string.Join(", ", block))); }
                else onlyTwo++;
            }
        });
        foreach (var l in lines.OrderBy(l => l).Take(list)) Console.WriteLine(l);
        foreach (var l in plantLines.OrderBy(l => l).Take(list)) Console.WriteLine(l);
        foreach (var l in shellLines.OrderBy(l => l).Take(list)) Console.WriteLine(l);
        foreach (var l in plantAround.OrderBy(l => l)) Console.WriteLine(l);
        Console.WriteLine($"items inside a plant mesh (>= 24 of 32 horizontal rays meet it within 6 m): {plantAround.Count}");
        Console.WriteLine($"items enclosed by one mesh (every horizontal ray 0.3 m over the item meets it within 6 m): {enclosed.Values.Sum()}: " +
                          string.Join(", ", enclosed.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
        Console.WriteLine($"items no stand point sees two-sided but some does with plant meshes ignored: {plantOnly}; plant blockers: {string.Join(", ", _plantBlocked.Distinct())}");
        Console.WriteLine($"seen both ways {both}; seen by G3 only (one-sided trace) {onlyG3}; seen two-sided only {onlyTwo}; seen neither {none}; no stand point {noStand}");
        foreach (var kv in blockers.OrderByDescending(k => k.Value).Take(30)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        return 0;
    }

    /// <summary>
    /// <c>--map</c> (x z r [y]): a 0.25 m grid round x z: '.' a floor near y with 1.8 m of headroom, ':' a floor with less
    /// headroom (under a low ceiling or a mesh), ' ' no floor near that height; rows north (+z) first.
    /// </summary>
    static int StandMap(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float r, float y)
    {
        const float step = 0.25f;
        for (float pz = z + r; pz >= z - r; pz -= step)
        {
            var row = new System.Text.StringBuilder();
            for (float px = x - r; px <= x + r; px += step)
            {
                var surf = Surfaces(grid, px, pz);
                char ch = ' ';
                for (int i = 0; i < surf.Count; i++)
                {
                    float fy = surf[i].y;
                    if (fy > y + 1.2f || fy < y - 1.5f) continue;
                    bool head = true;
                    for (int j = 0; j < i; j++) if (surf[j].y > fy + 0.2f && surf[j].y <= fy + 1.8f) head = false;
                    ch = head ? '.' : ':';
                    break;
                }
                row.Append(ch);
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,8:F2} |{1}|", pz, row));
        }
        return 0;
    }

    /// <summary>
    /// <c>--reach x0,z0</c>: flood fill of the stand grid (0.25 m) from x0 z0 within the region: a step to a neighbour is
    /// open when its floor is within 0.5 m and three horizontal rays (0.5, 1.0, 1.6 m over the feet, 0.3 m past the
    /// neighbour) meet no triangle; twice: every face blocks (our two-sided colliders) and only front faces block. Then per
    /// item the nearest reached stand point and whether the focus ray from it is free.
    /// </summary>
    static int ReachMap(Dictionary<(int, int), List<FloorTri>> grid, float x, float z, float r, float x0, float z0,
                        List<(string name, float x, float y, float z)> items)
    {
        const float step = 0.25f;
        int n = (int)MathF.Ceiling(r / step);
        float y0 = items.Count > 0 ? items.Average(i => i.y) : 0;
        float Floor(float px, float pz, float near)
        {
            var surf = Surfaces(grid, px, pz);
            float best = float.NaN;
            for (int i = 0; i < surf.Count; i++)
            {
                float fy = surf[i].y;
                if (fy > near + 0.7f || fy < near - 1.5f) continue;
                bool head = true;
                for (int j = 0; j < i; j++) if (surf[j].y > fy + 0.2f && surf[j].y <= fy + _head) head = false;
                if (head) { best = fy; break; }
            }
            return best;
        }
        foreach (bool oneSided in new[] { false, true })
        {
            var feet = new Dictionary<(int, int), float>();
            var open = new Queue<(int, int)>();
            int si = (int)MathF.Round((x0 - x) / step), sk = (int)MathF.Round((z0 - z) / step);
            float sf = Floor(x + si * step, z + sk * step, y0);
            if (float.IsNaN(sf)) { Console.WriteLine("start has no floor"); return 1; }
            feet[(si, sk)] = sf; open.Enqueue((si, sk));
            while (open.Count > 0)
            {
                var (i, k) = open.Dequeue();
                float f = feet[(i, k)], px = x + i * step, pz = z + k * step;
                foreach (var (di, dk) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var nb = (i + di, k + dk);
                    if (Math.Abs(nb.Item1) > n || Math.Abs(nb.Item2) > n || feet.ContainsKey(nb)) continue;
                    float nx = x + nb.Item1 * step, nz = z + nb.Item2 * step;
                    float nf = Floor(nx, nz, f);
                    if (float.IsNaN(nf) || MathF.Abs(nf - f) > FloorStep) continue;
                    bool blocked = false;
                    foreach (float h in new[] { 0.7f, 1.0f, _head - 0.2f })
                    {
                        float hy = Math.Max(f, nf) + h;
                        var (g3, two, _) = SightRay(grid, px, hy, pz, nx + di * 0.3f, hy, nz + dk * 0.3f, 0f);
                        if (!(oneSided ? g3 : two)) { blocked = true; break; }
                    }
                    if (blocked) continue;
                    feet[nb] = nf; open.Enqueue(nb);
                }
            }
            Console.WriteLine($"{(oneSided ? "front faces block" : "every face blocks")}: {feet.Count} stand points reached");
            foreach (var it in items)
            {
                float bestD = float.MaxValue; string verdict = "unreached";
                foreach (var kv in feet)
                {
                    float px = x + kv.Key.Item1 * step, pz = z + kv.Key.Item2 * step;
                    float d = MathF.Sqrt((px - it.x) * (px - it.x) + (pz - it.z) * (pz - it.z));
                    if (d >= bestD || (_floorOnly && MathF.Abs(kv.Value - it.y) > 0.6f)) continue;
                    var (g3, two, first) = SightRay(grid, px, kv.Value + 1.35f, pz, it.x, it.y + 0.1f, it.z, 0.1f, _ignorePlants);
                    bool free = oneSided ? g3 : two;
                    if (!free) continue;
                    bestD = d; verdict = string.Format(CultureInfo.InvariantCulture, "seen from ({0:F2}, {1:F2}, {2:F2}) at {3:F2} m", px, kv.Value, pz, d);
                }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-24} ({1:F2}, {2:F2}, {3:F2}): {4}", it.name, it.x, it.y, it.z, verdict));
            }
        }
        return 0;
    }

    /// <summary>Dev switch: treat the stored winding as the back face (checks the winding convention).</summary>
    static bool _reversedWinding;
    static readonly System.Collections.Concurrent.ConcurrentBag<string> _plantBlocked = new System.Collections.Concurrent.ConcurrentBag<string>();
    /// <summary>Dev: the headroom the reach flood fill needs (m, --head).</summary>
    static float _head = 1.8f;
    static bool _ignorePlants, _floorOnly;

    static readonly float[] SightDistances = { 0.6f, 1.0f, 1.4f, 1.8f, 2.2f };

    /// <summary>Whether some stand point sees the item G3's way / two-sided, how many stand points there are, and the two-sided blockers.</summary>
    static (bool g3, bool two, int stands, List<string> blockers, bool noPlant) ItemSight(Dictionary<(int, int), List<FloorTri>> grid, float x, float y, float z, bool print)
    {
        bool g3 = false, two = false, noPlant = false; int stands = 0;
        var block = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        float tx = x, ty = y + 0.1f, tz = z;
        for (int k = 0; k < 16; k++)
        {
            float a = k * MathF.PI / 8, ux = MathF.Cos(a), uz = MathF.Sin(a);
            foreach (float d in SightDistances)
            {
                float sx = x + ux * d, sz = z + uz * d;
                var surf = Surfaces(grid, sx, sz);
                float feet = float.NaN;
                for (int i = 0; i < surf.Count; i++)
                {
                    float fy = surf[i].y;
                    if (fy > y + 0.6f || fy < y - 1.5f) continue;
                    bool head = true;
                    for (int j = 0; j < i; j++) if (surf[j].y > fy + 0.2f && surf[j].y <= fy + 1.8f) head = false;
                    if (head) { feet = fy; break; }
                }
                if (float.IsNaN(feet)) continue;
                stands++;
                float sy = feet + 1.35f;
                var (freeG3, freeTwo, first) = SightRay(grid, sx, sy, sz, tx, ty, tz);
                if (print) Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "    stand ({0:F2}, {1:F2}, {2:F2}): G3 {3}, two-sided {4} {5}", sx, feet, sz, freeG3 ? "free" : "blocked", freeTwo ? "free" : "blocked", first));
                g3 |= freeG3; two |= freeTwo;
                if (!freeTwo && freeG3 && first != null) block.Add(first);
                if (!two && !noPlant && SightRay(grid, sx, sy, sz, tx, ty, tz, 0.1f, true).two) { noPlant = true; if (first != null) _plantBlocked.Add(first); }
            }
        }
        return (g3, two, stands, block.ToList(), noPlant || two);
    }

    /// <summary>
    /// The one collision mesh that every one of 32 horizontal rays from (x, y, z) meets first within 6 m (the item lies in its
    /// hollow: a trunk, a hut, a closed room), or null.
    /// </summary>
    static string EnclosingShell(Dictionary<(int, int), List<FloorTri>> grid, float x, float y, float z)
    {
        string shell = null;
        for (int k = 0; k < 32; k++)
        {
            float a = k * MathF.PI / 16, ux = MathF.Cos(a), uz = MathF.Sin(a);
            float best = 6f; string src = null;
            var cs = new HashSet<(int, int)>();
            for (float s = 0; s <= 6f; s += FloorCell / 2) cs.Add(CellOf(x + ux * s, z + uz * s));
            foreach (var c in cs)
                if (grid.TryGetValue(c, out var l))
                    foreach (var t in l)
                    {
                        float d = RayTri(x, y, z, ux, uz, t);
                        if (d >= 0 && d < best) { best = d; src = t.Source; }
                    }
            if (src == null || (shell != null && !string.Equals(shell, src, StringComparison.OrdinalIgnoreCase))) return null;
            shell = src;
        }
        return shell;
    }

    /// <summary>The plant mesh that at least 24 of 32 horizontal rays from (x, y, z) meet within 6 m (the item lies in its hollow), or null.</summary>
    static string PlantAround(Dictionary<(int, int), List<FloorTri>> grid, float x, float y, float z)
    {
        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < 32; k++)
        {
            float a = k * MathF.PI / 16, ux = MathF.Cos(a), uz = MathF.Sin(a);
            var cs = new HashSet<(int, int)>();
            for (float s = 0; s <= 6f; s += FloorCell / 2) cs.Add(CellOf(x + ux * s, z + uz * s));
            var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cs)
                if (grid.TryGetValue(c, out var l))
                    foreach (var t in l)
                    {
                        if (!IsPlantSource(t.Source) || hit.Contains(t.Source)) continue;
                        float d = RayTri(x, y, z, ux, uz, t);
                        if (d >= 0 && d < 6f) hit.Add(t.Source);
                    }
            foreach (var h in hit) count[h] = count.TryGetValue(h, out int n) ? n + 1 : 1;
        }
        var best = count.OrderByDescending(k => k.Value).FirstOrDefault();
        return best.Value >= 24 ? best.Key : null;
    }

    /// <summary>A decorative plant mesh (G3_Object_Plant_*: rotten trunks, ferns, reed; G3_*_Objects_Plants_*).</summary>
    static bool IsPlantSource(string src) => src != null && (src.IndexOf("_Plant_", StringComparison.OrdinalIgnoreCase) >= 0 || src.IndexOf("_Plants_", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>One sight ray: free for G3 (one-sided triangle meshes), free two-sided, and the first two-sided blocker.</summary>
    static (bool g3, bool two, string first) SightRay(Dictionary<(int, int), List<FloorTri>> grid, float ox, float oy, float oz, float tx, float ty, float tz, float endTolerance = 0.1f, bool ignorePlants = false)
    {
        float dx = tx - ox, dy = ty - oy, dz = tz - oz, len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 0.01f) return (true, true, null);
        dx /= len; dy /= len; dz /= len;
        bool g3 = true, two = true; float best = float.MaxValue; string first = null;
        var seen = new HashSet<FloorTri>();
        var cs = new HashSet<(int, int)>();
        for (float s = 0; s <= len + FloorCell / 2; s += FloorCell / 2) cs.Add(CellOf(ox + dx * Math.Min(s, len), oz + dz * Math.Min(s, len)));
        foreach (var c in cs)
        {
            if (!grid.TryGetValue(c, out var l)) continue;
            foreach (var t in l)
            {
                if (!seen.Add(t)) continue;
                if (ignorePlants && IsPlantSource(t.Source)) continue;
                float e1x = t.X1 - t.X0, e1y = t.Y1 - t.Y0, e1z = t.Z1 - t.Z0, e2x = t.X2 - t.X0, e2y = t.Y2 - t.Y0, e2z = t.Z2 - t.Z0;
                float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                float det = e1x * px + e1y * py + e1z * pz;
                if (MathF.Abs(det) < 1e-12f) continue;
                float inv = 1 / det, sx = ox - t.X0, sy = oy - t.Y0, sz = oz - t.Z0;
                float u = (sx * px + sy * py + sz * pz) * inv;
                if (u < 0 || u > 1) continue;
                float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                float v = (dx * qx + dy * qy + dz * qz) * inv;
                if (v < 0 || u + v > 1) continue;
                float dist = (e2x * qx + e2y * qy + e2z * qz) * inv;
                // the item's own end: 10 cm (FocusSight.EndToleranceM); a solid shape near the item is what it lies on or in (fix-114)
                float end = endTolerance <= 0 ? 0f : t.Side == 0 ? 0.6f : endTolerance;
                if (dist < 0 || dist >= len - end) continue;
                two = false;
                if (dist < best) { best = dist; first = ShortSrc(t.Source); }
                // n = e1 x e2; the stored face meets the ray when dir . n < 0 (det = -(dir . n))
                bool front = (det > 0) != _reversedWinding;
                if (t.Side != 1 || front) g3 = false;
            }
        }
        return (g3, two, first);
    }
}
