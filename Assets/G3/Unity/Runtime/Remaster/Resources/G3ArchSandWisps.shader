// arch-2 (Docs/remaster-world.md 25.7; fx-1 22.11): sand blown off tent / awning edges after a sandstorm (G3ArchSandWisps.cs).
// One procedural draw through G3SandFx (no buffers): vertex v of ribbon v / 6, ribbon r of emitter r / perEmitter. Each wisp
// starts at a random point of the emitter's lee-side rim (its bounds' downwind half, upper 15-60 %), streams downwind
// 0.3-0.9 m long, 0.04-0.12 m tall, turned half-way to the eye about its wind axis (never a hard line), rising then
// falling (grains thrown off the cloth), and lives 0.8-1.8 s. Fragment: fx-1's torn noise (G3SandStreak.hlsl) with ragged
// soft ends and edges, soft against the depth, low opacity (archSandWispOpacity); colour = G3's sand (mean) lit by sky + sun,
// through the v2 fog and the storm column. Alpha-blended, no depth write, fogged.
Shader "Hidden/G3/Remaster/ArchSandWisps"
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
            Name "G3ArchSandWisps"
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

            float4 _G3ArchWisp0;            // x amount 0..1, y ribbons per emitter, z emitters, w time (s)
            float4 _G3ArchWisp1;            // xy wind direction (unit world x z), z speed (m/s), w peak opacity
            float4 _G3ArchWispEmit[8];      // bounds: x centre x, y top, z centre z, w bottom
            float4 _G3ArchWispSize[8];      // bounds extents x y z, w seed

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uvw : TEXCOORD0;      // x along, y up, z seed, w fade
                float3 positionWS : TEXCOORD1;
                half fog : TEXCOORD2;
            };

            Varyings Vert(uint vid : SV_VertexID)
            {
                Varyings o = (Varyings)0;
                uint ribbon = vid / 6u, corner = vid % 6u;
                float2 q = corner == 0u ? float2(0, 0) : corner == 1u ? float2(1, 0) : corner == 2u ? float2(1, 1) :
                           corner == 3u ? float2(0, 0) : corner == 4u ? float2(1, 1) : float2(0, 1);
                uint per = max((uint)_G3ArchWisp0.y, 1u);
                uint e = min(ribbon / per, 7u);
                float4 em = _G3ArchWispEmit[e], sz = _G3ArchWispSize[e];
                float4 r = G3StreakRand(ribbon, 1u);
                float life = 0.8 + 1.0 * r.w;
                float cyc = _G3ArchWisp0.w / life + r.z + sz.w;
                float age = frac(cyc);
                float4 rg = G3StreakRand(ribbon * 7919u + (uint)floor(cyc), 2u);
                float2 w = normalize(_G3ArchWisp1.xy + float2(1e-5, 0));
                float2 side = float2(-w.y, w.x);
                // lee-side rim of the bounds: the downwind half, spread across
                float2 ell = float2(max(sz.x, 0.3), max(sz.z, 0.3));
                float ang = (rg.x - 0.5) * 2.6;                                  // +-75 degrees around the downwind direction
                float2 dirR = w * cos(ang) + side * sin(ang);
                float2 rim = em.xz + dirR * ell * 0.92;
                float h = lerp(em.w, em.y, 0.15 + 0.45 * rg.y);                    // low edges and mid roof
                float speed = _G3ArchWisp1.z * (0.6 + 0.6 * r.x);
                float len = 0.3 + 0.6 * rg.z, hgt = 0.04 + 0.08 * r.y;
                float travel = speed * age * life * 0.5;
                float2 xz = rim + w * (travel + (q.x - 0.5) * len) + side * (rg.w - 0.5) * 0.3;
                float drop = 0.3 * age * age * life;                             // thrown off, then falling
                float3 cam = _WorldSpaceCameraPos;
                float3 d3 = float3(w.x, 0, w.y);
                float3 c3 = float3(rim.x, h, rim.y);
                // half-way between upright and facing the eye about the wind axis (edge-on it would be a line)
                float3 s3 = normalize(cross(d3, normalize(cam - c3)) + float3(0, 1e-4, 0));
                float3 upv = normalize(lerp(float3(0, 1, 0), s3 * (s3.y < 0 ? -1 : 1), 0.5));
                float3 pw = float3(xz.x, h + 0.05 * sin(q.x * 3.14159) - drop, xz.y) + upv * (q.y - 0.5) * hgt;
                float dist = distance(pw, cam);
                float fade = sin(age * 3.14159265) * (1 - smoothstep(22, 32, dist)) * smoothstep(2.0, 4.0, dist);
                fade *= _G3ArchWisp0.x * step((float)e, _G3ArchWisp0.z - 1) * step(0.01, sz.y);
                o.positionWS = pw;
                o.positionCS = fade > 0.002 ? TransformWorldToHClip(pw) : float4(0, 0, -1, 1);
                o.uvw = float4(q, rg.x * 37.1 + r.z * 11.7, fade);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 q = i.uvw.xy;
                float fade = i.uvw.w;
                clip(fade - 0.002);
                float t = _G3ArchWisp0.w * _G3ArchWisp1.z * 0.6;
                float n1 = G3StreakFbm(float2(q.x * 2.5 - t, q.y * 3.0 + i.uvw.z), 21u);
                float n2 = G3StreakNoise(float2(q.x * 4.0 - t * 0.6 + i.uvw.z, q.y * 2.0), 23u);
                float dens = smoothstep(0.35, 0.75, n1) * smoothstep(0.2, 0.6, n2);
                float rag = (n2 - 0.5) * 0.4;
                float ends = smoothstep(0.0, 0.4, q.x + rag) * (1 - smoothstep(0.45, 1.0, q.x - rag));
                float across = 1 - smoothstep(0.2, 1.0, abs(q.y * 2 - 1) + rag);
                float alpha = dens * ends * across * fade * _G3ArchWisp1.w;
                alpha *= G3StreakSoft(i.positionCS, 0.15);
                clip(alpha - 0.003);
                float3 lit = MixFog(G3StreakLit(i.positionWS, 0.35, 1.0), i.fog);
                float a = saturate(alpha);
                lit = G3StreakMedium(i.positionWS, lit, a);
                return half4(lit, a);
            }
            ENDHLSL
        }
    }
}
