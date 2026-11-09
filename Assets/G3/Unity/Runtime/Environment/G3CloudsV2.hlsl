// v2 volumetric cloud slab (Docs/remaster-world.md 10.1 / 11.3 / 16.4), shared by G3/Sky (the ray-marched sky) and
// Hidden/G3/Remaster/CloudShadow (the top-down cloud shadow map): the same density everywhere, anchored in the world
// (remaster-gi: the slab used to travel with the camera, so shadows on the ground could not match the clouds above).
// Positions are in km: xz world, y height above the camera (slab 1.5 .. 2.7 km).
#ifndef G3_CLOUDS_V2_INCLUDED
#define G3_CLOUDS_V2_INCLUDED

float4 _G3V2Clouds;        // v2 volumetric layer: x density (0 = off), y steps, z base coverage, w shape scale
float4 _G3V2Clouds2;       // remaster-look: x detail erosion (0 = v2 blobs as before), y multiple-scattering glow, z/w unused
float4 _G3CloudDrift;      // G3Wind (fix-146): xz = wind travel (s x unit dir, integrated), w = 1 when set

#define G3_CLOUD_BASE 1.5
#define G3_CLOUD_TOP 2.7

float G3CloudHash(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float G3CloudNoise3(float3 x)
{
    float3 i = floor(x), f = frac(x);
    float3 u = f * f * (3 - 2 * f);
    return lerp(lerp(lerp(G3CloudHash(i), G3CloudHash(i + float3(1, 0, 0)), u.x),
                     lerp(G3CloudHash(i + float3(0, 1, 0)), G3CloudHash(i + float3(1, 1, 0)), u.x), u.y),
                lerp(lerp(G3CloudHash(i + float3(0, 0, 1)), G3CloudHash(i + float3(1, 0, 1)), u.x),
                     lerp(G3CloudHash(i + float3(0, 1, 1)), G3CloudHash(i + float3(1, 1, 1)), u.x), u.y), u.z);
}

float G3CloudShape(float3 p, int octaves)
{
    float a = 0.55, n = 0, norm = 0;
    [loop] for (int k = 0; k < octaves; k++)
    {
        n += a * G3CloudNoise3(p);
        norm += a;
        p = p * 2.07 + float3(17.1, 3.3, 9.7);
        a *= 0.5;
    }
    return n / norm;
}

// wind travel of the clouds (s x unit direction): integrated by G3Wind, else a fixed direction x shader time (fix-146)
float2 G3CloudTravel()
{
    return _G3CloudDrift.w > 0 ? _G3CloudDrift.xz : float2(0.8, 0.6) * _Time.y;
}

float3 G3CloudDriftKm() { float2 t = G3CloudTravel(); return float3(t.x, 0, t.y) * 0.012; }   // km/s (12 m/s at 2 km)
float G3CloudScale() { return 0.42 / max(_G3V2Clouds.w, 0.05); }
float G3CloudProfile(float h) { return saturate(h * 5) * saturate((1 - h) * 2.2); }   // h 0..1 in the slab

// coarse density (no erosion; light taps and the shadow map), per km
float G3CloudCoarse(float3 pk, float cover, int octaves)
{
    float h = saturate((pk.y - G3_CLOUD_BASE) / (G3_CLOUD_TOP - G3_CLOUD_BASE));
    float s = G3CloudShape((pk + G3CloudDriftKm()) * G3CloudScale(), octaves);
    return saturate((s - (1 - cover)) / 0.2) * G3CloudProfile(h) * _G3V2Clouds.x * 6;
}

// full density with the remaster-look erosion (sky view rays), per km
float G3CloudDensity(float3 pk, float cover)
{
    float h = saturate((pk.y - G3_CLOUD_BASE) / (G3_CLOUD_TOP - G3_CLOUD_BASE));
    float profile = G3CloudProfile(h);
    float3 drift = G3CloudDriftKm();
    float scale = G3CloudScale();
    float3 q = (pk + drift) * scale;
    float shape = G3CloudShape(q, 4);
    [branch] if (_G3V2Clouds2.x > 0)
    {
        // flat bases, rounded tops (the threshold rises with height), edges eroded by a 5x finer noise (Schneider, HZD)
        float base = saturate((shape - (1 - cover) - h * 0.1) / 0.14);
        if (base * profile <= 1e-3) return 0;
        float detail = G3CloudShape(q * 5.3 - drift * scale * 2, 3);
        float erode = detail * _G3V2Clouds2.x * (1 - h * 0.5);
        return saturate((base - erode) / max(1 - erode, 0.1)) * profile * _G3V2Clouds.x * 6;
    }
    return saturate((shape - (1 - cover)) / 0.2) * profile * _G3V2Clouds.x * 6;
}

#endif
