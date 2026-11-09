using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Remaster;
using G3.Formats.Trees;
using G3.Unity.Editor.Trees;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity.Editor.Remaster
{
    /// <summary>
    /// World remaster v2 trees (Docs/remaster-world.md 10.15): builds one prefab per generated model from
    /// GameData/Remaster/World/Trees (Tools/Remaster/trees: <c>Models/*.g3tv2</c> + <c>manifest.tsv</c> from
    /// treev2_build.py, card atlases <c>Atlases/&lt;species&gt;_{albedo,normal,mask}.png</c> and impostors
    /// <c>Impostors/&lt;model&gt;_{albedo,normal}.png</c> from treev2_bake.py in Blender, CC0 bark from <c>Source</c>).
    /// Each prefab: LODGroup with LOD0-2 (bark + card sub-meshes) and the 8-frame impostor, G3/SpeedTree materials
    /// (v2 floats: _TreeV2, _ImpostorFrames, _Snow, _MaskMap; GPU instancing / SRP batcher / GPU Resident Drawer like
    /// the G3 trees), no colliders (the G3 root keeps its trunk capsule). Writes <see cref="G3TreeV2Set"/> and links it
    /// into the remaster set. Generated under Assets/G3Imported (git-ignored).
    /// CLI: <c>G3.Unity.Editor.Remaster.G3TreeV2Builder.Build()</c>.
    /// </summary>
    public static class G3TreeV2Builder
    {
        public const string Root = G3WorldRemasterBuilder.Root + "/V2/Trees";
        public const string SetPath = Root + "/G3TreeV2Set.asset";
        const string TextureDir = Root + "/Textures", MaterialDir = Root + "/Materials", MeshDir = Root + "/Meshes", PrefabDir = Root + "/Prefabs";
        const string ShaderName = "G3/SpeedTree";
        const float CardCutoff = 0.4f;
        const int ImpostorFrames = 8;

        static readonly int[] Budget = { 25000, 8000, 2000 };

        static string Project => Directory.GetParent(Application.dataPath)!.FullName;
        static string Source => Path.Combine(WorldRemasterPaths.Root(Project), "Trees");

        [MenuItem("G3/Remaster/World/V2/Build Trees", priority = 233)]
        static void MenuBuild() => Debug.Log(Build());

        /// <summary>Builds or refreshes every v2 tree prefab and the set; returns a summary (details in the log).</summary>
        public static string Build()
        {
            string manifestPath = Path.Combine(Source, "Models", "manifest.tsv");
            if (!File.Exists(manifestPath))
                return $"v2 trees: no {manifestPath} (run python -I Tools/Remaster/trees/treev2_build.py)";
            var shader = Shader.Find(ShaderName);
            if (shader == null) return $"v2 trees: shader {ShaderName} missing";
            var entries = TreeV2ManifestEntry.Parse(File.ReadAllText(manifestPath));
            foreach (var d in new[] { Root, TextureDir, MaterialDir, MeshDir, PrefabDir }) Directory.CreateDirectory(d);

            var set = AssetDatabase.LoadAssetAtPath<G3TreeV2Set>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<G3TreeV2Set>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            var models = new List<G3TreeV2Set.Model>();
            var log = new List<string>();
            var skipped = new List<string>();
            var settings = G3SpeedTreeImporter.Settings;
            var cards = new Dictionary<string, Material>();
            var tints = ReadTints(Path.Combine(Source, "Atlases", "tints.tsv"));
            var barks = new Dictionary<string, Material>();

            foreach (var e in entries)
            {
                try
                {
                    string file = Path.Combine(Source, "Models", e.Model + ".g3tv2");
                    if (!File.Exists(file)) { skipped.Add(e.Model + " (no .g3tv2)"); continue; }
                    TreeV2Model model;
                    using (var fs = File.OpenRead(file)) model = TreeV2Model.Read(fs);
                    for (int k = 0; k < 3 && k < model.Lods.Count; k++)
                        if (model.Lods[k].Triangles > Budget[k]) throw new Exception($"LOD{k} {model.Lods[k].Triangles} triangles > {Budget[k]}");

                    if (!cards.TryGetValue(e.Species, out var cardMat))
                        cards[e.Species] = cardMat = CardMaterial(e, shader, settings);
                    if (cardMat == null) { skipped.Add(e.Model + $" (no card atlas Atlases/{e.Species}_albedo.png: run treev2_bake.py)"); continue; }
                    if (!barks.TryGetValue(e.Species, out var barkMat))
                        barks[e.Species] = barkMat = BarkMaterial(e, shader, settings, tints);
                    if (barkMat == null) { skipped.Add(e.Model + $" (no bark Source/ambientcg_{e.Bark})"); continue; }
                    var impostorMat = ImpostorMaterial(e, shader, settings);

                    var prefab = BuildPrefab(e, model, barkMat, cardMat, impostorMat);
                    models.Add(new G3TreeV2Set.Model { species = e.Species, variant = e.Variant, prefab = prefab, height = model.Height, halfWidth = model.HalfWidth });
                    log.Add($"{e.Model}: {string.Join("/", model.Lods.Select(l => l.Triangles))} tris, h {model.Height:0.0} m, w {2 * model.HalfWidth:0.0} m" +
                            (impostorMat == null ? ", no impostor (LOD2 culls)" : ""));
                }
                catch (Exception ex) { skipped.Add($"{e.Model}: {ex.Message}"); }
            }

            set.models = models;
            EditorUtility.SetDirty(set);
            var remaster = AssetDatabase.LoadAssetAtPath<G3WorldRemasterSet>(G3WorldRemasterBuilder.SetPath);
            if (remaster != null) { remaster.treeV2 = set; EditorUtility.SetDirty(remaster); }
            AssetDatabase.SaveAssets();
            foreach (var l in log) Debug.Log("[G3TreeV2] " + l);
            foreach (var l in skipped) Debug.LogWarning("[G3TreeV2] skipped " + l);
            var (perSpecies, replaced) = TreeV2Plan.Coverage();
            return $"v2 trees: {models.Count} prefabs ({string.Join(", ", models.GroupBy(m => m.species).Select(g => $"{g.Key} {g.Count()}"))}), " +
                   $"{skipped.Count} skipped; replaces {replaced:N0} of {TreeV2Plan.G3TotalPlacements:N0} G3 placements " +
                   $"({100f * replaced / TreeV2Plan.G3TotalPlacements:0}%)" +
                   (remaster == null ? "; no remaster set yet (G3/Remaster/World/Build Overrides) - link it by re-running" : "") + $" -> {Root}";
        }

        // ------------------------------------------------------------------ prefab

        static GameObject BuildPrefab(TreeV2ManifestEntry e, TreeV2Model model, Material bark, Material cards, Material impostor)
        {
            var root = new GameObject(e.Model);
            try
            {
                var lods = new List<LOD>();
                for (int k = 0; k < model.Lods.Count; k++)
                {
                    var lod = model.Lods[k];
                    bool isImpostor = lod.IsImpostor;
                    if (isImpostor && impostor == null) continue;
                    var mesh = SaveMesh(BuildMesh(lod, model, $"{e.Model}_{(isImpostor ? "Impostor" : "LOD" + k)}"),
                                        $"{MeshDir}/{e.Model}_{(isImpostor ? "Impostor" : "LOD" + k)}.asset");
                    var mats = lod.Submeshes.Select(s => s.Material == TreeV2Model.MaterialBark ? bark : s.Material == TreeV2Model.MaterialCards ? cards : impostor).ToArray();
                    var r = AddRenderer(root, isImpostor ? "Impostor" : "LOD" + k, mesh, mats);
                    // impostors cast no shadows: a far billboard's shadow is invisible but its alpha test costs in every cascade
                    if (isImpostor) r.shadowCastingMode = ShadowCastingMode.Off;
                    int slot = Math.Min(lods.Count, TreeV2Plan.LodHeights.Length - 1);
                    lods.Add(new LOD(TreeV2Plan.LodHeights[slot], new Renderer[] { r }));
                }
                var arr = lods.ToArray();
                // without an impostor the last geometry LOD takes the cull height
                if (arr.Length > 0) arr[arr.Length - 1].screenRelativeTransitionHeight = TreeV2Plan.LodHeights[TreeV2Plan.LodHeights.Length - 1];
                for (int k = 0; k < arr.Length; k++) arr[k].fadeTransitionWidth = TreeV2Plan.FadeWidth;
                var group = root.AddComponent<LODGroup>();
                group.fadeMode = LODFadeMode.CrossFade;
                group.animateCrossFading = false;   // dithered by distance: GPU Resident Drawer compatible (see G3SpeedTreeImporter)
                group.SetLODs(arr);
                group.localReferencePoint = new Vector3(0, model.Height * 0.5f, 0);
                group.size = Mathf.Max(model.Height, 2 * model.HalfWidth);
                return PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabDir}/{e.Model}.prefab");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static Mesh BuildMesh(TreeV2Lod lod, TreeV2Model model, string name)
        {
            int n = lod.VertexCount;
            var pos = new Vector3[n]; var nrm = new Vector3[n]; var col = new Color32[n];
            var uv0 = new Vector2[n]; var uv1 = new Vector4[n];
            for (int i = 0; i < n; i++)
            {
                pos[i] = new Vector3(lod.Positions[3 * i], lod.Positions[3 * i + 1], lod.Positions[3 * i + 2]);
                nrm[i] = new Vector3(lod.Normals[3 * i], lod.Normals[3 * i + 1], lod.Normals[3 * i + 2]);
                col[i] = new Color32(lod.Colors[4 * i], lod.Colors[4 * i + 1], lod.Colors[4 * i + 2], lod.Colors[4 * i + 3]);
                uv0[i] = new Vector2(lod.Uv0[2 * i], lod.Uv0[2 * i + 1]);
                uv1[i] = new Vector4(lod.Uv1[4 * i], lod.Uv1[4 * i + 1], lod.Uv1[4 * i + 2], lod.Uv1[4 * i + 3]);
            }
            var mesh = new Mesh { name = name, indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(pos); mesh.SetNormals(nrm); mesh.SetColors(col);
            mesh.SetUVs(0, uv0); mesh.SetUVs(1, uv1);
            mesh.subMeshCount = lod.Submeshes.Count;
            for (int s = 0; s < lod.Submeshes.Count; s++) mesh.SetTriangles(lod.Submeshes[s].Indices, s, false);
            if (!lod.IsImpostor) mesh.RecalculateTangents();
            // bounds: the impostor is a line on the axis in object space (the shader widens it); everything sways in the wind
            float h = model.Height, w = model.HalfWidth * 1.1f;
            mesh.bounds = new Bounds(new Vector3(0, h * 0.5f, 0), new Vector3(2 * w, h * 1.05f, 2 * w));
            mesh.UploadMeshData(false);
            return mesh;
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
            r.lightProbeUsage = LightProbeUsage.Off;          // as the G3 trees: ambient probe, no GRD re-uploads
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            r.allowOcclusionWhenDynamic = true;
            return r;
        }

        static Mesh SaveMesh(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
            EditorUtility.CopySerialized(mesh, existing);
            UnityEngine.Object.DestroyImmediate(mesh);
            existing.MarkModified();
            EditorUtility.SetDirty(existing);
            return existing;
        }

        // ------------------------------------------------------------------ materials

        static Material NewMaterial(string name, Shader shader, G3SpeedTreeSettings set)
        {
            string path = $"{MaterialDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader) mat.shader = shader;
            mat.shaderKeywords = Array.Empty<string>();
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_WindScale", set.windScale);
            mat.SetFloat("_LeafRock", set.leafRock);
            mat.SetFloat("_LeafRockSpeed", set.leafRockSpeed);
            mat.SetFloat("_TreeV2", 1);
            mat.SetFloat("_Snow", 0);
            mat.SetFloat("_UseMaskMap", 0);
            mat.SetFloat("_ImpostorFrames", 0);
            mat.SetFloat("_G3Tree", 0);
            mat.SetFloat("_G3RemasterFoliage", 0);
            mat.SetFloat("_G3RemasterBark", 0);
            mat.SetTexture("_MaskMap", null);
            mat.enableInstancing = true;
            return mat;
        }

        /// <summary>Bark tint per species towards G3's bark colour (treev2_atlas.py assemble).</summary>
        static Dictionary<string, Color> ReadTints(string path)
        {
            var d = new Dictionary<string, Color>();
            if (!File.Exists(path)) return d;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in File.ReadAllLines(path).Skip(1))
            {
                var c = line.Split('	');
                if (c.Length >= 4 && float.TryParse(c[1], System.Globalization.NumberStyles.Float, ci, out float r) &&
                    float.TryParse(c[2], System.Globalization.NumberStyles.Float, ci, out float g) &&
                    float.TryParse(c[3], System.Globalization.NumberStyles.Float, ci, out float b))
                    d[c[0]] = new Color(r, g, b, 1);
            }
            return d;
        }

        static Material BarkMaterial(TreeV2ManifestEntry e, Shader shader, G3SpeedTreeSettings set, Dictionary<string, Color> tints)
        {
            string dir = Path.Combine(Source, "Source", "ambientcg_" + e.Bark);
            string color = FindFile(dir, "_Color.jpg"), normal = FindFile(dir, "_NormalGL.jpg");
            if (color == null) return null;
            var mat = NewMaterial("Bark_" + e.Species, shader, set);
            mat.SetColor("_BaseColor", tints.TryGetValue(e.Species, out var tint) ? tint : Color.white);
            mat.SetTexture("_BaseMap", Import(color, "Bark_" + e.Bark + "_Color", Use.Albedo, wrap: true));
            var n = normal != null ? Import(normal, "Bark_" + e.Bark + "_Normal", Use.Normal, wrap: true) : null;
            mat.SetTexture("_BumpMap", n);
            Keyword(mat, "_NORMALMAP", n != null);
            mat.SetFloat("_BumpScale", 1);
            mat.SetFloat("_Smoothness", set.barkSmoothness);
            mat.SetFloat("_Cull", (float)CullMode.Back);
            mat.SetFloat("_AlphaClip", 0);
            mat.SetFloat("_Snow", e.Snow);
            mat.renderQueue = (int)RenderQueue.Geometry;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material CardMaterial(TreeV2ManifestEntry e, Shader shader, G3SpeedTreeSettings set)
        {
            string dir = Path.Combine(Source, "Atlases");
            string albedo = Path.Combine(dir, e.Species + "_albedo.png");
            if (!File.Exists(albedo)) return null;
            var mat = NewMaterial("Cards_" + e.Species, shader, set);
            mat.SetTexture("_BaseMap", Import(albedo, e.Species + "_albedo", Use.AlbedoAlpha, wrap: false));
            string normal = Path.Combine(dir, e.Species + "_normal.png"), mask = Path.Combine(dir, e.Species + "_mask.png");
            var n = File.Exists(normal) ? Import(normal, e.Species + "_normal", Use.Normal, wrap: false) : null;
            mat.SetTexture("_BumpMap", n);
            Keyword(mat, "_NORMALMAP", n != null);
            mat.SetFloat("_BumpScale", 0.8f);
            if (File.Exists(mask))
            {
                mat.SetTexture("_MaskMap", Import(mask, e.Species + "_mask", Use.Mask, wrap: false));
                mat.SetFloat("_UseMaskMap", 1);
            }
            CardCommon(mat, set);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material ImpostorMaterial(TreeV2ManifestEntry e, Shader shader, G3SpeedTreeSettings set)
        {
            string dir = Path.Combine(Source, "Impostors");
            string albedo = Path.Combine(dir, e.Model + "_albedo.png");
            if (!File.Exists(albedo)) return null;
            var mat = NewMaterial("Impostor_" + e.Model, shader, set);
            mat.SetTexture("_BaseMap", Import(albedo, e.Model + "_impostor_albedo", Use.AlbedoAlpha, wrap: false));
            string normal = Path.Combine(dir, e.Model + "_normal.png");
            var n = File.Exists(normal) ? Import(normal, e.Model + "_impostor_normal", Use.Normal, wrap: false) : null;
            mat.SetTexture("_BumpMap", n);
            Keyword(mat, "_NORMALMAP", n != null);
            mat.SetFloat("_BumpScale", 1);
            CardCommon(mat, set);
            mat.SetFloat("_ImpostorFrames", ImpostorFrames);
            mat.SetFloat("_G3Tree", 2);
            Keyword(mat, "_G3TREE_BILLBOARD", true);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void CardCommon(Material mat, G3SpeedTreeSettings set)
        {
            mat.SetFloat("_Cutoff", CardCutoff);
            mat.SetFloat("_Smoothness", set.leafSmoothness);
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.SetFloat("_AlphaClip", 1);
            Keyword(mat, "_ALPHATEST_ON", true);
            mat.SetFloat("_G3RemasterFoliage", 1);
            Keyword(mat, "_G3REMASTER_FOLIAGE", true);
            mat.SetFloat("_Translucency", 0.45f);
            mat.renderQueue = (int)RenderQueue.AlphaTest;
        }

        static void Keyword(Material m, string k, bool on)
        {
            if (on) m.EnableKeyword(k); else m.DisableKeyword(k);
        }

        static string FindFile(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) : null;

        enum Use { Albedo, AlbedoAlpha, Normal, Mask }

        /// <summary>Copies a generated/CC0 image into the project (re-copied when the source changes) and sets its importer.</summary>
        static Texture2D Import(string src, string name, Use use, bool wrap)
        {
            string path = $"{TextureDir}/{name}{Path.GetExtension(src).ToLowerInvariant()}";
            string stamp = path + ".src";
            string id = $"{File.GetLastWriteTimeUtc(src).Ticks}:{new FileInfo(src).Length}";
            if (!File.Exists(path) || !File.Exists(stamp) || File.ReadAllText(stamp) != id)
            {
                File.Copy(src, path, true);
                File.WriteAllText(stamp, id);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = use == Use.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = use == Use.Albedo || use == Use.AlbedoAlpha;
                ti.alphaSource = use == Use.AlbedoAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                ti.alphaIsTransparency = use == Use.AlbedoAlpha;          // dilated colour under the cut-out edges
                ti.mipMapsPreserveCoverage = use == Use.AlbedoAlpha;      // cards keep their coverage in the distance
                ti.alphaTestReferenceValue = CardCutoff;
                ti.maxTextureSize = 2048;
                ti.anisoLevel = G3AssetCache.WorldAnisoLevel;
                ti.filterMode = FilterMode.Trilinear;
                ti.mipmapEnabled = true;
                ti.streamingMipmaps = wrap;   // tiling bark may stream; card atlases/impostors keep every mip (crisp leaves, runner-32)
                ti.wrapMode = wrap ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;   // BC7 / BC5
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
