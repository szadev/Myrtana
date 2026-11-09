// remaster-ssgi (Docs/remaster-world.md section 19): screen-space indirect light for Modern lighting, after
// Therrien, Levesque, Gilet 2023, "Screen Space Indirect Lighting with Visibility Bitmask" (The Visual Computer 39,
// arXiv 2301.11376). Own implementation from the paper (no third-party code). Half resolution:
//   pass 0 trace:    per pixel `slices` screen slices (rotated per frame), `steps` depth samples per side; each sample is a
//                    slab of constant thickness occupying the sectors between its front and back angle (32-bit mask per
//                    slice). Newly occupied sectors remove sky light (cos-weighted share -> visibility) and add the
//                    sample's radiance (1/4-res frame colour = albedo x G3's light) -> rgb indirect, a visibility.
//   pass 1 temporal: camera reprojection (previous view-projection), depth test, age-based blend (WorldSsgi.Blend);
//                    MRT: history (rgb, a) + (linear depth, age).
//   pass 2 filter:   9-tap depth-aware blur (radius 2 half-res px).
// The light-ratio pass (Hidden/G3/Remaster/LightRatio) multiplies Modern lighting's fill by the visibility and adds the
// indirect light x gain. Maths mirrored in WorldSsgi (tested).
Shader "Hidden/G3/Remaster/Ssgi"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _G3Ssgi;      // x radius (m), y max radius (half-res px), z thickness (m), w fade distance (m)
            float4 _G3Ssgi2;     // x slices, y steps per side, z AO strength, w bounce gain
            float4 _G3Ssgi3;     // x frame index, y projection scale (half-res px per m at depth 1), z 1 = camera normals texture valid, w 1 = flip y of the previous projection
            float4 _G3SsgiSize;  // xy half-res size, zw 1/size

            bool G3SsIsSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 1e-6;
            #else
                return raw >= 1 - 1e-6;
            #endif
            }

            float3 G3SsPos(float2 uv, float raw)
            {
            #if UNITY_REVERSED_Z
                float d = raw;
            #else
                float d = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
            #endif
                return ComputeWorldSpacePosition(uv, d, UNITY_MATRIX_I_VP);
            }

            // the half-res pixel's sample point: the full-res depth texel centre (as the ratio pass, no row flips)
            float2 G3SsSnap(float2 uv) { return (floor(uv * _CameraDepthTexture_TexelSize.zw) + 0.5) * _CameraDepthTexture_TexelSize.xy; }

            float G3SsIgn(float2 px) { return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715)))); }
        ENDHLSL

        Pass
        {
            Name "G3SsgiTrace"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTrace
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            TEXTURE2D(_G3SsgiColor);   // 1/4-res frame (rgb), a mean linear depth (0 = sky)

            float3 G3SsNormal(float2 uv, float3 p)
            {
                float3 nn = 0;
                [branch] if (_G3Ssgi3.z > 0.5) nn = SampleSceneNormals(uv);
                float2 t = _CameraDepthTexture_TexelSize.xy;
                float3 pr = G3SsPos(uv + float2(t.x, 0), SampleSceneDepth(uv + float2(t.x, 0)));
                float3 pl = G3SsPos(uv - float2(t.x, 0), SampleSceneDepth(uv - float2(t.x, 0)));
                float3 pu = G3SsPos(uv + float2(0, t.y), SampleSceneDepth(uv + float2(0, t.y)));
                float3 pd = G3SsPos(uv - float2(0, t.y), SampleSceneDepth(uv - float2(0, t.y)));
                float3 dx = dot(pr - p, pr - p) < dot(p - pl, p - pl) ? pr - p : p - pl;
                float3 dy = dot(pu - p, pu - p) < dot(p - pd, p - pd) ? pu - p : p - pd;
                float3 n = normalize(cross(dy, dx));
                n = dot(n, GetCameraPositionWS() - p) < 0 ? -n : n;
                return dot(nn, nn) > 0.25 ? normalize(nn) : n;
            }

            // WorldSsgi.SectorMask: sectors touched by [u0, u1] (ceil)
            uint G3SsMask(float u0, float u1)
            {
                float a = saturate(min(u0, u1)), b = saturate(max(u0, u1));
                uint start = (uint)min(31.0, floor(a * 32));
                uint count = (uint)min(ceil((b - a) * 32), 32.0 - start);
                if (count == 0) return 0;
                uint m = count >= 32 ? 0xFFFFFFFFu : (1u << count) - 1u;
                return m << start;
            }

            float4 FragTrace(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = G3SsSnap(i.texcoord);
                float raw = SampleSceneDepth(uv);
                if (G3SsIsSky(raw)) return float4(0, 0, 0, 1);
                float3 p = G3SsPos(uv, raw);
                float3 cam = GetCameraPositionWS();
                float dist = length(p - cam);
                if (dist > _G3Ssgi.w) return float4(0, 0, 0, 1);
                float lin = LinearEyeDepth(raw, _ZBufferParams);
                float rPx = min(_G3Ssgi.y, _G3Ssgi.x * _G3Ssgi3.y / max(lin, 0.05));   // WorldSsgi.RadiusPixels
                if (rPx < 1.5) return float4(0, 0, 0, 1);
                float3 n = G3SsNormal(uv, p);
                float3 V = (cam - p) / dist;
                float thick = _G3Ssgi.z * max(1, dist / 15);                         // WorldSsgi.Thickness
                float2 px = i.texcoord * _G3SsgiSize.xy;
                float frame = _G3Ssgi3.x;
                float jA = G3SsIgn(px + frame * 5.588238);
                float jB = G3SsIgn(px.yx + 17 + frame * 7.137);
                int slices = (int)_G3Ssgi2.x, steps = (int)_G3Ssgi2.y;
                float visSum = 0, wSum = 1e-4;
                float3 ind = 0;
                [loop] for (int s = 0; s < slices; s++)
                {
                    float phi = (s + jA) * PI / slices;
                    float2 dir = float2(cos(phi), sin(phi));
                    float2 duv = dir * _G3SsgiSize.zw;   // one half-res pixel along the slice
                    // slice tangent from the screen itself: a point at the same depth a few pixels along +dir (sign-safe)
                    float3 dT = G3SsPos(uv + duv * 4, raw) - p;
                    float3 T = normalize(dT - V * dot(dT, V));
                    float3 sN = normalize(cross(V, T));
                    float3 pn = n - sN * dot(n, sN);
                    float lenP = length(pn);
                    if (lenP < 1e-3) continue;
                    pn /= lenP;
                    float N = sign(dot(pn, T)) * acos(clamp(dot(pn, V), -1, 1));   // projected normal angle from V (towards +T)
                    uint occ = 0;
                    float occW = 0;
                    float3 sliceInd = 0;
                    [unroll] for (int side = 0; side < 2; side++)
                    {
                        float sg = side == 0 ? 1 : -1;
                        [loop] for (int k = 0; k < steps; k++)
                        {
                            float t = (k + jB) / steps;
                            float off = max(k + 1.0, t * t * rPx);   // exponential steps, at least one pixel apart
                            float2 suv = uv + sg * duv * off;
                            if (any(suv <= 0) || any(suv >= 1)) break;
                            float sraw = SampleSceneDepth(suv);
                            if (G3SsIsSky(sraw)) continue;
                            float3 d = G3SsPos(suv, sraw) - p;
                            d -= sN * dot(d, sN);                    // into the slice plane
                            float l2 = dot(d, d);
                            if (l2 < 1e-6) continue;
                            float3 db = d - V * thick;               // the slab's back face
                            float tf = sg * acos(clamp(dot(d * rsqrt(l2), V), -1, 1));
                            float tb = sg * acos(clamp(dot(normalize(db), V), -1, 1));
                            float u0 = saturate((tf - N + HALF_PI) / PI), u1 = saturate((tb - N + HALF_PI) / PI);   // WorldSsgi.HemiUnit
                            uint m = G3SsMask(u0, u1);
                            uint nb = m & ~occ;
                            [branch] if (nb != 0)
                            {
                                // WorldSsgi.CosShare: new sectors x pi/64 x cos(angle from the normal)
                                float w = countbits(nb) * (PI / 64) * max(0, cos(0.5 * (tf + tb) - N));
                                occW += w;
                                // farther than the radius: occludes (thin slab) but its light falls off (inverse square past R)
                                float fall = saturate(_G3Ssgi.x * _G3Ssgi.x / max(l2, 1e-4));
                                sliceInd += SAMPLE_TEXTURE2D_LOD(_G3SsgiColor, sampler_LinearClamp, suv, 0).rgb * (w * fall);
                                occ |= m;
                            }
                        }
                    }
                    visSum += lenP * saturate(1 - occW);
                    ind += lenP * sliceInd;
                    wSum += lenP;
                }
                float vis = wSum > 1e-3 ? visSum / wSum : 1;
                return float4(min(ind / wSum, 16), vis);
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3SsgiTemporal"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTemporal

            TEXTURE2D(_G3SsgiHist);       // previous accumulated (rgb indirect, a visibility)
            TEXTURE2D(_G3SsgiHistDepth);  // previous (linear depth, age)
            float4x4 _G3SsgiPrevVP;       // previous frame's (non-jittered, render-into-texture) view-projection
            float4 _G3SsgiTemporal;       // x history weight (v2SsgiHistory), y 1 = history valid

            struct TOut { float4 hist : SV_Target0; float4 depth : SV_Target1; };

            TOut FragTemporal(Varyings i)
            {
                TOut o;
                float2 uv = G3SsSnap(i.texcoord);
                float raw = SampleSceneDepth(uv);
                float4 cur = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, i.texcoord, 0);
                if (G3SsIsSky(raw)) { o.hist = float4(0, 0, 0, 1); o.depth = 0; return o; }
                float lin = LinearEyeDepth(raw, _ZBufferParams);
                float3 p = G3SsPos(uv, raw);
                float4 pc = mul(_G3SsgiPrevVP, float4(p, 1));
                float2 puv = pc.xy / pc.w * 0.5 + 0.5;
                if (_G3Ssgi3.w > 0.5) puv.y = 1 - puv.y;
                float age = 0;
                float4 h = cur;
                [branch] if (_G3SsgiTemporal.y > 0.5 && pc.w > 0 && all(puv > 0) && all(puv < 1))
                {
                    float4 hd = SAMPLE_TEXTURE2D_LOD(_G3SsgiHistDepth, sampler_PointClamp, puv, 0);
                    if (hd.x > 0 && abs(pc.w - hd.x) <= 0.05 * pc.w + 0.05)   // WorldSsgi.HistoryValid
                    {
                        age = min(hd.y + 1, 32);
                        h = SAMPLE_TEXTURE2D_LOD(_G3SsgiHist, sampler_LinearClamp, puv, 0);
                    }
                }
                float a = max(1 / (age + 1), 1 - _G3SsgiTemporal.x);   // WorldSsgi.Blend
                o.hist = lerp(h, cur, a);
                o.depth = float4(lin, age, 0, 0);
                return o;
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3SsgiFilter"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragFilter

            TEXTURE2D(_G3SsgiDepthNow);   // this frame's (linear depth, age)
            float4 _G3SsgiDepthNow_TexelSize;
            float _G3SsgiDebug;           // runner: 3 = history age / 16 in rgb (1, 2 are shown by the ratio pass)

            float4 FragFilter(Varyings i) : SV_Target
            {
                float2 c = _G3SsgiDepthNow_TexelSize.xy;
                float4 dc = SAMPLE_TEXTURE2D_LOD(_G3SsgiDepthNow, sampler_PointClamp, i.texcoord, 0);
                float4 center = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, i.texcoord, 0);
                if (dc.x <= 0) return float4(0, 0, 0, 1);
                // fewer frames of history = wider footprint (fresh disocclusions are noisier)
                float r = dc.y < 4 ? 2 : 1;
                float4 sum = center; float ws = 1;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0) continue;
                    float2 o = float2(x, y) * c * r;
                    float dz = SAMPLE_TEXTURE2D_LOD(_G3SsgiDepthNow, sampler_PointClamp, i.texcoord + o, 0).x;
                    float w = dz > 0 ? saturate(1 - abs(dz - dc.x) / (0.03 * dc.x + 0.05)) : 0;
                    w *= (x == 0 || y == 0) ? 1 : 0.6;
                    sum += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, i.texcoord + o, 0) * w; ws += w;
                }
                float4 res = sum / ws;
                if (_G3SsgiDebug > 2.5) return float4((dc.y / 16).xxx, 1);   // history age (the ratio pass shows rgb)
                return res;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
