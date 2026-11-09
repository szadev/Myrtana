// Remaster snow (Docs/remaster-world.md section 12) for G3/Remaster/WorldV2: which layers are snow, lasting + fresh snow on
// top faces in snow regions, wind ripples (sastrugi), the footprints (G3RemasterSnowPrints.hlsl: parallax, normals, rim), and the
// snow light terms (soft wrap / scattering, blue shade, sun glints). Globals set by G3SnowV2 (all zero = off; uniform
// branches only, no variants). Maths mirrored in SnowV2.cs (tested). Every fetch: explicit LOD / gradients from Frag.
#ifndef G3_REMASTER_SNOW_INCLUDED
#define G3_REMASTER_SNOW_INCLUDED

float4 _G3Snow;        // x on, y fresh snow 0..1, z lasting cover on top faces, w glint strength
float4 _G3Snow2;       // x wrap, y scattered light, z blue shade, w ripples
float4 _G3Snow3;       // x fresh cover amount, y glint density, zw wind direction (world x z, unit)
float4 _G3Snow4;       // x snow albedo lift (x its own luminance), y G3 pattern flattening (0..1), z albedo shoulder (white),
                       // w 1 = the look before fix-178 (glints, gloss, sky reflection; G3SnowV2.LegacyLook, A/B shots only)
// _G3SnowTrail, _G3SnowTex and the prints: G3RemasterSnowPrints.hlsl (all passes)
TEXTURE2D(_G3SnowAlbedo);
TEXTURE2D(_G3SnowScan);
TEXTURE2D(_G3SnowScanNormal);

// SnowV2.ScanWeight: the CC0 snow scan is the only bright, grey ground scan (G3's own snow images are as dark as rock)
float G3SnowScanWeight(float kind, float3 scanMean)
{
    if (kind < 0.5 || kind > 1.5) return 0;
    float l = G3V2Luma(scanMean);
    float mx = max(scanMean.r, max(scanMean.g, scanMean.b)), mn = min(scanMean.r, min(scanMean.g, scanMean.b));
    float sat = (mx - mn) / max(mx, 1e-3);
    return smoothstep(0.28, 0.33, l) * (1 - smoothstep(0.08, 0.14, sat));
}

// snow region weight (1 in Nordmar): one texel per world cell, bilinear (soft 250 m border); clamped inside the map
float G3SnowRegionAt(float3 pw, SamplerState smp)
{
    if (_G3SnowRegion.x <= 0) return 0;
    float2 uv = pw.xz * _G3SnowRegion.xy + _G3SnowRegion.zw;
    return SAMPLE_TEXTURE2D_LOD(_G3SnowRegionTex, smp, saturate(uv), 0).r;
}

// SnowV2.Cover
float G3SnowCover(float up, float amount, float bias)
{
    float thr = 0.95 - 0.5 * saturate(amount);
    return smoothstep(thr, thr + 0.15, up + bias * 0.3) * saturate(amount * 2);
}

// sastrugi / wind ripples: crests across the wind, elongated along it; height gradient by central differences (world XZ)
float G3SnowRippleH(float2 q)
{
    float h = G3V2Noise(float2(q.x * 0.8, q.y * 2.0)) * 0.65 + G3V2Noise(float2(q.x * 2.1, q.y * 4.6) + 7.1) * 0.35;
    return 1 - abs(h * 2 - 1);   // sharp crests
}

float2 G3SnowRippleGrad(float3 pw)
{
    float2 w = _G3Snow3.zw;
    float2x2 rot = float2x2(w.x, w.y, -w.y, w.x);   // world -> (along wind, across)
    float2 q = mul(rot, pw.xz);
    const float e = 0.06;
    float hx = G3SnowRippleH(q + float2(e, 0)) - G3SnowRippleH(q - float2(e, 0));
    float hy = G3SnowRippleH(q + float2(0, e)) - G3SnowRippleH(q - float2(0, e));
    float2 g = float2(hx, hy) / (2 * e) * 0.03;   // ~3 cm relief
    return mul(g, rot);                           // back to world x z
}

