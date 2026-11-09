// v2 hero showcase character shaders (Docs/remaster-characters.md "v2 hero showcase"): G3/Remaster/V2/Skin, Eye, Hair
// (also stubble shells). Each .shader defines one of G3_V2_SKIN / G3_V2_EYE / G3_V2_HAIR before including this file.
// One UnityPerMaterial layout for all three (SRP Batcher compatible); URP's own shadow / depth / depth-normals passes
// (SurfaceInput.hlsl's _BaseMap/_BaseColor/_Cutoff). Parameters: G3.Formats.Remaster.RemasterV2Look.
//
// Skin  (maps from herov2tex.py: BaseMap sRGB albedo; BumpMap tangent normal incl. pores; MaskMap R thickness (1 = thin),
//        G cavity / AO, B curvature as LUT v, A smoothness)
//   diffuse   pre-integrated scattering (Penner 2011): LUT(N·L, curvature) from RemasterSkinLut (d'Eon's 6-Gaussian skin
//             profile), evaluated per colour channel on normals softened towards the geometric one (red most), with a
//             red-shifted shadow penumbra; ambient SH likewise per channel
//   specular  two GGX lobes (0.75x and 1.3x the roughness, 15 % broad), F0 0.028, specular occlusion by the cavity map
//   transmission  thin parts (ears, nostrils) by the thickness map: view-dependent back light (Barré-Brisebois 2011)
// Eye   one eyeball with a cornea bulge; planar front UV (centre = pupil, radius 0.5 = eyeball radius). The view ray is
//       refracted at the cornea (IOR) and the iris texture is read where it meets a virtual iris plane _IrisDepth behind
//       the apex (parallax refraction); concave iris normal (light enters on the far side), sharp cornea lobe + probe on
//       the geometric normal, soft sclera wrap. Lid shadow / AO = the separate occlusion shell (G3RemasterV2EyeOcclusion).
//       hero-v4 "alive eyes": a view-anchored catch light on the cornea (the bright sky / surroundings behind the camera,
//       the cinematic "eye light": its intensity is the ambient SH seen in that direction, so it never glows in the dark),
//       the probe reflection on the cornea at an artistic F0, ambient iris bowl AO (iris sits in a cup under the cornea)
//       and the limbal ring at the measured iris radius (eye_v2.json).
// Hair  Marschner R / TT / TRT in Karis' real-time form (SIGGRAPH 2016 "Physically Based Hair Shading in Unreal") +
//       wrapped Kajiya-Kay multiple-scatter diffuse; strands along V (or U); double-sided. Two passes: opaque cards above
//       _Cutoff with depth write, then the soft fringe (_FringeCutoff.._Cutoff) blended without depth write.
#ifndef G3_REMASTER_V2_INCLUDED
#define G3_REMASTER_V2_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

TEXTURE2D(_MaskMap);          SAMPLER(sampler_MaskMap);
TEXTURE2D(_DetailNormalMap);  SAMPLER(sampler_DetailNormalMap);
TEXTURE2D(_SkinLut);          SAMPLER(sampler_SkinLut);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _DetailNormalMap_ST;
    half4 _BaseColor;
    half4 _TransmissionColor;
    float _CurvatureScale;
    float _IrisDepth;
    float _EyeRadius;
    half _Cutoff;
    half _FringeCutoff;
    half _BumpScale;
    half _Smoothness;
    half _OcclusionStrength;
    half _DetailNormalScale;
    half _Lobe1Roughness;
    half _Lobe2Roughness;
    half _LobeMix;
    half _SkinF0;
    half _CavitySpecular;
    half _BakedCurvature;
    half _NormalBlurR;
    half _NormalBlurG;
    half _NormalBlurB;
    half _TransmissionStrength;
    half _TransmissionDistortion;
    half _TransmissionPower;
    half _EnvSpecular;
    half _EnvSaturation;
    half _RimStrength;
    half _SsaoStrength;
    half _AlbedoGain;
    half _ShadowSampleOffset;
    half _IrisRadius;
    half _CorneaRadius;
    half _IOR;
    half _CorneaSmoothness;
    half _CorneaF0;
    half _ScleraSmoothness;
    half _IrisConcavity;
    half _LimbusDark;
    half _HairRoughness;
    half _HairShift;
    half _HairSpecular;
    half _HairScatter;
    half _HairBacklit;
    half _StrandAxis;
    half _DitherRange;
    half _OcclusionPower;
    half _HairSpecCap;
    half _HairShell;
    half _CatchlightStrength;
    half _CatchlightSmoothness;
    half _CatchlightUp;
    half _CatchlightSide;
    half _IrisAO;
    half _IrisGain;
    half _ScleraGain;
    half _CaruncleGain;
    UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END

