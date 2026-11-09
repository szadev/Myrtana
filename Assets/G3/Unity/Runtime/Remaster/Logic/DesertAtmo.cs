using System;

namespace G3.Unity
{
    /// <summary>
    /// atmo-1 (Docs/remaster-world.md section 23): engine-free maths of the desert atmosphere and light over Varant, used by
    /// <c>G3DesertAtmo</c> and mirrored by the shaders (Volumetrics dust/storm medium, Atmosphere grade, G3/Sky glow).
    /// Inputs are G3's own day cycle and weather (sun elevation, Sandstormy share, overcast) and the desert weight (region
    /// map at the camera); everything scales with <see cref="WorldLookSettings.atmoStrength"/> and is zero with
    /// <c>atmo</c> off (= the remaster as before). Settings: <see cref="WorldRemasterLook.Current"/>.
    /// </summary>
    public static class DesertAtmo
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        /// <summary>json atmo on and a non-zero strength.</summary>
        public static bool Enabled => S.atmo && S.atmoStrength > 0;

        // ------------------------------------------------------------------ weights

        /// <summary>Daylight 0..1 from the sun elevation (degrees): 0 below -3, 1 from +5.</summary>
        public static float Day(float elevationDeg) => Smooth(-3, 5, elevationDeg);

        /// <summary>Golden hour 0..1: full near the horizon (but in daylight), gone by 24 degrees.</summary>
        public static float Golden(float elevationDeg) => Day(elevationDeg) * (1 - Smooth(6, 24, elevationDeg));

        /// <summary>High sun 0..1 (white-hot noon): 0 below 35 degrees, 1 above 65.</summary>
        public static float Noon(float elevationDeg) => Smooth(35, 65, elevationDeg);

        /// <summary>Camera-level weight of the desert terms: desert share x json strength (0 when off).</summary>
        public static float Weight(float desert) => Enabled ? Clamp01(desert) * S.atmoStrength : 0;

        /// <summary>Clear-weather factor: overcast (G3's 0..1) and the storm take the clear-sky terms away.</summary>
        public static float Clear(float overcast, float storm) => Clamp01(1 - (float)Math.Sqrt(Clamp01(overcast))) * (1 - Clamp01(storm));

        // ------------------------------------------------------------------ light (on top of G3Environment + v2)

        /// <summary>Sun intensity multiplier: stronger in clear desert daylight, dimmed in a sandstorm.</summary>
        public static float SunGain(float w, float elevationDeg, float overcast, float storm)
        {
            if (w <= 0) return 1;
            float clear = Clear(overcast, storm);
            float k = 1 + S.atmoSun * w * Day(elevationDeg) * clear;
            return Math.Max(0, k * (1 - 0.72f * Clamp01(storm * w)));
        }

        /// <summary>
        /// Relative transmittance of the dust for the sun's colour (R, G, B at 680/550/440 nm): optical depth atmoSunDust x
        /// air mass, Ångström exponent 1 (fine desert dust reddens a little), luminance normalised to 1 (the intensity is
        /// <see cref="SunGain"/>'s job). Low sun: warmer.
        /// </summary>
        public static float[] SunTint(float w, float elevationDeg)
        {
            var o = new[] { 1f, 1f, 1f };
            if (w <= 0 || S.atmoSunDust <= 0) return o;
            double am = WorldV2.AirMass(Math.Max(elevationDeg, 0.5f));
            double[] lambda = { 0.68, 0.55, 0.44 };
            for (int i = 0; i < 3; i++) o[i] = (float)Math.Exp(-S.atmoSunDust * w * am * Math.Pow(lambda[i] / 0.55, -1.0));
            float l = Luma(o[0], o[1], o[2]);
            for (int i = 0; i < 3; i++) o[i] /= Math.Max(l, 1e-5f);
            return o;
        }

