#ifndef G3_RELIEF_MORPH_INCLUDED
#define G3_RELIEF_MORPH_INCLUDED

// fix-171 (Docs/remaster-world.md 13.8): the terrain relief's fine detail faded by the LIVE camera distance, so the drawn
// surface is a function of where the camera is now, not of the last near batch: no shape change when a batch is swapped in
// (owner, build 21a: "the mountain changes as I move"). The relief mesh (G3TerrainRelief) stores per vertex:
//   POSITION  the coarse point: Phong + the macro octaves (>= macroLambda, the same in every mesh)
//   TEXCOORD2 xyz the displacement direction (welded smooth normal, the same for every copy of a point), w strata amplitude (m)
//   TEXCOORD3 x gap, y mask, z focus spacing (snow), w the spacing this vertex was sampled for (sigma)
//   TEXCOORD4 fine octave slots 0-3 (m at full weight), TEXCOORD5 xyz slots 4-6, w = 1 (marker: a relief vertex)
//   TEXCOORD6 xyz the shading normal without the fine octaves
// The band of an octave of wavelength L at spacing s is smoothstep((L/s - 2) / 1.5) like ReliefNoise.Band; the spacing is
// max(the camera's near spacing at the live distance, sigma): never finer than the mesh can carry. Landscape cells have
// identity transforms, so object space is world space. Every pass (colour, depth, depth-normals, shadows) calls it, so they
// see one surface. Non-relief meshes (no TEXCOORD5.w = 1) are untouched.

float4 _G3ReliefEye;      // xyz the main camera, w 1 while the relief is on
float4 _G3ReliefBand;     // x near spacing, y near slope (per m), z far spacing, w strata wavelength (0 = no strata)
float4 _G3ReliefLambdaA;  // wavelengths of fine slots 0-3 (m, 0 = unused)
float4 _G3ReliefLambdaB;  // xyz slots 4-6

float G3ReliefBandWeight(float lambda, float sigma)
{
    float t = saturate((lambda / max(sigma, 1e-4) - 2.0) / 1.5);   // lambda 0 (unused slot): t = 0
    return t * t * (3.0 - 2.0 * t);
}

float4 G3ReliefBand4(float4 lambda, float sigma)
{
    float4 t = saturate((lambda / max(sigma, 1e-4) - 2.0) / 1.5);
    return t * t * (3.0 - 2.0 * t);
}

// ReliefNoise.Hash / Value3 (the same integer hash, quintic fade)
uint G3ReliefHash(int x, int y, int z, uint seed)
{
    uint h = seed * 0x9E3779B1u;
    h ^= asuint(x) * 0x85EBCA77u; h = (h << 13) | (h >> 19); h *= 0xC2B2AE3Du;
    h ^= asuint(y) * 0x27D4EB2Fu; h = (h << 13) | (h >> 19); h *= 0x165667B1u;
    h ^= asuint(z) * 0x9E3779B9u; h = (h << 13) | (h >> 19); h *= 0x85EBCA6Bu;
    h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
    return h;
}

float G3ReliefHash01(int x, int y, int z, uint seed) { return (G3ReliefHash(x, y, z, seed) & 0xFFFFFFu) / 16777216.0; }

float G3ReliefValue3(float3 q, uint seed)
{
    float3 fl = floor(q);
    int3 i = (int3)fl;
    float3 f = q - fl;
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float c000 = G3ReliefHash01(i.x, i.y, i.z, seed), c100 = G3ReliefHash01(i.x + 1, i.y, i.z, seed);
    float c010 = G3ReliefHash01(i.x, i.y + 1, i.z, seed), c110 = G3ReliefHash01(i.x + 1, i.y + 1, i.z, seed);
    float c001 = G3ReliefHash01(i.x, i.y, i.z + 1, seed), c101 = G3ReliefHash01(i.x + 1, i.y, i.z + 1, seed);
    float c011 = G3ReliefHash01(i.x, i.y + 1, i.z + 1, seed), c111 = G3ReliefHash01(i.x + 1, i.y + 1, i.z + 1, seed);
    float x00 = c000 + (c100 - c000) * u.x, x10 = c010 + (c110 - c010) * u.x;
    float x01 = c001 + (c101 - c001) * u.x, x11 = c011 + (c111 - c011) * u.x;
    float y0 = x00 + (x10 - x00) * u.y, y1 = x01 + (x11 - x01) * u.y;
    return (y0 + (y1 - y0) * u.z) * 2.0 - 1.0;
}

