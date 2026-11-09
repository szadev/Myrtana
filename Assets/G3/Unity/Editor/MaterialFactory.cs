using System;
using G3.Formats.Material;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Rendering;
using BlendMode = G3.Formats.Material.BlendMode;

namespace G3.Unity.Editor
{
    /// <summary>Render-pipeline specific material creation, so the importer itself stays pipeline-agnostic.</summary>
    public interface IMaterialFactory
    {
        /// <param name="loadTexture">Loads the texture for a slot; bool = normal map.</param>
        Material Create(MaterialResource src, Func<TextureSlot, bool, Texture2D> loadTexture);
    }

    public sealed class UrpMaterialFactory : IMaterialFactory
    {
        readonly Shader _lit = Shader.Find("Universal Render Pipeline/Lit");
        readonly Shader _simpleLit = Shader.Find("Universal Render Pipeline/Simple Lit");
        readonly Shader _blend = Shader.Find("G3/LitBlend");
        readonly Shader _barrier = Shader.Find(G3.Formats.World.MagicBarrier.ShaderName);

        // ---- specular (Docs/formats-mesh-material-texture.md §4.6) ------------------------------------------------
        // Genome (ge_default.fx, CP 1.75) adds per light  colSpecular · saturate(normalised Phong) · mask  and has no
        // ambient/environment specular at all. URP Lit always adds an ambient specular term (_GlossyEnvironmentColor ·
        // Fresnel, even with environment reflections off) that turns bumpy stone and wood white in the shade. URP
        // Simple Lit (Blinn-Phong) has the same structure as Genome: light · specColour · pow(N·H, exp), no ambient
        // specular. So: Simple Lit, _SpecGlossMap/_SpecColor = the G3 specular mask, smoothness from SpecularPower.

        /// <summary>Mask used where G3 enables specular but leaves the Specular slot unset (Genome: 1, i.e. white).</summary>
        public const float UnmaskedSpecular = 0.5f;

        /// <summary>
        /// Blinn-Phong smoothness for a G3 SpecularPower p: Phong exponent n = 128·p; URP uses exp2(10·s + 1) on N·H.
        /// Genome's normalised, saturated Phong lobe has a flat top about as wide as pow(N·H, n), so s = (log2 n − 1)/10.
        /// </summary>
        public static float Smoothness(float power) =>
            Mathf.Clamp01((Mathf.Log(Mathf.Max(2f, 128f * power), 2f) - 1f) / 10f);

        /// <summary>
        /// Linear-space mask colour (+ optional map) for a G3 specular mask. Constants are applied as-is to the linear
        /// texture (G3 adds them in gamma space; with the ~2× Unity sun intensity this lands at a similar brightness).
        /// </summary>
        static Color MaskColor(SpecularMask mask, Func<TextureSlot, bool, Texture2D> loadTexture, out Texture2D map)
        {
            map = null;
            if (mask == null) return new Color(UnmaskedSpecular, UnmaskedSpecular, UnmaskedSpecular);
            var c = new Color(mask.Color[0], mask.Color[1], mask.Color[2]);
            if (mask.Map != null)
            {
                var tex = loadTexture(mask.Map, false);
                // Simple Lit multiplies the map's rgb; a mask taken from a single channel (.r/.g/.b of a grey-ish
                // diffuse) is close to that, an alpha-channel mask is not → use its mean instead.
                if (tex != null)
                {
                    if (mask.MapComponent == ColorComponent.A) c *= Mean(tex).a;
                    else map = tex;
                }
            }
            foreach (var extra in mask.ExtraMaps)
            {
                var t = loadTexture(extra, false);
                if (t != null) { var m = Mean(t); c *= (m.r + m.g + m.b) / 3f; }
            }
            c.a = 1;
            return c;
        }

        /// <summary>Mean colour of a texture's smallest mip (stored values); mid-grey if unreadable.</summary>
        static Color Mean(Texture2D tex)
        {
            try
            {
                var px = tex.GetPixels(tex.mipmapCount - 1);
                var sum = Color.clear;
                foreach (var p in px) sum += p;
                return px.Length > 0 ? sum / px.Length : new Color(0.5f, 0.5f, 0.5f, 1);
            }
            catch (Exception) { return new Color(0.5f, 0.5f, 0.5f, 1); }
        }

