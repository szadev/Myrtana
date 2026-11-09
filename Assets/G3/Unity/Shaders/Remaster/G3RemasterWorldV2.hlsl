// World remaster v2 (Docs/remaster-world.md section 10): shared helpers of G3/Remaster/WorldV2 - hex-tile stochastic
// sampling, world triplanar with skipped planes, parallax occlusion, colour matching of CC0 scans to G3's palette.
#ifndef G3_REMASTER_WORLDV2_INCLUDED
#define G3_REMASTER_WORLDV2_INCLUDED

// Globals set by G3WorldV2 from G3WorldLook.json (outside UnityPerMaterial: SRP batcher safe).
float4 _G3V2Surface;    // x PBR (0 = G3 Blinn-Phong), y stochastic tiling, z parallax depth (0 = off), w colour match (0 CC0 .. 1 G3 mean)
float4 _G3V2Surface2;   // x terrain triplanar / steep rock push, y G3 pattern kept, z height blend band, w rock triplanar detail
float4 _G3V2Weather;    // x wetness 0..1 (rain), y base dirt amount, z edge wear amount, w moss amount
float4 _G3V2Debug;      // x debug mode (0 off, 1 kinds, 2 G3 base, 3 CC0 detail, 4 AO, 5 tri weights, 6 macro, 7 height, 8 albedo, 9 normal)
float4 _G3V2Far;        // x detail fade start (m), y end (m), z detail kept at range (runner-31 lattice), w ground gloss cap (grass-v3; 0 = none)
float4 _G3V2Color;      // x hue lock ground/surfaces, y hue lock rocks (WorldV2.MatchColor), z G3 detail maps on ground (v2G3Detail), w G3 fine colour on ground (v2G3Fine)
float4 _G3V2Ai;         // ai-rocks pass 6: x AI rock saturation of the current region (WorldV2.AiGlobal; 0 = per material), yzw unused
float4 _G3V2Color2;     // x terrain ground hue lock (v2GroundHueLock), y its saturation (v2GroundSaturation; 0 = 1), z AI close-up grain (v2AiMicro), w its repeat (m)
float4 _G3V2ArchSand;   // arch-2 (25.7): x storm sand deposit 0..1, y cloth amount, z stone ledges following the deposit, w 1 = on (ArchSand.Global)
float4 _G3V2Arch2;      // arch-3 (26): x Myrtana moss/dirt scale, y Nordmar frost scale (WorldV2.ArchGlobal2; 0 = off)
float4 _G3V2Arch;       // arch-1 (25): x arch micro detail, y parallax scale, z dust scale, w cloth translucency scale (WorldV2.ArchGlobal; 0 = off)

// fix-130 rain shadow (G3WeatherDirector / G3.Weather.RainShadowGrid): per cell the highest static surface over it, from
// above; x, y the grid's lower corner (world x z, m), z cell size (m), w cells per side (0 = off).
TEXTURE2D_FLOAT(_G3RainShadow);
float4 _G3RainShadowParams;

