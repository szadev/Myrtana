// Gothic 3 wind: shared vertex displacement for trees (G3/WindLit) and the vegetation grid (G3/Vegetation).
// All globals are written by G3Wind (Runtime/Wind/G3Wind.cs); with no G3Wind in the scene they are zero and
// nothing moves. Every displacement is a pure function of (world vertex position, object origin, globals), so
// coincident vertices of different sub-meshes (bark / leaves / snow), passes (forward / shadow / depth) and LOD
// levels move identically - leaves stay attached and shadows follow.
#ifndef G3_WIND_INCLUDED
#define G3_WIND_INCLUDED

float  _G3WindTime;        // seconds (driven by G3Wind so it can be frozen / set from the editor)
float  _G3WindTimePrev;    // the wind clock of the previous frame (G3Wind; motion-vector passes evaluate the wind at it)
// Motion-vector passes (remaster TAA, Docs/remaster-world.md 11.7) set this to _G3WindTimePrev - _G3WindTime, evaluate the
// vertex for the previous frame, and reset it to 0. Every wind function reads the clock through G3_WIND_TIME.
static float g3WindTimeShift = 0;
#define G3_WIND_TIME (_G3WindTime + g3WindTimeShift)
float4 _G3WindDirection;   // xyz unit world direction (y = 0), w = total strength (base + gust), 0..~1
float4 _G3WindTree;        // x steady bend (rad), y sway amplitude (rad), z gust bend (rad, current), w branch amplitude (fraction of height)
float4 _G3WindLeaf;        // x leaf flutter amplitude (m), y flutter frequency (rad/s), z/w unused
float4 _G3WindGrass;       // x steady bend, y sway amplitude, z gust bend (all in m per m of plant height), w sway frequency (rad/s)
float4 _G3WindGust;        // x spatial frequency of gust fronts (rad/m), y travel speed (m/s), z contrast 0..1, w unused
// World remaster only (G3WorldRemasterLook, Docs/remaster-world.md section 7): 0 when the remaster is off -> no change.
// x soft rolling grass waves (m per m of plant height), y extra leaf-card rock (fraction), z wave length (m), w wave speed (m/s)
float4 _G3RemasterWind;

// Travelling gust fronts: 0..2 multiplier on the current global gust (mean 1).
float G3WindGustField(float2 xz)
{
    float2 dir = _G3WindDirection.xz;
    float k = _G3WindGust.x;
    float along = (dot(xz, dir) - G3_WIND_TIME * _G3WindGust.y) * k;
    float across = dot(xz, float2(-dir.y, dir.x)) * k * 0.43;
    float w = sin(along + sin(across) * 0.8) * 0.6 + sin(along * 2.37 + across * 1.3 + 1.7) * 0.4;   // -1..1
    return 1 + w * _G3WindGust.z;
}

// Trees and bushes. positionWS: vertex, originWS: renderer pivot (tree base), heightWS: tree height in metres,
// amount: per-material wind multiplier, flutter: per-material leaf flutter multiplier.
float3 G3WindTree(float3 positionWS, float3 originWS, float heightWS, float amount, float flutter)
{
    float H = max(heightWS, 0.5);
    float3 rel = positionWS - originWS;
    float h = max(rel.y, 0) / H;                       // 0 at the root, 1 at the top
    float t = G3_WIND_TIME;
    float2 dir = _G3WindDirection.xz;
    float2 perp = float2(-dir.y, dir.x);
    float phase = dot(originWS.xz, float2(0.173, 0.219));
    float freq = clamp(6.0 * rsqrt(H), 0.8, 5.0);      // big trees sway slower (~0.25 Hz at 16 m)

    // --- whole tree: steady lean + gust + sway, quadratic along the trunk (stiff base)
    float gust = _G3WindTree.z * G3WindGustField(originWS.xz);
    float osc = sin(t * freq + phase) * 0.65 + sin(t * freq * 2.13 + phase * 1.7) * 0.35;
    float angle = (_G3WindTree.x + gust + _G3WindTree.y * osc) * amount;
    float side = _G3WindTree.y * 0.35 * sin(t * freq * 0.77 + phase * 2.3) * amount;
    float2 off = (dir * angle + perp * side) * (h * h * H);
    float len = length(rel);
    float3 bent = rel + float3(off.x, 0, off.y);
    bent *= len / max(length(bent), 1e-4);             // keep the length -> the top dips slightly when bent

    // --- branches: secondary bob, grows with distance from the trunk axis (spatially coherent phase)
    float r = length(rel.xz) / H;
    float bw = saturate(r * 1.5) * saturate(h * 1.5);
    bw *= bw;
    float bphase = dot(positionWS, float3(0.31, 0.23, 0.37));
    float bosc = sin(t * freq * 2.7 + phase + bphase) * 0.7 + sin(t * freq * 4.1 + bphase * 1.9) * 0.3;
    float bamp = _G3WindTree.w * H * bw * amount * (0.6 + 0.4 * G3WindGustField(originWS.xz));
    bent.y += bosc * bamp;
    bent.xz += dir * (bosc * bamp * 0.5);

    // --- leaf flutter: small high-frequency noise of the vertex position, only away from the trunk
    float ft = t * _G3WindLeaf.y;
    float3 q = positionWS * 2.3;
    float3 fl = float3(sin(ft + dot(q, float3(1.0, 0.7, 1.3))),
                       sin(ft * 1.31 + dot(q, float3(-0.8, 1.1, 0.6)) + 1.3),
                       sin(ft * 0.87 + dot(q, float3(0.5, -1.2, 0.9)) + 2.1));
    bent += fl * (_G3WindLeaf.x * flutter * amount * saturate(r * 3) * saturate(h * 2));

    return originWS + bent;
}

