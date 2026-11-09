using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using G3.Formats.Remaster;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// R1 material override (Docs/remaster-pipeline.md §9): with <see cref="G3CharacterImporter.UseRemaster"/> on, every
    /// character material whose base texture has a set from <c>Tools/Remaster/texpipe.py</c>
    /// (<c>GameData/Remaster/Textures/&lt;image stem&gt;/{BaseMap,BumpMap,MaskMap}.png</c>) is swapped for a material on a
    /// G3/Remaster/{Armour,Skin,Hair,Eye} shader. The shader is chosen by material name: the pipeline's
    /// <c>info.json</c> records each material's kind (several materials share one atlas: hero head skin + eyes), else
    /// <see cref="RemasterMaterialKinds.Classify"/>. Originals are untouched (A/B: override off); eyelashes keep theirs.
    /// Output (git-ignored): <c>Characters/Textures/Remaster/R1/&lt;stem&gt;/</c>, <c>Characters/Materials/Remaster/R1/</c>.
    /// </summary>
    public static class G3RemasterMaterials
    {
        public const string TextureDir = G3CharacterImporter.Root + "/Textures/Remaster/R1";
        public const string MaterialDir = G3CharacterImporter.Root + "/Materials/Remaster/R1";
        public const string SharedDir = "_shared";
        public const string PoreMap = "SkinPores.png";

        /// <summary>Pore detail tiling over the UV atlas: heads fill their 0..1 square, bodies spread hands over a corner.</summary>
        const float HeadPoreTiling = 14f, BodyPoreTiling = 28f;

        static readonly Regex VariantSuffix = new Regex(@"(_S\d+)+$", RegexOptions.IgnoreCase);

        [Serializable] sealed class MaterialKindEntry { public string material; public string kind; }
        [Serializable] sealed class SetInfo { public string stem; public int[] size; public bool masked; public MaterialKindEntry[] material_kinds; public float alpha_cut = -1; }

        /// <summary>Replaces remasterable materials on every renderer under <paramref name="root"/>; returns the count.</summary>
        public static int ApplyTo(G3CharacterImporter imp, GameObject root)
        {
            int n = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = Remaster(imp, mats[i]);
                    if (m == mats[i]) continue;
                    mats[i] = m;
                    changed = true;
                    n++;
                }
                if (changed) r.sharedMaterials = mats;
            }
            return n;
        }

        /// <summary>Per importer run: original → remaster (a rerun of the pipeline is picked up by the next importer).</summary>
        static readonly ConditionalWeakTable<G3CharacterImporter, Dictionary<Material, Material>> Done =
            new ConditionalWeakTable<G3CharacterImporter, Dictionary<Material, Material>>();

        /// <summary>The remaster material for <paramref name="original"/>, or the original when there is no texture set.</summary>
        public static Material Remaster(G3CharacterImporter imp, Material original)
        {
            if (original == null || original.shader == null || original.shader.name.StartsWith("G3/Remaster/", StringComparison.Ordinal))
                return original;
            var done = imp != null ? Done.GetOrCreateValue(imp) : new Dictionary<Material, Material>();
            if (done.TryGetValue(original, out var cached) && cached != null) return cached;
            var result = Build(imp, original) ?? original;
            done[original] = result;
            return result;
        }

        static Material Build(G3CharacterImporter imp, Material original)
        {
            var baseTex = original.HasProperty("_BaseMap") ? original.GetTexture("_BaseMap") : null;
            if (baseTex == null) return null;
            string stem = RemasterTextures.StemOf(AssetPathName(baseTex));
            string project = G3RemasterOverride.ProjectDir;
            var maps = RemasterTextures.Find(project, stem);
            if (!maps.ContainsKey(RemasterTextures.BaseMap)) return null;

            var info = ReadInfo(Path.Combine(RemasterTextures.SetDir(project, stem), RemasterTextures.Info));
            string matName = VariantSuffix.Replace(original.name, "");
            var kind = KindOf(info, matName, stem);
            if (kind == RemasterMaterialKind.Lash) return null; // cutout eyelashes stay as they are
            var shader = Shader.Find("G3/Remaster/" + kind);
            if (shader == null) { imp?.Log.Add($"remaster: shader G3/Remaster/{kind} missing, {original.name} unchanged"); return null; }

            bool cutout = kind == RemasterMaterialKind.Hair || (info?.masked ?? false) || IsCutout(original);
            var baseMap = ImportMap(stem, maps[RemasterTextures.BaseMap], MapUse.Base, cutout);
            var bump = maps.TryGetValue(RemasterTextures.BumpMap, out var bp) ? ImportMap(stem, bp, MapUse.Normal, false) : null;
            var mask = maps.TryGetValue(RemasterTextures.MaskMap, out var mp) ? ImportMap(stem, mp, MapUse.Mask, false) : null;
            var pores = PoreTexture(project);

            Directory.CreateDirectory(MaterialDir);
            string path = $"{MaterialDir}/{Safe(original.name)}__{Safe(stem)}.mat";
            // the asset's name = its file name (C5: 'Main Object Name ... does not match filename' with the old "_R1" names)
            var mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            mat.SetTexture("_BaseMap", baseMap);
            if (bump != null) mat.SetTexture("_BumpMap", bump);
            if (mask != null) mat.SetTexture("_MaskMap", mask);
            // creature atlases (texpipe kind 'creature': fur, scales, chitin, Docs/remaster-characters.md 6.6) get no skin pores
            bool creature = info?.material_kinds != null && info.material_kinds.Any(x => string.Equals(x.kind, "creature", StringComparison.OrdinalIgnoreCase));
            if (pores != null && !creature && (kind == RemasterMaterialKind.Skin || kind == RemasterMaterialKind.Armour))
            {
                mat.SetTexture("_DetailNormalMap", pores);
                float t = kind == RemasterMaterialKind.Skin ? HeadPoreTiling : BodyPoreTiling;
                mat.SetTextureScale("_DetailNormalMap", new Vector2(t, t));
            }
            if (original.HasProperty("_BaseColor") && kind != RemasterMaterialKind.Hair)
            {
                var c = original.GetColor("_BaseColor");
                mat.SetColor("_BaseColor", new Color(c.r, c.g, c.b, 1));
            }
            if (cutout && original.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", original.GetFloat("_Cutoff"));
            FlipRows(original, mat);
            mat.enableInstancing = original.enableInstancing;
            // FACES v6 (C3): FACES head sets (facetex.py face.json) get the Blender-matched skin profile, others R1's own
            if (kind == RemasterMaterialKind.Skin)
                ApplySkinProfile(mat, RemasterSkinProfile.For(File.Exists(Path.Combine(RemasterTextures.SetDir(project, stem), RemasterSkinProfile.FaceInfo))));

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null) { AssetDatabase.CreateAsset(mat, path); existing = mat; }
            else
            {
                existing.shader = shader;
                existing.CopyPropertiesFromMaterial(mat);
                existing.name = mat.name;
                EditorUtility.SetDirty(existing);
            }
            imp?.Log.Add($"remaster: {original.name} -> {shader.name} ({stem}{(bump == null ? ", no normal" : "")})");
            return existing;
        }

        // -------------------------------------------------------------------------------------------------------------
        // FACES generated materials (no G3 original): eyes, cornea shell, hair cards and cap (RemasterGenerated)

        /// <summary>
        /// The material for a slot the face pipeline generated (<see cref="RemasterGenerated"/>), or null when the name is
        /// not a generated one or its texture set is missing. Eye: G3/Remaster/Eye on the generated eye texture, its own
        /// cornea lobe off (the cornea shell carries the gloss, with the right bulge). Cornea: G3/Remaster/Cornea (clear,
        /// specular only). Hair cards: G3/Remaster/Hair, alpha-tested (dithered), strands along V. Hair cap: the same
        /// shader on an opaque texture, matte. Saved as <c>Materials/Remaster/R1/&lt;name&gt;.mat</c>.
        /// </summary>
        public static Material Generated(G3CharacterImporter imp, string slotName, int materialSwitch = 0)
        {
            var kind = RemasterGenerated.Classify(slotName);
            if (kind == RemasterGeneratedKind.None) return null;
            if (RemasterGenerated.IsV2(kind)) return GeneratedV2(imp, slotName, kind);
            string project = G3RemasterOverride.ProjectDir;
            // v6: per-switch hair tint (shared hair meshes, NPCs with other switches)
            string set = RemasterGenerated.SwitchTextureSet(slotName, materialSwitch,
                s => RemasterTextures.Find(project, s).ContainsKey(RemasterTextures.BaseMap));
            string name = set ?? RemasterGenerated.BaseName(slotName);
            var shader = Shader.Find(RemasterGenerated.ShaderName(kind));
            if (shader == null) { imp?.Log.Add($"remaster: shader {RemasterGenerated.ShaderName(kind)} missing, {name} not built"); return null; }
            Texture2D baseMap = null;
            if (set != null)
            {
                var maps = RemasterTextures.Find(project, set);
                if (!maps.TryGetValue(RemasterTextures.BaseMap, out var bp))
                {
                    imp?.Log.Add($"remaster: {name}: texture set {set} missing (run Tools/Blender/remaster/facerun.py)");
                    return null;
                }
                baseMap = ImportMap(set, bp, MapUse.Base, RemasterGenerated.IsCutout(kind));
            }

            string path = $"{MaterialDir}/{Safe(name)}.mat";
            var mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) }; // = file name (C5)
            if (baseMap != null)
            {
                mat.SetTexture("_BaseMap", baseMap);
                mat.SetTextureScale("_BaseMap", new Vector2(1, -1)); // generated sets are stored like the R1 sets: rows flipped
                mat.SetTextureOffset("_BaseMap", new Vector2(0, 1));
            }
            switch (kind)
            {
                case RemasterGeneratedKind.Eye:
                    mat.SetFloat("_CorneaStrength", 0f);
                    mat.SetFloat("_Smoothness", 0.5f);
                    break;
                case RemasterGeneratedKind.HairCards:
                    mat.SetFloat("_Cutoff", 0.5f);
                    mat.SetFloat("_StrandAxis", 1f);
                    mat.SetFloat("_Smoothness", 0.4f);
                    break;
                case RemasterGeneratedKind.HairCap:
                    mat.SetFloat("_Cutoff", RemasterHairLook.DefaultCapCutoff); // the set's alpha_cut: ApplyGeneratedLook
                    mat.SetFloat("_DitherRange", 0f);
                    mat.SetFloat("_Smoothness", 0.15f);
                    mat.SetColor("_HairSpecColor1", new Color(0.02f, 0.02f, 0.02f, 1));
                    mat.SetColor("_HairSpecColor2", new Color(0.05f, 0.05f, 0.05f, 1));
                    break;
            }

            if (set != null) ApplyGeneratedLook(imp, mat, kind, set, project);

            Directory.CreateDirectory(MaterialDir);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null) { AssetDatabase.CreateAsset(mat, path); existing = mat; }
            else
            {
                existing.shader = shader;
                existing.CopyPropertiesFromMaterial(mat);
                existing.name = mat.name;
                EditorUtility.SetDirty(existing);
            }
            // C2: alpha-tested cards (hair, lashes) cast no shadow-map shadows (RemasterHairLook.CastsShadows)
            existing.SetShaderPassEnabled("ShadowCaster", RemasterHairLook.CastsShadows(kind));
            imp?.Log.Add($"remaster: generated {name} -> {shader.name}{(set != null ? $" ({set})" : "")}");
            return existing;
        }

        // -------------------------------------------------------------------------------------------------------------
        // v2 hero showcase (RemasterGenerated v2 kinds, RemasterV2Look)

        public const string V2TextureDir = G3CharacterImporter.Root + "/Textures/Remaster/V2";
        public const string SkinLutFile = "G3SkinPreintegratedLut.png";

        [Serializable] sealed class EyeV2Info { public float radius = -1; public float iris = -1; public float cornea = -1; }

        /// <summary>
        /// A v2 material: its shader (G3/Remaster/V2/{Skin,Eye,Hair,EyeOcclusion}, Cornea for the tear film, Armour for
        /// outfit surfaces), the set's BaseMap/BumpMap/MaskMap (stored like the other generated sets: rows flipped), the
        /// pre-integrated skin LUT and pores for skin, and the <see cref="RemasterV2Look"/> parameters.
        /// </summary>
        static Material GeneratedV2(G3CharacterImporter imp, string slotName, RemasterGeneratedKind kind)
        {
            string project = G3RemasterOverride.ProjectDir;
            string set = RemasterGenerated.TextureSet(slotName);
            string name = set ?? RemasterGenerated.BaseName(slotName);
            string shaderName = RemasterGenerated.ShaderName(kind);
            var shader = Shader.Find(shaderName);
            if (shader == null) { imp?.Log.Add($"remaster: shader {shaderName} missing, {name} not built"); return null; }
            var mat = new Material(shader) { name = name };
            if (set != null)
            {
                var maps = RemasterTextures.Find(project, set);
                if (!maps.TryGetValue(RemasterTextures.BaseMap, out var bp))
                {
                    imp?.Log.Add($"remaster: {name}: texture set {set} missing (run Tools/Blender/remaster/herov2.py)");
                    return null;
                }
                mat.SetTexture("_BaseMap", ImportMap(set, bp, MapUse.Base, RemasterGenerated.IsCutout(kind)));
                if (maps.TryGetValue(RemasterTextures.BumpMap, out var np)) mat.SetTexture("_BumpMap", ImportMap(set, np, MapUse.Normal, false));
                if (maps.TryGetValue(RemasterTextures.MaskMap, out var mp)) mat.SetTexture("_MaskMap", ImportMap(set, mp, MapUse.Mask, false));
                if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null) mat.EnableKeyword("_NORMALMAP");
                mat.SetTextureScale("_BaseMap", new Vector2(1, -1)); // generated sets: rows flipped like the R1 sets
                mat.SetTextureOffset("_BaseMap", new Vector2(0, 1));
            }
            bool bodySkin = kind == RemasterGeneratedKind.SkinV2 && RemasterV2Look.IsBodySkin(set);
            bool hairShell = kind == RemasterGeneratedKind.HairV2 && RemasterV2Look.IsHairShell(set);
            foreach (var kv in RemasterV2Look.Floats(kind, bodySkin, hairShell))
                if (mat.HasProperty(kv.Key)) mat.SetFloat(kv.Key, kv.Value);
            foreach (var kv in RemasterV2Look.Colors(kind))
                if (mat.HasProperty(kv.Key)) mat.SetColor(kv.Key, new Color(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
            switch (kind)
            {
                case RemasterGeneratedKind.SkinV2:
                {
                    var lut = SkinLutTexture();
                    if (lut != null) mat.SetTexture("_SkinLut", lut);
                    var pores = PoreTexture(project);
                    if (pores != null)
                    {
                        mat.SetTexture("_DetailNormalMap", pores);
                        // finer than R1's tiling: the 4096 maps already hold the pores, this is the micro relief on top
                        float t = bodySkin ? BodyPoreTiling * 1.5f : HeadPoreTiling * 3f;
                        mat.SetTextureScale("_DetailNormalMap", new Vector2(t, t));
                    }
                    break;
                }
                case RemasterGeneratedKind.EyeV2:
                {
                    var info = ReadJson<EyeV2Info>(Path.Combine(RemasterTextures.SetDir(project, set), "eye_v2.json"));
                    mat.SetFloat("_EyeRadius", RemasterV2Look.EyeRadius(info != null && info.radius > 0 ? info.radius : (float?)null));
                    float iris = RemasterV2Look.IrisRadius(info != null && info.iris > 0 ? info.iris : (float?)null);
                    mat.SetFloat("_IrisRadius", iris);
                    mat.SetFloat("_CorneaRadius", RemasterV2Look.CorneaRadius(info != null && info.cornea > 0 ? info.cornea : (float?)null, iris));
                    break;
                }
            }

            Directory.CreateDirectory(MaterialDir);
            string path = $"{MaterialDir}/{Safe(name)}.mat";
            mat.name = Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null) { AssetDatabase.CreateAsset(mat, path); existing = mat; }
            else
            {
                existing.shader = shader;
                existing.CopyPropertiesFromMaterial(mat);
                existing.shaderKeywords = mat.shaderKeywords;
                existing.name = mat.name;
                EditorUtility.SetDirty(existing);
            }
            existing.renderQueue = -1; // the shader's queue (hair: AlphaTest+20, occlusion shell: Transparent-10)
            existing.SetShaderPassEnabled("ShadowCaster", RemasterHairLook.CastsShadows(kind));
            imp?.Log.Add($"remaster: v2 {name} -> {shader.name}{(set != null ? $" ({set})" : "")}");
            return existing;
        }

        /// <summary>The pre-integrated skin LUT (<see cref="RemasterSkinLut"/>), written once (no game data: pure maths).</summary>
        static Texture2D SkinLutTexture()
        {
            Directory.CreateDirectory(V2TextureDir);
            string path = $"{V2TextureDir}/{SkinLutFile}";
            if (!File.Exists(path))
            {
                var table = RemasterSkinLut.Compute(RemasterV2Look.LutWidth, RemasterV2Look.LutHeight);
                var png = G3.Formats.Image.Png.Encode(RemasterSkinLut.ToRgba(table, RemasterV2Look.LutWidth, RemasterV2Look.LutHeight),
                                                      RemasterV2Look.LutWidth, RemasterV2Look.LutHeight);
                File.WriteAllBytes(path, png);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            if (AssetImporter.GetAtPath(path) is TextureImporter ti &&
                (ti.sRGBTexture || ti.mipmapEnabled || ti.wrapMode != TextureWrapMode.Clamp || ti.textureCompression != TextureImporterCompression.Uncompressed))
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false; // stored as sqrt(linear); the shader squares
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.filterMode = FilterMode.Bilinear;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static T ReadJson<T>(string path) where T : class
        {
            try { return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Skin parameters of G3/Remaster/Skin (<see cref="RemasterSkinProfile"/>).</summary>
        static void ApplySkinProfile(Material m, RemasterSkinProfile p)
        {
            m.SetFloat("_BumpScale", p.BumpScale);
            m.SetFloat("_DetailNormalScale", p.DetailNormalScale);
            m.SetFloat("_Smoothness", p.Smoothness);
            m.SetFloat("_SkinPrimaryLobe", p.PrimaryLobe);
            m.SetFloat("_SecondLobeSmoothness", p.SecondLobeSmoothness);
            m.SetFloat("_SkinF0", p.SkinF0);
            m.SetFloat("_Wrap", p.Wrap);
            m.SetFloat("_ScatterSaturation", p.ScatterSaturation);
            m.SetFloat("_SkinAmbientWarmth", p.AmbientWarmth);
            m.SetFloat("_SkinEnvSheen", p.EnvSheen);
        }

        /// <summary>
        /// FACES v6 in-engine look of a generated material (<see cref="RemasterEyeLook"/>, <see cref="RemasterHairLook"/>).
        /// C1 eyes: albedo gain from the texture's own sclera, shadow sampled at the lids, little SSAO. C2 cap: gain
        /// towards its cards, the set's own alpha cut.
        /// </summary>
        static void ApplyGeneratedLook(G3CharacterImporter imp, Material mat, RemasterGeneratedKind kind, string set, string project)
        {
            var maps = RemasterTextures.Find(project, set);
            if (!maps.TryGetValue(RemasterTextures.BaseMap, out var basePng)) return;
            switch (kind)
            {
                case RemasterGeneratedKind.Eye:
                {
                    float sclera = TextureStat(basePng, "sclera", RemasterEyeLook.ScleraLuminance);
                    float gain = RemasterEyeLook.AlbedoGain(sclera);
                    mat.SetFloat("_AlbedoGain", gain);
                    mat.SetFloat("_ShadowSampleOffset", RemasterEyeLook.ShadowSampleOffset);
                    mat.SetFloat("_SsaoStrength", RemasterEyeLook.SsaoStrength);
                    imp?.Log.Add($"remaster: {set}: sclera {sclera:0.000} -> albedo x{gain:0.00}");
                    break;
                }
                case RemasterGeneratedKind.HairCap:
                {
                    var info = ReadInfo(Path.Combine(RemasterTextures.SetDir(project, set), RemasterTextures.Info));
                    mat.SetFloat("_Cutoff", RemasterHairLook.CapCutoff(info != null && info.alpha_cut > 0 ? info.alpha_cut : (float?)null));
                    string cards = RemasterHairLook.CardsSetOfCap(set);
                    float capLum = TextureStat(basePng, "median", MedianOpaque);
                    float cardsLum = cards != null && RemasterTextures.Find(project, cards).TryGetValue(RemasterTextures.BaseMap, out var cardsPng)
                        ? TextureStat(cardsPng, "median", MedianOpaque) : -1;
                    float gain = RemasterHairLook.CapGain(capLum, cardsLum);
                    mat.SetFloat("_AlbedoGain", gain);
                    imp?.Log.Add($"remaster: {set}: cap {capLum:0.000} / cards {cardsLum:0.000} -> albedo x{gain:0.00}");
                    break;
                }
            }
        }

        static float MedianOpaque(byte[] rgba, int w, int h) => RemasterHairLook.MedianLuminance(rgba, w, h, 0.5f);

        static readonly Dictionary<string, (DateTime time, float value)> Stats = new Dictionary<string, (DateTime, float)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A statistic of a source PNG's RGBA texels (cached by file time); -1 when unreadable.</summary>
        static float TextureStat(string png, string what, Func<byte[], int, int, float> stat)
        {
            var time = File.GetLastWriteTimeUtc(png);
            string key = what + "|" + png;
            if (Stats.TryGetValue(key, out var c) && c.time == time) return c.value;
            float v = -1;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (t.LoadImage(File.ReadAllBytes(png), false))
                {
                    var px = t.GetPixels32();
                    var rgba = new byte[px.Length * 4];
                    for (int i = 0; i < px.Length; i++)
                    {
                        rgba[i * 4] = px[i].r; rgba[i * 4 + 1] = px[i].g; rgba[i * 4 + 2] = px[i].b; rgba[i * 4 + 3] = px[i].a;
                    }
                    v = stat(rgba, t.width, t.height);
                }
            }
            catch (Exception) { v = -1; }
            finally { UnityEngine.Object.DestroyImmediate(t); }
            Stats[key] = (time, v);
            return v;
        }

        /// <summary>
        /// Row order: G3 textures are uploaded raw (<c>LoadRawTextureData</c>, first DDS row at v = 0) and the meshes keep
        /// G3's Direct3D UVs (Docs/formats-mesh-material-texture.md, UVs). The pipeline's PNGs are written top row first,
        /// and Unity's PNG import puts the top row at v = 1, so the maps would sit upside down on the UVs. One shared
        /// flip of the sampling coordinate (all maps use <c>_BaseMap_ST</c>: v' = 1 - v, composed with the original's
        /// tiling) puts every texel back; the normal maps keep their channels (same texel, same tangent frame).
        /// Found in R2: the glb exports (texture read-back + flipped glTF v) are row-flipped against the R1 PNGs.
        /// </summary>
        static void FlipRows(Material original, Material mat)
        {
            Vector2 scale = Vector2.one, offset = Vector2.zero;
            if (original.HasProperty("_BaseMap"))
            {
                scale = original.GetTextureScale("_BaseMap");
                offset = original.GetTextureOffset("_BaseMap");
            }
            mat.SetTextureScale("_BaseMap", new Vector2(scale.x, -scale.y));
            mat.SetTextureOffset("_BaseMap", new Vector2(offset.x, 1f - offset.y));
        }

        static bool IsCutout(Material m) =>
            m.IsKeywordEnabled("_ALPHATEST_ON") || (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f);

        static RemasterMaterialKind KindOf(SetInfo info, string materialName, string stem)
        {
            var e = info?.material_kinds?.FirstOrDefault(x => string.Equals(x.material, materialName, StringComparison.OrdinalIgnoreCase));
            if (e != null && RemasterMaterialKinds.TryParse(e.kind, out var k)) return k;
            return RemasterMaterialKinds.Classify(materialName, stem);
        }

        static SetInfo ReadInfo(string path)
        {
            try { return File.Exists(path) ? JsonUtility.FromJson<SetInfo>(File.ReadAllText(path)) : null; }
            catch (Exception) { return null; }
        }

        static string AssetPathName(Texture t)
        {
            string p = AssetDatabase.GetAssetPath(t);
            return string.IsNullOrEmpty(p) ? t.name : Path.GetFileNameWithoutExtension(p);
        }

        // -------------------------------------------------------------------------------------------------------------
        // textures: copied from GameData into Assets (git-ignored) and imported with the right settings

        enum MapUse { Base, Normal, Mask, Detail }

        static Texture2D PoreTexture(string project)
        {
            string src = Path.Combine(RemasterTextures.Dir(project), SharedDir, PoreMap);
            return File.Exists(src) ? ImportMap(SharedDir, src, MapUse.Detail, false) : null;
        }

        static Texture2D ImportMap(string stem, string source, MapUse use, bool cutout)
        {
            string dir = $"{TextureDir}/{Safe(stem)}";
            Directory.CreateDirectory(dir);
            string path = $"{dir}/{Path.GetFileName(source)}";
            bool copy = !File.Exists(path) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(path) ||
                        new FileInfo(source).Length != new FileInfo(path).Length;
            if (copy)
            {
                File.Copy(source, path, true);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            if (AssetImporter.GetAtPath(path) is TextureImporter ti && Configure(ti, use, cutout)) ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Importer settings per map; true when something changed.</summary>
        static bool Configure(TextureImporter ti, MapUse use, bool cutout)
        {
            var type = use == MapUse.Normal || use == MapUse.Detail ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool srgb = use == MapUse.Base;
            int max = use == MapUse.Detail ? 512 : 4096;
            bool dirty = ti.textureType != type || ti.sRGBTexture != srgb || ti.maxTextureSize != max || ti.anisoLevel != 8 ||
                         ti.textureCompression != TextureImporterCompression.CompressedHQ || ti.mipmapEnabled == false ||
                         ti.alphaIsTransparency != (use == MapUse.Base && cutout) || ti.mipMapsPreserveCoverage != (use == MapUse.Base && cutout) ||
                         (use == MapUse.Detail && ti.wrapMode != TextureWrapMode.Repeat);
            if (!dirty) return false;
            ti.textureType = type;
            ti.sRGBTexture = srgb;
            ti.maxTextureSize = max;
            ti.anisoLevel = 8;
            ti.mipmapEnabled = true;
            ti.textureCompression = TextureImporterCompression.CompressedHQ; // BC7 colour/mask, BC5 normals on desktop
            ti.alphaIsTransparency = use == MapUse.Base && cutout;
            ti.mipMapsPreserveCoverage = use == MapUse.Base && cutout; // alpha-tested hair keeps its density in the distance
            ti.alphaTestReferenceValue = 0.5f;
            if (use == MapUse.Detail) ti.wrapMode = TextureWrapMode.Repeat;
            return true;
        }

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace('|', '_');
        }
    }
}
