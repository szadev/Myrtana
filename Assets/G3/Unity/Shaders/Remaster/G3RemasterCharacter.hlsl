// R1 character shaders (Docs/remaster-pipeline.md §9): shared input, surface and lighting for
// G3/Remaster/Armour, Skin, Hair and Eye. Each .shader defines one of G3_REMASTER_{ARMOUR,SKIN,HAIR,EYE} before
// including this file. All four share one UnityPerMaterial layout (SRP Batcher compatible) and use URP's own
// shadow / depth / depth-normals passes (via SurfaceInput.hlsl's _BaseMap/_Cutoff/Alpha()).
//
// Maps from Tools/Remaster/texpipe.py:
//   _BaseMap  sRGB albedo, alpha = cutout (hair cards)
//   _BumpMap  tangent-space normal
//   _MaskMap  R metallic, G occlusion, B skin weight, A smoothness
//   _DetailNormalMap  tileable skin pores (GameData/Remaster/Textures/_shared/SkinPores.png), weighted by skin
//
// Lighting: URP GGX for armour/metal/leather; skin = Lambert plus a wrapped terminator band in the albedo's own hue
// (_ScatterSaturation, x _ScatterColor; a cheap stand-in for subsurface scattering; the lit side keeps the original tone)
// + a soft, broad specular (F0 0.028, rough) + back-light transmission;
// colour fidelity to the G3 look (r1c): dielectric specular scaled down on dark albedos (_DarkSpecular: G3's albedos are
// dark and carry baked shading, F0 0.04 on them added a grey-blue sheen that made back/side views 3-7 % brighter), metals
// keep part of the albedo as diffuse (_MetalDiffuse) with F0 = brightened albedo (_MetalSpecBoost) - pure metalness turned
// the baked G3 metal albedos into black chrome / a dark gold crest; r1d: F0 hue limited (_MetalTint: luminance-based, the
// albedo's hue leaked into F0 and turned dark steel bronze), F0 capped (_MetalF0Max) with the Fresnel rim tied to it (dark
// iron became bright chrome), metal smoothness scaled down (_MetalSmoothness: worn plates); hair = Kajiya-Kay with two shifted
// lobes (Scheuermann) + wrap diffuse, dithered alpha test; eye = diffuse iris/sclera + a sharp cornea lobe with
// environment reflection. Ambient: SH + reflection probes, environment specular scaled by _EnvSpecular (G3 had none),
// desaturated by _EnvSaturation and weighted by smoothness² for non-metals (rough leather/cloth/skin must not mirror the sky).
// Forward+ (cluster light loop) and light layers are supported like URP Lit.
#ifndef G3_REMASTER_CHARACTER_INCLUDED
#define G3_REMASTER_CHARACTER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

