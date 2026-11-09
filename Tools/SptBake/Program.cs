using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Trees;

namespace G3.Tools.SptBake
{
    /// <summary>
    /// SptBake [--game &lt;Gothic 3 dir&gt;] [--out &lt;dir&gt;] [--obj] [tree names…]
    /// Generates the original tree meshes (.g3tree) for every .spt in Speedtrees.pak (or only the named ones) by running
    /// the SpeedTreeRT code of the installed Engine.dll. Output is game-derived: keep it local (default Temp/SptBake).
    /// </summary>
    static class Program
    {
        /// <summary>Median leaf card size (cm) per LOD from the baked corners.</summary>
        static string LeafCard(BakedTree t) => t.Leaves.Count == 0 ? "-" : string.Join(",", t.Leaves.Select(LeafCard));

        static string LeafCard(BakedLeafLod l)
        {
            var w = new List<float>(); var h = new List<float>();
            for (int i = 0; i < l.Count; i++)
            {
                float[] c = l.Corners; int o = 8 * i;
                w.Add(MathF.Sqrt((c[o + 2] - c[o]) * (c[o + 2] - c[o]) + (c[o + 3] - c[o + 1]) * (c[o + 3] - c[o + 1])));
                h.Add(MathF.Sqrt((c[o + 6] - c[o]) * (c[o + 6] - c[o]) + (c[o + 7] - c[o + 1]) * (c[o + 7] - c[o + 1])));
            }
            w.Sort(); h.Sort();
            return $"{w[w.Count / 2]:0}x{h[h.Count / 2]:0}";
        }

        /// <summary>Highest point of the LOD 0 geometry (leaf cards with their full extent), for checks against world bounds.</summary>
        static float GeometryTop(BakedTree t)
        {
            float top = float.MinValue;
            foreach (var m in new[] { t.Branches, t.Fronds })
                if (m.Count > 0) for (int i = 1; i < m[0].Positions.Length; i += 3) top = Math.Max(top, m[0].Positions[i]);
            if (t.Leaves.Count > 0)
            {
                var l = t.Leaves[0];
                for (int i = 0; i < l.Count; i++)
                {
                    float r = 0;
                    for (int k = 0; k < 4; k++) r = Math.Max(r, MathF.Sqrt(l.Corners[8 * i + 2 * k] * l.Corners[8 * i + 2 * k] + l.Corners[8 * i + 2 * k + 1] * l.Corners[8 * i + 2 * k + 1]));
                    top = Math.Max(top, l.Centers[3 * i + 1] + r);
                }
            }
            return top;
        }

        static string DefaultGame => G3.Formats.G3Install.Dir ?? "<Gothic 3 not found: pass the game folder or set G3_DIR>";

        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            string game = DefaultGame, outDir = null;
            bool obj = false;
            var names = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--game": game = args[++i]; break;
                    case "--out": outDir = args[++i]; break;
                    case "--obj": obj = true; break;
                    case "-h": case "--help":
                        Console.WriteLine("SptBake [--game <Gothic 3 dir>] [--out <dir>] [--obj] [tree names...]");
                        return 0;
                    default: names.Add(args[i]); break;
                }
            }
            outDir ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../Temp/SptBake"));
            Directory.CreateDirectory(outDir);

            using var fs = new G3FileSystem(Path.Combine(game, "Data"), "Speedtrees");
            var entries = fs.Files.Where(f => f.Path.EndsWith(".spt", StringComparison.OrdinalIgnoreCase))
                .Where(f => names.Count == 0 || names.Any(n => Path.GetFileNameWithoutExtension(f.Path).Equals(n, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Path).ToList();
            if (entries.Count == 0) { Console.Error.WriteLine("no matching .spt"); return 1; }

            using var rt = new SpeedTreeRuntime(game);
            var baker = new TreeBaker(rt);
            var report = new List<string> { "name	heightCm	geomTopCm	decoderSizeM	branchLods(v/t)	frondLods(v/t)	leafLods(n)	billboards	collision	lodNear	lodFar	leafCardWxH	sptLeafWxH" };
            int ok = 0;
            var sw = Stopwatch.StartNew();
            foreach (var e in entries)
            {
                string name = Path.GetFileNameWithoutExtension(e.Path);
                byte[] data = e.Read();
                var def = Spt.Read(data, name);
                var t = baker.Bake(name, data);
                using (var f = File.Create(Path.Combine(outDir, name + ".g3tree"))) BakedTreeIO.Write(t, f);
                if (obj) ObjExport.Write(t, Path.Combine(outDir, name + ".obj"));
                string Lods(List<BakedMeshLod> l) => string.Join(",", l.Select(m => $"{m.VertexCount}/{m.Triangles.Length / 3}"));
                report.Add(string.Join("\t", name, t.Height.ToString("0"), GeometryTop(t).ToString("0"), def.Size.ToString("0.#"),
                    Lods(t.Branches), Lods(t.Fronds), string.Join(",", t.Leaves.Select(l => l.Count)),
                    t.Billboard?.ImageCount ?? 0, string.Join(",", t.Collision.Select(c => c.Type)),
                    t.LodNear.ToString("0"), t.LodFar.ToString("0"), LeafCard(t),
                    string.Join(",", def.LeafMaps.Select(m => $"{m.Size[0] * def.Size * 100:0}x{m.Size[1] * def.Size * 100:0}"))));
                Console.WriteLine(report[^1]);
                ok++;
            }
            File.WriteAllLines(Path.Combine(outDir, "bake_report.tsv"), report);
            Console.WriteLine($"baked {ok}/{entries.Count} trees in {sw.Elapsed.TotalSeconds:0.0}s → {outDir}");
            return 0;
        }
    }
}