#endif // G3_REMASTER_V2_INCLUDED

// ---------------------------------------------------------------------------------------------------------------------
// Forward passes (compiled only where G3_V2_FORWARD is defined; G3_V2_FRINGE = the hair's blended second pass)
#if defined(G3_V2_FORWARD) && !defined(G3_V2_FORWARD_INCLUDED)
#define G3_V2_FORWARD_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
    float2 uv         : TEXCOORD0;   // mesh UV (untransformed: the eye refracts in mesh UV space)
    float3 positionWS : TEXCOORD1;
    half3 normalWS    : TEXCOORD2;
    half4 tangentWS   : TEXCOORD3;
    half fogFactor    : TEXCOORD4;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings V2Vert(Attributes v)
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
    o.uv = v.uv;
    o.fogFactor = ComputeFogFactor(pos.positionCS.z);
    return o;
}

half3 V2Desaturate(half3 c, half s) { return lerp(Luminance(c).xxx, c, s); }

// Ambient rim: the sky seen at grazing angles (silhouettes separate from G3's dark backgrounds); _RimStrength 0 = off.
half3 V2AmbientRim(half3 N, half3 V, half occlusion)
{
    half f = Pow4(1.0h - saturate(dot(N, V)));
    return SampleSH(N) * (f * _RimStrength * occlusion);
}

// ---- skin ------------------------------------------------------------------------------------------------------------
#if defined(G3_V2_SKIN)
half3 SkinLut(half nl, half v)
{
    half3 s = SAMPLE_TEXTURE2D_LOD(_SkinLut, sampler_SkinLut, float2(nl * 0.5h + 0.5h, v), 0).rgb;
    return s * s; // stored as sqrt (RemasterSkinLut.ToRgba)
}

struct SkinCtx
{
    half3 albedo;
    half3 N, Ng, Nr, Ngr, Nb;   // mapped, geometric, per-channel softened diffuse normals
    half3 V;
    half thin, cavity, curvV, specOcc;
    BRDFData lobe1, lobe2;
};

half3 SkinDirect(SkinCtx c, Light light)
{
    half3 L = light.direction;
    half shadow = light.shadowAttenuation;
    half atten = light.distanceAttenuation;
    // diffuse: pre-integrated per channel; the penumbra shifts to red (light scattered from the lit side)
    half3 d = half3(SkinLut(dot(c.Nr, L), c.curvV).r, SkinLut(dot(c.Ngr, L), c.curvV).g, SkinLut(dot(c.Nb, L), c.curvV).b);
    half3 sh = half3(pow(max(shadow, 1e-4h), 0.55h), pow(max(shadow, 1e-4h), 0.9h), shadow);
    half3 color = c.albedo * d * sh;
    // specular: two lobes
    half nl = saturate(dot(c.N, L));
    half s1 = DirectBRDFSpecular(c.lobe1, c.N, L, c.V);
    half s2 = DirectBRDFSpecular(c.lobe2, c.N, L, c.V);
    color += c.lobe1.specular * (lerp(s1, s2, _LobeMix) * nl * shadow * c.specOcc);
    color *= light.color * atten;
    // transmission through thin parts
    half3 Lt = normalize(L + c.Ng * _TransmissionDistortion);
    half fwd = pow(saturate(dot(c.V, -Lt)), _TransmissionPower);
    half back = saturate(-dot(c.Ng, L));
    half trans = (fwd + 0.25h * back) * c.thin * _TransmissionStrength;
    color += c.albedo * _TransmissionColor.rgb * trans * light.color * atten * lerp(0.3h, 1.0h, shadow);
    return color;
}
#endif

