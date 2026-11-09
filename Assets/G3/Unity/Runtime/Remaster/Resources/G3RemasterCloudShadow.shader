// Cloud shadow map of the v2 cloud slab (Docs/remaster-world.md 16.4, agent remaster-gi). A top-down window around the
// camera (G3CloudShadow: 256^2 texels over v2CloudShadowRange m): per texel the sun ray from the ground (at camera
// height) is followed up through the slab at 6 heights, the coarse slab density (the same function G3/Sky ray-marches,
// world-anchored) summed into an optical depth; out = sun transmittance. Read by the light-ratio pass (sun term) and the
// fog shafts. (URP's own volumetric-cloud ports use the main light cookie instead; G3's sun keeps no cookie, and a cookie
// would also shadow the transparents and characters twice.)
Shader "Hidden/G3/Remaster/CloudShadow"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "G3CloudShadow"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/G3/Unity/Runtime/Environment/G3CloudsV2.hlsl"

            float4 _G3CloudShadowWindow;   // x min x, y min z, z size (m), w camera y (m)
            float4 _G3CloudShadowSun;      // xyz direction to the sun, w coverage (as G3/Sky: base + overcast)

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float2 uv = float2((v.vertexID << 1) & 2, v.vertexID & 2);
                o.positionCS = float4(uv * 2 - 1, 0, 1);
            #if UNITY_UV_STARTS_AT_TOP
                uv.y = 1 - uv.y;
            #endif
                o.uv = uv;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float3 sd = normalize(_G3CloudShadowSun.xyz);
                float sy = max(sd.y, 0.12);
                float2 xz = _G3CloudShadowWindow.xy + i.uv * _G3CloudShadowWindow.z;   // ground point (m)
                float od = 0;
                const int N = 6;
                float dh = (G3_CLOUD_TOP - G3_CLOUD_BASE) / N;
                [unroll] for (int k = 0; k < N; k++)
                {
                    float h = G3_CLOUD_BASE + (k + 0.5) * dh;                          // km above the camera
                    float2 off = sd.xz / sy * h;                                         // km (WorldGi.SunOffset)
                    float3 pk = float3(xz.x * 0.001 + off.x, h, xz.y * 0.001 + off.y);
                    od += G3CloudCoarse(pk, _G3CloudShadowSun.w, 3) * dh / sy;
                }
                float t = exp(-od);
                return float4(t, t, t, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
