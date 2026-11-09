using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Image;
using G3.Formats.Material;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.Remaster;
using G3.Formats.Trees;
using G3.Formats.World;

static partial class Program
{
    /// <summary>
    /// World remaster inventory (Docs/remaster-world.md §1):
    ///   remaster world inventory [--out dir] [--box cx cz half] [--region Varant]   (cm; default box = Faring pilot area)
    /// --region: "inBox" means "in that land" instead (an entity's land = the region of the nearest landscape cell centre,
    /// like G3WorldBatch's manifest regions); materials.tsv then also lists the placed surface area in the region.
    /// Reads the load set like the importer (WorldLayout, CStat + sector nodes; the .lrentdat usable objects are left out:
    /// 525 instances of 26 meshes), resolves .xlmsh to LOD0, and writes TSVs (meshes, materials, textures, vegetation,
    /// trees) plus summary.txt. Categories: <see cref="WorldRemasterCategories"/>.
    /// </summary>
    static int RemasterWorldCommand(string data, string[] args)
    {
        if (args.Length >= 4 && args[2] == "png") return RemasterWorldPng(data, args[3], args.Skip(4).ToArray());
        if (args.Length >= 4 && args[2] == "uvstats") return RemasterWorldUvStats(data, args.Skip(3).ToArray());
        if (args.Length >= 4 && args[2] == "export-area") return RemasterWorldExportArea(data, args[3], args.Skip(4).ToArray());
        if (args.Length < 3 || args[2] != "inventory")
        {
            Console.WriteLine("usage: remaster world inventory [--out dir] [--box cx cz half] | remaster world png <outDir> <image stem | @list.txt>...");
            return 1;
        }
        string outDir = Path.Combine(WorldRemasterPaths.Root(Directory.GetCurrentDirectory()), "Inventory");
        float bx = 63500, bz = 58500, bh = 25000;
        string region = null;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--out") outDir = args[++i];
            else if (args[i] == "--region") region = args[++i];
            else if (args[i] == "--box")
            {
                bx = float.Parse(args[++i], CultureInfo.InvariantCulture);
                bz = float.Parse(args[++i], CultureInfo.InvariantCulture);
                bh = float.Parse(args[++i], CultureInfo.InvariantCulture);
            }
        }
        Directory.CreateDirectory(outDir);
        var cellCentres = new List<(float x, float z, bool want)>();
        bool InBox(float x, float z)
        {
            if (region == null) return Math.Abs(x - bx) <= bh && Math.Abs(z - bz) <= bh;
            float best = float.MaxValue; bool want = false;
            foreach (var c in cellCentres)
            {
                float d = (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z);
                if (d < best) { best = d; want = c.want; }
            }
            return want;
        }

        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial", "_compiledImage", "Speedtrees");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());

