// World remaster v2 (Docs/remaster-world.md section 10.1): exponential height fog with volumetric sun shafts.
// Pass 0 (half resolution): ray-march each pixel's view ray through height fog (density exp(-falloff (y - base)), base =
// ground under the camera + offset), sampling URP's main-light cascaded shadow map at every step: light through gaps in
// the canopy and past the castle walls becomes visible shafts (Henyey-Greenstein forward scattering). Beyond the
// marched distance the rest of the fog is integrated analytically, unshadowed. Output: in-scattered light (rgb) and
// transmittance (a). Pass 1 (full resolution): depth-aware upsample of pass 0 and composite col * T + inscatter.
// Pass 2 (runner-30 GPU, quarter resolution): reprojects the per-camera history with the previous view-projection, clamps
// it to the current 3x3 neighbourhood (no ghost trails) and blends 12 % of the new frame in.
// The fog colour is G3's (unity_FogColor, from the weather curves), the sun colour/direction URP's main light: the fog
// follows G3's day cycle and weather. G3WorldV2 sets the parameters (mirrored in WorldV2.FogOpticalDepth, tested).
Shader "Hidden/G3/Remaster/Volumetrics"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _G3V2Fog;     // x density at the base (1/m), y falloff (1/m), z base height (world m), w max amount
            float4 _G3V2Shaft;   // x sun in-scatter multiplier, y HG g, z steps, w marched distance (m)
            float4 _G3V2FogAmb;  // rgb ambient in-scatter colour (linear), w fog noise amount (remaster-gi, 0 = uniform)
            float4 _G3V2Temporal; // x weight of the current frame (1 = no history), y frame index (0..63, jitter)
            float4x4 _G3V2PrevVP; // previous frame's view-projection of this camera (reprojection)

            bool G3IsSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 1e-6;
            #else
                return raw >= 1 - 1e-6;
            #endif
            }
        ENDHLSL

        Pass
        {
            Name "G3V2Raymarch"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMarch
            #define _MAIN_LIGHT_SHADOWS_CASCADE 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/G3/Unity/Runtime/Environment/G3CloudsV2.hlsl"   // noise + wind travel (fog patches)
            #include "Assets/G3/Unity/Shaders/Remaster/G3Atmo.hlsl"            // atmo-1: desert dust / storm medium
            float4 _G3WindDirection;   // G3Wind: xyz direction, w strength

            float4 _G3CloudShadow;  // remaster-gi 16.4: x min x, y min z, z size (m), w strength (0 = off)
            TEXTURE2D(_G3CloudShadowTex);

            float G3CloudSunFog(float3 p)
            {
                float k = 1;
                [branch] if (_G3CloudShadow.w > 0)
                {
                    float2 uv = (p.xz - _G3CloudShadow.xy) / _G3CloudShadow.z;
                    k = 1 - _G3CloudShadow.w * (1 - SAMPLE_TEXTURE2D_LOD(_G3CloudShadowTex, sampler_LinearClamp, saturate(uv), 0).r);
                }
                return k;
            }

            float G3PhaseHG(float c, float g)
            {
                float g2 = g * g;
                return (1 - g2) / (4 * PI * pow(max(1 + g2 - 2 * g * c, 1e-4), 1.5));
            }

            // optical depth of the height fog along a segment from height h0 to h1 (relative to the base), length len
            float G3FogOD(float len, float h0, float h1)
            {
                float k = _G3V2Fog.y;
                float a = max(h0, 0), b = max(h1, 0), dh = b - a;
                float mean = abs(dh) < 1e-3 ? exp(-k * a) : (exp(-k * a) - exp(-k * b)) / (k * dh);
                return _G3V2Fog.x * len * mean;
            }

            float4 FragMarch(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                float raw = SampleSceneDepth(uv);
                bool sky = G3IsSky(raw);
                float3 cam = GetCameraPositionWS();
            #if UNITY_REVERSED_Z
                float d = sky ? 0.5 : raw;
            #else
                float d = sky ? 0.5 : lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
            #endif
                float3 pw = ComputeWorldSpacePosition(uv, d, UNITY_MATRIX_I_VP);
                float3 ray = pw - cam;
                // atmo-1: the desert medium also covers the sky (horizon haze band, storm), out to a capped length
                bool atmo = _G3AtmoOn.x > 0;
                float dist = sky ? (atmo ? _G3AtmoRay.w : 1e6) : length(ray);
                float3 v = normalize(ray);
                if (sky && atmo) pw = cam + v * dist;

                float steps = _G3V2Shaft.z;
                float marchLen = min(dist, _G3V2Shaft.w);
                float stepLen = marchLen / steps;
                float jitter = InterleavedGradientNoise(i.positionCS.xy, (int)_G3V2Temporal.y);   // rotates per frame: the temporal pass averages it out
                Light sun = GetMainLight();
                float cs = dot(v, sun.direction);
                float phase = G3PhaseHG(cs, _G3V2Shaft.y);
                float3 sunIn = sun.color * phase * _G3V2Shaft.x;
                float3 amb = _G3V2FogAmb.rgb;

                // atmo-1 media: dust (warm, Mie forward glow), near-ground dust (shafts), storm (swirling layers)
                float3 sunInDust = 0, ambDust = 0, stormCol = 0, sunInStorm = 0;
                float3 windDir = float3(0.5, 0, 0.866);
                float windSpeed = 0;
                [branch] if (atmo)
                {
                    // two lobes: Mie forward peak + a broad part (coarse dust and multiple scattering light the haze from every side)
                    sunInDust = sun.color * _G3AtmoTint.rgb * lerp(1 / (4 * PI), G3AtmoPhase(cs, _G3AtmoTint.w), 0.7) * _G3AtmoOn.z;
                    // over the sky the forward peak is G3's sun glow already (no white-out round the sun)
                    if (sky) sunInDust *= 0.35;
                    ambDust = _G3AtmoAmb.rgb;
                    // grade-1: soft cap of the dust's radiance (owner: from a high temple the whole distance went white - the
                    // forward Mie lobe x a long hazy path); identity below half the cap, never above the cap
                    [branch] if (_G3AtmoCap.x > 0)
                    {
                        float ld = dot(sunInDust + ambDust, float3(0.2126, 0.7152, 0.0722));
                        float ks = 1 / (1 + max(ld - 0.5 * _G3AtmoCap.x, 0) / _G3AtmoCap.x);
                        sunInDust *= ks; ambDust *= ks;
                    }
                    stormCol = _G3AtmoStormCol.rgb;
                    sunInStorm = sun.color * _G3AtmoTint.rgb * G3AtmoPhase(cs, 0.6) * _G3AtmoStormCol.w;
                    float3 wd = _G3WindDirection.xyz; wd.y = 0;
                    windDir = dot(wd, wd) > 1e-4 ? normalize(wd) : windDir;
                    windSpeed = 4 + 14 * saturate(_G3WindDirection.w / 5);
                }
                float sunUp = max(sun.direction.y, 0.08);

                float trans = 1;
                float3 scat = 0;
                float odAtmo = 0, odAll = 0;
                [loop] for (int k = 0; k < 96; k++)
                {
                    if (k >= (int)steps) break;
                    float t = (k + jitter) * stepLen;
                    float3 p = cam + v * t;
                    float hb = max(p.y - _G3V2Fog.z, 0);
                    float dens = _G3V2Fog.x * exp(-_G3V2Fog.y * hb);
                    // remaster-gi: drifting patches (2 octaves of value noise, ~40 m and ~13 m), mean kept at 1
                    float nz = 0.5;
                    [branch] if (_G3V2FogAmb.w > 0)
                    {
                        float2 tr2 = G3CloudTravel() * 0.6;
                        float3 np = p * 0.025 + float3(tr2.x, 0, tr2.y) * 0.025;
                        nz = G3CloudNoise3(np) * 0.65 + G3CloudNoise3(np * 3.1 + 7.3) * 0.35;
                        dens *= max(0, 1 + _G3V2FogAmb.w * (nz - 0.5) * 2.2);
                    }
                    float vis = MainLightRealtimeShadow(TransformWorldToShadowCoord(p)) * G3CloudSunFog(p);
                    float dDust = 0, dStorm = 0;
                    float3 src = dens * (sunIn * vis + amb);
                    [branch] if (atmo)
                    {
                        float rw = G3AtmoRegion(p.xz);
                        // the haze only in depth: the layers fade in from _G3AtmoHigh.z to .w metres (near and mid keep their
                        // contrast); the near-ground dust only scatters the sun (shafts, no milky ambient veil)
                        float depthK = smoothstep(_G3AtmoHigh.z, _G3AtmoHigh.w, t);
                        float dHaze = rw * depthK * (_G3AtmoDust.x * exp(-_G3AtmoDust.y * hb) + _G3AtmoHigh.x * exp(-_G3AtmoHigh.y * hb));
                        float dNear = rw * _G3AtmoDust.z * exp(-_G3AtmoDust.w * hb) * (0.4 + 1.2 * nz);
                        dDust = dHaze + dNear;
                        [branch] if (_G3AtmoStorm.x > 0)
                        {
                            float sw = lerp(1, G3AtmoSwirl(p, windDir, windSpeed, _G3AtmoOn.w), _G3AtmoStorm.z);
                            // + a dense sheet of blown sand in the lowest metres (streams across the ground at eye height)
                            dStorm = rw * _G3AtmoStorm.x * (exp(-_G3AtmoStorm.y * hb) + 1.5 * exp(-hb * 0.3)) * sw;
                        }
                        // the sun through the storm column above the point
                        float tsun = exp(-rw * _G3AtmoStorm.x * exp(-_G3AtmoStorm.y * hb) / max(_G3AtmoStorm.y, 1e-4) / sunUp);
                        src += dHaze * (sunInDust * vis * tsun + ambDust) + dNear * (sunInDust * vis * tsun) + dStorm * (stormCol + sunInStorm * vis * tsun);
                    }
                    float ext = dens + dDust + dStorm;
                    float tr = exp(-ext * stepLen);
                    scat += trans * src / max(ext, 1e-8) * (1 - tr);
                    trans *= tr;
                    odAtmo += (dDust + dStorm) * stepLen;
                    odAll += ext * stepLen;
                }
                [branch] if (!sky && !atmo && dist > marchLen)
                {
                    // the v2 tail as before
                    float3 p0 = cam + v * marchLen;
                    float tr = exp(-G3FogOD(dist - marchLen, p0.y - _G3V2Fog.z, pw.y - _G3V2Fog.z));
                    scat += trans * (sunIn * G3CloudSunFog(p0) + amb) * (1 - tr);
                    trans *= tr;
                }
                [branch] if (atmo && dist > marchLen)
                {
                    float3 p0 = cam + v * marchLen;
                    float len = dist - marchLen;
                    float h0 = p0.y - _G3V2Fog.z, h1 = pw.y - _G3V2Fog.z;
                    float odFog = sky ? 0 : G3FogOD(len, h0, h1);
                    float sunK = G3CloudSunFog(p0);
                    // region weight along the tail: start, middle, end (clamped to the map)
                    float3 pm = (p0 + pw) * 0.5;
                    // (the haze stays with the viewer's region a little: half of the start's weight, a ray out of Varant still hazy)
                    float r0 = G3AtmoRegion(p0.xz);
                    float rw = lerp((r0 + 2 * G3AtmoRegion(pm.xz) + G3AtmoRegion(pw.xz)) * 0.25, r0, 0.5);
                    // (the high thin layer only on geometry: over the sky it would pale the whole dome; G3's sky has its own)
                    float odDust = rw * len * (_G3AtmoDust.x * G3AtmoMeanExp(_G3AtmoDust.y, h0, h1) + (sky ? 0 : _G3AtmoHigh.x * G3AtmoMeanExp(_G3AtmoHigh.y, h0, h1))
                                             + _G3AtmoDust.z * G3AtmoMeanExp(_G3AtmoDust.w, h0, h1));
                    float odStorm = rw * len * _G3AtmoStorm.x * G3AtmoMeanExp(_G3AtmoStorm.y, h0, h1);
                    float tsun = exp(-rw * _G3AtmoStorm.x * exp(-_G3AtmoStorm.y * max(h0, 0)) / max(_G3AtmoStorm.y, 1e-4) / sunUp);
                    float3 radSum = odFog * (sunIn * sunK + amb) + odDust * (sunInDust * sunK * tsun + ambDust) + odStorm * (stormCol + sunInStorm * sunK * tsun);
                    float od = odFog + odDust + odStorm;
                    float tr = exp(-od);
                    scat += trans * (od > 1e-7 ? radSum / od : 0) * (1 - tr);
                    trans *= tr;
                    odAtmo += odDust + odStorm;
                    odAll += od;
                    // clear air (Rayleigh, scale height 8 km): the far distance turns blue; chromatic in-scatter (geometry only:
                    // G3's sky colours already are the sky's own Rayleigh light)
                    // grade-1: x the region weight along the ray (it ran wherever any desert was within 4 km: Myrtana's forests
                    // went purple-blue in the distance)
                    float3 beta = float3(5.8e-6, 13.5e-6, 33.1e-6) * _G3AtmoAmb.w * (sky ? 0 : 1) * rw;
                    float3 odR = beta * (dist * G3AtmoMeanExp(1.25e-4, cam.y - _G3V2Fog.z, h1));
                    float3 aR = 1 - exp(-odR);
                    scat += trans * _G3AtmoRay.rgb * aR;
                    float odRl = dot(odR, float3(0.2126, 0.7152, 0.0722));
                    trans *= exp(-odRl);
                    odAtmo += odRl;
                    odAll += odRl;
                }
                // cap: never more than _G3V2Fog.w of fog on a pixel (G3's own fog stays the main distance cue); atmo-1: the
                // desert haze may go up to its own cap (storm: almost opaque), in proportion to its share of the depth
                float share = atmo ? saturate(odAtmo / max(odAll, 1e-6)) : 0;
                float cap = lerp(_G3V2Fog.w, _G3AtmoStorm.w, share);
                float amount = 1 - trans;
                [branch] if (amount > cap)
                {
                    float s = cap / max(amount, 1e-4);
                    scat *= s;
                    trans = 1 - cap;
                }
                return float4(scat, trans);
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3V2Composite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            TEXTURE2D(_G3V2VolTex);
            float4 _G3V2VolTex_TexelSize;

            half4 FragComposite(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float raw = SampleSceneDepth(uv);
                float d0 = G3IsSky(raw) ? 1e6 : LinearEyeDepth(raw, _ZBufferParams);
                float2 hs = _G3V2VolTex_TexelSize.xy;
                float2 f = uv / hs - 0.5;
                float2 b = floor(f);
                float2 fr = f - b;
                float4 acc = 0;
                float wsum = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 o = float2(k & 1, k >> 1);
                    float2 uk = (b + o + 0.5) * hs;
                    float rk = SampleSceneDepth(uk);
                    float dk = G3IsSky(rk) ? 1e6 : LinearEyeDepth(rk, _ZBufferParams);
                    float bil = (o.x > 0 ? fr.x : 1 - fr.x) * (o.y > 0 ? fr.y : 1 - fr.y);
                    float w = bil / (1e-3 + abs(dk - d0) / max(d0, 1e-3));
                    acc += SAMPLE_TEXTURE2D_LOD(_G3V2VolTex, sampler_PointClamp, uk, 0) * w;
                    wsum += w;
                }
                float4 vol = acc / max(wsum, 1e-5);
                col.rgb = col.rgb * vol.a + vol.rgb;
                return col;
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3V2Temporal"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTemporal

            TEXTURE2D(_G3V2HistVol);
            float4 _G3V2LowTexel;   // 1/width, 1/height of the low-res target

            float4 FragTemporal(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                float4 cur = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0);
                [branch] if (_G3V2Temporal.x >= 0.999) return cur;
                float2 ts = _G3V2LowTexel.xy;
                float4 mn = cur, mx = cur;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float2 o = float2(k < 3 ? k - 1 : k < 5 ? (k == 3 ? -1 : 1) : k - 6, k < 3 ? -1 : k < 5 ? 0 : 1);
                    float4 n = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv + o * ts, 0);
                    mn = min(mn, n); mx = max(mx, n);
                }
                float raw = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                float d = G3IsSky(raw) ? 1e-5 : raw;
            #else
                float d = G3IsSky(raw) ? 1 - 1e-5 : lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
            #endif
                float3 pw = ComputeWorldSpacePosition(uv, d, UNITY_MATRIX_I_VP);
                float2 prevUV = ComputeNormalizedDeviceCoordinates(pw, _G3V2PrevVP);
                [branch] if (any(prevUV < 0) || any(prevUV > 1)) return cur;
                float4 hist = SAMPLE_TEXTURE2D_LOD(_G3V2HistVol, sampler_LinearClamp, prevUV, 0);
                hist = clamp(hist, mn, mx);
                return lerp(hist, cur, _G3V2Temporal.x);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