// trodden road snow: lumps (20-40 cm) and old, softened prints (ovals in a jittered 0.55 m grid, random heading and depth).
// x height (0..1, ~3 cm), yz its gradient (m / m) in world x z
float G3SnowTroddenH(float2 p)
{
    float2 g = p / 0.55 - 0.5;
    float2 id0 = floor(g);
    float d = 0;
    [unroll] for (int j = 0; j < 2; j++)
    [unroll] for (int k = 0; k < 2; k++)
    {
        float2 id = id0 + float2(k, j);
        float2 r = G3V2Hash2(id * 1.37 + 3.1), r2 = G3V2Hash2(id * 0.71 + 9.7);
        float2 c = (id + 0.5 + (r - 0.5) * 0.7) * 0.55;
        float ang = r2.x * 6.2832;
        float2 dir = float2(cos(ang), sin(ang));
        float2 q = p - c;
        float2 e = float2(dot(q, dir) / 0.15, dot(q, float2(-dir.y, dir.x)) / 0.065);
        d = max(d, (1 - smoothstep(0.45, 1.0, length(e))) * (0.35 + 0.65 * r2.y));
    }
    float lump = G3V2Noise(p * 2.3) * 0.6 + G3V2Noise(p * 6.1 + 2.7) * 0.4;
    return saturate(0.75 - d * 0.55 + (lump - 0.5) * 0.6);
}

float3 G3SnowTrodden(float2 p)
{
    const float e = 0.025;
    float h = G3SnowTroddenH(p);
    float hx = G3SnowTroddenH(p + float2(e, 0)) - G3SnowTroddenH(p - float2(e, 0));
    float hz = G3SnowTroddenH(p + float2(0, e)) - G3SnowTroddenH(p - float2(0, e));
    return float3(h, float2(hx, hz) / (2 * e) * 0.03);
}

struct G3SnowPrint
{
    float depth;    // 0..1
    float rim;
    float shadow;   // 0..1 the floor hidden from the sun by the print's own wall
    float3 n;       // perturbed normal
};