        var meshUse = new Dictionary<string, (int all, int box)>(StringComparer.OrdinalIgnoreCase);
        var treeUse = new Dictionary<string, (int all, int box)>(StringComparer.OrdinalIgnoreCase);
        var vegMeshes = new Dictionary<string, VegetationMesh>(StringComparer.OrdinalIgnoreCase);
        var vegUse = new Dictionary<string, (int all, int box)>(StringComparer.OrdinalIgnoreCase);
        int lights = 0, lightsBox = 0;
        if (region != null)
            foreach (var s in layout.Sectors)
            foreach (var path in s.Nodes)
            {
                var entry = fs.Get(vol + path);
                List<WorldEntity> list;
                try { list = entry == null ? null : EntityArchive.Read(entry.Read()); }
                catch (Exception) { continue; }
                if (list == null) continue;
                foreach (var e in list)
                {
                    if (e.Visual != VisualKind.Mesh || e.Resource == null || e.Bounds == null) continue;
                    int k = e.Resource.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase);
                    if (k < 0 || !e.Resource.StartsWith("G3_", StringComparison.OrdinalIgnoreCase)) continue;
                    cellCentres.Add(((e.Bounds[0] + e.Bounds[3]) / 2, (e.Bounds[2] + e.Bounds[5]) / 2,
                        e.Resource.Substring(3, k - 3).Equals(region, StringComparison.OrdinalIgnoreCase)));
                }
            }
        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes)
        {
            var entry = fs.Get(vol + path);
            List<WorldEntity> list;
            try { list = entry == null ? null : EntityArchive.Read(entry.Read()); }
            catch (Exception) { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (!e.Enabled || !e.RenderingEnabled || e.IsNpc || e.IsItem) continue;
                bool inBox = InBox(e.X, e.Z);
                if (e.Visual == VisualKind.Mesh && !string.IsNullOrEmpty(e.Resource))
                {
                    // Terrain cells have an identity matrix: use their bounds centre for the box test.
                    if (WorldRemasterCategories.IsTerrainMesh(e.Resource))
                        inBox = InBox((e.Bounds[0] + e.Bounds[3]) / 2, (e.Bounds[2] + e.Bounds[5]) / 2);
                    Bump(meshUse, e.Resource, inBox);
                }
                if (e.Visual == VisualKind.SpeedTree && !string.IsNullOrEmpty(e.Resource)) Bump(treeUse, e.Resource, inBox);
                if (e.Light != null) { lights++; if (inBox) lightsBox++; }
                if (e.Vegetation != null)
                {
                    foreach (var m in e.Vegetation.Meshes.Values)
                        if (!vegMeshes.ContainsKey(m.MeshFile ?? "")) vegMeshes[m.MeshFile ?? ""] = m;
                    foreach (var p in e.Vegetation.Plants)
                        if (e.Vegetation.Meshes.TryGetValue(p.MeshIndex, out var vm)) Bump(vegUse, vm.MeshFile ?? "", InBox(p.X, p.Z));
                }
            }
        }

        // ---- images (header info + bytes of the full chain) ----------------------------------------------------------
        var imgInfo = new Dictionary<string, (int w, int h, string fmt, long bytes)>(StringComparer.OrdinalIgnoreCase);
        (int w, int h, string fmt, long bytes)? Img(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            stem = Path.GetFileNameWithoutExtension(stem);
            if (imgInfo.TryGetValue(stem, out var v)) return v.w == 0 ? null : v;
            var ent = fs.GetByName(stem + ".ximg");
            (int, int, string, long) r = (0, 0, "-", 0);
            if (ent != null)
            {
                try
                {
                    var img = Ximg.Read(ent.Read());
                    long b = 0;
                    for (int i = 0; i < img.Mips.Length; i++) b += img.Mips[i].Length;
                    r = (img.Width, img.Height, img.Format.ToString(), b * (img.IsCubemap ? 6 : 1));
                }
                catch (Exception) { }
            }
            imgInfo[stem] = r;
            return r.Item1 == 0 ? null : r;
        }

        // ---- meshes / materials --------------------------------------------------------------------------------------
        var matInfo = new Dictionary<string, MaterialResource>(StringComparer.OrdinalIgnoreCase);
        MaterialResource Mat(string file)
        {
            string stem = Path.GetFileNameWithoutExtension(file ?? "");
            if (matInfo.TryGetValue(stem, out var m)) return m;
            var ent = fs.GetByName(stem + ".xshmat");
            try { m = ent == null ? null : Xshmat.Read(ent.Read(), stem); } catch (Exception) { m = null; }
            matInfo[stem] = m;
            return m;
        }
        var matUse = new Dictionary<string, (string cat, long tris, int inst, int box)>(StringComparer.OrdinalIgnoreCase);
        var matArea = new Dictionary<string, (double world, double uv)>(StringComparer.OrdinalIgnoreCase);
        var matAreaBox = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var texCats = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var texUse = new Dictionary<string, (int inst, int box)>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder("mesh\tcategory\tinstances\tinBox\tlod0\ttris\tverts\tsizeX_m\tsizeY_m\tsizeZ_m\tmaterials\n");
        var catStats = new Dictionary<string, (int meshes, long inst, long instBox, long tris, long trisInst)>();
        foreach (var kv in meshUse.OrderByDescending(k => k.Value.all))
        {
            string lod0 = kv.Key;
            if (lod0.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var le = fs.GetByName(lod0);
                try { lod0 = le == null ? null : Xlmsh.Read(le.Read()).FirstOrDefault(); } catch (Exception) { lod0 = null; }
            }
            MeshResource mr = null;
            if (lod0 != null)
            {
                var me = fs.GetByName(Path.GetFileNameWithoutExtension(lod0) + ".xcmsh");
                try { mr = me == null ? null : Xcmsh.Read(me.Read(), lod0); } catch (Exception) { mr = null; }
            }
            int tris = 0, verts = 0;
            var mats = new List<string>();
            if (mr != null)
                foreach (var el in mr.Elements)
                {
                    tris += el.Indices.Length / 3; verts += el.VertexCount;
                    string ms = Path.GetFileNameWithoutExtension(el.Material ?? "");
                    if (!mats.Contains(ms, StringComparer.OrdinalIgnoreCase)) mats.Add(ms);
                }
            var mres = mats.Select(Mat).Where(m => m != null).ToList();
            string cat = WorldRemasterCategories.Classify(kv.Key, mres.Select(m => m.Name), mres.Count > 0 && mres.All(UrpIsWater));
            catStats.TryGetValue(cat, out var cs);
            catStats[cat] = (cs.meshes + 1, cs.inst + kv.Value.all, cs.instBox + kv.Value.box, cs.tris + tris, cs.trisInst + (long)tris * kv.Value.all);
            float sx = 0, sy = 0, sz = 0;
            if (mr != null) { sx = (mr.BoundsMax[0] - mr.BoundsMin[0]) / 100; sy = (mr.BoundsMax[1] - mr.BoundsMin[1]) / 100; sz = (mr.BoundsMax[2] - mr.BoundsMin[2]) / 100; }
            sb.Append($"{kv.Key}\t{cat}\t{kv.Value.all}\t{kv.Value.box}\t{lod0 ?? "-"}\t{tris}\t{verts}\t{sx:F1}\t{sy:F1}\t{sz:F1}\t{string.Join(";", mats)}\n");
            if (mr != null)
                foreach (var el in mr.Elements)
                {
                    string ms = Path.GetFileNameWithoutExtension(el.Material ?? "");
                    string mc = WorldRemasterCategories.ClassifyMaterial(ms, cat);
                    matArea.TryGetValue(ms, out var ma);
                    var (wa, ua) = WorldRemasterCategories.Areas(el.Positions, el.Uv0, el.Indices);
                    matArea[ms] = (ma.world + wa, ma.uv + ua);
                    matAreaBox[ms] = (matAreaBox.TryGetValue(ms, out double ab) ? ab : 0) + wa / 1e4 * kv.Value.box;
                    matUse.TryGetValue(ms, out var mu);
                    matUse[ms] = (mu.cat == null || mu.cat.Split(',').Contains(mc) ? mu.cat ?? mc : mu.cat + "," + mc, mu.tris + (long)el.Indices.Length / 3 * kv.Value.all, mu.inst + kv.Value.all, mu.box + kv.Value.box);
                }
        }
        File.WriteAllText(Path.Combine(outDir, "meshes.tsv"), sb.ToString());

        var mb = new StringBuilder("material\tcategory\tshader\tkind\tinstances\tinBox\ttrisPlaced\tmPerUv\tdiffuse\tdiffuse2\tnormal\tnormal2\tdetail\tdetail2\tspecular\tdiffuseSize\tareaBox_m2\n");
        foreach (var kv in matUse.OrderByDescending(k => k.Value.tris))
        {
            var m = Mat(kv.Key);
            string kind = m == null ? "missing" : UrpIsWater(m) ? "water" : m.VertexAlphaBlend ? "blend2" : m.DiffuseDetail != null ? "single+detail" : m.Blend.ToString().ToLowerInvariant();
            var slots = m == null ? new TextureSlot[7] : new[] { m.Diffuse, m.Diffuse2, m.Normal, m.Normal2, m.DiffuseDetail, m.DiffuseDetail2, m.SpecularMask?.Map };
            foreach (var t in slots)
            {
                if (t?.Image == null) continue;
                if (!texCats.TryGetValue(t.Image, out var set)) texCats[t.Image] = set = new HashSet<string>();
                foreach (var c in kv.Value.cat.Split(',')) set.Add(c);
                texUse.TryGetValue(t.Image, out var tu);
                texUse[t.Image] = (tu.inst + kv.Value.inst, tu.box + kv.Value.box);
            }
            var d = Img(m?.Diffuse?.Image);
            mb.Append($"{kv.Key}\t{kv.Value.cat}\t{m?.ShaderClass ?? "-"}\t{kind}\t{kv.Value.inst}\t{kv.Value.box}\t{kv.Value.tris}\t{MPerUv(matArea, kv.Key)}\t" +
                      string.Join("\t", slots.Select(t => t?.Image == null ? "-" : t.Image + (t.ScaleU != 1 || t.ScaleV != 1 ? $"@{t.ScaleU:0.##}x{t.ScaleV:0.##}" : ""))) +
                      $"\t{(d == null ? "-" : $"{d.Value.w}x{d.Value.h}")}\t{(matAreaBox.TryGetValue(kv.Key, out double abx) ? abx : 0):F0}\n");
        }
        File.WriteAllText(Path.Combine(outDir, "materials.tsv"), mb.ToString());

        // ---- vegetation ----------------------------------------------------------------------------------------------
        var vb = new StringBuilder("mesh\tinstances\tinBox\ttris\tverts\tdiffuse\tdiffuseSize\tdoubleSided\twind\theight_m\n");
        long vegTris = 0, vegInst = 0;
        foreach (var kv in vegUse.OrderByDescending(k => k.Value.all))
        {
            var m = vegMeshes[kv.Key];
            int tris = (m.Indices?.Length ?? 0) / 3, verts = (m.Positions?.Length ?? 0) / 3;
            float h = 0;
            if (m.Positions != null) { float lo = float.MaxValue, hi = float.MinValue; for (int i = 1; i < m.Positions.Length; i += 3) { lo = Math.Min(lo, m.Positions[i]); hi = Math.Max(hi, m.Positions[i]); } h = (hi - lo) / 100; }
            string tex = Path.GetFileNameWithoutExtension(m.DiffuseTexture ?? "");
            var d = Img(tex);
            if (!string.IsNullOrEmpty(tex))
            {
                if (!texCats.TryGetValue(tex, out var set)) texCats[tex] = set = new HashSet<string>();
                set.Add("grass");
                texUse.TryGetValue(tex, out var tu);
                texUse[tex] = (tu.inst + kv.Value.all, tu.box + kv.Value.box);
            }
            vegTris += (long)tris * kv.Value.all; vegInst += kv.Value.all;
            vb.Append($"{kv.Key}\t{kv.Value.all}\t{kv.Value.box}\t{tris}\t{verts}\t{tex}\t{(d == null ? "-" : $"{d.Value.w}x{d.Value.h}")}\t{m.DoubleSided}\t{m.WindStrength:0.##}\t{h:F2}\n");
        }
        File.WriteAllText(Path.Combine(outDir, "vegetation.tsv"), vb.ToString());

        // ---- SpeedTrees ----------------------------------------------------------------------------------------------
        var tb = new StringBuilder("spt\tinstances\tinBox\tbark\tbarkSize\tcomposite\tcompositeSize\n");
        foreach (var kv in treeUse.OrderByDescending(k => k.Value.all))
        {
            var ent = fs.GetByName(kv.Key);
            string bark = "-", comp = "-";
            try
            {
                if (ent != null)
                {
                    var t = Spt.Read(ent.Read());
                    bark = string.IsNullOrEmpty(t.BranchTexture) ? "-" : Spt.ImageResourceName(t.BranchTexture);
                    comp = string.IsNullOrEmpty(t.CompositeMap) ? "-" : Spt.ImageResourceName(t.CompositeMap);
                }
            }
            catch (Exception) { }
            foreach (var (tex, c) in new[] { (bark, "tree_bark"), (comp, "tree_leaf") })
            {
                if (tex == "-") continue;
                if (!texCats.TryGetValue(tex, out var set)) texCats[tex] = set = new HashSet<string>();
                set.Add(c);
                texUse.TryGetValue(tex, out var tu);
                texUse[tex] = (tu.inst + kv.Value.all, tu.box + kv.Value.box);
            }
            var bi = Img(bark); var ci = Img(comp);
            tb.Append($"{kv.Key}\t{kv.Value.all}\t{kv.Value.box}\t{bark}\t{(bi == null ? "-" : $"{bi.Value.w}x{bi.Value.h}")}\t{comp}\t{(ci == null ? "-" : $"{ci.Value.w}x{ci.Value.h}")}\n");
        }
        File.WriteAllText(Path.Combine(outDir, "trees.tsv"), tb.ToString());

        // ---- textures ------------------------------------------------------------------------------------------------
        var xb = new StringBuilder("image\tcategories\twidth\theight\tformat\tbytes\tinstances\tinBox\n");
        var texCat = new Dictionary<string, (int n, long bytes, long bytesBox, int boxN, long px)>();
        foreach (var kv in texCats.OrderBy(k => k.Key))
        {
            var i = Img(kv.Key);
            texUse.TryGetValue(kv.Key, out var tu);
            string cats = string.Join(",", kv.Value.OrderBy(c => c));
            xb.Append($"{kv.Key}\t{cats}\t{i?.w ?? 0}\t{i?.h ?? 0}\t{i?.fmt ?? "missing"}\t{i?.bytes ?? 0}\t{tu.inst}\t{tu.box}\n");
            string main = WorldRemasterCategories.PrimaryOf(kv.Value);
            texCat.TryGetValue(main, out var tc);
            texCat[main] = (tc.n + 1, tc.bytes + (i?.bytes ?? 0), tc.bytesBox + (tu.box > 0 ? i?.bytes ?? 0 : 0), tc.boxN + (tu.box > 0 ? 1 : 0), tc.px + (long)(i?.w ?? 0) * (i?.h ?? 0));
        }
        File.WriteAllText(Path.Combine(outDir, "textures.tsv"), xb.ToString());

        // ---- summary -------------------------------------------------------------------------------------------------
        var su = new StringBuilder();
        su.AppendLine(region != null ? $"G3 world remaster inventory  (region {region}: nearest landscape cell)" : $"G3 world remaster inventory  (box centre {bx},{bz} cm, half {bh} cm)");
        su.AppendLine($"sectors {layout.Sectors.Count}; static point lights {lights} (box {lightsBox})");
        su.AppendLine("category       meshes   instances    inBox   tris(sum distinct)   tris placed");
        foreach (var kv in catStats.OrderByDescending(k => k.Value.trisInst))
            su.AppendLine($"{kv.Key,-13} {kv.Value.meshes,7} {kv.Value.inst,11:N0} {kv.Value.instBox,8:N0} {kv.Value.tris,18:N0} {kv.Value.trisInst,14:N0}");
        su.AppendLine($"vegetation     {vegUse.Count,7} {vegInst,11:N0} {vegUse.Values.Sum(v => (long)v.box),8:N0}  tris placed {vegTris:N0}");
        su.AppendLine($"speedtrees     {treeUse.Count,7} {treeUse.Values.Sum(v => (long)v.all),11:N0} {treeUse.Values.Sum(v => (long)v.box),8:N0}");
        su.AppendLine("texture class    images   MB(DXT, all mips)   Mpx(top)   used in box: images / MB");
        foreach (var kv in texCat.OrderByDescending(k => k.Value.bytes))
            su.AppendLine($"{kv.Key,-13} {kv.Value.n,8} {kv.Value.bytes / 1048576.0,12:F0} {kv.Value.px / 1e6,14:F0} {kv.Value.boxN,10} / {kv.Value.bytesBox / 1048576.0:F0}");
        su.AppendLine($"total          {texCat.Values.Sum(v => v.n),8} {texCat.Values.Sum(v => v.bytes) / 1048576.0,12:F0}");
        var sizes = imgInfo.Where(k => k.Value.w > 0 && texCats.ContainsKey(k.Key)).GroupBy(k => Math.Max(k.Value.w, k.Value.h)).OrderBy(g => g.Key);
        su.AppendLine("texture sizes (max side: count): " + string.Join(", ", sizes.Select(g => $"{g.Key}: {g.Count()}")));
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), su.ToString());
        Console.Write(su.ToString());
        return 0;
    }

    /// <summary>
    /// <c>remaster world png &lt;outDir&gt; &lt;stem | @file&gt;...</c>: top mip of world images as PNG (pipeline input, rows as stored =
    /// D3D top first). Images named <c>*_Normal_*</c> are written as plain RGB normals in Unity's convention (as R1).
    /// </summary>
    static int RemasterWorldPng(string data, string outDir, string[] names)
    {
        using var fs = new G3FileSystem(data, "_compiledImage");
        var stems = new List<string>();
        foreach (var n in names)
            if (n.StartsWith("@")) stems.AddRange(File.ReadAllLines(n.Substring(1)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")));
            else stems.Add(n);
        Directory.CreateDirectory(outDir);
        int ok = 0;
        foreach (var stem in stems.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var e = fs.GetByName(stem + ".ximg");
            if (e == null) { Console.WriteLine($"missing {stem}"); continue; }
            var img = Ximg.Read(e.Read());
            var rgba = Dxt.DecodeRgba(img.Mips[0], img.Width, img.Height, img.Format);
            if (stem.IndexOf("_Normal_", StringComparison.OrdinalIgnoreCase) >= 0) DxtNormalToRgb(rgba, img.Format == PixelFormat.DXT5);
            File.WriteAllBytes(Path.Combine(outDir, stem + ".png"), Png.Encode(rgba, img.Width, img.Height));
            ok++;
        }
        Console.WriteLine($"{ok}/{stems.Count} images -> {outDir}");
        return ok == stems.Count ? 0 : 2;
    }

    /// <summary>
    /// arch-4: per mesh element (material) the texel density of its UV0 - metres per UV unit along the UV's u and v axes
    /// (per triangle, area-weighted 5/50/95 %) and the share of the area whose UV repeats faster than 0.05 m per unit
    /// (a G3 image repeated 20+ times per metre: blurred to its mean by G3's mips, a lattice with anisotropic filtering
    /// of a sharp remastered image). <c>remaster world uvstats mesh.xcmsh...</c>
    /// </summary>
    static int RemasterWorldUvStats(string data, string[] meshes)
    {
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial", "_compiledImage");
        Console.WriteLine("mesh	material	tris	area_m2	mPerU_p5	mPerU_p50	mPerU_p95	mPerV_p5	mPerV_p50	mPerV_p95	denseShare");
        foreach (var name in meshes)
        {
            string lod0 = name;
            if (lod0.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var le = fs.GetByName(lod0);
                lod0 = le == null ? null : Xlmsh.Read(le.Read()).FirstOrDefault();
            }
            var me = lod0 == null ? null : fs.GetByName(Path.GetFileNameWithoutExtension(lod0) + ".xcmsh");
            if (me == null) { Console.WriteLine($"{name}	missing"); continue; }
            var mr = Xcmsh.Read(me.Read(), lod0);
            foreach (var el in mr.Elements)
            {
                if (el.Uv0 == null || el.Positions == null) continue;
                var su = new List<(double m, double w)>(); var sv = new List<(double m, double w)>();
                double area = 0, dense = 0;
                var P = el.Positions; var U = el.Uv0; var I = el.Indices;
                for (int t = 0; t + 2 < I.Length; t += 3)
                {
                    long a = I[t], b = I[t + 1], c = I[t + 2];
                    double[] e1 = { P[b * 3] - P[a * 3], P[b * 3 + 1] - P[a * 3 + 1], P[b * 3 + 2] - P[a * 3 + 2] };
                    double[] e2 = { P[c * 3] - P[a * 3], P[c * 3 + 1] - P[a * 3 + 1], P[c * 3 + 2] - P[a * 3 + 2] };
                    double u1 = U[b * 2] - U[a * 2], v1 = U[b * 2 + 1] - U[a * 2 + 1], u2 = U[c * 2] - U[a * 2], v2 = U[c * 2 + 1] - U[a * 2 + 1];
                    double det = u1 * v2 - u2 * v1;
                    double cx = e1[1] * e2[2] - e1[2] * e2[1], cy = e1[2] * e2[0] - e1[0] * e2[2], cz = e1[0] * e2[1] - e1[1] * e2[0];
                    double wa = Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2 / 1e4;
                    if (wa <= 0) continue;
                    area += wa;
                    if (Math.Abs(det) < 1e-12) { dense += wa; su.Add((0, wa)); sv.Add((0, wa)); continue; }
                    // dP/du, dP/dv (cm per UV unit)
                    double mu = 0, mv = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        double dpu = (e1[k] * v2 - e2[k] * v1) / det, dpv = (e2[k] * u1 - e1[k] * u2) / det;
                        mu += dpu * dpu; mv += dpv * dpv;
                    }
                    mu = Math.Sqrt(mu) / 100; mv = Math.Sqrt(mv) / 100;
                    su.Add((mu, wa)); sv.Add((mv, wa));
                    if (Math.Min(mu, mv) < 0.05) dense += wa;
                }
                static double Pct(List<(double m, double w)> l, double q)
                {
                    if (l.Count == 0) return 0;
                    var o = l.OrderBy(x => x.m).ToList(); double tot = o.Sum(x => x.w), acc = 0;
                    foreach (var x in o) { acc += x.w; if (acc >= q * tot) return x.m; }
                    return o[^1].m;
                }
                string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
                Console.WriteLine($"{Path.GetFileName(name)}	{Path.GetFileNameWithoutExtension(el.Material ?? "")}	{I.Length / 3}	{F(area)}	{F(Pct(su, .05))}	{F(Pct(su, .5))}	{F(Pct(su, .95))}	{F(Pct(sv, .05))}	{F(Pct(sv, .5))}	{F(Pct(sv, .95))}	{F(area > 0 ? dense / area : 0)}");
            }
        }
        return 0;
    }

    static string MPerUv(Dictionary<string, (double world, double uv)> d, string key) =>
        d.TryGetValue(key, out var a) && a.uv > 1e-9 ? (Math.Sqrt(a.world / a.uv) / 100).ToString("0.###", CultureInfo.InvariantCulture) : "-";

    static void Bump(Dictionary<string, (int all, int box)> d, string key, bool box)
    {
        d.TryGetValue(key, out var v);
        d[key] = (v.all + 1, v.box + (box ? 1 : 0));
    }

    static bool UrpIsWater(MaterialResource src) =>
        src.ShaderClass == "eCShaderRiver" || src.ShaderClass == "eCShaderOcean" || src.ShaderClass == "eCShaderWater" ||
        (src.Name != null && src.Name.IndexOf("_Water_", StringComparison.OrdinalIgnoreCase) >= 0);
}
