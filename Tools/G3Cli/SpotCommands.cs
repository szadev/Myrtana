using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using Myrtana.Story;

static partial class Program
{
    /// <summary>
    /// <c>spots [--pack dir] [--out file]</c>: computes the dev test-spot catalogue (Docs/dev-scenarios.md) from the
    /// content pack, the water zones and the landscape terrain, and writes Tools/Scenarios/spots.json (coordinates and ids
    /// only). Floors are landscape triangles only: rocks, bridges and docks are not seen, so the runtime teleport checks
    /// the water mode again.
    /// </summary>
    static int SpotCommand(string data, string[] args)
    {
        string pack = Option(args, "--pack") ?? DefaultPackDir;
        string output = Option(args, "--out") ?? Path.Combine(Project, "Tools", "Scenarios", "spots.json");
        var clock = Stopwatch.StartNew();
        var db = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) }).Database;
        var npcs = db.All<NpcDef>().Where(n => n.Spawn.HasValue && !n.Id.IsPlayer).ToList();
        var points = db.All<RoutinePointDef>().ToDictionary(p => p.Id);
        var objects = db.All<InteractiveObjectDef>().ToList();
        var important = new HashSet<string>(db.All<DialogEntryDef>().Where(e => e.Kind == DialogEntryKind.Important).Select(e => e.Owner.Name));
        Console.WriteLine($"pack: {npcs.Count} placed NPCs, {points.Count} routine points, {objects.Count} objects, {important.Count} NPCs with Important infos ({clock.Elapsed.TotalSeconds:0} s)");

        var map = BuildWaterMap(data, false);
        var terrain = new TerrainGrid(TerrainNear(data, 0, 0, 1e6f));
        Console.WriteLine($"water zones {map.Zones.Count}, terrain triangles {terrain.Count} ({clock.Elapsed.TotalSeconds:0} s)");

        var catalog = new SpotCatalog { Generated = $"G3Cli spots {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC" };
        var water = new WaterProbeGrid(map, terrain);

        // ------------------------------------------------ deep water (hero swims), with the nearest bloodfly
        var bloodflies = npcs.Where(n => Species(n) == "Bloodfly").ToList();
        var deep = DeepWater(water);
        Console.WriteLine($"deep water candidates {deep.Count} ({clock.Elapsed.TotalSeconds:0} s)");
        var withFly = deep.Select(d => (d, fly: Nearest(bloodflies, d.x, d.z))).Where(t => t.fly.npc != null).OrderBy(t => t.fly.dist).ToList();
        for (int i = 0; i < Math.Min(3, withFly.Count); i++)
        {
            var (d, fly) = withFly[i];
            catalog.Put(DeepSpot(i == 0 ? "deep_water_bloodfly" : $"deep_water_bloodfly_{i + 1}", d, "bloodfly", fly.npc.Id.Name, fly.dist,
                "deep water nearest to a bloodfly spawn; bring/spawn the fly if npcDist > 40"));
        }
        var shoreFirst = deep.Where(d => d.depth >= 3f).OrderBy(d => d.shoreDist).ThenByDescending(d => d.depth).ToList();
        for (int i = 0; i < Math.Min(3, shoreFirst.Count); i++)
        {
            var d = shoreFirst[i];
            var imp = Nearest(npcs.Where(n => important.Contains(n.Id.Name)), d.x, d.z);
            catalog.Put(DeepSpot(i == 0 ? "deep_water_shore" : $"deep_water_shore_{i + 1}", d, "shore", imp.npc?.Id.Name, imp.dist,
                "deep water a few metres off a dry shore (talk gate while swimming); npc = nearest NPC with an Important info"));
        }

        // ------------------------------------------------ wading ford (1.5-1.9 m column), near Abe
        var abe = npcs.FirstOrDefault(n => n.Id.Name == "Abe");
        var fords = Fords(water).OrderBy(f => abe != null ? Dist(abe, f.x, f.z) : 0).ToList();
        Console.WriteLine($"wading fords {fords.Count} ({clock.Elapsed.TotalSeconds:0} s)");
        for (int i = 0; i < Math.Min(3, fords.Count); i++)
        {
            var f = fords[i];
            catalog.Put(new TestSpot
            {
                Id = i == 0 ? "wade_ford" : $"wade_ford_{i + 1}", Kind = "water", Tags = { "wade" }, Source = "computed",
                Pos = new[] { R(f.x), R(f.surface - f.column), R(f.z) }, Npc = abe?.Id.Name,
                Points = new Dictionary<string, float[]> { ["shore"] = f.shore, ["far"] = f.far },
                Values = new Dictionary<string, float> { ["column"] = R(f.column), ["surface"] = R(f.surface), ["npcDist"] = abe != null ? R(Dist(abe, f.x, f.z)) : -1 },
                Note = "1.5-1.9 m column between two dry banks: a follower crosses shore -> far through wading depth",
            });
        }

        // ------------------------------------------------ flat open fields away from NPCs and towns
        var enclaves = db.All<EnclaveDef>().Where(e => e.Position != null).ToList();
        var fields = FlatFields(terrain, water, npcs, enclaves).Take(5).ToList();
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            catalog.Put(new TestSpot
            {
                Id = $"flat_field_{i + 1}", Kind = "field", Tags = { "flat", "quiet" }, Source = "computed",
                Pos = new[] { R(f.x), R(f.y), R(f.z) },
                Values = new Dictionary<string, float> { ["npcDist"] = R(f.npcDist), ["score"] = R(f.npcDist) },
                Note = $"flat (±1.2 m over 20 m), dry, nearest placed NPC {f.npcDist:0} m: isolated fights, spawns",
            });
        }
        Console.WriteLine($"flat fields {fields.Count} ({clock.Elapsed.TotalSeconds:0} s)");

        // ------------------------------------------------ seated NPCs that are likely peaceful
        var seats = new[] { "SitGround", "Bench", "Stool", "SitCampfire", "Throne" };
        var seated = npcs.Where(n => n.Routine != null && points.TryGetValue(n.Routine.Working, out var p) && seats.Contains(p.Interaction) && p.Slots.Count > 0
                                     && Species(n) == "Human" && LikelyPeaceful(n))
                         .Select(n => (n, p: points[n.Routine.Working]))
                         .OrderBy(t => t.n.Combat?.AttitudeToPlayer == Attitude.Neutral ? 0 : 1).ThenBy(t => t.n.Id.Name, StringComparer.Ordinal).Take(6).ToList();
        foreach (var (n, p) in seated)
        {
            var slot = p.Slots[0];
            catalog.Put(new TestSpot
            {
                Id = "seated_" + n.Id.Name.ToLowerInvariant().Replace('#', '_'), Kind = "npc", Tags = { "seated", "peaceful", p.Interaction.ToLowerInvariant() },
                Source = "computed", Pos = new[] { R(slot.X), R(slot.Y), R(slot.Z) }, Yaw = R(slot.Yaw), Npc = n.Id.Name,
                Note = $"{p.Interaction} working point of {n.Id} (faction {n.Combat?.Faction ?? "-"}, attitude {n.Combat?.AttitudeToPlayer?.ToString() ?? "rules"}); tp npc:{n.Id} at work hours",
            });
        }

        // ------------------------------------------------ campfires with peaceful sitters
        var campfires = objects.Where(o => o.UseType == "Campfire" && o.Position != null).Select(o =>
        {
            int sitters = npcs.Count(n => n.Routine != null && points.TryGetValue(n.Routine.Working, out var p) && p.Interaction == "SitCampfire" && p.Slots.Count > 0
                                          && LikelyPeaceful(n) && Hyp(p.Slots[0].X - o.Position[0], p.Slots[0].Z - o.Position[2]) < 8f);
            return (o, sitters);
        }).Where(t => t.sitters > 0).OrderByDescending(t => t.sitters).ThenBy(t => t.o.Id, StringComparer.Ordinal).Take(3).ToList();
        for (int i = 0; i < campfires.Count; i++)
        {
            var (o, sitters) = campfires[i];
            catalog.Put(ObjectSpot(i == 0 ? "campfire" : $"campfire_{i + 1}", "fire", o, 3f, new[] { "campfire", "fire" },
                $"campfire {o.Id} with {sitters} peaceful SitCampfire NPCs; pos = fire, stand = 3 m off"));
        }

        // ------------------------------------------------ wall torches (placed gCEffect_PS torch effects) near the dev start
        var torches = WallTorches(data).OrderBy(t => Hyp(t.x - 568f, t.z - 576f)).Take(3).ToList();
        for (int i = 0; i < torches.Count; i++)
        {
            var t = torches[i];
            catalog.Put(new TestSpot
            {
                Id = i == 0 ? "wall_torch" : $"wall_torch_{i + 1}", Kind = "fire", Tags = { "torch", "fire" }, Source = "computed",
                Pos = new[] { R(t.x), R(t.y), R(t.z) },
                Points = new Dictionary<string, float[]> { ["stand"] = new[] { R(t.x + t.fx * 3f), R(t.y - 2f), R(t.z + t.fz * 3f) } },
                Note = $"{t.effect} on {t.name}, {Hyp(t.x - 568f, t.z - 576f):0} m from the Faring dev start (use 'safe'); tp spot={(i == 0 ? "wall_torch" : $"wall_torch_{i + 1}")}.stand",
            });
        }

        // ------------------------------------------------ shrines, Erzofen, cows
        var shrines = objects.Where(o => o.Name == "G3_Object_Interact_Innos_Statue_01" && o.Position != null)
                             .OrderBy(o => Hyp(o.Position[0] - 568f, o.Position[2] - 576f)).Take(2).ToList();
        for (int i = 0; i < shrines.Count; i++)
            catalog.Put(ObjectSpot(i == 0 ? "shrine_innos" : $"shrine_innos_{i + 1}", "speaker", shrines[i], 2f, new[] { "shrine", "innos" },
                "Innos statue (Speaker): talk " + shrines[i].Name));
        var ofen = objects.FirstOrDefault(o => o.Kind == InteractiveKind.Speaker && o.Name.IndexOf("Erzofen", StringComparison.OrdinalIgnoreCase) >= 0 && o.Position != null);
        if (ofen != null) catalog.Put(ObjectSpot("erzofen", "speaker", ofen, 2.5f, new[] { "erzofen", "speaker" }, "Hammerclan ore furnace (Speaker): talk " + ofen.Name));
        var predators = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Wolf", "Bloodfly", "Snapper", "Lurker", "Ripper", "Shadowbeast", "Troll", "Orc", "Gnoll", "Goblin", "Boar", "Warg", "Sabretooth", "Bear", "Alligator", "Scavenger", "Skeleton", "Zombie" };
        var monsters = npcs.Where(n => predators.Contains(Species(n) ?? "")).ToList();
        var cows = npcs.Where(n => Species(n) == "Cow").Select(n => (n, threat: Nearest(monsters, n.Spawn.Value.X, n.Spawn.Value.Z).dist))
                       .OrderByDescending(t => t.threat).Take(2).ToList();
        for (int i = 0; i < cows.Count; i++)
        {
            var (n, threat) = cows[i];
            var sp = n.Spawn.Value;
            catalog.Put(new TestSpot
            {
                Id = i == 0 ? "cow" : $"cow_{i + 1}", Kind = "npc", Tags = { "cow", "animal" }, Source = "computed", Npc = n.Id.Name,
                Pos = new[] { R(sp.X), R(sp.Y), R(sp.Z) }, Yaw = R(sp.Yaw), Values = new Dictionary<string, float> { ["threatDist"] = R(threat) },
                Note = $"cow spawn, nearest predator/orc {threat:0} m",
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, catalog.ToJson());
        Console.WriteLine($"{catalog.Spots.Count} spots -> {output} ({clock.Elapsed.TotalSeconds:0} s)");
        foreach (var s in catalog.Spots) Console.WriteLine($"  {s.Id,-24} {SpotCatalog.Format(s.Pos),-22} {s.Npc}");
        return 0;
    }

    // ---------------------------------------------------------------- spot builders

    static TestSpot DeepSpot(string id, DeepCandidate d, string tag, string npc, float npcDist, string note) => new TestSpot
    {
        Id = id, Kind = "water", Tags = { "deep", "swim", tag }, Source = "computed", Npc = npc,
        Pos = new[] { R(d.x), R(d.surface), R(d.z) },
        Points = new Dictionary<string, float[]> { ["shore"] = d.shore },
        Values = new Dictionary<string, float> { ["depth"] = R(d.depth), ["surface"] = R(d.surface), ["shoreDist"] = R(d.shoreDist), ["npcDist"] = R(npcDist) },
        Note = note + $" ({d.zone})",
    };

    static TestSpot ObjectSpot(string id, string kind, InteractiveObjectDef o, float standOff, string[] tags, string note)
    {
        float fx = o.Forward != null ? o.Forward[0] : 0, fz = o.Forward != null ? o.Forward[2] : 1;
        float len = Hyp(fx, fz);
        if (len < 1e-3f) { fx = 0; fz = 1; len = 1; }
        fx /= len; fz /= len;
        var spot = new TestSpot
        {
            Id = id, Kind = kind, Source = "computed", Npc = o.Kind == InteractiveKind.Speaker ? o.Name : null,
            Pos = new[] { R(o.Position[0]), R(o.Position[1]), R(o.Position[2]) },
            Points = new Dictionary<string, float[]> { ["stand"] = new[] { R(o.Position[0] + fx * standOff), R(o.Position[1]), R(o.Position[2] + fz * standOff) } },
            Note = note,
        };
        spot.Tags.AddRange(tags);
        return spot;
    }

    // ---------------------------------------------------------------- searches

    struct DeepCandidate
    {
        public float x, z, surface, depth, shoreDist;
        public float[] shore;
        public string zone;
    }

    /// <summary>
    /// Points with a column ≥ 2.6 m whose 3 m ring is ≥ 2.0 m deep (the hero swims, NPCs keep out) and a dry bank
    /// (column &lt; 0.3 m, slope ok) within 25 m; best (deepest) one per 60 m cell.
    /// </summary>
    static List<DeepCandidate> DeepWater(WaterProbeGrid w)
    {
        var best = new Dictionary<(int, int), DeepCandidate>();
        foreach (var zone in w.Map.Zones)
        {
            float width = zone.MaxX - zone.MinX, depth = zone.MaxZ - zone.MinZ;
            if (width <= 0 || depth <= 0 || float.IsInfinity(width)) continue;
            float step = Math.Max(4f, (float)Math.Sqrt(width * depth / 40000f));
            for (float x = zone.MinX + step / 2; x < zone.MaxX; x += step)
            for (float z = zone.MinZ + step / 2; z < zone.MaxZ; z += step)
            {
                if (!w.Column(x, z, out float col, out float surf) || col < 2.6f) continue;
                bool ring = true;
                for (int k = 0; k < 8 && ring; k++)
                {
                    float a = k * MathF.PI / 4;
                    ring = w.Column(x + 3 * MathF.Cos(a), z + 3 * MathF.Sin(a), out float c2, out _) && c2 >= 2.0f;
                }
                if (!ring) continue;
                if (col > 12f || !w.FindBank(x, z, surf, 25f, out var bank, out float bankDist)) continue;
                var key = ((int)Math.Floor(x / 60f), (int)Math.Floor(z / 60f));
                if (best.TryGetValue(key, out var have) && have.depth >= col) continue;
                best[key] = new DeepCandidate { x = x, z = z, surface = surf, depth = col, shore = bank, shoreDist = bankDist, zone = zone.Name };
            }
        }
        return best.Values.ToList();
    }

    struct Ford
    {
        public float x, z, surface, column;
        public float[] shore, far;
    }

    /// <summary>1.5-1.9 m columns (NPC wading; humans walk up to 1.9 m) with dry banks on two opposite sides within 15 m.</summary>
    static List<Ford> Fords(WaterProbeGrid w)
    {
        var result = new Dictionary<(int, int), Ford>();
        foreach (var zone in w.Map.Zones)
        {
            float width = zone.MaxX - zone.MinX, depth = zone.MaxZ - zone.MinZ;
            if (width <= 0 || depth <= 0 || float.IsInfinity(width) || width * depth > 4e6f) continue;
            float step = Math.Max(3f, (float)Math.Sqrt(width * depth / 40000f));
            for (float x = zone.MinX + step / 2; x < zone.MaxX; x += step)
            for (float z = zone.MinZ + step / 2; z < zone.MaxZ; z += step)
            {
                if (!w.Column(x, z, out float col, out float surf) || col < 1.5f || col > 1.9f) continue;
                for (int k = 0; k < 8; k++)
                {
                    float a = k * MathF.PI / 4, dx = MathF.Cos(a), dz = MathF.Sin(a);
                    if (!w.DryAlong(x, z, surf, dx, dz, 15f, out var shore) || !w.DryAlong(x, z, surf, -dx, -dz, 15f, out var far)) continue;
                    if (!w.NoDeeperThan(x, z, shore, 1.95f) || !w.NoDeeperThan(x, z, far, 1.95f)) continue;
                    var key = ((int)Math.Floor(x / 80f), (int)Math.Floor(z / 80f));
                    if (!result.ContainsKey(key)) result[key] = new Ford { x = x, z = z, surface = surf, column = col, shore = shore, far = far };
                    break;
                }
            }
        }
        return result.Values.ToList();
    }

    /// <summary>Dry ground flat within ±1.2 m over a 20 m radius, ranked by the distance to the nearest placed NPC.</summary>
    static IEnumerable<(float x, float y, float z, float npcDist)> FlatFields(TerrainGrid t, WaterProbeGrid w, List<NpcDef> npcs, List<EnclaveDef> enclaves)
    {
        var spawns = new SpatialHash(100f);
        foreach (var n in npcs) spawns.Add(n.Spawn.Value.X, n.Spawn.Value.Z);
        var found = new List<(float, float, float, float)>();
        for (float x = t.MinX + 20; x < t.MaxX - 20; x += 40)
        for (float z = t.MinZ + 20; z < t.MaxZ - 20; z += 40)
        {
            float y = t.Height(x, z);
            if (float.IsNaN(y) || w.Column(x, z, out _, out _)) continue;
            bool flat = true;
            foreach (float r in new[] { 10f, 20f })
                for (int k = 0; k < 8 && flat; k++)
                {
                    float a = k * MathF.PI / 4, h = t.Height(x + r * MathF.Cos(a), z + r * MathF.Sin(a));
                    flat = !float.IsNaN(h) && Math.Abs(h - y) <= 1.2f && !w.Column(x + r * MathF.Cos(a), z + r * MathF.Sin(a), out _, out _);
                }
            if (!flat) continue;
            if (enclaves.Any(e => Hyp(e.Position[0] - x, e.Position[2] - z) < 200f)) continue;
            float nd = spawns.Nearest(x, z, 400f);
            if (nd < 70f || nd > 250f || y < 1f) continue; // quiet, but inside the populated world
            found.Add((x, y, z, nd));
        }
        return found.OrderByDescending(f => f.Item4);
    }

    static List<(float x, float y, float z, float fx, float fz, string effect, string name)> WallTorches(string data)
    {
        var list = new List<(float, float, float, float, float, string, string)>();
        using var fs = new G3FileSystem(data, "Projects_compiled");
        foreach (var (_, e) in AudioWorldEntities(fs, true))
        {
            if (e.IsNpc || e.IsItem || e.RawPropertySets == null) continue;
            var fx = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCEffect_PS");
            if (fx == null) continue;
            string eff = G3.Formats.Items.Props.Str(e.Source, fx, "Effect") ?? "";
            if (eff.IndexOf("torch", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = e.World;
            float len = Hyp(m[8], m[10]);
            list.Add((m[12] * 0.01f, m[13] * 0.01f, m[14] * 0.01f, len > 1e-3f ? m[8] / len : 0, len > 1e-3f ? m[10] / len : 1, eff, e.Name));
        }
        return list;
    }

    // ---------------------------------------------------------------- small helpers

    static string Species(NpcDef n) => n.Combat?.Species ?? n.Species;

    /// <summary>Heuristic: stored attitude Neutral/Friendly, a friend, or a rebel/Nordmar/nomad without an evil flag.</summary>
    static bool LikelyPeaceful(NpcDef n)
    {
        var c = n.Combat;
        if (c == null) return true;
        if (c.Evil || c.Faction == "Out") return false; // outlaws attack (runner-10: OutNovice at a SitGround point)
        if (c.AttitudeToPlayer == Attitude.Neutral || c.AttitudeToPlayer == Attitude.Friendly || c.FriendType) return true;
        return c.Faction == "Reb" || c.Faction == "Nrd" || c.Faction == "Nom" || c.Faction == "Slave";
    }

    static (NpcDef npc, float dist) Nearest(IEnumerable<NpcDef> list, float x, float z)
    {
        NpcDef best = null;
        float bd = float.MaxValue;
        foreach (var n in list)
        {
            float d = Dist(n, x, z);
            if (d < bd) { bd = d; best = n; }
        }
        return (best, best == null ? -1 : bd);
    }

    static float Dist(NpcDef n, float x, float z) => Hyp(n.Spawn.Value.X - x, n.Spawn.Value.Z - z);
    static float Hyp(float a, float b) => MathF.Sqrt(a * a + b * b);
    static float R(float v) => MathF.Round(v * 10f) / 10f;

    /// <summary>Landscape triangles bucketed on a 32 m grid; Height = highest triangle under (x, z) or NaN.</summary>
    sealed class TerrainGrid
    {
        const float Cell = 32f;
        readonly List<float[]> _tris;
        readonly Dictionary<(int, int), List<int>> _cells = new Dictionary<(int, int), List<int>>();
        public float MinX = float.MaxValue, MaxX = float.MinValue, MinZ = float.MaxValue, MaxZ = float.MinValue;

        public TerrainGrid(List<float[]> tris)
        {
            _tris = tris;
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                float x0 = Math.Min(t[0], Math.Min(t[3], t[6])), x1 = Math.Max(t[0], Math.Max(t[3], t[6]));
                float z0 = Math.Min(t[2], Math.Min(t[5], t[8])), z1 = Math.Max(t[2], Math.Max(t[5], t[8]));
                MinX = Math.Min(MinX, x0); MaxX = Math.Max(MaxX, x1); MinZ = Math.Min(MinZ, z0); MaxZ = Math.Max(MaxZ, z1);
                for (int cx = (int)Math.Floor(x0 / Cell); cx <= (int)Math.Floor(x1 / Cell); cx++)
                for (int cz = (int)Math.Floor(z0 / Cell); cz <= (int)Math.Floor(z1 / Cell); cz++)
                {
                    if (!_cells.TryGetValue((cx, cz), out var l)) _cells[(cx, cz)] = l = new List<int>();
                    l.Add(i);
                }
            }
        }

        public int Count => _tris.Count;

        public float Height(float x, float z)
        {
            if (!_cells.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var l)) return float.NaN;
            float best = float.NaN;
            foreach (int i in l)
            {
                var t = _tris[i];
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
    }

    /// <summary>Water column (surface − landscape floor) queries.</summary>
    sealed class WaterProbeGrid
    {
        public readonly WaterMap Map;
        readonly TerrainGrid _t;

        public WaterProbeGrid(WaterMap map, TerrainGrid terrain) { Map = map; _t = terrain; }

        /// <summary>True when (x, z) is wet with a known floor; column = surface − floor.</summary>
        public bool Column(float x, float z, out float column, out float surface)
        {
            column = 0;
            float floor = _t.Height(x, z);
            if (float.IsNaN(floor) || !Map.Sample(x, floor, z, out surface, out _, 1000f)) { surface = float.NaN; return false; }
            column = surface - floor;
            return column > 0.05f;
        }

        /// <summary>The nearest dry, gently sloped landscape point within radius (16 directions, 1 m steps).</summary>
        public bool FindBank(float x, float z, float surface, float radius, out float[] bank, out float dist)
        {
            bank = null;
            dist = float.MaxValue;
            for (int k = 0; k < 16; k++)
            {
                float a = k * MathF.PI / 8, dx = MathF.Cos(a), dz = MathF.Sin(a);
                for (float r = 1; r <= Math.Min(radius, dist); r += 1)
                {
                    float px = x + dx * r, pz = z + dz * r;
                    if (Column(px, pz, out float c, out _) && c > 0.3f) continue;
                    float y = _t.Height(px, pz), y2 = _t.Height(px + dx * 2, pz + dz * 2);
                    if (float.IsNaN(y) || float.IsNaN(y2) || Math.Abs(y2 - y) > 1.2f) break;
                    if (y2 < surface - 0.3f || y2 > surface + 3f) break; // beyond the zone's mask, or a cliff
                    if (r < dist) { dist = r; bank = new[] { R(px + dx * 2), R(y2), R(pz + dz * 2) }; }
                    break;
                }
            }
            return bank != null;
        }

        /// <summary>Walking from (x, z) along (dx, dz) reaches dry ground (column &lt; 0.3 m) within max metres.</summary>
        public bool DryAlong(float x, float z, float surface, float dx, float dz, float max, out float[] point)
        {
            point = null;
            for (float r = 1; r <= max; r += 1)
            {
                float px = x + dx * r, pz = z + dz * r;
                if (Column(px, pz, out float c, out _) && c >= 0.3f) continue;
                float y = _t.Height(px + dx * 2, pz + dz * 2);
                if (float.IsNaN(y) || y < surface - 0.3f || y > surface + 3f) return false;
                point = new[] { R(px + dx * 2), R(y), R(pz + dz * 2) };
                return true;
            }
            return false;
        }

        /// <summary>No sample on the straight line to the point is deeper than max (a walkable ford).</summary>
        public bool NoDeeperThan(float x, float z, float[] to, float max)
        {
            float len = Hyp(to[0] - x, to[2] - z);
            for (float r = 0; r <= len; r += 1)
                if (Column(x + (to[0] - x) * r / len, z + (to[2] - z) * r / len, out float c, out _) && c > max) return false;
            return true;
        }
    }

    sealed class SpatialHash
    {
        readonly float _cell;
        readonly Dictionary<(int, int), List<(float, float)>> _cells = new Dictionary<(int, int), List<(float, float)>>();
        public SpatialHash(float cell) => _cell = cell;

        public void Add(float x, float z)
        {
            var k = ((int)Math.Floor(x / _cell), (int)Math.Floor(z / _cell));
            if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<(float, float)>();
            l.Add((x, z));
        }

        /// <summary>Distance to the nearest point within reach (reach if none).</summary>
        public float Nearest(float x, float z, float reach)
        {
            float best = reach;
            int r = (int)Math.Ceiling(reach / _cell);
            int cx = (int)Math.Floor(x / _cell), cz = (int)Math.Floor(z / _cell);
            for (int i = -r; i <= r; i++)
            for (int j = -r; j <= r; j++)
                if (_cells.TryGetValue((cx + i, cz + j), out var l))
                    foreach (var (px, pz) in l) best = Math.Min(best, Hyp(px - x, pz - z));
            return best;
        }
    }
}
