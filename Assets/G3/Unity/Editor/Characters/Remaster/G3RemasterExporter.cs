using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using G3.Formats.Animation;
using G3.Formats.Remaster;
using G3.Unity.Characters;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Exports imported G3 characters to binary glTF for Blender (Docs/remaster-pipeline.md): the rig is built fresh
    /// by the importer in its bind pose (originals only: the remaster override is off), then every part — body, head,
    /// facial head, hair, beard — is written to <c>GameData/Remaster/Source/&lt;stem&gt;.glb</c> with the whole node
    /// hierarchy (bones with their exact bind transforms, Slot_* nodes, face bones), skin weights (4 per vertex, as
    /// Unity uses them), UV0/UV1, normals, one primitive per material slot (named after the Unity material) and,
    /// optionally, the base colour textures as PNG. A <c>&lt;name&gt;__all.glb</c> holds all parts together (context
    /// for fitting seams). The files are game-derived: they live under the git-ignored GameData folder.
    /// CLI: <c>return G3.Unity.Editor.Characters.G3RemasterExporter.ExportHero();</c>
    /// </summary>
    public static class G3RemasterExporter
    {
        /// <summary>The hero (PC_Hero parts) and his talking head.</summary>
        public static string ExportHero(bool textures = true)
        {
            var spawn = G3HeroImporter.FindHeroSpawn(out _);
            var sb = new StringBuilder(ExportSpawn(spawn, textures));
            var body = spawn.Parts.FirstOrDefault(p => p.IsBody)?.Resource;
            var head = spawn.Parts.FirstOrDefault(p => p.IsHead);
            string facial = head?.FacialResource != null ? Path.GetFileNameWithoutExtension(head.FacialResource) : "G3_Head_Hero_Hero_Animated_01";
            // The talking-head rig (body + facial actor with its face nodes) writes the body as <stem>__talk.glb, so the
            // static rig's <stem>.glb is kept; the facial head exists only there and keeps its own name.
            if (body != null)
            {
                string bodyStem = RemasterPaths.Stem(body);
                Func<string, string> talkStem = stem => string.Equals(stem, bodyStem, StringComparison.OrdinalIgnoreCase) ? RemasterPaths.TalkStem(stem) : stem;
                sb.Append('\n').Append(ExportActors(facial, textures, talkStem, body, facial + ".xact"));
            }
            return sb.ToString();
        }

        /// <summary>A world NPC by spawn name (its parts as worn, switches applied to the embedded textures).</summary>
        public static string ExportNpc(string spawnName, bool textures = true)
        {
            var s = G3NpcImporter.AllSpawns.FirstOrDefault(x => string.Equals(x.Name, spawnName, StringComparison.OrdinalIgnoreCase));
            if (s == null) return $"no spawn {spawnName}";
            return ExportSpawn(s, textures);
        }

        /// <summary>
        /// Arbitrary parts on the first body actor's skeleton: actors (<c>.xact</c>/<c>.FXA</c>) and static head meshes
        /// (<c>.xcmsh</c>, hung on their Slot_Hair/Beard/Helmet), e.g. a facial head on a body.
        /// </summary>
        public static string ExportActors(string name, bool textures, params string[] resources) => ExportActors(name, textures, null, resources);

        /// <param name="fileStem">The Source file stem of each part (null = the part's own stem).</param>
        public static string ExportActors(string name, bool textures, Func<string, string> fileStem, params string[] resources)
        {
            var s = new NpcSpawn { Name = name, Enabled = true, World = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } };
            foreach (var r in resources)
                s.Parts.Add(new NpcPart { Resource = r, IsMesh = r.EndsWith(".xcmsh", StringComparison.OrdinalIgnoreCase), EntityName = Path.GetFileNameWithoutExtension(r) });
            s.Actor = resources.FirstOrDefault(r => !r.EndsWith(".xcmsh", StringComparison.OrdinalIgnoreCase));
            return ExportSpawn(s, textures, fileStem);
        }

        /// <summary>Exports every part of the spawn; returns one line per file.</summary>
        /// <param name="fileStem">The Source file stem of each part (null = the part's own stem).</param>
        public static string ExportSpawn(NpcSpawn s, bool textures = true, Func<string, string> fileStem = null)
        {
            string project = G3RemasterOverride.ProjectDir;
            var lines = new List<string>();
            using var imp = new G3CharacterImporter { BindAddedNodes = true, UseRemaster = false };
            var root = imp.BuildNpcRig(s, s.Name, out _, out _, out _, out _);
            try
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true).ToList();
                foreach (var part in s.Parts.Count > 0 ? s.Parts : new List<NpcPart> { new NpcPart { Resource = s.Actor } })
                {
                    var mine = renderers.Where(r => BelongsTo(r, part)).ToList();
                    if (mine.Count == 0) { lines.Add($"{part.Stem}: no mesh"); continue; }
                    var model = FromRig(root, mine, textures);
                    string file = RemasterPaths.SourceFile(project, fileStem != null ? fileStem(part.Stem) : part.Stem);
                    Glb.Write(model, file);
                    lines.Add($"{file}: {Summary(model)}");
                }
                var all = FromRig(root, renderers, textures);
                string allFile = Path.Combine(RemasterPaths.SourceDir(project), Safe(s.Name) + "__all.glb");
                Glb.Write(all, allFile);
                lines.Add($"{allFile}: {Summary(all)}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
            lines.AddRange(imp.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(10).Select(l => "  " + l));
            return string.Join("\n", lines);
        }

        static string Summary(RigModel m) =>
            $"{m.Nodes.Count} nodes, {m.Meshes.Count} meshes ({string.Join(", ", m.Meshes.Select(x => $"{x.Name} {x.VertexCount} v"))})";

        static bool BelongsTo(Renderer r, NpcPart part)
        {
            if (part.IsMesh) return r is MeshRenderer && r.name.Equals(part.Stem, StringComparison.OrdinalIgnoreCase);
            if (!(r is SkinnedMeshRenderer)) return false;
            return r.name.Equals(part.Stem, StringComparison.OrdinalIgnoreCase) ||
                   Regex.IsMatch(r.name, "^" + Regex.Escape(part.Stem) + @"_\d+$", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// The rig under <paramref name="root"/> (which must be in its bind pose, at the origin) with the given renderers,
        /// in glTF space. Nodes are all transforms except the renderer objects; skinned meshes sit at the scene root.
        /// </summary>
        public static RigModel FromRig(GameObject root, IList<Renderer> renderers, bool textures)
        {
            var model = new RigModel { Generator = "G3World G3RemasterExporter" };
            var index = new Dictionary<Transform, int>();
            void Walk(Transform t, int parent)
            {
                if (t != root.transform && t.GetComponent<Renderer>() != null) return;
                bool isRoot = t == root.transform;
                var p = isRoot ? Vector3.zero : t.localPosition;
                var q = isRoot ? Quaternion.identity : t.localRotation;
                int i = model.AddNode(t.name, parent, new double[] { p.x, p.y, p.z }, new double[] { q.x, q.y, q.z, q.w });
                index[t] = i;
                foreach (Transform c in t) Walk(c, i);
            }
            Walk(root.transform, -1);

            var materials = new Dictionary<string, RigMaterial>();
            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var rm = new RigMesh { Name = r.name };
                var v = mesh.vertices;
                var n = mesh.normals;
                rm.Positions = Flatten(v);
                rm.Normals = n.Length == v.Length ? Flatten(n) : null;
                rm.Uv0 = Flatten(mesh.uv);
                rm.Uv1 = mesh.uv2.Length == v.Length ? Flatten(mesh.uv2) : null;
                if (rm.Uv0.Length == 0) rm.Uv0 = null;
                var mats = r.sharedMaterials;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var mat = s < mats.Length ? mats[s] : null;
                    string mname = mat != null ? mat.name : $"{r.name}_slot{s}";
                    rm.SubMeshes.Add(new RigSubMesh { Material = mname, Indices = mesh.GetTriangles(s) });
                    if (!materials.ContainsKey(mname))
                        materials[mname] = new RigMaterial { Name = mname, BaseColorImage = textures && mat != null ? BaseColorPng(mat) : null };
                }
                if (r is SkinnedMeshRenderer sk)
                {
                    // Unique joints (several part nodes can be bound to the same bone).
                    var joints = new List<int>();
                    var bindIndex = new List<int>();
                    var remap = new int[sk.bones.Length];
                    for (int b = 0; b < sk.bones.Length; b++)
                    {
                        var bone = sk.bones[b];
                        int node = bone != null && index.TryGetValue(bone, out int ni) ? ni : 0;
                        int j = joints.IndexOf(node);
                        if (j < 0) { joints.Add(node); bindIndex.Add(b); j = joints.Count - 1; }
                        remap[b] = j;
                    }
                    var bp = mesh.bindposes;
                    rm.Joints = joints.ToArray();
                    rm.InverseBind = bindIndex.Select(b => ToRows(bp[b])).ToArray();
                    var bw = mesh.boneWeights;
                    rm.JointIndices = new int[bw.Length * 4];
                    rm.Weights = new float[bw.Length * 4];
                    for (int i = 0; i < bw.Length; i++)
                    {
                        var w = bw[i];
                        // Merge influences that map to the same joint.
                        var acc = new Dictionary<int, float>();
                        void Add(int bi, float wt) { if (wt <= 0) return; int j = remap[bi]; acc[j] = (acc.TryGetValue(j, out var o) ? o : 0) + wt; }
                        Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
                        int k = 0;
                        foreach (var kv in acc.OrderByDescending(x => x.Value).Take(4))
                        {
                            rm.JointIndices[i * 4 + k] = kv.Key;
                            rm.Weights[i * 4 + k] = kv.Value;
                            k++;
                        }
                    }
                }
                else
                {
                    // Static mesh: modelled in its parent's frame (the slot); the renderer object itself sits at identity.
                    rm.Node = index.TryGetValue(r.transform.parent, out int pn) ? pn : 0;
                }
                model.Meshes.Add(rm);
            }
            model.Materials.AddRange(materials.Values);
            model.MirrorX(); // Unity → glTF space
            return model;
        }

        static float[] Flatten(Vector3[] a)
        {
            var r = new float[a.Length * 3];
            for (int i = 0; i < a.Length; i++) { r[i * 3] = a[i].x; r[i * 3 + 1] = a[i].y; r[i * 3 + 2] = a[i].z; }
            return r;
        }

        static float[] Flatten(Vector2[] a)
        {
            var r = new float[a.Length * 2];
            for (int i = 0; i < a.Length; i++) { r[i * 2] = a[i].x; r[i * 2 + 1] = a[i].y; }
            return r;
        }

        static double[] ToRows(Matrix4x4 m)
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++) r[i * 4 + j] = m[i, j];
            return r;
        }

        /// <summary>The material's base map as PNG (GPU read-back, works for compressed, non-readable textures).</summary>
        static byte[] BaseColorPng(Material mat)
        {
            var tex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") as Texture2D : mat.mainTexture as Texture2D;
            if (tex == null) return null;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply();
                var png = copy.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(copy);
                return png;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Menu

        [MenuItem("G3/Remaster/Export Hero", priority = 210)]
        static void MenuHero() => Debug.Log(ExportHero());

        [MenuItem("G3/Remaster/Export Selected Character", priority = 211)]
        static void MenuSelected()
        {
            var info = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<G3Character>() : null;
            if (info == null) { Debug.LogWarning("[G3] select a G3 character (prefab or scene object)"); return; }
            Debug.Log(ExportSpawn(SpawnOf(info, Selection.activeGameObject.name)));
        }

        static readonly Regex SwitchSuffix = new Regex(@"_S(\d+)$");

        /// <summary>Parts of an imported character (<see cref="G3Character.Parts"/>: stems with an optional _S&lt;n&gt; switch).</summary>
        public static NpcSpawn SpawnOf(G3Character info, string name)
        {
            var s = new NpcSpawn { Name = name, Enabled = true, World = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } };
            var meshParts = new HashSet<string>(info.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.name), StringComparer.OrdinalIgnoreCase);
            foreach (var p in info.Parts)
            {
                var m = SwitchSuffix.Match(p);
                string stem = m.Success ? p.Substring(0, m.Index) : p;
                int sw = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                bool isMesh = meshParts.Contains(stem);
                s.Parts.Add(new NpcPart { Resource = stem + (isMesh ? ".xcmsh" : ".xact"), Switch = sw, IsMesh = isMesh, EntityName = stem });
            }
            s.Actor = (info.Skeleton ?? s.Parts.FirstOrDefault()?.Stem) + ".xact";
            return s;
        }
    }
}