// the prints at a surface point (G3RemasterSnowPrints.hlsl: analytic shapes, the soft wall widened by the pixel footprint):
// parallax occlusion into the print (10 linear steps + 2 secant refinements, within 18 m and not where the geometry is
// already displaced), the normal from the depth gradient, self-shadow towards the sun
G3SnowPrint G3SnowTrailSample(float3 pw, float3 n, float3 viewWS, float camDist, float3 dpx, float3 dpy, float dense)
{
    G3SnowPrint o;
    o.depth = 0; o.rim = 0; o.shadow = 0; o.n = n;
    if (_G3SnowTrail.x <= 0 || camDist > 45) return o;
    float foot = max(max(length(dpx), length(dpy)), 1e-4);   // the legacy look's grain (largest footprint)
    float soft = _G3SnowTex.z + foot * 1.5;
    float2 t0 = G3SnowPrintsAt(pw.xz, pw.y, soft);
    if (t0.x + t0.y < 0.002) return o;
    float depthM = _G3SnowTex.w;
    float2 p = pw.xz;
    [branch] if (dense < 0.5 && camDist < 18 && t0.x > 0.002)
    {
        // parallax occlusion: the ray goes down from the snow surface until it meets the print's floor or the wall facing
        // the eye (the far wall hides part of the floor, the near wall shows): 10 linear steps, then 2 secant refinements
        // between the last step above the floor and the first below - one loop, one lookup site
        float2 dir = -viewWS.xz / max(viewWS.y, 0.3);
        float lo = 0, hLo = t0.x * depthM, hi = 0, hHi = 0;
        bool found = false;
        int refine = 0;
        float d = 0;
        [loop] for (int i = 1; i <= 12; i++)
        {
            if (!found) d = depthM * i / 10.0;
            else d = lerp(lo, hi, saturate((hLo - lo) / max((hLo - lo) - (hHi - hi), 1e-5)));
            float hq = G3SnowPrintsAt(pw.xz + dir * d, pw.y, soft).x * depthM;
            if (!found)
            {
                if (d >= hq) { found = true; hi = d; hHi = hq; }
                else { lo = d; hLo = hq; if (i == 10) { hi = d; hHi = hq; break; } }
            }
            else
            {
                if (d >= hq) { hi = d; hHi = hq; } else { lo = d; hLo = hq; }
                if (++refine == 2) break;
            }
        }
        float u = found ? saturate((hLo - lo) / max((hLo - lo) - (hHi - hi), 1e-5)) : 1;
        p = pw.xz + dir * lerp(lo, hi, u);
    }
    // the print at the parallax point, its depth gradient (4 taps) and one tap towards the sun: one loop, one lookup site
    float e = max(0.012, foot);
    float3 L = _MainLightPosition.xyz;
    float2 t = 0, sunDir = L.xz / max(L.y, 0.25);
    float hx = 0, hz = 0, sunSide = 0;
    [loop] for (int j = 0; j < 6; j++)
    {
        float2 off = j == 1 ? float2(e, 0) : j == 2 ? float2(-e, 0) : j == 3 ? float2(0, e) : j == 4 ? float2(0, -e) : 0;
        if (j == 5) off = sunDir * t.x * depthM;
        float2 r = G3SnowPrintsAt(p + off, pw.y, soft);
        if (j == 0) t = r;
        else if (j == 1) hx += r.x;
        else if (j == 2) hx -= r.x;
        else if (j == 3) hz += r.x;
        else if (j == 4) hz -= r.x;
        else sunSide = r.x;
    }
    float2 g = -float2(hx, hz) / (2 * e) * depthM;   // height = -depth: gradient in m / m
    float fade = 1 - smoothstep(30, 45, camDist);
    // self-shadow: the print's wall towards the sun hides its floor
    o.shadow = saturate((t.x - sunSide) * 2.5 + t.x * 0.15) * fade;
    o.depth = t.x * fade;
    o.rim = t.y * fade;
    o.n = normalize(n + float3(-g.x, 0, -g.y) * fade);
    return o;
}

// sun glints: stochastic microfacet cells (Zirr & Kaplanyan 2016; Deliot & Belcour 2023 for the pixel-footprint LOD idea):
// a world-space grid of grains on the surface plane, one random crystal facet per grain; a grain sparkles when its facet
// mirrors the sun into the eye. fix-178 (owner: "снег блестит, как будто он мокрый"): the facets were tilted at most ~19 deg
// from the shading normal, so every sparkle sat inside the surface's own mirror lobe - the glints clustered into a broad,
// patchy highlight that read as a wet sheen, and at 80x the sun they bloomed into blobs. Now (G3SnowGlint, mirrored in
// SnowV2.GlintLobe): facets spread over a wide cone (40 deg, crystals lie any way), a narrow lobe (2-5 deg), so sparkles
// are sparse (~0.4 % of grains) and spread over the whole sunlit snow instead of following the highlight; one grain = one
// pixel footprint (two power-of-two levels blended) lit as a whole (a sub-pixel dot averaged away under TAA: the first try
// showed none); peak radiance bounded (_G3Snow.w x 2.5 the sun's colour) so bloom keeps them points; faded out by 12-40 m.
// fix-194 (owner, build 23a: "dense bright dots in rows, wet again"): the grain ids are xz / (a few mm) ~ 1e5, and the sin hash
// (G3V2Hash2: sin(127 id) ~ sin(1e7)) has no fp32 precision left there - neighbouring grains got correlated facets, so whole
// rows of grains lit together (stripes along x, denser the farther from the world origin). Now an integer hash (pcg3d, Jarzynski
// & Olano 2020) of the grain id and the level; the grain is the GEOMETRIC MEAN pixel footprint (the larger one made grains
// several pixels wide at grazing views: horizontal dashes); fewer facets (density 0.8 -> 0.25, G3WorldLook.json).

