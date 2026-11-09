// Gothic 3 particle (eCParticle_PS) for URP: unlit texture × vertex colour, blending from the G3 draw style
// (G3.Effects.EmitterRules.Blend: AlphaBlend, Brighten = additive, Modulated, Translucent, Darken ...), optional alpha
// test (AlphaTest + AlphaReference), depth write/test, fog (additive styles fade to black; DisableFogging = no fog).
// Texture-sheet frames come from the particle system's Texture Sheet Animation (UV0). Docs/formats-effects.md §4.
// _G3_LIT (LightingStyle Flat/Particle/System, mostly smoke): the colour is lit like an unshaded sprite — ambient (SH) +
// main light + the Forward+ additional lights by distance attenuation only (no normal), at most 1 — so torch smoke is dark
// at night and glows warm next to its fire instead of keeping its daylight texture colour [interp].
Shader "G3/Particle"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Tint("Tint", Color) = (1, 1, 1, 1)
        _Cutoff("Alpha reference", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst blend", Float) = 1
        [Enum(Off, 0, On, 1)] _ZWrite("Depth write", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("Depth test", Float) = 4
        [Toggle(_ALPHATEST_ON)] _AlphaTest("Alpha test", Float) = 0
        [Toggle(_G3_FOG_BLACK)] _FogBlack("Fog to black (additive)", Float) = 0
        [Toggle(_G3_NO_FOG)] _NoFog("Disable fogging", Float) = 0
        [Toggle(_G3_LIT)] _Lit("Lit (LightingStyle)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Cull Off

        Pass
        {
            Name "G3Particle"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _G3_FOG_BLACK
            #pragma shader_feature_local _G3_NO_FOG
            #pragma shader_feature_local _G3_LIT
            // Only the lit variant uses them; the unlit code is identical in every combination.
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #if defined(_G3_LIT)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #endif

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Tint;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                half fog : TEXCOORD1;
            #if defined(_G3_LIT)
                float3 positionWS : TEXCOORD2;
            #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
            #if defined(_G3_LIT)
                o.positionWS = positionWS;
            #endif
                o.color = v.color * _Tint;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

        #if defined(_G3_LIT)
            half3 SpriteLight(float3 positionWS, float4 positionCS)
            {
                half3 light = SampleSH(half3(0, 1, 0));
                Light mainLight = GetMainLight();
                light += mainLight.color * mainLight.distanceAttenuation;
            #if defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX)
                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
                uint lightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightCount)
                    Light l = GetAdditionalLight(lightIndex, positionWS);
                    light += l.color * l.distanceAttenuation;
                LIGHT_LOOP_END
            #endif
                return min(light, half3(1, 1, 1));
            }
        #endif

            half4 Frag(Varyings i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
            #if defined(_G3_LIT)
                c.rgb *= SpriteLight(i.positionWS, i.positionCS);
            #endif
            #if defined(_ALPHATEST_ON)
                clip(c.a - _Cutoff);
            #endif
            #if defined(_G3_LIT)
                // G3 blended in gamma space: a smoke sprite of alpha a over a dark background added a·colour in sRGB, i.e.
                // (a·colour)^2.2 in linear terms; blending a·colour_lin here made lit smoke at night (fully lit by its own
                // torch light) ~2× brighter and denser than G3's thin, faint smoke (fix-9, owner report). a^2.2 gives the
                // G3 result over dark backgrounds; over bright daylight backgrounds smoke is somewhat fainter [interp].
                c.a = pow(saturate(c.a), 2.2);
            #endif
            #if !defined(_G3_NO_FOG)
                #if defined(_G3_FOG_BLACK)
                    c.rgb = MixFogColor(c.rgb, half3(0, 0, 0), i.fog);
                #else
                    c.rgb = MixFog(c.rgb, i.fog);
                #endif
            #endif
                return c;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
