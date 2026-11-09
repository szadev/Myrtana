using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G3.Formats.Image;
using G3.Formats.Pak;
using G3.Formats.Trees;

static partial class Program
{
    /// <summary>
    /// G3Cli spt validate | list | dump &lt;name&gt; [--tokens] | textures | usage | png &lt;outDir&gt; &lt;name&gt;... |
    /// where &lt;filter&gt; [cellMetres]
    /// — SpeedTree .spt files from Speedtrees.pak (Docs/formats-speedtree.md).
    /// </summary>
    static int SptCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "validate";
        switch (sub)
        {
            case "validate": return SptValidate(data, false);
            case "list": return SptValidate(data, true);
            case "dump": return SptDump(data, args[2], args.Contains("--tokens"));
            case "textures": return SptTextures(data);
            case "usage": return SptUsage(data);
            case "png": return SptPng(data, args[2], args.Skip(3).ToArray());
            case "where": return SptWhere(data, args[2], args.Length > 3 ? float.Parse(args[3], CultureInfo.InvariantCulture) : 60f);
        }
        Console.WriteLine("usage: spt validate | list | dump <name> [--tokens] | textures | usage | png <outDir> <tree-name|image-name>... | where <filter> [cell m]");
        return 1;
    }

    static IEnumerable<(PakEntry e, SpeedTreeDef t)> AllTrees(G3FileSystem fs)
    {
        foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".spt", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path))
            yield return (e, Spt.Read(e.Read(), Path.GetFileNameWithoutExtension(e.Path)));
    }

    static int SptValidate(string data, bool table)
    {
        using var fs = new G3FileSystem(data, "Speedtrees");
        int ok = 0, fail = 0, tokens = 0;
        var unknownIds = new SortedSet<int>();
        foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".spt", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path))
        {
            try
            {
                var t = Spt.Read(e.Read(), Path.GetFileNameWithoutExtension(e.Path));
                tokens += t.Tokens.Count;
                if (t.Levels.Count != t.Tokens.First(k => k.Id == 1014).Int) throw new Exception("level count mismatch");
                if (t.LeafMaps.Count != t.Tokens.First(k => k.Id == 1006).Int) throw new Exception("leaf map count mismatch");
                foreach (var l in t.Levels)
                    if (l.Length == null || l.Radius == null || l.StartAngle == null) throw new Exception("level without curves");
                ok++;
                if (table)
                {
                    var trunk = t.Levels[0];
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,-40} lv{1} size {2,5:0.#}±{3,-4:0.#} seed {4,6} lod {5:0}/{6:0} fronds {7} leafmaps {8} quads {9}/{10}/{11} coll {12} trunk r={13:0.###} segs {14}x{15} comp {16}",
                        t.Name, t.Levels.Count, t.Size, t.SizeVariance, t.Seed, t.LodNear, t.LodFar,
                        t.FrondsEnabled ? $"L{t.FrondLevel}{(t.FrondType == 0 ? "b" + t.FrondBlades : "x")}" : "-",
                        t.LeafMaps.Count, t.LeafQuads.Count, t.BillboardQuads.Count, t.FrondQuads.Count, t.Collision.Count,
                        trunk.Radius.Max, trunk.CrossSegments, trunk.LengthSegments, Spt.ImageResourceName(t.CompositeMap)?.Replace("G3_Speedtree_Misc_", "")));
                }
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"FAIL {e.Path}: {ex.Message}");
            }
        }
        Console.WriteLine($"spt ok {ok}, failed {fail}, tokens {tokens:N0}");
        return fail == 0 ? 0 : 2;
    }

    static int SptDump(string data, string name, bool tokens)
    {
        using var fs = new G3FileSystem(data, "Speedtrees");
        var e = fs.Files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? fs.Files.First(f => f.Path.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        var t = Spt.Read(e.Read(), Path.GetFileNameWithoutExtension(e.Path));
        if (tokens)
        {
            foreach (var k in t.Tokens) Console.WriteLine($"{k.Offset,6:X}  {k}");
            return 0;
        }
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"{e.Path}  {t.Version}  {t.Tokens.Count} tokens");
        Console.WriteLine(string.Format(ci, "size {0}±{1}  seed {2}  lod near/far {3:0.##}/{4:0.##}  bark '{5}'  composite '{6}'",
            t.Size, t.SizeVariance, t.Seed, t.LodNear, t.LodFar, t.BranchTexture, t.CompositeMap));
        for (int i = 0; i < t.Levels.Count; i++)
        {
            var l = t.Levels[i];
            string kind = i == 0 ? "trunk" : i == t.Levels.Count - 1 ? "leaves" : $"branch{i}";
            Console.WriteLine(string.Format(ci,
                "  L{0} {1,-7} length {2} radius {3} angle {4} gravity {5} disturb {6} flex {7}",
                i, kind, l.Length, l.Radius, l.StartAngle, l.Gravity, l.Disturbance, l.Flexibility));
            Console.WriteLine(string.Format(ci,
                "            segs {0}x{1}  children {2:0.##}..{3:0.##} freq {4:0.##}  uv {5}x{6} abs {7}/{8}  twist {9} randV {10}  flares {11} pack {12}",
                l.CrossSegments, l.LengthSegments, l.ChildFirst, l.ChildLast, l.ChildFrequency, l.UTile, l.VTile,
                l.UAbsolute ? 1 : 0, l.VAbsolute ? 1 : 0, l.Twist, l.RandomVOffset ? 1 : 0, l.FlareCount, l.SegmentPack));
        }
        foreach (var m in t.LeafMaps)
            Console.WriteLine(string.Format(ci, "  leafmap '{0}' size {1:0.###}x{2:0.###} (ui {3}x{4}) origin {5:0.##},{6:0.##} blossom {7} colorVar {8}",
                m.Texture, m.Size[0], m.Size[1], m.SizeUi[0], m.SizeUi[1], m.Origin[0], m.Origin[1], m.Blossom ? 1 : 0, m.ColorVariance));
        Console.WriteLine(string.Format(ci, "  leaves: placement tol {0} collision {1} dimming {2}/{3} blossom dist {4} depth {5} weight {6}",
            t.LeafPlacementTolerance, t.LeafCollision, t.LeafDimmingOn ? 1 : 0, t.LeafDimming, t.BlossomDistance, t.BlossomDepth, t.BlossomWeight));
        Console.WriteLine($"  fronds: enabled {t.FrondsEnabled} level {t.FrondLevel} type {(t.FrondType == 0 ? "blades" : "extruded")} blades {t.FrondBlades} profileSegs {t.FrondProfileSegments} lods {t.FrondLodCount}");
        foreach (var f in t.FrondMaps)
            Console.WriteLine(string.Format(ci, "  frondmap '{0}' size {1} offset {2}..{3}°", f.Texture, f.SizeFactor, f.MinOffsetAngle, f.MaxOffsetAngle));
        void Q(string label, List<SptQuad> qs)
        {
            foreach (var q in qs)
                Console.WriteLine(string.Format(ci, "  {0} quad u {1:0.###}..{2:0.###} v {3:0.###}..{4:0.###}", label, q.MinU, q.MaxU, q.MinV, q.MaxV));
        }
        Q("leaf", t.LeafQuads); Q("billboard", t.BillboardQuads); Q("frond", t.FrondQuads);
        Console.WriteLine(string.Format(ci, "  LOD: branch lods {0} vol {1}..{2}  leaf lods {3} billboard r {4} exp {5} size {6} transition {7} ({8})",
            t.BranchLodCount, t.BranchMinVolume, t.BranchMaxVolume, t.LeafLodCount, t.LeafBillboardRadius, t.LeafBillboardExponent,
            t.LeafBillboardSize, t.LeafTransition, t.BillboardTransition));
        Console.WriteLine(string.Format(ci, "  wind: first level {0} rock {1} rustle {2} propagate {3}", t.WindFirstLevel, t.LeafRockScalar, t.LeafRustleScalar, t.PropagateFlexibility));
        foreach (var c in t.Collision)
            Console.WriteLine("  collision " + c.Kind + " " + string.Join(" ", c.Values.Select(v => v.ToString("0.##", ci))));
        Console.WriteLine($"  lighting branch/leaf/frond {t.BranchLighting}/{t.LeafLighting}/{t.FrondLighting} leafAdjust {t.LeafLightingAdjust.ToString(ci)}");
        return 0;
    }

    /// <summary>Check that every texture a tree references exists in _compiledImage (speedtree/&lt;name&gt;.ximg).</summary>
    static int SptTextures(string data)
    {
        using var fs = new G3FileSystem(data, "Speedtrees", "_compiledImage");
        var images = new HashSet<string>(fs.Files.Where(f => f.Path.EndsWith(".ximg", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.Path)), StringComparer.OrdinalIgnoreCase);
        var refs = new SortedDictionary<string, (string role, int n)>(StringComparer.OrdinalIgnoreCase);
        void Add(string tex, string role)
        {
            if (string.IsNullOrEmpty(tex)) return;
            var k = Spt.ImageResourceName(tex);
            refs[k] = refs.TryGetValue(k, out var v) ? (v.role, v.n + 1) : (role, 1);
        }
        foreach (var (_, t) in AllTrees(fs))
        {
            Add(t.BranchTexture, "bark"); Add(t.CompositeMap, "composite");
            foreach (var m in t.LeafMaps) Add(m.Texture, "leaf");
            foreach (var f in t.FrondMaps) Add(f.Texture, "frond");
        }
        int present = 0, missing = 0;
        foreach (var kv in refs)
        {
            bool has = images.Contains(kv.Key);
            bool normal = images.Contains(kv.Key.Replace("_Diffuse_", "_Normal_"));
            if (has) present++; else missing++;
            Console.WriteLine($"{(has ? "OK  " : "--  ")} {kv.Value.role,-9} {kv.Value.n,3}x  {kv.Key}{(normal && kv.Key.Contains("_Diffuse_") ? "  (+normal)" : "")}");
        }
        Console.WriteLine($"referenced {refs.Count}: shipped {present}, not shipped {missing} (leaf/frond sources live only in the composite maps)");
        return 0;
    }

    /// <summary>World instance statistics per .spt: count, scale range and the generated height implied by the entity bounds.</summary>
    static int SptUsage(string data)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled", "Speedtrees");
        const string vol = "Projects_compiled/";
        var layout = G3.Formats.World.WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var stats = new Dictionary<string, List<(float scale, float h, float w)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes)
        {
            var entry = fs.Get(vol + path);
            var list = entry == null ? null : G3.Formats.World.EntityArchive.Read(entry.Read());
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual != G3.Formats.World.VisualKind.SpeedTree || !e.Enabled || !e.RenderingEnabled) continue;
                float sc = (float)Math.Sqrt(e.World[4] * e.World[4] + e.World[5] * e.World[5] + e.World[6] * e.World[6]);
                float h = e.Bounds[4] - e.Bounds[1];
                float w = Math.Max(e.Bounds[3] - e.Bounds[0], e.Bounds[5] - e.Bounds[2]);
                var key = Path.GetFileNameWithoutExtension(e.Resource);
                if (!stats.TryGetValue(key, out var l)) stats[key] = l = new List<(float, float, float)>();
                l.Add((sc, h, w));
            }
        }
        var defs = AllTrees(fs).ToDictionary(x => x.t.Name, x => x.t, StringComparer.OrdinalIgnoreCase);
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine("instances  tree                                       scale min..max   bounds h/scale (cm)  w/scale   size±var  h/size");
        int total = 0;
        foreach (var kv in stats.OrderByDescending(k => k.Value.Count))
        {
            var l = kv.Value; total += l.Count;
            var hs = l.Select(x => x.h / Math.Max(x.scale, 1e-3f)).OrderBy(x => x).ToList();
            var ws = l.Select(x => x.w / Math.Max(x.scale, 1e-3f)).OrderBy(x => x).ToList();
            float hMed = hs[hs.Count / 2], wMed = ws[ws.Count / 2];
            defs.TryGetValue(kv.Key, out var d);
            Console.WriteLine(string.Format(ci, "{0,9}  {1,-42} {2,5:0.00}..{3,-5:0.00}   {4,8:0}             {5,6:0}   {6,5:0.#}±{7,-4:0.#} {8,6:0.#}",
                l.Count, kv.Key, l.Min(x => x.scale), l.Max(x => x.scale), hMed, wMed,
                d?.Size ?? float.NaN, d?.SizeVariance ?? float.NaN, d == null ? float.NaN : hMed / d.Size));
        }
        Console.WriteLine($"{total:N0} instances of {stats.Count} distinct trees; {defs.Keys.Count(k => !stats.ContainsKey(k))} .spt unused");
        return 0;
    }

    /// <summary>
    /// Densest spots of the trees whose .spt name contains <paramref name="filter"/> (case-insensitive; '|' separates
    /// alternatives): the 8 grid cells with the most instances, centre in Unity metres (G3 cm / 100, same axes) — camera
    /// positions for A/B forest shots (Docs/remaster-world.md 10.15).
    /// </summary>
    static int SptWhere(string data, string filter, float cell)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled");
        const string vol = "Projects_compiled/";
        var layout = G3.Formats.World.WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        var alts = filter.Split('|');
        var cells = new Dictionary<(int, int), (int n, double x, double y, double z, Dictionary<string, int> names)>();
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes)
        {
            var entry = fs.Get(vol + path);
            var list = entry == null ? null : G3.Formats.World.EntityArchive.Read(entry.Read());
            if (list == null) continue;
            foreach (var e in list)
            {
                if (e.Visual != G3.Formats.World.VisualKind.SpeedTree || !e.Enabled || !e.RenderingEnabled) continue;
                var name = Path.GetFileNameWithoutExtension(e.Resource);
                if (!alts.Any(a => name.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                double x = e.World[12] / 100.0, y = e.World[13] / 100.0, z = e.World[14] / 100.0;
                var key = ((int)Math.Floor(x / cell), (int)Math.Floor(z / cell));
                cells.TryGetValue(key, out var c);
                c.names ??= new Dictionary<string, int>();
                c.names.TryGetValue(name, out int k);
                c.names[name] = k + 1;
                cells[key] = (c.n + 1, c.x + x, c.y + y, c.z + z, c.names);
            }
        }
        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"{cells.Values.Sum(c => c.n)} instances matching '{filter}' in {cells.Count} cells of {cell} m");
        foreach (var kv in cells.OrderByDescending(k => k.Value.n).Take(8))
        {
            var c = kv.Value;
            var top = string.Join(", ", c.names.OrderByDescending(n => n.Value).Take(3).Select(n => $"{n.Key} {n.Value}"));
            Console.WriteLine(string.Format(ci, "{0,5}  centre {1,8:0} {2,6:0} {3,8:0}   {4}", c.n, c.x / c.n, c.y / c.n, c.z / c.n, top));
        }
        return 0;
    }

    /// <summary>Export textures as PNG: a tree name exports its bark plus the leaf/frond/billboard rectangles cut out
    /// of its composite map; any other argument is taken as an image name under speedtree/.</summary>
    static int SptPng(string data, string outDir, string[] names)
    {
        using var fs = new G3FileSystem(data, "Speedtrees", "_compiledImage");
        Directory.CreateDirectory(outDir);
        var trees = AllTrees(fs).ToDictionary(x => x.t.Name, x => x.t, StringComparer.OrdinalIgnoreCase);
        (byte[] rgba, int w, int h) Load(string img)
        {
            var e = fs.Files.First(f => Path.GetFileNameWithoutExtension(f.Path).Equals(img, StringComparison.OrdinalIgnoreCase)
                                        && f.Path.EndsWith(".ximg", StringComparison.OrdinalIgnoreCase));
            var r = Ximg.Read(e.Read());
            return (DecodeDxtAny(r.Mips[0], r.Width, r.Height, r.Format), r.Width, r.Height);
        }
        void Save(string file, byte[] rgba, int w, int h)
        {
            var p = Path.Combine(outDir, file + ".png");
            WritePng(p, rgba, w, h);
            Console.WriteLine($"{p} {w}x{h}");
        }
        foreach (var n in names)
        {
            if (!trees.TryGetValue(n, out var t))
            {
                var im = Load(n); Save(n, im.rgba, im.w, im.h);
                continue;
            }
            var bark = Load(Spt.ImageResourceName(t.BranchTexture));
            Save($"spt_{t.Name}_bark", bark.rgba, bark.w, bark.h);
            var comp = Load(Spt.ImageResourceName(t.CompositeMap));
            void Cut(string label, List<SptQuad> qs)
            {
                for (int i = 0; i < qs.Count; i++)
                {
                    var q = qs[i];
                    // composite quads use v up (OpenGL convention): image row = (1 - v) * height
                    int x0 = (int)Math.Round(q.MinU * comp.w), x1 = (int)Math.Round(q.MaxU * comp.w);
                    int y0 = (int)Math.Round((1 - q.MaxV) * comp.h), y1 = (int)Math.Round((1 - q.MinV) * comp.h);
                    int w = x1 - x0, h = y1 - y0;
                    if (w <= 0 || h <= 0) continue;
                    var o = new byte[w * h * 4];
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(comp.rgba, ((y0 + y) * comp.w + x0) * 4, o, y * w * 4, w * 4);
                    Save($"spt_{t.Name}_{label}{i}", o, w, h);
                }
            }
            Cut("leaf", t.LeafQuads); Cut("frond", t.FrondQuads); Cut("billboard", t.BillboardQuads);
        }
        return 0;
    }

    /// <summary>DXT1/3/5 or BGRA32 top mip → RGBA32 (see <see cref="Dxt.DecodeRgba"/>).</summary>
    static byte[] DecodeDxtAny(byte[] src, int width, int height, PixelFormat fmt) => Dxt.DecodeRgba(src, width, height, fmt);

}