// ---- eye -------------------------------------------------------------------------------------------------------------
#if defined(G3_V2_EYE)
struct EyeCtx
{
    half3 albedo;
    half3 N;          // sclera: mapped normal
    half3 Ng;         // cornea / eyeball surface
    half3 Niris;      // concave iris
    half3 V;
    half cornea;      // 1 inside the cornea
    half iris;        // 1 on the iris (refracted)
    BRDFData corneaB, scleraB;
};

half3 EyeDirect(EyeCtx c, Light light)
{
    half3 L = light.direction;
    half3 rad = light.color * (light.distanceAttenuation * light.shadowAttenuation);
    half scleraD = saturate((dot(c.N, L) + 0.3h) / 1.3h);
    half irisD = saturate(dot(c.Niris, L));
    half3 color = c.albedo * lerp(scleraD, irisD, c.iris);
    half nlg = saturate(dot(c.Ng, L));
    color += c.scleraB.specular * DirectBRDFSpecular(c.scleraB, c.N, L, c.V) * saturate(dot(c.N, L)) * (1.0h - c.cornea);
    color += _CorneaF0 * DirectBRDFSpecular(c.corneaB, c.Ng, L, c.V) * nlg * c.cornea;
    return color * rad;
}
#endif

// ---- hair ------------------------------------------------------------------------------------------------------------
#if defined(G3_V2_HAIR)
half HairG(half B, half theta) { return exp(-0.5h * theta * theta / (B * B)) / (sqrt(2.0h * PI) * B); }
half HairF(half cosTheta)
{
    const half n = 1.55h;
    const half F0 = (1.0h - n) * (1.0h - n) / ((1.0h + n) * (1.0h + n));
    return F0 + (1.0h - F0) * Pow4(1.0h - cosTheta) * (1.0h - cosTheta);
}

