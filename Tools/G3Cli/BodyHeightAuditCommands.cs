using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

/// <summary>
/// <c>bodies [x z radius] [--pack dir] [--save file.sav] [--tol 0.15] [--top 40] [--new]</c> (fix-163, owner: a body sunk
/// halfway into a yard's floor): every place a body is put without walking there — routine point slots (a station NPC
/// stands at the slot's authored height), NPC spawns (placed by <see cref="NpcGroundRules.Pick"/> from the column's
/// surfaces) and, with <c>--save</c>, every NPC pose a save holds (alive and dead) — compared with the walkable surface
/// directly under the body: the highest up-facing collision surface (slope ≤ <see cref="NpcGroundRules.MaxFloorSlope"/>) no
/// higher than <see cref="BodySinkRules.Reach"/> over the feet. A placement more than <c>tol</c> m under it is sunk,
/// more than <c>tol</c> above it floats (only with nothing in between). Collision as the cell import builds it
/// (<see cref="LoadFloorTriangles"/>, landscape from its .xnvmsh), no NavMesh (the runtime's first rule; the NavMesh lies on
/// these surfaces). <c>--new</c> also places stations with <see cref="BodySinkRules.StationFloor"/> (the fixed runtime) and
/// counts what stays off. Counts per cause (kind, sunk/float, floor source class, interaction), worst first.
/// </summary>
static partial class Program
{
    static readonly HashSet<string> FurnitureInteractions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bed", "Bench", "Stool", "Throne" };

    static int BodyHeights(string data, string[] args)
    {
        var pos = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).Select(a => float.Parse(a, CultureInfo.InvariantCulture)).ToArray();
        bool region = pos.Length >= 3;
        float cx = region ? pos[0] : 0, cz = region ? pos[1] : 0, radius = region ? pos[2] : float.MaxValue;
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        float tol = float.Parse(Option(args, "--tol") ?? "0.15", CultureInfo.InvariantCulture);
        int top = int.Parse(Option(args, "--top") ?? "40");
        bool fixedRules = args.Contains("--new");
        string save = Option(args, "--save");