        /// <summary>
        /// The ambient re-balance in clear desert daylight: (cut = share of G3's flat ambient removed, sky = blue sky light
        /// from above in units of G3's ambient level, bounce = warm sand light from below in units of the sun's irradiance on
        /// the ground x sand albedo). Shade stays readable (sky + bounce give back about what the cut takes) but turns
        /// blue on top and warm underneath instead of G3's flat orange.
        /// </summary>
        public static (float cut, float sky, float bounce) Ambient(float w, float elevationDeg, float overcast, float storm)
        {
            if (w <= 0) return (0, 0, 0);
            float k = w * Day(elevationDeg) * Clear(overcast, storm);
            return (Clamp01(S.atmoAmbCut * k), S.atmoSkyFill * k, S.atmoBounce * k);
        }

        /// <summary>G3's linear fog distances x this over the desert: the dust haze takes over the distance (not in a storm).</summary>
        public static float FogPush(float w, float storm) => 1 + S.atmoFogPush * w * (1 - Clamp01(storm));

        /// <summary>Shadow distance (m) over the open desert: v2's distance eased towards atmoShadowDistance with the weight.</summary>
        public static float ShadowDistance(float v2Distance, float w)
        {
            if (S.atmoShadowDistance <= 0 || w <= 0) return v2Distance;
            return v2Distance + (Math.Max(v2Distance, S.atmoShadowDistance) - v2Distance) * Clamp01(w);
        }

        // ------------------------------------------------------------------ medium (Volumetrics)

        /// <summary>
        /// Dust medium globals: x extinction at the ground (1/m), y 1/scale height, z near-ground dust (1/m), w its 1/scale
        /// height; all x strength (the region weight is applied per pixel by the shader). In a storm the clear haze grows.
        /// </summary>
        public static float[] DustGlobal(float strength, float storm)
        {
            float k = Math.Max(0, strength);
            float st = Clamp01(storm);
            return new[] { S.atmoDust * k * (1 + 2 * st), 1f / S.atmoDustHeight, S.atmoNear * k * (1 + 3 * st), 1f / S.atmoNearHeight };
        }

        /// <summary>High thin dust: x extinction at the base (1/m, x strength, storm x3), y 1/scale height; z/w the haze fade-in distances (m).</summary>
        public static float[] HighGlobal(float strength, float storm) =>
            new[] { S.atmoHigh * Math.Max(0, strength) * (1 + 2 * Clamp01(storm)), 1f / S.atmoHighHeight, S.atmoHazeStart, Math.Max(S.atmoHazeStart + 1, S.atmoHazeFull) };

        /// <summary>Storm medium: x extinction at the ground (1/m), y 1/scale height, z swirl, w haze cap.</summary>
        public static float[] StormGlobal(float strength, float storm)
        {
            float s = Clamp01(storm) * S.atmoStorm * Math.Max(0, Math.Min(strength, 1.5f));
            float cap = S.atmoMax + (S.atmoStormMax - S.atmoMax) * Clamp01(s);
            return new[] { S.atmoStormDensity * s, 1f / S.atmoStormHeight, S.atmoStormSwirl * Clamp01(s * 4), cap };
        }

        /// <summary>
        /// Optical depth of an exponential layer density·exp(-k·(h − base)) along a straight segment (h relative to the base;
        /// below it the base density). Same as <see cref="WorldV2.FogOpticalDepth"/>; the shader's G3AtmoOD.
        /// </summary>
        public static float LayerOpticalDepth(float distance, float h0, float h1, float density, float k) =>
            WorldV2.FogOpticalDepth(distance, h0, h1, density, k);

        /// <summary>Henyey-Greenstein phase (1/sr) for cos(angle to the sun) <paramref name="c"/> and anisotropy g.</summary>
        public static float PhaseHG(float c, float g)
        {
            float g2 = g * g;
            return (float)((1 - g2) / (4 * Math.PI * Math.Pow(Math.Max(1 + g2 - 2 * g * c, 1e-4), 1.5)));
        }

        /// <summary>grade-1: soft cap (luminance) of the dust's in-scattered radiance: atmoHazeBright x G3's horizon luminance (0 = none).</summary>
        public static float HazeRadianceCap(float horizonLuminance) => S.atmoHazeBright > 0 ? Math.Max(0.02f, S.atmoHazeBright * Math.Max(horizonLuminance, 0)) : 0;

