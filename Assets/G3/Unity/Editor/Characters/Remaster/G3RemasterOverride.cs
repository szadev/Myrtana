using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G3.Formats.Animation;
using G3.Formats.Remaster;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// The importer's remaster override (Docs/remaster-pipeline.md): when <see cref="G3CharacterImporter.UseRemaster"/> is
    /// on and <c>GameData/Remaster/Characters/&lt;stem&gt;.glb</c> exists, an actor part (body, head, facial head) or a
    /// static hair/beard mesh takes its geometry from that file instead of the .xact/.xcmsh. Everything else stays the
    /// original's: the skeleton and its bind pose (the file's joints are matched to bones by name, its own bind matrices
    /// only place the vertices), Slot_* nodes, clips, MaterialSwitch variants, face kits. Material slots are matched to
    /// the original materials by name; a material with embedded textures becomes a copy of the original with those maps.
    /// R2 LOD files (<c>Characters/LOD/&lt;stem&gt;_LOD1|2.glb</c>) add renderers <c>&lt;stem&gt;_LOD&lt;n&gt;</c> that
    /// <see cref="G3RemasterLods"/> groups into a LODGroup.
    /// Output (git-ignored): Characters/Meshes/Remaster, Materials/Remaster, Textures/Remaster under G3Imported.
    /// </summary>
    public static class G3RemasterOverride
    {
        public const string MeshDir = G3CharacterImporter.Root + "/Meshes/Remaster";
        public const string MaterialDir = G3CharacterImporter.Root + "/Materials/Remaster";
        public const string TextureDir = G3CharacterImporter.Root + "/Textures/Remaster";

        public static string ProjectDir => Path.GetDirectoryName(Application.dataPath);
        public static string FileFor(string stem) => RemasterPaths.OverrideFile(ProjectDir, stem);
        public static bool Exists(string stem) => File.Exists(FileFor(stem));

        /// <summary>Stems that currently have an override file.</summary>
        public static List<string> Overrides()
        {
            string dir = RemasterPaths.OverrideDir(ProjectDir);
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*" + RemasterPaths.Extension).Select(Path.GetFileNameWithoutExtension).OrderBy(s => s).ToList()
                : new List<string>();
        }

        static readonly Dictionary<string, (DateTime time, RigModel model)> Cache = new Dictionary<string, (DateTime, RigModel)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The override of a part in Unity space (read-only, cached by file time), or null.</summary>
        public static RigModel Load(string stem) => LoadFile(FileFor(stem));

        /// <summary>An R2 LOD (level 1..<see cref="RemasterPaths.LodLevels"/>) of a part's override, or null.</summary>
        public static RigModel LoadLod(string stem, int level) => LoadFile(RemasterPaths.LodFile(ProjectDir, stem, level));

        static RigModel LoadFile(string file)
        {
            if (!File.Exists(file)) return null;
            var time = File.GetLastWriteTimeUtc(file);
            if (Cache.TryGetValue(file, out var c) && c.time == time) return c.model;
            var model = Glb.Read(file);
            model.MirrorX(); // glTF → Unity space
            // fix-52 (E1/E2): FACES heads up to v6 have inside-out eyeballs/cornea and skin triangles inside the eyeballs
            // (black eyes in game); repaired here so no Blender re-batch is needed. RigCheck/validate.py report them.
            var notes = new List<string>();
            foreach (var m in model.Meshes)
            {
                var fix = RemasterEyes.Repair(m);
                if (fix.Changed) notes.Add(fix.ToString());
                var left = RemasterEyes.Check(m);
                if (left.HasEyes && !left.Passed) notes.Add("WARNING eye contract still broken after the repair: " + left);
            }
            RepairNotes[file] = notes;
            Cache[file] = (time, model);
            return model;
        }

        static readonly Dictionary<string, List<string>> RepairNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What <see cref="LoadFile"/> repaired in a part's file (eye contract), for the importer log.</summary>
        static void LogRepairs(G3CharacterImporter imp, string stem, string file)
        {
            if (RepairNotes.TryGetValue(file, out var notes))
                foreach (var n in notes) imp.Log.Add($"{stem}: remaster {Path.GetFileName(file)}: {n}");
        }

        // ---------------------------------------------------------------------------------------------------------
        // Skinned parts

        internal static bool TryAttach(G3CharacterImporter imp, ActorResource res, EmfxActor skel, Transform[] skelXf, ActorPose.Xf[] skelBind,
                                       Transform root, int materialSwitch, IReadOnlyDictionary<string, Transform> addedNodes)
        {
            string stem = Path.GetFileNameWithoutExtension(res.Name);
            // The talking-head rig prefers the override made from its own export (<stem>__talk.glb).
            string file = imp.TalkRig && Exists(RemasterPaths.TalkStem(stem)) ? RemasterPaths.TalkStem(stem) : stem;
            RigModel rig;
            try { rig = Load(file); }
            catch (Exception ex) { imp.Log.Add($"{stem}: remaster file unreadable, original used: {ex.Message}"); return false; }
            if (rig == null) return false;
            LogRepairs(imp, stem, FileFor(file));
            var meshes = rig.Meshes.Where(m => m.IsSkinned).ToList();
            if (meshes.Count == 0) { imp.Log.Add($"{stem}: remaster file has no skinned mesh, original used"); return false; }

            var act = res.Actor;
            var originals = OriginalMaterials(imp, act, stem);
            int meshNo = 0;
            foreach (var m in meshes)
            {
                string meshName = meshNo++ == 0 ? stem : $"{stem}_{meshNo}";
                AttachMesh(imp, rig, m, meshName, stem, act, skel, skelXf, skelBind, root, materialSwitch, addedNodes, originals);
            }
            int lods = 0;
            for (int level = 1; imp.RemasterLods && level <= RemasterPaths.LodLevels; level++)
            {
                RigModel lod;
                try { lod = LoadLod(stem, level); }
                catch (Exception ex) { imp.Log.Add($"{stem}: remaster LOD{level} unreadable, skipped: {ex.Message}"); continue; }
                if (lod == null) continue;
                int no = 0;
                foreach (var m in lod.Meshes.Where(x => x.IsSkinned))
                {
                    // renderer + mesh asset "<part>_LOD<n>": G3RemasterLods groups them, face kits still find LOD0 by name
                    string meshName = (no++ == 0 ? stem : $"{stem}_{no}") + RemasterLods.Suffix + level;
                    AttachMesh(imp, lod, m, meshName, stem, act, skel, skelXf, skelBind, root, materialSwitch, addedNodes, originals);
                }
                lods++;
            }
            imp.Log.Add($"{stem}: remaster {Path.GetFileName(FileFor(stem))} ({meshes.Count} meshes, {meshes.Sum(x => x.VertexCount)} vertices" +
                        (lods > 0 ? $", {lods} LODs" : "") + ")");
            return true;
        }

        /// <summary>One skinned renderer from a remaster mesh, bound to the character's own bones by name.</summary>
        static void AttachMesh(G3CharacterImporter imp, RigModel rig, RigMesh m, string meshName, string stem, EmfxActor act, EmfxActor skel,
                               Transform[] skelXf, ActorPose.Xf[] skelBind, Transform root, int materialSwitch,
                               IReadOnlyDictionary<string, Transform> addedNodes, List<Material> originals)
        {
            rig.RestWorld(m, out var pos, out var nrm); // character root space (the export's scene root)

            var bones = new Transform[m.Joints.Length];
            var bindposes = new Matrix4x4[m.Joints.Length];
            for (int j = 0; j < m.Joints.Length; j++)
            {
                string name = rig.Nodes[m.Joints[j]].Name;
                int an = act.FindNode(name), sn = skel.FindNode(name);
                if (an >= 0) imp.BindBone(act, an, skel, skelXf, skelBind, root, addedNodes, stem, out bones[j], out bindposes[j]);
                else if (sn >= 0) { bones[j] = skelXf[sn]; bindposes[j] = G3CharacterImporter.SkeletonBindpose(skelBind, sn); }
                else if (addedNodes != null && addedNodes.TryGetValue(name, out var added))
                {
                    bones[j] = added;
                    bindposes[j] = (root.worldToLocalMatrix * added.localToWorldMatrix).inverse;
                }
                else
                {
                    imp.Log.Add($"{stem}: remaster joint {name} is not in the rig, bound to {skel.Nodes[0].Name}");
                    bones[j] = skelXf[0];
                    bindposes[j] = G3CharacterImporter.SkeletonBindpose(skelBind, 0);
                }
            }
            var weights = Weights(m);
            var mesh = SaveMesh(meshName, ToVectors(pos), nrm != null ? ToVectors(nrm) : null, m, weights, bindposes, null, rig);
            var mats = m.SubMeshes.Select((s, i) => Material(imp, rig, stem, s.Material, originals, i, materialSwitch)).ToArray();
            G3CharacterImporter.AddSkinnedRenderer(meshName, mesh, bones, mats, skelXf, root);
            if (m.Morphs.Count > 0) imp.Log.Add($"{stem}: remaster {meshName}: {m.Morphs.Count} blend shapes");
        }

        /// <summary>The original actor's materials (before MaterialSwitch), in submesh order.</summary>
        static List<Material> OriginalMaterials(G3CharacterImporter imp, EmfxActor act, string stem)
        {
            var list = new List<Material>();
            foreach (var m in act.Meshes)
            {
                if (m.IsCollisionMesh || m.Indices.Length == 0) continue;
                foreach (var sm in m.SubMeshes)
                {
                    var mat = imp.ActorMaterial(act, sm.MaterialId, stem);
                    if (mat != null && !list.Contains(mat)) list.Add(mat);
                }
            }
            return list;
        }

        /// <summary>Top 4 influences per vertex, renormalised.</summary>
        static BoneWeight[] Weights(RigMesh m)
        {
            var w = new BoneWeight[m.VertexCount];
            for (int v = 0; v < w.Length; v++)
            {
                var inf = Enumerable.Range(0, m.Stride).Select(k => (j: m.JointIndices[v * m.Stride + k], w: m.Weights[v * m.Stride + k]))
                                    .Where(x => x.w > 0).OrderByDescending(x => x.w).Take(4).ToList();
                float sum = inf.Sum(x => x.w);
                var bw = new BoneWeight();
                if (inf.Count == 0) { bw.weight0 = 1; }
                for (int k = 0; k < inf.Count; k++)
                {
                    float x = inf[k].w / sum;
                    switch (k)
                    {
                        case 0: bw.boneIndex0 = inf[k].j; bw.weight0 = x; break;
                        case 1: bw.boneIndex1 = inf[k].j; bw.weight1 = x; break;
                        case 2: bw.boneIndex2 = inf[k].j; bw.weight2 = x; break;
                        default: bw.boneIndex3 = inf[k].j; bw.weight3 = x; break;
                    }
                }
                w[v] = bw;
            }
            return w;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Static parts (hair, beard on a head slot)

        /// <param name="materialSwitch">The part's MaterialSwitch: picks the per-switch set of generated hair materials
        /// (hairmesh.py <c>…_S&lt;n&gt;_HairCards/Cap</c>; fix-52: switch 0 was passed, so only the S1 materials were
        /// ever rebuilt). The original materials stay unswitched: the importer applies their variant.</param>
        internal static bool TryStatic(G3CharacterImporter imp, string stem, Transform slot, Transform root, Material[] originals,
                                       int materialSwitch, out Mesh mesh, out Material[] mats) =>
            TryStaticFile(imp, FileFor(stem), stem, slot, root, originals, materialSwitch, out mesh, out mats);

        /// <summary>
        /// FACES hair cards (<see cref="RemasterHair"/>) for a head part whose remaster override is in use: a static mesh on
        /// the head's Slot_Hair (<c>Characters/Hair/&lt;static head&gt;__hair.glb</c>, materials generated:
        /// G3/Remaster/Hair cards + cap). Returns the new renderer's object or null.
        /// </summary>
        internal static GameObject TryHair(G3CharacterImporter imp, string partStem, Transform slot, Transform root)
        {
            string file = RemasterHair.File(ProjectDir, partStem);
            if (slot == null || !File.Exists(file)) return null;
            string stem = RemasterHair.MeshStem(partStem);
            if (!TryStaticFile(imp, file, stem, slot, root, null, 0, out var mesh, out var mats)) return null;
            var go = new GameObject(stem);
            go.transform.SetParent(slot, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return go;
        }

        static bool TryStaticFile(G3CharacterImporter imp, string file, string stem, Transform slot, Transform root, Material[] originals,
                                  int generatedSwitch, out Mesh mesh, out Material[] mats)
        {
            mesh = null;
            mats = null;
            RigModel rig;
            try { rig = LoadFile(file); }
            catch (Exception ex) { imp.Log.Add($"{stem}: remaster file unreadable, original used: {ex.Message}"); return false; }
            if (rig == null || rig.Meshes.Count == 0) return false;
            LogRepairs(imp, stem, file);
            var m = rig.Meshes[0];
            rig.RestWorld(m, out var pos, out var nrm);
            // Root space (the rig is still in its bind pose here) → the slot's frame the mesh is modelled in.
            var toSlot = slot.worldToLocalMatrix * root.localToWorldMatrix;
            var v = ToVectors(pos).Select(p => toSlot.MultiplyPoint3x4(p)).ToArray();
            var n = nrm != null ? ToVectors(nrm).Select(d => toSlot.MultiplyVector(d).normalized).ToArray() : null;
            mesh = SaveMesh(stem, v, n, m, null, null, null);
            var orig = originals?.Where(x => x != null).ToList() ?? new List<Material>();
            mats = m.SubMeshes.Select((s, i) => Material(imp, rig, stem, s.Material, orig, i, 0, generatedSwitch)).ToArray();
            imp.Log.Add($"{stem}: remaster {Path.GetFileName(file)} ({m.VertexCount} vertices)");
            return true;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Assets

        static Vector3[] ToVectors(float[] f)
        {
            var r = new Vector3[f.Length / 3];
            for (int i = 0; i < r.Length; i++) r[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
            return r;
        }

        static Vector2[] ToUv(float[] f)
        {
            if (f == null) return null;
            var r = new Vector2[f.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = new Vector2(f[i * 2], f[i * 2 + 1]);
            return r;
        }

        static Mesh SaveMesh(string name, Vector3[] pos, Vector3[] nrm, RigMesh src, BoneWeight[] weights, Matrix4x4[] bindposes,
                             IList<int[]> indices, RigModel morphRig = null)
        {
            Directory.CreateDirectory(MeshDir);
            string path = $"{MeshDir}/{Safe(name)}.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool created = mesh == null;
            mesh ??= new Mesh();
            mesh.Clear();
            mesh.name = name; // same name as the original part: face kits and head lookups go by mesh name
            mesh.indexFormat = pos.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = pos;
            if (nrm != null) mesh.normals = nrm;
            var uv0 = ToUv(src.Uv0);
            if (uv0 != null) mesh.uv = uv0;
            var uv1 = ToUv(src.Uv1);
            if (uv1 != null) mesh.uv2 = uv1;
            mesh.subMeshCount = src.SubMeshes.Count;
            for (int s = 0; s < src.SubMeshes.Count; s++) mesh.SetTriangles(indices != null ? indices[s] : src.SubMeshes[s].Indices, s, false);
            if (nrm == null) mesh.RecalculateNormals();
            if (weights != null) { mesh.boneWeights = weights; mesh.bindposes = bindposes; }
            // morph targets (MetaHuman hero head: ARKit shapes, Docs/remaster-metahuman.md §5), in the same rest space as pos
            mesh.ClearBlendShapes();
            if (morphRig != null && indices == null)
                foreach (var mo in src.Morphs)
                {
                    var dp = morphRig.RestWorldDeltas(src, mo.Positions);
                    var dn = morphRig.RestWorldDeltas(src, mo.Normals);
                    if (dp == null || dp.Length != pos.Length * 3) continue;
                    mesh.AddBlendShapeFrame(mo.Name, 100f, ToVectors(dp), dn != null ? ToVectors(dn) : null, null);
                }
            mesh.RecalculateBounds();
            if (uv0 != null) mesh.RecalculateTangents();
            if (created) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            return mesh;
        }

        static readonly Regex Suffix = new Regex(@"(\.\d{3}|_S\d+)+$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Material of a remaster slot: the original material of the same name (Blender's .001 and the _S&lt;n&gt; variant
        /// suffix ignored), else the original slot at the same index; with embedded textures, a copy carrying them.
        /// </summary>
        static Material Material(G3CharacterImporter imp, RigModel rig, string stem, string slotName, List<Material> originals, int index, int materialSwitch,
                                 int generatedSwitch = -1)
        {
            // FACES: generated eyes, cornea shell and hair have no original; their own shaders (an original at the same
            // index would put the G3 atlas on the new eyeball and an opaque material on the clear cornea)
            if (RemasterGenerated.IsGenerated(slotName))
            {
                var gen = G3RemasterMaterials.Generated(imp, slotName, generatedSwitch >= 0 ? generatedSwitch : materialSwitch);
                if (gen != null) return gen;
            }
            string key = Suffix.Replace(slotName ?? "", "");
            var baseMat = originals.FirstOrDefault(o => string.Equals(Suffix.Replace(o.name, ""), key, StringComparison.OrdinalIgnoreCase))
                          ?? (originals.Count > 0 ? originals[Math.Min(index, originals.Count - 1)] : null);
            var rm = rig.Materials.FirstOrDefault(x => x.Name == slotName);
            if (rm == null || (rm.BaseColorImage == null && rm.NormalImage == null) || baseMat == null)
                return imp.Variant(baseMat, materialSwitch);

            Directory.CreateDirectory(MaterialDir);
            string path = $"{MaterialDir}/{Safe(stem)}_{Safe(key)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var copy = new Material(baseMat) { name = $"{key}_Remaster" };
            if (rm.BaseColorImage != null && copy.HasProperty("_BaseMap"))
                copy.SetTexture("_BaseMap", SaveTexture($"{stem}_{key}_base", rm.BaseColorImage, rm.BaseColorMime, false));
            if (rm.NormalImage != null && copy.HasProperty("_BumpMap"))
            {
                copy.SetTexture("_BumpMap", SaveTexture($"{stem}_{key}_normal", rm.NormalImage, rm.NormalMime, true));
                copy.EnableKeyword("_NORMALMAP");
            }
            if (mat == null) { AssetDatabase.CreateAsset(copy, path); return copy; }
            mat.shader = copy.shader;
            mat.CopyPropertiesFromMaterial(copy);
            mat.shaderKeywords = copy.shaderKeywords;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Texture2D SaveTexture(string name, byte[] data, string mime, bool normal)
        {
            Directory.CreateDirectory(TextureDir);
            string path = $"{TextureDir}/{Safe(name)}{(mime == "image/jpeg" ? ".jpg" : ".png")}";
            var old = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (old == null || !old.SequenceEqual(data))
            {
                File.WriteAllBytes(path, data);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                if (ti.textureType != type || ti.sRGBTexture == normal)
                {
                    ti.textureType = type;
                    ti.sRGBTexture = !normal;
                    ti.anisoLevel = 4;
                    ti.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace('|', '_');
        }
    }
}
