// fx-1 (Docs/remaster-world.md 22.11): shared helpers of the blown-sand effects (G3SandWisps streamers, G3ArchSandWisps tent
// wisps, G3SandDust puffs): hash / value noise, the loose-sand mask of the relief (ReliefGroundMap.Sand, _G3SandMask0 on
// ground map 0's grid), the sand colour lit by sky + sun (+ the main light's shadow), and the medium between the eye and the
// effect - the v2 height fog and atmo-1's storm column - so a streamer inside a sandstorm takes the same haze as the ground
// behind it (they are drawn after the volumetric composite: unfogged they read as dark lines, atmo-1 24.4).
// Include after Core.hlsl, Lighting.hlsl and G3Atmo.hlsl.
#ifndef G3_SAND_STREAK_INCLUDED
#define G3_SAND_STREAK_INCLUDED

TEXTURE2D(_G3SandAlbedo);
TEXTURE2D(_G3SandMask0);
TEXTURE2D(_G3ReliefGround0);
float4 _G3ReliefGroundA, _G3ReliefGroundC;   // ground map 0 (G3ReliefGround.hlsl): xy origin, z 1/texel, w texels (0 = off); C.x base height
float4 _G3SandMaskP;                         // x 1 = the sand mask is uploaded (G3ReliefGroundGpu)
float4 _G3StreakFog;                             // v2 height fog: x density at the base (1/m), y falloff (1/m), z base height (m), w max amount
float4 _G3StreakFogAmb;                          // rgb ambient in-scatter of the v2 fog

uint3 G3StreakPcg(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}

float4 G3StreakRand(uint id, uint salt)
{
    uint3 a = G3StreakPcg(uint3(id, salt, 0x5A4Du)), b = G3StreakPcg(uint3(id, salt, 0x77u));
    return float4(a.xy >> 8u, b.xy >> 8u) * (1.0 / 16777216.0);
}

float G3StreakHash(int2 c, uint salt) { return (G3StreakPcg(uint3(asuint(c), salt)).x >> 8u) * (1.0 / 16777216.0); }

float G3StreakNoise(float2 p, uint salt)
{
    float2 i = floor(p), f = p - i;
    float2 u = f * f * (3 - 2 * f);
    int2 c = (int2)i;
    return lerp(lerp(G3StreakHash(c, salt), G3StreakHash(c + int2(1, 0), salt), u.x),
                lerp(G3StreakHash(c + int2(0, 1), salt), G3StreakHash(c + int2(1, 1), salt), u.x), u.y);
}

// three octaves, mean ~0.5
float G3StreakFbm(float2 p, uint salt)
{
    return G3StreakNoise(p, salt) * 0.55 + G3StreakNoise(p * 2.03 + 17.1, salt + 1u) * 0.3 + G3StreakNoise(p * 4.11 + 5.3, salt + 2u) * 0.15;
}

// ground map 0 texel coordinates of xz; inside = false off the map (or the map off)
float2 G3StreakMapUv(float2 xz, out bool inside)
{
    float n = max(_G3ReliefGroundA.w, 2);
    float2 g = (xz - _G3ReliefGroundA.xy) * _G3ReliefGroundA.z;
    float2 e = min(g, (n - 1) - g);
    inside = _G3ReliefGroundA.w > 0 && min(e.x, e.y) > 1;
    return (g + 0.5) / n;
}

// drawn ground height at xz (relief ground map 0: collision + relief delta), else the fallback
float G3StreakGround(float2 xz, float fallback)
{
    bool inside;
    float2 uv = G3StreakMapUv(xz, inside);
    float y = fallback;
    [branch] if (inside)
    {
        float2 s = SAMPLE_TEXTURE2D_LOD(_G3ReliefGround0, sampler_LinearClamp, uv, 0).rg;
        y = s.g + _G3ReliefGroundC.x + s.r;
    }
    return y;
}

// loose open sand at xz (0..1): the relief's sand mask; strength 0 = everywhere (the desert gate is the caller's). Without the
// mask (relief off, off the map) 1 - the old behaviour.
float G3StreakSand(float2 xz, float strength)
{
    bool inside;
    float2 uv = G3StreakMapUv(xz, inside);
    float m = 1;
    [branch] if (inside && _G3SandMaskP.x > 0.5) m = SAMPLE_TEXTURE2D_LOD(_G3SandMask0, sampler_LinearClamp, uv, 0).r;
    return lerp(1, m, saturate(strength));
}

