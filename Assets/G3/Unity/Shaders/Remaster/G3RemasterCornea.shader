// FACES cornea shell (Tools/Blender/remaster/eyegen.py): a thin clear cap over each generated eyeball. Specular only:
// a sharp GGX lobe for every light (the catch light, where the bulge sits in front of the iris) plus the reflection
// probe, both weighted by Schlick Fresnel; premultiplied transparency so the iris/sclera under it stays visible
// (alpha = Fresnel x _EnvSpecular: only grazing angles dim it). No refraction (the iris is modelled concave instead).
// Applied by G3RemasterMaterials.Generated to the material G3_Remaster_Cornea (RemasterGenerated).
Shader "G3/Remaster/Cornea"
{
    Properties
    {
        _Smoothness("Smoothness", Range(0, 1)) = 0.95
        _F0("Reflectance at normal incidence", Range(0, 0.1)) = 0.025
        _SpecularStrength("Light specular strength", Range(0, 4)) = 0.8
        _EnvSpecular("Environment reflection", Range(0, 1)) = 0.35
        _EnvSaturation("Environment reflection saturation", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex CorneaVert
            #pragma fragment CorneaFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Smoothness;
                half _F0;
                half _SpecularStrength;
                half _EnvSpecular;
                half _EnvSaturation;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS    : TEXCOORD1;
                half fogFactor    : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CorneaVert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half3 CorneaLight(BRDFData brdf, half3 N, half3 V, Light light)
            {
                half NdotL = saturate(dot(N, light.direction));
                half spec = DirectBRDFSpecular(brdf, N, light.direction, V);
                return light.color * (light.distanceAttenuation * light.shadowAttenuation * NdotL * spec * _SpecularStrength);
            }

            half4 CorneaFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 N = normalize(i.normalWS);
                half3 V = GetWorldSpaceNormalizeViewDir(i.positionWS);
                half NoV = saturate(dot(N, V));
                half fresnel = _F0 + (1.0h - _F0) * Pow4(1.0h - NoV) * (1.0h - NoV);

                BRDFData brdf;
                half alphaUnused = 1;
                InitializeBRDFData(half3(0, 0, 0), 0, half3(0, 0, 0), _Smoothness, alphaUnused, brdf);

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = N;
                inputData.viewDirectionWS = V;
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                half4 shadowMask = half4(1, 1, 1, 1);
                uint meshRenderingLayers = GetMeshRenderingLayer();

                // DirectBRDFSpecular = the GGX D x V term only; Fresnel (_F0, Schlick) is applied once below for lights and probe
                half3 color = 0;
                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, shadowMask);
#ifdef _LIGHT_LAYERS
                if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
                    color += CorneaLight(brdf, N, V, mainLight);
#if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
#if USE_CLUSTER_LIGHT_LOOP
                [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
#ifdef _LIGHT_LAYERS
                    if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
                        color += CorneaLight(brdf, N, V, light);
                }
#endif
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
#ifdef _LIGHT_LAYERS
                    if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
                        color += CorneaLight(brdf, N, V, light);
                LIGHT_LOOP_END
#endif
                color *= fresnel;

                half3 env = GlossyEnvironmentReflection(reflect(-V, N), i.positionWS, brdf.perceptualRoughness, 1.0h, inputData.normalizedScreenSpaceUV);
                env = lerp(Luminance(env).xxx, env, _EnvSaturation);
                color += env * fresnel * _EnvSpecular;

                half alpha = saturate(fresnel * _EnvSpecular);
                color = MixFogColor(color, half3(0, 0, 0), i.fogFactor); // additive-like layer: fog fades it out
                return half4(min(color, HALF_MAX), alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