// Karis 2016: T = strand direction, V/L unit vectors towards the eye / light; returns radiance per unit light.
half3 HairBsdf(half3 T, half3 V, half3 L, half3 albedo, half shadow)
{
    half VoL = dot(V, L);
    half sinL = clamp(dot(T, L), -1.0h, 1.0h);
    half sinV = clamp(dot(T, V), -1.0h, 1.0h);
    half cosThetaD = cos(0.5h * abs(asin(sinV) - asin(sinL)));
    half3 Lp = L - sinL * T;
    half3 Vp = V - sinV * T;
    half cosPhi = dot(Lp, Vp) * rsqrt(dot(Lp, Lp) * dot(Vp, Vp) + 1e-4h);
    half cosHalfPhi = sqrt(saturate(0.5h + 0.5h * cosPhi));
    half r2 = _HairRoughness * _HairRoughness;
    half3 S = 0;
    // R
    {
        half alpha = -2.0h * _HairShift;
        half Mp = HairG(r2 * sqrt(2.0h) * cosHalfPhi + 1e-3h, sinL + sinV - alpha);
        half Np = 0.25h * cosHalfPhi;
        half Fp = HairF(sqrt(saturate(0.5h + 0.5h * VoL)));
        S += (Mp * Np * Fp * _HairSpecular * 2.0h) * lerp(1.0h, _HairBacklit, saturate(-VoL));
    }
    // TT
    {
        half Mp = HairG(r2 * 0.5h + 1e-3h, sinL + sinV - _HairShift);
        half nPrime = 1.19h / cosThetaD + 0.36h * cosThetaD;
        half a = 1.0h / nPrime;
        half h = cosHalfPhi * (1.0h + a * (0.6h - 0.8h * cosPhi));
        half f = HairF(cosThetaD * sqrt(saturate(1.0h - h * h)));
        half Fp = (1.0h - f) * (1.0h - f);
        half3 Tp = pow(max(albedo, 1e-4h), 0.5h * sqrt(saturate(1.0h - (h * a) * (h * a))) / cosThetaD);
        half Np = exp(-3.65h * cosPhi - 3.98h);
        S += Mp * Np * Fp * Tp * _HairBacklit;
    }
    // TRT
    {
        half Mp = HairG(r2 * 2.0h + 1e-3h, sinL + sinV - 4.0h * _HairShift);
        half f = HairF(cosThetaD * 0.5h);
        half Fp = (1.0h - f) * (1.0h - f) * f;
        half3 Tp = pow(max(albedo, 1e-4h), 0.8h / cosThetaD);
        half Np = exp(17.0h * cosPhi - 16.78h);
        S += Mp * Np * Fp * Tp;
    }
    // owner (in game): hair read white-grey / furry against the sun from behind -> the lobes are capped (the narrow
    // R/TT peaks of dark, thin cards blew out under G3's strong sun), the diffuse is plain albedo (UE's sqrt(albedo)
    // lifted dark brown towards grey)
    S = min(S, half3(_HairSpecCap, _HairSpecCap, _HairSpecCap));
    // multiple scattering: wrapped Kajiya-Kay diffuse on a normal facing the viewer around the strand
    {
        half kajiya = 1.0h - abs(sinL);
        half3 Nf = SafeNormalize(V - T * sinV);
        half wrapNL = saturate((dot(Nf, L) + 1.0h) / 4.0h);
        half scatter = (1.0h / PI) * lerp(wrapNL, kajiya, 0.33h) * _HairScatter;
        half luma = max(Luminance(albedo), 1e-4h);
        half3 tint = pow(max(albedo / luma, 1e-4h), 1.0h - shadow);
        S += max(albedo, 0) * scatter * tint;
    }
    return S * PI; // URP lights carry no 1/pi (Lambert = albedo x N·L)
}

// hero-v3 hair shell (helmet in the skin layout, painted strands + normal map): the strand tangent follows the packed
// UV islands, not the hair, so no anisotropic lobes - a matte wrapped Lambert on the normal-mapped surface and a faint
// broad sheen (owner: v2 hair 'shiny')
half3 HairShellBsdf(half3 N, half3 V, half3 L, half3 albedo)
{
    half nl = saturate((dot(N, L) + 0.2h) / 1.2h);
    half3 H = SafeNormalize(L + V);
    half spec = pow(saturate(dot(N, H)), 10.0h) * 0.04h * _HairSpecular * saturate(dot(N, L));
    return albedo * nl + spec;
}

half3 HairDirect(half3 strand, half3 N, half3 V, half3 L, half3 albedo, half shadow)
{
    return _HairShell > 0.5h ? HairShellBsdf(N, V, L, albedo) : HairBsdf(strand, V, L, albedo, shadow);
}
#endif

// ---------------------------------------------------------------------------------------------------------------------

half4 V2Frag(Varyings i, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    float2 uv = TRANSFORM_TEX(i.uv, _BaseMap);

    half3 geomN = normalize(i.normalWS);
#if defined(G3_V2_HAIR)
    geomN *= IS_FRONT_VFACE(facing, 1.0h, -1.0h); // double-sided cards
#endif
    half3 tangent = normalize(i.tangentWS.xyz);
    half3 bitangent = i.tangentWS.w * cross(geomN, tangent);
    half3x3 tbn = half3x3(tangent, bitangent, geomN);
    half3 V = GetWorldSpaceNormalizeViewDir(i.positionWS);

    half4 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
    half alphaOut = 1;
#if defined(G3_V2_HAIR)
  #if defined(G3_V2_FRINGE)
    clip(base.a - _FringeCutoff);
    clip(_Cutoff - base.a - 1e-3h);
    alphaOut = saturate((base.a - _FringeCutoff) / max(_Cutoff - _FringeCutoff, 1e-3h));
  #else
    clip(base.a - _Cutoff);
  #endif
#endif
    half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv);
    half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _BumpScale);