        /// <summary>Haze cap for the weather: atmoMax in clear air, atmoStormMax in a full storm.</summary>
        public static float HazeCap(float storm) => S.atmoMax + (S.atmoStormMax - S.atmoMax) * Clamp01(storm * S.atmoStorm);

        // ------------------------------------------------------------------ grade (Atmosphere pass 1)

        /// <summary>
        /// Lottes (GDC 2016) filmic curve f(x) = x^a / (x^(a d) b + c) with mid grey 0.18 mapped to <paramref name="midOut"/>
        /// and f(white) = 1: returns (b, c) for contrast a, shoulder d and white point.
        /// </summary>
        public static (float b, float c) LottesBC(float a, float d, float white, float midOut = 0.18f)
        {
            double mid = 0.18;
            double ma = Math.Pow(mid, a), mad = Math.Pow(mid, a * d), wa = Math.Pow(white, a), wad = Math.Pow(white, a * d);
            double den = (wad - mad) * midOut;
            double b = (-ma + wa * midOut) / den;
            double c = (wad * ma - wa * mad * midOut) / den;
            return ((float)b, (float)c);
        }

        /// <summary>The filmic curve (see <see cref="LottesBC"/>), clamped to [0, 1].</summary>
        public static float Filmic(float x, float a, float d, float b, float c)
        {
            if (x <= 0) return 0;
            double xa = Math.Pow(x, a);
            return (float)Math.Min(1.0, xa / (Math.Pow(xa, d) * b + c));
        }

        /// <summary>Shoulder exponent d of the filmic curve.</summary>
        public const float Shoulder = 0.97f;

        /// <summary>
        /// Grade globals: x weight (0 = off), y contrast a, z b, w c of the filmic curve (<see cref="LottesBC"/>). Golden hour
        /// adds a little contrast, a storm takes most of it (flat dusty light).
        /// </summary>
        public static float[] GradeGlobal(float w, float elevationDeg, float storm)
        {
            float g = Clamp01(w) * S.atmoGrade * Day(elevationDeg);   // night: G3's own (no deeper toe in the dark)
            if (g <= 0) return new float[4];
            float a = 1 + (S.atmoContrast - 1) * (1 + 0.3f * Golden(elevationDeg) * S.atmoGolden) * (1 - 0.6f * Clamp01(storm));
            var (b, c) = LottesBC(a, Shoulder, S.atmoWhite, S.atmoMid);
            return new[] { g, a, b, c };
        }

        /// <summary>Grade globals 2: x exposure (noon high key), y saturation offset, z path-to-white, w unused.</summary>
        public static float[] Grade2Global(float w, float elevationDeg, float storm)
        {
            float g = Clamp01(w) * S.atmoGrade;
            float exposure = 1 + S.atmoHighKey * g * Noon(elevationDeg) * (1 - Clamp01(storm));
            // grade-1: golden hour +0.03 (was +0.08: the orange-saturated evening)
            float sat = g * (S.atmoSaturation + 0.03f * S.atmoGolden * Golden(elevationDeg) - 0.15f * Clamp01(storm));
            return new[] { exposure, sat, 0.25f, 0 };
        }

        /// <summary>
        /// Split-tone tints (luminance 1 each): shadows cooler (sky-lit shade), highlights warmer; golden hour pushes the
        /// highlights to amber and the shade a little violet. Returns (shadow rgb, highlight rgb) already weighted.
        /// </summary>
        public static (float[] shadow, float[] high) SplitTints(float w, float elevationDeg, float storm)
        {
            float k = Clamp01(w) * S.atmoGrade * S.atmoSplit * (1 - 0.7f * Clamp01(storm));
            float gold = Golden(elevationDeg) * S.atmoGolden;
            float[] sh = { 1 - 0.03f * k * (1 - 0.3f * gold), 1 - 0.005f * k, 1 + 0.05f * k };   // a touch cool: the shade stays dark and warm
            float[] hi = { 1 + (0.05f + 0.05f * gold) * k, 1 + 0.01f * k, 1 - (0.1f + 0.08f * gold) * k };   // grade-1: golden amber 0.07/0.12 -> 0.05/0.08
            return (Normalize(sh), Normalize(hi));
        }

