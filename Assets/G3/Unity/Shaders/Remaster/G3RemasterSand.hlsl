// Remaster desert sand (Docs/remaster-world.md section 22) for G3/Remaster/WorldV2 (ForwardLit): Varant's sand ground layers
// (class per layer in _V2Snow.zw: 1 loose, 2 wet, 3 packed path; G3SandV2 / SandV2.LayerClass) get a grain detail (runtime
// tile: grain normals + albedo factor of mean 1, so G3's sand colour stays), dry matte roughness, wind ripples across the
// prevailing wind (sand-2: a ripple tile grown by the Swift-Hohenberg equation - crests that bend, fork and end - in
// two domain-warped fields, amplitude patches, parallax, soft self-shadows at low sun; faded on steep/lee slopes, layer edges,
// footprint and distance), a Journey-like sheen and backlit rim, the
// footprints of the shared print grid (G3RemasterSnowPrints.hlsl, sand prints = kind 4..6 on the sand clock) and a faint
// quartz sparkle in direct sun (the snow glint grains, sparser). All behind _G3Sand.x and the desert region (G of the region
// map): Myrtana's coast sand and Nordmar are untouched. Uniform branches only, no variants. Maths mirrored in SandV2.cs.
// Needs: G3RemasterWorldV2.hlsl (G3V2_SMP, G3V2PlaneNormal), G3RemasterSnow.hlsl (G3SnowPcg3d, region map, prints, glints).
#ifndef G3_REMASTER_SAND_INCLUDED
#define G3_REMASTER_SAND_INCLUDED

float4 _G3Sand;    // x on, y ripple normal strength, z grain strength, w glint strength
float4 _G3Sand2;   // xy prevailing wind (unit world x z), z ripple wavelength (m), w ripple height (m)
float4 _G3Sand3;   // x grain tile (m), y grain normal strength, z glint density, w CC0 coast-sand share kept in the desert
float4 _G3Sand4;   // smoothness of x loose, y wet, z packed sand; w unused
float4 _G3Sand5;   // x ripple tile (m), y peak slope (0 = ripples off), z ripple patch coverage, w self-shadow strength
float4 _G3Sand6;   // x broad sheen, y backlit rim sheen, z macro tint, w ripple parallax
float4 _G3Sand7;   // CC0 sand micro tile (22.8): x 1 / size (1/m, 0 = none), y normal strength, z albedo strength, w parallax height (m)
// sand-3 (22.10): the scan-based base material - three CC0 photoscans (Tools/Remaster/sand_scans.py --set, builder-imported):
// fine desert grain (hex-tiled), mid mottling (larger, turned), coarse gravelly lag sand with pebbles (patches, hollows, edges)
float4 _G3Sand8;   // x 1 / fine scan size (1/m), y 1 / mid scan size as sampled, z 1 / coarse scan size, w scan share (0 = no set)
float4 _G3Sand9;   // x coarse patch amount, y grade towards golden desert sand (0 = G3's hue), z brightness, w windward / crest lightening
float4 _G3Sand10;  // x diffuse contrast (detail normals exaggerated for the light), y backscatter, z scan normal strength, w scan parallax height (m)
float4 _G3Sand11;  // x mega-ripple strength, y mega-ripple scale (x wavelength), z ripple far fade end (m), w edge accumulation on meshes
float4 _G3Sand12;  // x macro value variation, y glint range (m), z POM (0/1), w scan colour contrast (1 = the scans own)
float4 _G3Sand13;  // fx-1: x mega-ripple strength kept at the camera, y extra cut there at low sun, z mega warp scale, w crest breaks
TEXTURE2D(_G3SandScanFineA); TEXTURE2D(_G3SandScanFineN);
TEXTURE2D(_G3SandScanMidA); TEXTURE2D(_G3SandScanMidN);
TEXTURE2D(_G3SandScanCoarseA); TEXTURE2D(_G3SandScanCoarseN);
TEXTURE2D(_G3SandMicro);
TEXTURE2D(_G3SandAlbedo);   // sand-3: G3's loose sand image (= the wisps' _G3SandAlbedo; mip 15 = its mean) for sand settled on meshes   // RGBA as the grain tile: Tools/Remaster/sand_scans.py (a high-passed CC0 sand scan), builder-imported
// _G3SandTrail (prints): G3RemasterSnowPrints.hlsl (all passes)
TEXTURE2D(_G3SandRipple);  // RGBA: slope along / across the wind (0.5 flat), height (0.5 mean): SandRipples.Generate, linear, mipped
TEXTURE2D(_G3SandGrain);   // RG normal xy, B height, A albedo factor (0.5 = x1): SandDetail.Generate, linear, mipped, repeat

