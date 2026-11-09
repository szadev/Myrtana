using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G3.Formats.Animation;
using G3.Formats.Mesh;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// An NPC rig as the Unity character importer builds it (G3CharacterImporter.BuildNpcRig, override off), without
    /// Unity: the transform hierarchy (root, skeleton nodes in actor order, part nodes the skeleton lacks, renderer
    /// objects) with float local TRS in Unity space, and the renderers' mesh data, bones, bind poses, weights and
    /// materials exactly as Unity's Mesh would hold them. <see cref="SourceExport.FromRig"/> turns it into the Source glb.
    /// </summary>
    public sealed class SourceRig
    {
        public sealed class Node
        {
            public string Name;
            public int Parent = -1;
            public float[] T = new float[3];
            public float[] R = { 0, 0, 0, 1 };
            public readonly List<int> Children = new List<int>();
            /// <summary>Set on renderer objects (part meshes); null on rig transforms.</summary>
            public SourceRenderer Renderer;
            public override string ToString() => Name;
        }

        public readonly List<Node> Nodes = new List<Node>();
        public string Skeleton;
        public readonly List<string> Parts = new List<string>();
        public readonly List<string> Log = new List<string>();

        public int Add(string name, int parent, float[] t = null, float[] r = null)
        {
            Nodes.Add(new Node { Name = name, Parent = parent, T = t ?? new float[3], R = r ?? new float[] { 0, 0, 0, 1 } });
            int i = Nodes.Count - 1;
            if (parent >= 0) Nodes[parent].Children.Add(i);
            return i;
        }

        /// <summary>Renderers in hierarchy order (Unity's GetComponentsInChildren: depth first, children in order).</summary>
        public List<SourceRenderer> Renderers()
        {
            var list = new List<SourceRenderer>();
            void Walk(int i)
            {
                if (Nodes[i].Renderer != null) list.Add(Nodes[i].Renderer);
                foreach (int c in Nodes[i].Children) Walk(c);
            }
            if (Nodes.Count > 0) Walk(0);
            return list;
        }

        /// <summary>Local matrix (row-major, Unity space) of a node from its float TRS.</summary>
        public double[] Local(int i)
        {
            var n = Nodes[i];
            return M4.Trs(new double[] { n.T[0], n.T[1], n.T[2] }, new double[] { n.R[0], n.R[1], n.R[2], n.R[3] }, null);
        }

        public double[] World(int i)
        {
            var m = Local(i);
            for (int p = Nodes[i].Parent; p >= 0; p = Nodes[p].Parent) m = M4.Mul(Local(p), m);
            return m;
        }
    }

    /// <summary>A part mesh renderer of a <see cref="SourceRig"/> (Unity space, metres).</summary>
    public sealed class SourceRenderer
    {
        public string Name;
        /// <summary>The part (stem) the renderer was built for.</summary>
        public string Part;
        public bool Skinned;
        /// <summary>Its node in <see cref="SourceRig.Nodes"/> (static meshes: child of the slot, at identity).</summary>
        public int Object;
        public float[] Positions, Normals, Uv0, Uv1;
        public readonly List<int[]> Triangles = new List<int[]>();
        public readonly List<SourceMaterial> Materials = new List<SourceMaterial>();
        /// <summary>Skinned: bone nodes (SkinnedMeshRenderer.bones), bind poses (row-major), 4 influences per vertex.</summary>
        public int[] Bones;
        public double[][] Bindposes;
        public int[] BoneIndex;
        public float[] BoneWeight;
        public int VertexCount => Positions.Length / 3;
        public override string ToString() => Name;
    }

    /// <summary>
    /// Builds <see cref="SourceRig"/>s from the original files: the pure counterpart of G3CharacterImporter.BuildNpcRig /
    /// AttachMeshes / BindBone / BuildMesh and G3AssetCache.TryGetMesh (Docs/remaster-pipeline.md §12). Conventions:
    /// engine cm → m (× 0.01), EMotionFX quaternions copied, actor triangles as stored, .xcmsh triangles (i0, i2, i1).
    /// </summary>
    public sealed class SourceRigBuilder
    {
        const float S = 0.01f;
        readonly ISourceData _data;
        public readonly SourceMaterials Materials;
        /// <summary>Skin vertices to part nodes the skeleton lacks (face bones), as the exporter and face kits do.</summary>
        public bool BindAddedNodes = true;

        public SourceRigBuilder(ISourceData data)
        {
            _data = data;
            Materials = new SourceMaterials(data);
        }

        public static float[] ToUnity(V3 v) => new[] { v.X * S, v.Y * S, v.Z * S };
        public static float[] ToUnity(Q4 q) { var n = q.Normalized; return new[] { n.X, n.Y, n.Z, n.W }; }

        public SourceRig Build(NpcSpawn s, string name)
        {
            var actorParts = s.Parts.Where(p => !p.IsMesh).Select(p => (p.Resource, p.Switch)).ToList();
            if (actorParts.Count == 0) actorParts.Add((s.Actor, s.Switch));
            string skelName = s.Parts.FirstOrDefault(p => p.IsBody)?.Resource ?? actorParts[0].Resource;
            var skel = _data.Actor(skelName);
            var rig = new SourceRig { Skeleton = Path.GetFileNameWithoutExtension(skelName) };
            int root = rig.Add(name, -1);

            // BuildSkeleton: all nodes first, then parented in index order (that is the children order).
            var xf = new int[skel.Nodes.Count];
            for (int i = 0; i < xf.Length; i++) { rig.Nodes.Add(new SourceRig.Node { Name = skel.Nodes[i].Name }); xf[i] = rig.Nodes.Count - 1; }
            for (int i = 0; i < xf.Length; i++)
            {
                var n = skel.Nodes[i];
                var node = rig.Nodes[xf[i]];
                node.Parent = n.Parent >= 0 ? xf[n.Parent] : root;
                rig.Nodes[node.Parent].Children.Add(xf[i]);
                node.T = ToUnity(n.Position);
                node.R = ToUnity(n.Rotation);
            }
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < xf.Length; i++) byName[skel.Nodes[i].Name] = xf[i];

            foreach (var (res, sw) in actorParts)
            {
                EmfxActor a;
                try { a = _data.Actor(res); }
                catch (Exception ex) { rig.Log.Add($"{s.Name}: part {res}: {ex.Message}"); continue; }
                AddMissingNodes(rig, a, byName);
                AttachMeshes(rig, a, Path.GetFileNameWithoutExtension(res), skel, xf, root, sw, BindAddedNodes ? byName : null);
                rig.Parts.Add(Path.GetFileNameWithoutExtension(res) + (sw != 0 ? "_S" + sw : ""));
            }
            foreach (var p in s.Parts.Where(p => p.IsMesh))
            {
                var mesh = _data.Mesh(p.Resource);
                if (mesh == null || mesh.Elements.Count == 0) { rig.Log.Add($"{s.Name}: mesh {p.Resource} missing"); continue; }
                if (!byName.TryGetValue(p.Slot, out int slot)) { rig.Log.Add($"{s.Name}: no {p.Slot} for {p.Resource}"); continue; }
                int go = rig.Add(p.Stem, slot);
                var r = StaticRenderer(mesh, p.Stem);
                r.Object = go;
                r.Part = p.Stem;
                foreach (var e in mesh.Elements) r.Materials.Add(Materials.Variant(Materials.World(e.Material), p.Switch));
                rig.Nodes[go].Renderer = r;
                rig.Parts.Add(p.Stem + (p.Switch != 0 ? "_S" + p.Switch : ""));
            }
            rig.Log.AddRange(Materials.Log);
            Materials.Log.Clear();
            return rig;
        }

        /// <summary>Adds part-actor nodes the skeleton lacks (head Slot_Hair/Beard/Helmet, face bones) under their same-named parent.</summary>
        static void AddMissingNodes(SourceRig rig, EmfxActor a, Dictionary<string, int> byName)
        {
            for (bool added = true; added;)
            {
                added = false;
                foreach (var n in a.Nodes)
                {
                    if (byName.ContainsKey(n.Name) || n.Parent < 0) continue;
                    if (!byName.TryGetValue(a.Nodes[n.Parent].Name, out int parent)) continue;
                    byName[n.Name] = rig.Add(n.Name, parent, ToUnity(n.Position), ToUnity(n.Rotation));
                    added = true;
                }
            }
        }

        void AttachMeshes(SourceRig rig, EmfxActor act, string stem, EmfxActor skel, int[] skelXf, int root, int materialSwitch,
                          IReadOnlyDictionary<string, int> addedNodes)
        {
            // Bind poses come from the skeleton the part is worn on (some heads carry their own, rotated Hero_ROOT).
            var skelBind = ActorPose.Bind(skel);
            int meshNo = 0;
            foreach (var m in act.Meshes)
            {
                if (m.IsCollisionMesh || m.Indices.Length == 0) continue;
                string meshName = meshNo++ == 0 ? stem : $"{stem}_{meshNo}";
                var used = new List<int>();
                var slot = new Dictionary<int, int>();
                int Slot(int node) { if (!slot.TryGetValue(node, out int s)) { s = used.Count; slot[node] = s; used.Add(node); } return s; }

                int nv = m.Positions.Length;
                var r = new SourceRenderer { Name = meshName, Part = stem, Skinned = true, BoneIndex = new int[nv * 4], BoneWeight = new float[nv * 4] };
                for (int v = 0; v < nv; v++)
                {
                    var inf = m.Skin?[m.OrgVertex[v]];
                    var list = inf == null || inf.Length == 0
                        ? new List<SkinInfluence> { new SkinInfluence((ushort)m.Node, 1) }
                        : inf.Where(x => x.Weight > 0).OrderByDescending(x => x.Weight).Take(4).ToList();
                    float sum = list.Sum(x => x.Weight);
                    for (int k = 0; k < list.Count; k++)
                    {
                        r.BoneIndex[v * 4 + Math.Min(k, 3)] = Slot(list[k].Node);
                        r.BoneWeight[v * 4 + Math.Min(k, 3)] = list[k].Weight / sum;
                    }
                }
                r.Bones = new int[used.Count];
                r.Bindposes = new double[used.Count][];
                for (int b = 0; b < used.Count; b++)
                    BindBone(rig, act, used[b], skel, skelXf, skelBind, addedNodes, stem, out r.Bones[b], out r.Bindposes[b]);

                r.Positions = new float[nv * 3];
                r.Normals = new float[nv * 3];
                r.Uv0 = new float[nv * 2];
                for (int i = 0; i < nv; i++)
                {
                    var p = ToUnity(m.Positions[i]);
                    r.Positions[i * 3] = p[0]; r.Positions[i * 3 + 1] = p[1]; r.Positions[i * 3 + 2] = p[2];
                    var n = Normalized(m.Normals[i].X, m.Normals[i].Y, m.Normals[i].Z);
                    r.Normals[i * 3] = n[0]; r.Normals[i * 3 + 1] = n[1]; r.Normals[i * 3 + 2] = n[2];
                    if (m.Uv0 != null) { r.Uv0[i * 2] = m.Uv0[i * 2]; r.Uv0[i * 2 + 1] = m.Uv0[i * 2 + 1]; }
                }
                if (m.Uv1 != null) r.Uv1 = (float[])m.Uv1.Clone();
                foreach (var sm in m.SubMeshes)
                {
                    // EMotionFX actors are wound opposite to .xcmsh: the stored order is Unity's.
                    var tris = new int[sm.IndexCount];
                    Array.Copy(m.Indices, sm.IndexStart, tris, 0, tris.Length);
                    r.Triangles.Add(tris);
                    r.Materials.Add(Materials.Variant(Materials.ForActor(act, sm.MaterialId, stem), materialSwitch));
                }
                int go = rig.Add(meshName, root);
                r.Object = go;
                rig.Nodes[go].Renderer = r;
            }
        }

        /// <summary>Unity's Vector3.normalized in float (zero below 1e-5).</summary>
        public static float[] Normalized(float x, float y, float z)
        {
            float mag = (float)Math.Sqrt(x * x + y * y + z * z);
            return mag > 1e-5f ? new[] { x / mag, y / mag, z / mag } : new float[3];
        }

        /// <summary>
        /// The bone and bind pose a part node is skinned to: a node added to the rig (face bone) in its bind pose; else
        /// the same-named skeleton bone or the nearest part ancestor the skeleton has; the skeleton root when none.
        /// </summary>
        static void BindBone(SourceRig rig, EmfxActor act, int node, EmfxActor skel, int[] skelXf, ActorPose.Xf[] skelBind,
                             IReadOnlyDictionary<string, int> addedNodes, string stem, out int bone, out double[] bindpose)
        {
            if (addedNodes != null && skel.FindNode(act.Nodes[node].Name) < 0 && addedNodes.TryGetValue(act.Nodes[node].Name, out int added))
            {
                bone = added;
                bindpose = M4.Inverse(rig.World(added));
                return;
            }
            int target = -1, src = node;
            for (int p = node; p >= 0 && target < 0; p = act.Nodes[p].Parent)
            {
                target = skel.FindNode(act.Nodes[p].Name);
                src = p;
            }
            if (target < 0) { target = 0; rig.Log.Add($"{stem}: bone {act.Nodes[node].Name} not in skeleton, bound to {skel.Nodes[0].Name}"); }
            else if (src != node) rig.Log.Add($"{stem}: bone {act.Nodes[node].Name} not in skeleton, bound to {skel.Nodes[target].Name}");
            bone = skelXf[target];
            var b = skelBind[target];
            var t = ToUnity(b.Pos);
            var q = ToUnity(b.Rot);
            bindpose = M4.Inverse(M4.Trs(new double[] { t[0], t[1], t[2] }, new double[] { q[0], q[1], q[2], q[3] }, null));
        }

        /// <summary>G3AssetCache.BuildMesh: elements concatenated, cm → m, D3D winding → (i0, i2, i1), missing normals zero.</summary>
        static SourceRenderer StaticRenderer(MeshResource res, string name)
        {
            int total = res.Elements.Sum(e => e.VertexCount);
            var r = new SourceRenderer { Name = name, Positions = new float[total * 3], Normals = new float[total * 3], Uv0 = new float[total * 2] };
            if (res.Elements.Any(e => e.Uv1 != null)) r.Uv1 = new float[total * 2];
            int baseVertex = 0;
            foreach (var e in res.Elements)
            {
                int n = e.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    int v = baseVertex + i;
                    for (int c = 0; c < 3; c++) r.Positions[v * 3 + c] = e.Positions[i * 3 + c] * S;
                    if (e.Normals != null) for (int c = 0; c < 3; c++) r.Normals[v * 3 + c] = e.Normals[i * 3 + c];
                    if (e.Uv0 != null) { r.Uv0[v * 2] = e.Uv0[i * 2]; r.Uv0[v * 2 + 1] = e.Uv0[i * 2 + 1]; }
                    if (r.Uv1 != null && e.Uv1 != null) { r.Uv1[v * 2] = e.Uv1[i * 2]; r.Uv1[v * 2 + 1] = e.Uv1[i * 2 + 1]; }
                }
                var tris = new int[e.Indices.Length];
                for (int t = 0; t < tris.Length; t += 3)
                {
                    tris[t] = (int)e.Indices[t] + baseVertex;
                    tris[t + 1] = (int)e.Indices[t + 2] + baseVertex;
                    tris[t + 2] = (int)e.Indices[t + 1] + baseVertex;
                }
                r.Triangles.Add(tris);
                baseVertex += n;
            }
            return r;
        }
    }
}
