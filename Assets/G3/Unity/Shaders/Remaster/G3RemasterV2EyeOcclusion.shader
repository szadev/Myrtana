// v2 hero eye occlusion shell (eyegen2.py): a thin transparent cap just in front of each eyeball, under the lids, that
// darkens the eye where the lids, lashes and the socket shade it (multiply blend). Replaces the lid shadow v1-v6 baked
// into the eye albedo (too dark in game, editor-runner-29/30) and the shadow-map / SSAO shadow, which cannot resolve the
// ~1 cm lid gap. _BaseMap R = occlusion (1 = none); result = lerp(1, R^_OcclusionPower, _OcclusionStrength).
// Applied by G3RemasterMaterials.Generated to G3_Remaster_V2_EyeOcclusion__<tag> (RemasterGenerated).
Shader "G3/Remaster/V2/EyeOcclusion"
{
    Properties
    {
        _BaseMap("Occlusion (R)", 2D) = "white" {}
        _OcclusionStrength("Strength", Range(0, 1)) = 0.85
        _OcclusionPower("Power", Range(0.2, 4)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend DstColor Zero
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex OccVert
            #pragma fragment OccFrag
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half _OcclusionStrength;
                half _OcclusionPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings OccVert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            half4 OccFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half occ = pow(saturate(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).r), _OcclusionPower);
                half m = lerp(1.0h, occ, _OcclusionStrength);
                return half4(m, m, m, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