// desert region weight (1 in Varant): the G channel of the region map (one texel per world cell, bilinear: soft 250 m border)
float G3SandDesertAt(float3 pw, SamplerState smp)
{
    if (_G3SnowRegion.x <= 0 || _G3Sand.x <= 0.5) return 0;
    float2 uv = pw.xz * _G3SnowRegion.xy + _G3SnowRegion.zw;
    return SAMPLE_TEXTURE2D_LOD(_G3SnowRegionTex, smp, saturate(uv), 0).g;
}

// per-class weights (x loose, y wet, z packed) of one layer from its class value (0 none, 1, 2, 3)
float3 G3SandClassWeights(float cls)
{
    return float3(abs(cls - 1) < 0.5 ? 1 : 0, abs(cls - 2) < 0.5 ? 1 : 0, abs(cls - 3) < 0.5 ? 1 : 0);
}

// SandV2.Hash / NoiseD: integer-hashed value noise with its gradient (no sin-hash precision loss far from the origin)
float G3SandHash(int2 c, uint salt)
{
    return (G3SnowPcg3d(uint3(asuint(c), salt)).x >> 8u) * (1.0 / 16777216.0);
}

float3 G3SandNoiseDHash(float2 p, uint salt)
{
    float2 i = floor(p), f = p - i;
    float2 u = f * f * (3 - 2 * f), du = 6 * f * (1 - f);
    int2 c = (int2)i;
    float a = G3SandHash(c, salt), b = G3SandHash(c + int2(1, 0), salt), cc = G3SandHash(c + int2(0, 1), salt), d = G3SandHash(c + int2(1, 1), salt);
    float k = a - b - cc + d;
    return float3(a + (b - a) * u.x + (cc - a) * u.y + k * u.x * u.y, ((b - a) + k * u.y) * du.x, ((cc - a) + k * u.x) * du.y);
}

// sand-3 (GPU): the same value noise from a periodic tile (SandV2.NoiseTile: NoiseTileCells cells, NoiseTileRes texels per
// cell, RGB = value, d/dx, d/dy per cell, RGBAHalf) - one bilinear fetch instead of four pcg3d hashes (the ripples, patches,
// warps and macro terms took ~60 hashes per sand pixel: ~0.6 ms at 1440p). Salts are offsets into the tile. Texel centres hold
// the exact noise, between them it is bilinear (the derivative a little smoother). Off (_G3SandNoiseP.x 0): the hash noise.
TEXTURE2D(_G3SandNoise);
float4 _G3SandNoiseP;   // x texels per cell, y 1 / texture size (texels), z cells per tile, w on
float3 G3SandNoiseD(float2 p, uint salt)
{
    float3 r;   // one exit (fxc: "potentially uninitialized")
    [branch] if (_G3SandNoiseP.w > 0.5)
    {
        float2 o = frac(float2(salt * 0.6180339887, salt * 0.7548776662)) * _G3SandNoiseP.z;
        float2 uv = ((p + o) * _G3SandNoiseP.x + 0.5) * _G3SandNoiseP.y;
        r = SAMPLE_TEXTURE2D_LOD(_G3SandNoise, G3V2_SMP, uv, 0).rgb;
    }
    else r = G3SandNoiseDHash(p, salt);
    return r;
}

// ---- wind ripples (sand-2, 22.4): the Swift-Hohenberg ripple tile (SandRipples.cs: sinuous crests that fork and end) as two
// domain-warped fields in the wind frame (B 1.37x larger, turned 9 deg) blended in ~6 m patches, amplitude patches, parallax and
// the ripples' own soft shadows at low sun. Slopes only (no albedo stripes). Mirrors SandV2.RippleWarp / RippleBlend / RipplePatch.

// SandV2.RippleWarp: the displacement (wA, wC) m; J = (dAa, dAc, dCa, dCc)
// fx-1: amplitudes x ws (the mega field bends and stretches more: orientation and wavelength vary, no grating)
float2 G3SandRippleWarpS(float2 r, float wave, uint salt, float ws, out float4 J)
{
    float l1 = wave * 36, a1 = wave * 8.2 * ws, l2 = wave * 15, a2 = wave * 1.9 * ws, l3 = wave * 50, a3 = wave * 7 * ws;
    float3 n1 = G3SandNoiseD(r / l1, salt);
    float3 n2 = G3SandNoiseD(r / l2 + float2(7.7, 0), salt + 5u);
    float3 n3 = G3SandNoiseD(r / l3 + float2(3.3, 0), salt + 9u);
    J = float4(n1.y * a1 / l1 + n2.y * a2 / l2, n1.z * a1 / l1 + n2.z * a2 / l2, n3.y * a3 / l3, n3.z * a3 / l3);
    return float2((n1.x - 0.5) * a1 + (n2.x - 0.5) * a2, (n3.x - 0.5) * a3);
}