        // ------------------------------------------------------------------ sky, motes

        /// <summary>Sky globals: x aureole/disc strength (desert daylight), y zenith deepening of the model sky, z cloud coverage change, w Mie g.</summary>
        public static float[] SkyGlobal(float w, float elevationDeg, float storm)
        {
            float glow = Clamp01(w) * S.atmoSkyGlow * Smooth(-4, 2, elevationDeg);
            return new[] { glow, S.atmoSkyDeepen, S.atmoClouds * Clamp01(w) * (1 - Clamp01(storm)), S.atmoMie };
        }

        /// <summary>
        /// Single-scattering sky radiance (relative, sun 20) towards unit direction v for the unit direction to the sun sd:
        /// Rayleigh (8 km) + Mie (1.2 km, turbidity x 21e-6 /m, Cornette-Shanks g), 12 view steps, 4 light steps, the planet
        /// shadowing the sun. Mirrors G3/Sky's AtmoSkyPB.
        /// </summary>
        public static float[] SkyRadiance(double vx, double vy, double vz, double sx, double sy, double sz, double turbidity, double g)
        {
            const double Rg = 6360e3, Rt = 6420e3, HR = 8000, HM = 1200;
            double[] bR = { 5.8e-6, 13.5e-6, 33.1e-6 };
            double bM = 21e-6 * turbidity;
            vy = Math.Max(vy, 0);
            double vl = Math.Sqrt(vx * vx + vy * vy + vz * vz); vx /= vl; vy /= vl; vz /= vl;
            double oy = Rg + 200;
            double tmax = SphereFar(0, oy, 0, vx, vy, vz, Rt);
            double dt = tmax / 12, odR = 0, odM = 0;
            var sumR = new double[3]; var sumM = new double[3];
            for (int i = 0; i < 12; i++)
            {
                double t = (i + 0.5) * dt;
                double px = vx * t, py = oy + vy * t, pz = vz * t;
                double h = Math.Sqrt(px * px + py * py + pz * pz) - Rg;
                double hr = Math.Exp(-h / HR) * dt, hm = Math.Exp(-h / HM) * dt;
                odR += hr; odM += hm;
                double tl = SphereFar(px, py, pz, sx, sy, sz, Rt), dl = tl / 4, odRl = 0, odMl = 0;
                bool lit = true;
                for (int j = 0; j < 4; j++)
                {
                    double u = (j + 0.5) * dl;
                    double lx = px + sx * u, ly = py + sy * u, lz = pz + sz * u;
                    double hl = Math.Sqrt(lx * lx + ly * ly + lz * lz) - Rg;
                    if (hl < 0) lit = false;
                    odRl += Math.Exp(-Math.Max(hl, 0) / HR) * dl;
                    odMl += Math.Exp(-Math.Max(hl, 0) / HM) * dl;
                }
                if (!lit) continue;
                for (int c = 0; c < 3; c++)
                {
                    double att = Math.Exp(-(bR[c] * (odR + odRl) + bM * 1.1 * (odM + odMl)));
                    sumR[c] += att * hr;
                    sumM[c] += att * hm;
                }
            }
            double mu = vx * sx + vy * sy + vz * sz;
            double pr = 3.0 / (16.0 * Math.PI) * (1 + mu * mu);
            double g2 = g * g;
            double pm = 3.0 / (8.0 * Math.PI) * ((1 - g2) * (1 + mu * mu)) / ((2 + g2) * Math.Pow(Math.Max(1 + g2 - 2 * g * mu, 1e-4), 1.5));
            var o = new float[3];
            for (int c = 0; c < 3; c++) o[c] = (float)(20 * (sumR[c] * bR[c] * pr + sumM[c] * bM * pm));
            return o;
        }

