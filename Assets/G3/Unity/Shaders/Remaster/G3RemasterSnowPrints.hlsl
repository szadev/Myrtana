// Remaster snow footprints (Docs/remaster-world.md 12.5 / 12.6), shared by every pass of G3/Remaster/WorldV2 (and by any
// other shader that wants the prints, e.g. a dense near-camera ground mesh): the prints are a ring buffer of analytic shapes
// (boot sole, paw pads, oval) and a toroidal grid of cells holding up to 8 print indices each (G3SnowV2 / SnowPrintGrid).
// A point looks up its cell and evaluates the shapes - no texture, so no texel stair steps at any distance, and the soft
// wall is widened by the pixel footprint (filtered, no aliasing). Maths mirrored in SnowV2.Print (tested).
// Needs: Core.hlsl; StructuredBuffer (target 4.5). Globals zero / dummy buffers while snow or prints are off.
#ifndef G3_REMASTER_SNOW_PRINTS_INCLUDED
#define G3_REMASTER_SNOW_PRINTS_INCLUDED

float4 _G3SnowTrail;   // x prints on (1), y cell size (m), z cells per side (power of two), w refill clock now (wrapped)
float4 _G3SnowTex;     // x G3 snow repeat (1/m), y scan repeat (1/m), z soft wall (m), w print depth (m)
float4 _G3SnowRegion;  // region map uv = p.xz * xy + zw (x 0 = no map)
TEXTURE2D(_G3SnowRegionTex);   // R snow region (Nordmar), G desert (Varant; remaster-sand, section 22)
float4 _G3SandTrail;   // remaster-sand: x sand prints on, y sand clock now (wrapped), z sand depth rel. to _G3SnowTex.w, w floor shade

struct G3SnowPrintData
{
    float4 a;   // x, z (world m), walk direction x, z (unit)
    float4 b;   // half length, half width (m), ground height (m), refill clock at the stamp (wrapped)
    float4 c;   // depth 0..1, kind (0 boot, 1 paw, 2 oval; + 4 = a sand print on the sand clock), side (-1 left, +1 right), rim strength
};
StructuredBuffer<G3SnowPrintData> _G3SnowPrints;
struct G3SnowCell { uint4 a; uint4 b; };   // 8 print indices, 0xFFFFFFFF = free
StructuredBuffer<G3SnowCell> _G3SnowCells;

#define G3SNOW_CLOCK_WRAP 4096.0
#define G3SNOW_RIM_WIDTH 0.035
#define G3SNOW_WALL 0.016        // SnowV2.Wall

// SnowV2.Age / Fade / HeightMatch
float G3SnowAge(float now, float stamped) { float a = now - stamped; return a < 0 ? a + G3SNOW_CLOCK_WRAP : a; }
float G3SnowTrailFade(float age) { return 1 - smoothstep(0.3, 1.0, age); }
float G3SnowHeightMatch(float dy) { return 1 - smoothstep(0.35, 0.8, abs(dy)); }

float G3SnowPad(float u, float v, float cu, float cv, float ru, float rv, float soft)
{
    float2 q = float2((u - cu) / ru, (v - cv) / rv);
    float sd = (length(q) - 1) * min(ru, rv);
    return 1 - smoothstep(-soft * 0.6, soft * 0.25, sd);
}

// SnowV2.Print: x depth 0..1, y rim 0..1 at local metres (u along the walk, v across)
float2 G3SnowPrintShape(float u, float v, float hl, float hw, float kind, float side, float soft)
{
    float x = u / hl;
    float2 res = 0;   // one exit (fxc: no "potentially uninitialized" through branch returns)
    [branch] if (kind < 0.5)
    {
        float xb = x + 0.05;
        float wf = 0.76 + 0.24 * smoothstep(-0.2, 0.45, x) - 0.1 * exp(-(xb * xb) / (0.22 * 0.22));
        float vv = v * side - 0.06 * hw * smoothstep(0.1, 0.8, x);
        float y = vv / (hw * wf);
        float rd = pow(pow(abs(x), 2.6) + pow(abs(y), 2.6), 1 / 2.6);
        float sd = (rd - 1) * min(hl, hw * wf);
        float xa = x + 0.08, xh = x + 0.7;
        float fl = 1 - 0.2 * exp(-(xa * xa) / 0.04) - 0.2 * smoothstep(0.75, 1.05, x) + 0.08 * exp(-(xh * xh) / 0.04);
        res.x = (1 - smoothstep(-soft, soft * 0.35, sd)) * saturate(fl);
        res.y = smoothstep(-soft * 0.2, soft, sd) * (1 - smoothstep(soft, soft + G3SNOW_RIM_WIDTH, sd)) * (0.55 + 0.45 * smoothstep(0.2, 1, x));
    }
    else
    {
        float y0 = v / hw;
        float sd0 = (length(float2(x, y0)) - 1) * min(hl, hw);
        [branch] if (kind > 1.5)
        {
            res.x = 1 - smoothstep(-soft, soft * 0.35, sd0);
            res.y = smoothstep(-soft * 0.2, soft, sd0) * (1 - smoothstep(soft, soft + G3SNOW_RIM_WIDTH, sd0)) * 0.7;
        }
        else
        {
            float sp = max(soft - 0.7 * G3SNOW_WALL, 0.005);   // SnowV2: the ~1 cm pads get a sharper wall
            float d = 0.35 * (1 - smoothstep(-soft, soft * 0.35, sd0 + 0.1 * min(hl, hw)));
            d = max(d, G3SnowPad(u, v, -0.32 * hl, 0, 0.36 * hl, 0.5 * hw, sp));
            d = max(d, G3SnowPad(u, v, 0.46 * hl, 0.2 * hw, 0.2 * hl, 0.22 * hw, sp) * 0.9);
            d = max(d, G3SnowPad(u, v, 0.46 * hl, -0.2 * hw, 0.2 * hl, 0.22 * hw, sp) * 0.9);
            d = max(d, G3SnowPad(u, v, 0.12 * hl, 0.62 * hw, 0.19 * hl, 0.2 * hw, sp) * 0.85);
            d = max(d, G3SnowPad(u, v, 0.12 * hl, -0.62 * hw, 0.19 * hl, 0.2 * hw, sp) * 0.85);
            res.x = d;
            res.y = smoothstep(-soft * 0.2, soft, sd0) * (1 - smoothstep(soft, soft + G3SNOW_RIM_WIDTH * 0.7, sd0)) * 0.6;
        }
    }
    return res;
}