float2 G3SandRippleWarp(float2 r, float wave, uint salt, out float4 J) { return G3SandRippleWarpS(r, wave, salt, 1, J); }

// fx-1: crest breaks - the amplitude drops in short stretches along the crests (noise ~4 wavelengths along a crest, ~1.4
// across): crests end and restart (terminations) instead of running on as a grating. q = wind frame (m), b = _G3Sand13.w
float G3SandCrestBreak(float2 q, float wave, uint salt, float b)
{
    float n = G3SandNoiseD(float2(q.x / (1.4 * wave), q.y / (4.2 * wave)), salt).x * 0.7
            + G3SandNoiseD(float2(q.x / (3.1 * wave), q.y / (9.5 * wave)) + float2(13.7, 0), salt + 2u).x * 0.3;
    return lerp(1, smoothstep(0.28, 0.6, n), saturate(b));
}

// fx-1: mega-ripple strength by camera distance and sun: _G3Sand13.x at the camera, full from 16 m; at low sun (< ~25 deg) a
// further cut within 12 m (grazing light made the near mega field a stripe grating). Mirrors SandV2.MegaNear.
float G3SandMegaNear(float dist, float sunUp)
{
    float k = lerp(_G3Sand13.x, 1, smoothstep(3, 16, dist));
    float low = 1 - smoothstep(0.18, 0.45, sunUp);
    return k * (1 - _G3Sand13.y * low * (1 - smoothstep(4, 12, dist)));
}

// SandV2.RipplePatch: amplitude 0.25..1 in 9 m / 23 m patches covering ~coverage of the open sand
float G3SandRipplePatch(float2 q, float coverage)
{
    float m = G3SandNoiseD(q / 9.0, 41u).x * 0.65 + G3SandNoiseD(q / 23.0, 43u).x * 0.35;
    float t = 1 - saturate(coverage);
    return 0.25 + 0.75 * smoothstep(t - 0.12, t + 0.12, m);
}

// One ripple field: uv in the tile; JR = d(r + w)/dq (the warp's Jacobian times the field's turn), M = JR / tile maps a
// wind-frame offset (m) to a uv offset (parallax, shadow taps, sample gradients)
struct G3SandRipField { float2 uv; float2x2 M; float2x2 JR; };

G3SandRipField G3SandRippleFieldS(float2 q, float wave, float tile, float2 cs, float2 ofs, uint salt, float ws)
{
    G3SandRipField f;
    float2 r = float2(cs.x * q.x - cs.y * q.y, cs.y * q.x + cs.x * q.y);
    float4 J;
    float2 w = G3SandRippleWarpS(r, wave, salt, ws, J);
    f.uv = (r + w) / tile + ofs;
    float2x2 Jm = float2x2(1 + J.x, J.y, J.z, 1 + J.w);
    float2x2 R = float2x2(cs.x, -cs.y, cs.y, cs.x);
    f.JR = mul(Jm, R);
    f.M = f.JR / tile;
    return f;
}

G3SandRipField G3SandRippleField(float2 q, float wave, float tile, float2 cs, float2 ofs, uint salt)
{
    return G3SandRippleFieldS(q, wave, tile, cs, ofs, salt, 1);
}