        var db = Myrtana.Session.GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) }).Database;
        var objects = new HashSet<string>(db.All<InteractiveObjectDef>().Select(o => o.Id), StringComparer.OrdinalIgnoreCase);
        var probes = new List<(string kind, string name, string what, float x, float y, float z, bool raw)>();
        foreach (var p in db.All<RoutinePointDef>())
            for (int i = 0; i < p.Slots.Count; i++)
            {
                var s = p.Slots[i];
                if (!In(s.X, s.Z)) continue;
                string kind = FurnitureInteractions.Contains(p.Interaction ?? "") ? "furniture" : objects.Contains(p.Id.Name) ? "objslot" : "slot";
                probes.Add((kind, p.Id.Name + (p.Slots.Count > 1 ? "#" + i : ""), p.Interaction ?? "-", s.X, s.Y, s.Z, true));
            }
        foreach (var n in db.All<NpcDef>())
            if (n.Spawn is WorldPose sp && !n.Id.IsPlayer && In(sp.X, sp.Z)) probes.Add(("spawn", n.Id.Name, n.Species ?? "-", sp.X, sp.Y, sp.Z, false));
        if (save != null)
            foreach (var (id, pose, activity, dead) in SavedNpcPoses(save, db))
                if (In(pose.X, pose.Z))
                    probes.Add((dead ? "save-dead" : "save-" + activity, id, db.TryGet(id, out NpcDef d) ? d.Species ?? "-" : "-", pose.X, pose.Y, pose.Z,
                                activity == NpcActivity.AtStation.ToString() || dead));
        Console.WriteLine($"bodies: {probes.Count} probes: " + string.Join(", ", probes.GroupBy(p => p.kind).Select(g => $"{g.Key} {g.Count()}")) +
                          (region ? $" within {radius} m of ({cx}, {cz})" : ""));

        var cells = new HashSet<(int, int)>(probes.Select(p => CellOf(p.x, p.z)));
        var grid = LoadFloorTriangles(data, cells, out int entities, landscapeXnv: true);
        Console.WriteLine($"collision: {entities} entities, {grid.Values.Sum(l => l.Count)} triangle refs in {grid.Count} cells");

        var rows = new List<(float d, string kind, string cause, string line)>();
        var causes = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts = new Dictionary<string, (int n, int sunk, int floats, int noFloor, int sunkNew, int floatNew)>(StringComparer.Ordinal);
        foreach (var p in probes)
        {
            var col = SlopedSurfaces(grid, p.x, p.z);
            var surfaces = col.Select(c => new NpcGroundRules.Surface(c.y, c.slope)).ToList();
            // the runtime's placement: a station / corpse as authored or saved, the rest on the picked floor
            float placed = p.raw ? p.y
                : NpcGroundRules.Pick(p.y, surfaces, 0.35f, 150f, float.NaN, out int pi) ? surfaces[pi].Y : p.y;
            float gap = BodySinkRules.Gap(placed, surfaces, out int real); // > 0: the body's feet are under the floor
            counts.TryGetValue(p.kind, out var c0);
            c0.n++;
            if (real < 0) { c0.noFloor++; counts[p.kind] = c0; continue; }
            string src = SrcClass(col[real].src);
            bool sunk = gap > tol, floats = -gap > tol;
            if (sunk) c0.sunk++;
            if (floats) c0.floats++;
            if ((sunk || floats) && fixedRules)
            {
                // the fixed runtime: stations through BodySinkRules.StationFloor, then every body through the safety net
                float after = placed;
                if (p.kind == "slot" || p.kind == "objslot" || p.kind == "save-AtStation")
                    after = BodySinkRules.StationFloor(p.y, surfaces, 0.35f, out int si) ? surfaces[si].Y : p.y;
                if (p.kind != "furniture") after += BodySinkRules.SinkLift(after, surfaces, out _); // a bed / bench keeps its height by design
                float left = BodySinkRules.Gap(after, surfaces, out _);
                if (left > tol) c0.sunkNew++;
                if (-left > tol) c0.floatNew++;
            }
            counts[p.kind] = c0;
            if (!sunk && !floats) continue;
            string cause = $"{p.kind} {(sunk ? "sunk" : "float")} into {src}" + (p.kind == "spawn" || p.kind.StartsWith("save") ? "" : $" [{p.what}]");
            causes[cause] = causes.TryGetValue(cause, out int k) ? k + 1 : 1;
            rows.Add((gap, p.kind, cause, string.Format(CultureInfo.InvariantCulture,
                "{0,-10} {1,-46} {2,-14} ({3:F2}, {4:F2}, {5:F2}) placed {6:F2} floor {7:F2} {8:+0.00;-0.00} m ({9})",
                p.kind, p.name, p.what, p.x, p.y, p.z, placed, surfaces[real].Y, gap, ShortSrc(col[real].src))));
        }
        Console.WriteLine($"tolerance {tol} m; per kind: probes / sunk / floating / no floor" + (fixedRules ? " / still sunk / still floating with the fix" : ""));
        foreach (var kv in counts.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,-12} {kv.Value.n,6} {kv.Value.sunk,6} {kv.Value.floats,6} {kv.Value.noFloor,6}" + (fixedRules ? $" {kv.Value.sunkNew,6} {kv.Value.floatNew,6}" : ""));
        Console.WriteLine("causes:");
        foreach (var kv in causes.OrderByDescending(k => k.Value).Take(top)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        Console.WriteLine("worst sunk:");
        foreach (var r in rows.Where(r => r.d > 0).OrderByDescending(r => r.d).Take(top)) Console.WriteLine("  " + r.line);
        Console.WriteLine("worst floating:");
        foreach (var r in rows.Where(r => r.d < 0).OrderBy(r => r.d).Take(top / 2)) Console.WriteLine("  " + r.line);
        return 0;

        bool In(float x, float z) => !region || (x - cx) * (x - cx) + (z - cz) * (z - cz) <= radius * radius;
    }

    static string SrcClass(string src) =>
        src == null ? "?" : src.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase) >= 0 ? "landscape"
        : CaveFloorRules.IsCaveName(src) ? "cave" : "mesh";

    /// <summary>The column's up-facing surfaces at (x, z) with their slopes, highest first (one per surface).</summary>
    static List<(float y, float slope, string src)> SlopedSurfaces(Dictionary<(int, int), List<FloorTri>> grid, float x, float z)
    {
        var o = new List<(float y, float slope, string src)>();
        if (grid.TryGetValue(CellOf(x, z), out var list))
            foreach (var t in list)
            {
                if (t.Up < 0 || !HeightOn(t, x, z, out float y)) continue; // a face looking down: a down ray does not hit it
                float ux = t.X1 - t.X0, uy = t.Y1 - t.Y0, uz = t.Z1 - t.Z0, vx = t.X2 - t.X0, vy = t.Y2 - t.Y0, vz = t.Z2 - t.Z0;
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-9f) continue;
                float slope = MathF.Acos(Math.Min(1f, Math.Abs(ny) / len)) * 180f / MathF.PI;
                o.Add((y, slope, t.Source));
            }
        o.Sort((a, b) => b.y.CompareTo(a.y));
        var d = new List<(float, float, string)>();
        foreach (var s in o)
            if (d.Count == 0 || Math.Abs(d[d.Count - 1].Item1 - s.y) > 0.02f || d[d.Count - 1].Item3 != s.src) d.Add(s);
        return d;
    }

    /// <summary>NPC poses of a save's "npcs" section (v2 full, v3 compact: an NPC not written stands at its spawn) with the dead of "corpses".</summary>
    static IEnumerable<(string id, WorldPose pose, string activity, bool dead)> SavedNpcPoses(string path, IContentDatabase db)
    {
        var sections = SaveSections(path);
        var dead = new HashSet<string>(StringComparer.Ordinal);
        if (sections.TryGetValue("corpses", out var cs))
        {
            var r = new BinarySaveReader(new MemoryStream(cs.body));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) { dead.Add(r.ReadString()); r.ReadDouble(); }
        }
        var list = new List<(string, WorldPose, string, bool)>();
        if (!sections.TryGetValue("npcs", out var ns)) return list;
        var rd = new BinarySaveReader(new MemoryStream(ns.body));
        int count = rd.ReadInt32();
        WorldPose D() => new WorldPose((float)rd.ReadDouble(), (float)rd.ReadDouble(), (float)rd.ReadDouble(), (float)rd.ReadDouble());
        WorldPose F() => new WorldPose(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());
        for (int i = 0; i < count; i++)
        {
            string id = rd.ReadString();
            if (ns.version >= 3)
            {
                int h = rd.ReadInt32();
                if ((h & 128) != 0) rd.ReadString();
                WorldPose? pose = (h & 1) == 0 ? F() : (WorldPose?)null;
                if ((h & 2) != 0) { if ((h & 4) == 0) rd.ReadString(); rd.ReadInt32(); }
                if ((h & 8) != 0) F();
                if ((h & 16) != 0) { rd.ReadDouble(); rd.ReadDouble(); }
                if ((h & 32) != 0) rd.ReadDouble();
                if ((h & 64) != 0) rd.ReadDouble();
                var act = ((NpcActivity)((h >> 16) & 0xff)).ToString();
                if (pose == null && db.TryGet(new ActorId(id).Name, out NpcDef def) && def.Spawn is WorldPose sp) pose = sp;
                if (pose != null) list.Add((id, pose.Value, act, dead.Contains(id)));
            }
            else
            {
                var pose = D();
                var act = ((NpcActivity)rd.ReadInt32()).ToString();
                rd.ReadString(); rd.ReadInt32(); rd.ReadInt32(); D();
                rd.ReadDouble(); rd.ReadDouble(); rd.ReadDouble(); rd.ReadDouble(); rd.ReadInt32();
                if (ns.version >= 2) rd.ReadString();
                list.Add((id, pose, act, dead.Contains(id)));
            }
        }
        return list;
    }

    /// <summary>The raw sections of a save file (<see cref="SaveFile"/> layout: magic, format, header, sections).</summary>
    static Dictionary<string, (int version, byte[] body)> SaveSections(string path)
    {
        var o = new Dictionary<string, (int, byte[])>(StringComparer.Ordinal);
        using var f = File.OpenRead(path);
        SaveFile.ReadHeader(f); // magic, format, header
        var r = new BinaryReader(f, System.Text.Encoding.UTF8, leaveOpen: true);
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadBoolean() ? r.ReadString() : null;
            int version = r.ReadInt32(), length = r.ReadInt32();
            o[name ?? ""] = (version, r.ReadBytes(length));
        }
        return o;
    }
}
