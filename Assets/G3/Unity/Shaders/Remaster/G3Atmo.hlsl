// atmo-1 (Docs/remaster-world.md section 23): desert atmosphere globals shared by Hidden/G3/Remaster/Volumetrics (dust
// and storm medium, Rayleigh distance blue) and the motes. Set every frame by G3DesertAtmo (all zero = off: the
// volumetric pass is exactly the v2 fog/shafts). Maths mirrored in DesertAtmo (tested).
#ifndef G3_ATMO_INCLUDED
#define G3_ATMO_INCLUDED

float4 _G3AtmoDust;     // x dust extinction at the base (1/m), y 1/scale height, z near-ground dust (1/m), w its 1/scale height
float4 _G3AtmoStorm;    // x storm extinction at the base (1/m), y 1/scale height, z swirl, w haze cap
float4 _G3AtmoTint;     // rgb dust single-scattering tint, w Henyey-Greenstein g
float4 _G3AtmoAmb;      // rgb sky light in-scattered by the dust (linear, tint and gain included), w Rayleigh scale
float4 _G3AtmoStormCol; // rgb storm dust in-scatter (G3's sandstorm fog colour, lit), w sun share in the storm
float4 _G3AtmoRay;      // rgb clear-sky colour for the Rayleigh in-scatter, w sky ray length cap (m)
float4 _G3AtmoHigh;     // x high thin dust (1/m) that reaches the mountains, y its 1/scale height, z/w haze fade-in distances (m)
float4 _G3AtmoCap;      // grade-1: x soft cap of the dust's in-scattered radiance (luminance; 0 = none): the haze never brighter than ~G3's horizon sky
float4 _G3AtmoOn;       // x 1 = on, y region weight when no region map, z sun in-scatter gain, w time (s)
float4 _G3SnowRegion;   // G3SnowV2 region map (G = desert): uv = xz * xy + zw; x = 0 before the map exists
TEXTURE2D(_G3SnowRegionTex);

float G3AtmoRegion(float2 xz)
{
    float2 uv = saturate(xz * _G3SnowRegion.xy + _G3SnowRegion.zw);
    float g = SAMPLE_TEXTURE2D_LOD(_G3SnowRegionTex, sampler_LinearClamp, uv, 0).g;
    return _G3SnowRegion.x > 0 ? g : _G3AtmoOn.y;
}

float G3AtmoPhase(float c, float g)
{
    float g2 = g * g;
    return (1 - g2) / (4 * PI * pow(max(1 + g2 - 2 * g * c, 1e-4), 1.5));
}

// mean of exp(-k max(h, 0)) between heights a and b (relative to the base) along a straight segment
float G3AtmoMeanExp(float k, float h0, float h1)
{
    float a = max(h0, 0), b = max(h1, 0), dh = b - a;
    return (abs(dh) < 1e-3 || k < 1e-7) ? exp(-k * a) : (exp(-k * a) - exp(-k * b)) / (k * dh);
}

float G3AtmoHash(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float G3AtmoNoise(float3 x)
{
    float3 i = floor(x), f = frac(x);
    float3 u = f * f * (3 - 2 * f);
    return lerp(lerp(lerp(G3AtmoHash(i), G3AtmoHash(i + float3(1, 0, 0)), u.x),
                     lerp(G3AtmoHash(i + float3(0, 1, 0)), G3AtmoHash(i + float3(1, 1, 0)), u.x), u.y),
                lerp(lerp(G3AtmoHash(i + float3(0, 0, 1)), G3AtmoHash(i + float3(1, 0, 1)), u.x),
                     lerp(G3AtmoHash(i + float3(0, 1, 1)), G3AtmoHash(i + float3(1, 1, 1)), u.x), u.y), u.z);
}

// swirling storm layers: two octaves advected with the wind at different speeds, the fine one domain-warped by the
// coarse one and stretched along the wind (streaks); mean ~1
float G3AtmoSwirl(float3 p, float3 windDir, float windSpeed, float t)
{
    float3 w = windDir * windSpeed;
    float3 q1 = (p - w * t) * float3(0.012, 0.03, 0.012) + float3(0, t * 0.03, 0);
    float n1 = G3AtmoNoise(q1);
    float3 pa = p - w * (t * 1.7);
    // stretch along the wind: project out most of the along-wind component
    float3 st = pa - windDir * dot(pa, windDir) * 0.75;
    float n2 = G3AtmoNoise(st * 0.05 + n1 * 2.2 + float3(3.1, 0, 7.7));
    float n = n1 * 0.55 + n2 * 0.45;
    // gust walls: a slow 250 m pattern racing with the wind - visibility drops and clears
    float gust = G3AtmoNoise((p - w * (t * 1.3)) * 0.004 + float3(11.3, 0, 5.1));
    return saturate((n - 0.5) * 2.4 + 0.5) * 2 * (0.35 + 1.3 * gust);
}

#endif
