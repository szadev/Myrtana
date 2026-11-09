using System;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free maths of the "New" lighting (Docs/remaster-world.md section 21, agent lighting-new): the third value of
    /// the Modern lighting row. G3's flat ambient fill is replaced by physically motivated light: the open-sky irradiance
    /// integrated from the sky itself (the G3SkyReflection cube, sun texels capped: the sun is the direct term), occluded by
    /// the probes' traced visibility and the screen-space visibility bitmask, plus the coloured bounce (probes + screen) and a
    /// small floor of G3's fill (multiple scattering, never pitch black); no exposure matching to the old frame but a
    /// histogram auto-exposure with eye adaptation, a filmic tonemapper (Khronos PBR Neutral: hues and saturation of G3's
    /// palette kept below the knee) and a per-region / time-of-day grade with a night (Purkinje) shift.
    /// Mirrored by Hidden/G3/Remaster/LightRatio (New branch), G3RemasterExposure.compute and the grade pass (tested here).
    /// </summary>
    public static class WorldLightNew
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        // ------------------------------------------------------------------ sky light

        /// <summary>
        /// Direction of a cube texel (D3D cube face convention: +x −x +y −y +z −z; u right, v down, both −1..1), unnormalised.
        /// </summary>
        public static (float x, float y, float z) FaceDir(int face, float u, float v)
        {
            switch (face)
            {
                case 0: return (1, -v, -u);
                case 1: return (-1, -v, u);
                case 2: return (u, 1, v);
                case 3: return (u, -1, -v);
                case 4: return (u, -v, 1);
                default: return (-u, -v, -1);
            }
        }

        /// <summary>Solid angle of the texel (i, j) of an n×n cube face (exact, area element of the unit-cube projection).</summary>
        public static float TexelSolidAngle(int i, int j, int n)
        {
            float x0 = 2f * i / n - 1, x1 = 2f * (i + 1) / n - 1, y0 = 2f * j / n - 1, y1 = 2f * (j + 1) / n - 1;
            static double A(double x, double y) => Math.Atan2(x * y, Math.Sqrt(x * x + y * y + 1));
            return (float)(A(x0, y0) - A(x0, y1) - A(x1, y0) + A(x1, y1));
        }

        /// <summary>
        /// The open-sky irradiance as an ambient cube (6 faces × rgb, Unity's convention without 1/π: a uniform radiance L over
        /// the whole sphere gives L on every face) from the cube's texels (<paramref name="faces"/>[f] = n×n×rgb, row j = v
        /// from the top). Only directions above the horizon count (the ground below is the bounce's part); texels brighter
        /// than <paramref name="capFactor"/> × the median sky luminance are capped there (the sun disc: the direct term).
        /// </summary>
        public static float[][] SkyIrradiance(float[][] faces, int n, float capFactor = 4f)
        {
            var cube = new float[6][];
            for (int f = 0; f < 6; f++) cube[f] = new float[3];
            if (faces == null || faces.Length < 6 || n <= 0) return cube;
            // median luminance of the upper hemisphere (the sun disc must not raise its own cap)
            var lums = new System.Collections.Generic.List<float>(6 * n * n / 2);
            for (int f = 0; f < 6; f++)
            {
                if (faces[f] == null || faces[f].Length < n * n * 3) return cube;
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        if (Dir(f, i, j, n).y <= 0) continue;
                        int k = (j * n + i) * 3;
                        lums.Add(Lum(faces[f][k], faces[f][k + 1], faces[f][k + 2]));
                    }
            }
            lums.Sort();
            float cap = lums.Count > 0 ? Math.Max(lums[lums.Count / 2], 1e-4f) * Math.Max(1f, capFactor) : 0;
            var acc = new double[6, 3];
            for (int f = 0; f < 6; f++)
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        var d = Dir(f, i, j, n);
                        if (d.y <= 0) continue;
                        float w = TexelSolidAngle(i, j, n);
                        int k = (j * n + i) * 3;
                        float r = Math.Max(faces[f][k], 0), g = Math.Max(faces[f][k + 1], 0), b = Math.Max(faces[f][k + 2], 0);
                        float l = Lum(r, g, b);
                        if (cap > 0 && l > cap) { float s = cap / l; r *= s; g *= s; b *= s; }
                        for (int c = 0; c < 6; c++)
                        {
                            var fd = WorldGi.Faces[c];
                            float cs = fd[0] * d.x + fd[1] * d.y + fd[2] * d.z;
                            if (cs <= 0) continue;
                            acc[c, 0] += r * cs * w; acc[c, 1] += g * cs * w; acc[c, 2] += b * cs * w;
                        }
                    }
            // Unity's convention: E = (1/π) ∫ L cos dω (uniform L -> L)
            for (int c = 0; c < 6; c++)
                for (int ch = 0; ch < 3; ch++) cube[c][ch] = (float)(acc[c, ch] / Math.PI);
            return cube;
        }

        static (float x, float y, float z) Dir(int f, int i, int j, int n)
        {
            var d = FaceDir(f, (i + 0.5f) / n * 2 - 1, (j + 0.5f) / n * 2 - 1);
            float l = (float)Math.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            return (d.x / l, d.y / l, d.z / l);
        }

        /// <summary>Rec. 709 luminance.</summary>
        public static float Lum(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

        /// <summary>
        /// Share of a surface's cosine lobe that sees the ground below a flat horizon, for normal elevation ny (−1..1):
        /// (1 − ny) / 2. The far-field ground bounce outside the probe window is albedo × (sun + sky on the ground) × this.
        /// </summary>
        public static float GroundShare(float ny) => 0.5f * (1 - Clamp(ny, -1, 1));

        /// <summary>
        /// The ambient of the New lighting (one channel): open-sky irradiance × sky visibility (traced inside the probe window,
        /// the occluder map outside: <paramref name="w"/> is the probe weight) × the screen visibility, + the bounce (probes'
        /// traced bounce inside the window, the flat-ground estimate outside) × screen visibility, + the screen-space bounce,
        /// + the floor × G3's fill.
        /// </summary>
        public static float Ambient(float sky, float relVisGi, float mapOcclusion, float w, float ssVis, float probeBounce, float groundBounce, float ssBounce, float g3Fill)
        {
            w = Clamp01(w);
            float vSky = (1 - Clamp01(mapOcclusion)) * (1 - w) + Clamp01(relVisGi) * w;
            float bounce = w * Math.Max(probeBounce, 0) + (1 - w) * Math.Max(groundBounce, 0);
            float ss = Clamp01(ssVis);
            return Math.Max(sky, 0) * S.v2NewSky * vSky * ss + bounce * ss + Math.Max(ssBounce, 0) * S.v2NewSsgiGain + S.v2NewFloor * Math.Max(g3Fill, 0);
        }

        /// <summary>The relight ratio under New (no 1.15 cap of the matched modes; the exposure follows).</summary>
        public static float Ratio(float amb, float sunLit, float local, float a, float d, float l)
        {
            float sum = Math.Max(a, 0) + Math.Max(d, 0) + Math.Max(l, 0);
            if (sum < 1e-6f) return 1;
            return Clamp((Math.Max(amb, 0) + Math.Max(sunLit, 0) * S.v2NewSun + Math.Max(local, 0)) / sum, 0, S.v2NewMaxRatio);
        }

        // ------------------------------------------------------------------ auto-exposure

        /// <summary>Histogram layout: 64 bins of log2 luminance over [MinLog, MaxLog).</summary>
        public const int Bins = 64;
        public const float MinLog = -14f, MaxLog = 4f;

        /// <summary>The bin of a luminance (≤ 0 -> 0).</summary>
        public static int Bin(float lum)
        {
            if (!(lum > 0)) return 0;
            float t = ((float)Math.Log(lum, 2) - MinLog) / (MaxLog - MinLog);
            return Math.Max(0, Math.Min(Bins - 1, (int)(t * Bins)));
        }

        /// <summary>log2 luminance at a bin's centre.</summary>
        public static float BinLog(int b) => MinLog + (b + 0.5f) * (MaxLog - MinLog) / Bins;

        /// <summary>
        /// Mean log2 luminance of the histogram between the <paramref name="low"/> and <paramref name="high"/> shares of its
        /// weight (Unreal's percentile metering: the darkest and brightest pixels do not move the exposure); bin 0 (black and
        /// the sky-less void) counts as weight but is excluded from the mean only when below the low share.
        /// </summary>
        public static float MeanLog(float[] bins, float low, float high)
        {
            double total = 0;
            for (int i = 0; i < bins.Length; i++) total += Math.Max(bins[i], 0);
            if (total <= 0) return BinLog(Bins / 2);
            double lo = total * Clamp01(low), hi = total * Clamp01(high), acc = 0, sum = 0, ws = 0;
            for (int i = 0; i < bins.Length; i++)
            {
                double b = Math.Max(bins[i], 0);
                double a0 = acc, a1 = acc + b;
                acc = a1;
                double take = Math.Min(a1, hi) - Math.Max(a0, lo);
                if (take <= 0) continue;
                sum += take * BinLog(i); ws += take;
            }
            return ws > 0 ? (float)(sum / ws) : BinLog(Bins / 2);
        }

        /// <summary>
        /// Target exposure in EV for a metered mean luminance: <c>v2NewAdapt</c> × log2(key / mean) + bias, clamped to
        /// [<c>v2NewMinEv</c>, <c>v2NewMaxEv</c>] (a cave is not lit to noon, a sunlit field is not dimmed to grey).
        /// </summary>
        public static float TargetEv(float meanLog2)
        {
            float ev = Clamp01(S.v2NewAdapt) * ((float)Math.Log(S.v2NewKey, 2) - meanLog2) + S.v2NewBiasEv;
            return Clamp(ev, S.v2NewMinEv, S.v2NewMaxEv);
        }

        /// <summary>grade-1: the exposure cap (EV) for a night weight 0..1: v2NewMaxEv by day, v2NewNightMaxEv in the night.</summary>
        public static float MaxEv(float night) => S.v2NewMaxEv + (Math.Max(S.v2NewNightMaxEv, S.v2NewMaxEv) - S.v2NewMaxEv) * Clamp01(night);

        /// <summary>
        /// Eye adaptation: moves <paramref name="ev"/> towards <paramref name="target"/> at most <c>v2NewSpeedUp</c> EV/s
        /// brighter (dark adaptation is slow) and <c>v2NewSpeedDown</c> EV/s darker (light adaptation is fast), with an
        /// exponential ease near the target (no overshoot).
        /// </summary>
        public static float Adapt(float ev, float target, float dt)
        {
            dt = Clamp(dt, 0, 0.5f);
            float d = target - ev;
            float speed = d > 0 ? S.v2NewSpeedUp : S.v2NewSpeedDown;
            float ease = d * (1 - (float)Math.Exp(-dt * 3f));
            float step = Math.Sign(d) * Math.Min(Math.Abs(ease), speed * dt);
            return Math.Abs(step) >= Math.Abs(d) ? target : ev + step;
        }

        /// <summary>Centre weight of the metering at screen uv (0..1): 1 in the middle, 1 − <c>v2NewCenter</c> at the corners.</summary>
        public static float CenterWeight(float u, float v)
        {
            float dx = u - 0.5f, dy = v - 0.5f;
            float r = Clamp01((float)Math.Sqrt(dx * dx + dy * dy) / 0.7071f);
            return 1 - Clamp01(S.v2NewCenter) * r * r;
        }

        // ------------------------------------------------------------------ tonemapper and grade

        /// <summary>
        /// Khronos PBR Neutral (2024): identity up to the knee 0.8 − 0.04 apart from a 0.04 toe offset, highlights compressed
        /// to white with path-to-white desaturation 0.15 — base colours keep hue and saturation (G3's palette), unlike AgX
        /// (desaturates, blue -> cyan) or ACES (darker, contrastier, hue shifts).
        /// </summary>
        public static (float r, float g, float b) Neutral(float r, float g, float b)
        {
            const float start = 0.8f - 0.04f, desat = 0.15f;
            float x = Math.Min(r, Math.Min(g, b));
            float offset = x < 0.08f ? x - 6.25f * x * x : 0.04f;
            r -= offset; g -= offset; b -= offset;
            float peak = Math.Max(r, Math.Max(g, b));
            if (peak < start) return (r, g, b);
            const float d = 1 - start;
            float newPeak = 1 - d * d / (peak + d - start);
            float s = newPeak / peak;
            r *= s; g *= s; b *= s;
            float gw = 1 - 1 / (desat * (peak - newPeak) + 1);
            return (r + (newPeak - r) * gw, g + (newPeak - g) * gw, b + (newPeak - b) * gw);
        }

        /// <summary>
        /// grade-1: the Neutral tonemapper as the grade pass runs it - the toe offset (Khronos' x - 6.25 x^2 below 0.08 of the
        /// min channel, else 0.04) x <paramref name="toe"/> taken as a share of the luminance (all channels scaled: hue and
        /// saturation kept), then Khronos' shoulder. toe 0 = no toe.
        /// </summary>
        public static (float r, float g, float b) NeutralToe(float r, float g, float b, float toe)
        {
            const float start = 0.8f - 0.04f, desat = 0.15f;
            float x = Math.Min(r, Math.Min(g, b));
            float offset = (x < 0.08f ? x - 6.25f * x * x : 0.04f) * toe;
            float lc = Lum(r, g, b);
            float k = Math.Max(1 - offset / Math.Max(lc, 1e-5f), 0);
            r *= k; g *= k; b *= k;
            float peak = Math.Max(r, Math.Max(g, b));
            if (peak < start) return (r, g, b);
            const float d = 1 - start;
            float newPeak = 1 - d * d / (peak + d - start);
            float s = newPeak / peak;
            r *= s; g *= s; b *= s;
            float gw = 1 - 1 / (desat * (peak - newPeak) + 1);
            return (r + (newPeak - r) * gw, g + (newPeak - g) * gw, b + (newPeak - b) * gw);
        }

        /// <summary>ACES filmic fit (Narkowicz 2015), per channel; the alternative tonemapper (v2NewTonemap 2).</summary>
        public static float Aces(float x)
        {
            x = Math.Max(x, 0);
            return Clamp01(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f));
        }

        /// <summary>
        /// Per region and time of day (G3's weather states Myrtana / Nordmar / Varant): white balance (rgb gains, luminance
        /// kept), saturation offset. Golden hour warms (sun low over the horizon, <paramref name="lightFade"/> &lt; 1 by day),
        /// overcast cools and desaturates, the regions keep their mood: Nordmar cold, Varant warm and dry, Myrtana green-neutral.
        /// Night is handled by the Purkinje shift (<see cref="Purkinje"/>), from the metered luminance.
        /// </summary>
        public static (float r, float g, float b, float sat) Grade(string region, float hour, bool isDay, float lightFade, float overcast, float twilight = 0f)
        {
            string reg = RegionOfState(region);
            return Grade(reg == "Varant" ? 1f : 0f, reg == "Nordmar" ? 1f : 0f, hour, isDay, lightFade, overcast, twilight);
        }

        /// <summary>
        /// fix-239: twilight 0..1 - by day the low sun (1 - G3's light fade: 0 from ~9 h to ~19 h, 1 at the horizon), after
        /// sunset / before sunrise fading out over <paramref name="afterHours"/> (G3's dusk keys run to ~22 h); 0 in the night.
        /// </summary>
        public static float Twilight(float hour, bool isDay, float lightFade, float sunrise = 6f, float sunset = 22f, float afterHours = 1f)
        {
            if (isDay) return Clamp01(1 - lightFade);
            float h = ((hour % 24f) + 24f) % 24f;
            float sinceSet = ((h - sunset) % 24f + 24f) % 24f, toRise = ((sunrise - h) % 24f + 24f) % 24f;
            return Clamp01(1 - Math.Min(sinceSet, toRise) / Math.Max(afterHours, 0.01f));
        }

        /// <summary>
        /// fix-239: the floor of G3's fill in the relight (_G3NewLight.z): <c>v2NewFloor</c> + <c>v2NewTwilightFloor</c> x
        /// twilight. Owner (Varant dusk): characters were black silhouettes - the open sky's irradiance falls far below G3's
        /// own dusk ambient (Sunny(Varant) 20-21 h: AmbientGeneral (0.8, 0.5, 0.25) x 1.3-1.4) while the exposure sits at its
        /// max EV; the night (twilight 0) keeps the plain floor.
        /// </summary>
        public static float Floor(float twilight) => S.v2NewFloor + S.v2NewTwilightFloor * Clamp01(twilight);

        /// <summary>
        /// grade-1: the floor with the night term - + <c>v2NewNightFloor</c> x G3's fill in the night (<paramref name="night"/>
        /// 1 = full night, fading in as the twilight ends): G3 lights its night with a blue ambient that reads moonlit shapes;
        /// the open night sky's irradiance alone (+ 0.05) left an almost black frame even at the max exposure.
        /// </summary>
        public static float Floor(float twilight, float night) => Floor(twilight) + S.v2NewNightFloor * Clamp01(night) * (1 - Clamp01(twilight));

        /// <summary>grade-1: night weight 0..1 for <see cref="Floor(float, float)"/>: 0 by day, 1 after dusk (G3's IsDay false).</summary>
        public static float Night(bool isDay) => isDay ? 0f : 1f;

        /// <summary>
        /// fix-239: Purkinje strength for the grade (_G3NewGrade.w): none at twilight (the metered luminance is low there only
        /// because the exposure is capped; the rod shift turned Varant's orange dusk magenta-violet), full in the night.
        /// </summary>
        public static float PurkinjeStrength(float twilight) => Clamp01(S.v2NewPurkinje) * (1 - Clamp01(twilight));

        /// <summary>
        /// fix-255: and none by day - a rainy forest at noon meters ~2^-7 (the exposure at its cap), which turned on the rod
        /// shift (bluer, desaturated: the teal rain frame); rods take over only in the night (<paramref name="night"/>, eased).
        /// </summary>
        public static float PurkinjeStrength(float twilight, float night) => PurkinjeStrength(twilight) * Clamp01(night);

        /// <summary>
        /// fix-239: the region of a G3 weather state or environment name: the Varant / Nordmar environments' states carry the
        /// region in brackets ("Sunny(Varant)", "Sandstormy(Varant)", "Snowy(Nordmar)", "Snowstorm(Nordmar)"), Myrtana's are
        /// bare ("Sunny", "Rainy", "Foggy"). The grade tested <c>StartsWith("Nordmar"/"Varant")</c> on the state name, so
        /// every region got Myrtana's grade.
        /// </summary>
        public static string RegionOfState(string state)
        {
            if (string.IsNullOrEmpty(state)) return "Myrtana";
            if (state.IndexOf("Varant", StringComparison.OrdinalIgnoreCase) >= 0) return "Varant";
            if (state.IndexOf("Nordmar", StringComparison.OrdinalIgnoreCase) >= 0) return "Nordmar";
            return "Myrtana";
        }

        /// <summary>fix-239: Varant and Nordmar shares (0..1, weights normalized) of a weather state mix; the rest is Myrtana.</summary>
        public static (float varant, float nordmar) RegionShares(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, float>> mix)
        {
            if (mix == null) return (0f, 0f);
            float sum = 0, v = 0, n = 0;
            for (int i = 0; i < mix.Count; i++)
            {
                float w = Math.Max(0f, mix[i].Value);
                if (w <= 0) continue;
                sum += w;
                string reg = RegionOfState(mix[i].Key);
                if (reg == "Varant") v += w; else if (reg == "Nordmar") n += w;
            }
            return sum > 0 ? (v / sum, n / sum) : (0f, 0f);
        }

        /// <summary>
        /// <see cref="Grade(string, float, bool, float, float)"/> for a blend of regions (fix-239: the whole weather mix, so
        /// the grade fades across a region edge with the weather): <paramref name="varant"/> and <paramref name="nordmar"/>
        /// shares, the rest Myrtana; the region gains blend linearly.
        /// </summary>
        public static (float r, float g, float b, float sat) Grade(float varant, float nordmar, float hour, bool isDay, float lightFade, float overcast, float twilight = 0f)
        {
            if (!S.v2NewGrade) return (1, 1, 1, S.v2NewSaturation);
            float v = Clamp01(varant), n = Clamp01(nordmar);
            if (v + n > 1) { float k = 1 / (v + n); v *= k; n *= k; }
            float m = 1 - v - n;
            // Myrtana (1, 1.01, 1, +0.03) green-neutral, Nordmar (0.97, 1, 1.04, -0.06) cold, Varant (1.04, 1, 0.94, -0.02) warm, dry
            float r = m + n * 0.97f + v * 1.04f;
            float g = m * 1.01f + n + v;
            float b = m + n * 1.04f + v * 0.94f;
            // grade-1: Myrtana neutral (was +0.03), Varant a little drier (was -0.02): the vivid filter look
            float sat = S.v2NewSaturation - n * 0.06f - v * 0.04f;
            if (isDay)
            {
                float golden = Clamp01(1 - lightFade) * (1 - Clamp01(overcast));   // low sun
                r *= 1 + 0.05f * golden; b *= 1 - 0.06f * golden; sat += 0.02f * golden;   // grade-1: was 0.07 / 0.09 / +0.06
            }
            // fix-239: Varant's dusk amber, as G3's Sunny(Varant) 18-21 h keys (ambient (0.9, 0.43, 0.02) .. (0.65, 0.56, 0.48),
            // haze (0.94, 0.83, 0.73) .. (0.84, 0.63, 0.46), fog (1, 0.8, 0.65)): blue down, a little green up - the blue haze
            // over red low sun read magenta-violet
            float dusk = Clamp01(twilight) * (1 - Clamp01(overcast)) * v * S.v2NewDuskWarm;
            r *= 1 + 0.02f * dusk; g *= 1 + 0.05f * dusk; b *= 1 - 0.16f * dusk;
            float oc = Clamp01(overcast);
            r *= 1 - 0.02f * oc; b *= 1 + 0.03f * oc; sat -= 0.08f * oc;
            float l = Lum(r, g, b);
            return (r / l, g / l, b / l, sat);
        }

        /// <summary>
        /// Night (rod) vision weight 0..1 from the metered mean luminance before exposure (log2): full below ~0.002, none
        /// above ~0.03; the grade blends towards a bluer, desaturated image by this × <c>v2NewPurkinje</c>.
        /// </summary>
        public static float Purkinje(float meanLog2)
        {
            float t = Clamp01((meanLog2 - (-9f)) / ((-5f) - (-9f)));
            return Clamp01(S.v2NewPurkinje) * (1 - t * t * (3 - 2 * t));
        }

        /// <summary>_G3NewLight: x 1 = on, y sky scale, z floor (x G3's fill), w max ratio.</summary>
        public static float[] RatioGlobal(float twilight = 0f, float night = 0f) => WorldGi.New ? new[] { 1f, S.v2NewSky, Floor(twilight, night), S.v2NewMaxRatio } : new float[4];

        /// <summary>_G3NewLight2: x ground albedo, y screen bounce gain, z AO power, w multi-bounce blend.</summary>
        public static float[] RatioGlobal2() => new[] { S.v2NewGroundAlbedo, S.v2NewSsgiGain, S.v2NewAoPower, S.v2NewMultiBounce };

        /// <summary>Estimated extra GPU cost (ms) over Soft: histogram at 1/4 res + adapt + grade ops.</summary>
        public static float CostMs() => WorldGi.New ? 0.05f : 0;

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