// ReliefNoise.Strata without the band: horizontal bands of wavelength lambda in height, warped, sharpened towards the crests
float G3ReliefStrata(float3 p, float lambda)
{
    float warp = G3ReliefValue3(float3(p.x * 0.06, p.y * 0.02, p.z * 0.06), 5u) * 1.6 + G3ReliefValue3(float3(p.x * 0.3, p.y * 0.1, p.z * 0.3), 6u) * 0.25;
    float s = sin((p.y / lambda + warp) * 6.2831853);
    return s * abs(s) * 0.6 + s * 0.4;
}

// fix-185: the morph marker is uv5.w = 1 AND uv6.w = -0.5 (G3TerrainRelief.Pack). uv5.w alone also matched every
// building, rock and prop mesh without the relief channels (Unity feeds the missing TEXCOORD2-6 inputs other data there),
// so their depth-normals pass wrote a morphed garbage normal: URP SSAO (source DepthNormals) read those surfaces as fully
// occluded - black wood with sky-reflection speckle, pale blue under fog (build 21b).
bool G3ReliefIsMorphed(float4 uv5, float4 uv6) { return _G3ReliefEye.w > 0 && abs(uv5.w - 1.0) < 0.01 && abs(uv6.w + 0.5) < 0.01; }

// Moves a relief vertex (object = world space) to the surface of the live distance; with wantNormal also the normal.
void G3ReliefMorph(inout float3 posOS, inout float3 normalOS, float4 uv2, float4 uv3, float4 uv4, float4 uv5, float4 uv6, bool wantNormal)
{
    [branch] if (!G3ReliefIsMorphed(uv5, uv6)) return;
    float sigmaV = uv3.w;
    [branch] if (sigmaV >= _G3ReliefBand.z - 1e-3) return;   // a far vertex: no fine octaves at any distance
    float d = distance(posOS, _G3ReliefEye.xyz);
    float sigma = max(clamp(_G3ReliefBand.x + _G3ReliefBand.y * d, _G3ReliefBand.x, _G3ReliefBand.z), sigmaV);
    float3 ng = uv2.xyz;
    float4 wA = G3ReliefBand4(_G3ReliefLambdaA, sigma);
    float3 wB = G3ReliefBand4(float4(_G3ReliefLambdaB.xyz, 0), sigma).xyz;
    float fine = dot(wA, uv4) + dot(wB, uv5.xyz);
    float strata = 0;
    float3 sgrad = 0;
    float ls = _G3ReliefBand.w;
    [branch] if (uv2.w > 0 && ls > 0)
    {
        float ws = G3ReliefBandWeight(ls, sigma);
        [branch] if (ws > 0)
        {
            float a = uv2.w * ws;
            float s0 = G3ReliefStrata(posOS, ls);
            strata = a * s0;
            [branch] if (wantNormal)
            {
                float3 t1 = normalize(cross(ng, abs(ng.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0)));
                float3 t2 = cross(ng, t1);
                float eps = max(0.04, sigmaV * 0.35);
                float s1 = G3ReliefStrata(posOS + eps * t1, ls), s2 = G3ReliefStrata(posOS + eps * t2, ls);
                sgrad = a * ((s1 - s0) / eps * t1 + (s2 - s0) / eps * t2);
            }
        }
    }
    float3 p0 = posOS;
    posOS = p0 + ng * (fine + strata);
    [branch] if (wantNormal)
    {
        // the fine octaves present now against the vertex's own spacing: the coarse normal blended towards the vertex normal
        float4 vA = G3ReliefBand4(_G3ReliefLambdaA, sigmaV);
        float3 vB = G3ReliefBand4(float4(_G3ReliefLambdaB.xyz, 0), sigmaV).xyz;
        float full = dot(vA, abs(uv4)) + dot(vB, abs(uv5.xyz));
        float now = dot(wA, abs(uv4)) + dot(wB, abs(uv5.xyz));
        float k = full > 1e-5 ? saturate(now / full) : 1.0;
        float3 n = normalize(lerp(uv6.xyz, normalOS, k));
        normalOS = normalize(n - sgrad);
    }
}

#endif