#if defined(G3_V2_SKIN)
    if (_DetailNormalScale > 0.001h)
    {
        float2 duv = i.uv * _DetailNormalMap_ST.xy + _DetailNormalMap_ST.zw;
        half3 dn = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, duv), _DetailNormalScale);
        normalTS = normalize(half3(normalTS.xy + dn.xy, normalTS.z * dn.z));
    }
#endif
    half3 N = normalize(TransformTangentToWorld(normalTS, tbn));

    InputData input = (InputData)0;
    input.positionWS = i.positionWS;
    input.positionCS = i.positionCS;
    input.normalWS = N;
    input.viewDirectionWS = V;
    input.shadowCoord = TransformWorldToShadowCoord(i.positionWS + geomN * _ShadowSampleOffset);
    input.fogCoord = i.fogFactor;
    input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
    input.bakedGI = SampleSH(N);
    input.shadowMask = half4(1, 1, 1, 1);
    input.tangentToWorld = tbn;

    SurfaceData sd = (SurfaceData)0;
    sd.albedo = base.rgb;
    sd.occlusion = 1;
    sd.alpha = 1;
    AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(input, sd);
    ao.indirectAmbientOcclusion = lerp(1.0h, ao.indirectAmbientOcclusion, _SsaoStrength);
    ao.directAmbientOcclusion = lerp(1.0h, ao.directAmbientOcclusion, _SsaoStrength);
    uint meshRenderingLayers = GetMeshRenderingLayer();
    half4 shadowMask = CalculateShadowMask(input);
    Light mainLight = GetMainLight(input, shadowMask, ao);
    half3 color = 0;
    half alphaUnused = 1;

#if defined(G3_V2_SKIN)
    SkinCtx c;
    c.albedo = saturate(base.rgb * _AlbedoGain);
    c.N = N;
    c.Ng = geomN;
    c.Nr = normalize(lerp(N, geomN, _NormalBlurR));
    c.Ngr = normalize(lerp(N, geomN, _NormalBlurG));
    c.Nb = normalize(lerp(N, geomN, _NormalBlurB));
    c.V = V;
    c.thin = mask.r;
    c.cavity = lerp(1.0h, mask.g, _OcclusionStrength);
    c.specOcc = lerp(1.0h, mask.g, _CavitySpecular);
    // curvature: baked (MaskMap B = LUT v) or from screen-space derivatives of the geometric normal
    float3 dN = fwidth(geomN);
    float3 dP = fwidth(i.positionWS);
    float curvSS = length(dN) / max(length(dP), 1e-6);
    half vSS = sqrt(saturate(curvSS * _CurvatureScale));
    c.curvV = lerp(vSS, mask.b, _BakedCurvature);
    half smooth = saturate(mask.a * _Smoothness);
    half rough = 1.0h - smooth;
    InitializeBRDFData(c.albedo, 0, half3(0, 0, 0), 1.0h - saturate(rough * _Lobe1Roughness), alphaUnused, c.lobe1);
    InitializeBRDFData(c.albedo, 0, half3(0, 0, 0), 1.0h - saturate(rough * _Lobe2Roughness), alphaUnused, c.lobe2);
    c.lobe1.specular = _SkinF0.xxx;
    c.lobe1.reflectivity = _SkinF0;
    c.lobe1.diffuse = c.albedo * (1.0h - _SkinF0);
    // ambient: SH per channel on the softened normals, probe reflection on the sharp lobe
    half3 gi = half3(SampleSH(c.Nr).r, SampleSH(c.Ngr).g, SampleSH(c.Nb).b);
    color = gi * c.lobe1.diffuse * c.cavity;
    half NoV = saturate(dot(N, V));
    half3 env = GlossyEnvironmentReflection(reflect(-V, N), i.positionWS, c.lobe1.perceptualRoughness, 1.0h, input.normalizedScreenSpaceUV);
    c.lobe1.grazingTerm = saturate(smooth + _SkinF0) * 0.5h; // skin: half the grazing reflection (no oily rim)
    color += V2Desaturate(env, _EnvSaturation) * EnvironmentBRDFSpecular(c.lobe1, Pow4(1.0h - NoV)) * (_EnvSpecular * c.specOcc);
    color += V2AmbientRim(geomN, V, c.cavity) * c.albedo;
    color *= ao.indirectAmbientOcclusion;
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
        color += SkinDirect(c, mainLight);
    #define G3_V2_DIRECT(light) SkinDirect(c, light)

