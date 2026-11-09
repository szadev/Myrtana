// remaster-sand (sand-2, Docs/remaster-world.md 22.6; fx-1 22.11): footstep dust puffs on desert sand (G3SandDust.cs). One
// procedural draw through G3SandFx (no buffers): vertex v belongs to puff v / 18, billboard (v / 6) % 3 - three soft discs per
// puff, upright about the world up (cylindrical billboards) and flattened: they spread sideways (0.08 -> 0.55 m x size) and
// stay low - the top never above _G3SandDust0.w (sandDustHeight, 0.3 m; sand-3's reached waist height) - drifting downwind
// and settling within the puff's life (sandDustLife 0.8 s) while they fade; collapsed when dead. Fragment: soft elliptic alpha
// broken by noise, soft against the scene depth; colour = G3's sand (mean) lit by sky + sun, through the v2 fog and the storm
// column (G3SandStreak.hlsl). Alpha-blended, no depth write, fogged.
Shader "Hidden/G3/Remaster/SandDust"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "G3SandDust"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3Atmo.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3SandStreak.hlsl"

            float4 _G3SandPuff[64];    // xyz ground point, w birth time (s)
            float4 _G3SandPuff2[64];   // xy drift (m/s, world x z), z size, w strength
            float4 _G3SandDust0;       // x time (s), y life (s), z count, w top of a puff (m)
            float4 _G3SandDust1;       // x peak opacity

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uvw : TEXCOORD0;      // xy quad -1..1, z seed, w alpha
                float3 positionWS : TEXCOORD1;
                half fog : TEXCOORD2;
            };

            Varyings Vert(uint vid : SV_VertexID)
            {
                Varyings o = (Varyings)0;
                uint puff = vid / 18u, sub = (vid / 6u) % 3u, corner = vid % 6u;
                float2 q = corner == 0u ? float2(-1, -1) : corner == 1u ? float2(1, -1) : corner == 2u ? float2(1, 1) :
                           corner == 3u ? float2(-1, -1) : corner == 4u ? float2(1, 1) : float2(-1, 1);
                float4 p = _G3SandPuff[puff], p2 = _G3SandPuff2[puff];
                float life = max(_G3SandDust0.y, 0.1);
                float age = (_G3SandDust0.x - p.w) / life;
                float4 r = G3StreakRand(asuint(p.w * 1000.0) + puff * 31u, sub);
                float a = saturate(age);
                float grow = 1 - pow(1 - a, 3);
                float size = p2.z * (0.75 + 0.5 * r.x);
                float top = max(_G3SandDust0.w, 0.05) * (0.7 + 0.3 * saturate(p2.w));
                float rx = lerp(0.08, 0.55, grow) * size;                 // sideways spread
                float ry = min(rx * 0.5, top * 0.5) * (1 - 0.35 * a);     // low, and settling
                float t = a * life;
                // the discs spread out from the step and drift with the wind
                float2 off = (r.yz - 0.5) * (0.15 + 0.35 * grow) * size;
                float2 dxz = off + p2.xy * t * (0.7 + 0.6 * r.w);
                float cy = 0.02 + ry * 0.9 * (1 - 0.4 * a);               // centre: the top stays under sandDustHeight
                float3 c = p.xyz + float3(dxz.x, cy, dxz.y);
                float3 right = normalize(float3(UNITY_MATRIX_V[0].x, 0, UNITY_MATRIX_V[0].z) + float3(1e-5, 0, 0));
                float3 pw = c + right * q.x * rx + float3(0, 1, 0) * q.y * ry;
                float alpha = p2.w * _G3SandDust1.x * smoothstep(0.0, 0.06, age) * pow(saturate(1 - age), 1.6) * (age >= 0 && age < 1 ? 1 : 0);
                alpha *= 1 - smoothstep(25, 35, distance(c, _WorldSpaceCameraPos));
                o.positionWS = pw;
                o.positionCS = alpha > 0.002 ? TransformWorldToHClip(pw) : float4(0, 0, -1, 1);   // collapsed: no raster
                o.uvw = float4(q, r.x * 17.3 + sub * 5.1, alpha);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float a0 = i.uvw.w;
                clip(a0 - 0.002);
                float d2 = dot(i.uvw.xy, i.uvw.xy);
                float n = G3StreakNoise(i.uvw.xy * 2.5 + i.uvw.z, 7u) * 0.6 + G3StreakNoise(i.uvw.xy * 6.0 + i.uvw.z * 1.7, 8u) * 0.4;
                float alpha = a0 * exp(-d2 * 2.6) * saturate(1.1 - d2) * (0.4 + 0.9 * n);
                // soft against the ground and the feet
                alpha *= G3StreakSoft(i.positionCS, 0.12);
                clip(alpha - 0.003);
                float3 lit = MixFog(G3StreakLit(i.positionWS, 0.25, 0.9), i.fog);
                float a = saturate(alpha);
                lit = G3StreakMedium(i.positionWS, lit, a);
                return half4(lit, a);
            }
            ENDHLSL
        }
    }
}