// Grass, flowers and small plants of the vegetation grid (bend by height above the instance origin).
float3 G3WindGrass(float3 positionWS, float3 originWS, float amount)
{
    float3 rel = positionWS - originWS;
    float hgt = max(rel.y, 0);
    float t = G3_WIND_TIME;
    float2 dir = _G3WindDirection.xz;
    float2 perp = float2(-dir.y, dir.x);
    float phase = dot(originWS.xz, float2(0.71, 0.53));
    float w = _G3WindGrass.w;
    float osc = sin(t * w + phase) * 0.6 + sin(t * w * 1.93 + phase * 1.37) * 0.4;
    float a = _G3WindGrass.x + _G3WindGrass.z * G3WindGustField(originWS.xz) + _G3WindGrass.y * osc;
    float s = _G3WindGrass.y * 0.3 * sin(t * w * 1.31 + phase * 2.1);
    float2 off = (dir * a + perp * s) * (hgt * saturate(hgt * 2.5) * amount);
    float len = length(rel);
    float3 bent = rel + float3(off.x, 0, off.y);
    bent *= len / max(length(bent), 1e-4);
    // light flutter of leaves / flower heads
    float ft = t * _G3WindLeaf.y;
    float3 q = positionWS * 3.1;
    bent += float3(sin(ft + dot(q, float3(1.0, 0.6, 1.2))), 0, sin(ft * 1.27 + dot(q, float3(-0.7, 1.1, 0.8)))) *
            (_G3WindLeaf.x * 0.5 * amount * saturate(hgt * 2));
    // remaster: soft rolling waves across the grass (coherent over metres, visible at 5-30 m), scaled by the wind
    if (_G3RemasterWind.x > 0)
    {
        float k = 6.2831853 / max(_G3RemasterWind.z, 0.5);
        float along = dot(originWS.xz, dir) * k - t * _G3RemasterWind.w * k;
        float wave = 0.5 + 0.5 * sin(along + 0.6 * sin(dot(originWS.xz, perp) * k * 0.37));
        float strength = _G3RemasterWind.x * (0.35 + saturate(_G3WindDirection.w)) * amount;
        float2 woff = dir * (wave * wave * strength * hgt * saturate(hgt * 2.5));
        float wl = length(bent);
        bent.xz += woff;
        bent *= wl / max(length(bent), 1e-4);
    }
    return originWS + bent;
}

// Uniform object scale from the model matrix (works for rotated child pivots too).
float G3WindObjectScale()
{
    return length(float3(UNITY_MATRIX_M._m01, UNITY_MATRIX_M._m11, UNITY_MATRIX_M._m21));
}

float3 G3WindObjectOrigin()
{
    return float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23);
}


// NDC motion (uv units) between the previous and the current clip positions, like URP's CalcNdcMotionVectorFromCsPositions
// but without its force-no-motion branch: instanced / indirect plants have no per-object motion settings of their own.
float2 G3WindMotion(float4 posCS, float4 prevPosCS)
{
    float2 v = posCS.xy / posCS.w - prevPosCS.xy / prevPosCS.w;
#if UNITY_UV_STARTS_AT_TOP
    v.y = -v.y;
#endif
    return v * 0.5;
}
#endif