#elif defined(G3_V2_EYE)
    EyeCtx c;
    float2 p = (i.uv - 0.5) * 2.0;                 // eyeball radii in the eye's front plane
    float r = length(p);
    half3 E = normalize(geomN - p.x * tangent - p.y * bitangent);   // eye axis (front)
    c.cornea = smoothstep(_CorneaRadius + 0.04h, _CorneaRadius - 0.01h, r);
    float3 refr = refract(-V, geomN, 1.0 / _IOR);
    float hgt = _IrisDepth * saturate(1.0 - (r * r) / (_CorneaRadius * _CorneaRadius));
    float t = hgt / max(dot(-refr, E), 0.2);
    float3 off = refr * t;
    float2 duv = float2(dot(off, tangent), dot(off, bitangent)) / (2.0 * _EyeRadius);
    float2 uvIris = i.uv + duv * c.cornea;
    half4 irisTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TRANSFORM_TEX(uvIris, _BaseMap)) * _BaseColor;
    float2 pi_ = (uvIris - 0.5) * 2.0;
    float ri = length(pi_);
    c.iris = smoothstep(_IrisRadius + 0.03h, _IrisRadius - 0.01h, ri) * c.cornea;
    // hero-v4: iris / sclera balance (the dim v3 sclera and dark iris read as one flat grey-green), caruncle patch
    // (outside the eyeball disc, r > 1) toned down: it read as bright pink plastic in the inner corners
    half3 albedo = lerp(base.rgb, irisTex.rgb, c.cornea) * lerp(_ScleraGain, _IrisGain, c.iris);
    albedo *= lerp(1.0h, _CaruncleGain, saturate((r - 1.0h) / 0.03h));
    // limbal ring: dark from just inside the iris edge to just past the cornea edge (the sclera seen through the cornea
    // rim read as a light ring around the iris)
    half limb = smoothstep(_IrisRadius - 0.05h, _IrisRadius, ri) * (1.0h - smoothstep(_CorneaRadius - 0.02h, _CorneaRadius + 0.16h, ri));
    albedo *= 1.0h - _LimbusDark * limb;
    c.albedo = saturate(albedo * _AlbedoGain);
    c.N = N;
    c.Ng = geomN;
    c.Niris = normalize(E - _IrisConcavity * (pi_.x * tangent + pi_.y * bitangent));
    c.V = V;
    InitializeBRDFData(half3(0, 0, 0), 0, half3(0, 0, 0), _CorneaSmoothness, alphaUnused, c.corneaB);
    InitializeBRDFData(c.albedo, 0, half3(0, 0, 0), _ScleraSmoothness, alphaUnused, c.scleraB);
    half irisAO = 1.0h - _IrisAO * c.iris * saturate(ri / max(_IrisRadius, 1e-3h)) * saturate(ri / max(_IrisRadius, 1e-3h));
    color = SampleSH(lerp(N, c.Niris, c.iris)) * c.albedo * irisAO;
    // Fresnel / reflection on the normal half-way to the eye axis: the cornea's rim (bulge edge) is a normal crease
    // that lit up as a bright grazing-angle ring around the iris with the stronger hero-v4 reflection
    half3 Nr = normalize(geomN + E * c.cornea);
    half NoVg = saturate(dot(Nr, V));
    half fres = _CorneaF0 + (1.0h - _CorneaF0) * Pow4(1.0h - NoVg) * (1.0h - NoVg);
    half3 env = GlossyEnvironmentReflection(reflect(-V, Nr), i.positionWS, c.corneaB.perceptualRoughness, 1.0h, input.normalizedScreenSpaceUV);
    half wet = lerp(0.35h, 1.0h, c.cornea);   // sclera: a weaker, rougher film than the cornea
    color += V2Desaturate(env, _EnvSaturation) * fres * _EnvSpecular * wet;
    // catch light: a virtual light just above / beside the camera (view space), lit by the ambient seen there
    {
        half3 camRight = UNITY_MATRIX_V[0].xyz;
        half3 camUp = UNITY_MATRIX_V[1].xyz;
        half3 Lc = normalize(V + camUp * _CatchlightUp + camRight * _CatchlightSide);
        half3 sky = V2Desaturate(SampleSH(normalize(Lc + camUp)), 0.5h);
        BRDFData catchB;
        InitializeBRDFData(half3(0, 0, 0), 0, half3(0, 0, 0), _CatchlightSmoothness, alphaUnused, catchB);
        half catchSpec = DirectBRDFSpecular(catchB, geomN, Lc, V) * saturate(dot(geomN, Lc));
        color += sky * (_CatchlightStrength * _CorneaF0 * min(catchSpec, 16.0h) * c.cornea);
    }
    color *= ao.indirectAmbientOcclusion;
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
        color += EyeDirect(c, mainLight);
    #define G3_V2_DIRECT(light) EyeDirect(c, light)