TEXTURE2D(_MaskMap);          SAMPLER(sampler_MaskMap);
TEXTURE2D(_DetailNormalMap);  SAMPLER(sampler_DetailNormalMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _DetailNormalMap_ST;
    half4 _BaseColor;
    half4 _ScatterColor;
    half4 _HairSpecColor1;
    half4 _HairSpecColor2;
    half _Cutoff;
    half _BumpScale;
    half _Smoothness;
    half _Metallic;
    half _OcclusionStrength;
    half _EnvSpecular;
    half _EnvSaturation;
    half _Wrap;
    half _SkinMaskMin;
    half _Transmission;
    half _DetailNormalScale;
    half _SecondLobeSmoothness;
    half _HairShift1;
    half _HairShift2;
    half _HairExponent1;
    half _HairExponent2;
    half _StrandAxis;
    half _DitherRange;
    half _CorneaSmoothness;
    half _CorneaStrength;
    half _ScatterSaturation;
    half _DarkSpecular;
    half _MetalDiffuse;
    half _MetalSpecBoost;
    half _MetalTint;
    half _MetalF0Max;
    half _MetalSmoothness;
    // FACES v6 (in-engine fixes, RemasterLook.cs): neutral defaults 1 / 0 / 1 / 0.3 / 0.028 keep R1's look
    half _AlbedoGain;          // eye sclera / hair cap brightness (generated textures were tuned under Blender's key light)
    half _ShadowSampleOffset;  // eye: main-light shadow sampled this far (m) out along the eyeball normal (at the lids)
    half _SsaoStrength;        // share of SSAO kept (eye sockets: one occluded pit at the 0.3 m SSAO radius)
    half _SkinPrimaryLobe;     // skin: weight of the texture-driven GGX lobe (rest: the rough second lobe)
    half _SkinF0;              // skin reflectance at normal incidence
    // fix-52 (NPC heads matte/grey in shade): neutral 0 keeps R1's look
    half _SkinAmbientWarmth;   // skin: share of the SH ambient re-tinted to the skin's own hue (ambient light scatters too)
    half _SkinEnvSheen;        // skin: floor of the environment-specular weight on skin texels (soft forehead/nose sheen)
    UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END

#endif // G3_REMASTER_CHARACTER_INCLUDED

// ---------------------------------------------------------------------------------------------------------------------
// Forward pass (compiled only where G3_REMASTER_FORWARD is defined)
#if defined(G3_REMASTER_FORWARD) && !defined(G3_REMASTER_FORWARD_INCLUDED)
#define G3_REMASTER_FORWARD_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

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

Varyings RemasterVert(Attributes v)
{
    Varyings o = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
    VertexNormalInputs nrm = GetVertexNormalInputs(v.normalOS, v.tangentOS);
    o.positionCS = pos.positionCS;
    o.positionWS = pos.positionWS;
    o.normalWS = nrm.normalWS;
    o.tangentWS = half4(nrm.tangentWS, v.tangentOS.w * GetOddNegativeScale());
    o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
    o.fogFactor = ComputeFogFactor(pos.positionCS.z);
    return o;
}

struct RemasterSurface
{
    half3 albedo;
    half alpha;
    half metallic;
    half occlusion;
    half skin;
    half smoothness;
    half3 normalTS;
};

RemasterSurface SampleRemasterSurface(Varyings i)
{
    RemasterSurface s;
    half4 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor;
    half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, i.uv);
    s.albedo = saturate(base.rgb * _AlbedoGain);
    s.alpha = base.a;
    s.metallic = mask.r * _Metallic;
    s.occlusion = lerp(1.0h, mask.g, _OcclusionStrength);
    s.skin = max(mask.b, _SkinMaskMin);
    s.smoothness = saturate(mask.a * _Smoothness) * lerp(1.0h, _MetalSmoothness, s.metallic);
    // UnpackNormalScale directly: URP's SampleNormal() returns a flat normal unless the _NORMALMAP keyword is on.
    s.normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv), _BumpScale);
#if defined(G3_REMASTER_SKIN) || defined(G3_REMASTER_ARMOUR)
    // Pores: tiled detail normal, only where the skin mask itself says skin (whiteout blend; not raised by _SkinMaskMin,
    // which keeps hair/beard/painted mouth texels of a head atlas on the matte skin specular).
    half detailWeight = mask.b * _DetailNormalScale;
    if (detailWeight > 0.001h)
    {
        float2 duv = i.uv * _DetailNormalMap_ST.xy + _DetailNormalMap_ST.zw;
        half3 d = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, duv), detailWeight);
        s.normalTS = normalize(half3(s.normalTS.xy + d.xy, s.normalTS.z * d.z));
    }
#endif
    return s;
}

// ---- per-light terms -------------------------------------------------------------------------------------------------

