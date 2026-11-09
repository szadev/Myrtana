using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using G3.Formats.Image;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Source glbs from a <see cref="SourceRig"/>: the pure counterpart of G3RemasterExporter.FromRig / ExportSpawn
    /// (Docs/remaster-pipeline.md §4, §12). Every rig transform except renderer objects becomes a node (local TRS,
    /// root at identity), skinned meshes get joints = their unique bones with Unity's bind poses as inverse bind
    /// matrices and the top 4 influences, static meshes hang on their slot; one primitive per material slot, base colour
    /// images as PNG; finally Unity → glTF space (<see cref="RigModel.MirrorX"/>).
    /// </summary>
    public static class SourceExport
    {
        public const string Generator = "G3World G3RemasterExporter";

        /// <summary>Renderers of one part (BelongsTo): static by object name, skinned by name or <c>&lt;stem&gt;_&lt;n&gt;</c>.</summary>
        public static List<SourceRenderer> PartRenderers(SourceRig rig, string stem, bool isMesh)
        {
            var re = new Regex("^" + Regex.Escape(stem) + @"_\d+$", RegexOptions.IgnoreCase);
            return rig.Renderers().Where(r => isMesh
                ? !r.Skinned && r.Name.Equals(stem, StringComparison.OrdinalIgnoreCase)
                : r.Skinned && (r.Name.Equals(stem, StringComparison.OrdinalIgnoreCase) || re.IsMatch(r.Name))).ToList();
        }

        /// <param name="baseColorPng">PNG of an image stem (null = none); null = no textures.</param>
        public static RigModel FromRig(SourceRig rig, IList<SourceRenderer> renderers, Func<string, byte[]> baseColorPng)
        {
            var model = new RigModel { Generator = Generator };
            var index = new Dictionary<int, int>();
            void Walk(int t, int parent)
            {
                var n = rig.Nodes[t];
                if (t != 0 && n.Renderer != null) return;
                bool isRoot = t == 0;
                int i = model.AddNode(n.Name, parent,
                    isRoot ? new double[3] : new double[] { n.T[0], n.T[1], n.T[2] },
                    isRoot ? new double[] { 0, 0, 0, 1 } : new double[] { n.R[0], n.R[1], n.R[2], n.R[3] });
                index[t] = i;
                foreach (int c in n.Children) Walk(c, i);
            }
            Walk(0, -1);

            var materials = new Dictionary<string, RigMaterial>();
            var order = new List<string>();
            foreach (var r in renderers)
            {
                var rm = new RigMesh
                {
                    Name = r.Name,
                    Positions = (float[])r.Positions.Clone(),
                    Normals = r.Normals != null && r.Normals.Length == r.Positions.Length ? (float[])r.Normals.Clone() : null,
                    Uv0 = r.Uv0 != null && r.Uv0.Length > 0 ? (float[])r.Uv0.Clone() : null,
                    Uv1 = r.Uv1 != null && r.Uv1.Length * 3 == r.Positions.Length * 2 ? (float[])r.Uv1.Clone() : null,
                };
                for (int s = 0; s < r.Triangles.Count; s++)
                {
                    var mat = s < r.Materials.Count ? r.Materials[s] : null;
                    string mname = mat != null ? mat.Name : $"{r.Name}_slot{s}";
                    rm.SubMeshes.Add(new RigSubMesh { Material = mname, Indices = (int[])r.Triangles[s].Clone() });
                    if (!materials.ContainsKey(mname))
                    {
                        materials[mname] = new RigMaterial { Name = mname, BaseColorImage = baseColorPng != null && mat?.BaseImage != null ? baseColorPng(mat.BaseImage) : null };
                        order.Add(mname);
                    }
                }
                if (r.Skinned)
                {
                    // Unique joints (several part nodes can be bound to the same bone).
                    var joints = new List<int>();
                    var bindIndex = new List<int>();
                    var remap = new int[r.Bones.Length];
                    for (int b = 0; b < r.Bones.Length; b++)
                    {
                        int node = index.TryGetValue(r.Bones[b], out int ni) ? ni : 0;
                        int j = joints.IndexOf(node);
                        if (j < 0) { joints.Add(node); bindIndex.Add(b); j = joints.Count - 1; }
                        remap[b] = j;
                    }
                    rm.Joints = joints.ToArray();
                    rm.InverseBind = bindIndex.Select(b => (double[])r.Bindposes[b].Clone()).ToArray();
                    int nv = r.VertexCount;
                    rm.JointIndices = new int[nv * 4];
                    rm.Weights = new float[nv * 4];
                    for (int i = 0; i < nv; i++)
                    {
                        // Merge influences that map to the same joint, keep the 4 largest.
                        var keys = new List<int>();
                        var acc = new Dictionary<int, float>();
                        for (int k = 0; k < 4; k++)
                        {
                            float wt = r.BoneWeight[i * 4 + k];
                            if (wt <= 0) continue;
                            int j = remap[r.BoneIndex[i * 4 + k]];
                            if (acc.TryGetValue(j, out var o)) acc[j] = o + wt;
                            else { acc[j] = wt; keys.Add(j); }
                        }
                        int slot = 0;
                        foreach (int j in keys.OrderByDescending(j => acc[j]).Take(4))
                        {
                            rm.JointIndices[i * 4 + slot] = j;
                            rm.Weights[i * 4 + slot] = acc[j];
                            slot++;
                        }
                    }
                }
                else
                {
                    // Static mesh: modelled in its parent's frame (the slot); the renderer object sits at identity.
                    int parent = rig.Nodes[r.Object].Parent;
                    rm.Node = parent >= 0 && index.TryGetValue(parent, out int pn) ? pn : 0;
                }
                model.Meshes.Add(rm);
            }
            foreach (var name in order) model.Materials.Add(materials[name]);
            model.MirrorX(); // Unity → glTF space
            return model;
        }

        /// <summary>
        /// The base colour image as the Unity exporter's GPU read-back writes it: top mip decoded to RGBA, rows bottom
        /// first (Unity loads the top-down G3 data with row 0 at v = 0; EncodeToPNG writes the highest row first).
        /// </summary>
        public static byte[] BaseColorPng(ImageResource img)
        {
            if (img == null || img.Mips == null || img.Mips.Length == 0) return null;
            var rgba = Dxt.DecodeRgba(img.Mips[0], img.Width, img.Height, img.Format);
            return Png.Encode(Png.FlipRows(rgba, img.Width, img.Height), img.Width, img.Height);
        }

        public static string Summary(RigModel m) =>
            $"{m.Nodes.Count} nodes, {m.Meshes.Count} meshes ({string.Join(", ", m.Meshes.Select(x => $"{x.Name} {x.VertexCount} v"))})";
    }
}