// pcg3d (Jarzynski & Olano 2020, "Hash Functions for GPU Rendering", JCGT 9(3)); mirrored in SnowV2.GrainHash
uint3 G3SnowPcg3d(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}

// three uniform numbers in [0, 1) for grain (id) of level salt; ids are whole numbers (floor), negative ones wrap (two's complement)
float3 G3SnowGrainHash(float2 id, uint salt)
{
    uint3 h = G3SnowPcg3d(uint3(asuint((int2)id), salt));
    return float3(h >> 8u) * (1.0 / 16777216.0);
}

float G3SnowGlintLevel(float2 xz, float cell, float3 n, float3 H, float density)
{
    float2 g = xz / cell;
    float2 id = floor(g), f = frac(g);
    float3 r = G3SnowGrainHash(id, asuint(cell));   // the level (cell size) salts the hash: levels independent
    if (r.z > density) return 0;
    // facet tilt: uniform direction, tilt up to tan(40 deg) = 0.84 (square root: area-uniform over the cone's disc)
    float ang = r.x * 6.2831853, rad = sqrt(r.y) * 0.84;
    float3 t1 = normalize(abs(n.y) < 0.99 ? cross(n, float3(0, 1, 0)) : cross(n, float3(1, 0, 0)));
    float3 t2 = cross(n, t1);
    float3 m = normalize(n + (t1 * cos(ang) + t2 * sin(ang)) * rad);
    float lobe = smoothstep(0.99619, 0.99939, dot(m, H));   // 5 .. 2 deg
    float spot = saturate(1.5 - length(f - 0.5) * 2.2);       // the grain (~ one pixel), soft corners
    return lobe * spot;
}

// the glints before fix-178 (facets within ~19 deg of n, 80x the sun): A/B shots only (_G3Snow4.w)
float G3SnowGlintLegacyLevel(float2 xz, float cell, float3 n, float3 H, float density)
{
    float2 g = xz / cell;
    float2 id = floor(g), f = frac(g);
    float2 r = G3V2Hash2(id + 0.37), r2 = G3V2Hash2(id * 1.7 + 11.3);
    if (r2.x > density) return 0;
    float3 m = normalize(n + float3(r.x - 0.5, 0, r.y - 0.5) * 0.7);
    float lobe = smoothstep(0.9965, 0.9995, dot(m, H));
    float2 pos = 0.25 + float2(r2.y, frac(r2.y * 7.31)) * 0.5;
    float spot = saturate(1 - length(f - pos) * 2.6);
    return lobe * spot * spot;
}

float G3SnowGlint(float3 pw, float3 n, float3 H, float3 dpx, float3 dpy, float camDist)
{
    float result = 0;
    float foot = max(max(length(dpx), length(dpy)), 1e-4);   // the legacy look's grain (largest footprint)
    [branch] if (_G3Snow4.w > 0.5)
    {
        [branch] if (_G3Snow.w > 0 && camDist < 60)
        {
            float lv = log2(max(foot * 1.5, 0.008));
            float l0 = floor(lv), t = lv - l0;
            float c0 = exp2(l0);
            float g = lerp(G3SnowGlintLegacyLevel(pw.xz, c0, n, H, 0.06), G3SnowGlintLegacyLevel(pw.xz + 0.5, c0 * 2, n, H, 0.06), t);
            result = g * _G3Snow.w * 40 * (1 - smoothstep(20, 60, camDist));
        }
    }
    else [branch] if (_G3Snow.w > 0 && camDist < 40)
    {
        float lv = log2(max(sqrt(max(length(dpx) * length(dpy), 1e-8)), 0.004));   // fix-194: geometric mean footprint
        float l0 = floor(lv), t = lv - l0;
        float c0 = exp2(l0), c1 = c0 * 2;
        float dens = _G3Snow3.y;
        float g = lerp(G3SnowGlintLevel(pw.xz, c0, n, H, dens), G3SnowGlintLevel(pw.xz, c1, n, H, dens), t);
        result = g * _G3Snow.w * 2.5 * (1 - smoothstep(12, 40, camDist));
    }
    return result;
}

#endif
