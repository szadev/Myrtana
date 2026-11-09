// atmo-1 (Docs/remaster-world.md section 23): sunlit dust motes round the camera over the desert (G3DesertAtmo). One
// procedural draw: vertex v belongs to mote v / 6; each mote lives in a box of 2 x range wrapped round the camera, drifts
// with the G3 wind (calm: slow, with a little turbulence; sandstorm: fast grains stretched along their motion). A camera-
// facing quad at least ~1 px wide (smaller motes fade instead, energy kept). Fragment: lit by the sun only where the sun
// reaches the mote (URP's cascaded shadow map: motes glitter in the shafts and vanish in the shade), strongly forward
// scattering (backlit motes shine), a faint sky term. Additive, no depth write, depth tested.
// fx-2: calm motes light up only inside sun shafts: the mote is sunlit and the shadow map shows shade within
// _G3AtmoMotes3.y m round it across the sun (open sunlit air: nothing, no stray points in the sky); drawn at least
// _G3AtmoMotes3.x px in radius (soft disc, energy kept: no 1 px sparkles that TAA turns into flicker); the forward-scatter
// boost capped at _G3AtmoMotes3.z (was ~56 at the sun); no ambient term in calm air. The storm grains are unchanged.
Shader "Hidden/G3/Remaster/AtmoMotes"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One One
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "G3AtmoMotes"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #define _MAIN_LIGHT_SHADOWS_CASCADE 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3Atmo.hlsl"

            float4 _G3AtmoMotes;    // x range (m), y time (s), z storm 0..1, w brightness
            float4 _G3AtmoMotes2;   // xyz wind direction (unit, horizontal), w wind speed (m/s)
            float4 _G3AtmoMotes3;   // fx-2: x min radius (px), y shaft probe radius (m), z forward-scatter cap, w shaft gate 0..1

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 q : TEXCOORD0;        // xy quad -1..1 (x along the streak), z alpha, w seed
                float3 centre : TEXCOORD1;
            };

            uint3 MotePcg(uint3 v)
            {
                v = v * 1664525u + 1013904223u;
                v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
                v ^= v >> 16u;
                v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
                return v;
            }

            float3 MoteRand(uint id, uint salt) { return float3(MotePcg(uint3(id, salt, 0x4D07u)) >> 8u) * (1.0 / 16777216.0); }

            Varyings Vert(uint vid : SV_VertexID)
            {
                Varyings o = (Varyings)0;
                uint id = vid / 6u, corner = vid % 6u;
                float2 q = corner == 0u ? float2(-1, -1) : corner == 1u ? float2(1, -1) : corner == 2u ? float2(1, 1) :
                           corner == 3u ? float2(-1, -1) : corner == 4u ? float2(1, 1) : float2(-1, 1);
                float3 r0 = MoteRand(id, 1u), r1 = MoteRand(id, 2u);
                float range = _G3AtmoMotes.x, box = 2 * range, t = _G3AtmoMotes.y, storm = _G3AtmoMotes.z;
                float3 wind = _G3AtmoMotes2.xyz;
                float speed = _G3AtmoMotes2.w * (0.55 + 0.9 * r1.x);
                // drift + turbulence (calm: slow lazy curls; storm: straight and fast)
                float3 vel = wind * speed + float3(sin(t * (0.3 + r1.y) + r0.x * 40), 0.25 * sin(t * (0.21 + 0.3 * r1.z) + r0.y * 30), cos(t * (0.27 + r1.z) + r0.z * 50)) * (0.25 * (1 - storm));
                float3 drift = wind * speed * t + float3(sin(t * (0.3 + r1.y) + r0.x * 40) / (0.3 + r1.y), 0.25 * -cos(t * (0.21 + 0.3 * r1.z) + r0.y * 30) / (0.21 + 0.3 * r1.z), sin(t * (0.27 + r1.z) + r0.z * 50) / (0.27 + r1.z)) * (0.25 * (1 - storm));
                float3 cam = _WorldSpaceCameraPos;
                float3 pr = r0 * box + drift;
                // wrap into the box centred on the camera (height: mostly near the ground, the box is lower)
                float3 rel = frac((pr - cam) / box) * box - range;
                rel.y = rel.y * lerp(0.5, 0.8, storm) - range * 0.15;
                float3 c = cam + rel;
                float dist = length(rel);
                // fade at the box edge and very near the eye
                float edge = 1 - smoothstep(0.7, 1.0, dist / range);
                float nearF = smoothstep(0.25, 0.9, dist);
                float rad = lerp(0.0025, 0.007, r1.z) * (1 + storm * 0.5);
                float px = dist * 2 / (UNITY_MATRIX_P._m11 * _ScreenParams.y);   // metres per pixel at that distance
                float drawRad = max(rad, px * max(_G3AtmoMotes3.x, 0.75));
                float energy = (rad * rad) / (drawRad * drawRad);
                // streaks along the motion (storm: ~1/40 s of travel; calm: round)
                float3 toCam = normalize(cam - c);
                float3 along = normalize(vel - toCam * dot(vel, toCam) + 1e-5);
                float3 side = normalize(cross(along, toCam));
                float streak = max(drawRad, length(vel) * 0.025 * storm);
                float3 pw = c + along * q.x * streak + side * q.y * drawRad;
                float alpha = edge * nearF * energy * sqrt(drawRad / streak) * G3AtmoRegion(c.xz);   // streaks: part of the energy (they read)
                o.positionCS = alpha > 1e-4 ? TransformWorldToHClip(pw) : float4(0, 0, -1, 1);
                o.q = float4(q, alpha, r0.x);
                o.centre = c;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float d2 = i.q.y * i.q.y + i.q.x * i.q.x * 0.35;
                float a = i.q.z * saturate(1 - d2) * saturate(1 - d2);
                clip(a - 1e-4);
                Light sun = GetMainLight();
                float vis = MainLightRealtimeShadow(TransformWorldToShadowCoord(i.centre));
                float storm = _G3AtmoMotes.z;
                // fx-2: shaft gate (calm): shade must be near the sunlit mote, probed across the sun's direction
                float gate = saturate(_G3AtmoMotes3.w) * (1 - storm);
                float shaft = 1;
                [branch] if (gate > 0.001 && vis > 0.01)
                {
                    float3 sd = sun.direction;
                    float3 ta = normalize(abs(sd.y) < 0.95 ? cross(sd, float3(0, 1, 0)) : cross(sd, float3(1, 0, 0)));
                    float3 tb = cross(sd, ta);
                    float r = max(_G3AtmoMotes3.y, 0.2);
                    float lit8 = 0;
                    [unroll] for (int k = 0; k < 8; k++)
                    {
                        float ang = k * 0.7853982 + i.q.w * 6.2831853;
                        float rr = (k & 1) ? r : r * 0.5;
                        float3 p = i.centre + (ta * cos(ang) + tb * sin(ang)) * rr;
                        lit8 += MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                    }
                    float shade = 1 - lit8 / 8;
                    shaft = lerp(1, smoothstep(0.1, 0.5, shade), gate);
                }
                else shaft = lerp(1, 0, gate);
                float3 v = normalize(i.centre - _WorldSpaceCameraPos);
                float cs = dot(v, sun.direction);
                float fwd = min(G3AtmoPhase(cs, 0.82) * 4 * PI, max(_G3AtmoMotes3.z, 1));     // 1 = isotropic
                float3 tint = lerp(float3(1.0, 0.86, 0.66), _G3AtmoStormCol.rgb / max(dot(_G3AtmoStormCol.rgb, float3(0.2126, 0.7152, 0.0722)), 1e-3), storm);
                float3 lit = sun.color * tint * (0.08 + 0.55 * fwd) * vis * shaft + SampleSH(float3(0, 1, 0)) * tint * (0.6 * storm);
                return half4(lit * a * _G3AtmoMotes.w, 0);
            }
            ENDHLSL
        }
    }
}
