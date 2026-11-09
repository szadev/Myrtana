using System;
using System.Linq;
using G3.Formats.Pak;

static partial class Program
{
    /// <summary>
    /// <c>zones [nameFilter] [max]</c>: dumps the AI zones and enclaves of the world (entities with gCAIZone_PS,
    /// gCNavZone_PS or gCEnclave_PS), for Docs/perception-crime.md.
    /// </summary>
    static int ZoneCommand(string data, string[] args)
    {
        string filter = args.Length > 1 ? args[1] : null;
        int max = args.Length > 2 ? int.Parse(args[2]) : 20;
        var classes = new[] { "gCAIZone_PS", "gCNavZone_PS", "gCEnclave_PS", "gCNavPath_PS", "gCNegZone_PS" };
        using var fs = new G3FileSystem(data, "Projects_compiled");
        int shown = 0;
        var counts = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var f in fs.Files.Where(f => f.Path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".node", StringComparison.OrdinalIgnoreCase)))
        {
            System.Collections.Generic.List<G3.Formats.World.WorldEntity> list;
            try { list = G3.Formats.World.EntityArchive.Read(f.Read(), false, keepRaw: true); } catch { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                var sets = (e.RawPropertySets ?? new()).Where(ps => classes.Contains(ps.ClassName)).ToList();
                if (sets.Count == 0) continue;
                string key = string.Join("+", sets.Select(s => s.ClassName));
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                if (filter != null && !Matches(e, filter)) continue;
                if (shown++ >= max) continue;
                Console.WriteLine($"== {e.Name} {{{e.Guid}}} pos ({e.World[12]:F0},{e.World[13]:F0},{e.World[14]:F0}) bounds [{string.Join(",", e.Bounds.Select(b => b.ToString("F0")))}] in {f.Path}");
                foreach (var ps in e.RawPropertySets)
                {
                    Console.WriteLine($"  {ps.ClassName} v{ps.ClassVersion}");
                    if (!classes.Contains(ps.ClassName)) continue;
                    foreach (var p in ps.Properties)
                        Console.WriteLine($"     {p.Key} : {p.Value.Type} = {(p.Key == "Point" ? Points(p.Value.Value) : Fmt(e.Source, p.Value))}");
                }
            }
        }
        foreach (var kv in counts) Console.WriteLine($"{kv.Value,6} {kv.Key}");
        return 0;
    }

    /// <summary>A name part, or "x,z,radius" (centimetres) around the entity's position.</summary>
    static bool Matches(G3.Formats.World.WorldEntity e, string filter)
    {
        var parts = filter.Split(',');
        if (parts.Length == 3 && float.TryParse(parts[0], out float x) && float.TryParse(parts[1], out float z) && float.TryParse(parts[2], out float r))
        {
            float dx = e.World[12] - x, dz = e.World[14] - z;
            return dx * dx + dz * dz <= r * r;
        }
        return (e.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>bTValArray&lt;bCVector&gt;: a version byte, a count, then x, y, z per point (local to the entity).</summary>
    static string Points(byte[] v)
    {
        if (v.Length < 5) return "-";
        int n = BitConverter.ToInt32(v, 1);
        var sb = new System.Text.StringBuilder($"{n}:");
        for (int i = 0; i < n && 5 + i * 12 + 12 <= v.Length; i++)
            sb.Append($" ({BitConverter.ToSingle(v, 5 + i * 12):F0},{BitConverter.ToSingle(v, 9 + i * 12):F0},{BitConverter.ToSingle(v, 13 + i * 12):F0})");
        return sb.ToString();
    }
}
