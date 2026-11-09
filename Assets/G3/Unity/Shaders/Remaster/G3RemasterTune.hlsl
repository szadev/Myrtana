// World remaster tuning globals (Docs/remaster-world.md section 8), set by G3WorldRemasterLook from G3WorldLook.json.
// Global (not per-material, outside UnityPerMaterial: SRP batcher safe). An unset global reads 0, so every value is an
// offset from its neutral value: with nothing set the remaster materials render exactly as the builder baked them.
#ifndef G3_REMASTER_TUNE_INCLUDED
#define G3_REMASTER_TUNE_INCLUDED

float4 _G3RemasterDetailTune;   // macro, anti-tile, meso, triplanar multipliers - 1
float4 _G3RemasterSurfaceTune;  // normal - 1, occlusion - 1, cavity (0 = off), variance-preserving anti-tile (0 = plain lerp)
float4 _G3RemasterFadeTune;     // meso fade start - 20 m, end - 60 m, bark multiplier - 1, unused
float4 _G3RemasterFoliageTune;  // translucency multiplier - 1, back-light wrap, sun sheen, unused

#define G3TUNE_MACRO        (1 + _G3RemasterDetailTune.x)
#define G3TUNE_ANTITILE     (1 + _G3RemasterDetailTune.y)
#define G3TUNE_MESO         (1 + _G3RemasterDetailTune.z)
#define G3TUNE_TRIPLANAR    (1 + _G3RemasterDetailTune.w)
#define G3TUNE_NORMAL       (1 + _G3RemasterSurfaceTune.x)
#define G3TUNE_OCCLUSION    (1 + _G3RemasterSurfaceTune.y)
#define G3TUNE_CAVITY       (_G3RemasterSurfaceTune.z)
#define G3TUNE_ANTICONTRAST (_G3RemasterSurfaceTune.w)
#define G3TUNE_MESO_START   (20 + _G3RemasterFadeTune.x)
#define G3TUNE_MESO_END     (60 + _G3RemasterFadeTune.y)
#define G3TUNE_BARK         (1 + _G3RemasterFadeTune.z)
#define G3TUNE_TRANSLUCENCY (1 + _G3RemasterFoliageTune.x)
#define G3TUNE_WRAP         (_G3RemasterFoliageTune.y)
#define G3TUNE_SHEEN        (_G3RemasterFoliageTune.z)

// Light through thin foliage cards when looking towards the sun: the r1 pow-4 view lobe plus a wider wrap lobe, and a
// small sun sheen (leaf cuticle). albedo/normal/view in world space; sun from GetMainLight.
half3 G3FoliageLight(half3 albedo, half3 normalWS, half3 viewWS, half3 sunDir, half3 sunColor, half sunAtten, half translucency)
{
    half back = saturate(dot(-sunDir, viewWS));
    half b2 = back * back;
    half lobe = b2 * b2 + G3TUNE_WRAP * b2 * 0.5h;
    half3 light = albedo * sunColor * (sunAtten * lobe * translucency * G3TUNE_TRANSLUCENCY);
    half3 h = normalize(sunDir + viewWS);
    half ndh = saturate(abs(dot(normalWS, h)));   // cards are two-sided
    half spec = G3TUNE_SHEEN * pow(ndh, 24) * saturate(dot(normalWS, sunDir) * 0.5h + 0.5h);
    return light + sunColor * (sunAtten * spec);
}

// Grass and small plants (grass-v3, Docs/remaster-world.md 10.16): the same back-light lobe, but the sheen takes the
// plant's own hue and only the blades facing the sun get it. The white sheen of G3FoliageLight (pow 24 on normals bent
// half up) was about as bright as the diffuse light on G3's dark plant albedos (~0.15) and bleached every species -
// green tufts, dry grass, flowers, ferns - to one pale straw tone at midday.
half3 G3GrassLight(half3 albedo, half3 normalWS, half3 viewWS, half3 sunDir, half3 sunColor, half sunAtten, half translucency)
{
    half back = saturate(dot(-sunDir, viewWS));
    half b2 = back * back;
    half lobe = b2 * b2 + G3TUNE_WRAP * b2 * 0.5h;
    half3 light = albedo * sunColor * (sunAtten * lobe * translucency * G3TUNE_TRANSLUCENCY);
    half3 h = normalize(sunDir + viewWS);
    half ndh = saturate(abs(dot(normalWS, h)));
    half3 hue = albedo / max(max(albedo.r, max(albedo.g, albedo.b)), 0.02h);
    half spec = G3TUNE_SHEEN * 0.35h * pow(ndh, 32) * saturate(dot(normalWS, sunDir));
    return light + sunColor * (sunAtten * spec) * hue;
}

#endif
