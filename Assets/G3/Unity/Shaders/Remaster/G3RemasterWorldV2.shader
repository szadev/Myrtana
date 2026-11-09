// World remaster v2 (Docs/remaster-world.md section 10): CC0 PBR materials for G3's terrain, rocks and buildings, used
// only by the v2 copies G3WorldV2Builder makes (originals keep G3/LitBlend, r3 copies keep G3/Remaster/WorldBlend).
// Per layer (A, and B of the vertex-alpha blends) a CC0 scan (albedo, normal, mask: R metal, G AO, B height, A smooth)
// chosen by the G3 image's class (WorldV2.LayerFor), sampled by the layer's kind (_V2LayerX.z):
//   -1  no CC0 set: G3's (r3) maps only;
//    0  surface (buildings): mesh UV at the scan's real size (G3 metres per repeat / scan size); replace 1 = the scan
//       colour-matched to G3, replace < 1 = G3's image and baked height stay the structure, the scan adds grain and
//       roughness; parallax occlusion on the height;
//    1  ground: world XZ at the real size, hex-tile stochastic (no repetition), world triplanar on steep faces (> ~50 deg);
//    2  rock: world triplanar (planes under 2 % skipped) at three scales (the scan, 4.3x and 18x: cracks, strata, large
//       forms), strong relief, cavity; moss/grass only on up-facing normals, height-blended into the rock's low parts.
//    3  AI (10.18): the G3 image itself remastered offline (Tools/Remaster/ai/rock_ai.py), at G3's own mapping (mesh UV,
//       or world triplanar at G3's world repeat for terrain-cell rock); no CC0, no colour lock, no moss.
// Colour: every scan is matched to its G3 layer's mean colour (_G3V2Surface.w) and G3's blurred pattern stays on top
// (_G3V2Surface2.y): Myrtana keeps its palette. Buildings (_V2Surface.x): dirt at the base, edge wear on the high parts
// of the height map and on hard geometric edges. All: wetness from G3's rain (darker, glossier; none under roofs, fix-130). Lighting: URP PBR
// (GGX) or G3's Blinn-Phong (_G3V2Surface.x = 0). Every global is a uniform branch: no extra variants.
Shader "G3/Remaster/WorldV2"
{
    Properties
    {
        _BaseMap("G3 layer A", 2D) = "white" {}
        _BaseMap2("G3 layer B", 2D) = "white" {}
        [Normal] _BumpMap("G3 normal A", 2D) = "bump" {}
        [Normal] _BumpMap2("G3 normal B", 2D) = "bump" {}
        [NoScaleOffset] _MaskMap("G3 (r3) mask A", 2D) = "white" {}
        [NoScaleOffset] _MaskMap2("G3 (r3) mask B", 2D) = "white" {}
        [ToggleUI] _UseMask("Layer A has an r3 mask", Float) = 0
        [ToggleUI] _UseMask2("Layer B has an r3 mask", Float) = 0
        _BaseColor("Tint", Color) = (1,1,1,1)
        [Toggle(_SINGLE_LAYER)] _SingleLayer("Single layer (no vertex blend)", Float) = 0
        [Toggle(_NORMALMAP)] _UseNormalMap("G3 normal maps", Float) = 0
        [Toggle] _SwapLayers("Swap layers (debug)", Float) = 0
        _Smoothness("G3 smoothness (layers without a scan)", Range(0,1)) = 0.3
        [Header(CC0 layer A)]
        [NoScaleOffset] _V2AlbedoA("Albedo A", 2D) = "grey" {}
        [Normal][NoScaleOffset] _V2NormalA("Normal A", 2D) = "bump" {}
        [NoScaleOffset] _V2MaskA("Mask A (G AO, B height, A smooth)", 2D) = "white" {}
        _V2LayerA("A: scale, replace, kind (-1 none, 0 surface, 1 ground, 2 rock, 3 AI), parallax / AI triplanar", Vector) = (1, 1, -1, 0)
        [Header(CC0 layer B)]
        [NoScaleOffset] _V2AlbedoB("Albedo B", 2D) = "grey" {}
        [Normal][NoScaleOffset] _V2NormalB("Normal B", 2D) = "bump" {}
        [NoScaleOffset] _V2MaskB("Mask B", 2D) = "white" {}
        _V2LayerB("B: scale, replace, kind, parallax", Vector) = (1, 1, -1, 0)
        [Header(Material)]
        _V2Surface("building (dirt, wear), moss amount, normal strength, metres per G3 uv", Vector) = (0, 0, 1, 1)
        [NoScaleOffset] _V2Moss("Moss / grass albedo (CC0)", 2D) = "grey" {}
        [Normal][NoScaleOffset] _V2MossNormal("Moss normal", 2D) = "bump" {}
        [NoScaleOffset] _V2MossG3("G3 grass image (moss colour)", 2D) = "grey" {}
        _V2MossParams("moss: 1/size (1/m), up threshold, band, unused", Vector) = (0.5, 0.55, 0.15, 0)
        _V2Snow("snow (remaster-snow): packed road snow on layer A, on layer B (builder: Snow_Path, City_Snow); sand class of layer A, B (remaster-sand: 1 loose, 2 wet, 3 packed path)", Vector) = (0, 0, 0, 0)
        _V2Blend("blend (ai-rocks p6): height band (0 = global), steep-to-rock push, ground G3 fine (0 = global), CC0 grain on ground", Vector) = (0, 1, 0, 1)
        _V2Arch("arch-1 (25): parallax depth (m), sand dust, class (1 masonry 2 floor 3 sandstone 4 plaster 5 wood 6 cloth 7 metal), cloth translucency", Vector) = (0, 0, 0, 0)
        [Header(G3 detail maps from the original material)]
        [ToggleUI] _UseDetail("G3 detail map", Float) = 0
        _DetailMap("G3 detail A (or shared)", 2D) = "white" {}
        _DetailLayers("Detail on layer A, B (0/1)", Vector) = (1,1,0,0)
        [ToggleUI] _UseDetail2("Separate G3 detail map for layer B", Float) = 0
        _DetailMap2("G3 detail B", 2D) = "white" {}
        [HideInInspector] _Cutoff("Cutoff", Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Two material samplers only (runner-30: 15 per-texture samplers + URP's shadow/SSAO samplers exceeded the
            // 16 sampler registers of SM 5.0 -> magenta): G3's maps share sampler_BaseMap, the CC0 scans and the moss layer
            // one inline trilinear/repeat/aniso-8 sampler. Tools/Remaster/world_shader_check.py counts the registers.
            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BaseMap2);
            TEXTURE2D(_BumpMap);
            TEXTURE2D(_BumpMap2);
            TEXTURE2D(_MaskMap);
            TEXTURE2D(_MaskMap2);
            TEXTURE2D(_V2AlbedoA);
            TEXTURE2D(_V2NormalA);
            TEXTURE2D(_V2MaskA);
            TEXTURE2D(_V2AlbedoB);
            TEXTURE2D(_V2NormalB);
            TEXTURE2D(_V2MaskB);
            TEXTURE2D(_V2Moss);
            TEXTURE2D(_V2MossNormal);
            TEXTURE2D(_V2MossG3);
            TEXTURE2D(_DetailMap);
            TEXTURE2D(_DetailMap2);
            SAMPLER(sampler_trilinear_repeat_aniso8);
            #define G3V2_SMP sampler_trilinear_repeat_aniso8

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseMap2_ST;
                float4 _BumpMap_ST;
                float4 _BumpMap2_ST;
                half4 _BaseColor;
                half _UseMask;
                half _UseMask2;
                half _SwapLayers;
                half _Smoothness;
                half _Cutoff;
                float4 _V2LayerA;
                float4 _V2LayerB;
                float4 _V2Surface;
                float4 _V2MossParams;
                float4 _V2Blend;
                float4 _V2Snow;
                float4 _V2Arch;
                float4 _DetailMap_ST;
                float4 _DetailMap2_ST;
                half4 _DetailLayers;
                half _UseDetail;
                half _UseDetail2;
            CBUFFER_END

            // remaster-snow (12.5/12.6): the footprints, shared by every pass (real displaced prints on a dense ground mesh)
            #define G3SNOW_WORLDV2_VERTEX
            #include "Assets/G3/Unity/Shaders/Remaster/G3RemasterSnowPrints.hlsl"
            // fix-171: the relief's fine detail by the live camera distance, shared by every pass
            #include "Assets/G3/Unity/Shaders/Remaster/G3ReliefMorph.hlsl"

            // arch-2 (25.7): alpha test of the cut-out arch cloth in URP's shadow / depth fragments (they call these two;
            // SurfaceInput.hlsl is not included - it would declare _BaseMap a second time)
            #if defined(_ALPHATEST_ON)
            half4 SampleAlbedoAlpha(float2 uv, TEXTURE2D_PARAM(albedoAlphaMap, sampler_albedoAlphaMap))
            {
                return SAMPLE_TEXTURE2D(albedoAlphaMap, sampler_albedoAlphaMap, uv);
            }
            half Alpha(half albedoAlpha, half4 color, half cutoff)
            {
                half a = albedoAlpha * color.a;
                clip(a - cutoff);
                return a;
            }
            #endif
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _SINGLE_LAYER
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #define _ENVIRONMENTREFLECTIONS_OFF   // G3's sky changes every frame; the flat glossy environment colour follows it
            #define _SPECULAR_COLOR               // Blinn-Phong fallback path
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3RemasterSnow.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3RemasterSand.hlsl"
            TEXTURECUBE(_G3SkyCube); SAMPLER(sampler_G3SkyCube);
            float4 _G3SkyReflection;   // x strength (0 = URP's flat glossy colour as before), y mip count (G3SkyReflection)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float4 uv2        : TEXCOORD2;   // fix-171 relief morph (G3ReliefMorph.hlsl)
                float4 uv3        : TEXCOORD3;   // remaster-relief: x height above the collision, y relief mask, z dense patch edge (m), w spacing
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                half4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS    : TEXCOORD2;
                half4 tangentWS   : TEXCOORD3;
                half4 color       : COLOR;
                half fogFactor    : TEXCOORD4;
                half3 vertexSH    : TEXCOORD5;
                float baseY       : TEXCOORD6;   // object origin height (building base dirt)
                float2 snowDense  : TEXCOORD7;   // remaster-snow: x prints displaced in the geometry here (no parallax); y drawn surface above G3's collision (m, relief; snow3: rain shadow)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                G3ReliefMorph(v.positionOS.xyz, v.normalOS, v.uv2, v.uv3, v.uv4, v.uv5, v.uv6, true);
                VertexNormalInputs nrm = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                float dense;
                float3 pws = G3SnowDisplaceWS(TransformObjectToWorld(v.positionOS.xyz), v.uv3, v.color, dense);
                o.positionCS = TransformWorldToHClip(pws);
                o.positionWS = pws;
                // relief (13.4): uv3.x = drawn - collision along the face normal; vertical gap = that / n.y
                o.snowDense = float2(dense, v.uv3.x / max(abs(nrm.normalWS.y), 0.35));
                o.normalWS = nrm.normalWS;
                o.tangentWS = half4(nrm.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv = v.uv;
                o.color = v.color;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                o.vertexSH = SampleSHVertex(o.normalWS);
                o.baseY = UNITY_MATRIX_M._m13;
                return o;
            }

            // ---- G3's own detail map (grass-v3) ----------------------------------------------------------------
            // G3/LitBlend multiplies the landscape's G3_Misc_Detailmap_01 (1/40 m: large light/dark, warm/cool patches) and
            // the dirt maps into the layers; v2 had dropped them (the builder disables _DETAILMAP), which flattened the
            // meadows into fewer tones. The v2 copies still carry the maps and _UseDetail, so they come back here, mean
            // normalised like G3/LitBlend, on ground and G3-only layers (rock and building scans have their own detail),
            // scaled by _G3V2Color.z (json v2G3Detail).
            half3 G3V2Detail(TEXTURE2D_PARAM(tex, smp), float4 st, float2 uv, half on)
            {
                half3 r = 1;
                // explicit gradients before the branch: 'on' can vary per pixel (AI layers: mesh-UV share, pass 4)
                float2 duv = uv * st.xy + st.zw, ddxd = ddx(duv), ddyd = ddy(duv);
                [branch] if (on > 0.001 && _G3V2Color.z > 0.001)
                {
                    half3 m = SAMPLE_TEXTURE2D_LOD(tex, smp, float2(0.5, 0.5), 15).rgb;
                    half3 d = SAMPLE_TEXTURE2D_GRAD(tex, smp, duv, ddxd, ddyd).rgb / max(dot(m, half3(0.2126, 0.7152, 0.0722)), 0.02);
                    r = lerp(1, d, _G3V2Color.z * saturate(on));
                }
                return r;
            }

            // ---- one layer -------------------------------------------------------------------------------------
            // kind -1: G3 only; 0: surface (mesh UV); 1: ground (world XZ, hex, triplanar on steep); 2: rock (world triplanar, 3 scales)
            // runner-31: every fetch takes explicit gradients computed in Frag before any per-pixel branch (the plane-skip,
            // steep and parallax branches diverge inside pixel quads: implicit derivatives there gave the thin light
            // stripes on distant rock), one exit (Unity's "potentially uninitialized" warning), and the colour lock targets
            // G3's LOCAL colour (its image a few mips down: the green/brown patches at their real saturation and
            // darkness), not the global mean, which averaged G3's speckle to a pale grey.
            V2Sample SampleLayer(TEXTURE2D_PARAM(alb, sA), TEXTURE2D_PARAM(nrm, sN), TEXTURE2D_PARAM(msk, sM),
                                 TEXTURE2D_PARAM(g3, sG), TEXTURE2D_PARAM(g3n, sGN), TEXTURE2D_PARAM(g3m, sGM),
                                 float4 layer, float4 g3ST, float4 g3nST, half useG3Mask, float2 uvMesh, float2 uvdx, float2 uvdy,
                                 float3 pw, float3 dpx, float3 dpy, float3 nGeo, float3x3 tbn, float3 viewWS, float camDist, float steep, float sandK)
            {
                V2Sample o = (V2Sample)0;
                o.mask = float4(0, 1, 0.5, _Smoothness);
                float dm = _G3V2Debug.x;
                // distance fade of the tiled scans' detail: _G3V2Far x start (m), y end (m), z detail kept at range
                float farK = smoothstep(_G3V2Far.x, max(_G3V2Far.y, _G3V2Far.x + 1), camDist) * (1 - _G3V2Far.z);
                o.n = nGeo;
                float2 uvG3 = uvMesh * g3ST.xy + g3ST.zw;
                float2 gdx = uvdx * g3ST.xy, gdy = uvdy * g3ST.xy;
                float2 nuv = useG3Mask > 0.5 ? uvG3 : uvMesh * g3nST.xy + g3nST.zw;
                float2 ndx = useG3Mask > 0.5 ? gdx : uvdx * g3nST.xy, ndy = useG3Mask > 0.5 ? gdy : uvdy * g3nST.xy;
                float kind = layer.z;
                float match = _G3V2Surface.w;
                float nStrength = _V2Surface.z;

                [branch] if (kind < -0.5)
                {
                    o.albedo = SAMPLE_TEXTURE2D_GRAD(g3, sG, uvG3, gdx, gdy).rgb;
                    float3 nts = float3(0, 0, 1);
                #if defined(_NORMALMAP)
                    nts = UnpackNormal(SAMPLE_TEXTURE2D_GRAD(g3n, sGN, nuv, ndx, ndy));
                #endif
                    o.n = normalize(mul(nts, tbn));
                    o.mask = lerp(o.mask, SAMPLE_TEXTURE2D_GRAD(g3m, sGM, uvG3, gdx, gdy), useG3Mask);
                    o.mask.a = min(o.mask.a, 0.3);   // no grey GGX sheen on G3-only surfaces
                    o.dbg = dm < 2.5 ? (o.albedo) : dm < 3.5 ? (1.0.xxx) : dm < 4.5 ? (o.mask.g) : dm < 5.5 ? (float3(0.5, 0, 0)) : dm < 6.5 ? (0.5.xxx) : dm < 7.5 ? (o.mask.b) : float3(0, 0, 0);
                }
                else if (kind > 2.5)
                {
                    // ---- AI (10.18): G3's own image remastered offline (Tools/Remaster/ai/rock_ai.py: Real-ESRGAN + SDXL
                    // ControlNet-tile img2img, colour-locked to G3) - G3's own mapping and repeat, no CC0 scan, no moss.
                    // layer.x AI normal scale (on G3's own normal map), layer.y saturation, layer.w 1 = terrain cell / blend
                    // mesh: G3's mesh UV (pass 3: its per-face facets are G3's rock look; triplanar smeared them), world
                    // triplanar at G3's world repeat only where that UV is stretched (WorldV2.AiTriplanarWeight); 0 = mesh UV.
                    float3 tw = G3V2TriWeights(nGeo);
                    float tri = 0;
                    [branch] if (layer.w > 0.5)
                    {
                        // world tangents of the G3 UV from the screen derivatives (scaled by det: the ratio is invariant)
                        float3 tu = dpx * gdy.y - dpy * gdx.y, tv = dpy * gdx.x - dpx * gdy.x;
                        float uu = dot(tu, tu), vv = dot(tv, tv), uv = dot(tu, tv);
                        float sm = uu + vv, area2 = uu * vv - uv * uv;
                        float dd = sqrt(max(sm * sm - 4 * area2, 0));
                        float ratio = sqrt((sm + dd) / max(sm - dd, 1e-20));
                        tri = (area2 <= 1e-6 * sm * sm) ? 1 : smoothstep(2.0, 3.5, ratio);   // WorldV2.AiStretchStart/End
                    }
                    float3 cU = 0, nU = nGeo, cT = 0, nT = 0; float4 mU = 0, mT = 0;
                    [branch] if (tri < 0.99)
                    {
                        cU = SAMPLE_TEXTURE2D_GRAD(alb, sA, uvG3, gdx, gdy).rgb;
                        float3 tn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, uvG3, gdx, gdy), nStrength * layer.x);
                    #if defined(_NORMALMAP)
                        // G3's own authored normal map stays the relief (its facets and ledges; pass 2 without it read as
                        // crumpled paper); the AI normal is fine detail on top (whiteout)
                        float3 gn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(g3n, sGN, nuv, ndx, ndy), nStrength);
                        tn = float3(gn.xy + tn.xy, gn.z * tn.z);
                    #endif
                        mU = SAMPLE_TEXTURE2D_GRAD(msk, sM, uvG3, gdx, gdy);
                        nU = mul(normalize(tn), tbn);
                    }
                    [branch] if (tri > 0.01)
                    {
                        float kw = g3ST.x / max(_V2Surface.w, 0.1);
                        float kn = (useG3Mask > 0.5 ? g3ST.x : g3nST.x) / max(_V2Surface.w, 0.1);   // G3 normal map's own repeat
                        [unroll] for (int axis = 0; axis < 3; axis++)
                        {
                            float wa = axis == 0 ? tw.x : axis == 1 ? tw.y : tw.z;
                            float2 pq = axis == 0 ? pw.zy : axis == 1 ? pw.xz : pw.xy;
                            float2 qx = axis == 0 ? dpx.zy : axis == 1 ? dpx.xz : dpx.xy;
                            float2 qy = axis == 0 ? dpy.zy : axis == 1 ? dpy.xz : dpy.xy;
                            float2 p = pq * kw, px = qx * kw, py = qy * kw;
                            [branch] if (wa > 0.02)
                            {
                                cT += SAMPLE_TEXTURE2D_GRAD(alb, sA, p, px, py).rgb * wa;
                                float3 tn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, p, px, py), nStrength * layer.x);
                            #if defined(_NORMALMAP)
                                float3 gn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(g3n, sGN, pq * kn, qx * kn, qy * kn), nStrength);
                                tn = normalize(float3(gn.xy + tn.xy, gn.z * tn.z));
                            #endif
                                nT += G3V2PlaneNormal(tn, nGeo, axis) * wa;
                                mT += SAMPLE_TEXTURE2D_GRAD(msk, sM, p, px, py) * wa;
                            }
                        }
                    }
                    float3 c = lerp(cU, cT, tri);
                    // pass 4: no macro layer (its rotated self-copy and world noise read as dark smudges); the large-scale
                    // variation is G3's own second stone multiply (_DetailMap, AI version) applied in Frag like G3/LitBlend.
                    // layer.y: saturation of G3's own hue (pass 3: the AI rock read greyer than G3's cliffs, whose olive comes
                    // from G3's light; WorldV2.AiSaturation 2.1); 1 = as the image
                    // pass 6: per region (_G3V2Ai.x from the streamer's current region, WorldV2.AiSaturationFor: Nordmar's grey
                    // rock went yellow-olive with Myrtana's 2.1, Varant's red rock wants more); 0 = the material's layer.y
                    o.albedo = max(lerp(G3V2Luma(c).xxx, c, _G3V2Ai.x > 0 ? _G3V2Ai.x : layer.y), 0);
                    o.n = normalize(lerp(nU, nT, tri));
                    o.mask = lerp(mU, mT, tri);
                    o.uvMesh = 1 - tri;
                    // pass 5 close-up grain: Mask.R = band-passed AI luminance (rock_ai.micro_detail; metallic is 0 on rock)
                    // at a fine world repeat (_G3V2Color2.w m), world triplanar - isotropic, so it also breaks the streaks of a
                    // stretched G3 UV - faded in below 12 m (at ~1 m the 2048 texel is magnified: a smear without it)
                    float micro = _G3V2Color2.z * (1 - smoothstep(3.0, 12.0, camDist)), md = 0.5;
                    [branch] if (micro > 0.002)
                    {
                        float km = 1 / max(_G3V2Color2.w, 0.05);
                        float3 mn = 0;
                        md = 0;
                        [unroll] for (int ax = 0; ax < 3; ax++)
                        {
                            float wa = ax == 0 ? tw.x : ax == 1 ? tw.y : tw.z;
                            float2 pq = (ax == 0 ? pw.zy : ax == 1 ? pw.xz : pw.xy) * km;
                            float2 qx = (ax == 0 ? dpx.zy : ax == 1 ? dpx.xz : dpx.xy) * km;
                            float2 qy = (ax == 0 ? dpy.zy : ax == 1 ? dpy.xz : dpy.xy) * km;
                            // rotated 37 deg: never aligned with the G3 UV's own axes
                            pq = float2(pq.x * 0.8 - pq.y * 0.6, pq.x * 0.6 + pq.y * 0.8);
                            qx = float2(qx.x * 0.8 - qx.y * 0.6, qx.x * 0.6 + qx.y * 0.8);
                            qy = float2(qy.x * 0.8 - qy.y * 0.6, qy.x * 0.6 + qy.y * 0.8);
                            [branch] if (wa > 0.02)
                            {
                                md += SAMPLE_TEXTURE2D_GRAD(msk, sM, pq, qx, qy).r * wa;
                                float3 tn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, pq, qx, qy), nStrength * micro);
                                mn += G3V2PlaneNormal(tn, o.n, ax) * wa;
                            }
                            else md += 0.5 * wa;
                        }
                        o.albedo *= max(1 + (md - 0.5) * 2 * micro, 0);
                        o.n = normalize(mn + o.n * 1e-3);
                    }
                    o.mask.r = 0;
                    o.dbg = dm < 2.5 ? (c) : dm < 3.5 ? (md.xxx) : dm < 4.5 ? (o.mask.g) : dm < 5.5 ? (tw) : dm < 6.5 ? (float3(tri, 1 - tri, 0)) : dm < 7.5 ? (o.mask.b) : float3(0, 0, 0);
                }
                else
                {
                    float3 ccMean = SAMPLE_TEXTURE2D_LOD(alb, sA, float2(0.5, 0.5), 15).rgb;
                    float3 g3Mean = SAMPLE_TEXTURE2D_LOD(g3, sG, float2(0.5, 0.5), 15).rgb;
                    float lumCc = max(G3V2Luma(ccMean), 0.01);
                    // G3's local colour: its image ~3 mips blurrier than on screen (patches, not texels) and its large
                    // pattern (5 mips) relative to the mean
                    float3 g3Local = SAMPLE_TEXTURE2D_GRAD(g3, sG, uvG3, gdx * 8, gdy * 8).rgb;
                    float3 pattern = lerp(1.0, SAMPLE_TEXTURE2D_GRAD(g3, sG, uvG3, gdx * 32, gdy * 32).rgb / max(g3Mean, 0.01), _G3V2Surface2.y);

                    [branch] if (kind < 0.5)
                    {
                        // ---- surface: mesh UV, parallax ------------------------------------------------------------
                        float replace = layer.y;
                        float2 uvC = uvMesh * layer.x;
                        float2 cdx = uvdx * layer.x, cdy = uvdy * layer.x;
                        float3 viewTS = mul(tbn, viewWS);
                        float pomFade = saturate((25 - camDist) / 15) * layer.w * _G3V2Surface.z;
                        [branch] if (pomFade > 0.002)
                        {
                            float steps = lerp(16, 6, saturate(viewTS.z));
                            if (replace > 0.999) uvC += G3V2Parallax(TEXTURE2D_ARGS(msk, sM), uvC, cdx, cdy, viewTS, pomFade, steps);
                            // partial classes (planks, tiles): no parallax - it smeared G3's board seams at grazing angles (runner-31)
                        }
                        float3 c = SAMPLE_TEXTURE2D_GRAD(alb, sA, uvC, cdx, cdy).rgb;
                        // far fade of the tiled scan's contrast (runner-31: a 2-3 m tile repeating over a far wall/cliff
                        // reads as a regular lattice); the colour stays G3's
                        c = lerp(c, ccMean, farK);
                        float3 cn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, uvC, cdx, cdy), nStrength);
                        float4 cm = SAMPLE_TEXTURE2D_GRAD(msk, sM, uvC, cdx, cdy);
                        [branch] if (replace > 0.999)
                        {
                            float3 locked = g3Local * (G3V2Luma(c) / lumCc);
                            o.albedo = lerp(G3V2Match(c, ccMean, g3Mean, match, 0) * pattern, locked, _G3V2Color.x);
                            o.n = normalize(mul(cn, tbn));
                            o.mask = cm;
                        }
                        else if (_V2Arch.z > 0.5 && useG3Mask > 0.5)
                        {
                            // ---- arch-1 (25): Varant architecture - G3's own image remastered offline (archtex.py) is the
                            // surface; parallax on its height (integrated from G3's normal map), the class's high-passed CC0
                            // micro detail world triplanar at its real size (layer.x = 1/size), faded with distance
                            float2 uvA = uvG3;
                            // metres per G3 UV unit here (screen derivatives): the parallax depth in metres, whatever the mesh's UV scale
                            float mpu = sqrt(max(dot(dpx, dpx) + dot(dpy, dpy), 1e-12) / max(dot(gdx, gdx) + dot(gdy, gdy), 1e-12));
                            float pomK = _G3V2Arch.y * saturate((16 - camDist) / 10) * _V2Arch.x;
                            [branch] if (pomK > 1e-4)
                            {
                                float3 viewTS = mul(tbn, viewWS);
                                float steps = lerp(14, 6, saturate(viewTS.z));
                                uvA += G3V2Parallax(TEXTURE2D_ARGS(g3m, sGM), uvG3, gdx, gdy, viewTS, min(pomK / max(mpu, 1e-3), 0.08), steps);
                            }
                            float3 g = SAMPLE_TEXTURE2D_GRAD(g3, sG, uvA, gdx, gdy).rgb;
                            float3 gn = float3(0, 0, 1);
                        #if defined(_NORMALMAP)
                            gn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(g3n, sGN, nuv + (uvA - uvG3), ndx, ndy), nStrength);
                        #endif
                            float4 gm = SAMPLE_TEXTURE2D_GRAD(g3m, sGM, uvA, gdx, gdy);
                            o.n = normalize(mul(gn, tbn));
                            float detK = _G3V2Arch.x * (1 - smoothstep(6.0, 18.0, camDist));
                            float grain = 1, gd = 0.5;
                            [branch] if (detK > 0.002)
                            {
                                float3 tw = G3V2TriWeights(nGeo);
                                float k = layer.x;
                                float3 nW = 0; float gs = 0;
                                [unroll] for (int ax = 0; ax < 3; ax++)
                                {
                                    float wa = ax == 0 ? tw.x : ax == 1 ? tw.y : tw.z;
                                    float2 pq = (ax == 0 ? pw.zy : ax == 1 ? pw.xz : pw.xy) * k;
                                    float2 qx = (ax == 0 ? dpx.zy : ax == 1 ? dpx.xz : dpx.xy) * k;
                                    float2 qy = (ax == 0 ? dpy.zy : ax == 1 ? dpy.xz : dpy.xy) * k;
                                    [branch] if (wa > 0.05)
                                    {
                                        gs += G3V2Luma(SAMPLE_TEXTURE2D_GRAD(alb, sA, pq, qx, qy).rgb) / lumCc * wa;
                                        float3 tn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, pq, qx, qy), nStrength * detK);
                                        nW += G3V2PlaneNormal(tn, o.n, ax) * wa;
                                    }
                                    else { gs += wa; nW += o.n * wa; }
                                }
                                grain = lerp(1, clamp(gs, 0.6, 1.5), 0.7 * detK);
                                gd = gs * 0.5;
                                o.n = normalize(nW + o.n * 1e-3);
                            }
                            o.albedo = g * grain;
                            o.mask = gm;
                            o.dbg = dm < 2.5 ? (g) : dm < 3.5 ? (gd.xxx) : dm < 4.5 ? (o.mask.g) : dm < 5.5 ? (float3(0, 0.5, 0.5)) : dm < 6.5 ? (float3(frac(uvA * 4), 0)) : dm < 7.5 ? (o.mask.b) : float3(0, 0, 0);
                        }
                        else
                        {
                            // runner-31 (tower planks were grey smears): G3's own image stays the base - its board seams,
                            // its hue and saturation; the scan only adds a clamped luminance grain, fine normal detail and
                            // roughness
                            float3 g = SAMPLE_TEXTURE2D_GRAD(g3, sG, uvG3, gdx, gdy).rgb;
                            float grain = clamp(G3V2Luma(c) / lumCc, 0.75, 1.3);
                            o.albedo = g * lerp(1.0, grain, 0.5);
                            float3 gn = float3(0, 0, 1);
                        #if defined(_NORMALMAP)
                            gn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(g3n, sGN, nuv + (uvG3 - (uvMesh * g3ST.xy + g3ST.zw)), ndx, ndy), nStrength);
                        #endif
                            float3 dn = float3(cn.xy * 0.3, cn.z);
                            o.n = normalize(mul(normalize(float3(gn.xy + dn.xy, gn.z * dn.z)), tbn));
                            float4 gm = lerp(float4(0, 1, 0.5, _Smoothness), SAMPLE_TEXTURE2D_GRAD(g3m, sGM, uvG3, gdx, gdy), useG3Mask);
                            o.mask = float4(cm.r * replace, gm.g * lerp(1, cm.g, 0.3), useG3Mask > 0.5 ? gm.b : cm.b, min(lerp(gm.a, cm.a, 0.6), 0.3));
                        }
                        o.dbg = dm < 2.5 ? (g3Local) : dm < 3.5 ? ((G3V2Luma(c) / lumCc).xxx * 0.5) : dm < 4.5 ? (o.mask.g) : dm < 5.5 ? (float3(0.5, 0, 0)) : dm < 6.5 ? (pattern * 0.5) : dm < 7.5 ? (o.mask.b) : float3(0, 0, 0);
                    }
                    else if (kind < 1.5 && steep < 0.5)
                    {
                        // ---- ground: world XZ at the real size, hex-tile stochastic ----------------------------------
                        float2 uvW = pw.xz * layer.x;
                        float2 dx = dpx.xz * layer.x, dy = dpy.xz * layer.x;
                        float3 c = 0; float3 tn = float3(0, 0, 1); float4 m = float4(0, 1, 0.5, 0.3);
                        // sand-3 (GPU): in the desert core the scan-based sand replaces this layer's CC0 colour and normals
                        // entirely - skip its nine hex fetches (its mean colour and height stand in)
                        bool sandSkip = sandK * _G3Sand8.w > 0.999;
                        [branch] if (sandSkip)
                        {
                            c = ccMean;
                            m = float4(0, 1, SAMPLE_TEXTURE2D_LOD(msk, sM, float2(0.5, 0.5), 15).b, 0.3);
                        }
                        else if (_G3V2Surface.y > 0.5)
                        {
                            G3Hex h = G3HexSetup(uvW);
                            float3 a1 = SAMPLE_TEXTURE2D_GRAD(alb, sA, h.uv1, mul(dx, h.r1), mul(dy, h.r1)).rgb;
                            float3 a2 = SAMPLE_TEXTURE2D_GRAD(alb, sA, h.uv2, mul(dx, h.r2), mul(dy, h.r2)).rgb;
                            float3 a3 = SAMPLE_TEXTURE2D_GRAD(alb, sA, h.uv3, mul(dx, h.r3), mul(dy, h.r3)).rgb;
                            float3 w = G3HexWeights(h, a1, a2, a3);
                            c = a1 * w.x + a2 * w.y + a3 * w.z;
                            float3 n1 = G3HexNormal(UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, h.uv1, mul(dx, h.r1), mul(dy, h.r1)), nStrength), h.r1);
                            float3 n2 = G3HexNormal(UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, h.uv2, mul(dx, h.r2), mul(dy, h.r2)), nStrength), h.r2);
                            float3 n3 = G3HexNormal(UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, h.uv3, mul(dx, h.r3), mul(dy, h.r3)), nStrength), h.r3);
                            tn = normalize(n1 * w.x + n2 * w.y + n3 * w.z);
                            m = SAMPLE_TEXTURE2D_GRAD(msk, sM, h.uv1, mul(dx, h.r1), mul(dy, h.r1)) * w.x
                              + SAMPLE_TEXTURE2D_GRAD(msk, sM, h.uv2, mul(dx, h.r2), mul(dy, h.r2)) * w.y
                              + SAMPLE_TEXTURE2D_GRAD(msk, sM, h.uv3, mul(dx, h.r3), mul(dy, h.r3)) * w.z;
                        }
                        else
                        {
                            c = SAMPLE_TEXTURE2D_GRAD(alb, sA, uvW, dx, dy).rgb;
                            tn = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, uvW, dx, dy), nStrength);
                            m = SAMPLE_TEXTURE2D_GRAD(msk, sM, uvW, dx, dy);
                        }
                        // remaster-snow (12.3): the snow scan's crust normals at 30 % on snow - at full strength its 1 m crust plates
                        // read as blotches (owner: the clearing); the drift forms, ripples and close-up grain carry the relief
                        if (_G3Snow.x > 0.5) tn = normalize(float3(tn.xy * lerp(1, 0.3, G3SnowScanWeight(kind, ccMean)), tn.z));   // also on roads: the crust plates read as blotches there too (the snow block has its own road terms)
                        // remaster-sand (22.3): in the desert the CC0 coast sand's 15 m beach pattern mostly goes (sandK = desert x a
                        // sand layer); the runtime grain and the wind ripples are the detail there (sand block in Frag)
                        tn = normalize(float3(tn.xy * lerp(1, _G3Sand3.w, sandK), tn.z));
                        // macro variation (two octaves, 23 m and 6 m): brightness only, mean kept
                        float mv = G3V2Noise(pw.xz / 23.0) * 0.65 + G3V2Noise(pw.xz / 6.1 + 17.3) * 0.35;
                        // grass-v3: G3's own fine colour (its tufts, leaves, dry and dark specks at ~1.5 texels) back on
                        // top of the blurred local colour (_G3V2Color.w, json v2G3Fine); the scan then only adds a
                        // weaker luminance grain - with the blurred colour alone and the hue lock every meadow was one tone
                        // pass 6: per material (_V2Blend.z, builder: Varant sand 1 = all of G3's own sand: its ripples and
                        // light dune crests; the flat CC0 coast sand only as a weak grain, _V2Blend.w)
                        float fine = _V2Blend.z > 0 ? _V2Blend.z : _G3V2Color.w;
                        float3 g3Fine = SAMPLE_TEXTURE2D_GRAD(g3, sG, uvG3, gdx * 1.5, gdy * 1.5).rgb;
                        float grainK = (1 - 0.5 * fine) * saturate(_V2Blend.w) * lerp(1, _G3Sand3.w, sandK);
                        // remaster-snow (12.3): on snow G3's fine structure (the plates and scratches of its snow images) goes,
                        // G3 keeps its colour (the layer mean; 15 % of its local colour); the snow scan's grain and the drift forms are the detail
                        [branch] if (_G3Snow.x > 0.5)
                        {
                            float sn = G3SnowScanWeight(kind, ccMean);   // roads too (packedL unused here: G3's road plates read as blotches)
                            fine *= 1 - sn;
                            g3Local = lerp(g3Local, g3Mean, sn * 0.85);   // even 5 mips down G3's snow keeps its 1 m plates
                            grainK = lerp(grainK, 0.3, sn);   // the scan's crust plates (~1 m) only faintly; its normals carry the grain
                        }
                        float3 locked = lerp(g3Local, g3Fine, fine) * lerp(1.0, G3V2Luma(c) / lumCc, grainK);
                        // ai-rocks: terrain ground has its own hue lock (_G3V2Color2.x) - the CC0 share + URP light turned
                        // G3's olive meadow ochre; then G3's own hue more saturated (_G3V2Color2.y)
                        o.albedo = lerp(G3V2Match(c, ccMean, g3Mean, match, 0) * pattern, locked, _G3V2Color2.x) * (1 + (mv * 2 - 1) * 0.12);
                        // sand-3 (22.10): G3's sand colour at >= 1/24 of its tile per pixel (its patches; its painted ripples and
                        // specks gone) - the palette the scan-based sand is matched to
                        [branch] if (sandK > 0.001 && _G3Sand8.w > 0)
                        {
                            float gr = max(max(length(gdx), length(gdy)) * 16, 1.0 / 24);
                            o.g3Soft = SAMPLE_TEXTURE2D_GRAD(g3, sG, uvG3, float2(gr, 0), float2(0, gr)).rgb * (1 + (mv * 2 - 1) * 0.12);
                        }
                        // pass 4: only green/olive hues (Myrtana meadows) - Varant sand went orange and Nordmar snow blue with a global boost
                        float grn = saturate((o.albedo.g - max(o.albedo.r, o.albedo.b)) / max(o.albedo.g, 0.01) * 8);
                        o.albedo = max(lerp(G3V2Luma(o.albedo).xxx, o.albedo, lerp(1, _G3V2Color2.y > 0 ? _G3V2Color2.y : 1, grn)), 0);
                        o.n = normalize(G3V2PlaneNormal(tn, nGeo, 1));
                        o.mask = m;
                        // grass-v3: soil, grass and leaf litter are matte; the scans' gloss (leafy grass 0.31) made the
                        // GGX highlight about as bright as the diffuse light on G3's dark ground (albedo ~0.12-0.2): a
                        // pale beige sheet over every meadow. Cap from _G3V2Far.w (json v2GroundGloss; 0 = no cap);
                        // rain still makes it glossy (wetness below)
                        if (_G3V2Far.w > 0.001) o.mask.a = min(o.mask.a, _G3V2Far.w);
                        o.dbg = dm < 2.5 ? (g3Local) : dm < 3.5 ? ((G3V2Luma(c) / lumCc).xxx * 0.5) : dm < 4.5 ? (o.mask.g) : dm < 5.5 ? (float3(0, 1, 0)) : dm < 6.5 ? (mv.xxx) : dm < 7.5 ? (o.mask.b) : float3(0, 0, 0);
                    }
                    else
                    {
                        // ---- rock (and ground on steep faces): world triplanar, three scales ---------------------------
                        float3 tw = G3V2TriWeights(nGeo);
                        float s0 = layer.x, s1 = layer.x * 0.23, s2 = layer.x * 0.055;
                        float3 acc = 0; float3 nacc = 0; float4 macc = 0;
                        bool rock = kind > 1.5;
                        bool multi = rock && _G3V2Surface2.w > 0.5;
                        const float2x2 r1 = float2x2(0.8, -0.6, 0.6, 0.8), r2 = float2x2(0.28, -0.96, 0.96, 0.28);
                        [unroll] for (int axis = 0; axis < 3; axis++)
                        {
                            float wa = axis == 0 ? tw.x : axis == 1 ? tw.y : tw.z;
                            float2 p = axis == 0 ? pw.zy : axis == 1 ? pw.xz : pw.xy;
                            float2 px = axis == 0 ? dpx.zy : axis == 1 ? dpx.xz : dpx.xy;
                            float2 py = axis == 0 ? dpy.zy : axis == 1 ? dpy.xz : dpy.xy;
                            [branch] if (wa > 0.02)
                            {
                                float3 c1 = SAMPLE_TEXTURE2D_GRAD(alb, sA, p * s0, px * s0, py * s0).rgb;
                                float3 n1 = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, p * s0, px * s0, py * s0), nStrength);
                                float4 m1 = SAMPLE_TEXTURE2D_GRAD(msk, sM, p * s0, px * s0, py * s0);
                                [branch] if (multi)
                                {
                                    // cracks (4.3x, rotated) and large forms (18x normal + isotropic world noise; a layered scan
                                    // repeated at 18x read as stripes, runner-30); stronger macro normals (runner-31: flat plaster)
                                    float2 p2 = mul(r1, p) * s1 + 0.37, p3 = mul(r2, p) * s2 + 0.71;
                                    float2 x2 = mul(r1, px) * s1, y2 = mul(r1, py) * s1, x3 = mul(r2, px) * s2 * 2, y3 = mul(r2, py) * s2 * 2;
                                    float3 c2 = SAMPLE_TEXTURE2D_GRAD(alb, sA, p2, x2, y2).rgb;
                                    float3 n2 = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, p2, x2, y2), nStrength * 1.3);
                                    float3 n3 = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(nrm, sN, p3, x3, y3), nStrength * 1.6);
                                    float big = G3V2Noise(pw.xz / 21.0 + pw.y / 17.0) * 0.6 + G3V2Noise(pw.zy / 9.0 + pw.x / 13.0) * 0.4;
                                    c1 *= lerp(1.0, G3V2Luma(c2) / lumCc, 0.35) * (0.78 + 0.44 * big);
                                    n1 = float3(n1.xy + n2.xy * 0.9 + n3.xy * 0.9, n1.z);
                                    float h2 = SAMPLE_TEXTURE2D_GRAD(msk, sM, p2, x2, y2).b;
                                    m1.b = saturate(m1.b * 0.45 + h2 * 0.55);
                                }
                                acc += c1 * wa;
                                nacc += G3V2PlaneNormal(normalize(n1), nGeo, axis) * wa;
                                macc += m1 * wa;
                            }
                        }
                        // rock colour: the scan's light/dark detail (contrast 1.3) on G3's local grey-green/brown, at G3's
                        // darkness (runner-31: global-mean lock gave pale concrete)
                        float detail = min(pow(max(G3V2Luma(acc) / lumCc, 0), 1.3), 1.45);   // owner: pale streaks
                        detail = lerp(detail, 1, farK);   // runner-31 lattice: the scan's tiles repeat regularly at range
                        // runner-31 vista lattice: G3's terrain UVs are planar (XZ) - sampled through the mesh UV on a cliff
                        // they streak vertically. G3's local colour is therefore taken in world triplanar too, at G3's own
                        // world repeat (metres per UV from the inventory / the layer's UV scale), heavily blurred.
                        float3 g3Tri = 0;
                        // grass-v3: less blur with v2G3Fine (8x -> 2x): G3's own warm/cold patches and strata colour stay on
                        // cliffs and steep ground (world triplanar: no planar-UV streaks); fully blurred they read as one
                        // grey-beige (owner: the original looked richer)
                        float fineT = _G3V2Color.w;
                        if (_G3Snow.x > 0.5 && !rock) fineT *= 1 - G3SnowScanWeight(kind, ccMean);   // remaster-snow: steep snow slopes without G3's plates
                        float triBlur = lerp(8.0, 2.0, fineT);
                        float kw = g3ST.x / max(_V2Surface.w, 0.1);
                        [unroll] for (int ax = 0; ax < 3; ax++)
                        {
                            float wa = ax == 0 ? tw.x : ax == 1 ? tw.y : tw.z;
                            float2 p = ax == 0 ? pw.zy : ax == 1 ? pw.xz : pw.xy;
                            float2 px = ax == 0 ? dpx.zy : ax == 1 ? dpx.xz : dpx.xy;
                            float2 py = ax == 0 ? dpy.zy : ax == 1 ? dpy.xz : dpy.xy;
                            [branch] if (wa > 0.02) g3Tri += SAMPLE_TEXTURE2D_GRAD(g3, sG, p * kw, px * kw * triBlur, py * kw * triBlur).rgb * wa;
                        }
                        float3 locked = g3Tri * lerp(1.0, detail, 1 - 0.4 * fineT) * 0.9;
                        o.albedo = lerp(G3V2Match(acc, ccMean, g3Mean, match, 0) * lerp(1.0, g3Tri / max(g3Mean, 0.01), 0.6 * _G3V2Surface2.y), locked, rock ? _G3V2Color.y : _G3V2Color.x);
                        o.n = normalize(lerp(nacc, nGeo, farK * 0.7));
                        o.mask = macc;
                        o.mask.b = lerp(o.mask.b, SAMPLE_TEXTURE2D_LOD(msk, sM, float2(0.5, 0.5), 15).b, farK);
                        if (!rock && _G3V2Far.w > 0.001) o.mask.a = min(o.mask.a, _G3V2Far.w);   // steep ground: matte too (grass-v3)
                        // cavity: the low parts of the (combined) height darker, round the scan's mean; crevices get lichen
                        float hMean = SAMPLE_TEXTURE2D_LOD(msk, sM, float2(0.5, 0.5), 15).b;
                        o.albedo *= clamp(1 + (o.mask.b - hMean) * (rock ? 1.6 : 0.5), 0.15, 1.12);   // hollows dark, crests barely lifted
                        o.mask.g *= rock ? saturate(1 - (hMean - o.mask.b) * 1.2) : 1;
                        // G3's own green as the moss reference (owner: "use the G3 original for where green goes"): how green
                        // G3's rock colour is here, in world triplanar (no planar-UV streaks)
                        o.green = saturate((g3Tri.g - max(g3Tri.r, g3Tri.b)) / max(g3Tri.g, 0.01) * 5);
                        o.dbg = dm < 2.5 ? (g3Tri) : dm < 3.5 ? (detail.xxx * 0.5) : dm < 4.5 ? (o.mask.g) : dm < 5.5 ? (tw) : dm < 6.5 ? ((0.78 + 0.44 * (G3V2Noise(pw.xz / 21.0 + pw.y / 17.0) * 0.6 + G3V2Noise(pw.zy / 9.0 + pw.x / 13.0) * 0.4)).xxx * 0.5) : dm < 7.5 ? (o.mask.b) : float3(0, 0, 0);
                        [branch] if (rock && _G3V2Weather.w > 0.01 && _V2Surface.y > 0)
                        {
                            // moss/lichen follows the rock: hollows and the strata lines of the 4.3x layer (low height),
                            // only where G3 is green; no noise blobs (owner: camouflage patches)
                            float3 mossG3 = SAMPLE_TEXTURE2D_LOD(_V2MossG3, G3V2_SMP, float2(0.5, 0.5), 15).rgb;
                            float crev = saturate((hMean - o.mask.b) * 6);
                            // grade-1: x the natural-rock moss amount (_G3V2Weather.w = v2RockMoss), only where G3's own rock is green
                            float k = saturate(crev * o.green + o.green * 0.15) * 0.75 * _V2Surface.y * _G3V2Weather.w;
                            float3 tint = mossG3 * (G3V2Luma(o.albedo) / max(G3V2Luma(mossG3), 0.01));
                            o.albedo = lerp(o.albedo, tint, k);
                            o.mask.a = lerp(o.mask.a, 0.15, k);
                        }
                    }
                }
                return o;
            }

            half HeightWeightA(half a, half hA, half hB, half band)
            {
                half ta = hA + a, tb = hB + (1 - a);
                half m = max(ta, tb) - band;
                half wa = max(ta - m, 0), wb = max(tb - m, 0);
                half h = wa / max(wa + wb, 1e-4);
                return (a >= 0.999h) ? 1 : (a <= 0.001h ? 0 : h);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 pw = i.positionWS;
                float3 nGeo = normalize(i.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(pw);
                float camDist = distance(pw, GetCameraPositionWS());
                float3 dpx = ddx(pw), dpy = ddy(pw);          // before any per-pixel branch (runner-31 stripes)
                float2 uvdx = ddx(i.uv), uvdy = ddy(i.uv);
            #if defined(_ALPHATEST_ON)
                // arch-2 (25.7): cut-out cloth (sheets, banners, carpets, drapery) - G3's coverage in the (r3) base alpha;
                // after the derivatives (helper lanes keep them valid), at the mesh UV (no parallax on cloth)
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv * _BaseMap_ST.xy + _BaseMap_ST.zw).a * _BaseColor.a - _Cutoff);
            #endif
                float3 bitangent = i.tangentWS.w * cross(i.normalWS, i.tangentWS.xyz);
                float3x3 tbn = float3x3(normalize(i.tangentWS.xyz), normalize(bitangent), nGeo);
                float steepRaw = smoothstep(0.3, 0.5, 1 - abs(nGeo.y));   // ~45-60 degrees
                float steep = steepRaw * saturate(_G3V2Surface2.x);

                // remaster-sand (section 22): the desert region (Varant) and the layers' sand classes (_V2Snow.zw)
                float desert = G3SandDesertAt(pw, G3V2_SMP);
                float sandKA = _V2Snow.z > 0.5 ? desert : 0, sandKB = _V2Snow.w > 0.5 ? desert : 0;
                V2Sample A = SampleLayer(TEXTURE2D_ARGS(_V2AlbedoA, G3V2_SMP), TEXTURE2D_ARGS(_V2NormalA, G3V2_SMP),
                    TEXTURE2D_ARGS(_V2MaskA, G3V2_SMP), TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap),
                    TEXTURE2D_ARGS(_BumpMap, sampler_BaseMap), TEXTURE2D_ARGS(_MaskMap, sampler_BaseMap),
                    _V2LayerA, _BaseMap_ST, _BumpMap_ST, _UseMask, i.uv, uvdx, uvdy, pw, dpx, dpy, nGeo, tbn, viewWS, camDist, steep, sandKA);
                // G3's detail multiply: ground and G3-only layers (grass-v3), AI layers on their mesh-UV share (pass 4: G3's
                // second stone map, e.g. Myrtana_Rock_02_A = Stone_02 x Stone_03, in its AI version - set by the builder)
                if (_V2LayerA.z < -0.5 || (_V2LayerA.z > 0.5 && _V2LayerA.z < 1.5))
                    A.albedo *= G3V2Detail(TEXTURE2D_ARGS(_DetailMap, sampler_BaseMap), _DetailMap_ST, i.uv, _UseDetail * _DetailLayers.x);
                else if (_V2LayerA.z > 2.5)
                    A.albedo *= G3V2Detail(TEXTURE2D_ARGS(_DetailMap, sampler_BaseMap), _DetailMap_ST, i.uv, _UseDetail * _DetailLayers.x * A.uvMesh);
            #if defined(_SINGLE_LAYER)
                half a = 1;
                V2Sample S = A;
            #else
                V2Sample B = SampleLayer(TEXTURE2D_ARGS(_V2AlbedoB, G3V2_SMP), TEXTURE2D_ARGS(_V2NormalB, G3V2_SMP),
                    TEXTURE2D_ARGS(_V2MaskB, G3V2_SMP), TEXTURE2D_ARGS(_BaseMap2, sampler_BaseMap),
                    TEXTURE2D_ARGS(_BumpMap2, sampler_BaseMap), TEXTURE2D_ARGS(_MaskMap2, sampler_BaseMap),
                    _V2LayerB, _BaseMap2_ST, _BumpMap2_ST, _UseMask2, i.uv, uvdx, uvdy, pw, dpx, dpy, nGeo, tbn, viewWS, camDist, steep, sandKB);
                if (_V2LayerB.z < -0.5 || (_V2LayerB.z > 0.5 && _V2LayerB.z < 1.5))
                    B.albedo *= _UseDetail2 > 0.5
                        ? G3V2Detail(TEXTURE2D_ARGS(_DetailMap2, sampler_BaseMap), _DetailMap2_ST, i.uv, 1)
                        : G3V2Detail(TEXTURE2D_ARGS(_DetailMap, sampler_BaseMap), _DetailMap_ST, i.uv, _UseDetail * _DetailLayers.y);
                else if (_V2LayerB.z > 2.5)
                    B.albedo *= _UseDetail2 > 0.5
                        ? G3V2Detail(TEXTURE2D_ARGS(_DetailMap2, sampler_BaseMap), _DetailMap2_ST, i.uv, B.uvMesh)
                        : G3V2Detail(TEXTURE2D_ARGS(_DetailMap, sampler_BaseMap), _DetailMap_ST, i.uv, _UseDetail * _DetailLayers.y * B.uvMesh);
                half a = 1 - saturate(i.color.a);   // weight of layer A: G3 lerp(src1, src2, VC.a)
                if (_SwapLayers > 0.5) a = 1 - a;
                // bare rock on steep faces: a blend of rock and grass/soil goes to the rock side where the slope is steep
                half rockA = _V2LayerA.z > 1.5 ? 1 : 0, rockB = _V2LayerB.z > 1.5 ? 1 : 0;
                // pass 6: _V2Blend.y scales that push (Varant sand: G3 lets the sand run up the dune slopes)
                if (rockA + rockB == 1) a = lerp(a, rockA, steep * 0.85 * _V2Blend.y);
                // round each scan's own mean height: otherwise the layer with the higher scan heights wins every
                // transition (runner-31: mud 0.61 vs grass 0.45 -> beige valley)
                float hmA = _V2LayerA.z > -0.5 ? SAMPLE_TEXTURE2D_LOD(_V2MaskA, G3V2_SMP, float2(0.5, 0.5), 15).b : 0.5;
                float hmB = _V2LayerB.z > -0.5 ? SAMPLE_TEXTURE2D_LOD(_V2MaskB, G3V2_SMP, float2(0.5, 0.5), 15).b : 0.5;
                a = HeightWeightA(a, saturate(A.mask.b - hmA + 0.5), saturate(B.mask.b - hmB + 0.5), max(_V2Blend.x > 0 ? _V2Blend.x : _G3V2Surface2.z, 0.01));
                V2Sample S = (V2Sample)0;
                S.albedo = lerp(B.albedo, A.albedo, a);
                S.n = normalize(lerp(B.n, A.n, a));
                S.mask = lerp(B.mask, A.mask, a);
                S.green = lerp(B.green, A.green, a);
                S.g3Soft = lerp(B.g3Soft, A.g3Soft, a);
            #endif
                float3 albedo = S.albedo * _BaseColor.rgb;
                float3 n = S.n;
                float smooth = S.mask.a;
                float occl = S.mask.g;
                float h = S.mask.b;

                // ---- moss / grass on up-facing rock, height-blended into the low parts -------------------------------
                float mossAmt = _V2Surface.y * _G3V2Weather.w;
                // grade-1: no moss over the desert (region map G), and only where G3's own rock colour is green (owner: the
                // "leopard" blotches on bare boulders came from the 0.35 floor of the green term)
                [branch] if (mossAmt > 0.01 && _G3SnowRegion.x > 0)
                    mossAmt *= 1 - SAMPLE_TEXTURE2D_LOD(_G3SnowRegionTex, G3V2_SMP, saturate(pw.xz * _G3SnowRegion.xy + _G3SnowRegion.zw), 0).g;
                [branch] if (mossAmt > 0.01)
                {
                    float up = lerp(nGeo.y, n.y, 0.5);
                    float cover = smoothstep(_V2MossParams.y, _V2MossParams.y + 0.25, up) * mossAmt * (0.1 + 0.9 * S.green);
                    float t = cover * 1.5 - h * 0.8;
                    float mw = smoothstep(0.35 - _V2MossParams.z, 0.35 + _V2MossParams.z, t);
                    [branch] if (mw > 0.01)
                    {
                        float2 uvM = pw.xz * _V2MossParams.x;
                        float2 mdx = dpx.xz * _V2MossParams.x, mdy = dpy.xz * _V2MossParams.x;
                        const float2x2 rm = float2x2(0.8, -0.6, 0.6, 0.8);
                        float3 mc = SAMPLE_TEXTURE2D_GRAD(_V2Moss, G3V2_SMP, uvM, mdx, mdy).rgb;
                        float3 mc2 = SAMPLE_TEXTURE2D_GRAD(_V2Moss, G3V2_SMP, mul(rm, uvM) * 0.31 + 0.4, mul(rm, mdx) * 0.31, mul(rm, mdy) * 0.31).rgb;
                        float3 mMean = SAMPLE_TEXTURE2D_LOD(_V2Moss, G3V2_SMP, float2(0.5, 0.5), 15).rgb;
                        float lmm = max(G3V2Luma(mMean), 0.01);
                        float det = clamp(pow(G3V2Luma(mc) / lmm, 1.4) * lerp(1.0, G3V2Luma(mc2) / lmm, 0.5), 0.35, 1.6);
                        // colour and patches from G3's grass image, world XZ at G3's ground repeat (7 m), slightly blurred
                        float2 uvG = pw.xz / 7.1;
                        float3 gcol = SAMPLE_TEXTURE2D_GRAD(_V2MossG3, G3V2_SMP, uvG, dpx.xz / 7.1 * 2, dpy.xz / 7.1 * 2).rgb;
                        mc = gcol * det;
                        float3 mn = normalize(G3V2PlaneNormal(UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_V2MossNormal, G3V2_SMP, uvM, mdx, mdy), 1.4), nGeo, 1));
                        occl = lerp(occl, saturate(0.55 + 0.45 * det), mw);
                        albedo = lerp(albedo, mc, mw);
                        n = normalize(lerp(n, mn, mw * 0.8));
                        smooth = lerp(smooth, 0.12, mw);
                    }
                }

                // ---- buildings: dirt at the base, edge wear --------------------------------------------------------
                [branch] if (_V2Surface.x > 0.5)
                {
                    float above = pw.y - i.baseY;
                    float nz = G3V2Noise(pw.xz * 1.7 + pw.y * 0.9);
                    float dirt = (1 - smoothstep(0.05, 1.1 + nz * 0.7, above)) * (1 - saturate(nGeo.y * 1.5)) * _G3V2Weather.y;
                    albedo = lerp(albedo, albedo * float3(0.58, 0.52, 0.44), saturate(dirt));
                    smooth *= 1 - 0.5 * saturate(dirt);
                    float edge = saturate(length(fwidth(nGeo)) * 2) * saturate((20 - camDist) / 12);
                    float wear = saturate(max((h - 0.62) * 2.5 * (0.5 + nz), edge * 0.4)) * _G3V2Weather.z * saturate((60 - camDist) / 40);
                    float l = G3V2Luma(albedo);
                    albedo = lerp(albedo, lerp(l.xxx, albedo, 0.7) * 1.3, wear * 0.45);
                }

                // ---- remaster snow (section 12): G3's snow layers get a snow BRDF, top faces in snow regions get snow ---
                float snowW = 0, snowPrint = 0, snowPacked = 0;
                [branch] if (_G3Snow.x > 0.5)
                {
                    float3 scanA = _V2LayerA.z > -0.5 ? SAMPLE_TEXTURE2D_LOD(_V2AlbedoA, G3V2_SMP, float2(0.5, 0.5), 15).rgb : 0;
                    float ground = G3SnowScanWeight(_V2LayerA.z, scanA);
                #if !defined(_SINGLE_LAYER)
                    float3 scanB = _V2LayerB.z > -0.5 ? SAMPLE_TEXTURE2D_LOD(_V2AlbedoB, G3V2_SMP, float2(0.5, 0.5), 15).rgb : 0;
                    ground = lerp(G3SnowScanWeight(_V2LayerB.z, scanB), ground, a);
                #endif
                    float region = G3SnowRegionAt(pw, G3V2_SMP);
                    // SnowV2.CoverAmount: ground materials get only the fresh layer, everything else the lasting cover too;
                    // none under a roof (the rain-shadow grid, kept traced while snow is on)
                    // ground materials (no rock layer) get only the fresh layer; anything with rock (cliffs, rock/snow blends of
                    // the landscape: the mountains) also the lasting cover on its ledges
                    bool rockB = _V2LayerB.z > 1.5;
                #if defined(_SINGLE_LAYER)
                    rockB = false;
                #endif
                    bool groundMat = _V2LayerA.z > 0.5 && _V2LayerA.z < 1.5 && !rockB;
                    float fresh = _G3Snow.y;
                    float amount = saturate(groundMat ? fresh * _G3Snow3.x * 1.6 : _G3Snow.z + fresh * _G3Snow3.x) * region;
                    amount *= 1 - G3RainShelter(pw, i.snowDense.y);
                    float acc = 0;
                    [branch] if (amount > 0.005)
                    {
                        float bias = (G3V2Noise(pw.xz * 0.9) - 0.5) * 0.6 + (G3V2Noise(pw.xz * 4.3 + 3.1) - 0.5) * 0.4 + (0.5 - h) * 0.6;   // hollows fill first
                        acc = G3SnowCover(lerp(nGeo.y, n.y, 0.6), amount, bias) * (1 - ground);
                    }
                    snowW = saturate(ground + acc);
                    [branch] if (acc > 0.005)
                    {
                        // top-face snow: G3's own snow image (world XZ at G3's ground repeat: its palette) x the CC0 scan's
                        // grain, the scan's normals
                        float kg = _G3SnowTex.x, ks = _G3SnowTex.y;
                        float3 g3s = SAMPLE_TEXTURE2D_GRAD(_G3SnowAlbedo, G3V2_SMP, pw.xz * kg, dpx.xz * kg, dpy.xz * kg).rgb;
                        float3 sc = SAMPLE_TEXTURE2D_GRAD(_G3SnowScan, G3V2_SMP, pw.xz * ks, dpx.xz * ks, dpy.xz * ks).rgb;
                        float3 scMean = SAMPLE_TEXTURE2D_LOD(_G3SnowScan, G3V2_SMP, float2(0.5, 0.5), 15).rgb;
                        float3 col = g3s * clamp(G3V2Luma(sc) / max(G3V2Luma(scMean), 0.01), 0.8, 1.2);
                        float3 sn = normalize(G3V2PlaneNormal(UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_G3SnowScanNormal, G3V2_SMP, pw.xz * ks, dpx.xz * ks, dpy.xz * ks), 0.8), nGeo, 1));
                        albedo = lerp(albedo, col, acc);
                        n = normalize(lerp(n, sn, acc));
                        occl = lerp(occl, 1, acc * 0.7);
                    }
                    [branch] if (snowW > 0.005)
                    {
                        // snow albedo: G3's snow images are dark (luminance ~0.1-0.15: G3 lit them with a strong flat
                        // ambient), real snow is ~0.8. Lifted by _G3Snow4.x (capped at 0.62), hue kept; fresh snow brighter
                        // and cleaner (G3's dirt patches covered)
                        // soft shoulder towards 0.62 (SnowV2.LiftLuma): the bright snow keeps some of G3's pattern
                        // without the plates of a plain gain
                        // packed road snow (builder: G3's Snow_Path / City_Snow layers): trodden, grey-blue, lumpy, old prints;
                        // fresh snow covers it again. Its edge follows G3's vertex blend (not the height-sharpened layer
                        // weight: that gave a hard decal edge), broken up by noise so it frays into the powder
                    #if defined(_SINGLE_LAYER)
                        float packed = _V2Snow.x;
                    #else
                        float aV = 1 - saturate(i.color.a);
                        if (_SwapLayers > 0.5) aV = 1 - aV;
                        float packed = lerp(_V2Snow.y, _V2Snow.x, aV);
                        [branch] if (packed > 0.001 && packed < 0.999)
                            packed = smoothstep(0.15, 0.85, packed + (G3V2Noise(pw.xz * 0.7 + 5.3) - 0.5) * 0.5 + (G3V2Noise(pw.xz * 2.9 + 1.1) - 0.5) * 0.3);
                    #endif
                        packed *= ground * (1 - 0.8 * fresh * region);
                        float l = max(G3V2Luma(albedo), 0.002);
                        float wht = _G3Snow4.z > 0 ? _G3Snow4.z : 0.62;
                        float kl = _G3Snow4.x * (1 + 0.25 * fresh * region) * lerp(1, 0.85, packed);
                        float nl = wht * (1 - exp(-kl * l / wht));
                        // and G3's blotches at less contrast (pulled towards the lift of G3's typical snow luminance 0.12, so
                        // the mean stays where the lift put it): powder reads as one surface whose relief comes from the light
                        // (forms, ripples, prints), not as plates of grey and white
                        float nlMean = wht * (1 - exp(-kl * 0.12 / wht));
                        nl = lerp(nl, nlMean, 0.4 * _G3Snow4.y * (1 - 0.3 * packed));
                        nl *= 1 - 0.12 * packed;   // trodden snow is greyer (crushed grains, dirt)
                        // all distances: large wind-drift forms (31 m / 9 m) and a slight powder-vs-crust brightness
                        float m1 = G3V2Noise(pw.xz / 31.0 + 4.7), m2 = G3V2Noise(pw.xz / 9.0 + 1.3);
                        nl *= 1 + ((m1 - 0.5) * 0.1 + (m2 - 0.5) * 0.06) * (1 - packed);
                        float3 sa = albedo * max(nl / l, 0.6);
                        sa = lerp(sa, G3V2Luma(sa).xxx, 0.25 * fresh * region * (1 - packed));
                        // trodden: G3's path image is brown - its hue goes, grey-blue compacted snow instead (beige read as a
                        // sand decal from afar)
                        sa = lerp(sa, G3V2Luma(sa) * float3(0.93, 0.98, 1.06), packed * 0.85);
                        albedo = lerp(albedo, sa, snowW);
                        smooth = lerp(smooth, _G3Snow4.w > 0.5 ? lerp(0.3, 0.36, packed) : lerp(0.16, 0.28, packed), snowW);   // fix-178: powder is matte (0.3 + the sky cube read as a wet sheen)
                        [branch] if (ground > 0.01)
                        {
                            // drift forms as normals: finite differences of the two macro octaves (slopes ~0.1 m / m)
                            const float e = 0.7;
                            float2 gx = float2(G3V2Noise((pw.xz + float2(e, 0)) / 31.0 + 4.7) - G3V2Noise((pw.xz - float2(e, 0)) / 31.0 + 4.7),
                                               G3V2Noise((pw.xz + float2(0, e)) / 31.0 + 4.7) - G3V2Noise((pw.xz - float2(0, e)) / 31.0 + 4.7)) / (2 * e) * 31.0 * 0.11;
                            float2 gy = float2(G3V2Noise((pw.xz + float2(e, 0)) / 9.0 + 1.3) - G3V2Noise((pw.xz - float2(e, 0)) / 9.0 + 1.3),
                                               G3V2Noise((pw.xz + float2(0, e)) / 9.0 + 1.3) - G3V2Noise((pw.xz - float2(0, e)) / 9.0 + 1.3)) / (2 * e) * 9.0 * 0.05;
                            float2 gm = (gx / 31.0 + gy / 9.0) * 6 * ground * (1 - packed) * (0.6 + 0.4 * fresh);
                            n = normalize(n + float3(-gm.x, 0, -gm.y));
                            // close-up grain: the snow scan once more, 4.4x finer and rotated (crisp crystals at 1-5 m where
                            // the 2 m tile is magnified); packed snow: trampled lumps instead
                            float near = (1 - smoothstep(4.0, 18.0, camDist)) * ground;
                            [branch] if (near > 0.01)
                            {
                                const float2x2 rg = float2x2(0.6, -0.8, 0.8, 0.6);
                                float kf = 2.2;
                                float2 uf = mul(rg, pw.xz) * kf, fx = mul(rg, dpx.xz) * kf, fy = mul(rg, dpy.xz) * kf;
                                float3 tf = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_G3SnowScanNormal, G3V2_SMP, uf, fx, fy), 0.9);
                                tf.xy = mul(tf.xy, rg);   // back to world x z
                                float3 nf = float3(tf.x * (1 - packed), 0, tf.y * (1 - packed));
                                n = normalize(n + nf * near * 0.6);
                            }
                            // trodden road snow: lumps and old, softened prints (G3SnowTrodden), darker and bluer in the dents
                            float trod = packed * (1 - smoothstep(14.0, 30.0, camDist));
                            [branch] if (trod > 0.01)
                            {
                                float3 th = G3SnowTrodden(pw.xz);
                                n = normalize(n + float3(-th.y, 0, -th.z) * trod);
                                float dent = saturate(0.5 - th.x);
                                albedo *= lerp(1, float3(0.86, 0.9, 0.97), dent * trod);
                            }
                        }
                        // wind ripples / sastrugi on flat powder (filled by fresh snow, none on roads), near the camera
                        float rip = _G3Snow2.w * snowW * (1 - 0.6 * fresh) * (1 - packed) * saturate(n.y * 2 - 1) * (1 - smoothstep(25, 60, camDist));
                        [branch] if (rip > 0.01)
                        {
                            float2 g = G3SnowRippleGrad(pw);
                            n = normalize(n + float3(-g.x, 0, -g.y) * rip * 8);
                        }
                        snowPacked = packed;
                        // footprints and trails (trail map)
                        [branch] if (_G3SnowTrail.x > 0 && snowW > 0.25)
                        {
                            G3SnowPrint pr = G3SnowTrailSample(pw, n, viewWS, camDist, dpx, dpy, i.snowDense.x);
                            float k = saturate(snowW * 1.5 - 0.4);
                            snowPrint = pr.depth * k;
                            n = normalize(lerp(n, pr.n, k));
                            // compacted snow: darker, bluish and glossier at the bottom (crushed grains); the floor in the
                            // shadow of its own wall
                            albedo *= lerp(1, float3(0.48, 0.56, 0.74), snowPrint);
                            albedo *= 1 - 0.6 * pr.shadow * k;
                            albedo *= 1 + 0.1 * pr.rim * k;
                            occl *= 1 - 0.45 * snowPrint;
                            smooth = lerp(smooth, _G3Snow4.w > 0.5 ? 0.42 : 0.3, snowPrint);
                        }
                    }
                }

                // ---- remaster sand (section 22): Varant's sand layers - grain, matte, wind ripples, prints ---------------
                float sandW = 0, sandDry = 0, sandPrint = 0, ripShadow = 1;
                // arch-4: indoors / under a roof (G3V2Covered) - no wind ripples, no settled sand, no dust there
                bool coverMesh = !(abs(_V2LayerA.z - 1) < 0.5 || abs(_V2LayerB.z - 1) < 0.5);
                float covered = desert > 0.001 ? G3V2Covered(pw, nGeo, i.snowDense.y, pw.y - i.baseY, coverMesh) : 0;
                // arch-4: the sand / dust / weathering terms read G3's relief (h) for the low parts; on cloth (carpet weave,
                // G3's coarse cloth normal integrated) and metal (gold mosaic grout) that relief is a regular fine lattice
                // and the sand filled it as a dotted grid ("chainmail" on the round carpets and the temple pillar bases)
                bool flatRelief = abs(_V2Arch.z - 6) < 0.5 || abs(_V2Arch.z - 7) < 0.5;
                [branch] if (desert > 0.001)
                {
                    float3 cls = G3SandClassWeights(_V2Snow.z);   // x loose, y wet, z packed path
                #if !defined(_SINGLE_LAYER)
                    cls = lerp(G3SandClassWeights(_V2Snow.w), cls, a);
                #endif
                    float looseShare = cls.x;   // of the layer blend (ripples: the core of loose sand only)
                    cls *= desert * (1 - snowW);
                    sandW = saturate(cls.x + cls.y + cls.z);
                    sandDry = saturate(cls.x + cls.z);
                    [branch] if (sandW > 0.005)
                    {
                        float foot = max(max(length(dpx), length(dpy)), 1e-4);
                        float wetK = cls.y / max(sandW, 1e-3);
                        float coarse = 0;
                        [branch] if (_G3Sand8.w > 0)
                        {
                            // sand-3 (22.10): the scan-based base - CC0 fine grain (hex-tiled), mid mottling and coarse pebbly lag
                            // sand on G3's sand colour (blurred to its patches, graded towards sunlit golden sand), large-scale
                            // value patches, windward faces lighter and lee faces darker
                            float edgeK = (1 - G3SandCoreFade(looseShare)) * 0.55 + cls.z / max(sandW, 1e-3) * 0.3;
                            G3SandScanOut sc = G3SandScan(pw, dpx, dpy, viewWS, camDist, nGeo, edgeK);
                            float3 g3s = G3V2Luma(S.g3Soft) > 1e-4 ? S.g3Soft : albedo;
                            float3 pal = G3SandGrade(g3s);
                            float lee = dot(nGeo.xz, _G3Sand2.xy);
                            float macro = (G3SandNoiseD(pw.xz / 23.0, 91u).x * 0.6 + G3SandNoiseD(pw.xz / 71.0, 93u).x * 0.4 - 0.5) * 2 * _G3Sand12.x;
                            pal *= (1 + macro) * (1 + _G3Sand9.w * (smoothstep(0.02, 0.25, -lee) - 0.6 * smoothstep(0.05, 0.3, lee)));
                            float sk = _G3Sand8.w * sandW;
                            albedo = lerp(albedo, pal * lerp(1, sc.ratio, 1 - 0.5 * wetK), sk);
                            n = normalize(n + float3(sc.nxz.x, 0, sc.nxz.y) * sk * (1 - 0.6 * wetK));
                            occl *= lerp(1, sc.ao, sk * 0.3);
                            h = lerp(h, sc.h, sk);
                            coarse = sc.coarse * sk;
                        }
                        else
                        {
                            // grain (runtime tile, albedo factor of mean 1: G3's sand colour stays; wet sand clumps: less contrast)
                            float3 gr = G3SandGrain(pw, dpx, dpy, camDist, viewWS);
                            albedo *= lerp(1, gr.x, sandW * (1 - 0.5 * wetK));
                            n = normalize(n + float3(gr.y, 0, gr.z) * sandW * (1 - 0.6 * wetK));
                        }
                        // dry sand is matte (the CC0 coast sand scan 0.04); packed a little firmer, wet glossier
                        float sm = dot(cls, _G3Sand4.xyz) / max(sandW, 1e-3);
                        smooth = lerp(smooth, sm, sandW);
                        // macro tint (sand-2): 70 m / 160 m warm-cool drift of the dry sand (G3's colour stays the mean)
                        [branch] if (_G3Sand6.z > 0)
                        {
                            float mt = G3SandNoiseD(pw.xz / 70.0, 51u).x * 0.6 + G3SandNoiseD(pw.xz / 160.0, 53u).x * 0.4 - 0.5;
                            albedo *= 1 + mt * 2 * _G3Sand6.z * float3(0.9, 0.15, -0.8) * sandDry;
                        }
                        // wind ripples (sand-2): the core of loose sand, across the prevailing wind, in patches; none on steep
                        // and lee faces, near layer edges (grass, rock, paths), below the pixel size or far away; slopes only
                        float ripBase = cls.x * G3SandCoreFade(looseShare) * G3SandRippleSlope(nGeo, _G3Sand2.xy) * (1 - coarse);
                        ripBase *= 1 - covered;
                        float rip = _G3Sand.y * ripBase * G3SandFootFade(foot, _G3Sand2.z) * G3SandDistFade(camDist);
                        // sand-3: mega ripples (0.4-0.5 m) that carry the ripple field to 5-60 m
                        float ripM = _G3Sand11.x * ripBase * G3SandFootFade(foot, _G3Sand2.z * max(_G3Sand11.y, 1)) * (1 - smoothstep(30, 70, camDist));
                        [branch] if (ripM > 0.01 && _G3Sand5.y > 0)
                        {
                            float2 slM = G3SandMegaRipples(pw, dpx, dpy, ripM);
                            n = normalize(n + float3(-slM.x, 0, -slM.y));
                        }
                        [branch] if (rip > 0.01 && _G3Sand5.y > 0)
                        {
                            float2 rq = float2(dot(pw.xz, _G3Sand2.xy), dot(pw.xz, float2(-_G3Sand2.y, _G3Sand2.x)));
                            float2 sl;
                            G3SandRipples(pw, viewWS, dpx, dpy, rip * G3SandRipplePatch(rq, _G3Sand5.z), camDist, sl, ripShadow);
                            n = normalize(n + float3(-sl.x, 0, -sl.y));
                        }
                        // footprints (the shared print grid; sand prints fill in on the wind clock); loose and wet sand only
                        float takes = cls.x + cls.y;
                        [branch] if (_G3SandTrail.x > 0 && _G3SnowTrail.x > 0 && takes > 0.25)
                        {
                            G3SnowPrint pr = G3SnowTrailSample(pw, n, viewWS, camDist, dpx, dpy, 0);
                            float k = saturate(takes * 1.5 - 0.4);
                            float rel = max(_G3SandTrail.z, 0.05);   // stored depth = sand depth / snow depth
                            sandPrint = saturate(pr.depth / rel) * k;
                            n = normalize(lerp(n, pr.n, k));
                            albedo *= 1 - _G3SandTrail.w * sandPrint;            // compacted floor, a little moister
                            albedo *= 1 - (_G3Sand8.w > 0 ? 0.7 : 0.5) * pr.shadow * k;   // in the shadow of its own wall (sand-3: crisper)
                            albedo *= 1 + (_G3Sand8.w > 0 ? 0.22 : 0.12) * saturate(pr.rim / rel) * k;   // loose grains pushed up round it
                            occl *= 1 - 0.3 * sandPrint;
                        }
                    }
                }

                // sand-3 (22.10): diffuse contrast (Journey: the detail normals - ripples, scans, prints - exaggerated against the
                // ground's own normal for the light, so the ripples read at noon and pop at dawn/dusk)
                [branch] if (sandDry > 0.005 && _G3Sand10.x > 0)
                    n = normalize(nGeo + (n - nGeo) * (1 + _G3Sand10.x * sandDry));
                // arch-2 (25.7): the share of the settled sand / dust that the storm deposit leaves on this pixel
                float archSandCover = 1;
                [branch] if (_V2Arch.z > 0.5 && _G3V2ArchSand.w > 0.5)
                {
                    float dep = _G3V2ArchSand.x;
                    [branch] if (abs(_V2Arch.z - 6) < 0.5)
                    {
                        // per object: a hash of its origin (tents differ a little); per pixel: a shedding threshold
                        float3 org = float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23);
                        float varT = frac(sin(dot(org.xz, float2(12.9898, 78.233))) * 43758.5453);
                        float steepC = 1 - saturate((nGeo.y - 0.3) / 0.6);
                        float apex = smoothstep(2.5, 5.5, pw.y - i.baseY);
                        float fold = saturate((0.5 - h) * 2) * (1 - smoothstep(0.8, 0.95, nGeo.y));   // arch-4: tent folds, not a flat carpet's weave
                        float pn = G3SandNoiseD(pw.xz / 0.9 + pw.y * 0.37 + varT * 17, 79u).x;
                        float thr = min(saturate(0.05 + 0.35 * steepC + 0.2 * apex - 0.35 * fold + 0.3 * (pn - 0.5) + 0.15 * (varT - 0.5)), 0.85);
                        // deposit 1 = the full sand-3 cover (owner-approved post-storm look); at rest folds keep patches and the
                        // rest a faint film (0.3 x deposit)
                        archSandCover = saturate(max((dep - thr) / 0.15, 0.3 * dep)) * _G3V2ArchSand.y;
                    }
                    else archSandCover = 1 - _G3V2ArchSand.z * (1 - dep);
                }
                // sand-3: wind-blown sand settled on meshes in the desert - ledges and tops (the low parts first), the foot of
                // buildings (from the object's base height), never on the sand layers themselves
                [branch] if (desert > 0.001 && _G3Sand11.w > 0 && sandW < 0.99)
                {
                    float nz = G3SandNoiseD(pw.xz / 1.7 + pw.y * 0.31, 71u).x * 0.7 + G3SandNoiseD(pw.xz / 0.45 + pw.y, 73u).x * 0.3;
                    float up = smoothstep(0.55, 0.85, nGeo.y);
                    float ledge = up * smoothstep(0.45, 0.75, nz * 0.7 + (0.55 - (flatRelief ? 0.5 : h)) * 1.2 + 0.25);
                    float above = pw.y - i.baseY;
                    float foot0 = _V2Surface.x > 0.5 ? (1 - smoothstep(0.05, 0.2 + 0.7 * nz, above)) * saturate(0.5 + nGeo.y) : 0;
                    float acc = saturate(max(foot0, ledge * 0.9)) * desert * _G3Sand11.w * (1 - sandW) * (1 - covered);
                    // arch-2 (25.7): on Varant architecture the settled sand follows the storm deposit - cloth by a per-pixel
                    // threshold (steep parts and the apex shed first, folds and low parts keep it; deposit 1 = the full
                    // sand-3 look), the stone ledges only a little (archStoneSand)
                    [branch] if (_V2Arch.z > 0.5 && _G3V2ArchSand.w > 0.5) acc *= archSandCover;
                    [branch] if (acc > 0.005)
                    {
                        float3 sandCol = G3SandGrade(SAMPLE_TEXTURE2D_LOD(_G3SandAlbedo, G3V2_SMP, float2(0.5, 0.5), 15).rgb);
                        float kf = max(_G3Sand8.x, 0.3);
                        bool flatP = abs(nGeo.y) > 0.5;
                        float2 pp = flatP ? pw.xz : (abs(nGeo.x) > abs(nGeo.z) ? pw.zy : pw.xy);
                        float2 px = flatP ? dpx.xz : (abs(nGeo.x) > abs(nGeo.z) ? dpx.zy : dpx.xy);
                        float2 py = flatP ? dpy.xz : (abs(nGeo.x) > abs(nGeo.z) ? dpy.zy : dpy.xy);
                        float3 fd = _G3Sand8.w > 0 ? SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineA, G3V2_SMP, pp * kf, px * kf, py * kf).rgb / max(G3SandScanMean(_G3SandScanFineA), 1e-3) : 1;
                        float e = smoothstep(0, 1, acc);
                        albedo = lerp(albedo, sandCol * fd, e);
                        n = normalize(lerp(n, float3(0, 1, 0), e * 0.6 * up) + nGeo * 1e-3);
                        smooth = lerp(smooth, _G3Sand4.x, e);
                        occl = lerp(occl, 1, e * 0.5);
                        sandDry = max(sandDry, e * 0.8);   // sheen, backscatter and glints on it too
                    }
                }

                // ---- arch-1 (25): sand dust on Varant architecture - in the joints and cracks (low parts of G3's height), a
                // thin film on up faces; sand-3's sandEdge above does the ledges and the foot of buildings (no second copy)
                [branch] if (_V2Arch.y > 0 && _G3V2Arch.z > 0 && desert > 0.001)
                {
                    float joints = saturate((0.42 - (flatRelief ? 0.5 : h)) * 3.0);
                    float film = smoothstep(0.2, 0.9, nGeo.y) * 0.45 + smoothstep(-0.3, 0.3, n.y) * 0.15;
                    float dn = G3SandNoiseD(pw.xz / 2.3 + pw.y * 0.47, 77u).x;
                    float dust = saturate((joints * 0.75 + film) * (0.6 + 0.8 * dn)) * _V2Arch.y * _G3V2Arch.z * desert * (1 - sandW) * archSandCover * (1 - covered);
                    [branch] if (dust > 0.003)
                    {
                        float3 sandCol = G3SandGrade(SAMPLE_TEXTURE2D_LOD(_G3SandAlbedo, G3V2_SMP, float2(0.5, 0.5), 15).rgb);
                        // the dust takes the wall's own brightness a little (fine dust, not a sand decal)
                        float3 dc = lerp(sandCol, sandCol * (G3V2Luma(albedo) / max(G3V2Luma(sandCol), 0.02)), 0.35);
                        float e = dust * 0.4;
                        albedo = lerp(albedo, dc, e);
                        smooth = lerp(smooth, 0.05, e);
                        occl = lerp(occl, 1, e * 0.3);
                    }
                }

                // ---- arch-3 (26): weathering of Myrtana / Nordmar architecture (_V2Arch.y: arch classes and the shared
                // v2 CC0 stems). Myrtana (neither desert nor snow region): G3's moss colour in the joints of up faces and on
                // the low parts, dirt in the low joints of walls. Nordmar: rime in the joints and a thin frost at the edge of
                // the snow cover - only where G3SnowV2's cover amount says it is snowy, and fading out where its snow lies
                // (snowW), so the snow layer itself is never drawn twice. Subtle; wetness below darkens both.
                [branch] if (_V2Arch.y > 0 && (_G3V2Arch2.x > 0 || _G3V2Arch2.y > 0) && camDist < 150)
                {
                    float2 rgn = _G3SnowRegion.x > 0 ? SAMPLE_TEXTURE2D_LOD(_G3SnowRegionTex, G3V2_SMP, saturate(pw.xz * _G3SnowRegion.xy + _G3SnowRegion.zw), 0).rg : float2(0, 0);
                    float joints = saturate((0.42 - (flatRelief ? 0.5 : h)) * 3.0);
                    float upF = smoothstep(0.25, 0.9, nGeo.y);
                    float lowF = 1 - smoothstep(0.1, 1.8, pw.y - i.baseY);
                    float wn = G3V2Noise(pw.xz * 0.61 + pw.y * 0.83);
                    float wn2 = G3V2Noise(pw.xz * 2.7 + pw.y * 2.3 + 5.3);
                    float far = 1 - smoothstep(60.0, 150.0, camDist);
                    float temperate = saturate(1 - rgn.r - rgn.g) * (1 - sandW);
                    [branch] if (_G3V2Arch2.x > 0 && temperate > 0.01)
                    {
                        float3 mossC = SAMPLE_TEXTURE2D_LOD(_V2MossG3, G3V2_SMP, float2(0.5, 0.5), 15).rgb;
                        if (G3V2Luma(mossC) < 0.01 || mossC.g < mossC.r) mossC = float3(0.20, 0.23, 0.10);   // no G3 moss bound: olive
                        float l = G3V2Luma(albedo);
                        float3 mc = mossC * (l / max(G3V2Luma(mossC), 0.02)) * 0.85;   // the wall's own brightness, a little darker
                        // grade-1: the joints only (the up-face / low-band patches read as leopard spots on Rock_01, statues,
                        // gravestones), deeper joints, less of it
                        float mossK = saturate((0.34 - h) * 4.0) * (0.3 + 0.7 * max(upF, lowF)) * smoothstep(0.35, 0.8, wn * 0.7 + wn2 * 0.3);
                        float dirtK = joints * (1 - upF) * (0.35 + 0.65 * lowF) * (0.6 + 0.8 * wn2);
                        float k = _V2Arch.y * _G3V2Arch2.x * temperate * (1 - snowW) * far;
                        float em = saturate(mossK * 0.25 * k), ed = saturate(dirtK * 0.3 * k);
                        albedo = lerp(albedo, albedo * float3(0.62, 0.56, 0.48), ed);
                        albedo = lerp(albedo, mc, em);
                        smooth = lerp(smooth, 0.08, max(em, ed));
                    }
                    [branch] if (_G3V2Arch2.y > 0 && _G3Snow.x > 0.5 && rgn.r > 0.01)
                    {
                        float cover = saturate(_G3Snow.z + _G3Snow.y * _G3Snow3.x) * rgn.r;
                        float frostK = saturate(joints * 0.8 + upF * 0.35 * (0.5 + wn)) * smoothstep(0.25, 0.65, wn2 * 0.6 + 0.4 * (1 - h));
                        float e = saturate(frostK * cover * (1 - snowW) * 0.45 * _V2Arch.y * _G3V2Arch2.y * far);
                        [branch] if (e > 0.003)
                        {
                            float wl = _G3Snow4.z > 0 ? _G3Snow4.z : 0.62;
                            float3 rime = float3(0.92, 0.96, 1.04) * wl * 0.85;
                            albedo = lerp(albedo, rime, e);
                            smooth = lerp(smooth, 0.32, e);
                            occl = lerp(occl, 1, e * 0.3);
                        }
                    }
                }

                // ---- wetness (G3 rain): darker, glossier, more on exposed and low parts -----------------------------
                float wetMetal = 0;   // fix-255
                [branch] if (_G3V2Weather.x > 0.01)
                {
                    float exposed = 0.35 + 0.65 * saturate(nGeo.y + 0.3);
                    float wet = _G3V2Weather.x * exposed * lerp(1, 1.3 - h * 0.6, 0.5);
                    wet *= 1 - G3RainShelter(pw, i.snowDense.y);   // fix-130: dry under roofs (top-down rain shadow)
                    wet *= 1 - snowW;   // fix-194: snow never turns glossy-wet (a rain blend reaching Nordmar read as a wet sheen)
                    albedo *= lerp(1, 0.6, saturate(wet));
                    // fix-255: 0.86 x 0.85 -> 0.68 x 0.8: a film on rough ground, not a mirror (the eye adapted to the dark
                    // forest saw the sky in it blown out to white)
                    smooth = lerp(smooth, 0.68, saturate(wet) * 0.8);
                    // fix-255: the water film fills the fine relief (Lagarde, "Water drop 3b - physically based wet
                    // surfaces"): the relief normals flatten towards the geometric one - each grain of the
                    // detail normal mirrored a bright sky texel (white sparkly blotches on rainy forest ground); the film
                    // is dielectric water, whatever metal the dry mask said
                    n = normalize(lerp(n, nGeo, saturate(wet) * 0.4));
                    wetMetal = saturate(wet);
                }

                [branch] if (_G3V2Debug.x > 1.5)
                {
                    // runner debug modes (G3WorldLook.json "v2DebugMode"): 2 G3 base, 3 CC0 detail, 4 AO, 5 triplanar
                    // weights, 6 macro noise, 7 height, 8 final albedo, 9 normal - unlit, no fog
                #if defined(_SINGLE_LAYER)
                    float3 dv = A.dbg;
                #else
                    float3 dv = lerp(B.dbg, A.dbg, a);
                #endif
                    if (_G3V2Debug.x > 8.5) dv = n * 0.5 + 0.5;
                    else if (_G3V2Debug.x > 7.5) dv = albedo;
                    return half4(dv, 1);
                }
                [branch] if (_G3V2Debug.x > 0.5)
                    albedo = float3(_V2LayerA.z < -0.5 ? 0.5 : 0, _V2LayerA.z > 0.5 && _V2LayerA.z < 1.5 ? 0.6 : 0, _V2LayerA.z > 1.5 ? 0.6 : 0) + (_V2LayerA.z > -0.5 && _V2LayerA.z < 0.5 ? 0.4 : 0);

                InputData input = (InputData)0;
                input.positionWS = pw;
                input.positionCS = i.positionCS;
                input.normalWS = n;
                input.viewDirectionWS = viewWS;
                input.shadowCoord = TransformWorldToShadowCoord(pw);
                input.fogCoord = i.fogFactor;
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
            #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
                input.bakedGI = SampleProbeVolumePixel(i.vertexSH, pw, n, viewWS, input.normalizedScreenSpaceUV);
            #else
                input.bakedGI = SampleSHPixel(i.vertexSH, n);
            #endif
                input.shadowMask = half4(1, 1, 1, 1);
                input.tangentToWorld = tbn;

                SurfaceData s = (SurfaceData)0;
                s.albedo = albedo;
                s.alpha = 1;
                s.metallic = S.mask.r * (1 - wetMetal);
                s.smoothness = saturate(smooth);
                s.occlusion = saturate(occl);
                s.normalTS = half3(0, 0, 1);
                half4 color;
                [branch] if (_G3V2Surface.x > 0.5)
                {
                    color = UniversalFragmentPBR(input, s);
                    BRDFData brdf;
                    InitializeBRDFData(s.albedo, s.metallic, s.specular, s.smoothness, s.alpha, brdf);
                    half fres = Pow4(1 - saturate(dot(n, viewWS)));
                    half3 envBrdf = EnvironmentBRDFSpecular(brdf, fres) * s.occlusion;
                    half3 envSpec = envBrdf * _GlossyEnvironmentColor.rgb;
                    [branch] if (_G3SkyReflection.x > 0)
                    {
                        // remaster-look2 (11.9): the live sky cube (G3SkyReflection) instead of the flat glossy colour; the
                        // light-ratio pass later removes it where the sky is hidden (caves, under roofs); below the horizon
                        // the reflection fades (the ground, not the sky, is mirrored there)
                        half3 R = reflect(-viewWS, n);
                        half mip = PerceptualRoughnessToMipmapLevel(brdf.perceptualRoughness, (int)_G3SkyReflection.y);
                        half3 env = SAMPLE_TEXTURECUBE_LOD(_G3SkyCube, sampler_G3SkyCube, R, mip).rgb * saturate(R.y * 3 + 0.4);
                        // fix-178: snow does not mirror the sky (matte grains; the sparkle is the glints' job)
                        // fix-258: the wet film mirrors the overcast sky at 30 % strength (the rain-dark ground under a
                        // bright cloud deck read pale blue-white at the exposure of the scene; wet earth stays darker than dry)
                        color.rgb = max(color.rgb - envSpec, 0) + envBrdf * env * _G3SkyReflection.x * (1 - 0.7 * snowW * step(_G3Snow4.w, 0.5)) * (1 - 0.7 * wetMetal);
                    }
                    else color.rgb = max(color.rgb - envSpec * 0.8, 0);
                    // fix-258: no sun glare on the wet film. Rain is an overcast sky: G3 still drives a directional sun, and its
                    // GGX lobe on the glossy wet ground filled the whole path towards the sun with a silver-white sheen,
                    // sparkling on every grain of the detail normal (Ardea 10 h, looking south-east; Faring, looking away
                    // from the sun, stayed dark). The overcast light reaches wet ground diffusely: 85 % of the sun's specular
                    // lobe goes with the film; the sky reflection above stays (dark, mildly glossy)
                    [branch] if (wetMetal > 0.01)
                    {
                        Light wl = GetMainLight(input.shadowCoord, pw, input.shadowMask);
                        half wndl = saturate(dot(n, wl.direction));
                        half3 wspec = brdf.specular * DirectBRDFSpecular(brdf, n, wl.direction, viewWS)
                                    * wl.color * (wl.distanceAttenuation * wl.shadowAttenuation * wndl);
                        color.rgb = max(color.rgb - wspec * (0.85 * wetMetal), 0);
                    }
                    // arch-1 (25): sunlight through tent cloth - seen from the side away from the sun; the shadow is sampled a
                    // cloth's thickness towards the light, so the lit wall's inside glows while the far wall stays dark
                    [branch] if (_V2Arch.w > 0 && _G3V2Arch.w > 0)
                    {
                        Light tl = GetMainLight();
                        float back = saturate(-dot(nGeo, tl.direction));
                        [branch] if (back > 0.01)
                        {
                            float3 pt = pw + tl.direction * 0.12 - nGeo * 0.02;
                            half ta = MainLightRealtimeShadow(TransformWorldToShadowCoord(pt));
                            float vt = 0.5 + 0.5 * saturate(dot(-viewWS, tl.direction));
                            color.rgb += s.albedo * s.albedo * 2.0 * tl.color * ta * back * vt * _V2Arch.w * _G3V2Arch.w;
                        }
                    }
                    [branch] if (snowW > 0.005)
                    {
                        // snow light (section 12): soft wrap of the sun past the terminator (light scattered through the
                        // snowpack comes back out blue-shifted; cheap translucency after Barre-Brisebois and Bouchard 2011),
                        // sky-blue shade where the sun does not reach, sun glints
                        Light ml = GetMainLight(input.shadowCoord, pw, input.shadowMask);
                        float ndl = dot(n, ml.direction);
                        float atten = ml.shadowAttenuation * ml.distanceAttenuation;
                        float w = _G3Snow2.x;
                        float wr = saturate((ndl + w) / (1 + w));
                        float3 scatter = s.albedo * ml.color * atten * max(wr * wr - saturate(ndl), 0) * float3(0.8, 0.93, 1.12) * _G3Snow2.y;
                        float direct = saturate(ndl) * atten;
                        float3 tint = float3(0.88, 1.0, 1.22);
                        tint /= G3V2Luma(tint);
                        color.rgb *= lerp(1, tint, (1 - direct) * _G3Snow2.z * snowW);
                        float3 Hs = normalize(ml.direction + viewWS);
                        float gl = G3SnowGlint(pw, n, Hs, dpx, dpy, camDist) * direct * (1 - 0.8 * snowPrint) * (1 - 0.7 * snowPacked);
                        color.rgb += (scatter + gl * ml.color) * snowW;
                    }
                    [branch] if (sandDry > 0.005 && (_G3Sand.w > 0 || _G3Sand6.x > 0 || _G3Sand6.y > 0 || _G3Sand10.y > 0 || ripShadow < 0.999))
                    {
                        Light sl = G3SandSun(input.shadowCoord, pw);
                        float satten = sl.shadowAttenuation * sl.distanceAttenuation;
                        // sand-2: the ripples' own soft shadows at low sun take their share of the direct light back
                        [branch] if (ripShadow < 0.999)
                            color.rgb = max(color.rgb - LightingPhysicallyBased(brdf, sl, n, viewWS) * (1 - ripShadow), 0);
                        float sdirect = saturate(dot(n, sl.direction)) * satten * ripShadow;
                        // Journey-like: a broad soft sheen and a backlit rim towards the sun (dry sand, in sun only)
                        color.rgb += G3SandSheen(n, viewWS, sl.direction, s.albedo) * sl.color * satten * ripShadow * sandDry * (1 - 0.6 * sandPrint);
                        // sand-3: backscatter of the grains (opposition: brighter with the sun behind the eye)
                        [branch] if (_G3Sand10.y > 0)
                            color.rgb += G3SandBackscatter(n, viewWS, sl.direction, s.albedo) * sl.color * satten * ripShadow * sandDry;
                        // remaster-sand: a faint quartz/mica sparkle on dry sand, in direct sun only
                        [branch] if (_G3Sand.w > 0)
                        {
                            float3 Hd = normalize(sl.direction + viewWS);
                            // fx-2: only on near-level sand (the grain cells are laid out in xz: on walls and steep faces they
                            // smear into stray points that read as sparks in the air)
                            float sg = G3SandGlint(pw, n, Hd, dpx, dpy, camDist) * sdirect * (1 - 0.8 * sandPrint) * smoothstep(0.6, 0.85, n.y);
                            color.rgb += sg * sl.color * sandDry;
                        }
                    }
                }
                else
                {
                    s.specular = lerp(0.03, 0.3, s.smoothness).xxx;
                    color = UniversalFragmentBlinnPhong(input, s);
                }
                color.rgb = MixFog(color.rgb, input.fogCoord);
                color.a = 1;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex G3ShadowVertex
            #pragma fragment ShadowPassFragment
            #pragma shader_feature_local _SINGLE_LAYER
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

            // URP's ShadowPassVertex + the snow prints on a dense ground patch (12.6): the shadow map sees the same surface
            struct G3ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                float4 uv2        : TEXCOORD2;
                float4 uv3        : TEXCOORD3;
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                half4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings G3ShadowVertex(G3ShadowAttributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                G3ReliefMorph(input.positionOS.xyz, input.normalOS, input.uv2, input.uv3, input.uv4, input.uv5, input.uv6, false);
                float dense;
                float3 positionWS = G3SnowDisplaceWS(TransformObjectToWorld(input.positionOS.xyz), input.uv3, input.color, dense);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                output.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS)));
            #if defined(_ALPHATEST_ON)
                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
            #endif
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex G3DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma shader_feature_local _SINGLE_LAYER
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"

            // URP's DepthOnlyVertex + the snow prints on a dense ground patch (12.6): the depth prepass must match ForwardLit
            struct G3DepthAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                float4 uv2        : TEXCOORD2;
                float4 uv3        : TEXCOORD3;
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                half4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings G3DepthOnlyVertex(G3DepthAttributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 nrm = input.normalOS;
                G3ReliefMorph(input.positionOS.xyz, nrm, input.uv2, input.uv3, input.uv4, input.uv5, input.uv6, false);
                float dense;
                output.positionCS = TransformWorldToHClip(G3SnowDisplaceWS(TransformObjectToWorld(input.positionOS.xyz), input.uv3, input.color, dense));
            #if defined(_ALPHATEST_ON)
                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
            #endif
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma shader_feature_local _SINGLE_LAYER
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile _ _WRITE_SMOOTHNESS
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #if defined(LOD_FADE_CROSSFADE)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normal     : NORMAL;
                float2 texcoord   : TEXCOORD0;
                float4 uv2        : TEXCOORD2;
                float4 uv3        : TEXCOORD3;
                float4 uv4        : TEXCOORD4;
                float4 uv5        : TEXCOORD5;
                float4 uv6        : TEXCOORD6;
                half4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS    : TEXCOORD2;
            #if defined(_ALPHATEST_ON)
                float2 uv         : TEXCOORD0;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                G3ReliefMorph(input.positionOS.xyz, input.normal, input.uv2, input.uv3, input.uv4, input.uv5, input.uv6, true);
                float dense;
                output.positionCS = TransformWorldToHClip(G3SnowDisplaceWS(TransformObjectToWorld(input.positionOS.xyz), input.uv3, input.color, dense));
                output.normalWS = half3(TransformObjectToWorldNormal(input.normal));
            #if defined(_ALPHATEST_ON)
                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
            #endif
                return output;
            }

            void DepthNormalsFragment(Varyings input, out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #if defined(_ALPHATEST_ON)
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a - _Cutoff);
            #endif
            #if defined(LOD_FADE_CROSSFADE)
                LODFadeCrossFade(input.positionCS);
            #endif
            #if defined(_GBUFFER_NORMALS_OCT)
                float3 normalWS = normalize(input.normalWS);
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);
                outNormalWS = half4(packedNormalWS, 0.0);
            #else
                outNormalWS = half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            #endif
            #if defined(_WRITE_SMOOTHNESS)
                outNormalWS.a = _Smoothness;
            #endif
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
