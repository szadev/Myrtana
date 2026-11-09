// Two-layer lit material for Gothic 3 landscape / transition materials:
// albedo = lerp(A, B, α), α = vertex colour alpha (Genome eCColorSrcBlend "lerp( src1, src2, VC.a )": layer B = src2 where
// α = 1; verified in the editor, runner-20: the Faring road becomes a continuous path with soft edges).
// Each layer may multiply a second map (Genome eCColorSrcCombiner Mul → "( base * detail )", no 2x, no fade): the
// landscape's G3_Misc_Detailmap_01 at 1/40 m, dirt maps, a second stone map. _DETAILMAP = one map for the layers
// selected by _DetailLayers (one extra sample when both layers share it, the common case), _DETAILMAP2 = a different
// map for layer B. _SINGLE_LAYER = an ordinary one-layer material that only needs the detail map (layer B unused).
// Global _G3DetailDisable = 1 switches every detail map off (A/B comparison; default 0 = on).
// Brightness: a detail map is divided by the luminance of its mean colour (its smallest mip), so it adds G3's variation
// but keeps the base map's mean brightness [interp]. Genome multiplies raw ("( %s * %s )"), but the second stone maps on
// rock have means of 0.18-0.31 (product near black: rock slopes went from luma 46.7 to 39.2 frame-wide, runner-20) and the
// landscape's Detailmap_01 0.78; G3 lights the landscape with its x4 lightmaps (ge_default.fx) that the URP lighting
// calibrated on the base maps does not reproduce. Global _G3DetailRaw = 1 multiplies raw (as the Genome code).
// Lighting is URP's Blinn-Phong path (as Simple Lit): Genome has no ambient/environment specular, only a per-light
// highlight scaled by the material's specular mask (Docs/formats-mesh-material-texture.md §4.6).
Shader "G3/LitBlend"
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

            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BaseMap2); SAMPLER(sampler_BaseMap2);
            TEXTURE2D(_BumpMap);  SAMPLER(sampler_BumpMap);
            TEXTURE2D(_BumpMap2); SAMPLER(sampler_BumpMap2);
            TEXTURE2D(_DetailMap);  SAMPLER(sampler_DetailMap);
            TEXTURE2D(_DetailMap2); SAMPLER(sampler_DetailMap2);
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
            CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
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
            #if defined(_SINGLE_LAYER)
                half a = 1;
                half4 albedoA = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv * _BaseMap_ST.xy + _BaseMap_ST.zw);
                half4 albedoB = albedoA;
            #else
                half a = 1 - saturate(i.color.a);   // weight of layer A (src1): G3 lerp(src1, src2, VC.a)
                if (_SwapLayers > 0.5) a = 1 - a;
                half4 albedoA = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv * _BaseMap_ST.xy + _BaseMap_ST.zw);
                half4 albedoB = SAMPLE_TEXTURE2D(_BaseMap2, sampler_BaseMap2, i.uv * _BaseMap2_ST.xy + _BaseMap2_ST.zw);
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
                half3 albedo = lerp(colB, colA, a) * _BaseColor.rgb;

                InputData input = (InputData)0;
                input.positionWS = i.positionWS;
                input.positionCS = i.positionCS;
                half3 normalWS = normalize(i.normalWS);
            #if defined(_NORMALMAP)
                half3 nA = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv * _BumpMap_ST.xy + _BumpMap_ST.zw));
            #if defined(_SINGLE_LAYER)
                half3 normalTS = nA;
            #else
                half3 nB = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap2, sampler_BumpMap2, i.uv * _BumpMap2_ST.xy + _BumpMap2_ST.zw));
                half3 normalTS = normalize(lerp(nB, nA, a));
            #endif
                half3 bitangent = i.tangentWS.w * cross(i.normalWS, i.tangentWS.xyz);
                half3x3 tbn = half3x3(i.tangentWS.xyz, bitangent, i.normalWS);
                input.tangentToWorld = tbn;
                normalWS = normalize(TransformTangentToWorld(normalTS, tbn));
            #endif
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
                s.occlusion = 1;
                s.normalTS = half3(0, 0, 1);
            #if defined(_SPECULAR_COLOR)
                half3 specA = _SpecColor.rgb * lerp(1, albedoA.rgb, _SpecAlbedo);
                half3 specB = _SpecColor2.rgb * lerp(1, albedoB.rgb, _SpecAlbedo2);
                s.specular = lerp(specB, specA, a);
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
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
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
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
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
