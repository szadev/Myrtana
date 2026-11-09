// Gothic 3 heat haze (G3_Special_HeatHaze_01.xshmat: eCShaderParticle with DistortionScale and the map
// VFX_Env_HeatHaze_Distort_01) for URP: the particle offsets the screen behind it. It samples the camera's opaque texture
// (URP asset "Opaque Texture" on: PC_RPAsset has it, 2x downsampled), so it costs one extra texture read per pixel and no
// extra pass. Offset = (map.rg * 2 - 1) * _Strength * vertex alpha * map.a; fades with distance. Docs/formats-effects.md §8.
Shader "G3/HeatHaze"
{
    Properties
    {
        _MainTex("Distortion map", 2D) = "gray" {}
        _Strength("Strength (screen fraction) [interp]", Range(0, 0.05)) = 0.012
        _FadeDistance("Fade out by (m)", Float) = 40
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "G3HeatHaze"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half _Strength;
                half _FadeDistance;
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
                float4 screen : TEXCOORD1;
                half fade : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.screen = ComputeScreenPos(o.positionCS);
                o.fade = saturate(1 - distance(ws, GetCameraPositionWS()) / max(_FadeDistance, 1));
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half4 m = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                float2 uv = i.screen.xy / i.screen.w;
                float2 offset = (m.rg * 2 - 1) * _Strength * i.color.a * m.a * i.fade;
                half3 scene = SampleSceneColor(uv + offset);
                return half4(scene, saturate(i.color.a * m.a * i.fade * 4));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