// Skin diffuse: Lambert plus the wrap band (N·L + w) / (1 + w) − N·L tinted by _ScatterColor. The band is 0 on the lit
// side (N·L = 1: the original tone is kept) and on back faces (N·L < −w), at most w / (1 + w) at the terminator, so
// eyelid creases and silhouettes cannot turn red (the R1 per-channel wrap left only red there and greyed the lit side).
// r1c: the band's tint comes from the albedo's own hue (more saturated, same hue; _ScatterSaturation) instead of a fixed
// red: the fixed (1, .5, .38) tint pushed cheeks, eye sockets and chin to orange.
// r1d: the band is evaluated on a softened normal (NdotLBand: geometric normal blended with 25 % of the mapped one), as
// light diffusing under the skin ignores fine relief; on the mapped normal every steep normal-map ridge got a thin band
// line of its own (the orange "V" at the hero's lower-lip corner = the mouth-corner relief of G3's own normal map).
half3 SkinDiffuse(half NdotL, half NdotLBand, half skin, half3 albedo)
{
    half lambert = saturate(NdotL);
    half w = _Wrap;
    half band = saturate((NdotLBand + w) / (1.0h + w)) - saturate(NdotLBand);
    half3 hue = albedo / max(max(albedo.r, max(albedo.g, albedo.b)), 1e-3h);
    half3 tint = lerp(half3(1, 1, 1), hue, _ScatterSaturation) * _ScatterColor.rgb;
    return lambert + max(band, 0.0h) * tint * skin;
}

// Skin specular: low F0 (_SkinF0: 0.028 R1 skin, 0.04 FACES heads like the Blender renders) instead of 0.04 where the mask says skin and nothing is metal; half the grazing
// (Fresnel) reflection, which made R1's faces look oily.
#define kSkinF0 _SkinF0
void SkinSpecularF0(inout BRDFData brdf, half skin, half metallic)
{
    half k = skin * (1.0h - metallic);
    brdf.specular = lerp(brdf.specular, kSkinF0.xxx, k);
    brdf.reflectivity = lerp(brdf.reflectivity, kSkinF0, k);
    brdf.grazingTerm = saturate(1.0h - brdf.perceptualRoughness + brdf.reflectivity) * lerp(1.0h, 0.5h, k);
}

// Colour fidelity (r1c), after URP's InitializeBRDFData and the skin F0: dielectric specular x lerp(_DarkSpecular, 1,
// 4 x albedo luminance) (dark baked albedos get little sheen, mid tones full); metals keep _MetalDiffuse of the albedo as
// diffuse and reflect MetalF0 (r1d: luminance-based, hue limited, capped).
half3 MetalF0(half3 albedo)
{
    half lum = Luminance(albedo);
    half3 f0 = lerp(lum.xxx, albedo, _MetalTint) * _MetalSpecBoost;
    half peak = max(f0.r, max(f0.g, f0.b));
    return f0 * min(1.0h, _MetalF0Max / max(peak, 1e-4h)); // scaled, not clamped per channel: the hue stays
}

void FidelityBRDF(inout BRDFData brdf, half3 albedo, half metallic, half skin)
{
    half dark = lerp(_DarkSpecular, 1.0h, saturate(Luminance(albedo) * 4.0h));
    half dielectricF0 = lerp(kDielectricSpec.r, kSkinF0, skin * (1.0h - metallic)) * dark;
    half3 metalF0 = MetalF0(albedo);
    half metalReflectivity = max(metalF0.r, max(metalF0.g, metalF0.b));
    brdf.diffuse = albedo * lerp(kDielectricSpec.a, _MetalDiffuse, metallic);
    brdf.specular = lerp(dielectricF0.xxx, metalF0, metallic);
    brdf.reflectivity = lerp(dielectricF0, metalReflectivity, metallic);
    // Fresnel rim: dielectrics as before (scaled on dark albedos); metals at most 2 x their F0 (URP's smoothness +
    // reflectivity made every dark plate edge a white mirror of the environment)
    brdf.grazingTerm = lerp(brdf.grazingTerm * dark, min(brdf.grazingTerm, saturate(2.0h * metalReflectivity)), metallic);
}

