using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using G3.Formats.Pak;
using G3.Player;
using V3 = System.Numerics.Vector3;

static partial class Program
{
    /// <summary>
    /// fix-181 <c>relief wander [--npcs file] [--top N] [--box cx cz half]</c>: for every wandering creature (non-humanoid,
    /// no routine) the ring of its wander points (G3 ZS_Ambient: 3-5 m from the spawn, 2-4 m for ambient creatures, at the
    /// spawn's height) against the landscape collision: the height a shown Near body gets there (the column probe from the
    /// point's height, <see cref="NpcGroundRules.Pick"/>, the fallback ray, else the point's own height) versus the floor an
    /// Active walk ends on (the column up to 8 m above, fix-108). Lists the creatures whose Near body would be drawn inside
    /// the hill (no surface found: kept at the spawn's height under the slope).
    /// </summary>
    static int ReliefWander(string data, string[] args)
    {
        string npcs = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/Gothic3/npcs/npcs.json");
        int top = 30;
        float bx = 0, bz = 0, bh = -1;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--npcs") npcs = args[++i];
            else if (args[i] == "--top") top = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--box") { bx = F(args[++i]); bz = F(args[++i]); bh = F(args[++i]); }
        }
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        var spawns = new List<(string id, string species, bool ambient, V3 p)>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(npcs)))
            foreach (var n in doc.RootElement.EnumerateArray())
            {
                if (!n.TryGetProperty("spawn", out var sp) || n.TryGetProperty("routine", out _) || n.TryGetProperty("routines", out _)) continue;
                bool humanoid;
                string species = n.TryGetProperty("species", out var s) ? s.GetString() : "Human";
                bool ambient = false;
                if (n.TryGetProperty("combat", out var c))
                {
                    humanoid = c.TryGetProperty("humanoid", out var h) && h.GetBoolean();
                    ambient = c.TryGetProperty("ambientCreature", out var a) && a.GetBoolean();
                }
                else humanoid = species == "Human" || species == "Orc";
                if (humanoid) continue;
                var p = new V3(sp.GetProperty("x").GetSingle(), sp.GetProperty("y").GetSingle(), sp.GetProperty("z").GetSingle());
                if (bh > 0 && (Math.Abs(p.X - bx) > bh || Math.Abs(p.Z - bz) > bh)) continue;
                spawns.Add((n.GetProperty("id").GetString(), species, ambient, p));
            }
        Console.WriteLine($"{spawns.Count} wandering creatures");

        // landscape collision triangles, bucketed by 4 m
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        const float B = 4f;
        var buckets = new Dictionary<(int, int), List<(V3 a, V3 b, V3 c)>>();
        var need = new HashSet<(int, int)>();
        foreach (var sp in spawns)
            for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
                need.Add(((int)Math.Floor(sp.p.X / B) + dx, (int)Math.Floor(sp.p.Z / B) + dz));
        foreach (var (name, mr) in LoadLandscapeCells(fs))
        {
            var m = ToRelief(mr);
            foreach (var t in m.Sub)
                for (int i = 0; i < t.Length; i += 3)
                {
                    V3 a = m.Pos[t[i]], b = m.Pos[t[i + 1]], c = m.Pos[t[i + 2]];
                    var fn = V3.Cross(b - a, c - a);
                    if (fn.Y <= 0) continue; // only faces up (the column's floors)
                    int x0 = (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / B), x1 = (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) / B);
                    int z0 = (int)Math.Floor(Math.Min(a.Z, Math.Min(b.Z, c.Z)) / B), z1 = (int)Math.Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) / B);
                    if (x1 - x0 > 64 || z1 - z0 > 64) continue;
                    for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!need.Contains((x, z))) continue;
                        if (!buckets.TryGetValue((x, z), out var l)) buckets[(x, z)] = l = new List<(V3, V3, V3)>();
                        l.Add((a, b, c));
                    }
                }
        }

        var surfaces = new List<NpcGroundRules.Surface>();
        void Column(float x, float z)
        {
            surfaces.Clear();
            if (!buckets.TryGetValue(((int)Math.Floor(x / B), (int)Math.Floor(z / B)), out var l)) return;
            foreach (var (a, b, c) in l)
            {
                float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (Math.Abs(d) < 1e-12f) continue;
                float w0 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
                float w1 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
                float w2 = 1 - w0 - w1;
                if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                var fn = V3.Normalize(V3.Cross(b - a, c - a));
                surfaces.Add(new NpcGroundRules.Surface(w0 * a.Y + w1 * b.Y + w2 * c.Y, MathF.Acos(Math.Clamp(fn.Y, -1, 1)) * 180f / MathF.PI));
            }
        }
        // the surfaces a ray down from 'from' meets (a ray starting inside the hill does not see its top face)
        List<NpcGroundRules.Surface> Below(float from) => surfaces.Where(s => s.Y <= from).ToList();

        var rows = new List<(float worst, string line)>();
        int buriedCreatures = 0, points = 0, buriedPoints = 0;
        foreach (var sp in spawns)
        {
            float half = sp.species == "Wolf" ? 0.65f : 0.5f;
            float r0 = sp.ambient ? 2f : 3f;
            float worst = 0; int bad = 0, n = 0; string at = "";
            for (int k = 0; k < 16; k++)
            for (float r = r0; r <= r0 + 2.001f; r += 1f)
            {
                float x = sp.p.X + MathF.Sin(k * MathF.PI / 8) * r, z = sp.p.Z + MathF.Cos(k * MathF.PI / 8) * r, y = sp.p.Y;
                Column(x, z);
                if (surfaces.Count == 0) continue;
                n++;
                // Active walk end (NpcView.Destination): the column up to 8 m above, the surface nearest the point's height
                var all = Below(y + 8.05f);
                if (!NpcGroundRules.Pick(y, all, half, 150f, float.NaN, out int ia, 8f)) continue;
                float active = all[ia].Y;
                // Near body (GroundProbe.HeightAt, no NavMesh while moving): column from 3.05 m above, then the ray from 1.5 m, else y
                var col = Below(y + Math.Max(NpcGroundRules.MaxUpReach, NpcGroundRules.BuriedReach) + 0.05f);
                float shown;
                if (NpcGroundRules.Pick(y, col, half, 150f, float.NaN, out int ib)) shown = col[ib].Y;
                else { var ray = Below(y + 1.5f); shown = ray.Count > 0 ? ray.Max(s => s.Y) : y; }
                float buried = active - shown;
                if (buried > 0.3f)
                {
                    bad++;
                    if (buried > worst) { worst = buried; at = $"({x:F1},{z:F1}) floor {active:F1} shown {shown:F1}"; }
                }
            }
            points += n; buriedPoints += bad;
            if (bad > 0)
            {
                buriedCreatures++;
                rows.Add((worst, $"{sp.id,-34} {sp.species,-12} spawn ({sp.p.X:F1},{sp.p.Y:F1},{sp.p.Z:F1}) {bad}/{n} wander points drawn up to {worst:F1} m inside the ground, worst {at}"));
            }
        }
        Console.WriteLine($"{buriedCreatures} of {spawns.Count} wanderers have wander points whose Near body is drawn > 0.3 m under the floor ({buriedPoints} of {points} points)");
        foreach (var r in rows.OrderByDescending(r => r.worst).Take(top)) Console.WriteLine("  " + r.line);
        return 0;
    }
}