        /// <summary>Material colour properties are sRGB and linearised by Unity; pass the gamma value of a linear colour.</summary>
        static Color ShaderLinear(Color linear, float alpha)
        {
            var g = linear.gamma;
            g.a = alpha;
            return g;
        }

        public Material Create(MaterialResource src, Func<TextureSlot, bool, Texture2D> loadTexture)
        {
            var diffuse = src.Diffuse != null ? loadTexture(src.Diffuse, false) : null;
            var normal = src.Normal != null ? loadTexture(src.Normal, true) : null;
            var c = src.DiffuseColor;
            var tint = diffuse == null && c != null ? new Color(c[0], c[1], c[2]) : Color.white;
            float smoothness = Smoothness(src.SpecularPower);

            // fix-229: the Vengard dome (Brighten, glow in the SelfIllumination slot) as G3/MagicBarrier, not Simple Lit.
            if (G3.Formats.World.MagicBarrier.IsBarrierMaterial(src.Name) && _barrier != null)
                return CreateBarrier(src, loadTexture, diffuse);

            if (IsWater(src))
            {
                var wm = CreateWater(src, loadTexture, diffuse, normal);
                if (wm != null) return wm;
            }

            // Detail maps (Docs/formats-mesh-material-texture.md §4.7): a second map multiplied into a layer. URP Simple Lit
            // has no such slot, so an opaque one-layer material with a detail map also goes to G3/LitBlend (_SINGLE_LAYER).
            var detailA = src.DiffuseDetail != null ? loadTexture(src.DiffuseDetail, false) : null;
            var detailB = src.VertexAlphaBlend && src.DiffuseDetail2 != null ? loadTexture(src.DiffuseDetail2, false) : null;
            bool single = !src.VertexAlphaBlend && detailA != null && diffuse != null;

            if ((src.VertexAlphaBlend || single) && _blend != null && src.Blend == BlendMode.Normal)
            {
                var m = new Material(_blend) { name = src.Name };
                SetTex(m, "_BaseMap", diffuse, src.Diffuse);
                if (single)
                {
                    m.SetFloat("_SingleLayer", 1);
                    m.EnableKeyword("_SINGLE_LAYER");
                    if (src.DiffuseMulColor != null) tint = new Color(src.DiffuseMulColor[0], src.DiffuseMulColor[1], src.DiffuseMulColor[2]);
                }
                else SetTex(m, "_BaseMap2", src.Diffuse2 != null ? loadTexture(src.Diffuse2, false) : null, src.Diffuse2);
                // A Normal slot that is a single sampler (no vertex blend) applies to both layers in G3; a missing layer
                // map stays the flat default. Before, both maps were required and 18 layered materials lost their bump.
                bool normalShared = src.Normal != null && src.Normal2 == null;
                var n2 = src.Normal2 != null ? loadTexture(src.Normal2, true) : normalShared ? normal : null;
                SetTex(m, "_BumpMap", normal, src.Normal);
                SetTex(m, "_BumpMap2", n2, normalShared ? src.Normal : src.Normal2);
                m.SetColor("_BaseColor", tint);
                m.SetFloat("_Smoothness", smoothness);
                if (normal != null || (!single && n2 != null)) { m.SetFloat("_UseNormalMap", 1); m.EnableKeyword("_NORMALMAP"); }
                SetDetail(m, src, detailA, detailB);
                if (src.EnableSpecular)
                {
                    // Layer masks are almost always "layer diffuse · constant"; any map is approximated by the layer's albedo.
                    var specA = MaskColor(src.SpecularMask, loadTexture, out var mapA);
                    var maskB = src.SpecularMask2 ?? src.SpecularMask;
                    var specB = MaskColor(maskB, loadTexture, out var mapB);
                    m.SetColor("_SpecColor", ShaderLinear(specA, 1));
                    m.SetColor("_SpecColor2", ShaderLinear(specB, 1));
                    m.SetFloat("_SpecAlbedo", mapA != null ? 1 : 0);
                    m.SetFloat("_SpecAlbedo2", mapB != null ? 1 : 0);
                    m.SetFloat("_UseSpecular", 1);
                    m.EnableKeyword("_SPECULAR_COLOR");
                }
                return m;
            }

            if (IsWater(src))
            {
                // G3 water shaders missing: stand-in transparent water.
                var lit = new Material(_lit) { name = src.Name };
                lit.SetFloat("_Surface", 1);
                lit.SetFloat("_Blend", 0);
                lit.SetColor("_BaseColor", new Color(0.12f, 0.2f, 0.22f, 0.65f));
                lit.SetFloat("_Smoothness", 0.92f);
                if (normal != null) SetTex(lit, "_BumpMap", normal, src.Normal);
                BaseShaderGUI.SetMaterialKeywords(lit);
                return lit;
            }

            if (diffuse != null && src.DiffuseMulColor != null)
                tint = new Color(src.DiffuseMulColor[0], src.DiffuseMulColor[1], src.DiffuseMulColor[2]);   // gamma value, as G3
            var mat = new Material(_simpleLit) { name = src.Name };
            SetTex(mat, "_BaseMap", diffuse, src.Diffuse);
            mat.SetColor("_BaseColor", tint);
            if (normal != null) SetTex(mat, "_BumpMap", normal, src.Normal);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_SmoothnessSource", (float)SimpleLitGUI.SmoothnessMapChannel.SpecularAlpha);   // _SpecColor.a (× map alpha)
            if (src.EnableSpecular)
            {
                var spec = MaskColor(src.SpecularMask, loadTexture, out var map);
                mat.SetColor("_SpecColor", ShaderLinear(spec, smoothness));
                if (map != null) mat.SetTexture("_SpecGlossMap", map);
                mat.SetFloat("_SpecularHighlights", (float)SimpleLitGUI.SpecularSource.SpecularTextureAndColor);
            }
            else
            {
                mat.SetColor("_SpecColor", new Color(0, 0, 0, smoothness));
                mat.SetFloat("_SpecularHighlights", (float)SimpleLitGUI.SpecularSource.NoSpecular);
            }

            // fix-260: Genome adds the SelfIllumination colour unlit (fire golem lava veins). Simple Lit's emission samples
            // with the base map's UV transform; _EmissionColor white keeps the baked gamma colour (keywords set below).
            var glow = SelfIlluminationMap.ForUnlitEmission(src);
            var glowTex = glow != null ? loadTexture(glow.Slot, false) : null;
            if (glowTex != null)
            {
                mat.SetTexture("_EmissionMap", glowTex);
                mat.SetColor("_EmissionColor", Color.white);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }

            switch (src.Blend)
            {
                case BlendMode.Masked:
                    mat.SetFloat("_AlphaClip", 1);
                    mat.SetFloat("_Cutoff", src.MaskReference > 0 ? src.MaskReference / 255f : 0.5f);
                    // Keep back-face culling: Genome models two-sided cards (banners, leaves) as two quads with
                    // opposite normals; Cull Off would draw each back face with an inverted normal.
                    break;
                case BlendMode.AlphaBlend:
                case BlendMode.AlphaModulate:
                case BlendMode.Translucent:
                case BlendMode.Brighten:
                    mat.SetFloat("_Surface", 1);   // transparent
                    mat.SetFloat("_Blend", src.Blend == BlendMode.Brighten ? 2 : 0); // additive : alpha
                    break;
            }
            BaseShaderGUI.SetMaterialKeywords(mat, SimpleLitGUI.SetMaterialKeywords);
            return mat;
        }