// Kajiya-Kay strand highlight along tangent t (Scheuermann 2004).
half StrandSpecular(half3 t, half3 v, half3 l, half exponent)
{
    half3 h = SafeNormalize(l + v);
    half dotTH = dot(t, h);
    half sinTH = sqrt(max(0.0h, 1.0h - dotTH * dotTH));
    half dirAtten = smoothstep(-1.0h, 0.0h, dotTH);
    return dirAtten * pow(sinTH, exponent);
}

struct RemasterLightingInput
{
    BRDFData brdf;      // main lobe (GGX)
    BRDFData brdf2;     // skin: second rougher lobe; eye: cornea
    RemasterSurface s;
    half3 normalWS;
    half3 geomNormalWS;
    half3 viewWS;
    half3 strandT1;
    half3 strandT2;
};

half3 RemasterDirect(RemasterLightingInput li, Light light)
{
    half3 L = light.direction;
    half3 N = li.normalWS;
    half3 V = li.viewWS;
    half NdotL = dot(N, L);
    half shadowed = light.distanceAttenuation * light.shadowAttenuation;
    half3 radiance = light.color * shadowed;
    half3 color = 0;

#if defined(G3_REMASTER_HAIR)
    half wrapD = saturate((NdotL + 0.35h) / 1.35h);
    color = li.brdf.diffuse * wrapD;
    half spec1 = StrandSpecular(li.strandT1, V, L, _HairExponent1);
    half spec2 = StrandSpecular(li.strandT2, V, L, _HairExponent2);
    color += (_HairSpecColor1.rgb * spec1 + _HairSpecColor2.rgb * li.s.albedo * spec2) * saturate(NdotL * 2.0h + 0.2h);
    return color * radiance;
#elif defined(G3_REMASTER_EYE)
    half lambert = saturate(NdotL);
    color = li.brdf.diffuse * lambert + li.brdf.specular * DirectBRDFSpecular(li.brdf, N, L, V) * lambert;
    // Cornea: smooth dielectric lobe on the geometric eyeball normal (the iris is under it).
    half3 Ng = li.geomNormalWS;
    half cornea = DirectBRDFSpecular(li.brdf2, Ng, L, V) * kDielectricSpec.r * _CorneaStrength;
    color += cornea * saturate(dot(Ng, L));
    return color * radiance;
#else
    half3 Nb = normalize(lerp(li.geomNormalWS, N, 0.25h));
    half3 diffuseTerm = SkinDiffuse(NdotL, dot(Nb, L), li.s.skin, li.s.albedo);
    half lambert = saturate(NdotL);
    half spec = DirectBRDFSpecular(li.brdf, N, L, V);
    // Skin: the broad lobe (_SecondLobeSmoothness, rough) mixed with _SkinPrimaryLobe of the texture-driven one (R1 0.3: matte, no oily
    // sheen; FACES heads 0.75 like Blender's single lobe); clamped so a grazing key light cannot burn a white streak into the face.
    half spec2 = DirectBRDFSpecular(li.brdf2, N, L, V);
    spec = lerp(spec, min(_SkinPrimaryLobe * spec + (1.0h - _SkinPrimaryLobe) * spec2, 4.0h), li.s.skin);
    color = li.brdf.diffuse * diffuseTerm + li.brdf.specular * spec * lambert;
    color *= radiance;
    // Thin-part transmission (ears, fingers, nose) for light from behind, only on the side facing away from the light
    // (no rim on lit silhouettes), tinted half-way to _ScatterColor and clamped.
    half3 Lt = normalize(L + N * 0.3h);
    half trans = pow(saturate(dot(V, -Lt)), 4.0h) * saturate(-NdotL * 2.0h) * _Transmission * li.s.skin;
    half3 transTint = lerp(half3(1, 1, 1), _ScatterColor.rgb, 0.5h);
    color += li.brdf.diffuse * transTint * min(trans, 0.5h) * light.color * light.distanceAttenuation * lerp(0.3h, 1.0h, light.shadowAttenuation);
    return color;
#endif
}

