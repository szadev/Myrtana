// Gothic 3 waterfall sheets (G3_Myrtana_Water_Falls_*: eCShaderDefault, BlendMode Brighten).
// Two foam textures scrolled down the mesh V (Genome eCTexCoordSrcScroller: uv += Direction * Rate * time),
// combined and added on top of the scene; fades with fog.
Shader "G3/WaterFall"
{
    Properties
    {
        _BaseMap("Foam A", 2D) = "white" {}
        _BaseMap2("Foam B", 2D) = "white" {}
        _Tint("Tint", Color) = (0.58, 0.58, 0.58, 1)
        _FlowA("Flow A (xy dir, z speed)", Vector) = (-0.02, -0.9, 0.55, 0)
        _FlowB("Flow B (xy dir, z speed)", Vector) = (0.01, -0.2, 0.4, 0)
        _Intensity("Intensity", Range(0, 3)) = 1
        _VertexAlpha("Use vertex alpha (sheet opacity mask)", Range(0, 1)) = 1
        _TexAlpha("Use texture alpha", Range(0, 1)) = 0
        _Contrast("Foam contrast", Range(0.5, 3)) = 1.6
        _EdgeSoftness("Side edge softness (U)", Range(0.01, 0.5)) = 0.2
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardFall"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BaseMap2); SAMPLER(sampler_BaseMap2);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseMap2_ST;
                half4 _Tint;
                float4 _FlowA, _FlowB;
                half _Intensity;
                half _VertexAlpha;
                half _TexAlpha;
                half _Contrast;
                half _EdgeSoftness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : TEXCOORD1;
                half fogFactor : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                o.color = input.color;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float t = _Time.y;
                float2 uvA = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw + _FlowA.xy * _FlowA.z * t;
                float2 uvB = input.uv * _BaseMap2_ST.xy + _BaseMap2_ST.zw + _FlowB.xy * _FlowB.z * t;
                half4 ta = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvA);
                half4 tb = SAMPLE_TEXTURE2D(_BaseMap2, sampler_BaseMap2, uvB);
                half3 a = ta.rgb * lerp(1.0h, ta.a, _TexAlpha);
                half3 b = tb.rgb * lerp(1.0h, tb.a, _TexAlpha);
                half3 c = max(a, b) * 0.7 + a * b * 0.6;
                // streakier foam + soft side edges (sheet meshes span U 0..1 across the fall)
                c = pow(saturate(c), _Contrast) * (0.6 + _Contrast * 0.6);
                half edge = smoothstep(0.0, _EdgeSoftness, input.uv.x) * smoothstep(1.0, 1.0 - _EdgeSoftness, input.uv.x);
                c *= edge;
                // light with the ambient sky + sun so night falls are not glowing
                Light l = GetMainLight();
                half3 light = max(half3(0, 0, 0), SampleSH(half3(0, 1, 0))) + l.color * 0.6;
                c *= _Tint.rgb * light * _Intensity;
                c *= lerp(1.0h, input.color.a, _VertexAlpha);
                c = MixFogColor(c, half3(0, 0, 0), input.fogFactor); // additive: fade to nothing in fog
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
