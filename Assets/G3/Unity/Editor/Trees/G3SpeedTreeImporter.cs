using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.Trees;
using G3.Formats.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace G3.Unity.Editor.Trees
{
    /// <summary>
    /// The original Gothic 3 trees: prefabs built from the meshes SpeedTreeRT 4.0 generates for the installed game
    /// (Tools/SptBake → .g3tree, Docs/formats-speedtree.md §8). Per tree: one prefab with a LODGroup (the engine's
    /// discrete branch / frond / leaf levels between LodNear and LodFar × tree height, then the original billboard), the
    /// trunk capsule as collider, and shared G3/SpeedTree materials per bark texture / composite atlas.
    /// <para>World import: <see cref="Instantiate"/> is called for every eCSpeedTree_PS entity; it returns null (→ the
    /// replacement TreeCatalog) when original trees are switched off or a tree cannot be generated.</para>
    /// Output: Assets/G3Imported/SpeedTrees/{Meshes,Materials,Prefabs} (generated, git-ignored).
    /// </summary>
    public static class G3SpeedTreeImporter
    {
        public const string Root = G3AssetCache.Root + "/SpeedTrees";
        const string MeshDir = Root + "/Meshes", MaterialDir = Root + "/Materials", PrefabDir = Root + "/Prefabs";
        const string ShaderName = "G3/SpeedTree";
        const float S = G3AssetCache.Scale;

        static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static G3SpeedTreeSettings _settings;

        // ------------------------------------------------------------------ settings / menu

        public static G3SpeedTreeSettings Settings
        {
            get
            {
                if (_settings != null) return _settings;
                _settings = AssetDatabase.LoadAssetAtPath<G3SpeedTreeSettings>(G3SpeedTreeSettings.AssetPath);
                if (_settings == null)
                {
                    _settings = ScriptableObject.CreateInstance<G3SpeedTreeSettings>();
                    Directory.CreateDirectory(Path.GetDirectoryName(G3SpeedTreeSettings.AssetPath));
                    AssetDatabase.CreateAsset(_settings, G3SpeedTreeSettings.AssetPath);
                    AssetDatabase.SaveAssets();
                }
                return _settings;
            }
        }

        public static bool Enabled => Settings.useOriginalTrees;

        const string MenuUse = "G3/Trees/Use Original SpeedTrees (else replacements)";

        [MenuItem(MenuUse, priority = 200)]
        static void MenuToggle()
        {
            Settings.useOriginalTrees = !Settings.useOriginalTrees;
            EditorUtility.SetDirty(Settings);
            AssetDatabase.SaveAssetIfDirty(Settings);
            Debug.Log($"G3 trees: {(Settings.useOriginalTrees ? "original SpeedTrees" : "replacement catalog")} for the next world import");
        }

        [MenuItem(MenuUse, true)]
        static bool MenuToggleValidate()
        {
            Menu.SetChecked(MenuUse, Settings.useOriginalTrees);
            return true;
        }

        [MenuItem("G3/Trees/Rebuild Original SpeedTree Prefabs", priority = 201)]
        static void MenuRebuild() => Debug.Log(RebuildAll(rebake: true));

        /// <summary>Regenerates the .g3tree files (optional) and every prefab, mesh and material. Existing cell scenes
        /// keep working: prefab assets are overwritten in place (same GUIDs).</summary>
        public static string RebuildAll(bool rebake)
        {
            var sw = Stopwatch.StartNew();
            if (rebake) RunBaker();
            Prefabs.Clear();
            Failed.Clear();
            int ok = 0;
            var log = new List<string>();
            using (var ctx = new BuildContext())
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var file in Directory.GetFiles(BakeDir, "*.g3tree").OrderBy(f => f))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        try
                        {
                            // SaveAsPrefabAsset returns null for new prefabs while asset editing is paused, so count
                            // completed builds rather than returned assets.
                            Build(name, ctx);
                            ok++;
                        }
                        catch (Exception ex) { log.Add($"{name}: {ex.Message}"); }
                    }
                }
                finally { AssetDatabase.StopAssetEditing(); }
                log.AddRange(ctx.Cache.Warnings);
            }
            AssetDatabase.SaveAssets();
            Prefabs.Clear();   // reload the imported assets on next use
            return $"original SpeedTrees: {ok} prefabs in {sw.Elapsed.TotalSeconds:0.0}s" + (log.Count > 0 ? "\n" + string.Join("\n", log) : "");
        }

        // ------------------------------------------------------------------ world import hook

        /// <summary>
        /// Places the original tree of a SpeedTree entity under <paramref name="parent"/> with the entity's world matrix,
        /// or returns null when original trees are off or this tree cannot be generated (caller falls back to the
        /// replacement catalog).
        /// </summary>
        public static GameObject Instantiate(WorldEntity e, Transform parent)
        {
            if (!Enabled || string.IsNullOrEmpty(e.Resource)) return null;
            var prefab = GetPrefab(Path.GetFileNameWithoutExtension(e.Resource));
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = prefab.name;
            G3WorldImporter.ApplyMatrix(go.transform, e.World);
            return go;
        }

        /// <summary>Prefab of an original tree (built on first use); null if it cannot be generated.</summary>
        public static GameObject GetPrefab(string sptName)
        {
            if (Prefabs.TryGetValue(sptName, out var p) && p != null) return p;
            if (Failed.Contains(sptName)) return null;
            p = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}/{sptName}.prefab");
            if (p == null)
            {
                try
                {
                    if (!File.Exists(BakeFile(sptName))) RunBaker();
                    using (var ctx = new BuildContext()) p = Build(sptName, ctx);
                }
                catch (Exception ex) { Debug.LogWarning($"original SpeedTree {sptName}: {ex.Message}"); p = null; }
            }
            if (p == null) Failed.Add(sptName);
            else Prefabs[sptName] = p;
            return p;
        }

        // ------------------------------------------------------------------ generator

        static string ProjectDir => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string BakeDir => Path.GetFullPath(Path.Combine(ProjectDir, Settings.bakeDirectory));
        static string BakeFile(string name) => Path.Combine(BakeDir, name + ".g3tree");

        /// <summary>Runs Tools/SptBake (building it first if needed) for every tree of the installed game.</summary>
        public static void RunBaker()
        {
            string exe = Path.GetFullPath(Path.Combine(ProjectDir, Settings.bakerExecutable));
            if (!File.Exists(exe))
            {
                string proj = Path.Combine(ProjectDir, "Tools/SptBake");
                Run("dotnet", "build -c Release", proj, 300);
                if (!File.Exists(exe)) throw new FileNotFoundException("SptBake not built (dotnet build -c Release in Tools/SptBake)", exe);
            }
            string game = Path.GetDirectoryName(Path.GetFullPath(G3WorldImporter.DataDir.TrimEnd('\\', '/')));
            Run(exe, $"--game \"{game}\" --out \"{BakeDir}\"", Path.GetDirectoryName(exe), 300);
        }

        static void Run(string file, string args, string workDir, int timeoutSeconds)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            var err = proc.StandardError.ReadToEndAsync();
            var output = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutSeconds * 1000)) { proc.Kill(); throw new TimeoutException(file + " timed out"); }
            proc.WaitForExit();
            if (proc.ExitCode != 0) throw new Exception($"{Path.GetFileName(file)} failed ({proc.ExitCode}): {err.Result}{output.Result}");
        }

        // ------------------------------------------------------------------ build

        /// <summary>Pak access + texture cache for one batch of builds.</summary>
        sealed class BuildContext : IDisposable
        {
            public readonly G3AssetCache Cache = new G3AssetCache(G3WorldImporter.DataDir, new UrpMaterialFactory());
            readonly G3FileSystem _trees = new G3FileSystem(G3WorldImporter.DataDir, "Speedtrees");
            readonly Dictionary<string, SpeedTreeDef> _defs = new Dictionary<string, SpeedTreeDef>(StringComparer.OrdinalIgnoreCase);

            public SpeedTreeDef Def(string name)
            {
                if (_defs.TryGetValue(name, out var d)) return d;
                var e = _trees.Files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
                d = e != null ? Spt.Read(e.Read(), name) : null;
                return _defs[name] = d;
            }

            public void Dispose() { Cache.Dispose(); _trees.Dispose(); }
        }

        static GameObject Build(string name, BuildContext ctx)
        {
            string file = BakeFile(name);
            if (!File.Exists(file)) return null;
            var tree = BakedTreeIO.Read(file);
            var def = ctx.Def(name);
            if (def == null) throw new Exception("no .spt in Speedtrees.pak");
            var set = Settings;
            foreach (var d in new[] { Root, MeshDir, MaterialDir, PrefabDir }) Directory.CreateDirectory(d);

            // Materials (shared per texture).
            string bark = Spt.ImageResourceName(def.BranchTexture);
            string composite = Spt.ImageResourceName(def.CompositeMap);
            var barkMat = GetMaterial("Bark_" + bark, bark, ctx, Mode.Bark);
            Material frondMat = null, leafMat = null, billboardMat = null;
            if (!string.IsNullOrEmpty(composite))
            {
                frondMat = GetMaterial("Frond_" + composite, composite, ctx, Mode.Frond);
                leafMat = GetMaterial("Leaf_" + composite, composite, ctx, Mode.Leaf);
                billboardMat = GetMaterial("Billboard_" + composite, composite, ctx, Mode.Billboard);
            }

            // Combined LOD levels (distances and screen heights: TreeLodPlan).
            var levels = Levels(tree, set.maxGeometryLevels);
            float heightM = tree.Height * S;

            var root = new GameObject(name);
            try
            {
                var lods = new List<LOD>();
                var starts = new List<float>();
                var parts = new List<G3SpeedTreeMeshBuilder.Part>();
                for (int j = 0; j < levels.Count; j++)
                {
                    var (b, f, l, t) = levels[j];
                    var mesh = G3SpeedTreeMeshBuilder.BuildLevel($"{name}_LOD{j}", b, f, l, parts);
                    if (mesh == null) continue;
                    mesh = SaveMesh(mesh, $"{MeshDir}/{name}_LOD{j}.asset");
                    var mats = parts.Select(p => p == G3SpeedTreeMeshBuilder.Part.Bark ? barkMat : p == G3SpeedTreeMeshBuilder.Part.Frond ? frondMat : leafMat).ToArray();
                    var r = AddRenderer(root, $"LOD{j}", mesh, mats);
                    lods.Add(new LOD(0, new Renderer[] { r }));
                    starts.Add(starts.Count == 0 ? 0f : t);
                }
                if (lods.Count == 0) throw new Exception("no geometry");

                bool hasBillboard = false;
                if (set.billboards && billboardMat != null)
                {
                    var mesh = G3SpeedTreeMeshBuilder.BuildBillboard($"{name}_Billboard", tree.Billboard);
                    if (mesh != null)
                    {
                        mesh = SaveMesh(mesh, $"{MeshDir}/{name}_Billboard.asset");
                        var r = AddRenderer(root, "Billboard", mesh, new[] { billboardMat });
                        lods.Add(new LOD(0, new Renderer[] { r }));
                        hasBillboard = true;
                    }
                }

                var plan = TreeLodPlan.Build(starts, hasBillboard, heightM, tree.LodNear * S, tree.LodFar * S, PlanSettings(set));
                var arr = lods.ToArray();
                for (int j = 0; j < arr.Length; j++)
                {
                    arr[j].screenRelativeTransitionHeight = plan.Heights[j];
                    arr[j].fadeTransitionWidth = set.crossFade ? plan.FadeWidths[j] : 0f;
                }
                var group = root.AddComponent<LODGroup>();
                group.fadeMode = set.crossFade ? LODFadeMode.CrossFade : LODFadeMode.None;
                // Dithered by distance (fadeTransitionWidth), never time-animated: the GPU Resident Drawer excludes
                // LODGroups with animateCrossFading, and every tree renderer then costs one draw per sub-mesh and pass
                // (24.9k draws in Faring, Docs/performance.md).
                group.animateCrossFading = false;
                group.SetLODs(arr);
                group.localReferencePoint = new Vector3(0, heightM * 0.5f, 0);
                group.size = Mathf.Max(heightM, 0.5f);

                AddCollision(root, tree);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabDir}/{name}.prefab");
                Prefabs[name] = prefab;
                return prefab;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static TreeLodPlan.Settings PlanSettings(G3SpeedTreeSettings set) => new TreeLodPlan.Settings
        {
            LodDistanceScale = set.lodDistanceScale,
            MinFirstTransition = set.minFirstTransition,
            MinLodStep = set.minLodStep,
            MinBillboardDistance = set.minBillboardDistance,
            CullScreenHeight = set.cullScreenHeight,
            ReferenceFieldOfView = set.referenceFieldOfView,
            CrossFadeWidth = set.crossFadeWidth,
        };

        /// <summary>
        /// Combined levels: SpeedTree picks each part's discrete LOD from one LOD value v (1 near → 0 far) as
        /// floor((1 − v) · count). A new combined level starts wherever any part changes (identical neighbours merged);
        /// t = 1 − v at which the level starts.
        /// </summary>
        static List<(BakedMeshLod b, BakedMeshLod f, BakedLeafLod l, float t)> Levels(BakedTree tree, int maxLevels)
        {
            var cuts = new SortedSet<float> { 0f };
            void Cuts<T>(List<T> lods, Func<T, T, bool> same)
            {
                for (int k = 1; k < lods.Count; k++)
                    if (!same(lods[k], lods[k - 1])) cuts.Add((float)Math.Round((double)k / lods.Count, 4));
            }
            Cuts(tree.Branches, (a, b) => a.Triangles.Length == b.Triangles.Length && a.VertexCount == b.VertexCount);
            Cuts(tree.Fronds, (a, b) => a.Triangles.Length == b.Triangles.Length && a.VertexCount == b.VertexCount);
            Cuts(tree.Leaves, (a, b) => a.Count == b.Count);

            T At<T>(List<T> lods, float t) where T : class =>
                lods.Count == 0 ? null : lods[Mathf.Clamp(Mathf.FloorToInt(t * lods.Count + 1e-3f), 0, lods.Count - 1)];

            var all = cuts.ToList();
            if (all.Count > maxLevels)
            {
                // keep the full-detail level and an even spread of the rest
                var keep = new List<float> { 0f };
                for (int i = 1; i < maxLevels; i++) keep.Add(all[Mathf.RoundToInt(i * (all.Count - 1f) / (maxLevels - 1f))]);
                all = keep.Distinct().ToList();
            }
            return all.Select(t => (At(tree.Branches, t), At(tree.Fronds, t), At(tree.Leaves, t), t)).ToList();
        }

        static MeshRenderer AddRenderer(GameObject root, string name, Mesh mesh, Material[] mats)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            // No light probes in the world: Off reads the ambient probe directly and keeps the GPU Resident Drawer from
            // re-uploading per-instance probe data whenever the ambient changes (Docs/performance.md).
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            r.allowOcclusionWhenDynamic = true;
            return r;
        }

        /// <summary>
        /// Trunk capsule exactly like eCWraper_SpeedTreeRT::CreateCollisionShape: radius max(d0, d2), total height d1,
        /// segment starting at the given position. Spheres and boxes are crown shapes in a non-blocking shape group
        /// (17) and are not added.
        /// </summary>
        static void AddCollision(GameObject root, BakedTree tree)
        {
            var c = tree.Collision.FirstOrDefault(x => x.Type == BakedCollision.Capsule);
            if (c == null) return;
            float r = Mathf.Max(c.Dimensions[0], c.Dimensions[2]);
            float total = c.Dimensions[1];
            if (r <= 0 || total <= 0) return;
            float segment = Mathf.Max(total - 2 * r, 0.0001f);
            var cap = root.AddComponent<CapsuleCollider>();
            cap.direction = 1;
            cap.radius = r * S;
            cap.height = (segment + 2 * r) * S;
            cap.center = new Vector3(c.Position[0], c.Position[1] + segment * 0.5f, c.Position[2]) * S;
        }

        static Mesh SaveMesh(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            // Overwrite in place so references (GUID + file id) stay valid.
            EditorUtility.CopySerialized(mesh, existing);
            UnityEngine.Object.DestroyImmediate(mesh);
            existing.MarkModified();   // CopySerialized does not re-upload the GPU copy: open scenes kept the old geometry
            EditorUtility.SetDirty(existing);
            return existing;
        }

        // ------------------------------------------------------------------ materials

        enum Mode { Bark, Frond, Leaf, Billboard }

        static Material GetMaterial(string name, string image, BuildContext ctx, Mode mode)
        {
            string path = $"{MaterialDir}/{Safe(name)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new Exception($"shader {ShaderName} not found");
            bool create = mat == null;
            if (create) mat = new Material(shader) { name = name };
            else if (mat.shader != shader) mat.shader = shader;
            var set = Settings;

            var tex = ctx.Cache.GetImageTexture(image);
            if (tex == null) throw new Exception($"texture {image} not found");
            mat.SetTexture("_BaseMap", tex);
            mat.SetFloat("_Cutoff", set.alphaCutoff);
            mat.SetFloat("_WindScale", set.windScale);
            mat.SetFloat("_LeafRock", set.leafRock);
            mat.SetFloat("_LeafRockSpeed", set.leafRockSpeed);
            mat.SetFloat("_Smoothness", mode == Mode.Bark ? set.barkSmoothness : set.leafSmoothness);
            mat.SetFloat("_Cull", (float)(mode == Mode.Bark ? CullMode.Back : CullMode.Off));
            SetKeyword(mat, "_ALPHATEST_ON", mode != Mode.Bark);
            mat.SetFloat("_AlphaClip", mode != Mode.Bark ? 1 : 0);
            mat.SetFloat("_G3Tree", mode == Mode.Leaf ? 1 : mode == Mode.Billboard ? 2 : 0);
            SetKeyword(mat, "_G3TREE_LEAF", mode == Mode.Leaf);
            SetKeyword(mat, "_G3TREE_BILLBOARD", mode == Mode.Billboard);
            Texture2D normal = null;
            if (mode == Mode.Bark && image.Contains("_Diffuse_"))
                normal = ctx.Cache.GetImageTexture(image.Replace("_Diffuse_", "_Normal_"), normalMap: true, warn: false);
            mat.SetTexture("_BumpMap", normal);
            SetKeyword(mat, "_NORMALMAP", normal != null);
            mat.renderQueue = mode == Mode.Bark ? (int)RenderQueue.Geometry : (int)RenderQueue.AlphaTest;
            mat.enableInstancing = true;

            if (create) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        static void SetKeyword(Material m, string k, bool on)
        {
            if (on) m.EnableKeyword(k); else m.DisableKeyword(k);
        }

        static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