half4 RemasterFrag(Varyings i, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    RemasterSurface s = SampleRemasterSurface(i);

#if defined(_ALPHATEST_ON)
    // Dithered cutout: soft hair-card edges without sorting (TAA/MSAA resolve the dither).
    half dither = (InterleavedGradientNoise(i.positionCS.xy, 0) - 0.5h) * _DitherRange;
    clip(s.alpha - _Cutoff - dither);
#endif

    half3 geomN = normalize(i.normalWS);
#if defined(G3_REMASTER_HAIR)
    geomN *= IS_FRONT_VFACE(facing, 1.0h, -1.0h); // double-sided cards
#endif
    half3 bitangent = i.tangentWS.w * cross(geomN, i.tangentWS.xyz);
    half3x3 tbn = half3x3(i.tangentWS.xyz, bitangent, geomN);
    half3 N = normalize(TransformTangentToWorld(s.normalTS, tbn));

    InputData input = (InputData)0;
    input.positionWS = i.positionWS;
    input.positionCS = i.positionCS;
    input.normalWS = N;
    input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
    // FACES v6: the eyeball samples the shadow at its lids (_ShadowSampleOffset along the eyeball normal; 0 elsewhere)
    input.shadowCoord = TransformWorldToShadowCoord(i.positionWS + geomN * _ShadowSampleOffset);
    input.fogCoord = i.fogFactor;
    input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
    input.bakedGI = SampleSH(N);
    input.shadowMask = half4(1, 1, 1, 1);
    input.tangentToWorld = tbn;

    RemasterLightingInput li;
    li.s = s;
    li.normalWS = N;
    li.geomNormalWS = geomN;
    li.viewWS = input.viewDirectionWS;
    half alpha = 1;
#if defined(G3_REMASTER_HAIR)
    InitializeBRDFData(s.albedo, 0, half3(0, 0, 0), s.smoothness, alpha, li.brdf);
#else
    InitializeBRDFData(s.albedo, s.metallic, half3(0, 0, 0), s.smoothness, alpha, li.brdf);
#endif
#if defined(G3_REMASTER_EYE)
    InitializeBRDFData(half3(0, 0, 0), 0, half3(0, 0, 0), _CorneaSmoothness, alpha, li.brdf2);
#else
    InitializeBRDFData(s.albedo, s.metallic, half3(0, 0, 0), min(s.smoothness, _SecondLobeSmoothness), alpha, li.brdf2);
#endif
#if defined(G3_REMASTER_SKIN) || defined(G3_REMASTER_ARMOUR)
    SkinSpecularF0(li.brdf, s.skin, s.metallic);
    SkinSpecularF0(li.brdf2, s.skin, s.metallic);
    FidelityBRDF(li.brdf, s.albedo, s.metallic, s.skin);
    FidelityBRDF(li.brdf2, s.albedo, s.metallic, s.skin);
#endif
    // Hair strand tangents: along U (tangent) or V (bitangent), shifted along the normal; per-texel jitter from albedo.
    half3 strand = _StrandAxis > 0.5h ? bitangent : i.tangentWS.xyz;
    half jitter = (dot(s.albedo, half3(0.3h, 0.59h, 0.11h)) - 0.25h) * 0.6h;
    li.strandT1 = SafeNormalize(strand + (_HairShift1 + jitter) * N);
    li.strandT2 = SafeNormalize(strand + (_HairShift2 + jitter) * N);

    SurfaceData sd = (SurfaceData)0;
    sd.albedo = s.albedo;
    sd.occlusion = s.occlusion;
    sd.alpha = 1;
    AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(input, sd);
    ao.indirectAmbientOcclusion = lerp(1.0h, ao.indirectAmbientOcclusion, _SsaoStrength);
    ao.directAmbientOcclusion = lerp(1.0h, ao.directAmbientOcclusion, _SsaoStrength);
    uint meshRenderingLayers = GetMeshRenderingLayer();
    half4 shadowMask = CalculateShadowMask(input);

    Light mainLight = GetMainLight(input, shadowMask, ao);
    half3 bakedGI = input.bakedGI;
    MixRealtimeAndBakedGI(mainLight, N, bakedGI);
#if defined(G3_REMASTER_SKIN)
    // fix-52: in shade a face gets only the SH ambient (blue-grey sky): untinted it greys the skin, while the lit side
    // has the warm wrap band. Part of the ambient takes the albedo's own hue (as the band does), luminance kept.
    {
        half3 hue = s.albedo / max(max(s.albedo.r, max(s.albedo.g, s.albedo.b)), 1e-3h);
        half3 warm = lerp(half3(1, 1, 1), hue, _ScatterSaturation);
        warm /= max(Luminance(warm), 1e-3h);
        bakedGI = lerp(bakedGI, Luminance(bakedGI) * warm, saturate(_SkinAmbientWarmth * s.skin));
    }
#endif

    // Ambient: SH diffuse (wrapped for skin: SH is already soft) + reflection probes, specular scaled by _EnvSpecular.
    half NoV = saturate(dot(N, li.viewWS));
    half fresnel = Pow4(1.0h - NoV);
    half3 reflectVector = reflect(-li.viewWS, N);
    half3 envSpec = GlossyEnvironmentReflection(reflectVector, i.positionWS, li.brdf.perceptualRoughness, 1.0h, input.normalizedScreenSpaceUV);
    // Colour fidelity: the sky/probe colour must not tint the G3 albedo (blue leather in R1): partly desaturated, and
    // rough non-metals (cloth ~0.12, leather ~0.22, skin ~0.3 smoothness) get smoothness² of it; metals keep all of it.
    envSpec = lerp(Luminance(envSpec).xxx, envSpec, _EnvSaturation);
    half envWeight = _EnvSpecular * lerp(s.smoothness * s.smoothness, 1.0h, s.metallic);
#if defined(G3_REMASTER_SKIN)
    envWeight = max(envWeight, _EnvSpecular * _SkinEnvSheen * s.skin * (1.0h - s.metallic)); // fix-52: FACES skin sheen in shade
#endif
    half3 color = bakedGI * li.brdf.diffuse + envWeight * envSpec * EnvironmentBRDFSpecular(li.brdf, fresnel);
#if defined(G3_REMASTER_EYE)
    half3 rg = reflect(-li.viewWS, geomN);
    half3 corneaEnv = GlossyEnvironmentReflection(rg, i.positionWS, li.brdf2.perceptualRoughness, 1.0h, input.normalizedScreenSpaceUV);
    // r1c: desaturated and scaled by _EnvSpecular like the other materials (the full sky/probe colour on a dark iris made
    // brown eyes look blue-grey and glassy); the sharp direct catch light above stays.
    corneaEnv = lerp(Luminance(corneaEnv).xxx, corneaEnv, _EnvSaturation);
    color += corneaEnv * (kDielectricSpec.r + (1 - kDielectricSpec.r) * Pow4(1.0h - saturate(dot(geomN, li.viewWS)))) * _CorneaStrength * _EnvSpecular;
#endif
    color *= ao.indirectAmbientOcclusion * s.occlusion;

#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
        color += RemasterDirect(li, mainLight);

#if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();
    InputData inputData = input; // name used by URP's LIGHT_LOOP_BEGIN
#if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
            color += RemasterDirect(li, light);
    }
#endif
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
            color += RemasterDirect(li, light);
    LIGHT_LOOP_END
#endif

    color = MixFog(color, input.fogCoord);
    return half4(min(color, HALF_MAX), 1);
}

#endif // G3_REMASTER_FORWARD
