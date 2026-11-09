using System;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free part of the remaster snow (Docs/remaster-world.md section 12): which v2 layers are snow, the snowfall and
    /// fresh-snow curve, the cover on top faces, the trail map window (toroidal, follows the camera), the refill clock of the
    /// prints, the footprint shape and the foot-plant detector. The shader (G3RemasterWorldV2.shader, G3RemasterSnow.hlsl)
    /// and the compute shader (Resources/G3SnowTrails.compute) mirror the functions marked so; tested in SnowV2Tests.
    /// </summary>
    public static class SnowV2
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>HLSL smoothstep.</summary>
        public static float Smooth(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        // ------------------------------------------------------------------ which layers are snow

        /// <summary>
        /// Weight of a v2 ground layer being snow, from its CC0 scan's mean (linear luminance, saturation): the snow scan
        /// (polyhaven snow_02: 0.38 / 0.03) is the only bright, grey ground scan; G3's own snow images are no help (0.09..0.15
        /// luminance, as dark as Nordmar's grey rock). Kind 1 (ground) only: the bright grey metal scan is a surface.
        /// Mirrors G3SnowScanWeight.
        /// </summary>
        public static float ScanWeight(int kind, float luma, float saturation) =>
            kind != 1 ? 0 : Smooth(0.28f, 0.33f, luma) * (1 - Smooth(0.08f, 0.14f, saturation));

        // ------------------------------------------------------------------ snowfall, fresh snow

        /// <summary>Snowfall of one G3 weather state: Snowy(Nordmar) 1, Snowstorm(Nordmar) 1.2 (G3: 1000 / 1200 flakes/s), else 0.</summary>
        public static float SnowfallOf(string state)
        {
            if (string.IsNullOrEmpty(state) || state.IndexOf("snow", StringComparison.OrdinalIgnoreCase) < 0) return 0;
            return state.IndexOf("storm", StringComparison.OrdinalIgnoreCase) >= 0 ? 1.2f : 1f;
        }

        /// <summary>
        /// Snowfall of G3Environment's state blend, as G3 shows precipitation (G3WeatherDirector.UpdatePrecipitation): only the
        /// dominant state (weight &gt; 50 %) falls, at (weight − 0.5) · 2 (WeatherRules.Dominant). fix-178 (owner: "снег постоянно идёт?"): the linear mix
        /// counted a snowy state from its first percent of a fade (G3's Nordmar fades last 3-15 game hours), so the fresh snow
        /// built up and the tracks were covered while the sky was clear and no flake fell.
        /// </summary>
        public static float Snowfall(string state, string blendState, float blend)
        {
            float b = Clamp01(blend);
            return b > 0.5f ? SnowfallOf(blendState) * Math.Min(1f, (b - 0.5f) * 2f) : b < 0.5f ? SnowfallOf(state) * Math.Min(1f, (0.5f - b) * 2f) : 0f;
        }

        /// <summary>
        /// fix-215: <see cref="Snowfall(string, string, float)"/> over the director's whole state mix (G3Environment.StateMix):
        /// the one state above 50 % falls at (w − 0.5) · 2. Empty → null (use the two-state form).
        /// </summary>
        public static float? Snowfall(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, float>> mix)
        {
            if (mix == null || mix.Count == 0) return null;
            float sum = 0;
            for (int i = 0; i < mix.Count; i++) sum += Math.Max(0f, mix[i].Value);
            if (sum <= 0) return null;
            for (int i = 0; i < mix.Count; i++)
            {
                float w = mix[i].Value / sum;
                if (w > 0.5f) return SnowfallOf(mix[i].Key) * Math.Min(1f, (w - 0.5f) * 2f);
            }
            return 0f;
        }

        /// <summary>
        /// Fresh snow (0..1) after dt seconds: grows linearly with the snowfall (a full layer after <c>snowFreshBuild</c> s
        /// of steady snowfall), settles away exponentially without it (<c>snowFreshMelt</c>).
        /// </summary>
        public static float FreshStep(float current, float snowfall, float dt)
        {
            dt = Math.Max(dt, 0);
            if (snowfall > 0.01f) return Clamp01(current + snowfall * dt / S.snowFreshBuild);
            return Clamp01(current * (float)Math.Exp(-dt / S.snowFreshMelt));
        }

        // ------------------------------------------------------------------ cover on top faces (mirrors the shader)

        /// <summary>
        /// How much of the lasting + fresh snow lands on a surface: regions (1 in Nordmar), ground layers that are not
        /// snow get only the fresh layer, everything else the lasting cover plus the fresh one; none under a roof.
        /// </summary>
        public static float CoverAmount(bool groundLayer, float region, float fresh, float shelter)
        {
            float k = groundLayer ? fresh * S.snowFresh * 1.6f : S.snowAccumulation + fresh * S.snowFresh;
            return Clamp01(k) * Clamp01(region) * (1 - Clamp01(shelter));
        }

        /// <summary>
        /// Snow on a face with world up-component <paramref name="up"/> (normal y) at cover <paramref name="amount"/>: the
        /// threshold drops from flat-only (0.95) to fairly steep (0.45) as the cover grows; <paramref name="bias"/> = noise
        /// and height-map relief (−0.5..0.5) so the edge follows the surface. Mirrors the shader's G3SnowCover.
        /// </summary>
        public static float Cover(float up, float amount, float bias)
        {
            float thr = 0.95f - 0.5f * Clamp01(amount);
            return Smooth(thr, thr + 0.15f, up + bias * 0.3f) * Clamp01(amount * 2);
        }

        /// <summary>
        /// Packed (trodden road) snow of a G3 ground image stem: G3's snow path and the town's snow ground (1), else 0. The
        /// builder stores it per layer in _V2Snow (shader: greyer, glossier, lumpy, no ripples, fewer glints; fresh snow
        /// covers it).
        /// </summary>
        public static float PackedSnow(string stem) =>
            stem != null && (stem.StartsWith("G3_Nature_Snow_Path", StringComparison.OrdinalIgnoreCase) ||
                             stem.StartsWith("G3_Architecture_Ground_City_Snow", StringComparison.OrdinalIgnoreCase)) ? 1f : 0f;

        /// <summary>Snow-region weight of a manifest region name (Nordmar 1).</summary>
        public static float RegionWeight(string region) =>
            region != null && region.IndexOf("nordmar", StringComparison.OrdinalIgnoreCase) >= 0 ? 1f : 0f;

        // ------------------------------------------------------------------ print grid (toroidal cells round the camera)

        /// <summary>Print slots per grid cell (the shader reads them as two uint4).</summary>
        public const int Slots = 8;

        /// <summary>An empty slot.</summary>
        public const uint Empty = 0xFFFFFFFFu;

        /// <summary>Cell size (m) of a grid of <paramref name="cells"/> per side over <paramref name="extent"/> m.</summary>
        public static float CellSize(float extent, int cells) => extent / Math.Max(cells, 1);

        /// <summary>Mathematical modulo (never negative).</summary>
        public static int Mod(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }

        /// <summary>World cell coordinate of a world position (floor).</summary>
        public static int WorldCell(float x, float cell) => (int)Math.Floor(x / cell);

        /// <summary>First slot of a world cell in the toroidal grid (cells repeat every <paramref name="cells"/>: nothing is
        /// copied when the camera moves; a print stored for a cell 64 m away is simply not near the pixel). Mirrors the shader.</summary>
        public static int SlotBase(int cx, int cz, int cells) => (Mod(cz, cells) * cells + Mod(cx, cells)) * Slots;

        /// <summary>Radius round a print's centre that its shape (sole/pads + rim) can reach, m.</summary>
        public static float Reach(float halfLength, float halfWidth) => Math.Max(halfLength, halfWidth) * 1.15f + RimWidth + 0.03f;

        /// <summary>Width of the pushed-up ridge round a print, m.</summary>
        public const float RimWidth = 0.035f;

        /// <summary>Soft wall of a print (m): the shape's edge ramps over this (plus the pixel footprint in the shader: no
        /// stair steps or saw teeth at any distance).</summary>
        public const float Wall = 0.016f;

        // ------------------------------------------------------------------ refill clock

        /// <summary>The clock sent to the GPU wraps at this value (float: 1/2000 step at 4096).</summary>
        public const float ClockWrap = 4096f;

        /// <summary>A print is gone (its slots are free again) at this age.</summary>
        public const float AgeGone = 1.2f;

        /// <summary>
        /// Refill clock after dt: one unit = one full refill; <c>snowTrailRefill</c> s without snowfall (wind drift),
        /// <c>snowTrailRefillSnowing</c> s under full snowfall.
        /// </summary>
        public static double ClockStep(double clock, float snowfall, float dt)
        {
            float rate = 1f / S.snowTrailRefill + Clamp01(snowfall) * (1f / S.snowTrailRefillSnowing - 1f / S.snowTrailRefill);
            return clock + Math.Max(dt, 0) * rate;
        }

        /// <summary>The clock as stored / sent to the GPU (wrapped).</summary>
        public static float Wrapped(double clock) => (float)(clock - Math.Floor(clock / ClockWrap) * ClockWrap);

        /// <summary>Age of a stamp (refill units) from wrapped clocks. Mirrors G3SnowAge.</summary>
        public static float Age(float now, float stamped)
        {
            float a = now - stamped;
            return a < 0 ? a + ClockWrap : a;
        }

        /// <summary>Depth left of a print of this age: stays sharp, then fills in (0 from age 1). Mirrors G3SnowTrailFade.</summary>
        public static float Fade(float age) => 1 - Smooth(0.3f, 1f, age);

        // ------------------------------------------------------------------ level

        /// <summary>A print shows on a surface only within this height of where it was made (no prints on a ledge above).
        /// <paramref name="distance"/> = surface y minus print ground y. Mirrors the shader.</summary>
        public static float HeightMatch(float distance) => 1 - Smooth(0.35f, 0.8f, Math.Abs(distance));

        // ------------------------------------------------------------------ footprint shapes (mirrors G3RemasterSnowPrints.hlsl)

        /// <summary>Print kinds: a boot sole, an animal paw (main pad + four toe pads), a round trail (rigs without feet).</summary>
        public const int Boot = 0, Paw = 1, Round = 2;

        public struct Shape { public float depth, rim; }

        static float Pow(float a, float b) => (float)Math.Pow(a, b);
        static float Exp(float a) => (float)Math.Exp(a);
        static float Sqrt(float a) => (float)Math.Sqrt(a);
        static float Sq(float a) => a * a;

        /// <summary>
        /// Depth (0..1) and rim (0..1) of one print at local metres (<paramref name="u"/> along the walk direction,
        /// <paramref name="v"/> across, <paramref name="side"/> -1 left foot / +1 right) for a print of half length/width
        /// <paramref name="hl"/>/<paramref name="hw"/>, evaluated analytically (no texture: smooth at any distance; the shader
        /// widens <paramref name="soft"/> by the pixel footprint). Boot: a sole outline (superellipse, heel narrower than the
        /// ball, a waist), flat floor with a shallower arch and a deeper heel, soft wall, a rim that is higher at the toe
        /// (push-off). Paw: a shallow oval dent with a main pad and four toe pads pressed deeper. Round: an oval.
        /// </summary>
        public static Shape Print(float u, float v, float hl, float hw, int kind, float side, float soft)
        {
            hl = Math.Max(hl, 1e-3f); hw = Math.Max(hw, 1e-3f); soft = Math.Max(soft, 1e-3f);
            var o = new Shape();
            float x = u / hl;
            if (kind == Boot)
            {
                float wf = 0.76f + 0.24f * Smooth(-0.2f, 0.45f, x) - 0.1f * Exp(-Sq((x + 0.05f) / 0.22f));
                float vv = v * side - 0.06f * hw * Smooth(0.1f, 0.8f, x);   // the ball sits a little to the inside
                float y = vv / (hw * wf);
                float rd = Pow(Pow(Math.Abs(x), 2.6f) + Pow(Math.Abs(y), 2.6f), 1 / 2.6f);
                float sd = (rd - 1) * Math.Min(hl, hw * wf);   // ~ metres outside the outline
                float floor = 1 - 0.2f * Exp(-Sq((x + 0.08f) / 0.2f)) - 0.2f * Smooth(0.75f, 1.05f, x) + 0.08f * Exp(-Sq((x + 0.7f) / 0.2f));
                o.depth = (1 - Smooth(-soft, soft * 0.35f, sd)) * Clamp01(floor);
                o.rim = Smooth(-soft * 0.2f, soft, sd) * (1 - Smooth(soft, soft + RimWidth, sd)) * (0.55f + 0.45f * Smooth(0.2f, 1f, x));
                return o;
            }
            float y0 = v / hw;
            float r0 = Sqrt(x * x + y0 * y0);
            float sd0 = (r0 - 1) * Math.Min(hl, hw);
            if (kind == Round)
            {
                o.depth = 1 - Smooth(-soft, soft * 0.35f, sd0);
                o.rim = Smooth(-soft * 0.2f, soft, sd0) * (1 - Smooth(soft, soft + RimWidth, sd0)) * 0.7f;
                return o;
            }
            // paw: main (metacarpal) pad behind, two front toes, two outer toes a little behind them; a shallow dent round all
            // the pads are ~1 cm: a sharper wall than the sole's (the shader's pixel-footprint part of soft still filters them)
            float sp = Math.Max(soft - 0.7f * Wall, 0.005f);
            float d = 0.35f * (1 - Smooth(-soft, soft * 0.35f, sd0 + 0.1f * Math.Min(hl, hw)));
            d = Math.Max(d, Pad(u, v, -0.32f * hl, 0, 0.36f * hl, 0.5f * hw, sp));
            d = Math.Max(d, Pad(u, v, 0.46f * hl, 0.2f * hw, 0.2f * hl, 0.22f * hw, sp) * 0.9f);
            d = Math.Max(d, Pad(u, v, 0.46f * hl, -0.2f * hw, 0.2f * hl, 0.22f * hw, sp) * 0.9f);
            d = Math.Max(d, Pad(u, v, 0.12f * hl, 0.62f * hw, 0.19f * hl, 0.2f * hw, sp) * 0.85f);
            d = Math.Max(d, Pad(u, v, 0.12f * hl, -0.62f * hw, 0.19f * hl, 0.2f * hw, sp) * 0.85f);
            o.depth = d;
            o.rim = Smooth(-soft * 0.2f, soft, sd0) * (1 - Smooth(soft, soft + RimWidth * 0.7f, sd0)) * 0.6f;
            return o;
        }

        /// <summary>One pad of a paw: an oval at (cu, cv) with radii (ru, rv), soft edge.</summary>
        static float Pad(float u, float v, float cu, float cv, float ru, float rv, float soft)
        {
            float a = (u - cu) / ru, b = (v - cv) / rv;
            float sd = (Sqrt(a * a + b * b) - 1) * Math.Min(ru, rv);
            return 1 - Smooth(-soft * 0.6f, soft * 0.25f, sd);
        }

        /// <summary>Print size of a character of standing height h (m): humans ~0.29 x 0.11 m, wolves' paws ~0.11 x 0.09 m.</summary>
        public static (float halfLength, float halfWidth) PrintSize(float height, bool paw) => paw
            ? (Math.Max(0.035f, Math.Min(0.12f, height * 0.055f)), Math.Max(0.03f, Math.Min(0.1f, height * 0.045f)))
            : (Math.Max(0.08f, Math.Min(0.3f, height * 0.08f)), Math.Max(0.035f, Math.Min(0.12f, height * 0.031f)));

        /// <summary>Bones that leave prints: G3 rigs name them &lt;Rig&gt;_(Left|Right)_Foot_Foot_1 (feet) and _Hand_Hand_1
        /// (the front paws of quadrupeds; human hands only print when they are on the ground).</summary>
        public static bool IsFootBone(string name) => name != null &&
            (name.EndsWith("_Left_Foot_Foot_1", StringComparison.Ordinal) || name.EndsWith("_Right_Foot_Foot_1", StringComparison.Ordinal));

        public static bool IsPawBone(string name) => name != null &&
            (name.EndsWith("_Left_Hand_Hand_1", StringComparison.Ordinal) || name.EndsWith("_Right_Hand_Hand_1", StringComparison.Ordinal));

        /// <summary>The rig prefix of a print bone ("Hero_Left_Foot_Foot_1" → "Hero"); null when it has none.</summary>
        public static string RigOfBone(string name)
        {
            if (name == null) return null;
            int i = name.IndexOf("_Left_", StringComparison.Ordinal);
            if (i < 0) i = name.IndexOf("_Right_", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : null;
        }

        /// <summary>
        /// fix-193: whether a character prints paws (front and back) rather than boots. Known rigs decide by their kind: the
        /// hero/human, orc and goblin skeletons walk on their feet and their hands never print, the four-legged animals print
        /// paws. Other rigs by their pose when first seen (<paramref name="footY"/> / <paramref name="pawY"/>: the lowest
        /// foot and front-paw bone above the root, float.MaxValue when the rig has none): front paws hanging about as low as the
        /// feet. The pose alone misread the hero as a quadruped when he was first seen swimming, crouched or just turned back
        /// from an animal form (wolf prints from the human hero in the snow).
        /// </summary>
        public static bool IsQuadruped(string rig, float footY, float pawY, float height)
        {
            if (pawY == float.MaxValue || footY == float.MaxValue) return false;   // feet only (or paws only): one kind of bone
            switch (rig)
            {
                case "Hero": case "Human": case "Orc": case "Goblin":
                    return false;
                case "Wolf": case "Boar": case "Bison": case "Cow": case "Deer": case "Pig": case "Rabbit": case "Rhino":
                case "Sabertooth": case "Shadowbeast": case "Alligator": case "Waran":
                    return true;
                default:
                    return pawY < footY + 0.25f * height;
            }
        }

        // ------------------------------------------------------------------ foot plant detector

        /// <summary>
        /// One foot (or paw) of a character: a print when it comes down after a swing - its horizontal speed drops below a
        /// fraction of the body's (or below 0.25 m/s for slow bodies) while it is low (within 7 cm of its lowest height
        /// above the character's root, learnt as it walks). Like G3HeroAnimator's step events, but for every rig.
        /// </summary>
        public struct FootPlant
        {
            public float lastX, lastZ, lastStep;
            public float rest;           // lowest height above the root seen (learnt; rises slowly)
            public bool valid, swinging;

            public const float SwingRatio = 0.9f, PlantRatio = 0.35f, Low = 0.07f, MinInterval = 0.18f;

            /// <summary>Feeds one frame; true = the foot was planted this frame (a print).</summary>
            public bool Update(float x, float height, float z, float bodySpeed, float dt, float time)
            {
                if (!valid || dt <= 0 || dt > 0.1f)
                {
                    lastX = x; lastZ = z; valid = dt > 0 && dt <= 0.1f; swinging = false;
                    if (!(rest > 0) || height < rest) rest = Math.Max(height, 0.001f);
                    return false;
                }
                float dx = x - lastX, dz = z - lastZ;
                lastX = x; lastZ = z;
                float speed = (float)Math.Sqrt(dx * dx + dz * dz) / dt;
                rest = height < rest ? Math.Max(height, 0.001f) : rest + dt * 0.02f;   // re-learn slowly (slopes, other gaits)
                bool low = height < rest + Low;
                float body = Math.Max(bodySpeed, 0.3f);
                if (speed > body * SwingRatio || !low) { swinging = true; return false; }
                if (swinging && low && speed < body * PlantRatio)
                {
                    swinging = false;
                    if (time - lastStep < MinInterval) return false;
                    lastStep = time;
                    return true;
                }
                return false;
            }
        }

        // ------------------------------------------------------------------ glints (mirrors G3SnowGlintLevel)

        /// <summary>Largest tilt of a crystal facet from the surface normal (tan 40 deg).</summary>
        public const float GlintTilt = 0.84f;

        /// <summary>
        /// Sparkle (0..1) of one grain whose facet has random numbers (<paramref name="r0"/>, <paramref name="r1"/>) in [0, 1):
        /// the facet leans <c>sqrt(r1) * GlintTilt</c> (area-uniform over the cone's disc) towards azimuth 2 pi r0 off the normal
        /// (nx, ny, nz); it sparkles when it mirrors the sun (half vector h) within 2-5 deg. fix-178: the facets spread over a
        /// wide cone, so sparkles are sparse and spread over all sunlit snow, not a patch round the surface's own mirror lobe.
        /// </summary>
        public static float GlintLobe(float nx, float ny, float nz, float hx, float hy, float hz, float r0, float r1)
        {
            // tangent frame as the shader: t1 = normalize(cross(n, up or x)), t2 = cross(n, t1)
            float ax = 0, ay = 1, az = 0;
            if (Math.Abs(ny) >= 0.99f) { ax = 1; ay = 0; }
            float t1x = ny * az - nz * ay, t1y = nz * ax - nx * az, t1z = nx * ay - ny * ax;
            float l = Sqrt(t1x * t1x + t1y * t1y + t1z * t1z);
            t1x /= l; t1y /= l; t1z /= l;
            float t2x = ny * t1z - nz * t1y, t2y = nz * t1x - nx * t1z, t2z = nx * t1y - ny * t1x;
            double ang = r0 * 2 * Math.PI;
            float rad = Sqrt(r1) * GlintTilt, c = (float)Math.Cos(ang) * rad, si = (float)Math.Sin(ang) * rad;
            float mx = nx + t1x * c + t2x * si, my = ny + t1y * c + t2y * si, mz = nz + t1z * c + t2z * si;
            float ml = Sqrt(mx * mx + my * my + mz * mz);
            float hl = Sqrt(hx * hx + hy * hy + hz * hz);
            return Smooth(0.99619f, 0.99939f, (mx * hx + my * hy + mz * hz) / (ml * hl));
        }

        /// <summary>
        /// The glint grain's three random numbers in [0, 1) (mirrors G3SnowGrainHash: pcg3d of the grain id and the level salt,
        /// Jarzynski and Olano 2020). fix-194: the sin hash it replaced lost all fp32 precision at grain ids ~1e5 (a few mm
        /// grains, hundreds of metres from the origin), so neighbouring grains sparkled together in rows.
        /// </summary>
        public static (float r0, float r1, float r2) GrainHash(int idx, int idz, uint salt)
        {
            uint x = unchecked((uint)idx * 1664525u + 1013904223u), y = unchecked((uint)idz * 1664525u + 1013904223u),
                z = unchecked(salt * 1664525u + 1013904223u);
            unchecked
            {
                x += y * z; y += z * x; z += x * y;
                x ^= x >> 16; y ^= y >> 16; z ^= z >> 16;
                x += y * z; y += z * x; z += x * y;
            }
            const float k = 1f / 16777216f;
            return ((x >> 8) * k, (y >> 8) * k, (z >> 8) * k);
        }

        // ------------------------------------------------------------------ globals

        /// <summary>_G3Snow: x on (1), y fresh snow 0..1, z lasting cover on top faces, w glint strength.</summary>
        public static float[] SnowGlobal(bool on, float fresh) => on ? new[] { 1f, Clamp01(fresh), S.snowAccumulation, S.snowGlint } : new float[4];

        /// <summary>_G3Snow2: x wrap, y scattered light, z blue shadow, w ripples.</summary>
        public static float[] Snow2Global => new[] { S.snowWrap, S.snowScatter, S.snowBlueShadow, S.snowRipples };

        /// <summary>_G3Snow4: x snow albedo lift, y G3 pattern flattening, z the albedo the lift approaches, w 1 = the glints,
        /// gloss and sky reflection before fix-178 (<see cref="LegacyLook"/>, A/B shots only).</summary>
        public static float[] Snow4Global => new[] { S.snowBrightness, S.snowFlatten, S.snowWhite, LegacyLook ? 1f : 0f };

        /// <summary>fix-178 A/B: the snow glints, gloss and sky reflection as before (runner / shots only; not stored).</summary>
        public static bool LegacyLook { get; set; }

        /// <summary>_G3Snow3: x fresh snow cover amount, y glint density, z, w wind direction (unit, world x z).</summary>
        public static float[] Snow3Global
        {
            get
            {
                double a = S.snowWindAngle * Math.PI / 180;
                return new[] { S.snowFresh, S.snowGlintDensity, (float)Math.Cos(a), (float)Math.Sin(a) };
            }
        }
    }
}