// The ripples at a world point: slopeW = world (d/dx, d/dz) slope (m/m) scaled by k; shadow = 0..1 of the sun (1 = lit).
// k = strength (class core x slope fade x patches x footprint x distance x json; the caller's)
void G3SandRipples(float3 pw, float3 viewWS, float3 dpx, float3 dpy, float k, float camDist, out float2 slopeW, out float shadow)
{
    slopeW = 0;
    shadow = 1;
    float2 wd = _G3Sand2.xy, wp = float2(-wd.y, wd.x);
    float wave = max(_G3Sand2.z, 0.03), H = _G3Sand2.w, tile = max(_G3Sand5.x, 0.1), peak = _G3Sand5.y;
    float2 q = float2(dot(pw.xz, wd), dot(pw.xz, wp));
    float2 dqx = float2(dot(dpx.xz, wd), dot(dpx.xz, wp)), dqy = float2(dot(dpy.xz, wd), dot(dpy.xz, wp));
    const float2 csB = float2(0.98769, 0.15643);   // cos, sin 9 deg (SandV2.FieldBAngleDeg)
    G3SandRipField fa = G3SandRippleField(q, wave, tile, float2(1, 0), float2(0.13, 0.71), 3u);
    G3SandRipField fb = G3SandRippleField(q, wave * 1.37, tile * 1.37, csB, float2(0.57, 0.29), 11u);
    float t = smoothstep(0.3, 0.7, G3SandNoiseD(q / 6.3 + float2(31, 0), 21u).x);   // SandV2.RippleBlend
    // parallax: sand-3 POM (8 linear steps + a secant through the blended height of both fields, within 12 m; the crests
    // hide the troughs behind them), else sand-2's one step towards the eye
    [branch] if (_G3Sand6.w > 0.01 && camDist < 12)
    {
        float2 vq = float2(dot(viewWS.xz, wd), dot(viewWS.xz, wp)) / max(viewWS.y, 0.2);
        float amp = H * k * _G3Sand6.w * (1 - smoothstep(6, 12, camDist));
        [branch] if (_G3Sand12.z > 0.5)
        {
            float2 dir = -vq;
            float tPrev = 0, dPrev = 0, tHit = 2 * amp;
            [loop] for (int s = 0; s <= 8; s++)
            {
                float tt = 2 * amp * s / 8.0;   // depth of the ray below the top (+amp)
                float2 o = dir * tt;
                float hs = (lerp(SAMPLE_TEXTURE2D_LOD(_G3SandRipple, G3V2_SMP, fa.uv + mul(fa.M, o), 0).b,
                                 SAMPLE_TEXTURE2D_LOD(_G3SandRipple, G3V2_SMP, fb.uv + mul(fb.M, o), 0).b, t) * 2 - 1) * amp;
                float dlt = (amp - tt) - hs;   // ray height above the surface
                if (dlt <= 0) { tHit = s == 0 ? 0 : lerp(tPrev, tt, dPrev / max(dPrev - dlt, 1e-6)); break; }
                tPrev = tt; dPrev = dlt;
            }
            float2 o = dir * tHit;
            fa.uv += mul(fa.M, o);
            fb.uv += mul(fb.M, o);
        }
        else
        {
            float ha = SAMPLE_TEXTURE2D_LOD(_G3SandRipple, G3V2_SMP, fa.uv, 0).b, hb = SAMPLE_TEXTURE2D_LOD(_G3SandRipple, G3V2_SMP, fb.uv, 0).b;
            float2 o = vq * (lerp(ha, hb, t) * 2 - 1) * amp;
            fa.uv += mul(fa.M, o);
            fb.uv += mul(fb.M, o);
        }
    }
    float4 sa = SAMPLE_TEXTURE2D_GRAD(_G3SandRipple, G3V2_SMP, fa.uv, mul(fa.M, dqx), mul(fa.M, dqy));
    float4 sb = SAMPLE_TEXTURE2D_GRAD(_G3SandRipple, G3V2_SMP, fb.uv, mul(fb.M, dqx), mul(fb.M, dqy));
    // tile slopes (per m of r + w) back to the wind frame: (JR)^T g
    float2 gq = lerp(mul(sa.rg * 2 - 1, fa.JR), mul(sb.rg * 2 - 1, fb.JR), t) * peak * k;
    gq *= G3SandCrestBreak(q, wave, 61u, _G3Sand13.w * 0.6);   // fx-1: crests end and restart
    slopeW = gq.x * wd + gq.y * wp;
    // self-shadow at low sun: 4 taps towards the sun over ~0.8 wavelength; soft by the height excess over the ray
    float3 L = _MainLightPosition.xyz;
    float lh = length(L.xz);
    [branch] if (_G3Sand5.w > 0.01 && L.y > 0.02 && L.y < 0.62 && camDist < 15 && k > 0.05)
    {
        float2 lq = float2(dot(L.xz, wd), dot(L.xz, wp)) / max(lh, 1e-4);
        float rise = L.y / max(lh, 1e-4);   // ray height gain per m
        float h0 = lerp(sa.b, sb.b, t);
        float occ = 0;
        [unroll] for (int i = 1; i <= 4; i++)
        {
            float d = wave * 0.2 * i;
            float2 o = lq * d;
            float hi = lerp(SAMPLE_TEXTURE2D_LOD(_G3SandRipple, G3V2_SMP, fa.uv + mul(fa.M, o), 1).b,
                            SAMPLE_TEXTURE2D_LOD(_G3SandRipple, G3V2_SMP, fb.uv + mul(fb.M, o), 1).b, t);
            occ = max(occ, ((hi - h0) * 2 * H * k - d * rise) / (d * 0.35 + H * 0.3));
        }
        shadow = 1 - saturate(occ) * saturate(_G3Sand5.w) * (1 - smoothstep(8, 15, camDist));
    }
}

