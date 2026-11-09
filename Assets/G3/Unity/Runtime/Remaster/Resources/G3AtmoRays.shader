// atmo-1 (Docs/remaster-world.md section 23): screen-space crepuscular rays (god rays) towards the sun over the desert
// (G3DesertAtmo; GPU Gems 3 ch. 13 "Volumetric Light Scattering as a Post-Process", two-pass radial blur = N x N taps).
// The v2 volumetric march gives shafts through the shadow map but only within its 140 m and at quarter resolution; these
// rays carry the sun past ruins, palms and dune crests at any distance when the sun is in view (low sun: long rays).
// Pass 0 (1/4 res): mask = sky pixels (and the far haze) x closeness to the sun. Pass 1/2 (1/4 res): radial blur towards
// the sun's screen position (24 taps each, the second over 1/24 of the first's length). Pass 3 (full res): additive,
// in the sun's colour x the dust tint, before the grade.
Shader "Hidden/G3/Remaster/AtmoRays"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _G3AtmoRays;    // x intensity (0 = off), y decay per tap, z ray length (share of the way to the sun), w mask sharpness
            float4 _G3AtmoRaysSun; // x unused, y unused, z sun in front (0..1 fade), w far depth for the mask (m)
            float4 _G3AtmoRaysCol; // rgb colour (linear: the sun's colour x dust tint)
            float4 _G3AtmoRaysDir; // xyz direction to the sun (world)
            TEXTURE2D(_G3AtmoRaysTex);

            bool RaysSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 1e-6;
            #else
                return raw >= 1 - 1e-6;
            #endif
            }

            float2 RaysSunUV()
            {
                return ComputeNormalizedDeviceCoordinates(GetCameraPositionWS() + _G3AtmoRaysDir.xyz * 10000, UNITY_MATRIX_VP);
            }

            float RaysBlur(float2 uv, float len)
            {
                float2 d = (RaysSunUV() - uv) * len / 24;
                float jitter = InterleavedGradientNoise(uv * _ScreenParams.xy, 0);
                float2 p = uv + d * jitter;
                float decay = 1, sum = 0, wsum = 0;
                [unroll] for (int k = 0; k < 24; k++)
                {
                    sum += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, p, 0).r * decay;
                    wsum += decay;
                    decay *= _G3AtmoRays.y;
                    p += d;
                }
                return sum / max(wsum, 1e-4);
            }
        ENDHLSL

        Pass
        {
            Name "G3AtmoRaysMask"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMask
            float4 FragMask(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float raw = SampleSceneDepth(uv);
                float open;
                float3 v;
                [branch] if (RaysSky(raw))
                {
                    open = 1;
                #if UNITY_REVERSED_Z
                    float3 pw = ComputeWorldSpacePosition(uv, 0.5, UNITY_MATRIX_I_VP);
                #else
                    float3 pw = ComputeWorldSpacePosition(uv, 0.5, UNITY_MATRIX_I_VP);
                #endif
                    v = normalize(pw - GetCameraPositionWS());
                }
                else
                {
                #if UNITY_REVERSED_Z
                    float d = raw;
                #else
                    float d = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
                #endif
                    float3 pw = ComputeWorldSpacePosition(uv, d, UNITY_MATRIX_I_VP);
                    float3 r = pw - GetCameraPositionWS();
                    float dist = length(r);
                    v = r / max(dist, 1e-4);
                    open = 0.35 * smoothstep(_G3AtmoRaysSun.w * 0.5, _G3AtmoRaysSun.w, dist);   // the far haze glows a little too
                }
                float c = saturate(dot(v, _G3AtmoRaysDir.xyz));
                float m = open * pow(c, _G3AtmoRays.w);
                return float4(m, 0, 0, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3AtmoRaysBlur1"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur1
            float4 FragBlur1(Varyings i) : SV_Target { return float4(RaysBlur(i.texcoord, _G3AtmoRays.z), 0, 0, 1); }
            ENDHLSL
        }

        Pass
        {
            Name "G3AtmoRaysBlur2"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur2
            float4 FragBlur2(Varyings i) : SV_Target { return float4(RaysBlur(i.texcoord, _G3AtmoRays.z / 24), 0, 0, 1); }
            ENDHLSL
        }

        Pass
        {
            Name "G3AtmoRaysComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            half4 FragComposite(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float r = SAMPLE_TEXTURE2D_LOD(_G3AtmoRaysTex, sampler_LinearClamp, uv, 0).r;
                // falloff away from the sun on screen (the phase function): rays near the sun, nothing washed far from it
                float2 ds = (uv - RaysSunUV()) * float2(_ScreenParams.x / _ScreenParams.y, 1);
                float att = saturate(1 - length(ds) / 1.1);
                // over the sky the glow is the sky's own (Mie): the rays light the air in front of geometry
                float onSky = RaysSky(SampleSceneDepth(uv)) ? 0.15 : 1;
                col.rgb += _G3AtmoRaysCol.rgb * (r * att * att * onSky * _G3AtmoRays.x * _G3AtmoRaysSun.z);
                return col;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
