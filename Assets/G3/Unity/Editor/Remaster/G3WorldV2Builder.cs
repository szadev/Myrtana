using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Remaster;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace G3.Unity.Editor.Remaster
{
    /// <summary>
    /// World remaster v2 (Docs/remaster-world.md section 10.2): CC0 PBR materials for G3's terrain, rocks and buildings.
    /// For every opaque world material whose layer images have a v2 class (<see cref="WorldV2.LayerFor"/>) it makes a copy
    /// with <c>G3/Remaster/WorldV2</c> in <see cref="MaterialDir"/>, starting from the r3 copy when there is one (its
    /// upscaled maps and baked heights stay the structure of the partial classes), sets the packed CC0 scans
    /// (Tools/Remaster/worldv2_layers.py → GameData/Remaster/World/V2/Layers, imported into <see cref="LayerDir"/>), the
    /// per-layer kind/scale/replace vector and the moss layer, and writes the pairs into the remaster set
    /// (<see cref="G3WorldRemasterSet.v2pairs"/>; kind 1 terrain, 2 buildings and rocks); the v2 trees come from G3TreeV2Builder.
    /// Originals and r3 copies are never edited. CLI: <c>G3.Unity.Editor.Remaster.G3WorldV2Builder.Build()</c>.
    /// </summary>
    public static class G3WorldV2Builder
    {
        public const string Root = G3WorldRemasterBuilder.Root + "/V2";
        public const string LayerDir = Root + "/Layers";
        public const string MaterialDir = Root + "/Materials";
        public const string Shader = "G3/Remaster/WorldV2";
        const string Moss = "polyhaven_leafy_grass";
        const string SandMicro = "sand_micro";

        [Serializable] class SandMicroStats { public float size_m, height_m; }
        const string SandScans = "sand_scans";
        [Serializable] class SandScanSlot { public float size_m, height_m; }
        [Serializable] class SandScanStats { public SandScanSlot fine, mid, coarse; }
        const string MossG3Image = "G3_Nature_Ground_Grass_01_Diffuse_01";
        const float DefaultMetresPerUv = 2.4f;   // median of the architecture materials (materials.tsv)

        static string Project => Directory.GetParent(Application.dataPath)!.FullName;
        static string LayerSource(string folder) => Path.Combine(WorldRemasterPaths.Root(Project), "V2", "Layers", folder);

        [MenuItem("G3/Remaster/World/V2/Build Materials", priority = 230)]
        static void MenuBuild() => Debug.Log(Build());

        [MenuItem("G3/Remaster/World/V2/Reload Look JSON (play mode)", priority = 231)]
        static void MenuReload() => Debug.Log(G3WorldRemasterLook.Reload());

        [MenuItem("G3/Remaster/World/V2/Describe", priority = 232)]
        static void MenuDescribe() => Debug.Log(G3WorldRemasterLook.Describe() + " | " + G3WorldV2.Describe());

        /// <summary>Builds or refreshes every v2 material; returns a one-line summary (per-material lines in the log).</summary>
        public static string Build(string filter = null)
        {
            var set = AssetDatabase.LoadAssetAtPath<G3WorldRemasterSet>(G3WorldRemasterBuilder.SetPath);
            if (set == null) return "v2: no remaster set - run G3/Remaster/World/Build Overrides first";
            var shader = UnityEngine.Shader.Find(Shader);
            if (shader == null) return $"v2: shader {Shader} missing";
            Directory.CreateDirectory(MaterialDir);
            var inv = ReadInventory();
            var r3 = new Dictionary<Material, Material>();
            foreach (var p in set.pairs) if (p.original != null && p.remaster != null) r3[p.original] = p.remaster;

            var layers = new Dictionary<string, (Texture2D albedo, Texture2D normal, Texture2D mask)>();
            (Texture2D, Texture2D, Texture2D)? Layer(string folder)
            {
                if (layers.TryGetValue(folder, out var l)) return l;
                var a = Import(folder, "Albedo.png", Use.Albedo);
                var n = Import(folder, "Normal.png", Use.Normal);
                var m = Import(folder, "Mask.png", Use.Mask);
                if (a == null || n == null || m == null) return null;
                return layers[folder] = (a, n, m);
            }
            var moss = Layer(Moss);
            var mossG3 = FindTexture(MossG3Image);

            var pairs = new List<G3WorldRemasterSet.V2Pair>();
            var log = new List<string>();
            int scanned = 0, terrain = 0, surfaces = 0, missing = 0, aiCount = 0;
            var partial = new List<string>();   // runner-31: show which materials keep G3's image as the base
            string matRoot = G3AssetCache.Root + "/Materials";
            if (!AssetDatabase.IsValidFolder(matRoot)) return $"v2: {matRoot} missing";
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { matRoot }))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (mat == null || mat.shader == null) continue;
                if (filter != null && mat.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // arch-2 (25.7): alpha-tested arch cloth (sheets, banners, carpets, drapery) too - the v2 shader's
                // _ALPHATEST_ON clips every pass (forward, shadow, depth, depth normals) on G3's (r3) base alpha
                bool cutoutArch = IsCutoutWorld(mat) && WorldV2.ArchCutoutAllowed(Stem(mat, "_BaseMap"));
                if (!IsOpaqueWorld(mat) && !cutoutArch) continue;
                scanned++;
                string stemA = Stem(mat, "_BaseMap"), stemB = Stem(mat, "_BaseMap2");
                bool single = mat.shader.name != "G3/LitBlend" || mat.IsKeywordEnabled("_SINGLE_LAYER");
                var la = WorldV2.LayerFor(stemA);
                var lb = single ? null : WorldV2.LayerFor(stemB);
                // arch-1 (25): a Varant architecture layer only with its remastered G3 set (an r3 copy): without one the
                // material keeps G3's original (no CC0 grain on an un-remastered image)
                if (la?.Kind == "arch" && !r3.ContainsKey(mat)) la = null;
                if (lb?.Kind == "arch" && !r3.ContainsKey(mat)) lb = null;
                bool aiA = HasAi(stemA), aiB = !single && HasAi(stemB);   // 10.18: local-AI remaster of the G3 image
                if (la == null && lb == null && !aiA && !aiB) continue;
                inv.TryGetValue(mat.name, out var row);
                bool isTerrain = row.category != null ? row.category.Contains("terrain") : mat.shader.name == "G3/LitBlend";
                bool isArch = row.category != null ? row.category.Contains("architecture") && !isTerrain : la != null && la.Kind != "ground" && la.Kind != "rock";
                float mPerUv = row.mPerUv > 0 ? row.mPerUv : DefaultMetresPerUv;

                var src = r3.TryGetValue(mat, out var copy) ? copy : mat;
                var v2 = new Material(src) { shader = shader };
                if (src.shader.name != "G3/LitBlend" && src.shader.name != WorldRemasterPlan.BlendShader) FromSimpleLit(src, v2);
                if (cutoutArch)
                {
                    v2.EnableKeyword("_ALPHATEST_ON");
                    v2.SetFloat("_Cutoff", src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f);
                    v2.SetOverrideTag("RenderType", "TransparentCutout");
                    v2.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                }
                bool anyMissing = false;
                Vector4 Setup(WorldV2.Layer l, string stem, bool ai, string suffix)
                {
                    if (ai && Layer(WorldV2.AiFolder(stem)) is { } at)
                    {
                        v2.SetTexture("_V2Albedo" + suffix, at.Item1);
                        v2.SetTexture("_V2Normal" + suffix, at.Item2);
                        v2.SetTexture("_V2Mask" + suffix, at.Item3);
                        // terrain cells and every G3/LitBlend mesh: G3's mesh UV (its per-face facets), world triplanar only where
                        // that UV is stretched (planar XZ on walls, dungeon blends); authored mesh UVs (boulders) keep G3's layout
                        var f = WorldV2.AiLayerVector(isTerrain || mat.shader.name == "G3/LitBlend");
                        return new Vector4(f[0], f[1], f[2], f[3]);
                    }
                    if (l == null) return new Vector4(1, 1, -1, 0);
                    var t = Layer(l.Cc0);
                    if (t == null) { anyMissing = true; return new Vector4(1, 1, -1, 0); }
                    v2.SetTexture("_V2Albedo" + suffix, t.Value.Item1);
                    v2.SetTexture("_V2Normal" + suffix, t.Value.Item2);
                    v2.SetTexture("_V2Mask" + suffix, t.Value.Item3);
                    // kind: rocks world triplanar; ground world XZ on terrain; everything on meshes with authored UVs = surface
                    float kind = l.Rock ? 2 : (isTerrain && l.Kind == "ground") ? 1 : 0;
                    // arch-1 (25): the arch detail tile is sampled world triplanar at its real size (G3's metres per UV vary a lot
                    // between the Varant meshes: 0.002-25 m in materials.tsv)
                    float scale = kind > 0.5f || l.Kind == "arch" ? 1f / l.RealSize : WorldV2.UvScale(mPerUv, l.RealSize);
                    return new Vector4(scale, l.Replace, kind, l.Parallax ? 1 : 0);
                }
                var va = Setup(la, stemA, aiA, "A");
                var vb = single ? new Vector4(1, 1, -1, 0) : Setup(lb, stemB, aiB, "B");
                bool anyAi = va.z > 2.5f || vb.z > 2.5f;
                if (anyMissing) missing++;
                if (anyAi) aiCount++;
                // 10.18 pass 4: G3's second stone multiply (_DetailMap, e.g. Myrtana_Rock_02_A = Stone_02 x Stone_03) in its AI
                // version where one exists (the shader multiplies it into the AI layers, mean-normalised like G3/LitBlend)
                if (anyAi)
                    foreach (var dp in new[] { "_DetailMap", "_DetailMap2" })
                        if (HasAi(Stem(mat, dp)) && Layer(WorldV2.AiFolder(Stem(mat, dp))) is { } dt) v2.SetTexture(dp, dt.Item1);
                if (va.z < -0.5f && vb.z < -0.5f) { UnityEngine.Object.DestroyImmediate(v2); continue; }
                // arch-1 (25): Varant architecture class of layer A (else B): parallax depth, dust, class, cloth translucency
                string archCls = WorldV2.ArchClassFor(stemA) ?? (single ? null : WorldV2.ArchClassFor(stemB));
                var av = WorldV2.ArchVector(archCls);
                // arch-3 (26): stems that keep their v2 CC0 class get the region weathering only (class id 0: no arch branch)
                if (archCls == null) av[1] = Mathf.Max(WorldV2.ArchWeatherFor(stemA), single ? 0 : WorldV2.ArchWeatherFor(stemB));
                v2.SetVector("_V2Arch", new Vector4(av[0], av[1], av[2], av[3]));
                v2.SetVector("_V2LayerA", va);
                v2.SetVector("_V2LayerB", vb);
                // pass 6: sand ground against rock blends soft and keeps G3's own sand image (WorldV2.BlendVector)
                var bv = WorldV2.BlendVector(va.z > -0.5f ? la : null, vb.z > -0.5f ? lb : null);
                v2.SetVector("_V2Blend", new Vector4(bv[0], bv[1], bv[2], bv[3]));
                // remaster-snow (12.3): G3's road snow layers are packed snow (trodden, glossy, no ripples)
                // remaster-sand (22.2): z, w = sand class of the ground layers A, B (1 loose, 2 wet, 3 packed path; G3SandV2 also sets it at runtime)
                float sandA = Mathf.Abs(va.z - 1) < 0.5f ? SandV2.LayerClass(stemA) : 0, sandB = !single && Mathf.Abs(vb.z - 1) < 0.5f ? SandV2.LayerClass(stemB) : 0;
                v2.SetVector("_V2Snow", new Vector4(SnowV2.PackedSnow(stemA), single ? 0 : SnowV2.PackedSnow(stemB), sandA, sandB));
                bool rocky = va.z > 1.5f || vb.z > 1.5f;
                // AI layers: no CC0 moss (the G3 image keeps its own green), normals as derived (owner: no odd green on the mountains)
                v2.SetVector("_V2Surface", new Vector4(isArch ? 1 : 0, rocky && !anyAi ? 1 : 0, WorldV2.NormalStrength(stemA, single ? null : stemB, anyAi, rocky), mPerUv));
                if (moss != null) { v2.SetTexture("_V2Moss", moss.Value.Item1); v2.SetTexture("_V2MossNormal", moss.Value.Item2); }
                if (mossG3 != null) v2.SetTexture("_V2MossG3", mossG3);
                v2.SetVector("_V2MossParams", new Vector4(0.5f, 0.4f, 0.15f, 0));
                var saved = Save(v2, mat.name);
                int kindPair = isTerrain ? 1 : 2;
                var part = WorldRemasterParts.Classify(row.category, mat.shader.name, mat.name, kindPair, isArch, rocky);   // fix-71
                pairs.Add(new G3WorldRemasterSet.V2Pair { original = mat, v2 = saved, kind = kindPair, part = (int)part });
                if (kindPair == 1) terrain++; else surfaces++;
                if ((va.z > -0.5f && va.z < 0.5f && va.y < 0.999f) || (vb.z > -0.5f && vb.z < 0.5f && vb.y < 0.999f)) partial.Add(mat.name);
                log.Add($"{mat.name} ({mat.shader.name}{(src != mat ? ", from r3" : "")}) A {la?.ToString() ?? "-"} {Fmt(va)} B {lb?.ToString() ?? "-"} {Fmt(vb)} mPerUv {mPerUv:0.##}{(isArch ? " building" : "")}{(rocky && !anyAi ? " moss" : "")}{(anyAi ? " AI" : "")}{(bv[0] > 0 ? " sand-blend" : "")}{(archCls != null ? " arch:" + archCls + (src != mat ? "" : " (no r3 set: detail only)") : "")} part {part}");
            }
            if (filter != null && set.v2pairs != null)
                foreach (var p in set.v2pairs)
                    if (p.original != null && p.original.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) pairs.Add(p);
            set.v2pairs = pairs;
            // sand-2 (22.8): the CC0 sand micro tile, when packed (Tools/Remaster/sand_scans.py)
            set.sandMicro = File.Exists(Path.Combine(LayerSource(SandMicro), "SandMicro.png")) ? Import(SandMicro, "SandMicro.png", Use.Mask) : null;
            if (set.sandMicro != null)
            {
                try
                {
                    var st = JsonUtility.FromJson<SandMicroStats>(File.ReadAllText(Path.Combine(LayerSource(SandMicro), "stats.json")));
                    if (st.size_m > 0.1f) set.sandMicroSize = st.size_m;
                    if (st.height_m > 0) set.sandMicroHeight = st.height_m;
                }
                catch (Exception e) { Debug.LogWarning("[G3WorldV2] sand micro stats: " + e.Message); }
            }
            ImportSandScans(set);
            var trees = AssetDatabase.LoadAssetAtPath<G3TreeV2Set>(G3TreeV2Builder.SetPath);   // keep the v2 trees if built
            if (trees != null) set.treeV2 = trees;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            foreach (var l in log) Debug.Log("[G3WorldV2] " + l);
            return $"world v2: {scanned} opaque world materials scanned, {pairs.Count} v2 copies ({terrain} terrain, {surfaces} buildings/rocks)" +
                   $"{(aiCount > 0 ? $", {aiCount} with AI layers (10.18)" : "")}{(missing > 0 ? $", {missing} with missing CC0 layers (run Tools/Remaster/worldv2_layers.py)" : "")}, v2 trees {(set.treeV2 != null ? "set" : "not built (G3/Remaster/World/V2/Build Trees)")}, sand micro {(set.sandMicro != null ? set.sandMicroSize + " m" : "none")} -> {MaterialDir}; " +
                   $"{partial.Count} partial (G3 image base + CC0 grain): {string.Join(", ", partial.Take(12))}{(partial.Count > 12 ? ", ..." : "")}";
        }

        [MenuItem("G3/Remaster/World/V2/Import Sand Scans", priority = 233)]
        static void MenuSandScans() => Debug.Log(ImportSandScans());

        /// <summary>
        /// sand-3 (22.10): imports the scan-based sand set (Tools/Remaster/sand_scans.py --set: fine, mid, coarse - an albedo image
        /// with height in A, sRGB, and a normal/roughness/occlusion image, linear) into the remaster set with the scans' sizes and
        /// heights; without the packed set the fields are cleared (sand-2 look). Also run by <see cref="Build"/>. CLI:
        /// <c>G3.Unity.Editor.Remaster.G3WorldV2Builder.ImportSandScans()</c>.
        /// </summary>
        public static string ImportSandScans()
        {
            var set = AssetDatabase.LoadAssetAtPath<G3WorldRemasterSet>(G3WorldRemasterBuilder.SetPath);
            if (set == null) return "sand scans: no remaster set";
            string r = ImportSandScans(set);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return r;
        }

        static string ImportSandScans(G3WorldRemasterSet set)
        {
            string dir = LayerSource(SandScans);
            bool all = true;
            foreach (var slot in new[] { "fine", "mid", "coarse" })
                all &= File.Exists(Path.Combine(dir, slot + "_A.png")) && File.Exists(Path.Combine(dir, slot + "_N.png"));
            if (!all)
            {
                set.sandFineA = set.sandFineN = set.sandMidA = set.sandMidN = set.sandCoarseA = set.sandCoarseN = null;
                return "sand scans: none (run python -I Tools/Remaster/sand_scans.py --set)";
            }
            set.sandFineA = Import(SandScans, "fine_A.png", Use.Albedo);
            set.sandFineN = Import(SandScans, "fine_N.png", Use.Mask);
            set.sandMidA = Import(SandScans, "mid_A.png", Use.Albedo);
            set.sandMidN = Import(SandScans, "mid_N.png", Use.Mask);
            set.sandCoarseA = Import(SandScans, "coarse_A.png", Use.Albedo);
            set.sandCoarseN = Import(SandScans, "coarse_N.png", Use.Mask);
            try
            {
                var st = JsonUtility.FromJson<SandScanStats>(File.ReadAllText(Path.Combine(dir, "stats.json")));
                if (st.fine != null && st.mid != null && st.coarse != null)
                {
                    set.sandScanSize = new Vector3(st.fine.size_m, st.mid.size_m, st.coarse.size_m);
                    set.sandScanHeight = new Vector3(st.fine.height_m, st.mid.height_m, st.coarse.height_m);
                }
            }
            catch (Exception e) { Debug.LogWarning("[G3WorldV2] sand scan stats: " + e.Message); }
            return $"sand scans: {(set.HasSandScans ? "set" : "incomplete")}, sizes {set.sandScanSize}";
        }

        /// <summary>10.18: env G3_V2_AI=0 builds the CC0 rock path even where an AI layer exists (A/B of the two).</summary>
        static bool AiEnabled => Environment.GetEnvironmentVariable("G3_V2_AI") != "0";

        static bool HasAi(string stem) => AiEnabled && !string.IsNullOrEmpty(stem) &&
                                          File.Exists(Path.Combine(LayerSource(WorldV2.AiFolder(stem)), "Albedo.png"));

        static string Fmt(Vector4 v) => v.z < -0.5f ? "" : $"[k{v.z:0} s{v.x:0.###} r{v.y:0.##}{(v.w > 0 ? (v.z > 2.5f ? " tri" : " pom") : "")}]";

        static bool IsOpaqueWorld(Material m)
        {
            string s = m.shader.name;
            if (s != "G3/LitBlend" && s != "Universal Render Pipeline/Simple Lit" && s != "Universal Render Pipeline/Lit") return false;
            bool clip = (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f) || m.IsKeywordEnabled("_ALPHATEST_ON");
            bool transparent = m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f;
            return !clip && !transparent;
        }

        /// <summary>arch-2: alpha-tested (not blended) world material on the shaders the v2 copy converts.</summary>
        static bool IsCutoutWorld(Material m)
        {
            string s = m.shader.name;
            if (s != "Universal Render Pipeline/Simple Lit" && s != "Universal Render Pipeline/Lit") return false;
            bool clip = (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f) || m.IsKeywordEnabled("_ALPHATEST_ON");
            bool transparent = m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f;
            return clip && !transparent;
        }

        static string Stem(Material m, string prop) =>
            m.HasProperty(prop) && m.GetTexture(prop) is Texture t ? RemasterTextures.StemOf(t.name) : null;

        /// <summary>Simple Lit / Lit → single layer: base/normal maps and tint (like the r3 conversion).</summary>
        static void FromSimpleLit(Material src, Material dst)
        {
            dst.SetFloat("_SingleLayer", 1);
            dst.EnableKeyword("_SINGLE_LAYER");
            if (src.HasProperty("_BaseColor")) dst.SetColor("_BaseColor", src.GetColor("_BaseColor"));
            if (src.HasProperty("_BumpMap") && src.GetTexture("_BumpMap") != null) { dst.SetFloat("_UseNormalMap", 1); dst.EnableKeyword("_NORMALMAP"); }
            foreach (var k in new[] { "_SPECGLOSSMAP", "_SPECULAR_COLOR", "_ALPHATEST_ON", "_SURFACE_TYPE_TRANSPARENT", "_DETAILMAP", "_DETAILMAP2" }) dst.DisableKeyword(k);
        }

        static Material Save(Material fresh, string name)
        {
            string path = $"{MaterialDir}/{Safe(name)}.mat";
            fresh.name = name;
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null) { AssetDatabase.CreateAsset(fresh, path); return fresh; }
            EditorUtility.CopySerialized(fresh, existing);
            UnityEngine.Object.DestroyImmediate(fresh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        enum Use { Albedo, Normal, Mask }

        /// <summary>Copies a packed CC0 map into the project (no row flip: Unity convention, see worldv2_layers.py).</summary>
        static Texture2D Import(string folder, string file, Use use)
        {
            string src = Path.Combine(LayerSource(folder), file);
            if (!File.Exists(src)) { Debug.LogWarning($"[G3WorldV2] missing {src}"); return null; }
            string dir = $"{LayerDir}/{folder}";
            Directory.CreateDirectory(dir);
            string path = $"{dir}/{file}";
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
                var type = use == Use.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                bool srgb = use == Use.Albedo;
                // 10.18: the AI rock albedo keeps its detail in the mips (Kaiser) - box mips washed the cliffs out at range
                var mipFilter = use == Use.Albedo && folder.StartsWith(WorldV2.AiPrefix, StringComparison.Ordinal)
                    ? TextureImporterMipFilter.KaiserFilter : TextureImporterMipFilter.BoxFilter;
                if (ti.textureType != type || ti.sRGBTexture != srgb || ti.maxTextureSize != 2048 || ti.anisoLevel != G3AssetCache.WorldAnisoLevel ||
                    ti.filterMode != FilterMode.Trilinear || !ti.mipmapEnabled || !ti.streamingMipmaps || ti.wrapMode != TextureWrapMode.Repeat ||
                    ti.textureCompression != TextureImporterCompression.CompressedHQ || ti.mipmapFilter != mipFilter)
                {
                    ti.textureType = type;
                    ti.sRGBTexture = srgb;
                    ti.maxTextureSize = 2048;
                    ti.anisoLevel = G3AssetCache.WorldAnisoLevel;
                    ti.filterMode = FilterMode.Trilinear;
                    ti.mipmapEnabled = true;
                    ti.streamingMipmaps = true;
                    ti.wrapMode = TextureWrapMode.Repeat;
                    ti.textureCompression = TextureImporterCompression.CompressedHQ;   // BC7 albedo/mask, BC5 normal
                    ti.mipmapFilter = mipFilter;
                    ti.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Texture2D FindTexture(string name)
        {
            foreach (var g in AssetDatabase.FindAssets(name + " t:Texture2D", new[] { G3AssetCache.Root }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
            }
            return null;
        }

        /// <summary>materials.tsv (G3Cli remaster world inventory): material → category list, metres per UV.</summary>
        internal static Dictionary<string, (string category, float mPerUv)> ReadInventory()
        {
            var d = new Dictionary<string, (string, float)>(StringComparer.OrdinalIgnoreCase);
            string tsv = Path.Combine(WorldRemasterPaths.Root(Project), "Inventory", "materials.tsv");
            if (!File.Exists(tsv)) { Debug.LogWarning("[G3WorldV2] no " + tsv + " (G3Cli remaster world inventory): default metres per UV"); return d; }
            var lines = File.ReadAllLines(tsv);
            if (lines.Length == 0) return d;
            var head = lines[0].Split('\t');
            int iName = Array.IndexOf(head, "material"), iCat = Array.IndexOf(head, "category"), iM = Array.IndexOf(head, "mPerUv");
            foreach (var l in lines.Skip(1))
            {
                var c = l.Split('\t');
                if (c.Length <= Math.Max(iName, Math.Max(iCat, iM)) || iName < 0) continue;
                float.TryParse(iM >= 0 ? c[iM] : "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m);
                d[c[iName]] = (iCat >= 0 ? c[iCat] : null, m);
            }
            return d;
        }

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // ------------------------------------------------------------------ probe GI (Adaptive Probe Volumes)

        /// <summary>
        /// Prepares an Adaptive Probe Volume bake over Faring (section 10.6): switches the URP asset's light probe system to
        /// probe volumes (project-wide: affects the original look too, so only for v2 experiments), adds a local probe
        /// volume over the Faring box to the active scene, and marks the loaded cells' static renderers in that box as
        /// Contribute GI. The bake itself is started from the Lighting window (Adaptive Probe Volumes: Sky Occlusion on, so
        /// G3's day cycle keeps driving the sky light) or by <see cref="BakeProbeGI"/>. Returns a summary.
        /// </summary>
        [MenuItem("G3/Remaster/World/V2/Prepare Probe GI (APV, Faring box)", priority = 240)]
        public static string PrepareProbeGI()
        {
            var lines = new List<string>();
            var urp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (urp != null)
            {
                var so = new SerializedObject(urp);
                var prop = so.FindProperty("m_LightProbeSystem");
                if (prop != null) { prop.intValue = 1; so.ApplyModifiedProperties(); lines.Add($"{urp.name}: light probe system = probe volumes (revert: set it back to Light Probe Groups)"); }
                else lines.Add("URP asset has no m_LightProbeSystem: set 'Light Probe System' to 'Probe Volumes' by hand");
            }
            var center = new Vector3(635, 0, 585);
            const float half = 260;
            if (Physics.Raycast(new Vector3(center.x, 3000, center.z), Vector3.down, out var hit, 6000)) center.y = hit.point.y;
            var type = Type.GetType("UnityEngine.Rendering.ProbeVolume, Unity.RenderPipelines.Core.Runtime");
            if (type == null) lines.Add("ProbeVolume type not found (Core RP): add a Probe Volume by hand");
            else
            {
                var go = GameObject.Find("G3 V2 Probe Volume (Faring)") ?? new GameObject("G3 V2 Probe Volume (Faring)");
                go.transform.position = center + Vector3.up * 40;
                var pv = go.GetComponent(type) ?? go.AddComponent(type);
                var so = new SerializedObject(pv);
                var mode = so.FindProperty("mode");
                if (mode != null) mode.intValue = 2;   // Local
                var size = so.FindProperty("size");
                if (size != null) size.vector3Value = new Vector3(half * 2, 220, half * 2);
                so.ApplyModifiedProperties();
                lines.Add($"probe volume at {go.transform.position} size {half * 2}x220x{half * 2} in scene '{go.scene.name}'");
            }
            int marked = 0;
            var box = new Bounds(center, new Vector3(half * 2, 2000, half * 2));
            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude))
            {
                if (!box.Intersects(r.bounds)) continue;
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                if ((flags & StaticEditorFlags.ContributeGI) != 0) continue;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags | StaticEditorFlags.ContributeGI);
                r.receiveGI = ReceiveGI.LightProbes;
                marked++;
            }
            lines.Add($"{marked} renderers marked Contribute GI (receive GI: light probes) - the cell scenes are now dirty");
            lines.Add("next: Lighting window > Adaptive Probe Volumes: Baking Mode 'Single Scene', Sky Occlusion ON, Probe spacing 3-27 m; Generate Lighting (or BakeProbeGI())");
            string s = "[G3WorldV2] probe GI: " + string.Join("; ", lines);
            Debug.Log(s);
            return s;
        }

        /// <summary>Starts the asynchronous bake of the prepared probe volume (editor; the runner waits for Lightmapping.isRunning == false).</summary>
        [MenuItem("G3/Remaster/World/V2/Bake Probe GI", priority = 241)]
        public static string BakeProbeGI()
        {
            bool ok = Lightmapping.BakeAsync();
            string s = ok ? "[G3WorldV2] probe GI bake started (Lightmapping.isRunning)" : "[G3WorldV2] probe GI bake did not start";
            Debug.Log(s);
            return s;
        }
    }
}