// SandV2.SlopeFade: flat and windward ground keeps the ripples; steep (> ~18-34 deg) and lee faces lose them
float G3SandRippleSlope(float3 nGeo, float2 wind)
{
    float flat = smoothstep(0.83, 0.95, nGeo.y);
    float lee = dot(nGeo.xz, wind);
    return flat * (1 - 0.8 * smoothstep(0.05, 0.25, lee));
}

// SandV2.FootprintFade / DistanceFade / CoreFade
float G3SandFootFade(float foot, float wave) { return 1 - smoothstep(0.15, 0.35, foot / max(wave, 1e-4)); }
float G3SandDistFade(float d) { float e = _G3Sand11.z > 1 ? _G3Sand11.z : 28; return 1 - smoothstep(e * 0.36, e, d); }
float G3SandCoreFade(float loose) { return smoothstep(0.7, 0.98, loose); }

// The sun for the sand's own light terms (sheen, rim, glints, ripple self-shadow): one hardware-PCF tap of the shadow map
// instead of URP's soft filter (sand-2 GPU: the second soft-filtered main light cost ~0.3 ms on a sand-filled screen)
Light G3SandSun(float4 shadowCoord, float3 pw)
{
    Light l = GetMainLight();
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS) && !defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    half s = BEYOND_SHADOW_FAR(shadowCoord) ? 1.0h : SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture, sampler_LinearClampCompare, shadowCoord.xyz);
    s = LerpWhiteTo(s, GetMainLightShadowParams().x);
    l.shadowAttenuation = lerp(s, 1, GetMainLightShadowFade(pw));
#else
    l.shadowAttenuation = MainLightRealtimeShadow(shadowCoord);
#endif
    return l;
}

// Journey-like dry sand light (sand-2): a broad soft specular sheen (on the detail normal: ripples and grain texture it) and a
// backlit rim sheen at grazing views towards the sun; the added radiance per unit of sun colour x attenuation
float3 G3SandSheen(float3 n, float3 viewWS, float3 L, float3 albedo)
{
    float3 H = normalize(L + viewWS);
    float nh = saturate(dot(n, H)), nl = saturate(dot(n, L));
    float fres = 0.04 + 0.96 * pow(1 - saturate(dot(H, viewWS)), 5);
    // sand-3: a floor under the Fresnel (0.04 at normal incidence hid the sheen except at grazing views): sun-facing ripple
    // flanks catch a broad warm highlight as in current desert games
    float sheen = pow(nh, 10) * (0.3 + 0.7 * fres) * nl * 2.0 * _G3Sand6.x;
    float back = pow(saturate(dot(-viewWS, L)), 3);
    float rim = pow(1 - saturate(dot(n, viewWS)), 4) * back * saturate(nl + 0.25) * _G3Sand6.y;
    // sand-3: both tinted towards the sand's own hue (white highlights washed golden-hour sand out to beige)
    float3 hue = albedo / max(dot(albedo, float3(0.2126, 0.7152, 0.0722)), 1e-3);
    return sheen * lerp(1, hue, 0.45) + rim * albedo * 1.6;
}

