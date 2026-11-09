using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free maths of the world remaster v2 look (Docs/remaster-world.md section 10), mirrored by the shaders and
    /// used by <c>G3WorldV2</c>: sun/ambient balance on top of G3's day cycle, an air-mass sun colour, shadow cascade
    /// splits, height fog and in-scatter, the highlight shoulder, the per-class CC0 material table, tree species and the
    /// frame budget. Settings come from <see cref="WorldRemasterLook.Current"/>.
    /// </summary>
    public static class WorldV2
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        /// <summary>True when the v2 layer and the given feature are on (the world remaster switch itself is checked by the caller).</summary>
        public static bool On(bool feature) => S.v2 && feature;

        // ------------------------------------------------------------------ lighting

        /// <summary>
        /// Sun / ambient multipliers for G3's overcast (0 clear .. 1 fully overcast, G3Environment's value): clear weather gets
        /// the v2 contrast (stronger sun, less flat ambient), overcast keeps G3's soft light (eased with overcast^0.5, so
        /// a light haze already softens it). Returns (sun multiplier, ambient multiplier, sky fill fraction).
        /// </summary>
        public static (float sun, float ambient, float fill) LightBalance(float overcast)
        {
            if (!On(S.v2Sun)) return (1, 1, 0);
            float k = 1 - (float)Math.Sqrt(Clamp01(overcast));
            float sun = 1 + (S.v2SunIntensity - 1) * k;
            float amb = 1 + (S.v2AmbientScale - 1) * k;
            float fill = S.v2SkyFill * k * Math.Max(0f, 1 - amb);   // fix-169: none when the ambient is raised (v2AmbientScale > 1)
            return (sun, amb, fill);
        }

        /// <summary>
        /// fix-255: the overcast light's colour. G3's Rainy / Foggy keys are cyan (ambient (0, 0.54, 0.71), fog (0.16, 0.47,
        /// 0.54), sky (0.33, 0.60, 0.64), sun (0.43, 0.73, 0.86)); G3Environment's saturation caps keep ~0.4 of it and the
        /// remaster's sky light, exposure and fog turned the Myrtana rain forest teal. Under a closed cloud deck the light is
        /// a neutral-cool grey (the eye white-balances to it): the colours are pulled towards <see cref="RainGrey"/>,
        /// luminance kept, by <c>v2RainNeutral</c> x the overcast (full from 0.35; Sunny's thin clouds give 0).
        /// </summary>
        public static float RainNeutralK(float overcast) => On(S.v2Sun) ? Clamp01(S.v2RainNeutral) * Clamp01(overcast / 0.35f) : 0f;

        /// <summary>fix-255: the neutral-cool overcast chroma (linear, luminance 1): a 7000 K-ish grey.</summary>
        public static readonly float[] RainGrey = { 0.93f / 0.975868f, 0.98f / 0.975868f, 1.07f / 0.975868f };

        /// <summary>fix-255: linear colour pulled towards <see cref="RainGrey"/> by <paramref name="k"/> (0..1), luminance kept.</summary>
        public static float[] RainNeutral(float r, float g, float b, float k)
        {
            float l = Luma(r, g, b);
            k = Clamp01(k);
            if (l <= 1e-6f || k <= 0) return new[] { r, g, b };
            return new[] { r + (l * RainGrey[0] - r) * k, g + (l * RainGrey[1] - g) * k, b + (l * RainGrey[2] - b) * k };
        }

        /// <summary>
        /// fix-255: per-channel factors (von Kries) that move the ambient probe's mean colour (its DC term, linear rgb) to
        /// <see cref="RainNeutral"/> - applied to every SH coefficient, so the probe's directions keep their shape. Clamped
        /// 0.2..5 (G3's cyan ambient has a red of ~0).
        /// </summary>
        public static float[] RainProbeScale(float r, float g, float b, float k)
        {
            var t = RainNeutral(Math.Max(r, 0), Math.Max(g, 0), Math.Max(b, 0), k);
            float[] c = { r, g, b };
            var o = new float[3];
            for (int i = 0; i < 3; i++) o[i] = c[i] > 1e-5f ? Math.Max(0.2f, Math.Min(5f, t[i] / c[i])) : 1f;
            return o;
        }

        /// <summary>Sun shadow strength: G3's (shadowDensity × 1.25) pushed towards v2ShadowStrength in clear weather.</summary>
        public static float ShadowStrength(float g3Strength, float overcast)
        {
            if (!On(S.v2Sun)) return g3Strength;
            float k = 1 - (float)Math.Sqrt(Clamp01(overcast));
            return g3Strength + (Math.Max(g3Strength, S.v2ShadowStrength) - g3Strength) * k;   // runner-31: 0.95 crushed the forest shade
        }

        /// <summary>
        /// Relative direct-sun transmittance (R, G, B at 680/550/440 nm) through the atmosphere for a sun
        /// <paramref name="elevationDeg"/> above the horizon: Kasten-Young air mass, Rayleigh (λ^-4.08) + rural aerosol
        /// (Ångström β 0.04, α 1.3), normalised to the zenith so noon is (1, 1, 1).
        /// </summary>
        public static float[] AirMassTransmittance(float elevationDeg)
        {
            double[] lambda = { 0.68, 0.55, 0.44 };
            double am = AirMass(elevationDeg), am0 = AirMass(90);
            var o = new float[3];
            for (int i = 0; i < 3; i++)
            {
                double tau = 0.008735 * Math.Pow(lambda[i], -4.08) + 0.04 * Math.Pow(lambda[i], -1.3);
                o[i] = (float)Math.Exp(-(am - am0) * tau);
            }
            return o;
        }

        /// <summary>Kasten and Young (1989) relative optical air mass for an elevation in degrees (clamped at -1°).</summary>
        public static double AirMass(float elevationDeg)
        {
            double e = Math.Max(elevationDeg, -1.0);
            double z = 90 - e;
            return 1 / (Math.Cos(z * Math.PI / 180) + 0.50572 * Math.Pow(96.07995 - z, -1.6364));
        }

        /// <summary>
        /// G3's sun colour (gamma, as the weather keys give it) with its hue pulled towards the air-mass colour by
        /// v2SunColor; the luminance stays G3's (the intensity is LightBalance's job).
        /// </summary>
        public static float[] SunColor(float r, float g, float b, float elevationDeg)
        {
            if (!On(S.v2Sun) || S.v2SunColor <= 0) return new[] { r, g, b };
            var t = AirMassTransmittance(elevationDeg);
            float lt = Luma(t[0], t[1], t[2]);
            float l = Luma(r, g, b);
            var o = new float[3];
            float[] c = { r, g, b };
            for (int i = 0; i < 3; i++)
            {
                float target = l * t[i] / Math.Max(lt, 1e-4f);   // air-mass hue at G3's luminance
                o[i] = Math.Max(c[i] + (target - c[i]) * S.v2SunColor, 0);
            }
            float lo = Luma(o[0], o[1], o[2]);
            if (lo > 1e-6f) for (int i = 0; i < 3; i++) o[i] *= l / lo;
            return o;
        }

        // ------------------------------------------------------------------ shadows

        /// <summary>
        /// Cascade split fractions of the shadow distance (count - 1 values, increasing, in (0, 1)) by the practical split
        /// scheme: lerp(uniform, logarithmic, lambda) between <paramref name="near"/> and <paramref name="far"/>.
        /// </summary>
        public static float[] CascadeSplits(float near, float far, int count, float lambda)
        {
            count = Math.Max(1, Math.Min(4, count));
            near = Math.Max(near, 0.01f);
            far = Math.Max(far, near + 1);
            var o = new float[count - 1];
            for (int i = 1; i < count; i++)
            {
                double p = (double)i / count;
                double log = near * Math.Pow(far / near, p);
                double uni = near + (far - near) * p;
                o[i - 1] = (float)((lambda * log + (1 - lambda) * uni) / far);
            }
            return o;
        }

        // ------------------------------------------------------------------ fog

        /// <summary>
        /// Fogginess of G3's current weather from its (Unity) linear fog end in metres: 0 at ≥ 1200 m (clear Myrtana),
        /// 1 at ≤ 150 m (Foggy).
        /// </summary>
        public static float Fogginess(float fogEndMetres) => Clamp01((1200 - fogEndMetres) / (1200 - 150));

        /// <summary>Height fog density at the base for the weather (per metre).</summary>
        public static float FogDensity(float fogEndMetres) =>
            On(S.v2Fog) ? S.v2FogDensity * (1 + S.v2FogWeather * Fogginess(fogEndMetres)) : 0;

        /// <summary>
        /// Optical depth of exponential height fog density·exp(-falloff·(h − base)) along a straight segment of length
        /// <paramref name="distance"/> from height h0 to h1 (relative to the base; below the base the density is capped at
        /// the base value). Mirrors Hidden/G3/Remaster/Volumetrics.
        /// </summary>
        public static float FogOpticalDepth(float distance, float h0, float h1, float density, float falloff)
        {
            if (distance <= 0 || density <= 0) return 0;
            float a = Math.Max(h0, 0), b = Math.Max(h1, 0);
            float dh = b - a;
            float mean = Math.Abs(dh) < 1e-3f || falloff < 1e-6f
                ? (float)Math.Exp(-falloff * a)
                : (float)((Math.Exp(-falloff * a) - Math.Exp(-falloff * b)) / (falloff * dh));
            return density * distance * mean;
        }

        /// <summary>Fog amount (1 − transmittance), capped by v2FogMax.</summary>
        public static float FogAmount(float distance, float h0, float h1, float density) =>
            Math.Min(1 - (float)Math.Exp(-FogOpticalDepth(distance, h0, h1, density, S.v2FogFalloff)), S.v2FogMax);

        /// <summary>Henyey-Greenstein phase function (1/sr) for the cosine between view and light direction.</summary>
        public static float PhaseHG(float cosTheta, float g)
        {
            float g2 = g * g;
            double d = 1 + g2 - 2 * g * cosTheta;
            return (float)((1 - g2) / (4 * Math.PI * Math.Pow(Math.Max(d, 1e-6), 1.5)));
        }

        // ------------------------------------------------------------------ tone

        /// <summary>
        /// Highlight shoulder (extended Reinhard above the knee): identity up to <paramref name="knee"/>, C1-continuous,
        /// monotonic, <paramref name="white"/> → 1. Applied to the max channel in the grade pass (hue kept).
        /// </summary>
        public static float Shoulder(float x, float knee, float white)
        {
            if (x <= knee) return x;
            float a = 1 - knee;
            float t = (x - knee) / a, T = Math.Max((white - knee) / a, 1);
            float g = t * (1 + t / (T * T)) / (1 + t);
            return Math.Min(knee + a * g, 1);
        }

        /// <summary>The v2 grade globals: x knee (0 = shoulder off), y white, z highlight path to white (<see cref="WorldV2Light.DesatAmount"/>), w unused.</summary>
        public static float[] ShoulderParams => On(S.v2Shoulder) ? new[] { S.v2ShoulderKnee, S.v2ShoulderWhite, S.v2HighlightDesat, 0f } : new float[4];

        // ------------------------------------------------------------------ clouds

        /// <summary>Cloud coverage of the v2 cloud layer for G3's overcast (0..1).</summary>
        public static float CloudCoverage(float overcast) => Clamp01(S.v2CloudCoverage + Clamp01(overcast) * 0.55f);

        // ------------------------------------------------------------------ shader globals (all zero when v2 is off)

        /// <summary>_G3V2Surface: PBR, stochastic tiling, parallax depth (0 = off), colour match.</summary>
        public static float[] SurfaceGlobal => S.v2
            ? new[] { S.v2Pbr ? 1f : 0f, S.v2Stochastic ? 1f : 0f, S.v2Parallax ? S.v2ParallaxScale : 0f, S.v2ColorMatch }
            : new float[4];

        /// <summary>_G3V2Surface2: terrain triplanar / steep rock push, G3 pattern, height blend band, rock triplanar detail.</summary>
        public static float[] Surface2Global => S.v2
            ? new[] { S.v2TerrainTriplanar ? 1f : 0f, S.v2G3Pattern, S.v2HeightBlend, S.v2RockTriplanar ? 1f : 0f }
            : new float[4];

        /// <summary>_G3V2Weather: wetness (0..1), base dirt, edge wear, moss.</summary>
        public static float[] WeatherGlobal(float wetness) => S.v2
            ? new[] { S.v2Wetness ? Clamp01(wetness) : 0f, S.v2BaseDirt ? 1f : 0f, S.v2EdgeWear ? 1f : 0f, S.v2Moss ? S.v2RockMoss : 0f }
            : new float[4];

        /// <summary>_G3V2Clouds: density (0 = off: G3/Sky falls back to the r1 layer), steps, base coverage, shape scale.</summary>
        public static float[] CloudsGlobal => On(S.v2Clouds)
            ? new[] { S.v2CloudDensity, (float)S.v2CloudSteps, S.v2CloudCoverage, S.v2CloudScale }
            : new float[4];

        /// <summary>_G3V2Clouds2 (remaster-look): x edge erosion, y multiple-scattering glow (zero when the clouds are off).</summary>
        public static float[] Clouds2Global => On(S.v2Clouds) ? new[] { S.v2CloudDetail, S.v2CloudGlow, 0f, 0f } : new float[4];

        /// <summary>_G3V2Debug: debug mode (v2DebugMode, or 1 for the old v2Debug kinds flag).</summary>
        public static float[] DebugGlobal => S.v2 ? new[] { (float)(S.v2DebugMode > 0 ? S.v2DebugMode : S.v2Debug ? 1 : 0), 0f, 0f, 0f } : new float[4];

        /// <summary>_G3V2Far: detail fade start, end (m), detail kept at range, ground gloss cap (0 = none).</summary>
        public static float[] FarGlobal => S.v2 ? new[] { S.v2DetailFadeStart, S.v2DetailFadeEnd, S.v2DetailFar, S.v2GroundGloss } : new[] { 1e6f, 2e6f, 1f, 0f };

        /// <summary>Detail kept (0..1) at a distance, mirrors farK in G3/Remaster/WorldV2.</summary>
        public static float DetailAt(float distance)
        {
            float t = Clamp01((distance - S.v2DetailFadeStart) / Math.Max(S.v2DetailFadeEnd - S.v2DetailFadeStart, 1));
            float farK = t * t * (3 - 2 * t) * (1 - S.v2DetailFar);
            return 1 - farK;
        }

        /// <summary>_G3V2Color: hue lock of ground/surface scans, hue lock of rock scans (1 = luminance only on G3's mean), G3 detail maps on ground, G3 fine colour on ground.</summary>
        public static float[] ColorGlobal => S.v2 ? new[] { S.v2HueLock, S.v2RockHueLock, S.v2G3Detail, S.v2G3Fine } : new float[4];

        /// <summary>_G3V2Color2 (ai-rocks): x hue lock of the terrain ground, y saturation of G3's own ground hue, z AI close-up grain contrast, w its world repeat (m).</summary>
        public static float[] Color2Global => S.v2 ? new[] { S.v2GroundHueLock, S.v2GroundSaturation, S.v2AiMicro, S.v2AiMicroRepeat } : new float[4];

        /// <summary>
        /// AI rock saturation of a world region (G3WorldManifest region: Myrtana / Nordmar / Varant; unknown = Myrtana): G3's
        /// own lighting gives each region's rock its tint, URP's does not, and one constant fit only Myrtana (ai-rocks pass 6).
        /// </summary>
        public static float AiSaturationFor(string region)
        {
            if (region != null && region.IndexOf("nordmar", StringComparison.OrdinalIgnoreCase) >= 0) return S.v2AiSatNordmar;
            if (region != null && region.IndexOf("varant", StringComparison.OrdinalIgnoreCase) >= 0) return S.v2AiSatVarant;
            return S.v2AiSatMyrtana;
        }

        /// <summary>Region saturation moving towards its target (exponential, ~3 s): no pop when the streamer's region changes.</summary>
        public static float AiSaturationStep(float current, float target, float dt)
            => current <= 0 ? target : current + (target - current) * (1 - (float)Math.Exp(-Math.Max(dt, 0) / 3f));

        /// <summary>_G3V2Ai: x AI rock saturation of the current region (0 = v2 off: the shader uses the material's layer.y).</summary>
        public static float[] AiGlobal(float saturation) => S.v2 ? new[] { saturation, 0f, 0f, 0f } : new float[4];

        /// <summary>
        /// Colour of one scan texel on the G3 palette (mirrors G3V2Match): the per-channel ratio match (amount), then the
        /// hue lock: G3's mean colour × the texel's relative luminance (lock 1 = the scan only brings light/dark detail).
        /// </summary>
        public static float[] MatchColor(float[] cc0, float[] cc0Mean, float[] g3Mean, float amount, float hueLock)
        {
            var o = new float[3];
            float lc = Luma(cc0[0], cc0[1], cc0[2]), lm = Math.Max(Luma(cc0Mean[0], cc0Mean[1], cc0Mean[2]), 0.01f);
            for (int i = 0; i < 3; i++)
            {
                float ratio = Math.Min(Math.Max(g3Mean[i] / Math.Max(cc0Mean[i], 0.01f), 0.2f), 5f);
                float m = cc0[i] + (cc0[i] * ratio - cc0[i]) * amount;
                float locked = g3Mean[i] * lc / lm;
                o[i] = m + (locked - m) * hueLock;
            }
            return o;
        }

        /// <summary>_G3V2Foliage: on, crown AO, mip coverage boost, grass translucency.</summary>
        public static float[] FoliageGlobal => On(S.v2Foliage)
            ? new[] { 1f, S.v2FoliageAO, S.v2FoliageCoverage, S.v2GrassTranslucency }
            : new float[4];

        /// <summary>
        /// _G3V2Foliage for the parts that are on (fix-71): x/y/z (crown AO, mip coverage boost; read by G3/SpeedTree) only with
        /// the trees part, w (grass translucency; G3/Remaster/GrassV2) only with the grass part.
        /// </summary>
        public static float[] FoliageGlobalFor(bool trees, bool grass)
        {
            var f = FoliageGlobal;
            if (!trees) { f[0] = 0; f[1] = 0; f[2] = 0; }
            if (!grass) f[3] = 0;
            return f;
        }

        /// <summary>True for G3 weather states that rain or storm (Rainy, Storm, Thunderstorm...; snow and sandstorms do not wet).</summary>
        public static bool IsRainy(string state) =>
            !string.IsNullOrEmpty(state) && state.IndexOf("snow", StringComparison.OrdinalIgnoreCase) < 0 &&   // remaster-snow: Snowstorm(Nordmar) does not wet
            state.IndexOf("sand", StringComparison.OrdinalIgnoreCase) < 0 &&   // fix-236: Sandstormy(Varant) is dry
            (state.IndexOf("rain", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("storm", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// fix-239: rain falling at full strength wets fully: the target is the rain intensity / this (G3's precipitation is
        /// (w - 0.5) * 2 of the dominant state, so 0.25 = the rainy state at 62.5 % of the mix).
        /// </summary>
        public const float WetFullAtRain = 0.25f;

        /// <summary>Wetness target of a rain intensity (0..1, G3's (w - 0.5) * 2 of the dominant rainy state).</summary>
        public static float WetOfRain(float rain) => Clamp01(rain / WetFullAtRain);

        /// <summary>
        /// Rain intensity of G3Environment's two-state blend, as G3 shows precipitation (eCWeatherAdmin: only the state above
        /// 50 % falls, at (w - 0.5) * 2) - and only a rainy one wets.
        /// </summary>
        public static float RainOf(string state, string blendState, float blend)
        {
            float b = Clamp01(blend);
            if (b > 0.5f) return IsRainy(blendState) ? Math.Min(1f, (b - 0.5f) * 2f) : 0f;
            if (b < 0.5f) return IsRainy(state) ? Math.Min(1f, (0.5f - b) * 2f) : 0f;
            return 0f;
        }

        /// <summary>
        /// Rain intensity of the director's whole state mix (G3Environment.StateMix; weights normalized): the one state above
        /// 50 % falls at (w - 0.5) * 2 if it rains, else 0. Empty -> null.
        /// </summary>
        public static float? RainOf(IReadOnlyList<KeyValuePair<string, float>> mix)
        {
            if (mix == null || mix.Count == 0) return null;
            float sum = 0;
            for (int i = 0; i < mix.Count; i++) sum += Math.Max(0f, mix[i].Value);
            if (sum <= 0) return null;
            for (int i = 0; i < mix.Count; i++)
            {
                float w = Math.Max(0f, mix[i].Value) / sum;
                if (w > 0.5f) return IsRainy(mix[i].Key) ? Math.Min(1f, (w - 0.5f) * 2f) : 0f;
            }
            return 0f;
        }

        /// <summary>
        /// Target wetness of G3Environment's state blend. fix-239 (owner, Braga at the Myrtana/Varant edge, day: rocks and
        /// sand glossy white): the target was the rainy <i>share</i> of the blend, so a Rainy -> Sunny fade (G3: 4-16 game
        /// hours) kept the world 40-50 % wet for hours after the last drop, and at the Varant edge a Myrtana rain share that
        /// never rained there (no state above 50 %) wetted the sand. Now only rain that actually falls wets (G3's precipitation
        /// rule); without it the surfaces dry (<see cref="WetStep(float, float, float, float)"/>).
        /// </summary>
        public static float WetTarget(string state, string blendState, float blend) => WetOfRain(RainOf(state, blendState, blend));

        /// <summary>fix-215/239: <see cref="WetTarget(string, string, float)"/> over the whole state mix; empty -> null.</summary>
        public static float? WetTarget(IReadOnlyList<KeyValuePair<string, float>> mix)
        {
            var r = RainOf(mix);
            return r.HasValue ? WetOfRain(r.Value) : (float?)null;
        }

        /// <summary>
        /// fix-239: the desert share of a weather state mix (states of the Varant environment: "Sunny(Varant)",
        /// "Sandstormy(Varant)"); weights normalized; empty -> 0.
        /// </summary>
        public static float DesertShare(IReadOnlyList<KeyValuePair<string, float>> mix)
        {
            if (mix == null) return 0f;
            float sum = 0, d = 0;
            for (int i = 0; i < mix.Count; i++)
            {
                float w = Math.Max(0f, mix[i].Value);
                sum += w;
                if (WorldLightNew.RegionOfState(mix[i].Key) == "Varant") d += w;
            }
            return sum > 0 ? d / sum : 0f;
        }

        /// <summary>Wet time constant (s): wets in ~20 s of rain.</summary>
        public const float WetTau = 20f;
        /// <summary>Dry time constant (s) after the rain (Myrtana; ~5 min to a trace). </summary>
        public const float DryTau = 90f;
        /// <summary>Dry time constant (s) in the desert (Varant sand and rock dry fast).</summary>
        public const float DesertDryTau = 25f;

        /// <summary>Wetness after dt seconds: wets in ~20 s, dries in ~90 s (exponential approach).</summary>
        public static float WetStep(float current, float target, float dt) => WetStep(current, target, dt, 0f);

        /// <summary>
        /// As <see cref="WetStep(float, float, float)"/>; drying speeds up with the desert share of the weather
        /// (<paramref name="desert"/> 1: ~25 s).
        /// </summary>
        public static float WetStep(float current, float target, float dt, float desert)
        {
            float tau = target > current ? WetTau : DryTau + (DesertDryTau - DryTau) * Clamp01(desert);
            return current + (target - current) * (1 - (float)Math.Exp(-Math.Max(dt, 0) / tau));
        }

        /// <summary>
        /// fix-239: the wetness the surfaces show - only where it rains: the desert share of the weather stays dry (Varant's
        /// sand and rock never take Myrtana's rain at the region edge).
        /// </summary>
        public static float ShownWetness(float wet, float desert) => Clamp01(wet) * (1 - Clamp01(desert));

        // ------------------------------------------------------------------ materials

        /// <summary>One CC0 material class: folder under GameData/Remaster/Materials, real size and how far it replaces G3.</summary>
        public sealed class Layer
        {
            public string Kind;        // ground, rock, wall, wood, roof, thatch, metal
            public string Cc0;         // e.g. polyhaven_leafy_grass
            public float RealSize;     // metres covered by one tile of the CC0 scan
            public float Replace;      // 0 = keep G3's albedo structure (CC0 only as detail) .. 1 = CC0 albedo (colour-matched)
            public bool Parallax;      // height-map parallax on this class
            public bool Rock;          // world-space triplanar detail
            public override string ToString() => $"{Kind}:{Cc0}@{RealSize:0.##}m r{Replace:0.##}{(Parallax ? " pom" : "")}{(Rock ? " rock" : "")}";
        }

        // Image-stem prefix -> class (first match wins; same CC0 folders as Tools/Remaster/worldtex.py LOOKS, sizes from
        // SOURCES.md). Replace 1 = the CC0 scan replaces G3's image (colour-matched to G3's mean, G3's blurred pattern on
        // top): ground, rock, masonry, plaster, thatch (owner: the tower masonry and the cliffs must change). Replace < 1
        // where G3's texture carries layout a CC0 scan cannot know (plank runs along beams, roof-tile rows, cobble
        // layout, iron fittings): G3's (r3-upscaled) image and its baked height stay the structure, the CC0 scan adds
        // grain, roughness and normal detail at its real size.
        static readonly (string prefix, string kind, string cc0, float size, float replace, bool pom, bool rock)[] Table =
        {
            ("G3_Nature_Ground_Grass", "ground", "polyhaven_leafy_grass", 2.0f, 1f, false, false),
            ("G3_Nature_Ground_Forest", "ground", "polyhaven_forest_floor", 2.14f, 1f, false, false),
            ("G3_Nature_Forest_Snowground", "ground", "polyhaven_snow_02", 2.0f, 1f, false, false),
            ("G3_Nature_Earthground", "ground", "polyhaven_brown_mud_02", 1.3f, 1f, false, false),
            ("G3_Nature_Ground_Earth", "ground", "polyhaven_dirt", 2.0f, 1f, false, false),
            ("G3_Nature_Ground_Gravel", "ground", "polyhaven_rocky_trail", 2.0f, 1f, true, false),
            ("G3_Nature_Ground_Path", "ground", "polyhaven_dirt", 2.0f, 1f, false, false),
            ("G3_Nature_Ground_Riverstone", "ground", "polyhaven_river_small_rocks", 2.9f, 1f, true, false),
            ("G3_Nature_Ground_Snow", "ground", "polyhaven_snow_02", 2.0f, 1f, false, false),
            ("G3_Nature_Snow_Path", "ground", "polyhaven_snow_02", 2.0f, 1f, false, false),
            ("G3_Architecture_Ground_City_Snow", "ground", "polyhaven_snow_02", 2.0f, 1f, false, false),
            ("G3_Architecture_Ground_City", "ground", "polyhaven_river_small_rocks", 2.9f, 0.6f, true, false),
            ("G3_Nature_Dunes", "ground", "polyhaven_coast_sand_01", 15f, 1f, false, false),
            ("G3_Desert_Sand", "ground", "polyhaven_coast_sand_01", 15f, 1f, false, false),
            ("G3_Architecture_SandGround", "ground", "polyhaven_coast_sand_01", 15f, 1f, false, false),
            ("G3_Nature_Wall_Stone_12", "rock", "polyhaven_rock_pitted_mossy", 1.0f, 1f, true, true),
            ("G3_Nature_Wall_Stone_02", "rock", "polyhaven_rock_face", 2.38f, 1f, true, true),   // r30: cliff_side strata read as stripes
            ("G3_Nature_Rock", "rock", "polyhaven_rock_face", 2.38f, 1f, true, true),
            ("G3_Desert_Rock", "rock", "polyhaven_cliff_side", 1.83f, 1f, true, true),
            ("G3_Nature_Wall_Stone", "rock", "polyhaven_rock_face", 2.38f, 1f, true, true),
            ("G3_Architecture_Wall_Stone", "wall", "polyhaven_stacked_stone_wall", 2.0f, 1f, true, false),
            ("G3_Architecture_Bricks", "wall", "polyhaven_castle_brick_07", 2.5f, 1f, true, false),
            ("G3_Architecture_GroundStone", "wall", "polyhaven_stacked_stone_wall", 2.0f, 1f, true, false),
            ("G3_Architecture_WallHouse", "wall", "polyhaven_worn_plaster_wall", 1.8f, 1f, true, false),
            ("G3_Architecture_Wood_07", "wood", "polyhaven_brown_planks_03", 1.0f, 0.4f, true, false),
            ("G3_Architecture_Wood_08", "wood", "polyhaven_brown_planks_03", 1.0f, 0.4f, true, false),
            ("G3_Architecture_Wood", "wood", "polyhaven_weathered_planks", 2.0f, 0.4f, true, false),
            ("G3_Architecture_Rooftile", "roof", "polyhaven_roof_tiles_14", 1.5f, 0.4f, true, false),
            ("G3_Architecture_Roofshingle", "roof", "polyhaven_roof_slates_02", 3.0f, 0.5f, true, false),
            ("G3_Architecture_Strawroof", "thatch", "polyhaven_thatch_roof_angled", 0.54f, 1f, true, false),
            ("G3_Architecture_Iron", "metal", "ambientcg_Metal053C", 1.0f, 0.3f, false, false),
            ("G3_Architecture_Metal", "metal", "ambientcg_Metal054A", 1.0f, 0.3f, false, false),
        };

        /// <summary>
        /// Normal strength of a v2 material (<c>_V2Surface.z</c>): AI layers 1.0 (as derived), rock 1.5, everything else 1.2.
        /// fix-187: charred wood (G3_Architecture_Wood_Burned: campfire stands, burnt beams) 0.6 - its normal map is fine
        /// crackle, and boosted it turned the campfire's own point light (~1 m away, grazing) into dark/orange speckle.
        /// </summary>
        public static float NormalStrength(string stemA, string stemB, bool anyAi, bool rocky)
        {
            if (anyAi) return 1.0f;
            if (rocky) return 1.5f;
            if (IsCharred(stemA) || IsCharred(stemB)) return 0.6f;
            return 1.2f;
        }

        static bool IsCharred(string stem) => !string.IsNullOrEmpty(stem) && stem.IndexOf("_Burned", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The CC0 class of a G3 image stem (null = no v2 replacement: props, foliage, decals...).</summary>
        public static Layer LayerFor(string imageStem)
        {
            if (string.IsNullOrEmpty(imageStem)) return null;
            var arch = ArchClassFor(imageStem);
            if (arch != null)
                return new Layer { Kind = "arch", Cc0 = ArchDetailFolder(arch), RealSize = ArchDetailSize(arch), Replace = 0, Parallax = ArchPomDepth(arch) > 0 };
            foreach (var t in Table)
                if (imageStem.StartsWith(t.prefix, StringComparison.OrdinalIgnoreCase))
                    return new Layer { Kind = t.kind, Cc0 = t.cc0, RealSize = t.size, Replace = t.replace, Parallax = t.pom, Rock = t.rock };
            return null;
        }

        // ------------------------------------------------------------------ Varant architecture (Docs/remaster-world.md 25)

        // arch-1: G3's own Varant architecture images remastered offline (Tools/Remaster/archtex.py: 4x-UltraSharp, colour
        // locked, de-lit, height integrated from G3's normal map) are the surface; the material keeps G3's image, layout and
        // colour (a "partial" surface, replace 0) and gets a high-passed CC0 micro-detail tile per class (world triplanar),
        // parallax on G3's own height, sand dust in the joints and cloth translucency (_V2Arch). Same prefixes as
        // archtex.PREFIXES (first match wins). Kept off Myrtana's shared stems (Bricks, GroundStone, Wood, WallHouse, Iron).
        static readonly (string prefix, string cls)[] ArchTable =
        {
            ("G3_Architecture_SandStoneTiles", "masonry"),
            ("G3_Architecture_SandStone_06", "masonry"),
            ("G3_Architecture_SandStone_09", "masonry"),
            ("G3_Architecture_SandStone_10", "masonry"),
            ("G3_Architecture_SandStone", "sandstone"),
            ("G3_Architecture_SandWall", "sandstone"),
            ("G3_Desert_Ruins_Ground", "floor"),
            ("G3_Architecture_GroundPlates", "floor"),
            ("G3_Desert_Ruins_Stone", "masonry"),
            ("G3_Architecture_StoneComp", "masonry"),
            ("G3_Architecture_SlaveCastle", "masonry"),
            ("G3_Architecture_RoofStone", "masonry"),
            ("G3_Architecture_Marble", "masonry"),
            ("G3_Desert_Well", "masonry"),
            ("G3_Architecture_DesertWell", "masonry"),
            ("G3_Architecture_RoomWalls", "plaster"),
            ("G3_Architecture_VarantArena", "wood"),
            ("G3_Architecture_WhiteWood", "wood"),
            ("G3_Objects_Orient_Wood", "wood"),
            ("G3_Objects_Desert_Stuff", "wood"),
            ("G3_Desert_Tent", "cloth"),
            ("G3_Object_Varant_Sheet", "cloth"),
            ("G3_Object_Varant_Banner", "cloth"),
            ("G3_Object_Varant_Carpet", "cloth"),
            ("G3_Objects_Carpet", "cloth"),
            ("G3_Architecture_Drapery", "cloth"),
            ("G3_Architecture_Fabrics", "cloth"),
            ("G3_Architecture_Gold", "metal"),
            ("G3_Architecture_Iron_V", "metal"),
            // arch-2 (25.7): two shared-prefix stems whose v2 CC0 class was wrong for the image. WallHouse_02 (998 placements,
            // 475 in Varant: the mud-brick houses) is G3's dark rubble masonry with plaster patches - the CC0 worn plaster
            // (replace 1) painted it as a grey stained plaster wall; GroundStone_03 (Varant only, 92k m2 under the city sand)
            // is pebbled desert ground - the CC0 stacked stone wall (replace 1) turned it into a wall texture on the floor.
            // Both keep G3's image (their r3 sets: worldtex / archtex) with the arch terms.
            ("G3_Architecture_WallHouse_02", "masonry"),
            ("G3_Architecture_GroundStone_03", "floor"),
            // arch-3 (26): Myrtana / Nordmar images without a v2 CC0 class (the shared Table stems keep theirs, see ArchWeatherFor)
            ("G3_Architecture_Snowwood", "wood"),
            ("G3_Architecture_Ornamente", "wood"),
            ("G3_Architecture_Dungeonsupport", "wood"),
            ("G3_Architecture_woodwall", "wood"),
            ("G3_Architecture_Wall_Wood", "wood"),
            ("G3_Architecture_DryWood", "wood"),
            ("G3_Objects_Wood", "wood"),
            ("G3_Nordmar_sign", "wood"),
            ("G3_Architecture_stairHouse", "floor"),
            ("G3_Architecture_Rock_01", "masonry"),
            ("G3_Architecture_Beliar_Statue", "masonry"),
            ("G3_Architecture_Gravestone", "masonry"),
            ("G3_Architecture_PfeilerKopf", "masonry"),
            ("G3_Architecture_Well_Inside", "masonry"),
            ("G3_Architecture_Prop_stonefeeder", "masonry"),
            ("G3_Architecture_Brickmud", "plaster"),
            ("G3_Object_IceWall", "ice"),
            ("G3_Architecture_Prop_strawbale", "thatch"),
            ("G3_Architecture_Prop_dunghill", "thatch"),
            ("G3_Architecture_Prop_", "wood"),
            ("G3_Architecture_Coarse", "cloth"),
            ("G3_Architecture_Bridgerope", "cloth"),
            ("G3_Nordmar_Banner", "cloth"),
            ("G3_Objects_Banner", "cloth"),
            ("G3_Objects_Pelt", "cloth"),
            ("G3_Objects_Leather", "cloth"),
            ("G3_Objects_Cloth", "cloth"),
            ("G3_Objects_Curtains", "cloth"),
            ("G3_Objects_Barrelmetal", "metal"),
            ("G3_Objects_Iron", "metal"),
            ("G3_Architecture_Chandelier", "metal"),
        };

        /// <summary>Architecture classes (shader id = index + 1 in <c>_V2Arch.z</c>).</summary>
        public static readonly string[] ArchClasses = { "masonry", "floor", "sandstone", "plaster", "wood", "cloth", "metal", "ice", "thatch" };   // arch-3: + ice, thatch

        /// <summary>Class of a Varant architecture image stem (null = not part of the arch-1 set).</summary>
        public static string ArchClassFor(string imageStem)
        {
            if (string.IsNullOrEmpty(imageStem)) return null;
            foreach (var t in ArchTable)
                if (imageStem.StartsWith(t.prefix, StringComparison.OrdinalIgnoreCase)) return t.cls;
            return null;
        }

        /// <summary>
        /// arch-2 (25.7): an alpha-tested material of this image gets the v2 arch terms (micro detail, translucency, dust;
        /// the shader's _ALPHATEST_ON keeps the cut-out in every pass). Cloth only: its parallax depth is 0, so the coverage
        /// test at the mesh UV is exact (parallax would shift the texels but not the clip edge).
        /// </summary>
        public static bool ArchCutoutAllowed(string imageStem) => ArchClassFor(imageStem) == "cloth";

        /// <summary>V2/Layers folder of the class's CC0 micro-detail tile (archtex.py --details).</summary>
        public static string ArchDetailFolder(string cls) => "arch_detail_" + (cls == "masonry" || cls == "floor" || cls == "sandstone" || cls == "ice" ? "stone" : cls == "thatch" ? "wood" : cls);

        /// <summary>World size (m) of one detail tile: the CC0 scan's real size (archtex.DETAILS).</summary>
        public static float ArchDetailSize(string cls) => cls == "plaster" || cls == "wood" || cls == "thatch" ? 1.8f : cls == "cloth" ? 1.0f : cls == "metal" ? 1.0f : 1.83f;

        /// <summary>Parallax depth (m) of G3's own relief per class: deep mortar joints, floor plates, rough sandstone; none on cloth.</summary>
        public static float ArchPomDepth(string cls) => cls switch
        {
            "masonry" => 0.025f, "floor" => 0.018f, "sandstone" => 0.012f, "plaster" => 0.006f, "wood" => 0.008f, "metal" => 0.003f, "ice" => 0.01f, "thatch" => 0.012f, _ => 0f,
        };

        /// <summary>
        /// <c>_V2Arch</c> of a class: x parallax depth (m), y sand dust in the joints / on up faces (desert only), z class id
        /// (1..7, 0 = not arch), w cloth translucency.
        /// </summary>
        public static float[] ArchVector(string cls)
        {
            int id = Array.IndexOf(ArchClasses, cls);
            if (id < 0) return new float[4];
            float dust = cls == "cloth" || cls == "metal" || cls == "ice" ? 0.35f : cls == "wood" || cls == "thatch" ? 0.6f : 1f;
            float trans = cls == "cloth" ? 0.4f : 0f;
            return new[] { ArchPomDepth(cls), dust, id + 1f, trans };
        }

        /// <summary>
        /// arch-3 (26): weathering (<c>_V2Arch.y</c>, with class id 0 = no arch branch) of an image that keeps its v2 CC0 class
        /// (Myrtana's shared stems: walls, wood, roofs, thatch, metal): the region terms - desert dust, Myrtana moss/dirt in the
        /// joints, Nordmar frost - without taking the owner-approved CC0 look away. 0 for arch stems (their ArchVector has it),
        /// ground, rock and unclassed images.
        /// </summary>
        public static float ArchWeatherFor(string imageStem)
        {
            if (ArchClassFor(imageStem) != null) return 0f;
            var l = LayerFor(imageStem);
            if (l == null) return 0f;
            return l.Kind switch { "wall" => 1f, "roof" => 0.8f, "wood" => 0.6f, "thatch" => 0.5f, "metal" => 0.35f, _ => 0f };
        }

        /// <summary>_G3V2Arch2 (arch-3): x Myrtana moss/dirt scale, y Nordmar frost scale (json archMoss, archFrost; 0 = off).</summary>
        public static float[] ArchGlobal2 => S.v2 && S.arch ? new[] { S.archMoss, S.archFrost, 0f, 0f } : new float[4];

        /// <summary>_G3V2Arch: x micro detail, y parallax scale, z dust scale, w translucency scale (json arch*; all 0 = the arch terms off).</summary>
        public static float[] ArchGlobal => S.v2 && S.arch ? new[] { S.archDetail, S.archPom, S.archDust, S.archTranslucency } : new float[4];

        // ------------------------------------------------------------------ AI layers (Docs/remaster-world.md 10.18)

        /// <summary>Folder prefix of the local-AI remasters of G3 images (Tools/Remaster/ai/rock_ai.py) under V2/Layers.</summary>
        public const string AiPrefix = "ai_";

        /// <summary>Layer kind of an AI layer in <c>_V2LayerX.z</c> (G3/Remaster/WorldV2: -1 none, 0 surface, 1 ground, 2 rock, 3 AI).</summary>
        public const float AiKind = 3f;

        /// <summary>V2/Layers folder of the AI remaster of a G3 image stem (null for none).</summary>
        public static string AiFolder(string imageStem) => string.IsNullOrEmpty(imageStem) ? null : AiPrefix + imageStem;

        /// <summary>
        /// <c>_V2Blend</c> of a v2 material (pass 6): x height-blend band (0 = global v2HeightBlend), y steep-to-rock push,
        /// z G3 fine colour on its ground layers (0 = global v2G3Fine), w CC0 luminance grain on ground. Sand ground (G3 desert
        /// sand, dunes) against rock: a soft blend like G3's vertex-alpha lerp, sand allowed up the slopes, and all of G3's
        /// own sand image (its ripples and pale crests; the flat CC0 coast sand made hard, flat orange patches in Varant).
        /// </summary>
        public static float[] BlendVector(Layer a, Layer b)
        {
            bool sand = IsSand(a) || IsSand(b);
            return sand ? new[] { SandBlendBand, SandSteepPush, 1f, SandCc0Grain } : new[] { 0f, 1f, 0f, 1f };
        }

        /// <summary>Sand ground layer: the coast-sand CC0 class (G3_Desert_Sand, G3_Nature_Dunes, sand ground).</summary>
        public static bool IsSand(Layer l) => l != null && l.Kind == "ground" && l.Cc0 != null && l.Cc0.IndexOf("sand", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Sand blend constants (pass 6): band 0.45 (global 0.12 = crisp), steep push 0.3 (1 = 0.85 towards rock), CC0 grain 0.3.</summary>
        public const float SandBlendBand = 0.45f, SandSteepPush = 0.3f, SandCc0Grain = 0.3f;

        /// <summary>
        /// <c>_V2LayerX</c> of an AI layer: AI normal scale <see cref="AiNormalScale"/>, saturation <see cref="AiSaturation"/>, kind 3, and w: 1 = terrain cell / blend mesh:
        /// G3's mesh UV with world triplanar (G3's world repeat) only where the UV is stretched (pass 4), 0 = G3's mesh UV only.
        /// </summary>
        public static float[] AiLayerVector(bool terrainCell) => new[] { AiNormalScale, AiSaturation, AiKind, terrainCell ? 1f : 0f };

        /// <summary>AI normal (derived from the AI albedo) as detail on G3's own authored normal map (pass 5: 0.6 -> 0.25, its fine relief read as streaks at grazing light up close).</summary>
        public const float AiNormalScale = 0.25f;

        /// <summary>Saturation of the AI rock's own hue (pass 3: 1.6 greyer than G3's olive cliffs; pass 4: 2.4 slightly over A in Faring, Nordmar's grey rock went yellow-olive -> 2.1).</summary>
        public const float AiSaturation = 2.1f;

        /// <summary>UV stretch (singular-value ratio of the UV-to-world map) where kind 3 starts / ends blending to world triplanar.</summary>
        public const float AiStretchStart = 2.0f, AiStretchEnd = 3.5f;

        /// <summary>
        /// Weight of world triplanar for an AI layer (mirrors G3/Remaster/WorldV2 kind 3): from the world-space tangents of the
        /// mesh UV (dP/du, dP/dv): 0 while the map is near-conformal, 1 when stretched past <see cref="AiStretchEnd"/> or degenerate.
        /// </summary>
        public static float AiTriplanarWeight(float[] dPdu, float[] dPdv)
        {
            float uu = Dot(dPdu, dPdu), vv = Dot(dPdv, dPdv), uv = Dot(dPdu, dPdv);
            float area2 = uu * vv - uv * uv, s = uu + vv;
            if (area2 <= 1e-12f * s * s || s <= 0) return 1;
            float d = (float)Math.Sqrt(Math.Max(s * s - 4 * area2, 0));
            float ratio = (float)Math.Sqrt((s + d) / Math.Max(s - d, 1e-12f));
            float t = Clamp01((ratio - AiStretchStart) / (AiStretchEnd - AiStretchStart));
            return t * t * (3 - 2 * t);
        }

        static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        /// <summary>All CC0 folders the v2 materials need (for the layer packer and the builder).</summary>
        public static IEnumerable<string> Cc0Folders()
        {
            var seen = new HashSet<string>();
            foreach (var t in Table) if (seen.Add(t.cc0)) yield return t.cc0;
        }

        /// <summary>
        /// UV multiplier that maps a G3 layer UV (one repeat of the G3 image covering <paramref name="metresPerRepeat"/> m)
        /// onto the CC0 scan at its real size: CC0 uv = G3 uv × this.
        /// </summary>
        public static float UvScale(float metresPerRepeat, float realSize) =>
            metresPerRepeat > 0 && realSize > 0 ? metresPerRepeat / realSize : 1;

        /// <summary>
        /// Hex-tile weights (Mikkelsen 2022 "Practical real-time hex-tiling"): the three barycentric weights raised to a
        /// contrast exponent and renormalised; sums to 1. Mirrored by G3V2Hex in the shader.
        /// </summary>
        public static float[] HexWeights(float w1, float w2, float w3, float contrast = 7)
        {
            double a = Math.Pow(Math.Max(w1, 0), contrast), b = Math.Pow(Math.Max(w2, 0), contrast), c = Math.Pow(Math.Max(w3, 0), contrast);
            double s = a + b + c;
            if (s < 1e-12) return new[] { 1 / 3f, 1 / 3f, 1 / 3f };
            return new[] { (float)(a / s), (float)(b / s), (float)(c / s) };
        }

        // ------------------------------------------------------------------ trees

        /// <summary>Species group of a SpeedTree name (G3 .spt stems): fir, pine, deciduous, palm, bush or other.</summary>
        public static string TreeSpecies(string sptName)
        {
            if (string.IsNullOrEmpty(sptName)) return "other";
            string n = sptName.ToLowerInvariant();
            if (n.Contains("palm") || n.Contains("cycad")) return "palm";
            if (n.Contains("fir") || n.Contains("spruce") || n.Contains("hemlock") || n.Contains("cedar")) return "fir";
            if (n.Contains("pine") || n.Contains("cypress") || n.Contains("juniper")) return "pine";
            if (n.Contains("buckthorn") || n.Contains("bush") || n.Contains("shrub") || n.Contains("hedge")) return "bush";
            if (n.Contains("oak") || n.Contains("maple") || n.Contains("beech") || n.Contains("birch") || n.Contains("ash") ||
                n.Contains("elm") || n.Contains("willow") || n.Contains("poplar") || n.Contains("linden") || n.Contains("chestnut") ||
                n.Contains("sassafras") || n.Contains("aspen") || n.Contains("alder") || n.Contains("walnut")) return "deciduous";
            return "other";
        }

        // ------------------------------------------------------------------ budget

        /// <summary>
        /// Frame-time estimate (ms, RTX 3080, 1080p, Faring) per v2 feature on top of the r3 frame, from the measured r1-r3
        /// costs and the pass sizes (steps, fetches, shadow casters). The runner replaces it with the profiler numbers.
        /// </summary>
        public static Dictionary<string, float> BudgetMs()
        {
            var s = S;
            var d = new Dictionary<string, float>();
            if (!s.v2) return d;
            if (s.v2Shadows)
            {
                float dist = s.v2ShadowDistance / 50f, res = s.v2ShadowResolution / 2048f;
                d["shadows (GPU)"] = 0.35f * (dist - 1) + 0.25f * (res * res - 1) * 0.5f;
                d["shadows (main thread culling)"] = 0.25f * (dist - 1) * 0.5f;
            }
            if (s.v2Ssao) d["ssao"] = 0.15f + 0.2f * Math.Max(s.v2SsaoRadius - 0.4f, 0);
            if (s.v2Fog || s.v2Shafts) { float k = 2f / s.v2VolumetricDownsample; d["volumetrics"] = 0.08f + (0.04f + 0.018f * s.v2ShaftSteps) * k * k + (s.v2VolumetricTemporal ? 0.04f : 0); }
            if (s.v2Clouds) d["clouds"] = 0.05f + 0.025f * s.v2CloudSteps;
            if (s.v2Terrain) d["terrain CC0"] = s.v2Stochastic ? 0.55f : 0.3f;
            if (s.v2Surfaces) d["surfaces CC0"] = s.v2Parallax ? 0.45f : 0.25f;
            if (s.v2Grass) { d["grass (GPU)"] = 0.4f * s.v2GrassDensity * s.v2GrassDistance * s.v2GrassDistance * 0.5f; d["grass (main thread, saved)"] = -0.6f; }
            if (s.v2Foliage) d["foliage"] = 0.1f;
            if (WorldV2Light.PassWanted) d["light pass (sky occlusion, contact shadows)"] = WorldV2Light.CostMs();
            if (s.v2SkyOcclusion) d["sky occlusion map (main thread, amortised)"] = 0.05f;
            return d;
        }

        /// <summary>
        /// Estimated Faring frame (ms): r3 measured main thread 12.4 ms (the bound), GPU ~9 ms; GPU items ("(main thread"
        /// items excluded) add to the GPU, the main-thread items to the CPU; the frame is the larger of the two.
        /// </summary>
        public static float EstimatedFrameMs(float mainBaseline = 12.4f, float gpuBaseline = 9.0f)
        {
            float main = mainBaseline, gpu = gpuBaseline;
            foreach (var kv in BudgetMs())
                if (kv.Key.Contains("main thread")) main += kv.Value; else gpu += kv.Value;
            return Math.Max(main, gpu);
        }

        static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
