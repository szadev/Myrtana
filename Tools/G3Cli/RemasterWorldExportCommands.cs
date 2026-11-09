using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Material;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.Remaster;
using G3.Formats.World;

static partial class Program
{
    /// <summary>
    /// <c>remaster world export-area &lt;outDir&gt; [--box cx cz half] [--no-veg] [--min-size m]</c>: the static world of a box for
    /// offline renders (Tools/Blender/remaster/worldrender.py): <c>meshes/&lt;mesh&gt;.bin</c> (per element: material, positions,
    /// normals, uv0, D3D colours, indices; G3 cm, local space), <c>scene.json</c> (instances with G3 world matrix rows,
    /// category; materials with their slots and scales; vegetation meshes and plant records), <c>images.txt</c>. Terrain
    /// cells are included when their bounds touch the box.
    /// </summary>
    static int RemasterWorldExportArea(string data, string outDir, string[] opts)
    {
        float bx = 63500, bz = 58500, bh = 25000, minSize = 0;
        bool veg = true;
        for (int i = 0; i < opts.Length; i++)
        {
            if (opts[i] == "--box") { bx = F(opts[++i]); bz = F(opts[++i]); bh = F(opts[++i]); }
            else if (opts[i] == "--no-veg") veg = false;
            else if (opts[i] == "--min-size") minSize = F(opts[++i]) * 100;
        }
        static float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);
        bool InBox(float x, float z) => Math.Abs(x - bx) <= bh && Math.Abs(z - bz) <= bh;
        bool Touches(float[] b) => b[0] <= bx + bh && b[3] >= bx - bh && b[2] <= bz + bh && b[5] >= bz - bh;

        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        const string vol = "Projects_compiled/";
        var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
        Directory.CreateDirectory(Path.Combine(outDir, "meshes"));
        var inst = new StringBuilder();
        var meshDone = new Dictionary<string, MeshResource>(StringComparer.OrdinalIgnoreCase);
        var mats = new Dictionary<string, MaterialResource>(StringComparer.OrdinalIgnoreCase);
        var vegOut = new StringBuilder();
        var vegMeshes = new Dictionary<string, VegetationMesh>(StringComparer.OrdinalIgnoreCase);
        var vegPlants = new Dictionary<string, List<VegetationPlant>>(StringComparer.OrdinalIgnoreCase);
        int nInst = 0;

        void WriteElement(BinaryWriter bw, string material, float[] pos, float[] nrm, float[] uv, uint[] col, uint[] idx)
        {
            var nb = Encoding.UTF8.GetBytes(material);
            bw.Write(nb.Length); bw.Write(nb);
            int nv = pos.Length / 3;
            bw.Write(nv); bw.Write(idx.Length);
            foreach (var f in pos) bw.Write(f);
            for (int i = 0; i < nv * 3; i++) bw.Write(nrm != null && i < nrm.Length ? nrm[i] : (i % 3 == 1 ? 1f : 0f));
            for (int i = 0; i < nv * 2; i++) bw.Write(uv != null && i < uv.Length ? uv[i] : 0f);
            for (int i = 0; i < nv; i++) bw.Write(col != null && i < col.Length ? col[i] : 0xFFFFFFFFu);
            foreach (var ix in idx) bw.Write(ix);
        }

        MeshResource LoadMesh(string res)
        {
            if (meshDone.TryGetValue(res, out var m)) return m;
            string lod0 = res;
            if (lod0.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var le = fs.GetByName(lod0);
                try { lod0 = le == null ? null : Xlmsh.Read(le.Read()).FirstOrDefault(); } catch (Exception) { lod0 = null; }
            }
            var me = lod0 == null ? null : fs.GetByName(Path.GetFileNameWithoutExtension(lod0) + ".xcmsh");
            try { m = me == null ? null : Xcmsh.Read(me.Read(), lod0); } catch (Exception) { m = null; }
            meshDone[res] = m;
            if (m == null) return null;
            using var bw = new BinaryWriter(File.Create(Path.Combine(outDir, "meshes", Path.GetFileNameWithoutExtension(res) + ".bin")));
            bw.Write(m.Elements.Count);
            foreach (var el in m.Elements)
            {
                string ms = Path.GetFileNameWithoutExtension(el.Material ?? "");
                WriteElement(bw, ms, el.Positions, el.Normals, el.Uv0, el.Diffuse, el.Indices);
                if (!mats.ContainsKey(ms))
                {
                    var ent = fs.GetByName(ms + ".xshmat");
                    try { mats[ms] = ent == null ? null : Xshmat.Read(ent.Read(), ms); } catch (Exception) { mats[ms] = null; }
                }
            }
            return m;
        }

