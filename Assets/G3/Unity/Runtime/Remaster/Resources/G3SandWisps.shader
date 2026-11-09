// fx-1 (Docs/remaster-world.md 22.11): blown sand streamers (G3SandWisps.cs), replacing sand-2/3's upright camera-facing ribbons
// (owner: long glassy straight ribbons hanging in the air, across half the screen, over rock). One procedural draw through
// G3SandFx (no buffers): sheet v / 96 is a ground-hugging tongue of saltating sand - a grid of 8 x 2 quads (vertices from
// SV_VertexID) lying on the drawn ground (relief ground map 0 per vertex: it follows every bump), 1.2-4.5 m along the wind,
// 0.35-1.35 m wide, snaking a little, arched to a low crest (most < 0.15 m, sandWispHeight at most; off a dune crest the
// downwind end lifts into a short plume up to + sandWispCrest). Spawned only on open loose sand (the relief's sand mask:
// loose class x layer blend x flatness - no rock, cliffs, grass, paths or steep slopes), more on crests and lee edges;
// drifting slowly downwind in a camera-centred toroidal box while the texture streams through it at the wind speed. Fragment:
// world-space torn noise (streaks stretched along the wind, holes, ragged edges and ends), soft against the depth, low
// opacity, faded near the camera (sandWispNear) and with distance; colour = G3's sand (mean) lit by sky + sun in the main
// light's shadow, seen through the v2 fog and atmo-1's storm column (G3SandStreak.hlsl). Alpha-blended, no depth write.
Shader "Hidden/G3/Remaster/SandWisps"
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
            Name "G3SandWisps"
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

            #define NU 8u
            #define NV 2u
            #define SHEET_VERTS (NU * NV * 6u)

            float4 _G3SandWisp0;   // x amount 0..1, y peak opacity, z range (m), w count
            float4 _G3SandWisp1;   // xy wind direction (unit world x z), z speed (m/s), w time (s)
            float4 _G3SandWisp2;   // x ground height fallback (m), y sheet height (m), z crest plume lift (m), w near fade (m)
            float4 _G3SandWisp3;   // x sand mask strength (1 = loose open sand only)

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uvw : TEXCOORD0;      // x along 0..1, y across -1..1, z seed, w fade (life x distance x near x mask x desert)
                float3 positionWS : TEXCOORD1;
                float2 flow : TEXCOORD2;     // world m along / across the wind (continuous noise space)
                half fog : TEXCOORD3;
            };

            Varyings Vert(uint vid : SV_VertexID)
            {
                Varyings o = (Varyings)0;
                uint sheet = vid / SHEET_VERTS, local = vid % SHEET_VERTS;
                uint quad = local / 6u, corner = local % 6u;
                float2 dq = corner == 0u ? float2(0, 0) : corner == 1u ? float2(1, 0) : corner == 2u ? float2(1, 1) :
                            corner == 3u ? float2(0, 0) : corner == 4u ? float2(1, 1) : float2(0, 1);
                float u = ((quad % NU) + dq.x) / NU;
                float v = ((quad / NU) + dq.y) / NV * 2 - 1;

                float4 r = G3StreakRand(sheet, 1u), r2 = G3StreakRand(sheet, 2u);
                float range = max(_G3SandWisp0.z, 5);
                float box = range * 2.3;
                float2 w = normalize(_G3SandWisp1.xy + float2(1e-5, 0));
                float2 side = float2(-w.y, w.x);
                float t = _G3SandWisp1.w;
                float life = 2.5 + 3.5 * r.w;
                float cyc = t / life + r2.x;
                float age = frac(cyc);
                uint gen = (uint)floor(cyc);   // a new place every life
                float4 rg = G3StreakRand(sheet * 7919u + gen, 3u), rh = G3StreakRand(sheet * 7919u + gen, 4u);
                float3 cam = _WorldSpaceCameraPos;
                // the sheet drifts slowly (the shape keeps), the grains stream through it at the wind speed (fragment)
                float drift = _G3SandWisp1.z * 0.3 * (0.7 + 0.6 * r.z);
                float2 base = (rg.xy - 0.5) * box + w * drift * age * life;
                float2 rel = base - cam.xz;
                rel = (frac(rel / box + 0.5) - 0.5) * box;
                float2 c = cam.xz + rel;

                // where: open loose sand only; more on crests (convex ground) and lee edges (the ground falls away downwind)
                float fb = _G3SandWisp2.x;
                float gC = G3StreakGround(c, fb);
                float gD = G3StreakGround(c + w * 3.0, fb), gU = G3StreakGround(c - w * 3.0, fb);
                float gL = G3StreakGround(c + side * 3.0, fb), gR = G3StreakGround(c - side * 3.0, fb);
                float conv = gC - 0.25 * (gD + gU + gL + gR);
                float crest = saturate((gC - gD - 0.12) / 0.7);
                float sandC = G3StreakSand(c, _G3SandWisp3.x);
                float favour = 0.4 + 0.6 * saturate(crest * 1.5 + conv * 2.5);
                float keep = step(rh.x, sandC * favour * (1 - G3StreakCovered(c)));

                float len = 1.2 + 3.3 * rg.z * rg.z, wid = 0.35 + 1.0 * r.x;
                float H = _G3SandWisp2.y * (0.2 + 0.8 * r.y * r.y);   // mostly low: a few cm to sandWispHeight
                float sway = (rg.w - 0.5) * 0.5;
                float2 dir = normalize(w + side * sway);
                float2 sd = float2(-dir.y, dir.x);
                float taper = 0.35 + 0.65 * sin(PI * u);
                float meander = wid * 0.3 * sin(u * 5.0 + rh.y * 6.283 + t * (0.8 + 0.6 * rh.z));
                float2 xz = c + dir * (u - 0.5) * len + sd * (v * 0.5 * wid * taper + meander);
                float ground = G3StreakGround(xz, fb);
                // off a crest the downwind part does not follow the lee down: it flies on as a short plume
                float plume = crest * u * u;
                float gy = max(ground, lerp(ground, gC - 0.1 * u * len, saturate(plume * 2)));
                float gust = 0.75 + 0.25 * sin(t * (1.3 + rh.w) + rh.y * 9.0);
                float arch = (1 - v * v) * (0.45 + 0.55 * sin(PI * saturate(u * 1.15)));
                float y = gy + 0.015 + H * arch * gust + plume * _G3SandWisp2.z * (0.6 + 0.4 * (1 - v * v));
                float3 pw = float3(xz.x, y, xz.y);

                float dist = distance(pw, cam);
                float nearF = _G3SandWisp2.w;
                float fade = pow(sin(age * PI), 0.7)
                           * (1 - smoothstep(range * 0.6, range, distance(c, cam.xz)))
                           * smoothstep(nearF * 0.5, max(nearF, 0.01), dist)
                           * saturate(G3StreakSand(xz, _G3SandWisp3.x) * 1.6)   // never spills onto rock or grass
                           * (1 - G3StreakCovered(xz))                            // never under roofs, tents, awnings, indoors
                           * G3AtmoRegion(xz);
                fade *= keep * _G3SandWisp0.x * step((float)sheet, _G3SandWisp0.w - 1) * (0.85 + 0.4 * crest);
                o.positionWS = pw;
                o.positionCS = fade > 0.002 ? TransformWorldToHClip(pw) : float4(0, 0, -1, 1);   // collapsed: no raster
                o.uvw = float4(u, v, rg.x * 37.1 + r.z * 11.7, fade);
                o.flow = float2(dot(xz, w), dot(xz, side));
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float fade = i.uvw.w;
                clip(fade - 0.002);
                float u = i.uvw.x, v = i.uvw.y, seed = i.uvw.z;
                float t = _G3SandWisp1.w, speed = _G3SandWisp1.z;
                // streaks stretched along the wind streaming at the wind speed; holes tearing the sheet; fine grain lines
                float s = i.flow.x - t * speed, p = i.flow.y;
                float n1 = G3StreakFbm(float2(s / 1.4, p / 0.2), 11u);
                float n2 = G3StreakNoise(float2((i.flow.x - t * speed * 0.55) / 0.8 + seed, p / 0.6), 13u);
                float n3 = G3StreakNoise(float2(s / 0.3, p / 0.045), 17u);
                float dens = smoothstep(0.38, 0.78, n1 * 0.75 + n3 * 0.25) * smoothstep(0.22, 0.62, n2);
                // ragged soft edges and ends (no straight outline)
                float rag = (n2 - 0.5) * 0.45 + (n1 - 0.5) * 0.3;
                float ends = smoothstep(0.0, 0.35, u + rag) * (1 - smoothstep(0.5, 1.0, u - rag));
                float sides = 1 - smoothstep(0.25, 1.0, abs(v) + rag);
                float alpha = dens * ends * sides * fade * _G3SandWisp0.y;
                // a thin layer seen at a grazing angle is a longer path through the sand: denser, but only a little
                float3 toEye = normalize(_WorldSpaceCameraPos - i.positionWS);
                alpha *= lerp(1, min(1 / max(abs(toEye.y), 0.25), 3), 0.35);
                alpha *= G3StreakSoft(i.positionCS, 0.25);
                clip(alpha - 0.003);
                float3 lit = G3StreakLit(i.positionWS, 0.35, 1.0);
                lit = MixFog(lit, i.fog);
                float a = saturate(alpha);
                lit = G3StreakMedium(i.positionWS, lit, a);
                return half4(lit, a);
            }
            ENDHLSL
        }
    }
}