// The grain detail at a world point: two samples of the runtime tile (the second 3.7x larger, rotated: no repetition),
// x albedo factor (mean 1), yz world-xz normal tilt; faded by distance (the mips average it out anyway)
float3 G3SandGrain(float3 pw, float3 dpx, float3 dpy, float camDist, float3 viewWS)
{
    float tile = max(_G3Sand3.x, 0.05);
    float k1 = 1 / tile, k2 = 1 / (tile * 3.7);
    const float2x2 r = float2x2(0.8, -0.6, 0.6, 0.8);
    float4 a = SAMPLE_TEXTURE2D_GRAD(_G3SandGrain, G3V2_SMP, pw.xz * k1, dpx.xz * k1, dpy.xz * k1);
    float2 u2 = mul(r, pw.xz) * k2 + 0.37;
    float4 b;
    [branch] if (_G3Sand7.x > 0)
    {
        // 22.8: the CC0 scan tile at its real size instead of the larger grain sample, with a one-step parallax by its own
        // height (the mm-cm relief shifts against the view; within 12 m)
        float km = _G3Sand7.x;
        float2 um = mul(r, pw.xz) * km + 0.37;
        float2 gx = mul(r, dpx.xz) * km, gy = mul(r, dpy.xz) * km;
        [branch] if (_G3Sand7.w > 0 && camDist < 12)
        {
            float hm = (SAMPLE_TEXTURE2D_GRAD(_G3SandMicro, G3V2_SMP, um, gx, gy).b * 2 - 1) * _G3Sand7.w * (1 - smoothstep(6, 12, camDist));
            um += mul(r, viewWS.xz / max(viewWS.y, 0.3)) * hm * km;
        }
        b = SAMPLE_TEXTURE2D_GRAD(_G3SandMicro, G3V2_SMP, um, gx, gy);
        b = float4(lerp(0.5, b.rg, _G3Sand7.y / 0.6), b.b, lerp(0.5, b.a, _G3Sand7.z / 0.6));   // so the * 0.6 / * 1.2 below give y / z
    }
    else b = SAMPLE_TEXTURE2D_GRAD(_G3SandGrain, G3V2_SMP, u2, mul(r, dpx.xz) * k2, mul(r, dpy.xz) * k2);
    float f = (1 + (a.a - 0.5) * 2 * _G3Sand.z) * (1 + (b.a - 0.5) * 1.2 * _G3Sand.z);
    float2 na = a.rg * 2 - 1, nb = mul(b.rg * 2 - 1, r);   // b back from its rotated frame
    float nk = _G3Sand3.y * (1 - smoothstep(4.0, 16.0, camDist));
    return float3(f, (na + nb * 0.6) * nk);
}

// sand glints: the snow grain glints (G3SnowGlintLevel, pcg3d grains, two footprint levels), sparser and dimmer, within 25 m
float G3SandGlint(float3 pw, float3 n, float3 H, float3 dpx, float3 dpy, float camDist)
{
    float result = 0;
    float range = _G3Sand12.y > 1 ? _G3Sand12.y : 25;
    [branch] if (_G3Sand.w > 0 && camDist < range)
    {
        float lv = log2(max(sqrt(max(length(dpx) * length(dpy), 1e-8)), 0.004));
        float l0 = floor(lv), t = lv - l0;
        float c0 = exp2(l0), c1 = c0 * 2;
        float dens = _G3Sand3.z;
        float g = lerp(G3SnowGlintLevel(pw.xz + 0.31, c0, n, H, dens), G3SnowGlintLevel(pw.xz + 0.31, c1, n, H, dens), t);
        result = g * _G3Sand.w * (1 - smoothstep(range * 0.32, range, camDist));
    }
    return result;
}

// ---- sand-3 (22.10) ------------------------------------------------------------------------------------------------------