#else // hair
    half3 albedo = saturate(base.rgb * _AlbedoGain);
    half3 strand = SafeNormalize(_StrandAxis > 0.5h ? bitangent : tangent);
    half occ = lerp(1.0h, mask.g, _OcclusionStrength);
    half3 Nf = SafeNormalize(V - strand * dot(V, strand));
    color = _HairShell > 0.5h ? SampleSH(N) * albedo * occ : SampleSH(normalize(lerp(Nf, geomN, 0.5h))) * albedo * occ * 0.8h;
    half3 env = GlossyEnvironmentReflection(reflect(-V, Nf), i.positionWS, 0.5h, 1.0h, input.normalizedScreenSpaceUV);
    color += V2Desaturate(env, _EnvSaturation) * _EnvSpecular * 0.04h * occ;
    color += V2AmbientRim(geomN, V, occ) * albedo;
    color *= ao.indirectAmbientOcclusion;
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
        color += HairDirect(strand, N, V, mainLight.direction, albedo, mainLight.shadowAttenuation) * occ
                 * mainLight.color * (mainLight.distanceAttenuation * mainLight.shadowAttenuation);
    #define G3_V2_DIRECT(light) (HairDirect(strand, N, V, light.direction, albedo, light.shadowAttenuation) * occ * light.color * (light.distanceAttenuation * light.shadowAttenuation))
#endif

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
            color += G3_V2_DIRECT(light);
    }
#endif
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
            color += G3_V2_DIRECT(light);
    LIGHT_LOOP_END
#endif

    color = MixFog(color, input.fogCoord);
#if defined(G3_V2_FRINGE)
    // runner-31: the fringe read grey/whitish (specular + rim at full strength on near-transparent strand ends):
    // premultiplied, so a thin end adds its share of hair colour only (Blend One OneMinusSrcAlpha)
    return half4(min(color, HALF_MAX) * alphaOut, alphaOut);
#else
    return half4(min(color, HALF_MAX), alphaOut);
#endif
}

#endif // G3_V2_FORWARD