        /// <summary>
        /// G3_Special_Magic_Barrier_01 (eCShaderDefault, Brighten): SelfIllumination G3_Special_Magic_Barrier_05 = the blue
        /// glow, Diffuse G3_Special_Magic_Barrier_04 = the swirls; opacity Add(04.r, 05). The shader keeps those terms and
        /// adds the drift, Fresnel and depth fade (Assets/G3/Unity/Shaders/Effects/G3MagicBarrier.shader). The maps tile
        /// in the shader (repeat sampler), so the slots' scales are left at 1.
        /// </summary>
        Material CreateBarrier(MaterialResource src, Func<TextureSlot, bool, Texture2D> loadTexture, Texture2D diffuse)
        {
            var m = new Material(_barrier) { name = src.Name };
            var glow = src.SelfIllumination != null ? loadTexture(src.SelfIllumination, false) : null;
            if (glow != null) m.SetTexture("_CloudMap", glow);
            if (diffuse != null) m.SetTexture("_StreakMap", diffuse);
            return m;
        }

        // ---- water -------------------------------------------------------------------------------------------------
        // Values decoded from the shipped eCShaderRiver / eCShaderOcean graphs (G3_Myrtana_Water_River_01_A,
        // G3_Myrtana_Water_Ocean_01_A): normal maps G3_Special_Water_01_Normal_01/02 (river) and _01/_04/_03 (ocean),
        // scrollers Direction (0,-1) Rate 0.2 / 0.1 (river) and (∓0.2, 1) Rate 0.1 (ocean), sampler scales 1 / 2,
        // water colour (0.325, 0.51, 0.494), ReflectionColor 0.4, ShoreFadingScale 75 / 50 (cm),
        // Depth{Red,Green,Blue}HalfLife 0.03 / 0.035 / 0.03.
        enum WaterKind { River, Sea, Still, Fall }

