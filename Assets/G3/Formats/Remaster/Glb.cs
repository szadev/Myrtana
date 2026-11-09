using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Binary glTF 2.0 (.glb) for <see cref="RigModel"/>: the exchange format between the Unity importer and Blender
    /// (Docs/remaster-pipeline.md). Writes one skin per skinned mesh (joints = its bones, inverse bind matrices = the
    /// bind pose), every rig node with its local TRS, static meshes as children of their node, one primitive per
    /// material slot and optional embedded PNG/JPEG textures. Reads what Blender's glTF exporter writes (one primitive
    /// per material with its own vertex arrays, merged back into one mesh; JOINTS_1/WEIGHTS_1; normalised integer
    /// weights; matrix or TRS nodes). Pure C#: shared by the editor, G3Cli and the tests.
    /// </summary>
    public static class Glb
    {
        const uint Magic = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;
        const int Float = 5126, UShort = 5123, UInt = 5125, UByte = 5121, Short = 5122, Byte = 5120;

        // ---------------------------------------------------------------------------------------------------------
        // Write

        public static void Write(RigModel model, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllBytes(path, Write(model));
        }

        public static byte[] Write(RigModel model)
        {
            var bin = new MemoryStream();
            var views = new List<(int offset, int length, int? target)>();
            var accessors = new List<Action<Utf8JsonWriter>>();

            int View(byte[] data, int? target)
            {
                while (bin.Length % 4 != 0) bin.WriteByte(0);
                int off = (int)bin.Length;
                bin.Write(data, 0, data.Length);
                views.Add((off, data.Length, target));
                return views.Count - 1;
            }

            int Accessor(byte[] data, int comp, int count, string type, int? target, bool normalized = false, float[] min = null, float[] max = null)
            {
                int v = View(data, target);
                accessors.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", v);
                    w.WriteNumber("componentType", comp);
                    if (normalized) w.WriteBoolean("normalized", true);
                    w.WriteNumber("count", count);
                    w.WriteString("type", type);
                    if (min != null) { w.WriteStartArray("min"); foreach (var f in min) w.WriteNumberValue(f); w.WriteEndArray(); }
                    if (max != null) { w.WriteStartArray("max"); foreach (var f in max) w.WriteNumberValue(f); w.WriteEndArray(); }
                    w.WriteEndObject();
                });
                return accessors.Count - 1;
            }

            byte[] Floats(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }

            // Images (bufferView-embedded).
            var images = new List<(int view, string mime)>();
            var matTex = new List<(int baseTex, int normalTex)>();
            foreach (var m in model.Materials)
            {
                int bt = -1, nt = -1;
                if (m.BaseColorImage != null) { images.Add((View(m.BaseColorImage, null), m.BaseColorMime)); bt = images.Count - 1; }
                if (m.NormalImage != null) { images.Add((View(m.NormalImage, null), m.NormalMime)); nt = images.Count - 1; }
                matTex.Add((bt, nt));
            }
            var materialIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < model.Materials.Count; i++) materialIndex[model.Materials[i].Name ?? ""] = i;
            var extraMaterials = new List<string>();
            int MaterialOf(string name)
            {
                name ??= "";
                if (materialIndex.TryGetValue(name, out int i)) return i;
                extraMaterials.Add(name);
                return materialIndex[name] = model.Materials.Count + extraMaterials.Count - 1;
            }

            // Meshes.
            var meshJson = new List<Action<Utf8JsonWriter>>();
            var skinJson = new List<Action<Utf8JsonWriter>>();
            var meshNodes = new List<(string name, int mesh, int skin, int parent)>();
            foreach (var m in model.Meshes)
            {
                int nv = m.VertexCount;
                var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
                for (int v = 0; v < nv; v++)
                    for (int c = 0; c < 3; c++) { min[c] = Math.Min(min[c], m.Positions[v * 3 + c]); max[c] = Math.Max(max[c], m.Positions[v * 3 + c]); }
                int pos = Accessor(Floats(m.Positions), Float, nv, "VEC3", 34962, false, nv > 0 ? min : null, nv > 0 ? max : null);
                int nrm = m.Normals != null ? Accessor(Floats(m.Normals), Float, nv, "VEC3", 34962) : -1;
                int uv0 = m.Uv0 != null ? Accessor(Floats(FlipV(m.Uv0)), Float, nv, "VEC2", 34962) : -1;
                int uv1 = m.Uv1 != null ? Accessor(Floats(FlipV(m.Uv1)), Float, nv, "VEC2", 34962) : -1;
                int joints = -1, weights = -1, skin = -1;
                if (m.IsSkinned)
                {
                    // Exactly 4 influences per vertex (the contract); a wider model is truncated to its 4 largest.
                    var ji = new ushort[nv * 4];
                    var wt = new float[nv * 4];
                    for (int v = 0; v < nv; v++)
                    {
                        var order = Enumerable.Range(0, m.Stride).OrderByDescending(k => m.Weights[v * m.Stride + k]).Take(4).ToArray();
                        float sum = order.Sum(k => Math.Max(0, m.Weights[v * m.Stride + k]));
                        for (int k = 0; k < order.Length; k++)
                        {
                            float w = Math.Max(0, m.Weights[v * m.Stride + order[k]]);
                            wt[v * 4 + k] = sum > 0 ? w / sum : (k == 0 ? 1 : 0);
                            ji[v * 4 + k] = wt[v * 4 + k] > 0 ? (ushort)m.JointIndices[v * m.Stride + order[k]] : (ushort)0;
                        }
                    }
                    var jb = new byte[ji.Length * 2];
                    Buffer.BlockCopy(ji, 0, jb, 0, jb.Length);
                    joints = Accessor(jb, UShort, nv, "VEC4", 34962);
                    weights = Accessor(Floats(wt), Float, nv, "VEC4", 34962);
                    var ibm = new float[m.Joints.Length * 16];
                    for (int j = 0; j < m.Joints.Length; j++)
                        for (int r = 0; r < 4; r++)
                            for (int c = 0; c < 4; c++) ibm[j * 16 + c * 4 + r] = (float)m.InverseBind[j][r * 4 + c]; // column-major
                    int ibmAcc = Accessor(Floats(ibm), Float, m.Joints.Length, "MAT4", null);
                    var jointList = m.Joints;
                    string skinName = m.Name;
                    skinJson.Add(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("name", skinName);
                        w.WriteNumber("inverseBindMatrices", ibmAcc);
                        w.WriteStartArray("joints"); foreach (var j in jointList) w.WriteNumberValue(j); w.WriteEndArray();
                        w.WriteEndObject();
                    });
                    skin = skinJson.Count - 1;
                }
                var morphAcc = new List<(int pos, int nrm)>();
                foreach (var mo in m.Morphs)
                {
                    var mn = new float[3]; var mx = new float[3];
                    for (int c = 0; c < 3; c++) { mn[c] = float.MaxValue; mx[c] = float.MinValue; }
                    for (int v = 0; v < nv; v++)
                        for (int c = 0; c < 3; c++) { mn[c] = Math.Min(mn[c], mo.Positions[v * 3 + c]); mx[c] = Math.Max(mx[c], mo.Positions[v * 3 + c]); }
                    int tp = Accessor(Floats(mo.Positions), Float, nv, "VEC3", 34962, false, nv > 0 ? mn : null, nv > 0 ? mx : null);
                    int tnn = mo.Normals != null ? Accessor(Floats(mo.Normals), Float, nv, "VEC3", 34962) : -1;
                    morphAcc.Add((tp, tnn));
                }
                var morphNames = m.Morphs.Select(x => x.Name ?? "").ToArray();
                var prims = new List<(int idx, int mat)>();
                foreach (var sm in m.SubMeshes)
                {
                    bool big = nv > 65535;
                    byte[] ib;
                    if (big) { ib = new byte[sm.Indices.Length * 4]; Buffer.BlockCopy(sm.Indices, 0, ib, 0, ib.Length); }
                    else { var s = sm.Indices.Select(i => (ushort)i).ToArray(); ib = new byte[s.Length * 2]; Buffer.BlockCopy(s, 0, ib, 0, ib.Length); }
                    prims.Add((Accessor(ib, big ? UInt : UShort, sm.Indices.Length, "SCALAR", 34963), MaterialOf(sm.Material)));
                }
                string meshName = m.Name;
                meshJson.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("name", meshName);
                    w.WriteStartArray("primitives");
                    foreach (var (idx, mat) in prims)
                    {
                        w.WriteStartObject();
                        w.WriteStartObject("attributes");
                        w.WriteNumber("POSITION", pos);
                        if (nrm >= 0) w.WriteNumber("NORMAL", nrm);
                        if (uv0 >= 0) w.WriteNumber("TEXCOORD_0", uv0);
                        if (uv1 >= 0) w.WriteNumber("TEXCOORD_1", uv1);
                        if (joints >= 0) { w.WriteNumber("JOINTS_0", joints); w.WriteNumber("WEIGHTS_0", weights); }
                        w.WriteEndObject();
                        if (morphAcc.Count > 0)
                        {
                            w.WriteStartArray("targets");
                            foreach (var (tp, tnn) in morphAcc)
                            {
                                w.WriteStartObject();
                                w.WriteNumber("POSITION", tp);
                                if (tnn >= 0) w.WriteNumber("NORMAL", tnn);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        w.WriteNumber("indices", idx);
                        w.WriteNumber("material", mat);
                        w.WriteNumber("mode", 4);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    if (morphNames.Length > 0)
                    {
                        w.WriteStartObject("extras");
                        w.WriteStartArray("targetNames"); foreach (var n in morphNames) w.WriteStringValue(n); w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                });
                meshNodes.Add((m.Name, meshJson.Count - 1, skin, m.IsSkinned ? -1 : m.Node));
            }

            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var binBytes = bin.ToArray();

            var json = new MemoryStream();
            using (var w = new Utf8JsonWriter(json))
            {
                w.WriteStartObject();
                w.WriteStartObject("asset");
                w.WriteString("version", "2.0");
                w.WriteString("generator", string.IsNullOrEmpty(model.Generator) ? "G3World remaster" : model.Generator);
                w.WriteEndObject();

                int rigCount = model.Nodes.Count;
                var children = new List<int>[rigCount + meshNodes.Count];
                for (int i = 0; i < children.Length; i++) children[i] = new List<int>();
                var roots = new List<int>();
                for (int i = 0; i < rigCount; i++)
                    if (model.Nodes[i].Parent >= 0) children[model.Nodes[i].Parent].Add(i); else roots.Add(i);
                for (int i = 0; i < meshNodes.Count; i++)
                    if (meshNodes[i].parent >= 0) children[meshNodes[i].parent].Add(rigCount + i); else roots.Add(rigCount + i);

                w.WriteNumber("scene", 0);
                w.WriteStartArray("scenes");
                w.WriteStartObject();
                w.WriteStartArray("nodes"); foreach (var r in roots) w.WriteNumberValue(r); w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndArray();

                w.WriteStartArray("nodes");
                for (int i = 0; i < rigCount; i++)
                {
                    var n = model.Nodes[i];
                    w.WriteStartObject();
                    w.WriteString("name", n.Name);
                    if (children[i].Count > 0) { w.WriteStartArray("children"); foreach (var c in children[i]) w.WriteNumberValue(c); w.WriteEndArray(); }
                    w.WriteStartArray("translation"); foreach (var f in n.T) w.WriteNumberValue(f); w.WriteEndArray();
                    w.WriteStartArray("rotation"); foreach (var f in NormalizeQ(n.R)) w.WriteNumberValue(f); w.WriteEndArray();
                    if (n.S != null && (n.S[0] != 1 || n.S[1] != 1 || n.S[2] != 1)) { w.WriteStartArray("scale"); foreach (var f in n.S) w.WriteNumberValue(f); w.WriteEndArray(); }
                    w.WriteEndObject();
                }
                foreach (var mn in meshNodes)
                {
                    w.WriteStartObject();
                    w.WriteString("name", mn.name);
                    w.WriteNumber("mesh", mn.mesh);
                    if (mn.skin >= 0) w.WriteNumber("skin", mn.skin);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                if (meshJson.Count > 0) { w.WriteStartArray("meshes"); foreach (var a in meshJson) a(w); w.WriteEndArray(); }
                if (skinJson.Count > 0) { w.WriteStartArray("skins"); foreach (var a in skinJson) a(w); w.WriteEndArray(); }

                w.WriteStartArray("materials");
                for (int i = 0; i < model.Materials.Count + extraMaterials.Count; i++)
                {
                    string name = i < model.Materials.Count ? model.Materials[i].Name : extraMaterials[i - model.Materials.Count];
                    var (bt, nt) = i < model.Materials.Count ? matTex[i] : (-1, -1);
                    w.WriteStartObject();
                    w.WriteString("name", name ?? "");
                    w.WriteStartObject("pbrMetallicRoughness");
                    if (bt >= 0) { w.WriteStartObject("baseColorTexture"); w.WriteNumber("index", bt); w.WriteEndObject(); }
                    w.WriteNumber("metallicFactor", 0);
                    w.WriteNumber("roughnessFactor", 0.8);
                    w.WriteEndObject();
                    if (nt >= 0) { w.WriteStartObject("normalTexture"); w.WriteNumber("index", nt); w.WriteEndObject(); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                if (images.Count > 0)
                {
                    w.WriteStartArray("images");
                    foreach (var (view, mime) in images) { w.WriteStartObject(); w.WriteNumber("bufferView", view); w.WriteString("mimeType", mime); w.WriteEndObject(); }
                    w.WriteEndArray();
                    w.WriteStartArray("samplers"); w.WriteStartObject(); w.WriteEndObject(); w.WriteEndArray();
                    w.WriteStartArray("textures");
                    for (int i = 0; i < images.Count; i++) { w.WriteStartObject(); w.WriteNumber("sampler", 0); w.WriteNumber("source", i); w.WriteEndObject(); }
                    w.WriteEndArray();
                }

                w.WriteStartArray("accessors"); foreach (var a in accessors) a(w); w.WriteEndArray();
                w.WriteStartArray("bufferViews");
                foreach (var (offset, length, target) in views)
                {
                    w.WriteStartObject();
                    w.WriteNumber("buffer", 0);
                    w.WriteNumber("byteOffset", offset);
                    w.WriteNumber("byteLength", length);
                    if (target.HasValue) w.WriteNumber("target", target.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("buffers"); w.WriteStartObject(); w.WriteNumber("byteLength", binBytes.Length); w.WriteEndObject(); w.WriteEndArray();
                w.WriteEndObject();
            }
            var jsonBytes = json.ToArray();
            int jsonPad = (4 - jsonBytes.Length % 4) % 4;

            var outS = new MemoryStream();
            var bw = new BinaryWriter(outS);
            int total = 12 + 8 + jsonBytes.Length + jsonPad + 8 + binBytes.Length;
            bw.Write(Magic); bw.Write(2u); bw.Write((uint)total);
            bw.Write((uint)(jsonBytes.Length + jsonPad)); bw.Write(ChunkJson); bw.Write(jsonBytes);
            for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);
            bw.Write((uint)binBytes.Length); bw.Write(ChunkBin); bw.Write(binBytes);
            bw.Flush();
            return outS.ToArray();
        }

        /// <summary>Unity/G3 UVs have v up from the bottom; glTF has v down from the top.</summary>
        static float[] FlipV(float[] uv)
        {
            var r = (float[])uv.Clone();
            for (int i = 1; i < r.Length; i += 2) r[i] = 1 - r[i];
            return r;
        }

        static double[] NormalizeQ(double[] q)
        {
            double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return l > 0 ? new[] { q[0] / l, q[1] / l, q[2] / l, q[3] / l } : new double[] { 0, 0, 0, 1 };
        }

        // ---------------------------------------------------------------------------------------------------------
        // Read

        public static RigModel Read(string path) => Read(File.ReadAllBytes(path));

        public static RigModel Read(byte[] data)
        {
            if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != Magic) throw new InvalidDataException("not a .glb file");
            int p = 12;
            byte[] jsonBytes = null, bin = null;
            while (p + 8 <= data.Length)
            {
                int len = (int)BitConverter.ToUInt32(data, p);
                uint type = BitConverter.ToUInt32(data, p + 4);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, p + 8, chunk, 0, len);
                if (type == ChunkJson) jsonBytes = chunk; else if (type == ChunkBin && bin == null) bin = chunk;
                p += 8 + len;
            }
            if (jsonBytes == null) throw new InvalidDataException("glb without JSON chunk");
            using var doc = JsonDocument.Parse(jsonBytes);
            return new Reader(doc.RootElement, bin ?? Array.Empty<byte>()).Build();
        }

        sealed class Reader
        {
            readonly JsonElement _root;
            readonly byte[] _bin;
            public Reader(JsonElement root, byte[] bin) { _root = root; _bin = bin; }

            JsonElement Arr(string name, int i) => _root.GetProperty(name)[i];
            static bool Has(JsonElement e, string name, out JsonElement v) => e.TryGetProperty(name, out v);

            public RigModel Build()
            {
                var model = new RigModel();
                if (Has(_root, "asset", out var asset) && Has(asset, "generator", out var gen)) model.Generator = gen.GetString();
                if (!Has(_root, "nodes", out var nodes)) return model;
                int count = nodes.GetArrayLength();
                for (int i = 0; i < count; i++) model.Nodes.Add(new RigNode { Name = $"node{i}" });
                for (int i = 0; i < count; i++)
                {
                    var n = nodes[i];
                    var rn = model.Nodes[i];
                    if (Has(n, "name", out var nm)) rn.Name = nm.GetString();
                    if (Has(n, "matrix", out var mx))
                    {
                        var cm = mx.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                        var rm = new double[16];
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) rm[r * 4 + c] = cm[c * 4 + r];
                        M4.Decompose(rm, out rn.T, out rn.R, out rn.S);
                    }
                    else
                    {
                        if (Has(n, "translation", out var t)) rn.T = t.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                        if (Has(n, "rotation", out var r)) rn.R = r.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                        if (Has(n, "scale", out var s)) rn.S = s.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    }
                    if (Has(n, "children", out var ch))
                        foreach (var c in ch.EnumerateArray()) model.Nodes[c.GetInt32()].Parent = i;
                }

                var matNames = new List<string>();
                if (Has(_root, "materials", out var mats))
                    for (int i = 0; i < mats.GetArrayLength(); i++)
                    {
                        var m = mats[i];
                        var rm = new RigMaterial { Name = Has(m, "name", out var mn) ? mn.GetString() : $"material{i}" };
                        if (Has(m, "pbrMetallicRoughness", out var pbr) && Has(pbr, "baseColorTexture", out var bt))
                            rm.BaseColorImage = Image(bt.GetProperty("index").GetInt32(), out rm.BaseColorMime);
                        if (Has(m, "normalTexture", out var nt)) rm.NormalImage = Image(nt.GetProperty("index").GetInt32(), out rm.NormalMime);
                        model.Materials.Add(rm);
                        matNames.Add(rm.Name);
                    }

                for (int i = 0; i < count; i++)
                {
                    var n = nodes[i];
                    if (!Has(n, "mesh", out var meshIdx)) continue;
                    model.Nodes[i].CarriesMesh = true;
                    int skin = Has(n, "skin", out var sk) ? sk.GetInt32() : -1;
                    model.Meshes.Add(ReadMesh(Arr("meshes", meshIdx.GetInt32()), skin, i, model, matNames));
                }
                return model;
            }

            byte[] Image(int texture, out string mime)
            {
                mime = "image/png";
                var tex = Arr("textures", texture);
                if (!Has(tex, "source", out var src)) return null;
                var img = Arr("images", src.GetInt32());
                if (Has(img, "mimeType", out var mt)) mime = mt.GetString();
                if (!Has(img, "bufferView", out var bv)) return null; // external URIs are not followed
                var view = Arr("bufferViews", bv.GetInt32());
                int off = Has(view, "byteOffset", out var o) ? o.GetInt32() : 0;
                int len = view.GetProperty("byteLength").GetInt32();
                var r = new byte[len];
                Buffer.BlockCopy(_bin, off, r, 0, len);
                return r;
            }

            RigMesh ReadMesh(JsonElement mesh, int skin, int node, RigModel model, List<string> matNames)
            {
                var rm = new RigMesh { Name = Has(mesh, "name", out var mn) ? mn.GetString() : model.Nodes[node].Name };
                var pos = new List<float>(); var nrm = new List<float>(); var uv0 = new List<float>(); var uv1 = new List<float>();
                var ji = new List<int>(); var wt = new List<float>();
                bool hasN = true, hasUv0 = true, hasUv1 = true, hasSkin = skin >= 0;
                int stride = 4;
                var blocks = new Dictionary<string, int>(); // attribute set → base vertex (shared accessors are merged once)
                var morphBlocks = new List<(int baseVertex, int count, float[][] pos, float[][] nrm)>();
                string[] targetNames = null;
                if (Has(mesh, "extras", out var extras) && Has(extras, "targetNames", out var tn))
                    targetNames = tn.EnumerateArray().Select(e => e.GetString()).ToArray();
                int morphCount = 0;
                foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
                {
                    if (Has(prim, "mode", out var mode) && mode.GetInt32() != 4) continue; // triangles only
                    var attrs = prim.GetProperty("attributes");
                    string key = attrs.GetRawText();
                    if (!blocks.TryGetValue(key, out int baseVertex))
                    {
                        baseVertex = pos.Count / 3;
                        blocks[key] = baseVertex;
                        var p = Floats(attrs.GetProperty("POSITION").GetInt32(), out int nv);
                        pos.AddRange(p);
                        if (Has(prim, "targets", out var targets))
                        {
                            int tc = targets.GetArrayLength();
                            var tp = new float[tc][]; var tnrm = new float[tc][];
                            for (int t = 0; t < tc; t++)
                            {
                                var ta = targets[t];
                                tp[t] = Has(ta, "POSITION", out var tpa) ? Floats(tpa.GetInt32(), out _) : new float[nv * 3];
                                tnrm[t] = Has(ta, "NORMAL", out var tna) ? Floats(tna.GetInt32(), out _) : null;
                            }
                            morphCount = Math.Max(morphCount, tc);
                            morphBlocks.Add((baseVertex, nv, tp, tnrm));
                        }
                        if (hasN && Has(attrs, "NORMAL", out var a)) nrm.AddRange(Floats(a.GetInt32(), out _)); else hasN = false;
                        if (hasUv0 && Has(attrs, "TEXCOORD_0", out a)) uv0.AddRange(FlipV(Floats(a.GetInt32(), out _))); else hasUv0 = false;
                        if (hasUv1 && Has(attrs, "TEXCOORD_1", out a)) uv1.AddRange(FlipV(Floats(a.GetInt32(), out _))); else hasUv1 = false;
                        if (hasSkin && Has(attrs, "JOINTS_0", out var j0) && Has(attrs, "WEIGHTS_0", out var w0))
                        {
                            bool wide = Has(attrs, "JOINTS_1", out var j1) & Has(attrs, "WEIGHTS_1", out var w1);
                            if (wide && stride == 4)
                            {
                                // Widen what was read so far to 8 influences per vertex.
                                var oj = ji.ToArray(); var ow = wt.ToArray();
                                ji.Clear(); wt.Clear();
                                for (int v = 0; v < oj.Length / 4; v++)
                                {
                                    for (int k = 0; k < 4; k++) { ji.Add(oj[v * 4 + k]); wt.Add(ow[v * 4 + k]); }
                                    for (int k = 0; k < 4; k++) { ji.Add(0); wt.Add(0); }
                                }
                                stride = 8;
                            }
                            var a0 = Ints(j0.GetInt32(), out _); var b0 = Floats(w0.GetInt32(), out _);
                            int[] a1 = null; float[] b1 = null;
                            if (wide) { a1 = Ints(j1.GetInt32(), out _); b1 = Floats(w1.GetInt32(), out _); }
                            for (int v = 0; v < nv; v++)
                            {
                                for (int k = 0; k < 4; k++) { ji.Add(a0[v * 4 + k]); wt.Add(b0[v * 4 + k]); }
                                if (stride == 8)
                                    for (int k = 0; k < 4; k++) { ji.Add(a1 != null ? a1[v * 4 + k] : 0); wt.Add(b1 != null ? b1[v * 4 + k] : 0); }
                            }
                        }
                        else hasSkin = false;
                    }
                    int[] idx = Has(prim, "indices", out var ia) ? Ints(ia.GetInt32(), out _)
                        : Enumerable.Range(0, Floats(attrs.GetProperty("POSITION").GetInt32(), out int c).Length / 3).ToArray();
                    for (int k = 0; k < idx.Length; k++) idx[k] += baseVertex;
                    string mat = Has(prim, "material", out var mi) && mi.GetInt32() < matNames.Count ? matNames[mi.GetInt32()] : "";
                    var existing = rm.SubMeshes.FirstOrDefault(s => s.Material == mat);
                    if (existing != null) existing.Indices = existing.Indices.Concat(idx).ToArray();
                    else rm.SubMeshes.Add(new RigSubMesh { Material = mat, Indices = idx });
                }
                rm.Positions = pos.ToArray();
                for (int t = 0; t < morphCount; t++)
                {
                    var mo = new RigMorph
                    {
                        Name = targetNames != null && t < targetNames.Length ? targetNames[t] : $"target{t}",
                        Positions = new float[pos.Count],
                    };
                    foreach (var b in morphBlocks)
                    {
                        if (t >= b.pos.Length) continue;
                        Array.Copy(b.pos[t], 0, mo.Positions, b.baseVertex * 3, Math.Min(b.pos[t].Length, b.count * 3));
                        if (b.nrm[t] == null) continue;
                        mo.Normals ??= new float[pos.Count];
                        Array.Copy(b.nrm[t], 0, mo.Normals, b.baseVertex * 3, Math.Min(b.nrm[t].Length, b.count * 3));
                    }
                    rm.Morphs.Add(mo);
                }
                rm.Normals = hasN && nrm.Count == pos.Count ? nrm.ToArray() : null;
                rm.Uv0 = hasUv0 && uv0.Count / 2 == pos.Count / 3 ? uv0.ToArray() : null;
                rm.Uv1 = hasUv1 && uv1.Count / 2 == pos.Count / 3 ? uv1.ToArray() : null;
                if (skin >= 0)
                {
                    var s = Arr("skins", skin);
                    rm.Joints = s.GetProperty("joints").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    rm.InverseBind = new double[rm.Joints.Length][];
                    if (Has(s, "inverseBindMatrices", out var ibmA))
                    {
                        var f = Floats(ibmA.GetInt32(), out _);
                        for (int j = 0; j < rm.Joints.Length; j++)
                        {
                            var m = new double[16];
                            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) m[r * 4 + c] = f[j * 16 + c * 4 + r];
                            rm.InverseBind[j] = m;
                        }
                    }
                    else for (int j = 0; j < rm.Joints.Length; j++) rm.InverseBind[j] = M4.Identity();
                    if (hasSkin && wt.Count == pos.Count / 3 * stride) { rm.Stride = stride; rm.JointIndices = ji.ToArray(); rm.Weights = wt.ToArray(); }
                    else rm.Joints = null; // skin without weights: treated as static
                }
                if (!rm.IsSkinned) rm.Node = node;
                return rm;
            }

            static int Size(int comp) => comp == Float || comp == UInt ? 4 : comp == UShort || comp == Short ? 2 : 1;
            static int Width(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, "MAT3" => 9, "MAT2" => 4, _ => 1 };

            /// <summary>Elements of an accessor as doubles (normalised integers mapped to 0..1 / −1..1).</summary>
            double[] Raw(int accessor, out int count, out int width)
            {
                var a = Arr("accessors", accessor);
                count = a.GetProperty("count").GetInt32();
                width = Width(a.GetProperty("type").GetString());
                int comp = a.GetProperty("componentType").GetInt32();
                bool norm = Has(a, "normalized", out var nz) && nz.GetBoolean();
                var r = new double[count * width];
                if (Has(a, "bufferView", out var bvE))
                {
                    var view = Arr("bufferViews", bvE.GetInt32());
                    int off = (Has(view, "byteOffset", out var vo) ? vo.GetInt32() : 0) + (Has(a, "byteOffset", out var ao) ? ao.GetInt32() : 0);
                    int stride = Has(view, "byteStride", out var st) ? st.GetInt32() : Size(comp) * width;
                    // Matrix columns are 4-byte aligned for 1/2-byte components; only float matrices occur here.
                    for (int i = 0; i < count; i++)
                        for (int k = 0; k < width; k++)
                            r[i * width + k] = Element(comp, norm, off + i * stride + k * Size(comp));
                }
                // sparse: Blender writes morph targets this way (only the moved vertices; the rest stays 0 / the base view)
                if (Has(a, "sparse", out var sp))
                {
                    int n = sp.GetProperty("count").GetInt32();
                    var ix = sp.GetProperty("indices");
                    var iv = Arr("bufferViews", ix.GetProperty("bufferView").GetInt32());
                    int ioff = (Has(iv, "byteOffset", out var io) ? io.GetInt32() : 0) + (Has(ix, "byteOffset", out var io2) ? io2.GetInt32() : 0);
                    int icomp = ix.GetProperty("componentType").GetInt32();
                    var vl = sp.GetProperty("values");
                    var vv = Arr("bufferViews", vl.GetProperty("bufferView").GetInt32());
                    int voff = (Has(vv, "byteOffset", out var vo3) ? vo3.GetInt32() : 0) + (Has(vl, "byteOffset", out var vo4) ? vo4.GetInt32() : 0);
                    for (int j = 0; j < n; j++)
                    {
                        int idx = (int)Element(icomp, false, ioff + j * Size(icomp));
                        if (idx < 0 || idx >= count) throw new InvalidDataException($"sparse index {idx} out of {count}");
                        for (int k = 0; k < width; k++)
                            r[idx * width + k] = Element(comp, norm, voff + (j * width + k) * Size(comp));
                    }
                }
                return r;
            }

            double Element(int comp, bool norm, int o) => comp switch
            {
                Float => BitConverter.ToSingle(_bin, o),
                UInt => BitConverter.ToUInt32(_bin, o),
                UShort => norm ? BitConverter.ToUInt16(_bin, o) / 65535.0 : BitConverter.ToUInt16(_bin, o),
                Short => norm ? Math.Max(BitConverter.ToInt16(_bin, o) / 32767.0, -1) : BitConverter.ToInt16(_bin, o),
                UByte => norm ? _bin[o] / 255.0 : _bin[o],
                Byte => norm ? Math.Max((sbyte)_bin[o] / 127.0, -1) : (sbyte)_bin[o],
                _ => throw new NotSupportedException($"component type {comp}"),
            };

            float[] Floats(int accessor, out int count) => Raw(accessor, out count, out _).Select(d => (float)d).ToArray();
            int[] Ints(int accessor, out int count) => Raw(accessor, out count, out _).Select(d => (int)Math.Round(d)).ToArray();
        }
    }
}
