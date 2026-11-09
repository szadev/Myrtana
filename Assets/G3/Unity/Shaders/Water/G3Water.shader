// Gothic 3 water for URP (rivers, sea, lakes).
// Recreates what the Genome eCShaderRiver / eCShaderOcean graphs describe (two scrolling G3_Special_Water_01_Normal_*
// maps, Fresnel, sky reflection, per-channel depth absorption "Depth*HalfLife", "ShoreFadingScale") with:
//  - two scrolling normal maps; river mode scrolls along the mesh UV (river meshes: V runs downstream),
//    sea / lake mode samples world XZ so neighbouring cells tile seamlessly
//  - Fresnel-weighted reflection of the reflection probe / skybox, blended with an analytic sky
//    (fog colour at the horizon, ambient-probe sky above) so it looks right even with a stale probe
//  - refraction through _CameraOpaqueTexture + Beer-Lambert absorption from _CameraDepthTexture
//    (URP asset: "Depth Texture" and "Opaque Texture" must be on — they are on PC_RPAsset)
//  - shoreline alpha fade, sun specular with main-light shadows, URP fog
//  - optional soft foam (_ShoreFoam, off by default: G3's water graphs have none, and the depth-based foam also
//    turned every body just under the surface white — the halo around a swimming hero)
//  - optional Gerstner swell (vertex displacement + per-pixel normal), used by the ocean materials
Shader "G3/Water"
{
    Properties
    {
        [Header(Colour)]
        _ScatterColor("Scatter colour (deep water)", Color) = (0.03, 0.11, 0.11, 1)
        _Absorption("Absorption per metre (RGB)", Vector) = (0.45, 0.16, 0.13, 0)
        _ShoreFade("Shore fade depth (m)", Float) = 0.6
        _FoamColor("Foam colour", Color) = (0.85, 0.88, 0.85, 1)
        _FoamWidth("Foam width (m)", Float) = 0.35
        _FoamAmount("Foam amount", Range(0, 1)) = 0.35
        [ToggleUI] _ShoreFoam("Foam at depth edges (not in G3; also whitens swimmers)", Float) = 0

        [Header(Normals)]
        [NoScaleOffset][Normal] _NormalA("Normal A", 2D) = "bump" {}
        [NoScaleOffset][Normal] _NormalB("Normal B", 2D) = "bump" {}
        _TilingA("Tiling A (UV scale, or metres per tile in world mode)", Float) = 1
        _TilingB("Tiling B", Float) = 2
        _FlowA("Flow A (xy dir, z speed)", Vector) = (0, -1, 0.2, 0)
        _FlowB("Flow B (xy dir, z speed)", Vector) = (0.15, -1, 0.1, 0)
        _NormalStrength("Normal strength", Range(0, 2)) = 0.6
        _DistanceFlatten("Flatten normals with distance (m)", Float) = 250
        [ToggleUI] _WorldUV("World-space UV (sea / lake)", Float) = 0

        [Header(Surface)]
        _Smoothness("Smoothness", Range(0, 1)) = 0.95
        _SpecularIntensity("Sun specular intensity", Range(0, 4)) = 1.5
        _ReflectionStrength("Reflection strength", Range(0, 1.5)) = 1
        _ReflectionTint("Reflection tint", Color) = (1, 1, 1, 1)
        _ProbeBlend("Reflection probe vs analytic sky", Range(0, 1)) = 0.35
        _RefractionStrength("Refraction strength", Range(0, 0.2)) = 0.04
        [Toggle(_G3WATER_SSR)] _SSR("Screen-space reflections", Float) = 1
        _SSRMaxDistance("SSR max distance (m)", Float) = 80

        [Header(Waves)]
        [Toggle(_G3WATER_WAVES)] _Waves("Gerstner swell (ocean)", Float) = 0
        _WaveAmplitude("Wave amplitude (m)", Float) = 0.25
        _WaveLength("Wave length (m)", Float) = 45
        _WaveSpeed("Wave speed", Float) = 1
        _WaveDirection("Wave direction (xz)", Vector) = (0.6, 0, 0.8, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardWater"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _G3WATER_WAVES
            #pragma shader_feature_local_fragment _G3WATER_SSR
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            // Transparent surface: sample cascades directly (no screen-space shadow texture).
            #define _SURFACE_TYPE_TRANSPARENT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

            TEXTURE2D(_NormalA); SAMPLER(sampler_NormalA);
            TEXTURE2D(_NormalB); SAMPLER(sampler_NormalB);

            CBUFFER_START(UnityPerMaterial)
                half4 _ScatterColor;
                float4 _Absorption;
                float _ShoreFade;
                half4 _FoamColor;
                float _FoamWidth;
                half _FoamAmount;
                half _ShoreFoam;
                float _TilingA, _TilingB;
                float4 _FlowA, _FlowB;
                half _NormalStrength;
                float _DistanceFlatten;
                float _WorldUV;
                half _Smoothness;
                half _SpecularIntensity;
                half _ReflectionStrength;
                half4 _ReflectionTint;
                half _ProbeBlend;
                half _RefractionStrength;
                float _SSRMaxDistance;
                float _WaveAmplitude, _WaveLength, _WaveSpeed;
                float4 _WaveDirection;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS    : TEXCOORD2;
                half4 tangentWS   : TEXCOORD3;
                half fogFactor    : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ---- Gerstner swell: 4 waves fanned around _WaveDirection ------------------------------------------------
            #if defined(_G3WATER_WAVES)
            void Gerstner(float2 xz, float t, out float3 offset, out float3 normal)
            {
                offset = 0;
                float3 n = float3(0, 1, 0);
                float2 baseDir = normalize(_WaveDirection.xz + 1e-5);
                const float angles[4] = { 0.0, 0.55, -0.7, 0.25 };
                const float lengths[4] = { 1.0, 0.61, 0.43, 0.27 };
                [unroll] for (int i = 0; i < 4; i++)
                {
                    float s, c; sincos(angles[i], s, c);
                    float2 d = float2(baseDir.x * c - baseDir.y * s, baseDir.x * s + baseDir.y * c);
                    float L = max(1.0, _WaveLength * lengths[i]);
                    float k = TWO_PI / L;
                    float A = _WaveAmplitude * lengths[i];
                    float w = sqrt(9.81 * k) * _WaveSpeed;
                    float f = k * dot(d, xz) - w * t + i * 1.7;
                    float sf, cf; sincos(f, sf, cf);
                    float Q = 0.6 / (k * A * 4 + 1e-4) ; // steepness, normalised so crests never loop
                    Q = min(Q, 1.0);
                    offset += float3(Q * A * d.x * cf, A * sf, Q * A * d.y * cf);
                    n += float3(-d.x * k * A * cf, -Q * k * A * sf, -d.y * k * A * cf);
                }
                normal = normalize(n);
            }
            #endif

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                #if defined(_G3WATER_WAVES)
                    float3 off, wn;
                    Gerstner(positionWS.xz, _Time.y, off, wn);
                    positionWS += off;
                #endif
                VertexNormalInputs ni = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                o.positionWS = positionWS;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = ni.normalWS;
                o.tangentWS = half4(ni.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                o.uv = input.uv;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half3 SampleNormalTS(TEXTURE2D_PARAM(tex, smp), float2 uv)
            {
                return UnpackNormal(SAMPLE_TEXTURE2D(tex, smp, uv));
            }

            // Sky as seen in direction r: horizon = fog colour, zenith = ambient probe sky.
            half3 AnalyticSky(half3 r)
            {
                half3 zenith = max(half3(0, 0, 0), SampleSH(half3(0, 1, 0))) * 1.6;
                half3 horizon = unity_FogColor.rgb;
                half t = pow(saturate(r.y), 0.45);
                return lerp(horizon, zenith, t);
            }

            #if defined(_G3WATER_SSR)
            // clip-space position (or direction, w = 0 input) -> screen UV for the camera depth / opaque textures
            float2 ClipToScreenUV(float4 cs)
            {
                float2 ndc = cs.xy / cs.w;
                return float2(ndc.x, ndc.y * _ProjectionParams.x) * 0.5 + 0.5;
            }

            bool OnScreen(float2 uv) { return all(uv > 0.0) && all(uv < 1.0); }

            // Cheap screen-space reflection against the camera depth / opaque textures (banks, trees, rocks).
            // Returns rgb + confidence in a.
            half4 TraceSSR(float3 originWS, half3 R, float jitter)
            {
                float stepLen = 0.6;
                float traveled = stepLen * (0.5 + jitter);
                float prevT = 0;
                [loop] for (int i = 0; i < 20; i++)
                {
                    float4 cs = TransformWorldToHClip(originWS + R * traveled);
                    if (cs.w <= 0) break;
                    float2 uv = ClipToScreenUV(cs);
                    if (!OnScreen(uv)) break;
                    float diff = cs.w - LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                    if (diff > 0)
                    {
                        // behind the depth buffer: accept if the surface is close to the ray (not a thin occluder far in front)
                        if (diff < max(1.0, (traveled - prevT) * 2.0))
                        {
                            float a = prevT, b = traveled;   // binary refinement
                            [unroll] for (int k = 0; k < 4; k++)
                            {
                                float m = 0.5 * (a + b);
                                float4 c2 = TransformWorldToHClip(originWS + R * m);
                                float2 uv2 = ClipToScreenUV(c2);
                                if (c2.w > LinearEyeDepth(SampleSceneDepth(uv2), _ZBufferParams)) { b = m; uv = uv2; } else a = m;
                            }
                            float2 edge = saturate(min(uv, 1.0 - uv) * 8.0);
                            half conf = edge.x * edge.y * saturate(1.5 - 1.5 * traveled / _SSRMaxDistance);
                            return half4(SampleSceneColor(uv), conf);
                        }
                        break;
                    }
                    prevT = traveled;
                    stepLen *= 1.35;
                    traveled += stepLen;
                    if (traveled > _SSRMaxDistance) break;
                }

                // Miss: distant scenery / sky in direction R looks (almost) the same from the water as from the camera,
                // so use what the camera sees in that direction. If something near covers it on screen, walk up in
                // elevation to the first far (sky) pixel: the sky changes slowly with elevation.
                const float lift[5] = { 0.0, 0.06, 0.15, 0.3, 0.55 };
                [unroll] for (int j = 0; j < 5; j++)
                {
                    float3 d = normalize(R + float3(0, lift[j], 0));
                    float4 cs = mul(GetWorldToHClipMatrix(), float4(d, 0));
                    if (cs.w <= 1e-4) break;
                    float2 uv = ClipToScreenUV(cs);
                    if (!OnScreen(uv)) break;
                    float farEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                    if (farEye > _SSRMaxDistance * 2.0)
                    {
                        float2 edge = saturate(min(uv, 1.0 - uv) * 6.0);
                        return half4(SampleSceneColor(uv), edge.x * edge.y * (1.0 - j * 0.12));
                    }
                }
                return half4(0, 0, 0, 0);
            }
            #endif

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 positionWS = input.positionWS;
                float3 camToPos = positionWS - GetCameraPositionWS();
                float viewDist = length(camToPos);
                half3 V = (half3)(-camToPos / max(viewDist, 1e-4));
                float t = _Time.y;

                // ---- normals -------------------------------------------------------------------------------------
                float2 baseUV = _WorldUV > 0.5 ? positionWS.xz : input.uv;
                float2 tileA = _WorldUV > 0.5 ? 1.0 / max(_TilingA, 0.01) : _TilingA;
                float2 tileB = _WorldUV > 0.5 ? 1.0 / max(_TilingB, 0.01) : _TilingB;
                // Genome eCTexCoordSrcScroller: uv += Direction * Rate * time
                float2 uvA = baseUV * tileA + _FlowA.xy * _FlowA.z * t;
                float2 uvB = baseUV * tileB + _FlowB.xy * _FlowB.z * t;
                half3 nA = SampleNormalTS(TEXTURE2D_ARGS(_NormalA, sampler_NormalA), uvA);
                half3 nB = SampleNormalTS(TEXTURE2D_ARGS(_NormalB, sampler_NormalB), uvB);
                half3 nTS = normalize(half3(nA.xy + nB.xy, nA.z * nB.z)); // whiteout blend
                half flatten = saturate(1.0 - viewDist / max(_DistanceFlatten, 1.0));
                half strength = _NormalStrength * (0.35 + 0.65 * flatten);
                nTS.xy *= strength;

                half3 geomN = normalize(input.normalWS);
                half3 T, B;
                if (_WorldUV > 0.5)
                {
                    // world XZ mapping: tangent = +X, bitangent = +Z
                    T = half3(1, 0, 0); B = half3(0, 0, 1);
                }
                else
                {
                    T = normalize(input.tangentWS.xyz);
                    B = cross(geomN, T) * input.tangentWS.w;
                }
                #if defined(_G3WATER_WAVES)
                    float3 off, waveN;
                    Gerstner(positionWS.xz, t, off, waveN);
                    geomN = normalize(lerp(geomN, (half3)waveN, 0.5 + 0.5 * flatten));
                    T = normalize(T - geomN * dot(T, geomN));
                    B = normalize(B - geomN * dot(B, geomN));
                #endif
                half3 N = normalize(T * nTS.x + B * nTS.y + geomN * max(nTS.z, 0.05));

                // ---- depth / thickness ---------------------------------------------------------------------------
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float surfaceEye = LinearEyeDepth(positionWS, GetWorldToViewMatrix());
                float rayScale = viewDist / max(surfaceEye, 1e-4);            // eye depth -> distance along the ray
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float thickness = max(0.0, sceneEye - surfaceEye) * rayScale;  // water travelled along the view ray
                float vDepth = thickness * saturate(abs(V.y) + 0.05);          // ~vertical depth below the surface

                // refraction: offset by the surface slope, reject samples of geometry in front of the water
                float2 refrOffset = (N.xz - geomN.xz) * _RefractionStrength * saturate(thickness * 0.5) / max(1.0, surfaceEye * 0.05);
                float2 refrUV = screenUV + refrOffset;
                float refrEye = LinearEyeDepth(SampleSceneDepth(refrUV), _ZBufferParams);
                if (refrEye < surfaceEye) { refrUV = screenUV; refrEye = sceneEye; }
                float refrThickness = max(0.0, refrEye - surfaceEye) * rayScale;
                half3 sceneColor = SampleSceneColor(refrUV);

                // ---- lighting ------------------------------------------------------------------------------------
                float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half shadow = mainLight.shadowAttenuation;
                half3 sunColor = mainLight.color * mainLight.distanceAttenuation;
                half3 ambient = max(half3(0, 0, 0), SampleSH(half3(0, 1, 0)));

                // water body: Beer-Lambert absorption + in-scattering lit by sky and sun
                half3 transmittance = exp(-_Absorption.rgb * refrThickness);
                half sunDown = saturate(mainLight.direction.y);
                half3 scatter = _ScatterColor.rgb * (ambient + sunColor * sunDown * lerp(0.35, 1.0, shadow));
                half3 water = sceneColor * transmittance + scatter * (1.0 - transmittance);

                // reflection
                half NdotV = saturate(dot(N, V));
                half fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);
                half3 R = reflect(-V, N);
                R.y = abs(R.y); // never reflect the ground below the horizon
                half perceptualRoughness = 1.0 - _Smoothness;
                half3 probe = GlossyEnvironmentReflection(R, positionWS, perceptualRoughness, 1.0h, screenUV);
                half3 sky = lerp(AnalyticSky(R), probe, _ProbeBlend) * _ReflectionTint.rgb;
                #if defined(_G3WATER_SSR)
                    // reflect off a calmer normal: full-strength ripples make the mirrored banks noisy
                    half3 Rs = reflect(-V, normalize(lerp(geomN, N, 0.5)));
                    Rs.y = max(Rs.y, 0.02);
                    float jitter = InterleavedGradientNoise(input.positionCS.xy, 0);
                    half4 ssr = TraceSSR(positionWS + geomN * 0.05, Rs, jitter);
                    sky = lerp(sky, ssr.rgb, ssr.a);
                #endif
                half3 reflection = sky * _ReflectionStrength;

                half3 color = lerp(water, reflection, fresnel);

                // sun glint (GGX-ish, narrow), blocked by shadows
                half3 H = normalize(mainLight.direction + V);
                half NdotH = saturate(dot(N, H));
                half rough = max(0.02, perceptualRoughness * perceptualRoughness);
                half a2 = rough * rough;
                half d = NdotH * NdotH * (a2 - 1.0) + 1.0;
                half D = a2 / (PI * d * d + 1e-5);
                half NdotL = saturate(dot(N, mainLight.direction));
                half spec = min(D * 0.25, 60.0) * NdotL * (0.1 + 0.9 * fresnel * 4.0);
                color += sunColor * spec * shadow * _SpecularIntensity;

                // shoreline foam (breaks up with the normal map pattern)
                half foamNoise = saturate((nA.x - nB.y) * 2.5 + 0.5);
                half foamMask = saturate(1.0 - vDepth / max(_FoamWidth, 0.01));
                half foam = saturate(foamMask * foamMask * (foamNoise + foamMask * 0.5) * _FoamAmount * 2.0) * _ShoreFoam;
                color = lerp(color, _FoamColor.rgb * (ambient + sunColor * NdotL * shadow), foam);

                // fade into the shore so the intersection is soft
                half alpha = saturate(vDepth / max(_ShoreFade, 0.01));
                color = MixFog(color, input.fogFactor);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