        static double SphereFar(double ox, double oy, double oz, double dx, double dy, double dz, double r)
        {
            double b = ox * dx + oy * dy + oz * dz, c = ox * ox + oy * oy + oz * oz - r * r;
            double disc = b * b - c;
            return disc < 0 ? 0 : -b + Math.Sqrt(disc);
        }

        /// <summary>
        /// Physically based sky globals: x blend over G3's dome (desert daylight incl. twilight, not overcast), y Mie
        /// turbidity, z luminance scale (the model's side view 10 degrees up = G3's zenith/horizon mix 1:3: G3's day curves
        /// keep the brightness; the shader deepens the zenith by atmoSkyDeepen), w soft cap of its luminance (atmoSkyPBCap x that mean; Mie g 0.76). <paramref name="g3Zenith"/>/<paramref name="g3Horizon"/>:
        /// linear luminance of G3's SkyColor/HazeColor now.
        /// </summary>
        public static float[] SkyPBGlobal(float w, float elevationDeg, float storm, float sx, float sy, float sz, float g3Zenith, float g3Horizon)
        {
            float blend = Clamp01(w) * S.atmoSkyPB * Smooth(-7, -1, elevationDeg) * (1 - Clamp01(storm));
            if (blend <= 0) return new float[4];
            double turb = S.atmoSkyTurbidity, g = 0.76;
            // side view: perpendicular to the sun's azimuth, 10 degrees up
            double ax = -sz, az = sx, al = Math.Sqrt(ax * ax + az * az);
            if (al < 1e-4) { ax = 1; az = 0; al = 1; }
            double c10 = Math.Cos(10 * Math.PI / 180), s10 = Math.Sin(10 * Math.PI / 180);
            // matched on the side view 10 degrees up (the bulk of the visible dome); the zenith is deepened in the shader
            var hor = SkyRadiance(ax / al * c10, s10, az / al * c10, sx, sy, sz, turb, g);
            float deep = 1 - S.atmoSkyDeepen * (float)Math.Pow(s10, 0.8);
            float model = Luma(hor[0], hor[1], hor[2]) * deep;
            float target = (0.25f * g3Zenith + 0.75f * g3Horizon) * S.atmoSkyPBBright;
            float k = model > 1e-6f ? target / model : 0;
            k = Math.Max(0.05f, Math.Min(k, 50f));
            return new[] { blend, (float)turb, k, Math.Max(0.05f, target * S.atmoSkyPBCap) };
        }

        /// <summary>Motes drawn round the camera: calm daylight atmoMoteCount, full storm atmoMoteStormCount (any hour), none at night or off the desert.</summary>
        public static int MoteCount(float w, float elevationDeg, float storm)
        {
            if (S.atmoMotes <= 0 || w < 0.05f) return 0;
            float n = Math.Min(1, w * 2) * (S.atmoMoteCount * Day(elevationDeg) * (1 - Clamp01(storm)) + S.atmoMoteStormCount * Clamp01(storm));
            return Math.Max(0, Math.Min(8192, (int)n));
        }

        /// <summary>
        /// grade-1: the purple excess p = max(min(r, b) - g, 0) x <paramref name="k"/> removed (red fully, blue half) and the
        /// luminance restored: lavender -> blue-grey, pink -> salmon, blue / orange / grey untouched. Mirrors G3/Sky.
        /// </summary>
        public static float[] DePurple(float r, float g, float b, float k)
        {
            float l0 = Luma(r, g, b);
            float p = Math.Max(Math.Min(r, b) - g, 0) * Clamp01(k);
            r -= p; b -= 0.5f * p;
            float l1 = Luma(r, g, b);
            float s = l1 > 1e-5f ? l0 / l1 : 1;
            return new[] { r * s, g * s, b * s };
        }

        // ------------------------------------------------------------------ helpers

        public static float Smooth(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        static float[] Normalize(float[] c)
        {
            float l = Luma(c[0], c[1], c[2]);
            return new[] { c[0] / l, c[1] / l, c[2] / l };
        }

        static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