void G3SnowAccumulate(uint id, float2 xz, float y, float soft, inout float depth, inout float rim)
{
    [branch] if (id != 0xFFFFFFFFu)
    {
        G3SnowPrintData P = _G3SnowPrints[id];
        float2 d = xz - P.a.xy;
        float reach = max(P.b.x, P.b.y) * 1.15 + G3SNOW_RIM_WIDTH + 0.03 + soft;
        // remaster-sand (SandV2.IsSandPrint / BaseKind): sand prints age on the wind clock (they fill in in minutes)
        bool sandPrint = P.c.y > 3.5;
        float now = sandPrint ? _G3SandTrail.y : _G3SnowTrail.w;
        float k = G3SnowTrailFade(G3SnowAge(now, P.b.w)) * G3SnowHeightMatch(y - P.b.z) * P.c.x;
        [branch] if (dot(d, d) <= reach * reach && P.b.x > 0 && k > 0.001)
        {
            float u = dot(d, P.a.zw), v = dot(d, float2(-P.a.w, P.a.z));
            float2 s = G3SnowPrintShape(u, v, P.b.x, P.b.y, sandPrint ? P.c.y - 4 : P.c.y, P.c.z, soft);
            depth = max(depth, s.x * k);
            rim = max(rim, s.y * k * P.c.w);
        }
    }
}

// x print depth 0..1 (faded, on this level; the deepest print), y rim (outside every print) at a world point.
// soft = wall softness (m): _G3SnowTex.z, plus the pixel footprint in a fragment shader. One [loop] over the 8 slots: every
// call site inlines one shape evaluation only (fxc compile time, instruction cache).
float2 G3SnowPrintsAt(float2 xz, float y, float soft)
{
    if (_G3SnowTrail.x <= 0) return 0;
    uint n = (uint)_G3SnowTrail.z;
    int2 c = (int2)floor(xz / _G3SnowTrail.y);
    uint2 m = asuint(c) & (n - 1);   // n is a power of two: the AND is the modulo, also for negative cells
    G3SnowCell cell = _G3SnowCells[m.y * n + m.x];
    uint4 s0 = cell.a, s1 = cell.b;
    float depth = 0, rim = 0;
    [loop] for (uint k = 0; k < 8; k++)
    {
        uint4 sl = k < 4 ? s0 : s1;
        uint id = sl[k & 3];
        G3SnowAccumulate(id, xz, y, soft, depth, rim);
    }
    return float2(depth, rim * (1 - saturate(depth * 2)));
}

// ---- shared interface (Docs/remaster-world.md 12.6): height offset of the snow surface at a world point, metres (negative
// = pressed in, positive = the pushed-up rim), 0 with the prints off. Vertex/hull/domain safe (no derivatives, no
// textures). Scale it by the surface's snow weight before displacing along world up.
float G3SnowDisplacement(float3 pw)
{
    float2 t = G3SnowPrintsAt(pw.xz, pw.y, _G3SnowTex.z);
    return (-t.x + 0.35 * t.y) * _G3SnowTex.w;
}
float G3SnowDisplacement(float3 pw, SamplerState unused) { return G3SnowDisplacement(pw); }

#if defined(G3SNOW_WORLDV2_VERTEX)   // needs WorldV2's sampler_trilinear_repeat_aniso8
// Real displaced prints on a dense near-camera ground mesh (remaster-relief's focus patch, 13.5): TEXCOORD3.z = the spacing
// a focus point asks for there (m), 0 elsewhere / on meshes without it - only vertices closer than ~0.2 m apart follow the
// prints (coarse G3 cells would tilt whole triangles). The weight is positional only (z and the snow-region map): a weight
// from the material's layers or G3's vertex colour would differ on the two sides of a material seam inside the patch and
// open a crack (relief's warning). Returns the displaced world position; dense = 0..1 how far it applies (the fragment then
// skips the parallax, which would count the depth twice).
float3 G3SnowDisplaceWS(float3 pw, float4 uv3, half4 color, out float dense)
{
    dense = 0;
    [branch] if (_G3SnowTrail.x <= 0 || uv3.z <= 0) return pw;
    dense = 1 - smoothstep(0.14, 0.24, uv3.z);
    [branch] if (dense <= 0) return pw;
    float2 ruv = saturate(pw.xz * _G3SnowRegion.xy + _G3SnowRegion.zw);
    dense *= _G3SnowRegion.x > 0 ? SAMPLE_TEXTURE2D_LOD(_G3SnowRegionTex, sampler_trilinear_repeat_aniso8, ruv, 0).r : 0;
    pw.y += G3SnowDisplacement(pw) * dense;
    return pw;
}
#endif

#endif
