using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Remaster;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Remaster
{
    /// <summary>
    /// Builds the world remaster override (Docs/remaster-world.md §4) from <c>GameData/Remaster/World/</c>: for every world
    /// material asset (G3 materials, vegetation, SpeedTrees) whose images have replacement maps, a remaster copy in
    /// <see cref="MaterialDir"/> (shader per <see cref="WorldRemasterPlan"/>), the maps imported into <see cref="TextureDir"/>
    /// (rows flipped to the raw-DDS orientation of the originals, BC7/BC5, trilinear, aniso 16), and the pair list
    /// <see cref="SetPath"/> that <see cref="G3WorldRemaster"/> loads at runtime. Originals are never edited; rerunning
    /// updates the copies in place (GUIDs kept). CLI: <c>G3.Unity.Editor.Remaster.G3WorldRemasterBuilder.Build()</c>.
    /// </summary>
    public static class G3WorldRemasterBuilder
    {
        public const string Root = G3AssetCache.Root + "/Remaster/World";
        public const string TextureDir = Root + "/Textures";
        public const string MaterialDir = Root + "/Materials";
        public const string SetPath = Root + "/Resources/" + G3WorldRemasterSet.ResourceName + ".asset";

        /// <summary>Material folders scanned (generated, git-ignored).</summary>
        static readonly string[] Sources =
        {
            G3AssetCache.Root + "/Materials",
            "Assets/G3Imported/Vegetation/Materials",
            G3AssetCache.Root + "/SpeedTrees/Materials",
        };

        enum MapUse { Base, Normal, Mask }

        static string Project => Directory.GetParent(Application.dataPath)!.FullName;

        [MenuItem("G3/Remaster/World/Build Overrides", priority = 220)]
        static void MenuBuild() => Debug.Log(Build());

        [MenuItem("G3/Remaster/World/Use World Remaster (play mode A-B)", priority = 221)]
        static void MenuToggle()
        {
            bool on = RemasterOptions.MasterTarget(G3WorldRemaster.Parts);   // like F7: all world parts at once
            int n = G3WorldRemaster.SetAll(on, persist: true);
            Debug.Log($"[G3WorldRemaster] all world parts {(on ? "on" : "off")}{(Application.isPlaying ? $", {n} slots swapped" : " (applies in play mode)")}");
        }

        [MenuItem("G3/Remaster/World/Use World Remaster (play mode A-B)", true)]
        static bool MenuToggleValidate()
        {
            Menu.SetChecked("G3/Remaster/World/Use World Remaster (play mode A-B)", G3WorldRemaster.Enabled);
            return true;
        }

        /// <summary>Builds or refreshes all remaster materials; returns a one-line summary (per-material lines in the log).</summary>
        public static string Build(string filter = null)
        {
            string project = Project;
            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath)!);
            var set = AssetDatabase.LoadAssetAtPath<G3WorldRemasterSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<G3WorldRemasterSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            var pairs = new List<G3WorldRemasterSet.Pair>();
            int blend = 0, textures = 0, scanned = 0;
            var log = new List<string>();
            var inventory = G3WorldV2Builder.ReadInventory();   // categories → world part per pair (fix-71)
            var perPart = new Dictionary<WorldParts, int>();
            var folders = Sources.Where(AssetDatabase.IsValidFolder).ToArray();
            foreach (var guid in AssetDatabase.FindAssets("t:Material", folders))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) continue;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                if (filter != null && mat.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                scanned++;
                var info = Describe(mat);
                string recipeFile = WorldRemasterPaths.RecipeFile(project, mat.name);
                var plan = WorldRemasterPlan.For(info, stem => WorldRemasterPaths.FindTextures(project, stem),
                    File.Exists(recipeFile) ? File.ReadAllText(recipeFile) : null);
                if (plan.Mode == WorldRemasterMode.Keep) continue;
                var copy = BuildMaterial(mat, info, plan);
                if (copy == null) continue;
                var detail = WorldRemasterPlan.Detail(info, plan);
                ApplyDetail(copy, detail, project);
                inventory.TryGetValue(mat.name, out var row);
                var part = WorldRemasterParts.Classify(row.category, info.Shader, mat.name);
                perPart[part] = perPart.TryGetValue(part, out int k) ? k + 1 : 1;
                pairs.Add(new G3WorldRemasterSet.Pair { original = mat, remaster = copy, part = (int)part });
                if (plan.Mode == WorldRemasterMode.Blend) blend++; else textures++;
                log.Add($"{mat.name} ({info.Shader}) -> {copy.shader.name} {plan} {detail} part {part}");
            }
            if (filter != null)   // keep the other pairs of an earlier full build
                foreach (var p in set.pairs)
                    if (p.original != null && p.original.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) pairs.Add(p);
            set.pairs = pairs;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            foreach (var l in log) Debug.Log("[G3WorldRemaster] " + l);
            return $"world remaster: {scanned} materials scanned, {pairs.Count} overridden ({blend} blend, {textures} texture swaps; parts {string.Join(", ", perPart.Select(kv => $"{kv.Key} {kv.Value}"))}) -> {SetPath}";
        }

        static string Stem(Material m, string prop) =>
            m.HasProperty(prop) && m.GetTexture(prop) is Texture t ? RemasterTextures.StemOf(t.name) : null;

        static WorldMaterialInfo Describe(Material m)
        {
            bool alphaClip = (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f) || m.IsKeywordEnabled("_ALPHATEST_ON");
            bool transparent = m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f;
            return new WorldMaterialInfo
            {
                Name = m.name,
                Shader = m.shader.name,
                BaseMap = Stem(m, "_BaseMap"),
                BaseMap2 = Stem(m, "_BaseMap2"),
                BumpMap = Stem(m, "_BumpMap"),
                BumpMap2 = Stem(m, "_BumpMap2"),
                SingleLayer = m.IsKeywordEnabled("_SINGLE_LAYER"),
                Opaque = !alphaClip && !transparent && m.shader.name != "G3/Vegetation" && m.shader.name != "G3/SpeedTree",
                AlphaClip = alphaClip,
                TreeMode = m.HasProperty("_G3Tree") ? Mathf.RoundToInt(m.GetFloat("_G3Tree"))
                    : m.IsKeywordEnabled("_G3TREE_LEAF") ? 1 : m.IsKeywordEnabled("_G3TREE_BILLBOARD") ? 2 : 0,
            };
        }

        static Material BuildMaterial(Material original, WorldMaterialInfo info, WorldMaterialPlan plan)
        {
            Material fresh;
            if (plan.Mode == WorldRemasterMode.Blend)
            {
                var shader = Shader.Find(WorldRemasterPlan.BlendShader);
                if (shader == null) { Debug.LogWarning($"[G3WorldRemaster] shader {WorldRemasterPlan.BlendShader} missing"); return null; }
                fresh = new Material(original) { shader = shader };   // same-named properties and keywords carry over
                if (info.Shader != "G3/LitBlend") FromSimpleLit(original, fresh);
                fresh.SetFloat("_HeightBlend", plan.Recipe.HeightBlend);
                fresh.SetFloat("_HeightDepth", plan.Recipe.HeightDepth);
                fresh.SetFloat("_NormalStrength", plan.Recipe.NormalStrength);
                fresh.SetFloat("_OcclusionStrength", plan.Recipe.OcclusionStrength);
            }
            else
            {
                fresh = new Material(original);
                if (fresh.HasProperty("_G3RemasterFoliage") && !WorldRemasterPlan.IsBark(info))   // bark: detail, not light through it
                {
                    fresh.SetFloat("_G3RemasterFoliage", 1);
                    fresh.EnableKeyword(WorldRemasterPlan.FoliageKeyword);
                    if (fresh.HasProperty("_Translucency")) fresh.SetFloat("_Translucency", plan.Recipe.Translucency);
                }
            }

            bool cutout = !info.Opaque;
            var v3 = TreeV3Look.For(info, plan.Recipe);   // trees v3: original G3 SpeedTree material, remastered (10.17)
            if (v3.Applies) ApplyTreeV3(fresh, v3, plan);
            foreach (var kv in plan.Maps)
            {
                if (!fresh.HasProperty(kv.Key)) continue;
                bool normal = kv.Key.StartsWith("_BumpMap", StringComparison.Ordinal);
                bool mask = kv.Key.StartsWith("_MaskMap", StringComparison.Ordinal);
                // texture swaps keep the original's normal-map use (leaf cards have none; bark and Simple Lit walls do)
                if (normal && plan.Mode == WorldRemasterMode.Textures && !original.IsKeywordEnabled("_NORMALMAP") && !v3.ForceNormalMap) continue;
                if (mask && v3.Applies) continue;   // v3: bark masks (worldtex layout) unused, foliage mask is in its normal map
                string layer = kv.Key.EndsWith("2", StringComparison.Ordinal) ? "_BaseMap2" : "_BaseMap";
                var originalTex = original.HasProperty(layer) ? original.GetTexture(layer) as Texture2D : null;
                bool packed = normal && v3.PackedMask && kv.Key == "_BumpMap";   // v3 foliage: rg normal + b AO + a translucency
                var tex = ImportMap(kv.Value, packed ? MapUse.Mask : normal ? MapUse.Normal : mask ? MapUse.Mask : MapUse.Base, cutout && !normal && !mask, originalTex,
                                    alphaReference: v3.Applies ? TreeV3Look.AlphaReference : 0.5f);
                if (tex == null) continue;
                fresh.SetTexture(kv.Key, tex);
                if (mask) fresh.SetFloat(kv.Key == "_MaskMap" ? "_UseMask" : "_UseMask2", 1);
                if (packed && fresh.HasProperty("_UseMaskMap")) fresh.SetFloat("_UseMaskMap", 1);
                if (normal && v3.ForceNormalMap) fresh.EnableKeyword("_NORMALMAP");   // G3/SpeedTree: keyword only
                if (normal && fresh.HasProperty("_UseNormalMap")) { fresh.SetFloat("_UseNormalMap", 1); fresh.EnableKeyword("_NORMALMAP"); }
            }

            string path = $"{MaterialDir}/{Safe(original.name)}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            fresh.name = original.name;
            if (existing == null) { AssetDatabase.CreateAsset(fresh, path); return fresh; }
            EditorUtility.CopySerialized(fresh, existing);
            UnityEngine.Object.DestroyImmediate(fresh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        /// <summary>
        /// r1 gameplay-distance terms (<see cref="WorldRemasterPlan.Detail"/>): WorldBlend macro variation, anti-tiling,
        /// ground meso normal and triplanar cliff normal; SpeedTree bark detail normal. The shared CC0 normals come from
        /// <c>GameData/Remaster/World/Detail/&lt;kind&gt;/Normal.png</c> (Tools/Remaster/worlddetail.py); a missing file leaves
        /// that term off.
        /// </summary>
        static void ApplyDetail(Material m, WorldDetailPlan d, string project)
        {
            bool changed = false;
            void F(string prop, float v)
            {
                if (m.HasProperty(prop) && !Mathf.Approximately(m.GetFloat(prop), v)) { m.SetFloat(prop, v); changed = true; }
            }
            Texture2D Detail(string kind)
            {
                string src = WorldRemasterPaths.DetailNormal(project, kind);
                return File.Exists(src) ? ImportMap(src, MapUse.Normal, false, null, "_Detail_" + kind) : null;
            }
            if (m.shader != null && m.shader.name == WorldRemasterPlan.BlendShader)
            {
                F("_MacroStrength", d.MacroStrength);
                F("_AntiTile", d.AntiTile);
                var ground = d.MesoStrength > 0 ? Detail(WorldDetailPlan.Ground) : null;
                F("_MacroNormalStrength", ground != null ? d.MesoStrength : 0);
                if (ground != null) { m.SetTexture("_MacroNormal", ground); changed = true; }
                var cliff = d.Triplanar > 0 ? Detail(WorldDetailPlan.Cliff) : null;
                F("_Triplanar", cliff != null ? d.Triplanar : 0);
                if (cliff != null) { m.SetTexture("_SteepNormal", cliff); changed = true; }
            }
            else if (d.BarkDetail && m.HasProperty("_G3RemasterBark"))
            {
                var bark = Detail(WorldDetailPlan.Bark);
                if (bark != null)
                {
                    m.SetTexture("_DetailBumpMap", bark);
                    m.SetTextureScale("_DetailBumpMap", new Vector2(2, 4));   // bark uv: ~1 repeat round the trunk; CC0 bark 2 m
                    F("_G3RemasterBark", 1);
                    m.EnableKeyword(WorldRemasterPlan.BarkKeyword);
                    changed = true;
                }
            }
            if (changed) EditorUtility.SetDirty(m);
        }

        /// <summary>Simple Lit → WorldBlend single layer: base/normal maps, tint, smoothness and the specular mask.</summary>
        static void FromSimpleLit(Material src, Material dst)
        {
            dst.SetFloat("_SingleLayer", 1);
            dst.EnableKeyword("_SINGLE_LAYER");
            if (src.HasProperty("_BaseColor")) dst.SetColor("_BaseColor", src.GetColor("_BaseColor"));
            if (src.GetTexture("_BumpMap") != null) { dst.SetFloat("_UseNormalMap", 1); dst.EnableKeyword("_NORMALMAP"); }
            bool spec = src.HasProperty("_SpecularHighlights") && src.GetFloat("_SpecularHighlights") > 0.5f;
            if (spec)
            {
                var c = src.GetColor("_SpecColor");
                dst.SetColor("_SpecColor", new Color(c.r, c.g, c.b, 1));
                dst.SetFloat("_SpecAlbedo", src.GetTexture("_SpecGlossMap") != null ? 1 : 0);
                dst.SetFloat("_UseSpecular", 1);
                dst.EnableKeyword("_SPECULAR_COLOR");
            }
            else
            {
                dst.SetFloat("_UseSpecular", 0);
                dst.DisableKeyword("_SPECULAR_COLOR");
            }
            foreach (var k in new[] { "_SPECGLOSSMAP", "_SPECULAR_COLOR_ON", "_ALPHATEST_ON", "_SURFACE_TYPE_TRANSPARENT" }) dst.DisableKeyword(k);
        }

        /// <summary>
        /// Copies a replacement PNG into the project with its rows flipped: the original world textures are raw DDS uploads
        /// (first DDS row at v = 0), while Unity imports a PNG's top row at v = 1. Settings follow the original's wrap mode
        /// and the world filtering (<see cref="G3AssetCache.ApplyWorldFiltering"/>).
        /// </summary>
        /// <summary>Trees v3 values on a G3/SpeedTree remaster copy (maps are set by the caller's map loop).</summary>
        static void ApplyTreeV3(Material m, TreeV3Look v3, WorldMaterialPlan plan)
        {
            void F(string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
            F("_TreeV3", 1);
            F("_CrownNormal", v3.CrownNormal);
            F("_CrownAO", v3.CrownAO);
            F("_TintVariation", v3.TintVariation);
            F("_UseMaskMap", 0);   // set again when the foliage mask is imported
            if (v3.ForceNormalMap && plan.Maps.ContainsKey("_BumpMap")) F("_BumpScale", v3.NormalStrength);
        }

        static Texture2D ImportMap(string source, MapUse use, bool cutout, Texture2D original, string folder = null, float alphaReference = 0.5f)
        {
            string stem = folder ?? Path.GetFileName(Path.GetDirectoryName(source)) ?? "x";
            string dir = $"{TextureDir}/{Safe(stem)}";
            Directory.CreateDirectory(dir);
            string path = $"{dir}/{Path.GetFileName(source)}";
            string stamp = path + ".src";   // source write time + size: reimport only when the pipeline output changed
            string id = $"{File.GetLastWriteTimeUtc(source).Ticks}:{new FileInfo(source).Length}";
            if (!File.Exists(path) || !File.Exists(stamp) || File.ReadAllText(stamp) != id)
            {
                var img = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                if (!img.LoadImage(File.ReadAllBytes(source))) { Debug.LogWarning($"[G3WorldRemaster] unreadable {source}"); return null; }
                var px = img.GetPixels32();
                int w = img.width, h = img.height;
                var flipped = new Color32[px.Length];
                for (int y = 0; y < h; y++) Array.Copy(px, y * w, flipped, (h - 1 - y) * w, w);
                img.SetPixels32(flipped);
                File.WriteAllBytes(path, img.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(img);
                File.WriteAllText(stamp, id);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            if (AssetImporter.GetAtPath(path) is TextureImporter ti && Configure(ti, use, cutout, original, alphaReference)) ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static bool Configure(TextureImporter ti, MapUse use, bool cutout, Texture2D original, float alphaReference)
        {
            var type = use == MapUse.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool srgb = use == MapUse.Base;
            var wrap = original != null && original.wrapModeU == TextureWrapMode.Clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            bool dirty = ti.textureType != type || ti.sRGBTexture != srgb || ti.maxTextureSize != 4096 ||
                         ti.anisoLevel != G3AssetCache.WorldAnisoLevel || ti.filterMode != FilterMode.Trilinear ||
                         ti.textureCompression != TextureImporterCompression.CompressedHQ || !ti.mipmapEnabled ||
                         ti.alphaIsTransparency != cutout || ti.mipMapsPreserveCoverage != cutout || ti.wrapMode != wrap ||
                         ti.streamingMipmaps != true || (cutout && !Mathf.Approximately(ti.alphaTestReferenceValue, alphaReference));
            if (!dirty) return false;
            ti.textureType = type;
            ti.sRGBTexture = srgb;
            ti.maxTextureSize = 4096;
            ti.anisoLevel = G3AssetCache.WorldAnisoLevel;
            ti.filterMode = FilterMode.Trilinear;
            ti.mipmapEnabled = true;
            ti.streamingMipmaps = true;   // the remaster set is ~4x the original texels (Docs/remaster-world.md §5)
            ti.textureCompression = TextureImporterCompression.CompressedHQ;   // BC7 colour/mask, BC5 normals
            ti.alphaIsTransparency = cutout;
            ti.mipMapsPreserveCoverage = cutout;   // alpha-tested foliage keeps its density in the distance
            ti.alphaTestReferenceValue = alphaReference;   // the material's cutoff (SpeedTree 84/255), else 0.5
            ti.wrapMode = wrap;
            return true;
        }

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