// 0 = open to the rain, 1 = under a roof (RainShadowGrid.Shelter: tolerance 0.75 m, ramp 0.5 m). snow3: the occluder height
// is interpolated bilinearly between the four nearest cell centres (RainShadowGrid.TopAt): one point-sampled 1 m cell per
// pixel compared the slope's own height at the cell centre with every pixel of the cell, so on steep ground the lower part
// of each cell counted as "under a roof" - axis-aligned stair steps without snow (owner, build 20c). On open ground the
// interpolated top is the ground itself (planar between centres): no shelter at any steepness; a roof edge ramps over one
// cell. collisionDy: how far the drawn surface lies above G3's collision (remaster-relief swells; the grid traces the
// collision), so a relief hollow is not taken for a floor under a roof.
float G3RainShelter(float3 pw, float collisionDy)
{
    if (_G3RainShadowParams.w < 0.5) return 0;
    float2 g = (pw.xz - _G3RainShadowParams.xy) / _G3RainShadowParams.z - 0.5;
    int n = (int)_G3RainShadowParams.w;
    int2 c = (int2)floor(g);
    float2 f = g - c;
    if (any(c < -1) || any(c >= n)) return 0;
    int2 c0 = clamp(c, 0, n - 1), c1 = clamp(c + 1, 0, n - 1);
    float t00 = LOAD_TEXTURE2D(_G3RainShadow, int2(c0.x, c0.y)).r, t10 = LOAD_TEXTURE2D(_G3RainShadow, int2(c1.x, c0.y)).r;
    float t01 = LOAD_TEXTURE2D(_G3RainShadow, int2(c0.x, c1.y)).r, t11 = LOAD_TEXTURE2D(_G3RainShadow, int2(c1.x, c1.y)).r;
    // a corner with nothing traced / nothing below (Open = -1e5) takes the lowest valid corner: no -1e5 mixed in
    float lo = min(min(t00 > -9e4 ? t00 : 1e6, t10 > -9e4 ? t10 : 1e6), min(t01 > -9e4 ? t01 : 1e6, t11 > -9e4 ? t11 : 1e6));
    if (lo > 9e5) return 0;
    t00 = t00 > -9e4 ? t00 : lo; t10 = t10 > -9e4 ? t10 : lo; t01 = t01 > -9e4 ? t01 : lo; t11 = t11 > -9e4 ? t11 : lo;
    float top = lerp(lerp(t00, t10, f.x), lerp(t01, t11, f.x), f.y);
    // fix-169: not above the pixel's own cell (RainShadowGrid.OccluderAt): bilinear smeared a roof half a cell into the
    // open ground beside it (dry strip / probe 0.46 next to a house); on slopes the plane is already the lower one
    int2 co = (int2)floor(g + 0.5);
    if (all(co >= 0) && all(co < n))
    {
        float own = LOAD_TEXTURE2D(_G3RainShadow, co).r;
        if (own > -9e4) top = min(top, own);
    }
    return saturate((top - (pw.y - collisionDy) - 0.75) / 0.5);
}

// fx-1 (22.11) covered-ground mask round the camera (G3CoveredMap: roofs, tents, awnings, overhangs, interiors; 2D, no
// height): x0, z0, 1 / texel, N (0 = none). Shared with the arch terms (arch-4: no sand indoors).
TEXTURE2D(_G3CoveredMask);
float4 _G3CoveredP;

float G3V2CoveredAt(float2 xz)
{
    float n = max(_G3CoveredP.w, 2);
    float2 g = (xz - _G3CoveredP.xy) * _G3CoveredP.z;
    float r = 0;
    [branch] if (_G3CoveredP.w > 0 && all(g > 0) && all(g < n - 1))
        r = SAMPLE_TEXTURE2D_LOD(_G3CoveredMask, sampler_LinearClamp, (g + 0.5) / n, 0).r;
    return r;
}

// arch-4: 0 = open sky, 1 = indoors / under a roof, for the desert's settled sand, dust and wind ripples (owner: sand
// and a dotted dust grid on the carpets inside the houses). The rain-shadow grid (fix-130, height test: a roof's own top
// is open, the floor under it covered) when it runs; else fx-1's 2D covered mask, where a face whose outside (1.5 texels
// along its normal) is open counts as an outer wall, and an up face of a mesh > 2 m above its object's base as a roof top.
float G3V2Covered(float3 pw, float3 nGeo, float collisionDy, float aboveBase, bool mesh)
{
    float c = 0;
    [branch] if (_G3RainShadowParams.w > 0.5)
        c = G3RainShelter(pw, collisionDy);
    else if (_G3CoveredP.w > 0.5)
    {
        c = G3V2CoveredAt(pw.xz);
        float l = length(nGeo.xz);
        [branch] if (c > 0.01 && l > 0.3) c *= G3V2CoveredAt(pw.xz + nGeo.xz / l * (1.5 / max(_G3CoveredP.z, 1e-3)));
        if (mesh && nGeo.y > 0.5) c *= 1 - smoothstep(1.6, 2.4, aboveBase);
    }
    return c;
}

