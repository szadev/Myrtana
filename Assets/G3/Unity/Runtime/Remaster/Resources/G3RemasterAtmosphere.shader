// World remaster aerial perspective + distance clarity (Docs/remaster-world.md sections 7-8): a full-screen pass after
// the opaque objects and the sky (G3WorldRemasterLook injects it only while the world remaster switch is on). G3's own
// linear fog stays the reference; this pass must not grey the distance out (r1 in-engine A/B: it did). So:
//  * aerial: exponential height haze integrated along each pixel's ray, capped low (aerialMax), the reference height
//    clamped so nothing below it gets denser (views from high ground stay clear). Only aerialLift of it is added as light
//    (that part lifts blacks); the rest is a luminance-preserving tint towards G3's fog hue (cool, deep distance);
//  * clarity: depth-aware unsharp mask (4 diagonal taps, neighbours across a depth edge are ignored, so no halos round
//    silhouettes against the sky) plus a little saturation, ramping in from clarityStart to clarityFull metres. It
//    restores the local contrast and colour that G3's fog and the mip filtering take from the 30-300 m band.
// Sky pixels are left alone. Parameters: WorldLookSettings / WorldRemasterLook (engine-free, tested).
// Pass 1 "G3RemasterGrade" (r3, after the transparents, every pixel): the remaster's colour grade. r2 used a URP Volume,
// which never reached the image: the game camera (G3World_Master) has no UniversalAdditionalCameraData, so URP post-
// processing is off and no Volume applies. The grade is mirrored by WorldRemasterLook.GradeColor (tested).
Shader "Hidden/G3/Remaster/Atmosphere"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "G3RemasterAtmosphere"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _G3Atmos;      // x density (1/m), y height falloff (1/m), z max amount, w reference height (world m)
            float4 _G3AtmosSun;   // x forward-scatter strength, y lift fraction (rest of the haze is a luminance-kept tint)
            float4 _G3Clarity;    // x strength, y start (m), z full (m), w limit (per-pixel change)
            float4 _G3Clarity2;   // x radius (px), y saturation boost at full distance

            bool IsSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 1e-6;
            #else
                return raw >= 1 - 1e-6;
            #endif
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float raw = SampleSceneDepth(uv);
                if (IsSky(raw)) return col;
            #if UNITY_REVERSED_Z
                float depth = raw;
            #else
                float depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
            #endif
                float3 pw = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                float3 cam = GetCameraPositionWS();
                float3 d = pw - cam;
                float dist = length(d);

                // --- clarity (before the haze, so the haze is not sharpened) ---
                float cw = _G3Clarity.x * smoothstep(_G3Clarity.y, _G3Clarity.z, dist);
                [branch] if (cw > 1e-3)
                {
                    float2 px = _G3Clarity2.x / _ScreenParams.xy;
                    float lin = LinearEyeDepth(raw, _ZBufferParams);
                    float2 offs[4] = { float2(px.x, px.y), float2(-px.x, px.y), float2(px.x, -px.y), float2(-px.x, -px.y) };
                    half3 sum = 0;
                    [unroll] for (int k = 0; k < 4; k++)
                    {
                        float2 u = uv + offs[k];
                        half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, u).rgb;
                        float rn = SampleSceneDepth(u);
                        float ln = IsSky(rn) ? 1e6 : LinearEyeDepth(rn, _ZBufferParams);
                        // a neighbour across a depth edge (another object, the sky) counts as the centre: no halo
                        sum += abs(ln - lin) < lin * 0.08 ? c : col.rgb;
                    }
                    half3 blur = sum * 0.25h;
                    half3 detail = clamp((col.rgb - blur) * cw, -_G3Clarity.w, _G3Clarity.w);
                    col.rgb = max(col.rgb + detail, 0);
                    half l = dot(col.rgb, half3(0.2126h, 0.7152h, 0.0722h));
                    col.rgb = max(lerp(l.xxx, col.rgb, 1 + _G3Clarity2.y * cw / max(_G3Clarity.x, 1e-3)), 0);
                }

                // --- aerial perspective ---
                [branch] if (_G3Atmos.z > 1e-4)
                {
                    float k = _G3Atmos.y;
                    float h0 = max(cam.y - _G3Atmos.w, 0), h1 = max(pw.y - _G3Atmos.w, 0);
                    float dh = h1 - h0;
                    float mean = (abs(dh) < 1e-3 || k < 1e-6) ? exp(-k * h0) : (exp(-k * h0) - exp(-k * h1)) / (k * dh);
                    float amount = min(1 - exp(-_G3Atmos.x * dist * mean), _G3Atmos.z);
                    float3 v = d / max(dist, 1e-4);
                    float cs = saturate(dot(v, _MainLightPosition.xyz));
                    float3 sunTint = min(_MainLightColor.rgb, 4) * 0.25;
                    float3 inscatter = lerp(unity_FogColor.rgb, unity_FogColor.rgb * 0.7 + sunTint * 0.5, pow(cs, 6) * _G3AtmosSun.x);
                    float il = max(dot(inscatter, float3(0.2126, 0.7152, 0.0722)), 1e-4);
                    half3 tinted = col.rgb * lerp(1, inscatter / il, amount);           // hue only, luminance kept
                    half3 lifted = lerp(col.rgb, inscatter, amount);                     // classic haze (lifts blacks)
                    col.rgb = lerp(tinted, lifted, _G3AtmosSun.y);
                }
                return col;
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3RemasterGrade"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragGrade
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _G3Grade;   // x contrast (-1..1), y saturation (-1..1), z black lift (display), w gain (display)
            float4 _G3GradeV2; // v2 highlight shoulder: x knee (0 = off), y white (WorldV2.Shoulder, tested), z path-to-white D (remaster-look)
            // lighting-new (section 21, WorldLightNew): exposure, grade and filmic tonemap of the "New" lighting
            float4 _G3NewGrade; // x 1 = on, y tonemapper (0 none, 1 Khronos PBR Neutral, 2 ACES fit), z log contrast, w Purkinje strength
            float4 _G3NewWb;    // rgb white balance (luminance 1), a saturation offset
            TEXTURE2D(_G3NewExposureTex);   // 1x1: r exposure, g metered mean log2 luminance, b EV, a target EV
            // atmo-1 (section 23, DesertAtmo): desert grade - filmic curve (Lottes, mid grey kept), split tone, golden hour, noon key
            float4 _G3AtmoGrade;      // x weight (0 = off), y contrast a, z b, w c (f(x) = x^a / (x^(a d) b + c), d = 0.97)
            float4 _G3AtmoGrade2;     // x exposure, y saturation offset, z path-to-white D
            float4 _G3AtmoShadowTint; // rgb (luminance 1)
            float4 _G3AtmoHighTint;   // rgb (luminance 1)
            // grade-1 (section 27): global output saturation / vibrance (WorldRemasterLook.Vibrance, tested) and the Neutral toe
            float4 _G3Vibrance;       // x saturation offset (-1..1), y soft-limit strength (0..1), z knee (HSV saturation)
            float4 _G3NewTone;        // x PBR Neutral toe strength (0..1), applied hue-kept (WorldLightNew.NeutralToe), y highlight white (scene, 0 = off)

            // display-space rgb: saturation around the luminance, then the HSV saturation above the knee compressed (the max
            // channel kept): vivid colours lose their edge, greys and muted colours are untouched
            float3 G3Vibrance(float3 d)
            {
                const float3 w = float3(0.2126, 0.7152, 0.0722);
                float l = dot(d, w);
                d = max(l + (d - l) * (1 + _G3Vibrance.x), 0);
                float mx = max(d.r, max(d.g, d.b)), mn = min(d.r, min(d.g, d.b));
                float s = (mx - mn) / max(mx, 1e-4);
                float knee = _G3Vibrance.z;
                [branch] if (_G3Vibrance.y > 0 && s > knee)
                {
                    float x = saturate((s - knee) / max(1 - knee, 1e-3));
                    float s2 = knee + (1 - knee) * x * (1 - 0.5 * _G3Vibrance.y * x);
                    d = mx - (mx - d) * (s2 / s);
                }
                return d;
            }

            // DesertAtmo grade on linear scene colour; returns display-ready linear 0..1 (the filmic curve replaces the shoulder)
            float3 G3AtmoGrade(float3 c)
            {
                const float3 w = float3(0.2126, 0.7152, 0.0722);
                c *= _G3AtmoGrade2.x;
                float l = dot(c, w);
                c = max(l + (c - l) * (1 + _G3AtmoGrade2.y), 0);
                l = dot(c, w);
                float tt = smoothstep(-4.5, 0.3, log2(max(l, 1e-5)));
                // the warm highlight tint spares saturated blue (the sky stays blue, not lavender)
                float bluish = saturate((c.b - max(c.r, c.g)) / max(c.b, 1e-4) * 4);
                c *= lerp(_G3AtmoShadowTint.rgb, lerp(_G3AtmoHighTint.rgb, 1, bluish), tt);
                float m = max(c.r, max(c.g, c.b));
                [branch] if (m > 1e-6)
                {
                    float ma = pow(m, _G3AtmoGrade.y);
                    float nm = min(ma / (pow(ma, 0.97) * _G3AtmoGrade.z + _G3AtmoGrade.w), 1);
                    c *= nm / m;
                    // path to white for compressed highlights (no flat clipped hue on the sunlit sand / the sun)
                    float g = 1 - 1 / (_G3AtmoGrade2.z * max(m - nm, 0) * smoothstep(0.35, 0.9, nm) + 1);
                    c = lerp(c, nm.xxx, g);
                }
                return saturate(c);
            }

            // WorldLightNew.Neutral (Khronos PBR Neutral 2024)
            float3 G3Neutral(float3 c)
            {
                const float start = 0.8 - 0.04, desat = 0.15;
                float x = min(c.r, min(c.g, c.b));
                float offset = (x < 0.08 ? x - 6.25 * x * x : 0.04) * _G3NewTone.x;
                // grade-1: the toe offset as a ratio of the luminance (hue and saturation kept; per channel it pushed the
                // weakest channel of dark colours to 0: saturated, crushed darks - black-green moss, black nights)
                float lc = dot(c, float3(0.2126, 0.7152, 0.0722));
                c *= max(1 - offset / max(lc, 1e-5), 0);
                float peak = max(c.r, max(c.g, c.b));
                if (peak < start) return c;
                const float d = 1 - start;
                float newPeak = 1 - d * d / (peak + d - start);
                c *= newPeak / peak;
                float g = 1 - 1 / (desat * (peak - newPeak) + 1);
                return lerp(c, newPeak.xxx, g);
            }

            float3 G3AcesFit(float3 x) { x = max(x, 0); return saturate(x * (2.51 * x + 0.03) / (x * (2.43 * x + 0.59) + 0.14)); }

            // extended Reinhard above the knee: identity below, C1-continuous, white -> 1
            float G3Shoulder(float x, float knee, float white)
            {
                if (x <= knee) return x;
                float a = 1 - knee;
                float t = (x - knee) / a, T = max((white - knee) / a, 1);
                return min(knee + a * (t * (1 + t / (T * T)) / (1 + t)), 1);
            }

            // linear scene colour; same maths as WorldRemasterLook.GradeColor
            half4 FragGrade(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord);
                float3 c = max(col.rgb, 0);
                const float3 w = float3(0.2126, 0.7152, 0.0722);
                // saturation around the pixel's luminance (luminance kept)
                float l;
                [branch] if (_G3NewGrade.x > 0)
                {
                    float4 e = LOAD_TEXTURE2D(_G3NewExposureTex, int2(0, 0));
                    c *= max(e.r, 0) * _G3NewWb.rgb;
                    // night: rod vision (WorldLightNew.Purkinje) - bluer and desaturated at a low metered luminance
                    float t = saturate((e.g + 9) / 4);
                    float pk = _G3NewGrade.w * (1 - t * t * (3 - 2 * t));
                    l = dot(c, w);
                    c = lerp(c, l * float3(0.853, 1.007, 1.368), pk * 0.65);
                    l = dot(c, w);
                    c = max(l + (c - l) * (1 + _G3NewWb.a), 0);
                    l = dot(c, w);
                    if (l > 1e-6) c *= 0.18 * pow(l / 0.18, 1 + _G3NewGrade.z) / l;
                    [branch] if (_G3AtmoGrade.x > 0)
                    {
                        l = dot(c, w);
                        float3 tint = lerp(_G3AtmoShadowTint.rgb, _G3AtmoHighTint.rgb, smoothstep(-4.5, 0.3, log2(max(l, 1e-5))));
                        // grade-1: the warm highlight tint spares saturated blue here too (the New path tinted the sky lavender)
                        float bluish = saturate((c.b - max(c.r, c.g)) / max(c.b, 1e-4) * 4);
                        tint = lerp(tint, 1, bluish * smoothstep(-4.5, 0.3, log2(max(l, 1e-5))));
                        c = lerp(c, max(l + (c * tint - l) * (1 + _G3AtmoGrade2.y), 0), _G3AtmoGrade.x);
                    }
                    // grade-1: a long highlight shoulder on the max channel before the tonemapper (hue kept): a sky / haze seen
                    // from a dark interior keeps its gradient instead of going flat white (WorldLightNew.HighlightShoulder)
                    [branch] if (_G3NewTone.y > 1)
                    {
                        float mh = max(c.r, max(c.g, c.b));
                        if (mh > 0.8) c *= G3Shoulder(mh, 0.8, _G3NewTone.y) / mh;
                    }
                    c = _G3NewGrade.y < 0.5 ? saturate(c) : _G3NewGrade.y < 1.5 ? G3Neutral(c) : G3AcesFit(c);
                    float3 dn = G3Vibrance(pow(max(c, 0), 1 / 2.2));
                    dn = (dn + _G3Grade.z * (1 - saturate(dn))) * (1 + _G3Grade.w);
                    col.rgb = pow(max(dn, 0), 2.2);
                    return half4(col);
                }
                l = dot(c, w);
                c = max(l + (c - l) * (1 + _G3Grade.y), 0);
                // contrast: log-space around mid-grey 0.18 on luminance (hue kept; URP ColorAdjustments works in LogC)
                l = dot(c, w);
                if (l > 1e-6) c *= 0.18 * pow(l / 0.18, 1 + _G3Grade.x) / l;
                float3 cPre = c;   // atmo-1: before the shoulder
                // black lift and gain in display space (~gamma 2.2): lift -0.01 moves the black point 1 % down
                [branch] if (_G3GradeV2.x > 0)
                {
                    // v2: roll the highlights off on the max channel (hue kept): the stronger v2 sun does not clip
                    float m = max(c.r, max(c.g, c.b));
                    if (m > _G3GradeV2.x)
                    {
                        float nm = G3Shoulder(m, _G3GradeV2.x, _G3GradeV2.y);
                        c *= nm / m;
                        // remaster-look: path to white (Khronos PBR Neutral, D = z): compressed highlights desaturate
                        // instead of clipping to a flat hue (sun on sand/snow); WorldV2Light.DesatAmount
                        float g = _G3GradeV2.z > 0 ? 1 - 1 / (_G3GradeV2.z * (m - nm) + 1) : 0;
                        c = lerp(c, nm.xxx, g);
                    }
                }
                [branch] if (_G3AtmoGrade.x > 0) c = lerp(c, G3AtmoGrade(cPre), _G3AtmoGrade.x);
                float3 d = G3Vibrance(pow(max(c, 0), 1 / 2.2));
                d = (d + _G3Grade.z * (1 - saturate(d))) * (1 + _G3Grade.w);
                col.rgb = pow(max(d, 0), 2.2);
                return half4(col);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
