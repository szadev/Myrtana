#ifndef G3_SCATTER_BEND_INCLUDED
#define G3_SCATTER_BEND_INCLUDED
// Character bending of plants (Docs/remaster-world.md section 18.5, remaster-veg): a toroidal trail map round the camera,
// stamped every frame by G3ScatterCull.compute (kernel Bend) with the hero/NPC/animal positions and recovering over ~1.6 s.
// rg = push direction x strength (|rg| <= 1). Used by G3/Remaster/Scatter and G3/Remaster/GrassV2. Off: _G3BendParams.w = 0.

TEXTURE2D(_G3BendTrail);
SamplerState g3bend_linear_repeat_sampler;
float4 _G3BendParams;   // x extent (m), y 1 / extent, z strength, w on
float4 _G3BendCam;      // xz: centre of the map (camera), y unused

// Push vector (xz, |v| <= 1) at a plant's origin.
float2 G3BendPush(float3 originWS)
{
    if (_G3BendParams.w <= 0) return 0;
    float2 rel = originWS.xz - _G3BendCam.xz;
    float hw = _G3BendParams.x * 0.5;
    float edge = saturate((hw - max(abs(rel.x), abs(rel.y))) / (hw * 0.15));
    float2 v = SAMPLE_TEXTURE2D_LOD(_G3BendTrail, g3bend_linear_repeat_sampler, originWS.xz * _G3BendParams.y, 0).rg;
    return v * edge * _G3BendParams.z;
}

// Bends a plant vertex away from the push (like G3WindGrass: by height above the origin, length kept, flattened a little).
float3 G3BendApply(float3 positionWS, float3 originWS, float amount)
{
    if (amount <= 0 || _G3BendParams.w <= 0) return positionWS;
    float2 push = G3BendPush(originWS) * amount;
    float pl = length(push);
    if (pl < 1e-3) return positionWS;
    float3 rel = positionWS - originWS;
    float hgt = max(rel.y, 0);
    float len = length(rel);
    float3 bent = rel;
    bent.xz += push * (1.4 * hgt * saturate(hgt * 3));
    bent.y -= hgt * 0.45 * saturate(pl);
    bent *= len / max(length(bent), 1e-4);
    return originWS + bent;
}
#endif