// mega ripples: the same Swift-Hohenberg tile at _G3Sand11.y x the wavelength (0.4-0.5 m crests that read at 5-50 m, where the
// 10 cm ripples are below the pixel), its own warp and patches, turned 17 deg; world slope (d/dx, d/dz). k = strength (caller's)
float2 G3SandMegaRipples(float3 pw, float3 dpx, float3 dpy, float k)
{
    float2 wd = _G3Sand2.xy, wp = float2(-wd.y, wd.x);
    float sc = max(_G3Sand11.y, 1);
    float wave = max(_G3Sand2.z, 0.03) * sc, tile = max(_G3Sand5.x, 0.1) * sc, peak = _G3Sand5.y * 0.55;
    float2 q = float2(dot(pw.xz, wd), dot(pw.xz, wp));
    float2 dqx = float2(dot(dpx.xz, wd), dot(dpx.xz, wp)), dqy = float2(dot(dpy.xz, wd), dot(dpy.xz, wp));
    const float2 cs = float2(0.95630, 0.29237);   // 17 deg
    // fx-1: two warped fields (B 1.31x larger, turned 4 deg the other way) blended in ~9 m patches: where they cross over the
    // crests fork (Y-junctions); stronger warp (_G3Sand13.z) bends and stretches them; crest breaks end them; sparser patches
    float ws = _G3Sand13.z > 0 ? _G3Sand13.z : 1;
    const float2 csB = float2(0.99756, 0.06976);   // 4 deg
    G3SandRipField f = G3SandRippleFieldS(q, wave, tile, cs, float2(0.31, 0.83), 17u, ws);
    G3SandRipField fb = G3SandRippleFieldS(q, wave * 1.31, tile * 1.31, csB, float2(0.67, 0.12), 27u, ws);
    float4 sm = SAMPLE_TEXTURE2D_GRAD(_G3SandRipple, G3V2_SMP, f.uv, mul(f.M, dqx), mul(f.M, dqy));
    float4 sb = SAMPLE_TEXTURE2D_GRAD(_G3SandRipple, G3V2_SMP, fb.uv, mul(fb.M, dqx), mul(fb.M, dqy));
    float tb = smoothstep(0.32, 0.68, G3SandNoiseD(q / 9.1 + float2(17.3, 0), 53u).x);
    float patch = smoothstep(0.42, 0.75, G3SandNoiseD(q / 31.0 + float2(5.1, 0), 47u).x * 0.7 + G3SandNoiseD(q / 13.0, 49u).x * 0.3);
    float near = G3SandMegaNear(distance(pw, _WorldSpaceCameraPos), _MainLightPosition.y);
    float brk = G3SandCrestBreak(q, wave, 57u, _G3Sand13.w);
    float2 gq = lerp(mul(sm.rg * 2 - 1, f.JR), mul(sb.rg * 2 - 1, fb.JR), tb) * peak * k * (0.12 + 0.88 * patch) * near * brk;
    return gq.x * wd + gq.y * wp;
}

// a scan image's mean colour (its last mip)
#define G3SandScanMean(t) SAMPLE_TEXTURE2D_LOD(t, G3V2_SMP, float2(0.5, 0.5), 15).rgb

struct G3SandScanOut
{
    float3 ratio;   // colour factor round the scans' means (x the palette)
    float2 nxz;     // world x z normal tilt
    float h;        // 0..1 height (0.5 mean)
    float rough;    // scan roughness (perceptual)
    float ao;
    float coarse;   // weight of the coarse lag sand
};

