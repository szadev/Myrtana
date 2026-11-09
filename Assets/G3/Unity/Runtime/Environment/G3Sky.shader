// Gothic 3 sky (eCSkydome_PS stand-in) driven by G3Environment from the weather curves:
//   zenith = SkyColor, horizon = HazeColor, a thin band at/below the horizon = FogColor (so fogged terrain meets
//   the sky without a seam), overcast blend towards CloudColor from CloudThickness, sun disc + glow in the
//   LightDiffuse colour, a pale moon and hashed stars at night. All colours arrive linear (SetVector).
// World remaster (global _G3RemasterSky, set by G3WorldRemasterLook; x = 0 when the remaster is off -> unchanged):
//   a procedural cloud layer (fbm on a plane projection, drifting with the G3 wind), coverage from CloudThickness
//   (_Overcast) + a base, lit in G3's own CloudColor / LightDiffuse / HazeColor, self-shadowed towards the sun, silver
//   lining near the sun; sun, moon and stars are hidden behind it. Docs/remaster-world.md section 7.
// v2 (global _G3V2Clouds, G3WorldV2; section 10.1): a 2.5D volumetric slab (1.5-2.7 km) ray-marched per sky pixel,
//   coverage from CloudThickness, two light taps towards the sun (Beer + powder, dual-lobe phase), ambient from G3's
//   zenith/horizon/cloud colours; replaces the r1 layer while on.
Shader "G3/Sky"
{
    Properties
    {
        _ZenithColor("Zenith (SkyColor, linear)", Vector) = (0.04, 0.19, 0.39, 1)
        _HorizonColor("Horizon (HazeColor, linear)", Vector) = (0.41, 0.6, 1, 1)
        _FogColor("Fog (FogColor, linear)", Vector) = (0.43, 0.45, 0.62, 1)
        _CloudColor("Clouds (CloudColor, linear)", Vector) = (0.52, 0.75, 0.79, 1)
        _SunColor("Sun (linear)", Vector) = (1, 1, 0.9, 1)
        _SunDir("Direction to the sun", Vector) = (0, 0.86, 0.5, 0)
        _MoonDir("Direction to the moon", Vector) = (0, -0.86, -0.5, 0)
        _Overcast("Overcast", Range(0, 1)) = 0
        _Night("Night", Range(0, 1)) = 0
        _Exposure("Exposure", Float) = 1
        _HorizonFog("Fog band height", Range(0.01, 0.5)) = 0.12
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ZenithColor, _HorizonColor, _FogColor, _CloudColor, _SunColor, _SunDir, _MoonDir;
                float _Overcast, _Night, _Exposure, _HorizonFog;
            CBUFFER_END
            float4 _G3RemasterSky;     // x cloud amount (0 = off), y base coverage, z drift speed, w cloud scale
            float4 _G3WindDirection;   // G3Wind: xyz direction, w strength
            float _G3WindTime;
            float4 _G3AtmoSky;        // atmo-1 (DesertAtmo.SkyGlobal): x sun aureole/disc (desert), y zenith deepening of the model sky, z cloud coverage change, w Mie g
            float4 _G3SkyGrade;       // grade-1 (section 27): x de-purple strength (DesertAtmo.DePurple; 0 = G3's dome as is)
            float4 _G3AtmoSkyPB;      // atmo-1 (DesertAtmo.SkyPBGlobal): x blend (0 = G3's dome), y Mie turbidity, z luminance scale, w soft cap (luminance)

            // ---- atmo-1: single-scattering Rayleigh + Mie sky (Nishita: 12 view steps, 4 light steps), mirrored by
            // DesertAtmo.SkyRadiance (C#, tested); relative radiance (sun 20), scaled by z to G3's sky luminance
            float AtmoSphereFar(float3 o, float3 d, float r)
            {
                float b = dot(o, d), c = dot(o, o) - r * r;
                float disc = b * b - c;
                return disc < 0 ? 0 : -b + sqrt(disc);
            }

            float3 AtmoSkyPB(float3 v, float3 sd)
            {
                const float Rg = 6360e3, Rt = 6420e3, HR = 8000, HM = 1200;
                const float3 bR = float3(5.8e-6, 13.5e-6, 33.1e-6);
                float bM = 21e-6 * _G3AtmoSkyPB.y;
                v.y = max(v.y, 0.0);
                v = normalize(v);
                float3 o = float3(0, Rg + 200, 0);
                float tmax = AtmoSphereFar(o, v, Rt);
                float dt = tmax / 12;
                float odR = 0, odM = 0;
                float3 sumR = 0, sumM = 0;
                [loop] for (int i = 0; i < 12; i++)
                {
                    float3 p = o + v * ((i + 0.5) * dt);
                    float hgt = length(p) - Rg;
                    float hr = exp(-hgt / HR) * dt, hm = exp(-hgt / HM) * dt;
                    odR += hr; odM += hm;
                    float tl = AtmoSphereFar(p, sd, Rt);
                    float dl = tl / 4;
                    float odRl = 0, odMl = 0;
                    bool lit = true;
                    [unroll] for (int j = 0; j < 4; j++)
                    {
                        float3 pl = p + sd * ((j + 0.5) * dl);
                        float hl = length(pl) - Rg;
                        if (hl < 0) lit = false;
                        odRl += exp(-max(hl, 0) / HR) * dl;
                        odMl += exp(-max(hl, 0) / HM) * dl;
                    }
                    if (lit)
                    {
                        float3 att = exp(-(bR * (odR + odRl) + bM * 1.1 * (odM + odMl)));
                        sumR += att * hr;
                        sumM += att * hm;
                    }
                }
                float mu = dot(v, sd);
                float pr = 3.0 / (16.0 * PI) * (1 + mu * mu);
                const float g = 0.76, g2 = g * g;
                float pm = 3.0 / (8.0 * PI) * ((1 - g2) * (1 + mu * mu)) / ((2 + g2) * pow(max(1 + g2 - 2 * g * mu, 1e-4), 1.5));
                return 20 * (sumR * bR * pr + sumM * bM * pm);
            }
            #include "Assets/G3/Unity/Runtime/Environment/G3CloudsV2.hlsl"   // _G3V2Clouds, _G3V2Clouds2, _G3CloudDrift, the slab density

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Noise3(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                float3 u = f * f * (3 - 2 * f);
                return lerp(lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), u.x),
                                 lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), u.x), u.y),
                            lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), u.x),
                                 lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), u.x), u.y), u.z);
            }

            // fbm over the cloud plane; z slowly evolves the shapes
            float CloudFbm(float2 p, float z, int octaves)
            {
                float a = 0.5, n = 0;
                float3 q = float3(p, z);
                for (int k = 0; k < octaves; k++)
                {
                    n += a * Noise3(q);
                    q = float3(q.xy * 2.03 + float2(13.7, 7.1), q.z * 1.7);
                    a *= 0.5;
                }
                return n / (1 - a * 2 + 1e-4);   // ~0..1
            }

            // cloud density (0..1) and lighting factor (0 shadowed .. 1 lit) along view direction v
            float2 Clouds(float3 v, float3 sd)
            {
                float h = v.y;
                float2 p = v.xz / (max(h, 0) + 0.12) * _G3RemasterSky.w;
                float t = (_G3WindTime > 0 ? _G3WindTime : _Time.y);
                p += G3CloudTravel() * _G3RemasterSky.z;
                float z = t * 0.004;
                float n = CloudFbm(p, z, 5);
                float cover = saturate(_G3RemasterSky.y + _Overcast * 0.6);
                float d = smoothstep(1 - cover, 1 - cover + 0.3, n);
                d *= smoothstep(-0.01, 0.16, h);
                // self shadow: density a little towards the sun
                float n2 = CloudFbm(p + normalize(sd.xz + 1e-4) * 0.09, z, 3);
                float lit = saturate(1 - (n2 - n * 0.9) * 3.5);
                return float2(d, lit);
            }

            // ---- v2 (Docs/remaster-world.md 10.1): 2.5D volumetric cloud slab, ray-marched, lit towards the sun -------
            // km: slab from 1.5 to 2.7 km above the camera, anchored in the world (remaster-gi: matches the cloud shadow
            // map on the ground); returns rgb in-scatter and the transmittance
            float4 VolumetricClouds(float3 v, float3 sd, float cover)
            {
                const float baseH = G3_CLOUD_BASE, topH = G3_CLOUD_TOP;
                float vy = max(v.y, 0.035);
                float t0 = baseH / vy, t1 = min(topH / vy, t0 + 9.0);      // slant path capped at 9 km
                float steps = _G3V2Clouds.y;
                float dt = (t1 - t0) / steps;
                float3 camKm = float3(_WorldSpaceCameraPos.x, 0, _WorldSpaceCameraPos.z) * 0.001;
                float day = 1 - _Night;
                float cs = dot(v, sd);
                float g1 = 0.6, g2 = -0.25;   // dual-lobe phase: silver lining + back glow
                float ph = lerp((1 - g2 * g2) / pow(1 + g2 * g2 - 2 * g2 * cs, 1.5), (1 - g1 * g1) / pow(1 + g1 * g1 - 2 * g1 * cs, 1.5), 0.7) * 0.25;
                float3 sunL = _SunColor.rgb * 2.2 * day * smoothstep(-0.05, 0.08, sd.y);
                float3 ambTop = lerp(_ZenithColor.rgb, _CloudColor.rgb, 0.5);
                float3 ambBot = lerp(_HorizonColor.rgb, _CloudColor.rgb * 0.6, 0.5) * 0.55;
                float jitter = frac(sin(dot(v.xz * 731.7, float2(12.9898, 78.233))) * 43758.5453);
                float T = 1;
                float3 L = 0;
                [loop] for (int k = 0; k < 64; k++)
                {
                    if (k >= (int)steps || T < 0.02) break;
                    float t = t0 + (k + jitter) * dt;
                    float3 p = v * t + camKm;
                    float h = saturate((p.y - baseH) / (topH - baseH));
                    float dens = G3CloudDensity(p, cover);
                    if (dens <= 1e-3) continue;
                    // light march towards the sun: two taps
                    float od = 0;
                    [unroll] for (int j = 1; j <= 2; j++)
                        od += G3CloudCoarse(p + sd * (0.18 * j * j), cover, 2) * 0.18 * j;
                    float beer = exp(-od * 1.6);
                    // multiple scattering (Wrenninge octaves, one term): thick cloud interiors keep a soft glow
                    beer = max(beer, exp(-od * 0.35) * _G3V2Clouds2.y);
                    float powder = 1 - exp(-dens * 2);
                    float3 lum = sunL * ph * beer * lerp(1, powder, 0.5) * 2 + lerp(ambBot, ambTop, h);
                    float tr = exp(-dens * dt);
                    L += T * lum * (1 - tr);
                    T *= tr;
                }
                // fade into the haze band at the horizon (distance)
                float far = smoothstep(0.035, 0.12, v.y);
                return float4(L * far, lerp(1, T, far));
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 v = normalize(i.dir);
                float h = v.y;

                // dome gradient: haze at the horizon, sky colour towards the zenith
                float3 sky = lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(saturate(h), 0.55));
                // fog band around / below the horizon (terrain fog ends in this colour)
                float band = 1 - smoothstep(-0.02, _HorizonFog, h);
                sky = lerp(sky, _FogColor.rgb, band);

                // overcast: clouds cover the dome, darker towards the horizon
                float3 clouds = _CloudColor.rgb * lerp(0.75, 1.0, saturate(h * 2));
                clouds = lerp(clouds, _FogColor.rgb, band);
                sky = lerp(sky, clouds, _Overcast);

                // atmo-1: the desert sky from single scattering (gradient, warm horizon at the sun, Mie glow), G3's luminance
                [branch] if (_G3AtmoSkyPB.x > 0)
                {
                    // the model's zenith deepened towards G3's (G3's dome has a ~10x horizon/zenith ratio, single scattering ~3x)
                    float3 pb = AtmoSkyPB(v, normalize(_SunDir.xyz)) * _G3AtmoSkyPB.z * (1 - _G3AtmoSky.y * pow(saturate(v.y), 0.8));
                    // soft cap of the Mie glow round the sun (w = a few times G3's sky luminance): no white-out
                    float pl = dot(pb, float3(0.2126, 0.7152, 0.0722));
                    pb *= 1 / (1 + pl / _G3AtmoSkyPB.w);
                    float b = _G3AtmoSkyPB.x * (1 - _Overcast) * smoothstep(-0.12, 0.0, h);
                    // grade-1: near the horizon the model's single-scattering Rayleigh is cyan (g >> r): there it takes G3's own
                    // horizon hue at its luminance (owner: a cyan / rainbow band at the horizon, white above it)
                    float lh = max(dot(_HorizonColor.rgb, float3(0.2126, 0.7152, 0.0722)), 1e-4);
                    float3 pbHue = _HorizonColor.rgb * (dot(pb, float3(0.2126, 0.7152, 0.0722)) / lh);
                    pb = lerp(pb, pbHue, 0.85 * (1 - smoothstep(0.0, 0.2, h)));
                    sky = lerp(sky, pb, b);
                }
                float clear = 1 - _Overcast * 0.85;
                float above = smoothstep(-0.03, 0.02, h);

                // remaster cloud layer: v2 volumetric slab, else the r1 fbm layer
                float cloudCover = 0;
                [branch] if (_G3V2Clouds.x > 0 && h > 0)
                {
                    float4 vc = VolumetricClouds(v, normalize(_SunDir.xyz), saturate(_G3V2Clouds.z + _Overcast * 0.55 + _G3AtmoSky.z));
                    float3 cc = lerp(vc.rgb, vc.rgb * 0.3 + _FogColor.rgb * (1 - vc.a) * 0.7, band * 0.7);
                    sky = sky * vc.a + cc;
                    cloudCover = 1 - vc.a;
                }
                else if (_G3RemasterSky.x > 0 && h > -0.02)
                {
                    float3 sdc = normalize(_SunDir.xyz);
                    float2 c = Clouds(v, sdc);
                    float dens = c.x * _G3RemasterSky.x;
                    float day = 1 - _Night;
                    float3 lit = lerp(_CloudColor.rgb, _SunColor.rgb, 0.25 * day) * lerp(1.0, 1.18, day);
                    float3 shade = lerp(_CloudColor.rgb * 0.62, _HorizonColor.rgb * 0.55, 0.35);
                    float3 cc = lerp(shade, lit, c.y);
                    float cs0 = saturate(dot(v, sdc));
                    cc += _SunColor.rgb * pow(cs0, 10) * 0.6 * (1 - c.x) * day;   // silver lining near the sun
                    cc = lerp(cc, _FogColor.rgb, band * 0.7);                     // clouds sink into the haze band
                    sky = lerp(sky, cc, dens);
                    cloudCover = dens;
                }
                clear *= 1 - cloudCover * 0.92;

                // grade-1: no lavender / magenta dome (the model sky's red-shifted twilight and G3's own dusk keys over the
                // remaster grade): the purple excess min(r, b) - g goes, red fully and blue half, luminance kept - lavender
                // turns blue-grey, pink turns salmon (DesertAtmo.DePurple, tested)
                [branch] if (_G3SkyGrade.x > 0)
                {
                    float l0 = dot(sky, float3(0.2126, 0.7152, 0.0722));
                    float pp = max(min(sky.r, sky.b) - sky.g, 0) * _G3SkyGrade.x;
                    sky.r -= pp; sky.b -= 0.5 * pp;
                    sky *= l0 / max(dot(sky, float3(0.2126, 0.7152, 0.0722)), 1e-5);
                }

                // sun disc + glow
                float3 sd = normalize(_SunDir.xyz);
                float cs = dot(v, sd);
                float sunVis = smoothstep(-0.1, 0.05, sd.y);
                float disc = smoothstep(0.9993, 0.9996, cs);
                float glow = pow(saturate(cs), 200) * 0.6 + pow(saturate(cs), 12) * 0.12;
                sky += _SunColor.rgb * (disc * 6 + glow) * clear * sunVis * above;
                // atmo-1: desert sun - Mie aureole of the dust (warm, wide) and a larger, hotter disc with limb darkening
                [branch] if (_G3AtmoSky.x > 0)
                {
                    float g = _G3AtmoSky.w;
                    float ph = (1 - g * g) / pow(max(1 + g * g - 2 * g * cs, 1e-4), 1.5);
                    float lowSun = 1 + 1.5 * (1 - smoothstep(0.0, 0.35, sd.y));
                    float3 dustTint = float3(1.0, 0.84, 0.62);
                    sky += _SunColor.rgb * dustTint * (ph * 0.0025 * lowSun) * _G3AtmoSky.x * clear * sunVis * smoothstep(-0.08, 0.02, h);
                    float limb = saturate((cs - 0.99952) / (1 - 0.99952));
                    float disc2 = smoothstep(0.99952, 0.99962, cs) * (0.55 + 0.45 * sqrt(limb));
                    sky += _SunColor.rgb * disc2 * 14 * _G3AtmoSky.x * clear * sunVis * above;
                }

                // moon + stars
                float3 md = normalize(_MoonDir.xyz);
                float cm = dot(v, md);
                float moon = smoothstep(0.99975, 0.9999, cm);
                float moonGlow = pow(saturate(cm), 300) * 0.25;
                sky += float3(0.75, 0.8, 0.9) * (moon * 1.2 + moonGlow) * _Night * clear * above;
                float3 sp = v * 260;
                float3 cell = floor(sp);
                float st = Hash(cell);
                float3 off = float3(Hash(cell + 17.3), Hash(cell + 41.7), Hash(cell + 73.1)) - 0.5;
                float sdist = length(frac(sp) - 0.5 - off * 0.6);
                float star = step(0.997, st) * smoothstep(0.14, 0.02, sdist) * (0.5 + 0.5 * frac(st * 113.1));
                sky += star * 0.6 * _Night * clear * saturate(h * 4);

                return half4(sky * _Exposure, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
