// World remaster blend (Docs/remaster-world.md): G3/LitBlend plus the remaster maps of each layer, used only by the
// override materials (G3WorldRemasterBuilder) when the world remaster switch is on; the originals keep G3/LitBlend.
//  * _MaskMap / _MaskMap2 (linear): G occlusion, B height, A smoothness (R unused/metal 0) from Tools/Remaster/worldtex.py,
//    enabled per layer by _UseMask / _UseMask2 (a layer without a remaster set keeps G3's flat defaults);
//  * height blend: the vertex-alpha weight of each layer is pushed by its height (pebbles and stones stay on top of
//    the grass at a transition instead of a soft lerp); _HeightBlend 0 = G3's linear lerp, _HeightDepth = band width;
//  * normal strength, occlusion strength; G3's detail map (macro variation) and Blinn-Phong lighting as LitBlend (no
//    environment specular: Genome has none), the specular mask scaled by the mask smoothness;
//  r1 (world-r1, gameplay distance 5-30 m):
//  * macro variation: two octaves of world-space value noise (~_MacroScale m) vary the albedo brightness/saturation by
//    +-_MacroStrength, breaking the 7 m tiling without changing the mean colour;
//  * anti-tiling: beyond _AntiTileStart m each layer is mixed (noise-weighted) with a second fetch at 0.37x scale,
//    rotated (same image, same mean colour);
//  * meso normal: a CC0 normal map projected in world XZ at _MacroNormalScale m (default 6 m) adds relief that survives
//    mipmapping at 5-30 m (the baked CC0 detail at 2 m repeat averages out there);
//  * triplanar on steep faces (_Triplanar > 0): where 1 - n.y > _SteepStart the layers are sampled on the world X/Z planes
//    (no stretched planar UVs on cliffs) and the normal comes from the CC0 cliff normal (_SteepNormal, whiteout);
//  all of it uniform-branch controlled per material (no new variants); fetches: 10 on flat ground, +6 on steep pixels.
Shader "G3/Remaster/WorldBlend"
{
    Properties
    {
        _BaseMap("Layer A", 2D) = "white" {}
        _BaseMap2("Layer B", 2D) = "white" {}
        [Normal] _BumpMap("Normal A", 2D) = "bump" {}
        [Normal] _BumpMap2("Normal B", 2D) = "bump" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        [Toggle(_DETAILMAP)] _UseDetail("Detail map (multiplied)", Float) = 0
        _DetailMap("Detail A (or shared)", 2D) = "white" {}
        _DetailLayers("Detail on layer A, B (0/1)", Vector) = (1,1,0,0)
        [Toggle(_DETAILMAP2)] _UseDetail2("Separate detail map for layer B", Float) = 0
        _DetailMap2("Detail B", 2D) = "white" {}
        [Toggle(_SINGLE_LAYER)] _SingleLayer("Single layer (no vertex blend)", Float) = 0
        _Smoothness("Smoothness (Blinn-Phong exponent)", Range(0,1)) = 0.5
        [Toggle(_SPECULAR_COLOR)] _UseSpecular("Specular highlights", Float) = 0
        _SpecColor("Specular mask A", Color) = (0.2,0.2,0.2,1)
        _SpecColor2("Specular mask B", Color) = (0.2,0.2,0.2,1)
        [ToggleUI] _SpecAlbedo("Mask A × layer A albedo", Float) = 1
        [ToggleUI] _SpecAlbedo2("Mask B × layer B albedo", Float) = 1
        [Toggle(_NORMALMAP)] _UseNormalMap("Use normal maps", Float) = 0
        [Toggle] _SwapLayers("Swap layers (debug)", Float) = 0
        [NoScaleOffset] _MaskMap("Mask A (G occlusion, B height, A smoothness)", 2D) = "white" {}
        [NoScaleOffset] _MaskMap2("Mask B", 2D) = "white" {}
        [ToggleUI] _UseMask("Layer A has a mask", Float) = 0
        [ToggleUI] _UseMask2("Layer B has a mask", Float) = 0
        _HeightBlend("Height blend (0 = G3 linear)", Range(0, 1)) = 1
        _HeightDepth("Height blend band", Range(0.01, 1)) = 0.2
        _NormalStrength("Normal strength", Range(0, 2)) = 1
        _OcclusionStrength("Occlusion strength", Range(0, 1)) = 1
        [Header(Remaster r1 gameplay distance)]
        _MacroStrength("Macro variation (brightness +-)", Range(0, 0.3)) = 0.08
        _MacroScale("Macro noise size (m)", Float) = 37
        _AntiTile("Anti-tiling at distance", Range(0, 1)) = 0.5
        _AntiTileStart("Anti-tiling start (m)", Float) = 8
        [Normal][NoScaleOffset] _MacroNormal("Meso normal (CC0, world XZ)", 2D) = "bump" {}
        _MacroNormalScale("Meso normal repeat (m)", Float) = 6
        _MacroNormalStrength("Meso normal strength", Range(0, 2)) = 0
        [Normal][NoScaleOffset] _SteepNormal("Cliff normal (CC0, world X/Z planes)", 2D) = "bump" {}
        _SteepScale("Cliff normal repeat (m)", Float) = 4
        _Triplanar("Triplanar on steep faces", Range(0, 1)) = 0
        _SteepStart("Steep from 1 - n.y", Range(0, 1)) = 0.35
        _MPerUv("Metres per UV unit (triplanar layer scale)", Float) = 1
        // Kept for material compatibility (the shadow/depth passes used to be borrowed from Lit through UsePass).
        [HideInInspector] _Cutoff("Cutoff", Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        // Shared by every pass: one UnityPerMaterial layout -> SRP Batcher and GPU Resident Drawer (DOTS instancing)
        // compatible (Docs/performance.md). The shadow/depth passes reproduce what the former
        // UsePass "Universal Render Pipeline/Lit/..." passes did for these materials: no alpha test, no normal map
        // (_BumpScale was undefined = 0), smoothness written for SSR, and Cull Off (the Lit passes use Cull[_Cull],
        // which these materials never define = 0 = Off).
        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3RemasterTune.hlsl"   // r2 global knobs (G3WorldLook.json)
            #include "Assets/G3/Unity/Shaders/Remaster/G3ReliefMorph.hlsl"    // fix-171: relief fine detail by the live distance (every pass)

            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BaseMap2); SAMPLER(sampler_BaseMap2);
            TEXTURE2D(_BumpMap);  SAMPLER(sampler_BumpMap);
            TEXTURE2D(_BumpMap2); SAMPLER(sampler_BumpMap2);
            TEXTURE2D(_DetailMap);  SAMPLER(sampler_DetailMap);
            TEXTURE2D(_DetailMap2); SAMPLER(sampler_DetailMap2);
            TEXTURE2D(_MaskMap);  SAMPLER(sampler_MaskMap);
            TEXTURE2D(_MaskMap2); SAMPLER(sampler_MaskMap2);
            TEXTURE2D(_MacroNormal); SAMPLER(sampler_MacroNormal);
            TEXTURE2D(_SteepNormal); SAMPLER(sampler_SteepNormal);
            half _G3DetailDisable;   // global, not per material
            half _G3DetailRaw;       // global: 1 = raw multiply (no mean normalisation)

            // 1 / luminance of a detail map's mean colour (the smallest mip of G3's own chain), 1 when _G3DetailRaw is set.
            half DetailGain(TEXTURE2D_PARAM(tex, smp))
            {
                half3 m = SAMPLE_TEXTURE2D_LOD(tex, smp, float2(0.5, 0.5), 15).rgb;
                half gain = 1.0 / max(dot(m, half3(0.2126, 0.7152, 0.0722)), 0.02);
                return lerp(gain, 1.0, _G3DetailRaw);
            }

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseMap2_ST;
                float4 _BumpMap_ST;
                float4 _BumpMap2_ST;
                float4 _DetailMap_ST;
                float4 _DetailMap2_ST;
                half4 _DetailLayers;
                half4 _BaseColor;
                half4 _SpecColor;
                half4 _SpecColor2;
                half _Smoothness;
                half _SpecAlbedo;
                half _SpecAlbedo2;
                half _SwapLayers;
                half _Cutoff;
                half _UseMask;
                half _UseMask2;
                half _HeightBlend;
                half _HeightDepth;
                half _NormalStrength;
                half _OcclusionStrength;
                half _MacroStrength;
                float _MacroScale;
                half _AntiTile;
                float _AntiTileStart;
                float _MacroNormalScale;
                half _MacroNormalStrength;
                float _SteepScale;
                half _Triplanar;
                half _SteepStart;
                float _MPerUv;
            CBUFFER_END

            // value noise 0..1 (hash of the lattice corners, smooth interpolation)
            float G3Hash12(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }
            float G3ValueNoise(float2 x)
            {
                float2 i = floor(x), f = frac(x);
                float2 u = f * f * (3 - 2 * f);
                return lerp(lerp(G3Hash12(i), G3Hash12(i + float2(1, 0)), u.x),
                            lerp(G3Hash12(i + float2(0, 1)), G3Hash12(i + float2(1, 1)), u.x), u.y);
            }
            // two octaves at _MacroScale and _MacroScale / 4.1: 0..1, mean 0.5
            float G3MacroNoise(float2 xz)
            {
                float2 p = xz / max(_MacroScale, 1);
                return G3ValueNoise(p) * 0.65 + G3ValueNoise(p * 4.1 + 17.3) * 0.35;
            }
            // albedo with the macro variation: brightness and a little saturation, mean preserved
            half3 G3Macro(half3 c, float n)
            {
                half v = (half)(n * 2 - 1);
                half ms = _MacroStrength * G3TUNE_MACRO;
                half l = dot(c, half3(0.2126h, 0.7152h, 0.0722h));
                c = lerp(l.xxx, c, 1 + v * 0.5h * ms);
                return c * (1 + v * ms);
            }
            // anti-tiling mix of fetch a with the 0.37x fetch b: a plain lerp averages two uncorrelated textures and halves
            // their contrast (r1 in-engine: rock and cobbles went smooth at 10-50 m); the variance-preserving form rescales
            // the mix around the texture mean (Heitz & Neyret 2018), blended in by G3TUNE_ANTICONTRAST.
            half3 G3AntiBlend(half3 a, half3 b, half w, half3 mean)
            {
                half3 m = lerp(a, b, w);
                half k = rsqrt(max(w * w + (1 - w) * (1 - w), 0.5h));
                return lerp(m, max(mean + (m - mean) * k, 0), saturate(G3TUNE_ANTICONTRAST));
            }
            // whiteout blend of a world-projected tangent-space normal sample t onto the world normal n
            // axis 0: plane ZY (x faces), 1: XZ (y faces), 2: XY (z faces)
            half3 G3PlaneNormal(half3 t, half3 n, int axis)
            {
                if (axis == 0) { half3 r = half3(t.xy + n.zy, abs(t.z) * n.x); return r.zyx; }
                if (axis == 1) { half3 r = half3(t.xy + n.xz, abs(t.z) * n.y); return r.xzy; }
                return half3(t.xy + n.xy, abs(t.z) * n.z);
            }

            // Weight of layer A from the vertex-alpha weight and both layer heights (0..1): classic height blend, the
            // layer whose height + weight is highest wins within a band of _HeightDepth; lerped with G3's linear weight.
            half HeightWeightA(half a, half hA, half hB)
            {
                half ta = hA + a, tb = hB + (1 - a);
                half m = max(ta, tb) - _HeightDepth;
                half wa = max(ta - m, 0), wb = max(tb - m, 0);
                half h = wa / max(wa + wb, 1e-4);
                // keep the ends exact: pure A or B stays pure
                h = (a >= 0.999h) ? 1 : (a <= 0.001h ? 0 : h);
                return lerp(a, h, _HeightBlend);
            }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _SPECULAR_COLOR
            #pragma shader_feature_local_fragment _DETAILMAP
            #pragma shader_feature_local_fragment _DETAILMAP2
            #pragma shader_feature_local _SINGLE_LAYER
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"


            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                float4 uv2        : TEXCOORD2;   // fix-171 relief morph (G3ReliefMorph.hlsl)
                float4 uv3        : TEXCOORD3;
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                half4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS    : TEXCOORD2;
                half4 tangentWS   : TEXCOORD3;
                half4 color       : COLOR;
                half fogFactor    : TEXCOORD4;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                G3ReliefMorph(v.positionOS.xyz, v.normalOS, v.uv2, v.uv3, v.uv4, v.uv5, v.uv6, true);
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = nrm.normalWS;
                o.tangentWS = half4(nrm.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv = v.uv;
                o.color = v.color;
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                OUTPUT_LIGHTMAP_UV(v.lightmapUV, unity_LightmapST, o.staticLightmapUV);
                OUTPUT_SH(o.normalWS, o.vertexSH);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 pw = i.positionWS;
                half3 nGeo = normalize(i.normalWS);
                float camDist = distance(pw, GetCameraPositionWS());
                float macroN = G3MacroNoise(pw.xz);
                // steep weight (triplanar) and the X/Z plane weights
                half steep = saturate(_Triplanar * G3TUNE_TRIPLANAR) * smoothstep(_SteepStart, _SteepStart + 0.2h, 1 - abs(nGeo.y));
                half2 wxz = abs(nGeo.xz); wxz *= wxz; wxz /= max(wxz.x + wxz.y, 1e-3h);
                // anti-tiling weight: second fetch at 0.37x, rotated, noise-weighted, only beyond _AntiTileStart
                half anti = saturate(_AntiTile * G3TUNE_ANTITILE) * saturate((camDist - _AntiTileStart) / max(_AntiTileStart, 1)) * saturate(macroN * 1.6 - 0.3);
                const float2x2 rot = float2x2(0.8, -0.6, 0.6, 0.8);
                float2 uvA = i.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                half4 maskA = lerp(half4(0, 1, 0.5, 0.5), SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uvA), _UseMask);
                half4 albedoA = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvA);
                [branch] if (anti > 0.01h)
                    albedoA.rgb = G3AntiBlend(albedoA.rgb, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, mul(rot, uvA) * 0.37 + 0.31).rgb, anti,
                                              SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, float2(0.5, 0.5), 15).rgb);
                // triplanar layer albedo on steep faces (world metres -> layer UV scale)
                [branch] if (steep > 0.01h)
                {
                    float2 stA = _BaseMap_ST.xy / max(_MPerUv, 1e-3);
                    half3 tx = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, pw.zy * stA).rgb;
                    half3 tz = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, pw.xy * stA).rgb;
                    albedoA.rgb = lerp(albedoA.rgb, tx * wxz.x + tz * wxz.y, steep);
                }
            #if defined(_SINGLE_LAYER)
                half a = 1;
                half4 albedoB = albedoA;
                half4 maskB = maskA;
                float2 uvB = uvA;
            #else
                half a = 1 - saturate(i.color.a);   // weight of layer A (src1): G3 lerp(src1, src2, VC.a)
                if (_SwapLayers > 0.5) a = 1 - a;
                float2 uvB = i.uv * _BaseMap2_ST.xy + _BaseMap2_ST.zw;
                half4 albedoB = SAMPLE_TEXTURE2D(_BaseMap2, sampler_BaseMap2, uvB);
                [branch] if (anti > 0.01h)
                    albedoB.rgb = G3AntiBlend(albedoB.rgb, SAMPLE_TEXTURE2D(_BaseMap2, sampler_BaseMap2, mul(rot, uvB) * 0.37 + 0.53).rgb, anti,
                                              SAMPLE_TEXTURE2D_LOD(_BaseMap2, sampler_BaseMap2, float2(0.5, 0.5), 15).rgb);
                [branch] if (steep > 0.01h)
                {
                    float2 stB = _BaseMap2_ST.xy / max(_MPerUv, 1e-3);
                    half3 tx = SAMPLE_TEXTURE2D(_BaseMap2, sampler_BaseMap2, pw.zy * stB).rgb;
                    half3 tz = SAMPLE_TEXTURE2D(_BaseMap2, sampler_BaseMap2, pw.xy * stB).rgb;
                    albedoB.rgb = lerp(albedoB.rgb, tx * wxz.x + tz * wxz.y, steep);
                }
                half4 maskB = lerp(half4(0, 1, 0.5, 0.5), SAMPLE_TEXTURE2D(_MaskMap2, sampler_MaskMap2, uvB), _UseMask2);
                a = HeightWeightA(a, maskA.b, maskB.b);
            #endif
                half3 colA = albedoA.rgb, colB = albedoB.rgb;
            #if defined(_DETAILMAP)
                {
                    half3 d = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, i.uv * _DetailMap_ST.xy + _DetailMap_ST.zw).rgb
                            * DetailGain(TEXTURE2D_ARGS(_DetailMap, sampler_DetailMap));
                    d = lerp(d, 1, _G3DetailDisable);
                    colA *= lerp(1, d, _DetailLayers.x);
                    colB *= lerp(1, d, _DetailLayers.y);
                }
            #endif
            #if defined(_DETAILMAP2) && !defined(_SINGLE_LAYER)
                colB *= lerp(SAMPLE_TEXTURE2D(_DetailMap2, sampler_DetailMap2, i.uv * _DetailMap2_ST.xy + _DetailMap2_ST.zw).rgb
                             * DetailGain(TEXTURE2D_ARGS(_DetailMap2, sampler_DetailMap2)), 1, _G3DetailDisable);
            #endif
                half3 albedo = G3Macro(lerp(colB, colA, a), macroN) * _BaseColor.rgb;

                InputData input = (InputData)0;
                input.positionWS = i.positionWS;
                input.positionCS = i.positionCS;
                half3 normalWS = nGeo;
            #if defined(_NORMALMAP)
                // remaster maps share their layer's colour UV (same texel grid); a layer without a set keeps G3's normal ST
                float2 nuvA = _UseMask > 0.5 ? uvA : i.uv * _BumpMap_ST.xy + _BumpMap_ST.zw;
                half3 nA = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, nuvA), _NormalStrength * G3TUNE_NORMAL);
            #if defined(_SINGLE_LAYER)
                half3 normalTS = normalize(nA);
            #else
                float2 nuvB = _UseMask2 > 0.5 ? uvB : i.uv * _BumpMap2_ST.xy + _BumpMap2_ST.zw;
                half3 nB = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap2, sampler_BumpMap2, nuvB), _NormalStrength * G3TUNE_NORMAL);
                half3 normalTS = normalize(lerp(nB, nA, a));
            #endif
                half3 bitangent = i.tangentWS.w * cross(i.normalWS, i.tangentWS.xyz);
                half3x3 tbn = half3x3(i.tangentWS.xyz, bitangent, i.normalWS);
                input.tangentToWorld = tbn;
                normalWS = normalize(TransformTangentToWorld(normalTS, tbn));
            #endif
                // meso normal (world XZ, full to G3TUNE_MESO_START, gone at _END: r1 20/60 m) and the cliff normal on steep faces
                half meso = _MacroNormalStrength * G3TUNE_MESO
                          * saturate((G3TUNE_MESO_END - camDist) / max(G3TUNE_MESO_END - G3TUNE_MESO_START, 1.0));
                [branch] if (meso > 0.01h)
                {
                    half3 t = UnpackNormalScale(SAMPLE_TEXTURE2D(_MacroNormal, sampler_MacroNormal, pw.xz / max(_MacroNormalScale, 0.1)), meso);
                    normalWS = normalize(lerp(normalWS, normalize(G3PlaneNormal(t, normalWS, 1)), 1 - steep));
                }
                [branch] if (steep > 0.01h)
                {
                    float sc = 1 / max(_SteepScale, 0.1);
                    half3 tx = UnpackNormal(SAMPLE_TEXTURE2D(_SteepNormal, sampler_SteepNormal, pw.zy * sc));
                    half3 tz = UnpackNormal(SAMPLE_TEXTURE2D(_SteepNormal, sampler_SteepNormal, pw.xy * sc));
                    half3 nt = normalize(G3PlaneNormal(tx, nGeo, 0) * wxz.x + G3PlaneNormal(tz, nGeo, 2) * wxz.y);
                    normalWS = normalize(lerp(normalWS, nt, steep));
                }
                input.normalWS = normalWS;
                input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                input.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                input.fogCoord = i.fogFactor;
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
            #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
                // APV (v2 probe GI, Docs/remaster-world.md 10.6): the 3-argument SAMPLE_GI does not exist in the APV variants
                input.bakedGI = SampleProbeVolumePixel(i.vertexSH, i.positionWS, normalWS, GetWorldSpaceNormalizeViewDir(i.positionWS), GetNormalizedScreenSpaceUV(i.positionCS));
            #else
                input.bakedGI = SAMPLE_GI(i.staticLightmapUV, i.vertexSH, normalWS);
            #endif
                input.shadowMask = SAMPLE_SHADOWMASK(i.staticLightmapUV);

                SurfaceData s = (SurfaceData)0;
                s.albedo = albedo;
                s.alpha = 1;
                s.smoothness = _Smoothness;
                half4 mask = lerp(maskB, maskA, a);
                s.occlusion = lerp(1, mask.g, saturate(_OcclusionStrength * G3TUNE_OCCLUSION));
                // cavity: the height map's low parts darker, high parts lighter: reads as relief at 5-15 m. r3: around the
                // map's own mean height (its 1x1 mip), not 0.5 - the baked heights average 0.24-0.83, so r2 shifted whole
                // materials by up to +-14 % (brighter ones lifted their dark joints too)
                [branch] if (G3TUNE_CAVITY > 0.001h)
                {
                    half hA = lerp(0.5h, SAMPLE_TEXTURE2D_LOD(_MaskMap, sampler_MaskMap, float2(0.5, 0.5), 15).b, _UseMask);
                #if defined(_SINGLE_LAYER)
                    half hMean = hA;
                #else
                    half hMean = lerp(lerp(0.5h, SAMPLE_TEXTURE2D_LOD(_MaskMap2, sampler_MaskMap2, float2(0.5, 0.5), 15).b, _UseMask2), hA, a);
                #endif
                    s.albedo *= max(1 + G3TUNE_CAVITY * (mask.b - hMean) * 1.2h, 0);
                }
                s.normalTS = half3(0, 0, 1);
            #if defined(_SPECULAR_COLOR)
                half3 specA = _SpecColor.rgb * lerp(1, albedoA.rgb, _SpecAlbedo);
                half3 specB = _SpecColor2.rgb * lerp(1, albedoB.rgb, _SpecAlbedo2);
                s.specular = lerp(specB, specA, a) * (0.5 + mask.a);
            #endif

                half4 color = UniversalFragmentBlinnPhong(input, s);
                color.rgb = MixFog(color.rgb, input.fogCoord);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex G3ShadowVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

            // fix-171: URP's ShadowPassVertex + the relief morph (the shadow map sees the colour pass's surface)
            struct G3ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 uv2        : TEXCOORD2;
                float4 uv3        : TEXCOORD3;
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings G3ShadowVertex(G3ShadowAttributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                G3ReliefMorph(input.positionOS.xyz, input.normalOS, input.uv2, input.uv3, input.uv4, input.uv5, input.uv6, false);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                output.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS)));
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex G3DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"

            // fix-171: URP's DepthOnlyVertex + the relief morph (the depth prepass must match ForwardLit)
            struct G3DepthAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 uv2        : TEXCOORD2;
                float4 uv3        : TEXCOORD3;
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings G3DepthOnlyVertex(G3DepthAttributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 nrm = input.normalOS;
                G3ReliefMorph(input.positionOS.xyz, nrm, input.uv2, input.uv3, input.uv4, input.uv5, input.uv6, false);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile _ _WRITE_SMOOTHNESS
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #if defined(LOD_FADE_CROSSFADE)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normal     : NORMAL;
                float4 uv2        : TEXCOORD2;   // fix-171 relief morph
                float4 uv3        : TEXCOORD3;
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS    : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                G3ReliefMorph(input.positionOS.xyz, input.normal, input.uv2, input.uv3, input.uv4, input.uv5, input.uv6, true);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = half3(TransformObjectToWorldNormal(input.normal));
                return output;
            }

            void DepthNormalsFragment(Varyings input, out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #if defined(LOD_FADE_CROSSFADE)
                LODFadeCrossFade(input.positionCS);
            #endif
            #if defined(_GBUFFER_NORMALS_OCT)
                float3 normalWS = normalize(input.normalWS);
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);
                outNormalWS = half4(packedNormalWS, 0.0);
            #else
                outNormalWS = half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            #endif
            #if defined(_WRITE_SMOOTHNESS)
                outNormalWS.a = _Smoothness;   // Lit: SampleMetallicSpecGloss(...).a without a gloss map
            #endif
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