struct V2Sample
{
    float3 albedo;   // linear
    float3 n;        // world normal
    float4 mask;     // x metallic, y occlusion, z height, w smoothness
    float green;     // how green G3's own colour is here (moss reference, rock path; 0 elsewhere)
    float3 dbg;      // debug term of _G3V2Debug.x (runner isolation)
    float uvMesh;    // AI layers (kind 3): share of G3's mesh UV (1 - triplanar weight); G3's detail multiply follows it
    float3 g3Soft;   // sand-3: G3's own colour heavily blurred (the desert sand palette under the scans; 0 = not set)
};

float G3V2Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float2 G3V2Hash2(float2 p)
{
    float2 r = mul(float2x2(127.1, 311.7, 269.5, 183.3), p);
    return frac(sin(r) * 43758.5453);
}

float G3V2Noise(float2 x)
{
    float2 i = floor(x), f = frac(x);
    float2 u = f * f * (3 - 2 * f);
    float a = G3V2Hash2(i).x, b = G3V2Hash2(i + float2(1, 0)).x, c = G3V2Hash2(i + float2(0, 1)).x, d = G3V2Hash2(i + float2(1, 1)).x;
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// ---------------------------------------------------------------------------------------------------- hex tiling
// Mikkelsen 2022, "Practical Real-Time Hex-Tiling" (JCGT 11(2)): three rotated, offset copies of the texture on a
// triangle grid, blended by barycentric weights sharpened by the texels' luminance; no visible repetition, mean kept.
struct G3Hex
{
    float2 uv1, uv2, uv3;
    float2x2 r1, r2, r3;
    float3 w;
};

float2x2 G3HexRot(int2 id)
{
    float a = abs(id.x * id.y) + abs(id.x + id.y) + PI;
    a = fmod(a, 2 * PI);
    a = a < 0 ? a + 2 * PI : a;
    a = a > PI ? a - 2 * PI : a;
    a *= 0.6;   // rotation strength: some rotation hides the grid, full rotation would turn directional scans
    float c = cos(a), s = sin(a);
    return float2x2(c, -s, s, c);
}

float2 G3HexCenter(int2 v)
{
    const float2x2 invSkew = float2x2(1.0, 0.5, 0.0, 1.0 / 1.15470054);
    return mul(invSkew, float2(v)) / (2 * sqrt(3.0));
}

G3Hex G3HexSetup(float2 uv)
{
    G3Hex h;
    float2 st = uv * 2 * sqrt(3.0);
    const float2x2 skew = float2x2(1.0, -0.57735027, 0.0, 1.15470054);
    float2 sk = mul(skew, st);
    int2 base = int2(floor(sk));
    float3 t = float3(frac(sk), 0);
    t.z = 1 - t.x - t.y;
    float s = step(0.0, -t.z);
    float s2 = 2 * s - 1;
    h.w = float3(-t.z * s2, s - t.y * s2, s - t.x * s2);
    int2 v1 = base + int2(s, s), v2 = base + int2(s, 1 - s), v3 = base + int2(1 - s, s);
    h.r1 = G3HexRot(v1); h.r2 = G3HexRot(v2); h.r3 = G3HexRot(v3);
    float2 c1 = G3HexCenter(v1), c2 = G3HexCenter(v2), c3 = G3HexCenter(v3);
    h.uv1 = mul(uv - c1, h.r1) + c1 + G3V2Hash2(float2(v1));
    h.uv2 = mul(uv - c2, h.r2) + c2 + G3V2Hash2(float2(v2));
    h.uv3 = mul(uv - c3, h.r3) + c3 + G3V2Hash2(float2(v3));
    return h;
}

// final weights from the barycentrics and the three albedo samples (WorldV2.HexWeights mirrors the exponent part)
float3 G3HexWeights(G3Hex h, float3 a1, float3 a2, float3 a3)
{
    float3 lw = float3(G3V2Luma(a1), G3V2Luma(a2), G3V2Luma(a3));
    lw = lerp(1.0, lw, 0.6);
    float3 w = lw * pow(max(h.w, 0), 7);
    return w / max(w.x + w.y + w.z, 1e-5);
}

// rotate a tangent-space normal sample back from a hex copy's rotated frame
float3 G3HexNormal(float3 nts, float2x2 r) { nts.xy = mul(r, nts.xy); return nts; }

// ---------------------------------------------------------------------------------------------------- projections
// whiteout blend of a tangent-space sample t projected on a world plane onto the world normal n
// axis 0: plane ZY (x faces), 1: XZ (y faces), 2: XY (z faces)
float3 G3V2PlaneNormal(float3 t, float3 n, int axis)
{
    if (axis == 0) { float3 r = float3(t.xy + n.zy, abs(t.z) * n.x); return r.zyx; }
    if (axis == 1) { float3 r = float3(t.xy + n.xz, abs(t.z) * n.y); return r.xzy; }
    return float3(t.xy + n.xy, abs(t.z) * n.z);
}

float3 G3V2TriWeights(float3 n)
{
    // tightened (runner-31): a plane nearly parallel to its projection axis contributes nothing - its texels would
    // stretch into streaks. Weights reach 0 continuously, so skipping planes under 0.02 is seamless.
    float3 w = max(abs(n) - 0.25, 0);
    w = w * w * w * w;
    return w / max(w.x + w.y + w.z, 1e-6);
}

// ---------------------------------------------------------------------------------------------------- colour match
// CC0 scan recoloured to G3's palette: per-channel ratio of the layer means (the smallest mips), limited, blended by
// amount; then the hue lock: G3's mean colour x the texel's relative luminance (lock 1 = the scan brings only light and
// dark detail, its own hue - e.g. orange sandstone - is gone). Mirrors WorldV2.MatchColor (tested).
float3 G3V2Match(float3 cc0, float3 cc0Mean, float3 g3Mean, float amount, float hueLock)
{
    float3 ratio = clamp(g3Mean / max(cc0Mean, 0.01), 0.2, 5.0);
    float3 m = lerp(cc0, cc0 * ratio, amount);
    float3 locked = g3Mean * (G3V2Luma(cc0) / max(G3V2Luma(cc0Mean), 0.01));
    return lerp(m, locked, hueLock);
}

// ---------------------------------------------------------------------------------------------------- parallax
// Parallax occlusion on the B channel (height) of a mask map: linear search + one secant refinement. Returns the uv
// offset (same uv space as uv). depth = relief depth in uv units; fade 0 = off.
float2 G3V2Parallax(TEXTURE2D_PARAM(tex, smp), float2 uv, float2 dx, float2 dy, float3 viewTS, float depth, float steps)
{
    float2 delta = -viewTS.xy / max(viewTS.z, 0.15) * depth / steps;
    float layer = 1.0 / steps;
    float cur = 1.0;
    float2 off = 0;
    float h = SAMPLE_TEXTURE2D_GRAD(tex, smp, uv, dx, dy).b;
    float prevH = h, prevCur = cur;
    [loop] for (int i = 0; i < 24 && i < (int)steps; i++)
    {
        if (h >= cur) break;
        prevH = h; prevCur = cur;
        off += delta;
        cur -= layer;
        h = SAMPLE_TEXTURE2D_GRAD(tex, smp, uv + off, dx, dy).b;
    }
    // secant between the last two samples
    float after = h - cur, before = prevH - prevCur;
    float t = saturate(after / max(after - before, 1e-4));
    return off - delta * t;
}

#endif