        foreach (var s in layout.Sectors)
        foreach (var path in s.Nodes)
        {
            var entry = fs.Get(vol + path);
            List<WorldEntity> list;
            try { list = entry == null ? null : EntityArchive.Read(entry.Read()); } catch (Exception) { continue; }
            if (list == null) continue;
            foreach (var e in list)
            {
                if (!e.Enabled || !e.RenderingEnabled || e.IsNpc || e.IsItem) continue;
                if (e.Visual == VisualKind.Mesh && !string.IsNullOrEmpty(e.Resource))
                {
                    bool terrain = WorldRemasterCategories.IsTerrainMesh(e.Resource);
                    if (terrain ? !Touches(e.Bounds) : !InBox(e.X, e.Z)) continue;
                    if (!terrain && minSize > 0 && Math.Max(e.Bounds[3] - e.Bounds[0], e.Bounds[4] - e.Bounds[1]) < minSize) continue;
                    string cat = WorldRemasterCategories.Classify(e.Resource);
                    if (cat == WorldRemasterCategories.Hidden) continue;
                    if (LoadMesh(e.Resource) == null) continue;
                    inst.Append(nInst++ == 0 ? "" : ",\n").Append(
                        $"{{\"mesh\":{J(Path.GetFileNameWithoutExtension(e.Resource))},\"cat\":{J(cat)},\"m\":[{string.Join(",", e.World.Select(Fl))}]}}");
                }
                if (veg && e.Vegetation != null)
                    foreach (var p in e.Vegetation.Plants)
                    {
                        if (!InBox(p.X, p.Z) || !e.Vegetation.Meshes.TryGetValue(p.MeshIndex, out var vm)) continue;
                        string key = Path.GetFileNameWithoutExtension(vm.MeshFile ?? "veg");
                        vegMeshes[key] = vm;
                        if (!vegPlants.TryGetValue(key, out var l)) vegPlants[key] = l = new List<VegetationPlant>();
                        l.Add(p);
                    }
            }
        }
        var mj = new StringBuilder();
        foreach (var kv in mats)
        {
            var m = kv.Value;
            if (mj.Length > 0) mj.Append(",\n");
            if (m == null) { mj.Append($"{J(kv.Key)}:null"); continue; }
            mj.Append($"{J(kv.Key)}:{{\"shader\":{J(m.ShaderClass)},\"blend\":{J(m.Blend.ToString())},\"vertexBlend\":{B(m.VertexAlphaBlend)}," +
                      $"\"water\":{B(UrpIsWater(m))},\"invisible\":{B(m.Blend == BlendMode.Invisible || (m.Name ?? "").Contains("_Depth_"))}," +
                      $"\"diffuse\":{Slot(m.Diffuse)},\"diffuse2\":{Slot(m.Diffuse2)},\"normal\":{Slot(m.Normal)}," +
                      $"\"normal2\":{Slot(m.Normal2)},\"detail\":{Slot(m.DiffuseDetail)},\"detail2\":{Slot(m.DiffuseDetail2)}," +
                      $"\"color\":{(m.DiffuseColor == null ? "null" : "[" + string.Join(",", m.DiffuseColor.Select(Fl)) + "]")}}}");
        }
        int nPlants = 0;
        foreach (var kv in vegMeshes)
        {
            var vm = kv.Value;
            using (var bw = new BinaryWriter(File.Create(Path.Combine(outDir, "meshes", "veg_" + kv.Key + ".bin"))))
            {
                bw.Write(1);
                WriteElement(bw, "veg:" + Path.GetFileNameWithoutExtension(vm.DiffuseTexture ?? ""), vm.Positions, vm.Normals, vm.Uvs, null, vm.Indices);
            }
            if (vegOut.Length > 0) vegOut.Append(",\n");
            vegOut.Append($"{{\"mesh\":{J("veg_" + kv.Key)},\"tex\":{J(Path.GetFileNameWithoutExtension(vm.DiffuseTexture ?? ""))},\"doubleSided\":{B(vm.DoubleSided)},\"plants\":[");
            vegOut.Append(string.Join(",", vegPlants[kv.Key].Select(p =>
                $"[{Fl(p.X)},{Fl(p.Y)},{Fl(p.Z)},{Fl(p.Qx)},{Fl(p.Qy)},{Fl(p.Qz)},{Fl(p.Qw)},{Fl(p.ScaleWidth)},{Fl(p.ScaleHeight)},{p.Color}]")));
            vegOut.Append("]}");
            nPlants += vegPlants[kv.Key].Count;
        }
        File.WriteAllText(Path.Combine(outDir, "scene.json"),
            $"{{\"box\":[{Fl(bx)},{Fl(bz)},{Fl(bh)}],\n\"instances\":[\n{inst}],\n\"materials\":{{\n{mj}}},\n\"vegetation\":[\n{vegOut}]}}\n");
        var images = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mats.Values.Where(m => m != null))
            foreach (var t in new[] { m.Diffuse, m.Diffuse2, m.Normal, m.Normal2, m.DiffuseDetail, m.DiffuseDetail2 })
                if (t?.Image != null) images.Add(t.Image);
        foreach (var vm in vegMeshes.Values) images.Add(Path.GetFileNameWithoutExtension(vm.DiffuseTexture ?? ""));
        File.WriteAllLines(Path.Combine(outDir, "images.txt"), images);
        Console.WriteLine($"{nInst} instances of {meshDone.Count} meshes, {mats.Count} materials, {images.Count} images, {nPlants} plants of {vegMeshes.Count} meshes -> {outDir}");
        return 0;

        static string J(string v) => v == null ? "null" : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        static string Fl(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        static string Slot(TextureSlot t) => t?.Image == null ? "null" : $"{{\"image\":{J(t.Image)},\"su\":{Fl(t.ScaleU)},\"sv\":{Fl(t.ScaleV)}}}";
    }
}