// fx-1: covered ground (G3CoveredMap: roofs, tents, awnings, overhangs, interiors) at xz, 0..1; 0 without the map
TEXTURE2D(_G3CoveredMask);
float4 _G3CoveredP;   // x0, z0, 1 / texel, N (0 = none)
float G3StreakCovered(float2 xz)
{
    float n = max(_G3CoveredP.w, 2);
    float2 g = (xz - _G3CoveredP.xy) * _G3CoveredP.z;
    float r = 0;
    [branch] if (_G3CoveredP.w > 0 && all(g > 0) && all(g < n - 1))
        r = SAMPLE_TEXTURE2D_LOD(_G3CoveredMask, sampler_LinearClamp, (g + 0.5) / n, 0).r;
    return r;
}

// blown sand colour: G3's loose sand image (its mean, mip 15; a little paler, never brighter - finer grains), lit like the
// sunlit ground it comes from: sky (SH) + sun x the main light's shadow x a mild forward lobe (<= the ground's own N.L
// level: owner, temple hall: the old x1.45 albedo and x2.3 sun lobe without shadows made puffs glow like orange discs)
float3 G3StreakLit(float3 pw, float pale, float sunGain)
{
    float3 sand = SAMPLE_TEXTURE2D_LOD(_G3SandAlbedo, sampler_LinearClamp, float2(0.5, 0.5), 15).rgb;
    sand = lerp(sand, dot(sand, float3(0.3, 0.59, 0.11)).xxx, pale);
    Light ml = GetMainLight();
    float3 v = normalize(pw - _WorldSpaceCameraPos);
    float fwd = pow(saturate(dot(v, ml.direction)), 4);
    float sh = MainLightRealtimeShadow(TransformWorldToShadowCoord(pw));
    float up = saturate(ml.direction.y);   // the sun's height: as the ground's N.L (low sun: dim, not a backlit glow)
    return sand * (SampleSH(float3(0, 1, 0)) + ml.color * sh * sunGain * (0.35 + 0.35 * up + 0.3 * fwd));
}

// the medium between the eye and pw: v2 height fog + the storm column (region weighted), as the volumetric pass integrates
// it for the ground behind; returns the colour seen through it, alpha kept a little more where the haze hides it
float3 G3StreakMedium(float3 pw, float3 lit, inout float alpha)
{
    float3 cam = _WorldSpaceCameraPos;
    float len = distance(pw, cam);
    float h0 = cam.y - _G3StreakFog.z, h1 = pw.y - _G3StreakFog.z;
    float odFog = _G3StreakFog.x > 0 ? _G3StreakFog.x * len * G3AtmoMeanExp(_G3StreakFog.y, h0, h1) : 0;
    float rw = G3AtmoRegion(pw.xz);
    float odStorm = _G3AtmoStorm.x > 0 ? rw * len * _G3AtmoStorm.x * G3AtmoMeanExp(_G3AtmoStorm.y, h0, h1) : 0;
    float od = odFog + odStorm;
    float tr = exp(-od);
    float cap = _G3AtmoStorm.x > 0 ? lerp(saturate(_G3StreakFog.w), saturate(_G3AtmoStorm.w), saturate(odStorm / max(od, 1e-6))) : saturate(_G3StreakFog.w);
    tr = max(tr, 1 - max(cap, 1e-3));
    float3 inCol = od > 1e-6 ? (odFog * _G3StreakFogAmb.rgb + odStorm * _G3AtmoStormCol.rgb) / od : 0;
    alpha *= lerp(1, tr, 0.6);
    return lit * tr + inCol * (1 - tr);
}

// soft against the scene depth (no hard line where the ground or a foot cuts the sheet); needs DeclareDepthTexture.hlsl
float G3StreakSoft(float4 positionCS, float range)
{
    float2 suv = GetNormalizedScreenSpaceUV(positionCS);
    float scene = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
    float self = LinearEyeDepth(positionCS.z, _ZBufferParams);
    return saturate((scene - self) / max(range, 1e-3));
}

#endif
