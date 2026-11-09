using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Items;
using G3.Formats.Pak;
using G3.Formats.World;

static partial class Program
{
    /// <summary>
    /// G3Cli anim npc routines &lt;x&gt; &lt;z&gt; &lt;half&gt; (metres) — the routine points of the NPCs in a region
    /// (Docs/npc-simulation.md): per NPC its working / relaxing / sleeping point with use type and anchor, then a
    /// histogram of the use types.
    /// </summary>
    static int NpcRoutines(string data, string[] args)
    {
        float x = float.Parse(args[2]) * 100, z = float.Parse(args[3]) * 100, half = float.Parse(args[4]) * 100;
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var spawns = NpcSpawns.LoadWorld(layout, p => fs.Get(vol + p)?.Read())
            .Where(s => s.Enabled && !NpcSpawns.IsTestSector(s.Sector) && Math.Abs(s.X - x) < half && Math.Abs(s.Z - z) < half).ToList();
        var points = RoutinePoints.LoadWorld(layout, p => fs.Get(vol + p)?.Read());
        var useTypes = new Dictionary<string, int>();

        string Describe(Guid id)
        {
            if (id == Guid.Empty) return "-";
            if (!points.Points.TryGetValue(id, out var pt)) return "missing";
            string use = ((UseType)pt.UseType).ToString();
            useTypes[use] = useTypes.TryGetValue(use, out var n) ? n + 1 : 1;
            string anchor = pt.Anchor == Guid.Empty ? "" :
                points.Anchors.TryGetValue(pt.Anchor, out var a) ? $" @{a.Name}({a.Type},{a.MaxUsers})" : " @missing";
            return $"{pt.Name}[{use}x{pt.SlotCount}]{anchor}";
        }

        Console.WriteLine($"{spawns.Count} NPCs, {points.Points.Count} points, {points.Anchors.Count} anchors in the world");
        foreach (var s in spawns.OrderBy(s => s.Name))
            Console.WriteLine($"{s.Name,-28} sp{s.Species,-3} work {Describe(s.WorkingPoint)} | relax {Describe(s.RelaxingPoint)} | sleep {Describe(s.SleepingPoint)}");
        Console.WriteLine(string.Join(" ", useTypes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}")));
        return 0;
    }
}

static partial class Program
{
    /// <summary>G3Cli anim npc anidir &lt;nameFilter&gt; — raw gCNavOffset_PS.AniDirection bytes of matching interaction objects.</summary>
    static int NpcAniDirections(string data, string filter)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        int shown = 0;
        foreach (var f in fs.Files.Where(f => (f.Path.EndsWith(".node", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase))))
        {
            List<WorldEntity> list;
            try { list = EntityArchive.Read(f.Read(), false, keepRaw: true); } catch { continue; }
            foreach (var e in list ?? new List<WorldEntity>())
            {
                if (e.Name == null || e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var nav = e.RawPropertySets?.FirstOrDefault(p => p.ClassName == "gCNavOffset_PS");
                var v = nav?.Get("AniDirection")?.Value;
                var o = nav?.Get("OffsetPose")?.Value;
                int n = o == null || o.Length < 5 ? 0 : BitConverter.ToInt32(o, 1);
                var poses = new List<string>();
                for (int i = 0; i < n && 5 + (i + 1) * 28 <= o.Length; i++)
                {
                    float F(int k) => BitConverter.ToSingle(o, 5 + i * 28 + k * 4);
                    poses.Add($"({F(0):0.#},{F(1):0.#},{F(2):0.#} q {F(3):0.###},{F(4):0.###},{F(5):0.###},{F(6):0.###})");
                }
                var ut = e.RawPropertySets?.FirstOrDefault(p => p.ClassName == "gCInteraction_PS");
                Console.WriteLine($"{e.Name}: UseType {(ut == null ? -1 : Props.Enum(ut, "UseType"))} AniDirection {(v == null ? "-" : BitConverter.ToString(v))} | poses {n} {string.Join(" ", poses)}");
                if (++shown > 12) return 0;
            }
        }
        return 0;
    }
}