        readonly Shader _water = Shader.Find("G3/Water");
        readonly Shader _waterFall = Shader.Find("G3/WaterFall");

        static WaterKind Classify(MaterialResource src)
        {
            string n = src.Name ?? "";
            bool has(string k) => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
            if (has("Falls") && src.Blend == BlendMode.Brighten) return WaterKind.Fall;
            if (src.ShaderClass == "eCShaderOcean" || has("_Ocean_") || has("_Sea_")) return WaterKind.Sea;
            // river meshes (G3_*_Rivermesh_*): mesh V runs downstream, so the maps scroll along UV
            if (src.ShaderClass == "eCShaderRiver" || has("_Water_River_")) return WaterKind.River;
            return WaterKind.Still;   // lakes, ponds, barrels (G3_Nature_Water_River_01_A is a lake/barrel material)
        }

        Material CreateWater(MaterialResource src, Func<TextureSlot, bool, Texture2D> loadTexture, Texture2D diffuse, Texture2D normal)
        {
            var kind = Classify(src);
            Texture2D Load(string image, bool isNormal) => loadTexture(new TextureSlot { Image = image }, isNormal);

            if (kind == WaterKind.Fall)
            {
                if (_waterFall == null) return null;
                var f = new Material(_waterFall) { name = src.Name };
                var a = Load("G3_Special_Waterfall_01_Diffuse_01", false) ?? diffuse;
                var b = Load("G3_Special_Waterfall_02_Diffuse_01", false) ?? a;
                if (a != null) { f.SetTexture("_BaseMap", a); f.SetTextureScale("_BaseMap", new Vector2(0.6f, 0.4f)); }
                if (b != null) { f.SetTexture("_BaseMap2", b); f.SetTextureScale("_BaseMap2", new Vector2(0.2f, 0.1f)); }
                f.SetColor("_Tint", new Color(0.58f, 0.58f, 0.58f));
                f.SetVector("_FlowA", new Vector4(-0.02f, -0.9f, 0.55f, 0));
                f.SetVector("_FlowB", new Vector4(0.01f, -0.2f, 0.4f, 0));
                f.SetFloat("_VertexAlpha", 1);   // fall sheets carry their opacity in vertex alpha
                return f;
            }

            if (_water == null) return null;
            var m = new Material(_water) { name = src.Name };
            var n1 = Load("G3_Special_Water_01_Normal_01", true) ?? normal;
            // (the ocean graph also samples _Normal_04, but that map has visible tile seams in V)
            var n2 = Load(kind == WaterKind.Sea ? "G3_Special_Water_01_Normal_03" : "G3_Special_Water_01_Normal_02", true) ?? n1;
            if (n1 != null) m.SetTexture("_NormalA", n1);
            if (n2 != null) m.SetTexture("_NormalB", n2);
            m.renderQueue = (int)RenderQueue.Transparent - 10;
            m.SetFloat("_SSR", 1);
            m.EnableKeyword("_G3WATER_SSR");
            m.SetFloat("_ReflectionStrength", 0.85f);
            m.SetFloat("_ProbeBlend", 0.35f);   // the baked default probe does not follow the time of day

            switch (kind)
            {
                case WaterKind.River:
                    m.SetFloat("_WorldUV", 0);
                    m.SetFloat("_TilingA", 1f);
                    m.SetFloat("_TilingB", 2f);
                    m.SetVector("_FlowA", new Vector4(0.05f, -1f, 0.07f, 0));   // G3 rate 0.2 read as too fast in Unity units
                    m.SetVector("_FlowB", new Vector4(-0.1f, -1f, 0.045f, 0));
                    m.SetFloat("_NormalStrength", 0.55f);
                    m.SetColor("_ScatterColor", new Color(0.035f, 0.085f, 0.075f));
                    m.SetVector("_Absorption", new Vector4(0.55f, 0.22f, 0.3f, 0));
                    m.SetFloat("_ShoreFade", 0.5f);
                    m.SetFloat("_FoamWidth", 0.35f);
                    m.SetFloat("_FoamAmount", 0.4f);
                    break;
                case WaterKind.Sea:
                    m.SetFloat("_WorldUV", 1);
                    m.SetFloat("_TilingA", 22f);   // metres per tile
                    m.SetFloat("_TilingB", 9f);
                    m.SetVector("_FlowA", new Vector4(-0.2f, 1f, 0.025f, 0));
                    m.SetVector("_FlowB", new Vector4(0.2f, 1f, 0.035f, 0));
                    m.SetFloat("_NormalStrength", 0.5f);
                    m.SetColor("_ScatterColor", new Color(0.02f, 0.07f, 0.09f));
                    m.SetVector("_Absorption", new Vector4(0.4f, 0.12f, 0.09f, 0));
                    m.SetFloat("_ShoreFade", 0.8f);
                    m.SetFloat("_FoamWidth", 0.6f);
                    m.SetFloat("_FoamAmount", 0.5f);
                    m.SetFloat("_Waves", 1);
                    m.EnableKeyword("_G3WATER_WAVES");
                    break;
                default:
                    m.SetFloat("_WorldUV", 1);
                    m.SetFloat("_TilingA", 9f);
                    m.SetFloat("_TilingB", 4f);
                    m.SetVector("_FlowA", new Vector4(0.7f, 0.7f, 0.008f, 0));
                    m.SetVector("_FlowB", new Vector4(-0.6f, 0.8f, 0.012f, 0));
                    m.SetFloat("_NormalStrength", 0.35f);
                    m.SetColor("_ScatterColor", new Color(0.03f, 0.07f, 0.06f));
                    m.SetVector("_Absorption", new Vector4(0.6f, 0.25f, 0.3f, 0));
                    m.SetFloat("_ShoreFade", 0.3f);
                    m.SetFloat("_FoamAmount", 0.15f);
                    break;
            }
            return m;
        }

