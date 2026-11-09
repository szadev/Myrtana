using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Trees
{
    /// <summary>
    /// Sets up the replacement tree models in Assets/G3/Trees/Models/&lt;Name&gt;/&lt;Name&gt;.fbx:
    /// texture import settings, G3/WindLit materials (URP Lit + G3 wind; alpha-clipped two-sided leaves), material remaps,
    /// one prefab per model (Assets/G3/Trees/Prefabs) with a LODGroup, and the TreeCatalog mapping.
    /// Entry point: TreeModelSetup.RunAll().
    /// </summary>
    public static class TreeModelSetup
    {
        public const string ModelsRoot = "Assets/G3/Trees/Models";
        public const string PrefabRoot = "Assets/G3/Trees/Prefabs";
        public const string WindShader = "G3/WindLit";
        static readonly string[] LeafKeys = { "branch", "leaf", "vetka", "needle", "foliage" };

        // LOD screen-relative transition heights (LOD0, LOD1, LOD2 → culled)
        static readonly float[] LodHeights = { 0.25f, 0.08f, 0.004f };

        [MenuItem("G3/Trees/Setup Replacement Tree Models")]
        public static string RunAll()
        {
            var log = new List<string>();
            var dirs = Directory.GetDirectories(ModelsRoot).Select(d => d.Replace('\\', '/')).OrderBy(d => d).ToList();
            foreach (var d in dirs) SetupTextures(d);
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                var fbx = $"{d}/{name}.fbx";
                if (!File.Exists(fbx)) continue;
                log.Add(SetupModel(name, fbx));
            }
            AssetDatabase.SaveAssets();
            log.Add(FillCatalog());
            AssetDatabase.SaveAssets();
            return string.Join("\n", log);
        }

        // conifer textures are rather pale/blue: warm them up a little
        static readonly Dictionary<string, Color> LeafTints = new Dictionary<string, Color>
        {
            { "Spruce", new Color(0.78f, 0.92f, 0.70f) },
            { "Fir", new Color(0.78f, 0.92f, 0.70f) },
        };

        static bool IsLeaf(string matName) => LeafKeys.Any(k => matName.ToLowerInvariant().Contains(k));

        static void SetupTextures(string dir)
        {
            foreach (var png in Directory.GetFiles(dir, "*.png"))
            {
                var p = png.Replace('\\', '/');
                if (!(AssetImporter.GetAtPath(p) is TextureImporter ti)) continue;
                bool normal = p.ToLowerInvariant().Contains("_normal");
                bool changed = false;
                if (normal && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; changed = true; }
                if (!normal)
                {
                    if (!ti.alphaIsTransparency) { ti.alphaIsTransparency = true; changed = true; }
                    if (!ti.mipMapsPreserveCoverage) { ti.mipMapsPreserveCoverage = true; ti.alphaTestReferenceValue = 0.4f; changed = true; }
                }
                if (ti.maxTextureSize != 2048) { ti.maxTextureSize = 2048; changed = true; }
                if (changed) ti.SaveAndReimport();
            }
        }

        static Texture2D FindTex(string dir, string clean, string suffix)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.GetFiles(dir, "*.png"))
            {
                var fn = Path.GetFileNameWithoutExtension(f);
                if (fn.EndsWith(clean + suffix, System.StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(f.Replace('\\', '/'));
            }
            return null;
        }

        static Material MakeMaterial(string path, Texture2D baseMap, Texture2D normal, bool leaf)
        {
            var shader = Shader.Find(WindShader) ?? Shader.Find("Universal Render Pipeline/Lit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.SetTexture("_BaseMap", baseMap);
            var tint = Color.white;
            foreach (var kv in LeafTints)
                if (leaf && !path.Contains("_Snow") && Path.GetFileNameWithoutExtension(path).StartsWith(kv.Key + "_")) tint = kv.Value;
            mat.SetColor("_BaseColor", tint);
            mat.SetFloat("_Smoothness", leaf ? 0.15f : 0.05f);
            mat.SetFloat("_Metallic", 0f);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
            }
            else mat.DisableKeyword("_NORMALMAP");
            mat.SetFloat("_Surface", 0f);
            if (leaf)
            {
                mat.SetFloat("_AlphaClip", 1f);
                mat.SetFloat("_Cutoff", 0.4f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", 0f);          // two-sided leaves
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                mat.SetOverrideTag("RenderType", "TransparentCutout");
            }
            else
            {
                mat.SetFloat("_AlphaClip", 0f);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", 2f);
                mat.renderQueue = -1;
                mat.SetOverrideTag("RenderType", "Opaque");
            }
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static string SetupModel(string name, string fbxPath)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.bakeAxisConversion = false;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.addCollider = false;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importNormals = ModelImporterNormals.Import;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();

            string dir = Path.GetDirectoryName(fbxPath).Replace('\\', '/');
            bool winter = name.EndsWith("_Winter");
            string baseName = winter ? name.Substring(0, name.Length - 7) : name;
            string baseDir = $"{ModelsRoot}/{baseName}";
            string matDir = $"{dir}/Materials";
            if (!AssetDatabase.IsValidFolder(matDir)) AssetDatabase.CreateFolder(dir, "Materials");

            var srcMats = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Material>().Select(m => m.name)
                .Concat(importer.GetExternalObjectMap().Keys.Where(k => k.type == typeof(Material)).Select(k => k.name))
                .Distinct().ToList();
            var info = new List<string>();
            foreach (var mn in srcMats)
            {
                bool snow = mn.EndsWith("_Snow");
                string core = snow ? mn.Substring(0, mn.Length - 5) : mn;
                string clean = core.Replace('.', '_').Replace(' ', '_');
                bool leaf = IsLeaf(core);
                Material mat;
                if (winter && !snow)
                {
                    // reuse the summer material
                    mat = AssetDatabase.LoadAssetAtPath<Material>($"{baseDir}/Materials/{baseName}_{clean}.mat");
                    if (mat == null)
                        mat = MakeMaterial($"{matDir}/{name}_{clean}.mat", FindTex(baseDir, clean, "_baseColor"), FindTex(baseDir, clean, "_normal"), leaf);
                }
                else if (snow)
                    mat = MakeMaterial($"{matDir}/{name}_{clean}_Snow.mat", FindTex(dir, clean, "_baseColor_snow"), FindTex(baseDir, clean, "_normal"), leaf);
                else
                    mat = MakeMaterial($"{matDir}/{name}_{clean}.mat", FindTex(dir, clean, "_baseColor"), FindTex(dir, clean, "_normal"), leaf);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), mn), mat);
                info.Add($"{mn}->{mat.name}{(leaf ? "(leaf)" : "")}{(mat.GetTexture("_BaseMap") == null ? "[NO TEX]" : "")}");
            }
            importer.SaveAndReimport();

            // prefab
            if (!AssetDatabase.IsValidFolder(PrefabRoot)) AssetDatabase.CreateFolder("Assets/G3/Trees", "Prefabs");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.name = "Tree_" + name;
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
            foreach (var r in inst.GetComponentsInChildren<MeshRenderer>())
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
            var renderers = inst.GetComponentsInChildren<Renderer>()
                .Where(r => r.name.Contains("_LOD")).OrderBy(r => r.name).ToList();
            var lg = inst.GetComponent<LODGroup>();
            if (renderers.Count > 1)
            {
                if (lg == null) lg = inst.AddComponent<LODGroup>();
                var lods = new LOD[renderers.Count];
                for (int i = 0; i < renderers.Count; i++)
                {
                    float h = i < LodHeights.Length ? LodHeights[i] : LodHeights[LodHeights.Length - 1] * 0.5f;
                    if (i == renderers.Count - 1) h = LodHeights[LodHeights.Length - 1];
                    lods[i] = new LOD(h, new[] { renderers[i] });
                }
                lg.SetLODs(lods);
                lg.fadeMode = LODFadeMode.None;
                lg.RecalculateBounds();
            }
            else if (lg != null) Object.DestroyImmediate(lg);
            // No BatchingStatic: static batching bakes vertices to world space and the wind shader needs the
            // renderer pivot (tree base) from the model matrix.
            GameObjectUtility.SetStaticEditorFlags(inst, StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ContributeGI);
            foreach (Transform t in inst.transform) GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.OccludeeStatic);
            ApplyWind(inst, name);
            string prefabPath = $"{PrefabRoot}/Tree_{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
            var b = new Bounds(); bool first = true;
            foreach (var r in inst.GetComponentsInChildren<Renderer>()) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
            Object.DestroyImmediate(inst);
            return $"{name}: lods={renderers.Count} bounds min={b.min} max={b.max} | {string.Join(", ", info)}";
        }

        /// <summary>Wind multiplier per model (palms sway more, dense conifers less).</summary>
        static float WindAmount(string model)
        {
            var n = model.Replace("_Winter", "");
            if (n == "Palm" || n == "DatePalm") return 1.3f;
            if (n == "Spruce" || n == "Fir" || n == "Pine") return 0.8f;
            if (n == "Bush") return 1.1f;
            return 1f;
        }

        /// <summary>
        /// Wind setup of one tree instance (prefab contents at the origin, scale 1): every material of every LOD gets
        /// the same _G3WindHeight (model height in object metres), _G3WindAmount and _G3WindFlutter so bark, leaves,
        /// snow layers and all LOD levels bend identically. Also strips BatchingStatic.
        /// </summary>
        static string ApplyWind(GameObject root, string model)
        {
            var rs = root.GetComponentsInChildren<Renderer>();
            float top = 0;
            foreach (var r in rs) top = Mathf.Max(top, r.bounds.max.y - root.transform.position.y);
            if (top <= 0.1f) top = 8f;
            var shader = Shader.Find(WindShader);
            int n = 0;
            foreach (var r in rs)
            {
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                if ((flags & StaticEditorFlags.BatchingStatic) != 0)
                    GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags & ~StaticEditorFlags.BatchingStatic);
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !AssetDatabase.GetAssetPath(m).StartsWith("Assets/G3/Trees/")) continue;
                    if (shader != null && m.shader != shader) m.shader = shader;
                    m.SetFloat("_G3WindHeight", top);
                    m.SetFloat("_G3WindAmount", WindAmount(model));
                    m.SetFloat("_G3WindFlutter", 1f);
                    EditorUtility.SetDirty(m);
                    n++;
                }
            }
            var rf = GameObjectUtility.GetStaticEditorFlags(root);
            if ((rf & StaticEditorFlags.BatchingStatic) != 0)
                GameObjectUtility.SetStaticEditorFlags(root, rf & ~StaticEditorFlags.BatchingStatic);
            return $"{model}: h={top:F2} amount={WindAmount(model)} mats={n}";
        }

        /// <summary>Converts the existing tree prefabs/materials to G3/WindLit without re-running the full setup.</summary>
        [MenuItem("G3/Trees/Apply Wind To Tree Prefabs")]
        public static string ApplyWindToPrefabs()
        {
            var log = new List<string>();
            foreach (var p in Directory.GetFiles(PrefabRoot, "Tree_*.prefab").Select(f => f.Replace('\\', '/')).OrderBy(f => f))
            {
                var root = PrefabUtility.LoadPrefabContents(p);
                try
                {
                    root.transform.position = Vector3.zero;
                    log.Add(ApplyWind(root, Path.GetFileNameWithoutExtension(p).Substring(5)));
                    PrefabUtility.SaveAsPrefabAsset(root, p);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            return string.Join("\n", log);
        }

        static GameObject P(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabRoot}/Tree_{name}.prefab");
        static GameObject[] A(params string[] names) => names.Select(P).Where(p => p != null).ToArray();

        static string FillCatalog()
        {
            var catalog = G3TreeSetup.GetOrCreateCatalog();
            void Def(TreeCategory c, GameObject[] summer, GameObject[] winter)
            {
                var d = catalog.categoryDefaults.Find(x => x.category == c);
                if (d == null) { d = new TreeCatalog.CategoryDefault { category = c }; catalog.categoryDefaults.Add(d); }
                d.prefabs = summer;
                d.winterPrefabs = winter;
            }
            Def(TreeCategory.Conifer, A("Spruce", "Fir"), A("Spruce_Winter", "Fir_Winter"));
            Def(TreeCategory.Broadleaf, A("Oak", "Linden", "Elm"), A("Oak", "Linden", "Elm"));
            Def(TreeCategory.Palm, A("Palm", "DatePalm"), A("Palm", "DatePalm"));
            Def(TreeCategory.Desert, A("Acacia"), A("Acacia"));
            Def(TreeCategory.Bush, A("Bush"), A("Bush_Winter"));

            var species = new (string key, string[] prefabs)[]
            {
                ("DouglasFir", new[] { "Spruce", "Fir" }),
                ("DouglasFir_winter", new[] { "Spruce_Winter", "Fir_Winter" }),
                ("FraserFir", new[] { "Fir" }),
                ("FraserFir_winter", new[] { "Fir_Winter" }),
                ("LongleafPine", new[] { "Pine" }),
                ("MontereyCypress", new[] { "Fir" }),
                ("ItalianCypress", new[] { "Spruce" }),
                ("Bush", new[] { "Bush" }),
                ("Bush_winter", new[] { "Bush_Winter" }),
                ("CarolinaBuckthorn", new[] { "Elm" }),
                ("BulfordHolly", new[] { "Elm" }),
                ("CrepeMyrtle", new[] { "Elm" }),
                ("CrabApple", new[] { "Elm" }),
                ("GSApple", new[] { "Elm" }),
                ("PawPaw", new[] { "Elm" }),
                ("Linden", new[] { "Linden" }),
                ("PinOak", new[] { "Oak" }),
                ("RedOak", new[] { "Oak" }),
                ("Sycamore", new[] { "Oak", "Linden" }),
                ("HoneyLocust", new[] { "Linden" }),
                ("PagodaTree", new[] { "Oak" }),
                ("EuropeanAspen", new[] { "Linden" }),
                ("CoconutPalm", new[] { "Palm" }),
                ("CurlyPalm", new[] { "Palm" }),
                ("FanPalm", new[] { "Palm" }),
                ("DatePalm", new[] { "DatePalm" }),
                ("UmbrellaThorn", new[] { "Acacia" }),
                ("Tamarind", new[] { "Acacia" }),
                ("Gimlet", new[] { "Acacia" }),
                ("GhostGum", new[] { "Acacia" }),
                ("RedGum", new[] { "Acacia" }),
                ("JoshuaTree", new[] { "Acacia" }),
            };
            catalog.entries.RemoveAll(e => species.Any(s => s.key == e.key));
            foreach (var (key, names) in species)
            {
                var arr = A(names);
                if (arr.Length > 0) catalog.entries.Add(new TreeCatalog.Entry { key = key, prefabs = arr });
            }
            EditorUtility.SetDirty(catalog);
            return $"catalog: {catalog.entries.Count} entries, {catalog.categoryDefaults.Count} defaults";
        }
    }
}