// The scan base at a world point: fine hex-tiled grain (3 taps, height-weighted), mid mottling (larger, turned), coarse
// pebbly sand in patches (macro noise on flat interdune ground, plus coarseBias towards layer edges) blended by height (the
// pebbles poke through first). Normals as world x z tilts.
G3SandScanOut G3SandScan(float3 pw, float3 dpx, float3 dpy, float3 viewWS, float camDist, float3 nGeo, float coarseBias)
{
    G3SandScanOut o;
    float2 p = pw.xz;
    float kf = _G3Sand8.x;
    float2 gx = dpx.xz * kf, gy = dpy.xz * kf;
    float2 uv = p * kf;
    // one-step parallax by the fine scan's height within 8 m
    [branch] if (_G3Sand10.w > 0 && camDist < 8)
    {
        float hp = SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineA, G3V2_SMP, uv, gx, gy).a * 2 - 1;
        uv += viewWS.xz / max(viewWS.y, 0.3) * hp * _G3Sand10.w * kf * (1 - smoothstep(4, 8, camDist));
    }
    G3Hex hx = G3HexSetup(uv * 0.5);   // hex cells of two tiles
    float2 u1 = hx.uv1 * 2, u2 = hx.uv2 * 2, u3 = hx.uv3 * 2;
    float2 x1 = mul(gx, hx.r1), y1 = mul(gy, hx.r1), x2 = mul(gx, hx.r2), y2 = mul(gy, hx.r2), x3 = mul(gx, hx.r3), y3 = mul(gy, hx.r3);
    float4 a1 = SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineA, G3V2_SMP, u1, x1, y1);
    float4 a2 = SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineA, G3V2_SMP, u2, x2, y2);
    float4 a3 = SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineA, G3V2_SMP, u3, x3, y3);
    float3 w = G3HexWeights(hx, a1.aaa, a2.aaa, a3.aaa);
    float4 fa = a1 * w.x + a2 * w.y + a3 * w.z;
    float4 n1 = SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineN, G3V2_SMP, u1, x1, y1);
    float4 n2 = SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineN, G3V2_SMP, u2, x2, y2);
    float4 n3 = SAMPLE_TEXTURE2D_GRAD(_G3SandScanFineN, G3V2_SMP, u3, x3, y3);
    float2 fn = mul(hx.r1, n1.rg * 2 - 1) * w.x + mul(hx.r2, n2.rg * 2 - 1) * w.y + mul(hx.r3, n3.rg * 2 - 1) * w.z;
    float4 fm = n1 * w.x + n2 * w.y + n3 * w.z;
    float3 ratio = fa.rgb / max(G3SandScanMean(_G3SandScanFineA), 1e-3);
    // mid: mottling at its (larger) sampled size, turned 23 deg
    const float2x2 rm = float2x2(0.92050, -0.39073, 0.39073, 0.92050);
    float km = _G3Sand8.y;
    float2 um = mul(rm, p) * km + 0.29, mx = mul(rm, dpx.xz) * km, my = mul(rm, dpy.xz) * km;
    float4 ma = SAMPLE_TEXTURE2D_GRAD(_G3SandScanMidA, G3V2_SMP, um, mx, my);
    float4 mn = SAMPLE_TEXTURE2D_GRAD(_G3SandScanMidN, G3V2_SMP, um, mx, my);
    float3 mr = ma.rgb / max(G3SandScanMean(_G3SandScanMidA), 1e-3);
    ratio *= lerp(1, mr, 0.45);
    float2 nxz = fn + mul(mn.rg * 2 - 1, rm) * 0.25;   // its crusts and old prints only faintly
    float h = saturate(fa.a * 0.7 + ma.a * 0.3);
    float rough = fm.b, ao = fm.a * lerp(1, mn.a, 0.5);
    // coarse lag sand: patches (11 m / 37 m) on flat interdune ground, towards layer edges; height-blended
    float cm = G3SandNoiseD(p / 11.0, 81u).x * 0.55 + G3SandNoiseD(p / 37.0, 83u).x * 0.45;
    float cw = saturate(smoothstep(0.6, 0.8, cm) * smoothstep(0.9, 0.98, nGeo.y) + coarseBias) * _G3Sand9.x;
    o.coarse = 0;
    [branch] if (cw > 0.01)
    {
        const float2x2 rc = float2x2(0.32557, -0.94552, 0.94552, 0.32557);   // 71 deg
        float kc = _G3Sand8.z;
        float2 uc = mul(rc, p) * kc + 0.61, cx = mul(rc, dpx.xz) * kc, cy = mul(rc, dpy.xz) * kc;
        float4 ca = SAMPLE_TEXTURE2D_GRAD(_G3SandScanCoarseA, G3V2_SMP, uc, cx, cy);
        float4 cn = SAMPLE_TEXTURE2D_GRAD(_G3SandScanCoarseN, G3V2_SMP, uc, cx, cy);
        float wc = saturate((cw - 0.5) * 3 + (ca.a - h) * 2.5 + 0.5) * saturate(cw * 8);
        // pebbles keep their own hue round the coarse mean, a little darker (lag deposits)
        float3 cr = ca.rgb / max(G3SandScanMean(_G3SandScanCoarseA), 1e-3) * 0.82;
        ratio = lerp(ratio, cr, wc);
        nxz = lerp(nxz, mul(cn.rg * 2 - 1, rc) * 1.3, wc);
        h = lerp(h, ca.a, wc);
        rough = lerp(rough, cn.b, wc);
        ao = lerp(ao, cn.a, wc);
        o.coarse = wc;
    }
    // the scans' contrast round 1, their hue deviations (grey/dark mineral specks) at half: sunlit desert sand stays clean
    float lr = dot(ratio, float3(0.2126, 0.7152, 0.0722));
    o.ratio = max(1 + (lerp(lr.xxx, ratio, 0.5) - 1) * _G3Sand12.w, 0);
    o.nxz = nxz * _G3Sand10.z;
    o.h = h;
    o.rough = rough;
    o.ao = ao;
    return o;
}

// G3's sand colour graded towards a sunlit golden desert sand at the same luminance (_G3Sand9.y share), x brightness (_G3Sand9.z)
float3 G3SandGrade(float3 c)
{
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    const float3 gold = float3(1.60, 0.89, 0.38);   // linear (0.72, 0.40, 0.17) over its luminance
    return lerp(c, l * gold, saturate(_G3Sand9.y)) * _G3Sand9.z;
}

// sand-3 light: backscatter of a rough granular surface (opposition: brighter with the sun behind the eye), per sun colour
float3 G3SandBackscatter(float3 n, float3 viewWS, float3 L, float3 albedo)
{
    float c = saturate(dot(viewWS, L));
    float nl = saturate(dot(n, L));
    return albedo * nl * (0.35 * c * c + 0.65 * pow(c, 12)) * _G3Sand10.y;
}

#endif