        public static bool IsWater(MaterialResource src) =>
            src.ShaderClass == "eCShaderRiver" || src.ShaderClass == "eCShaderOcean" || src.ShaderClass == "eCShaderWater" ||
            (src.Name != null && (src.Name.IndexOf("_Water_", StringComparison.OrdinalIgnoreCase) >= 0));

        /// <summary>Helper meshes that the engine never draws as surfaces (river depth volumes, invisible blockers).</summary>
        public static bool IsInvisible(MaterialResource src) =>
            src.Blend == BlendMode.Invisible ||
            (src.Name != null && src.Name.IndexOf("_Depth_", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// _DETAILMAP: one map for the layers flagged in _DetailLayers (shared by both layers when G3 uses the same image
        /// and scale, the usual landscape case: one extra sample); _DETAILMAP2: a different map for layer B.
        /// </summary>
        static void SetDetail(Material m, MaterialResource src, Texture2D detailA, Texture2D detailB)
        {
            if (detailA == null && detailB == null) return;
            bool shared = detailA != null && detailB != null && detailA == detailB &&
                          src.DiffuseDetail.ScaleU == src.DiffuseDetail2.ScaleU && src.DiffuseDetail.ScaleV == src.DiffuseDetail2.ScaleV;
            var first = detailA ?? detailB;
            SetTex(m, "_DetailMap", first, detailA != null ? src.DiffuseDetail : src.DiffuseDetail2);
            m.SetVector("_DetailLayers", new Vector4(detailA != null ? 1 : 0, detailA == null || shared ? 1 : 0, 0, 0));
            m.SetFloat("_UseDetail", 1);
            m.EnableKeyword("_DETAILMAP");
            if (detailA != null && detailB != null && !shared)
            {
                SetTex(m, "_DetailMap2", detailB, src.DiffuseDetail2);
                m.SetFloat("_UseDetail2", 1);
                m.EnableKeyword("_DETAILMAP2");
            }
        }

        static void SetTex(Material m, string prop, Texture2D tex, TextureSlot slot)
        {
            if (tex == null) return;
            m.SetTexture(prop, tex);
            if (slot != null) m.SetTextureScale(prop, new Vector2(slot.ScaleU, slot.ScaleV));
        }
    }
}
