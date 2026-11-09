#ifndef G3_RELIEF_GROUND_INCLUDED
#define G3_RELIEF_GROUND_INCLUDED
// The relief's ground maps (Docs/remaster-world.md section 14, G3ReliefGroundGpu): drawn relief minus G3's collision height
// round the camera, two RG16 float levels (160 m at 0.31 m, 511 m at 1 m; G = collision height - base). Things G3 placed on
// its collision surface (grass) add G3ReliefGroundDelta(origin) to stand on what is drawn. 0 with the relief off.

TEXTURE2D(_G3ReliefGround0);
TEXTURE2D(_G3ReliefGround1);
SamplerState g3relief_linear_clamp_sampler;
float4 _G3ReliefGroundA, _G3ReliefGroundB, _G3ReliefGroundC;

// Delta of one level at xz; inside = 1 within it, fading to 0 over its outer 6 %; onGround from the collision height there
float G3ReliefLevel(TEXTURE2D_PARAM(tex, smp), float4 P, float baseY, float3 p, out float inside)
{
    float n = max(P.w, 2);
    float2 g = (p.xz - P.xy) * P.z;
    float2 e = min(g, (n - 1) - g);
    inside = P.w > 0 ? saturate(min(e.x, e.y) / (n * 0.06)) : 0;
    float2 s = SAMPLE_TEXTURE2D_LOD(tex, smp, (g + 0.5) / n, 0).rg;
    // a thing more than 0.6 m off the collision ground there is not on the terrain (a rock, a roof)
    float onGround = 1 - smoothstep(0.3, 0.6, abs(p.y - (s.g + baseY)));
    return s.r * onGround;
}

float G3ReliefGroundDelta(float3 p)
{
    if (_G3ReliefGroundB.w <= 0) return 0;
    float i0, i1;
    float d1 = G3ReliefLevel(TEXTURE2D_ARGS(_G3ReliefGround1, g3relief_linear_clamp_sampler), _G3ReliefGroundB, _G3ReliefGroundC.y, p, i1);
    float d0 = G3ReliefLevel(TEXTURE2D_ARGS(_G3ReliefGround0, g3relief_linear_clamp_sampler), _G3ReliefGroundA, _G3ReliefGroundC.x, p, i0);
    return lerp(d1 * i1, d0, i0);
}
#endif
