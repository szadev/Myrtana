// World remaster look (Docs/remaster-world.md section 11, agent remaster-look): sky occlusion + sun contact shadows in a
// forward renderer, without touching any material. Per pixel (pass 0, half resolution): world position and normal from
// the depth buffer; A = G3's ambient irradiance (the SH probe G3Environment writes, evaluated on the CPU into an ambient
// cube), D = the sun (URP main light, NdotL, cascaded shadow map), L = the local lights (Forward+ cluster loop, no
// shadows). v = sky visibility from the top-down occluder map (_G3SkyOccMap: R highest solid surface by physics raycasts,
// G highest tree crown), c = contact-shadow transmittance (screen-space march towards the sun). The frame colour
// albedo·(A + D + L) is multiplied by (A·v + D·c + L) / (A + D + L): albedo cancels, torches keep their light, the shade
// under crowns, roofs and cave ceilings loses the sky light it cannot see. Pass 1 (full resolution): depth-aware upsample
// and multiply, before the transparents and before the fog/aerial passes. Maths mirrored in WorldV2Light (tested).
Shader "Hidden/G3/Remaster/LightRatio"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            bool G3LrIsSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 1e-6;
            #else
                return raw >= 1 - 1e-6;
            #endif
            }

            float3 G3LrPos(float2 uv, float raw)
            {
            #if UNITY_REVERSED_Z
                float d = raw;
            #else
                float d = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
            #endif
                return ComputeWorldSpacePosition(uv, d, UNITY_MATRIX_I_VP);
            }
        ENDHLSL

        Pass
        {
            Name "G3LightRatio"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRatio
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_local _ _G3LR_EXPOSURE
            #define _MAIN_LIGHT_SHADOWS_CASCADE 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            float _G3LrNormals;     // fix-178: 1 = the camera normals texture (the depth-normals prepass) is valid this frame
            float4 _G3LightRatio;   // x sky-occlusion strength (0 = off), y crown density, z contact strength (0 = off), w contact length (m)
            float4 _G3LightRatio2;  // x contact steps, y contact fade (m), z ratio floor, w frame index (jitter)
            float4 _G3LightRatio3;  // fix-258: x interior floor (WorldV2Light.InteriorFloor; 0 = off)
            float4 _G3SkyOccWindow; // x min x, y min z, z size (m), w 1 = map valid
            float4 _G3AmbCube[6];   // ambient irradiance along +x -x +y -y +z -z (linear)
            TEXTURE2D(_G3SkyOccMap);
            float4 _G3Bounce;       // x bounce strength (0 = off), yz 1/size of the bounce texture
            TEXTURE2D(_G3BounceTex); // 1/16 res: rgb mean lit colour of the neighbourhood (sky excluded), a its mean linear depth
            // remaster-gi (section 16): ambient-cube irradiance probes (G3WorldGi), toroidal 3D textures (repeat addressing)
            float4 _G3GiParams;     // x strength (0 = off), y voxel size (m), z max ratio, w keep (min part of the old estimate)
            float4 _G3GiWindow;     // xyz min corner of the probe window (m), w probe spacing (m)
            TEXTURE3D(_G3GiVox);    // runner debug 4: the radiance voxels (rgb, a occupancy, -1 unseen)
            float4 _G3GiOrigin;     // runner debug 4: voxel origin (world voxel index)
            float _G3GiDebug;
            float4 _G3GiModern;     // remaster-gi2 (section 17, WorldGi.ModernGlobal): x fill scale (0 = Modern lighting off), y occlusion strength
            float _G3GiModernSky;   // remaster-gi2: directional sky under Modern lighting (fill x (1 + this x n.y), WorldGi.ModernSky)
            float _G3GiTint;        // bounce hue strength (G3WorldGi, v2GiTint)       // runner (with _G3LightRatioDebug 1): 1 = probe weight, 2 = probe irradiance / G3 ambient, 3 = cloud shadow
            TEXTURE3D(_G3GiCube0); TEXTURE3D(_G3GiCube1); TEXTURE3D(_G3GiCube2);
            TEXTURE3D(_G3GiCube3); TEXTURE3D(_G3GiCube4); TEXTURE3D(_G3GiCube5);
            TEXTURE3D(_G3GiConf);   // probe weight (confidence, dilated)
            // remaster-gi 16.4: cloud shadow map (G3CloudShadow)
            float4 _G3CloudShadow;  // x min x, y min z, z size (m), w strength (0 = off)
            TEXTURE2D(_G3CloudShadowTex);
            // remaster-ssgi (section 19, G3WorldSsgi): screen-space visibility bitmask GI at the ratio's resolution
            float4 _G3SsgiRatio;    // x 1 = valid, y AO strength, z bounce gain, w fade distance (m)
            float4 _G3SsgiRatio2;   // x multi-bounce blend, y AO power (WorldSsgi.FillVisibility)
            TEXTURE2D(_G3SsgiTex);  // rgb indirect irradiance from on-screen surfaces, a cos-weighted visibility
            float _G3SsgiDebug;     // runner (with _G3LightRatioDebug 1): 1 visibility, 2 indirect x gain, 3 history age
            // lighting-new (section 21, WorldLightNew): "New" value of the Modern lighting row
            float4 _G3NewLight;     // x 1 = on, y sky scale, z floor (x G3's fill), w max ratio
            float4 _G3NewLight2;    // x far ground albedo, y screen bounce gain, z AO power, w multi-bounce blend
            float4 _G3NewLight3;    // x sun scale
            float4 _G3NewSky[6];    // open-sky irradiance ambient cube (+x -x +y -y +z -z), from the sky cube (G3LightNew)

            float3 G3NewSkyIrr(float3 n)
            {
                float3 n2 = n * n;
                return n2.x * (n.x >= 0 ? _G3NewSky[0].rgb : _G3NewSky[1].rgb)
                     + n2.y * (n.y >= 0 ? _G3NewSky[2].rgb : _G3NewSky[3].rgb)
                     + n2.z * (n.z >= 0 ? _G3NewSky[4].rgb : _G3NewSky[5].rgb);
            }

            // WorldSsgi.MultiBounce (Jimenez et al. 2016): occluders reflect part of the light back, corners never go black
            float G3MultiBounce(float v)
            {
                const float al = 0.3;
                float a = 2.0404 * al - 0.3324, b = -4.7951 * al + 0.6417, c = 2.7552 * al + 0.6903;
                return max(v, ((v * a + b) * v + c) * v);
            }

            float G3CloudSun(float3 p)
            {
                float k = 1;
                [branch] if (_G3CloudShadow.w > 0)
                {
                    float2 uv = (p.xz - _G3CloudShadow.xy) / _G3CloudShadow.z;
                    float t = SAMPLE_TEXTURE2D_LOD(_G3CloudShadowTex, sampler_LinearClamp, saturate(uv), 0).r;
                    k = 1 - _G3CloudShadow.w * (1 - t);
                }
                return k;
            }

            // probe irradiance at q for normal n (WorldGi.EvalCube) and its weight (window edge fade x confidence, WorldGi.Weight)
            // probe faces at q for normal n (WorldGi.EvalCube): rgb = bounce irradiance, a = sky visibility; w = weight
            // (window edge fade x confidence, WorldGi.Weight)
            float4 G3GiProbe(float3 q, float3 n, out float w)
            {
                float sp = _G3GiWindow.w;
                float3 dims = float3(64, 32, 64);
                float3 u = (q - _G3GiWindow.xyz) / sp;
                float3 e3 = saturate(min(u - 0.5, dims - 0.5 - u) / 3);
                w = e3.x * e3.y * e3.z;
                float4 r = float4(0, 0, 0, 1);
                [branch] if (w > 0)
                {
                    float3 uvw = q / sp / dims;
                    w *= saturate(SAMPLE_TEXTURE3D_LOD(_G3GiConf, sampler_LinearRepeat, uvw, 0).r);
                    float3 n2 = n * n;
                    float4 ex = n.x >= 0 ? SAMPLE_TEXTURE3D_LOD(_G3GiCube0, sampler_LinearRepeat, uvw, 0) : SAMPLE_TEXTURE3D_LOD(_G3GiCube1, sampler_LinearRepeat, uvw, 0);
                    float4 ey = n.y >= 0 ? SAMPLE_TEXTURE3D_LOD(_G3GiCube2, sampler_LinearRepeat, uvw, 0) : SAMPLE_TEXTURE3D_LOD(_G3GiCube3, sampler_LinearRepeat, uvw, 0);
                    float4 ez = n.z >= 0 ? SAMPLE_TEXTURE3D_LOD(_G3GiCube4, sampler_LinearRepeat, uvw, 0) : SAMPLE_TEXTURE3D_LOD(_G3GiCube5, sampler_LinearRepeat, uvw, 0);
                    r = n2.x * ex + n2.y * ey + n2.z * ez;
                }
                return r;
            }

            // one-bounce irradiance (remaster-look2, 11.8): the frame colour around the pixel is albedo x irradiance of the
            // surroundings, i.e. the light they reflect; part of it reaches the pixel where the sky is hidden. 5 bilinear taps
            // of the 1/16 res blurred frame (~50 px), weighted by depth similarity (no bounce from the far background).
            float3 G3Bounce(float2 uv, float lin)
            {
                float2 t = _G3Bounce.yz * 1.5;
                float2 o[5] = { float2(0, 0), float2(t.x, t.y), float2(-t.x, t.y), float2(t.x, -t.y), float2(-t.x, -t.y) };
                float3 sum = 0; float ws = 1e-4;
                [unroll] for (int k = 0; k < 5; k++)
                {
                    float4 b = SAMPLE_TEXTURE2D_LOD(_G3BounceTex, sampler_LinearClamp, uv + o[k], 0);
                    float w = b.a > 0 ? saturate(1 - abs(b.a - lin) / (0.6 * lin + 1)) : 0;
                    sum += b.rgb * w; ws += w;
                }
                return sum / ws;
            }

            // the ambient with the sky-facing lobes (up and the horizon) scaled by the sky visibility; G3's ground lobe (its
            // bounce light from below) is kept: undersides of crowns and overhangs keep their bounce
            float3 G3AmbientVisible(float3 n, float v)
            {
                float3 n2 = n * n;
                return n2.x * (n.x >= 0 ? _G3AmbCube[0].rgb : _G3AmbCube[1].rgb) * v
                     + n2.y * (n.y >= 0 ? _G3AmbCube[2].rgb * v : _G3AmbCube[3].rgb)
                     + n2.z * (n.z >= 0 ? _G3AmbCube[4].rgb : _G3AmbCube[5].rgb) * v;
            }

            float3 G3Ambient(float3 n)
            {
                float3 n2 = n * n;
                return n2.x * (n.x >= 0 ? _G3AmbCube[0].rgb : _G3AmbCube[1].rgb)
                     + n2.y * (n.y >= 0 ? _G3AmbCube[2].rgb : _G3AmbCube[3].rgb)
                     + n2.z * (n.z >= 0 ? _G3AmbCube[4].rgb : _G3AmbCube[5].rgb);
            }

            // normal from the depth buffer: of the left/right and up/down neighbours take the closer one (no smearing over edges)
            static float g3Thin;   // set by G3NormalFromDepth: 1 = the pixel sits between depth steps on both sides (cards, fronds)
            float3 G3NormalFromDepth(float2 uv, float3 p)
            {
                float2 t = _CameraDepthTexture_TexelSize.xy;
                float3 pr = G3LrPos(uv + float2(t.x, 0), SampleSceneDepth(uv + float2(t.x, 0)));
                float3 pl = G3LrPos(uv - float2(t.x, 0), SampleSceneDepth(uv - float2(t.x, 0)));
                float3 pu = G3LrPos(uv + float2(0, t.y), SampleSceneDepth(uv + float2(0, t.y)));
                float3 pd = G3LrPos(uv - float2(0, t.y), SampleSceneDepth(uv - float2(0, t.y)));
                float3 dx = dot(pr - p, pr - p) < dot(p - pl, p - pl) ? pr - p : p - pl;
                float3 dy = dot(pu - p, pu - p) < dot(p - pd, p - pd) ? pu - p : p - pd;
                float3 n = normalize(cross(dy, dx));
                // depth jumps on both sides in x or in y: a thin alpha-tested card in a layered crown; its contact march
                // reads the next card layer and printed row-coherent stripes (Varant palms, session 2)
                float lim = 0.04 * length(p - GetCameraPositionWS());
                float jx = min(length(pr - p), length(p - pl)), jy = min(length(pu - p), length(p - pd));
                g3Thin = saturate((max(jx, jy) - lim) / lim);
                float3 toCam = GetCameraPositionWS() - p;
                return dot(n, toCam) < 0 ? -n : n;
            }

            // snow3: the solid height (R) is interpolated bilinearly between the texel centres (each texel is one ray at its
            // centre): on a slope that is the slope itself, so a ground point is not "under" its own texel's height. Point
            // sampling read a 45 deg slope up to ~0.6 m above itself in half of every 1.25 m texel: the sun block and the
            // straight-up sky taps cut world-axis stair steps into steep snow (build 20c, Nordmar clearing, top-down view).
            // Crowns (G) stay point-sampled (-10000 = none: a bilinear mix would shrink every crown by half a texel).
            float2 G3OccAt(float2 xz)
            {
                float2 uv = (xz - _G3SkyOccWindow.xy) / _G3SkyOccWindow.z;
                if (any(uv < 0) || any(uv > 1)) return float2(-1e5, -1e5);
                return float2(SAMPLE_TEXTURE2D_LOD(_G3SkyOccMap, sampler_LinearClamp, uv, 0).r,
                              SAMPLE_TEXTURE2D_LOD(_G3SkyOccMap, sampler_PointClamp, uv, 0).g);
            }

            float G3OccTopAt(float2 xz)
            {
                float2 uv = (xz - _G3SkyOccWindow.xy) / _G3SkyOccWindow.z;
                if (any(uv < 0) || any(uv > 1)) return -1e5;
                return SAMPLE_TEXTURE2D_LOD(_G3SkyOccMap, sampler_LinearClamp, uv, 0).r;
            }

            // occlusion 0..1 of the sky above p (WorldV2Light.Occlusion): straight up (4 sub-taps), ring 3 m (50 deg), ring 9 m (29 deg)
            float G3SkyOcclusion(float3 p)
            {
                float2 w = (p.xz - _G3SkyOccWindow.xy) / _G3SkyOccWindow.z;
                float edge = saturate(min(min(w.x, w.y), min(1 - w.x, 1 - w.y)) * 10);   // fades out at the window edge
                if (edge <= 0) return 0;
                float crown = _G3LightRatio.y;
                float occ = 0, s = 0;
                float2 sub[4] = { float2(1.0, 0.45), float2(-0.45, 1.0), float2(0.45, -1.0), float2(-1.0, -0.45) };   // rotated, ~1 texel: no 1.25 m stair steps (Faring overhang)
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 h = G3OccAt(p.xz + sub[k]);
                    float need = p.y + 1.0;
                    s += max(saturate((h.x - need) * 0.4), crown * saturate((h.y - need) * 0.4));
                }
                occ += 0.34 * s * 0.25;
                const float radius[2] = { 3.0, 9.0 }, rise[2] = { 1.2, 0.55 }, wt[2] = { 0.36, 0.30 }, rot[2] = { 0.0, 0.52 };
                [unroll] for (int r = 0; r < 2; r++)
                {
                    s = 0;
                    [unroll] for (int i = 0; i < 6; i++)
                    {
                        float a = i * (PI / 3) + rot[r];
                        float2 h = G3OccAt(p.xz + float2(cos(a), sin(a)) * radius[r]);
                        float need = p.y + 1.0 + radius[r] * rise[r];
                        s += max(saturate((h.x - need) * 0.3), crown * saturate((h.y - need) * 0.3));
                    }
                    occ += wt[r] * s / 6;
                }
                return saturate(occ) * edge;
            }

            // sun blocked by a solid occluder of the map (cave ceilings, mountains over a cave mouth, roofs): G3's cave and
            // interior meshes often cast no shadow into the shadow map, so the sun leaked onto cave floors (cave A/B)
            float G3MapSunBlock(float3 p, float3 l)
            {
                if (l.y < 0.05) return 0;
                float blocked = 0;
                [unroll] for (int k = 1; k <= 8; k++)
                {
                    float t = k * k * 0.7;   // 0.7 .. 45 m
                    float3 q = p + l * t;
                    float h = G3OccTopAt(q.xz);
                    blocked = max(blocked, saturate((h - q.y - 0.5) * 0.5));
                }
                return blocked;
            }

            float G3Ign(float2 px) { return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715)))); }

            // screen-space march towards the sun: 1 = lit, 0 = a closer surface blocks the sun within the ray length
            float G3Contact(float3 p, float3 n, float3 l, float dist, float2 px)
            {
                float len = min(_G3LightRatio.w + dist * 0.01, _G3LightRatio.w * 4);
                int steps = (int)_G3LightRatio2.x;
                float jitter = G3Ign(px + _G3LightRatio2.w * 5.588238);
                float3 start = p + n * (0.04 + dist * 0.003);
                float thick = max(0.25, len * 0.6);
                float hit = 0;
                [loop] for (int i = 0; i < steps; i++)
                {
                    float3 q = start + l * (len * (i + jitter) / steps);
                    float4 cs = mul(UNITY_MATRIX_VP, float4(q, 1));
                    float2 quv = cs.xy / cs.w * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    quv.y = 1 - quv.y;
                #endif
                    if (any(quv < 0) || any(quv > 1)) break;
                    float rs = SampleSceneDepth(quv);
                    if (G3LrIsSky(rs)) continue;
                    float diff = cs.w - LinearEyeDepth(rs, _ZBufferParams);
                    if (diff > 0.05 + cs.w * 0.004 && diff < thick) { hit = 1; break; }
                }
                float fade = 1 - saturate((dist - _G3LightRatio2.y * 0.7) / (_G3LightRatio2.y * 0.3));
                // grazing sun: the depth-derived normal and the surface's own relief self-shadow falsely (Montera evening A/B)
                fade *= saturate(dot(n, l) * 4 - 0.4);
                return 1 - hit * _G3LightRatio.z * fade;
            }

            // displayed value of a linear luminance (sRGB encoding minus the grade's black crush; WorldGi.DisplayLum)
            float G3DisplayLum(float l)
            {
                l = clamp(l, 0, 64);
                float s = l < 0.0031308 ? 12.92 * l : 1.055 * pow(l, 1.0 / 2.4) - 0.055;
                return max(s - 0.01, 0);
            }
            // remaster-gi2: sums.x = luminance of this pixel with the ratio, sums.y = without Modern lighting (the
            // exposure that keeps the frame's mean is their ratio over the frame, WorldGi.Exposure); 0 for the sky
            float4 RatioCore(Varyings i, out float2 sums)
            {
                sums = 0;
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // half resolution: a low-res pixel centre lies exactly on the border of two depth texels; point fetches
                // there flip rows with rounding, the depth normal collapsed on every ~12th row (horizontal stripes,
                // Varant session 3). Snap to the full-resolution texel centre.
                float2 uv = (floor(i.texcoord * _CameraDepthTexture_TexelSize.zw) + 0.5) * _CameraDepthTexture_TexelSize.xy;
                float raw = SampleSceneDepth(uv);
                if (G3LrIsSky(raw)) return float4(1, 1, 1, 1e6);
                float3 p = G3LrPos(uv, raw);
                float occMap = 0;
                float3 cam = GetCameraPositionWS();
                float dist = length(p - cam);
                float lin = LinearEyeDepth(raw, _ZBufferParams);
                float3 n = G3NormalFromDepth(uv, p);
                // fix-178 (owner, Nordmar top-down: "рельеф нереалистичный", flat facets with straight creases): the depth
                // normal is the triangle's own face normal. G3's landscape is coarse and its vertex normals are smooth (on
                // walkable ground ~13 deg off the faces on average, 20 % of the area over 20 deg: G3Cli relief shade), so
                // every term here (sun N.L in the ratio, sky occlusion and GI lookups along n, contact shadows, Modern's
                // directional sky) cut the smooth frame into G3's facets. The normals texture holds the vertex normals the
                // frame was lit with: use it where it belongs to the same surface; the depth normal stays for anything without
                // a depth-normals pass and for the thin-card test.
                [branch] if (_G3LrNormals > 0.5)
                {
                    float3 ns = SampleSceneNormals(uv);
                    float ls = dot(ns, ns);
                    [branch] if (ls > 0.25)
                    {
                        ns *= rsqrt(ls);
                        n = normalize(lerp(n, ns, smoothstep(0.2, 0.5, dot(ns, n))));
                    }
                }

                // the three light terms (irradiance; albedo cancels in the ratio)
                float3 A = max(G3Ambient(n), 1e-4);
                float3 l = _MainLightPosition.xyz;
                float ndl = saturate(dot(n, l));
                float sh = 1;
                // normal + light bias like the casters' (no acne: an acne band would read as D = 0 and print the sky
                // visibility as horizontal stripes, first Varant A|B|C)
                if (ndl > 0) sh = MainLightRealtimeShadow(TransformWorldToShadowCoord(p + n * (0.08 + dist * 0.004) + l * (0.05 + dist * 0.002)));
                float3 D = _MainLightColor.rgb * ndl * sh;
                float sunMap = 1;   // the sun as the occluder map sees it (applied by the ratio, like the contact shadow)
                [branch] if (_G3LightRatio.x > 0 && _G3SkyOccWindow.w > 0 && ndl > 0) sunMap = 1 - G3MapSunBlock(p + n * 0.3, l);
                float3 L = 0;
            #if USE_CLUSTER_LIGHT_LOOP
                InputData inputData = (InputData)0;
                inputData.normalizedScreenSpaceUV = uv;
                inputData.positionWS = p;
                uint lightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightCount)
                    Light li = GetAdditionalLight(lightIndex, p);
                    L += li.color * li.distanceAttenuation * saturate(dot(n, li.direction) * 0.5 + 0.5);   // wrapped: never under-counts a torch
                LIGHT_LOOP_END
            #endif

                float v = 1, c = 1;
                // looked up 1.6 m out along the normal: a wall or a rock face is not "under" its own top (WorldV2Light.LookupPoint)
                [branch] if (_G3LightRatio.x > 0 && _G3SkyOccWindow.w > 0) { occMap = G3SkyOcclusion(p + n * 1.6); v = 1 - _G3LightRatio.x * occMap; }
                [branch] if (_G3LightRatio.z > 0 && dist < _G3LightRatio2.y && dot(D, D) > 1e-6 && g3Thin < 1)
                    c = lerp(G3Contact(p, n, l, dist, uv * _ScreenParams.xy), 1, g3Thin);
                // the bounce fills (part of) the sky light the occlusion removed, coloured by the lit surroundings
                float3 B = 0;
                [branch] if (_G3Bounce.x > 0 && v < 0.999) B = G3Bounce(uv, lin) * (_G3Bounce.x * (1 - v));
                float3 amb = max(G3AmbientVisible(n, v), 1e-4 * v) + B;
                float3 ambOld = amb;   // the remaster without Modern lighting (and with v2Gi off), for the exposure match
                float maxRatio = 1;
                [branch] if (_G3GiParams.x > 0)
                {
                    // remaster-gi: the probes' irradiance (real occlusion + coloured bounce) replaces the estimate (WorldGi.Ratio);
                    // looked up off the surface (normal + towards the camera): no self-occlusion, less leaking through walls
                    float gw;
                    float3 q = p + n * (_G3GiWindow.w * 0.45) + normalize(cam - p) * (_G3GiWindow.w * 0.2);
                    float4 pr = G3GiProbe(q, n, gw);
                    // G3's own fill, its sky-facing lobes occluded by the traced sky visibility (strength as the map
                    // estimate; G3's ground lobe - its authored bounce from below - kept as before) + the traced bounce
                    float3 E = max(G3AmbientVisible(n, 1 - _G3LightRatio.x * (1 - saturate(pr.a))), 1e-4) + pr.rgb;   // WorldGi.Ratio
                    // keep: the GI may darken the old estimate only down to this part of it (open forest: the traced
                    // visibility under crowns read lower than the map's canopy density; owner rule "C not darker than B")
                    // energy: the GI's, never below keep x the old estimate; colour: G3's ambient tinted by the hue of the
                    // traced bounce in proportion to its share of the light (x _G3GiTint): G3's fill dominates the energy,
                    // so a physical bounce of a few % would never show - the hue of the surroundings does (green under
                    // canopies, warm from sunlit ground and torch-lit cave walls)
                    float lB = Luminance(pr.rgb);
                    float3 hue = lB > 1e-6 ? pr.rgb / lB : 1;
                    [branch] if (_G3NewLight.x > 0)
                    {
                        // lighting-new (WorldLightNew.Ambient): G3's flat fill replaced by the open-sky irradiance of the
                        // real sky x its visibility (traced in the probe window relative to an open field, the occluder
                        // map outside) x the screen visibility (contact AO), + the coloured bounce (probes; outside the
                        // window a flat ground of albedo _G3NewLight2.x under sun + sky) x screen visibility, + the
                        // on-screen bounce, + a small floor of G3's fill (multiple scattering; never pitch black)
                        float wg = saturate(gw * _G3GiParams.x);
                        float relVis = saturate(saturate(pr.a) / max(0.25, 0.5 * (1 + n.y)));
                        float vSky = lerp(1 - occMap, relVis, wg);
                        float3 skyE = G3NewSkyIrr(n) * _G3NewLight.y;
                        float3 groundE = _G3NewLight2.x * (_MainLightColor.rgb * saturate(l.y) * _G3NewLight3.x + _G3NewSky[2].rgb * _G3NewLight.y) * (0.5 * (1 - n.y));
                        float3 bounceN = wg * pr.rgb + (1 - wg) * groundE;
                        float ssv = 1; float3 ssB = 0;
                        [branch] if (_G3SsgiRatio.x > 0)
                        {
                            float4 ss = SAMPLE_TEXTURE2D_LOD(_G3SsgiTex, sampler_PointClamp, i.texcoord, 0);
                            if (_G3SsgiDebug > 0.5) return float4(_G3SsgiDebug < 1.5 ? ss.aaa * 2 : _G3SsgiDebug < 2.5 ? ss.rgb * 2 / A : ss.rgb * 2, lin);
                            float fade = saturate(1 - (dist - _G3SsgiRatio.w * 0.6) / (_G3SsgiRatio.w * 0.4));
                            float v0 = saturate(ss.a);
                            float vm = pow(max(lerp(v0, G3MultiBounce(v0), _G3NewLight2.w), 0), _G3NewLight2.z);
                            ssv = 1 + (vm - 1) * _G3SsgiRatio.y * fade;
                            ssB = ss.rgb * (_G3NewLight2.y * fade);
                        }
                        amb = (skyE * vSky + bounceN) * ssv + ssB + _G3NewLight.z * A;
                        if (_G3GiDebug > 6.5) return float4(_G3GiDebug < 7.5 ? skyE * vSky * ssv / A : bounceN * ssv / A, lin);   // runner: 7 sky / G3 fill, 8 bounce / G3 fill
                    }
                    else if (_G3GiModern.x > 0)
                    {
                        // remaster-gi2 Modern lighting (WorldGi.ModernAmbient): G3's fill x _G3GiModern.x, its sky-facing
                        // lobes occluded at full strength by the traced visibility relative to an open field (G3's cube
                        // already holds the horizon), blended from the map estimate outside the probe window; + the traced
                        // bounce (gain v2ModernGain in the trace), + the screen bounce (scaled like the fill) where the
                        // probes are not valid; the bounce hue tints what is left of the fill
                        float wg = saturate(gw * _G3GiParams.x);
                        float relVis = saturate(saturate(pr.a) / max(0.25, 0.5 * (1 + n.y)));
                        float vM = lerp(1 - _G3GiModern.y * occMap, 1 - _G3GiModern.y * (1 - relVis), wg);
                        // directional sky: light comes from above (tops lit, undersides and overhangs in shade, more shape)
                        float sky = max(0.15, 1 + _G3GiModernSky * n.y);
                        float3 fillM = _G3GiModern.x * sky * max(G3AmbientVisible(n, vM), 1e-4);
                        float3 bounceM = wg * pr.rgb + (1 - wg) * _G3GiModern.x * B;
                        [branch] if (_G3SsgiRatio.x > 0)
                        {
                            // remaster-ssgi: per-pixel visibility of the on-screen surfaces (contact level: grass, eaves,
                            // corners) on the fill and on the probes' low-frequency bounce, + the light those surfaces
                            // reflect (colour bleeding; WorldSsgi.FillVisibility). Off screen / beyond the fade the probes stay.
                            float4 ss = SAMPLE_TEXTURE2D_LOD(_G3SsgiTex, sampler_PointClamp, i.texcoord, 0);
                            if (_G3SsgiDebug > 0.5) return float4(_G3SsgiDebug < 1.5 ? ss.aaa * 2 : _G3SsgiDebug < 2.5 ? ss.rgb * 2 / A : ss.rgb * 2, lin);
                            float fade = saturate(1 - (dist - _G3SsgiRatio.w * 0.6) / (_G3SsgiRatio.w * 0.4));
                            float v0 = saturate(ss.a);
                            float vm = pow(max(lerp(v0, G3MultiBounce(v0), _G3SsgiRatio2.x), 0), _G3SsgiRatio2.y);
                            float vis = 1 + (vm - 1) * _G3SsgiRatio.y * fade;
                            fillM *= vis;
                            bounceM = bounceM * vis + ss.rgb * (_G3SsgiRatio.z * fade);
                        }
                        float3 M = fillM + bounceM;
                        float share = saturate(lB * wg / max(Luminance(M), 1e-5) * _G3GiTint);
                        amb = M * lerp(1, hue, share);
                    }
                    else
                    {
                        float lO = max(Luminance(amb), 1e-5);
                        float lE = Luminance(lerp(amb, E, gw * _G3GiParams.x));
                        float share = saturate(lB / max(Luminance(E), 1e-5) * _G3GiTint * gw * _G3GiParams.x);
                        amb = amb / lO * max(lE, lO * _G3GiParams.w) * lerp(1, hue, share);
                    }
                    maxRatio = _G3GiParams.z;
                    if (_G3GiDebug > 0.5 && _G3GiDebug < 1.5) return float4(gw.xxx * 2, lin);
                    if (_G3GiDebug > 1.5 && _G3GiDebug < 2.5) return float4(E / A, lin);
                    if (_G3GiDebug > 4.5 && _G3GiDebug < 5.5) return float4(pr.rgb / A, lin);       // bounce / G3 ambient
                    if (_G3GiDebug > 5.5) return float4(pr.aaa * 0.5, lin);                        // traced sky visibility
                    if (_G3GiDebug > 3.5)
                    {
                        // the voxel just inside the surface: unseen = blue, empty = red, solid = its radiance / G3 ambient
                        float3 vq = floor((p - n * 0.3) / _G3GiParams.y);
                        float3 rel = vq - _G3GiOrigin.xyz;
                        if (any(rel < 0) || any(rel >= float3(128, 64, 128))) return float4(0, 0, 0, lin);
                        float4 vx = LOAD_TEXTURE3D(_G3GiVox, (int3)(vq - floor(vq / float3(128, 64, 128)) * float3(128, 64, 128)));
                        return vx.a < 0 ? float4(0, 0, 1, lin) : vx.a < 0.5 ? float4(1, 0, 0, lin) : float4(vx.rgb / A, lin);
                    }
                }
                float cloud = G3CloudSun(p);
                if (_G3GiDebug > 2.5) return float4(cloud.xxx * 2, lin);
                float3 sunLit = D * c * sunMap * cloud;
                float3 ratio = (amb + sunLit + L) / (A + D + L);
                ratio = clamp(ratio, _G3LightRatio2.z, maxRatio);
                [branch] if (_G3NewLight.x > 0 && _G3GiParams.x > 0)   // lighting-new: no floor/cap of the matched modes (the exposure follows)
                    ratio = clamp((amb + sunLit * _G3NewLight3.x + L) / (A + D + L), 0, _G3NewLight.w);
                // fix-258: under a solid ceiling (roof, rock: the map's solid top straight above, no crowns) the ratio keeps
                // this part of G3's light (its fill + the sun its interior meshes let through): the occlusion and the map's
                // sun block left windowless halls near black (Bakaresh Beliar throne hall); outdoor sun shadows have no
                // ceiling and keep theirs. WorldV2Light.InteriorFloor, tested
                [branch] if (_G3LightRatio3.x > 0 && _G3SkyOccWindow.w > 0)
                {
                    float3 pc = p + n * 1.6;
                    float ceilK = 0;
                    float2 subc[4] = { float2(1.0, 0.45), float2(-0.45, 1.0), float2(0.45, -1.0), float2(-1.0, -0.45) };
                    [unroll] for (int kc = 0; kc < 4; kc++) ceilK += saturate((G3OccAt(pc.xz + subc[kc]).x - pc.y - 1.0) * 0.4);
                    ratio = max(ratio, _G3LightRatio3.x * smoothstep(0.5, 0.9, ceilK * 0.25));
                }
            #if defined(_G3LR_EXPOSURE)
                float3 ratioOld = clamp((ambOld + sunLit + L) / (A + D + L), _G3LightRatio2.z, 1);
                float3 frame = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0).rgb;
                // matched on the displayed value (sRGB encoding, minus the grade's black crush): a linear mean is ruled
                // by the few sunlit highlights, a sqrt mean by the darkest corners - both overshot the shown brightness
                // by 20-40 % (forest, session 1)
                sums = float2(G3DisplayLum(Luminance(frame * ratio)), G3DisplayLum(Luminance(frame * ratioOld)));
            #endif
                return float4(ratio, lin);
            }

        #if defined(_G3LR_EXPOSURE)
            struct RatioOut { float4 ratio : SV_Target0; float4 sums : SV_Target1; };
            RatioOut FragRatio(Varyings i)
            {
                RatioOut o;
                float2 s2;
                o.ratio = RatioCore(i, s2);
                o.sums = float4(s2, 0, 1);
                return o;
            }
        #else
            float4 FragRatio(Varyings i) : SV_Target
            {
                float2 s2;
                return RatioCore(i, s2);
            }
        #endif
            ENDHLSL
        }

        Pass
        {
            Name "G3LightRatioComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            TEXTURE2D_X(_G3LightRatioTex);
            float4 _G3LightRatioTexel;   // x 1/w, y 1/h, z w, w h of the ratio target as requested (C#)
            float4 _G3LightRatioTex_TexelSize;   // the texture's real size (Unity): the requested one drifted against it
                                                 // by ~2 % (game view size) -> every ~24th row read a wrong row (stripes)
            float _G3LightRatioDebug;    // runner: 1 = show the ratio itself (grey = 0.5 x ratio)
            float4 _G3Exposure;          // remaster-gi2: x 1 = multiply by the 1x1 exposure (Modern lighting)
            TEXTURE2D(_G3ExposureTex);   // r exposure, g mean with, b mean without Modern lighting

            half4 FragComposite(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
                float raw = SampleSceneDepth(uv);
                if (G3LrIsSky(raw)) return col;
                float lin = LinearEyeDepth(raw, _ZBufferParams);
                // depth-aware upsample: the 4 low-res texels around the pixel, weighted by bilinear x depth similarity
                float4 tx = _G3LightRatioTex_TexelSize.z > 0 ? _G3LightRatioTex_TexelSize : _G3LightRatioTexel;
                float2 st = uv * tx.zw - 0.5;
                float2 f = frac(st);
                float2 base = (floor(st) + 0.5) * tx.xy;
                float3 sum = 0; float ws = 0;
                float bw[4] = { (1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y };
                float2 o[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float4 r = SAMPLE_TEXTURE2D_X_LOD(_G3LightRatioTex, sampler_PointClamp, base + o[k] * tx.xy, 0);
                    float w = (bw[k] + 1e-3) / (1e-3 + abs(r.a - lin) / max(lin, 1e-3) * 40);
                    sum += r.rgb * w; ws += w;
                }
                float3 ratio = ws > 0 ? sum / ws : 1;
                [branch] if (_G3Exposure.x > 0) ratio *= LOAD_TEXTURE2D(_G3ExposureTex, int2(0, 0)).r;
                col.rgb = _G3LightRatioDebug > 0 ? ratio * 0.5 : col.rgb * ratio;
                return col;
            }
            ENDHLSL
        }
        // remaster-look2 bounce (11.8): full -> 1/4 (4 bilinear taps = 4x4 px, sky taps dropped), a = mean linear depth
        Pass
        {
            Name "G3BounceDown4"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDown
            float4 _G3BounceSrcTexel;   // xy 1/size of the source

            float4 FragDown(Varyings i) : SV_Target
            {
                float2 t = _G3BounceSrcTexel.xy;
                float2 o[4] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, 1) };
                float3 c = 0; float d = 0, w = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 uv = i.texcoord + o[k] * t;
                    float raw = SampleSceneDepth(uv);
                    if (G3LrIsSky(raw)) continue;
                    c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
                    d += LinearEyeDepth(raw, _ZBufferParams); w += 1;
                }
                return w > 0 ? float4(min(c / w, 64), d / w) : 0;
            }
            ENDHLSL
        }

        // 1/4 -> 1/16: 16 point taps, invalid (sky) texels dropped
        Pass
        {
            Name "G3BounceDown16"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDown
            float4 _G3BounceSrcTexel;

            float4 FragDown(Varyings i) : SV_Target
            {
                float2 t = _G3BounceSrcTexel.xy;
                float3 c = 0; float d = 0, w = 0;
                [unroll] for (int y = 0; y < 4; y++)
                [unroll] for (int x = 0; x < 4; x++)
                {
                    float4 b = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, i.texcoord + (float2(x, y) - 1.5) * t, 0);
                    if (b.a <= 0) continue;
                    c += b.rgb; d += b.a; w += 1;
                }
                return w > 0 ? float4(c / w, d / w) : 0;
            }
            ENDHLSL
        }

        // remaster-gi2 exposure (section 17): mean of 4x4 source texels (the sums of the ratio pass's second target)
        Pass
        {
            Name "G3ExposureDown"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDown
            float4 _G3ExposureSrc;   // xy source size (texels)

            float4 FragDown(Varyings i) : SV_Target
            {
                int2 o = (int2)floor(i.positionCS.xy) * 4;
                int2 mx = (int2)_G3ExposureSrc.xy - 1;
                float2 s = 0;
                [unroll] for (int y = 0; y < 4; y++)
                [unroll] for (int x = 0; x < 4; x++)
                    s += LOAD_TEXTURE2D_X(_BlitTexture, min(o + int2(x, y), mx)).rg;
                return float4(s / 16, 0, 1);
            }
            ENDHLSL
        }

        // the last level (at most 16 x 16): mean over all texels -> r exposure (WorldGi.Exposure), g mean with, b without
        Pass
        {
            Name "G3ExposureFinal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragFinal
            float4 _G3ExposureSrc;   // xy source size (texels), z blend rate with the previous frame (1 = take the new value)
            float4 _G3GiModern;      // z min exposure, w max exposure
            TEXTURE2D(_G3ExposurePrev);
            float4 _G3GiModern2;     // x exposure match 0..1

            float4 FragFinal(Varyings i) : SV_Target
            {
                int nx = min((int)_G3ExposureSrc.x, 16), ny = min((int)_G3ExposureSrc.y, 16);
                float2 s = 0;
                [loop] for (int y = 0; y < ny; y++)
                [loop] for (int x = 0; x < nx; x++)
                    s += LOAD_TEXTURE2D_X(_BlitTexture, int2(x, y)).rg;
                s /= max(1, nx * ny);
                // s = means of the displayed value; it responds to an exposure e about as e^0.7 in G3's dark range
                // (sRGB slope at ~0.01-0.05 linear + the black crush), so e = (s.y / s.x)^(1 / 0.7)
                float e = (s.x > 1e-4 && s.y > 1e-4) ? pow(abs(s.y / s.x), 1.43 * saturate(_G3GiModern2.x)) : 1;
                e = clamp(e, _G3GiModern.z, max(_G3GiModern.z, _G3GiModern.w));
                float prev = LOAD_TEXTURE2D(_G3ExposurePrev, int2(0, 0)).r;
                if (_G3ExposureSrc.z < 1 && prev > 0) e = exp(lerp(log(prev), log(e), _G3ExposureSrc.z));
                return float4(e, s.x, s.y, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
