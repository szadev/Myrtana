using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free part of the remaster desert sand (Docs/remaster-world.md section 22): which v2 ground layers are sand and
    /// of which kind (loose dune sand, wet shore sand, packed path sand), the desert region (Varant), the wind clock that
    /// fills sand prints in (calm wind slowly, a sandstorm in seconds), the wind ripples (a Swift-Hohenberg ripple tile, <see cref="SandRipples"/>,
    /// sampled as two domain-warped fields across the prevailing wind, in amplitude patches, faded on steep and lee slopes, layer
    /// edges, by the pixel footprint and distance), the sand-wisp amount, and the globals.
    /// The shader (G3RemasterSand.hlsl) mirrors the functions marked so; tested in SandV2Tests.
    /// </summary>
    public static class SandV2
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ------------------------------------------------------------------ which layers are sand

        /// <summary>Sand class of a v2 layer (stored in <c>_V2Snow.z</c> / <c>.w</c> for layer A / B).</summary>
        public const int None = 0, Loose = 1, Wet = 2, Packed = 3;

        /// <summary>
        /// Sand class of a G3 ground image stem. G3's desert terrain (Docs/remaster-world.md 22.2): <c>Dunes_PlainSand_01</c>
        /// (open dunes, 11 terrain materials) and <c>Dunes_WaveSand_01</c> (the rippled dune layer) are loose sand;
        /// <c>Dunes_PlainSand_02_Diffuse_03</c> is the wet shore sand (Plainsand_To_Wetsand, the coast transitions);
        /// <c>Dunes_PlainSand_02</c> (Diffuse_01: PlainSand_to_Path) and <c>Architecture_SandGround</c> are packed path
        /// sand. Everything else (rock, gravel, earth, grass, sandstone buildings) is not sand.
        /// </summary>
        public static int LayerClass(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return None;
            bool P(string p) => stem.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            if (P("G3_Nature_Dunes_PlainSand_02_Diffuse_03")) return Wet;
            if (P("G3_Nature_Dunes_PlainSand_02") || P("G3_Architecture_SandGround")) return Packed;
            if (P("G3_Nature_Dunes_Detail")) return None;   // detail multiplies, never a base layer
            if (P("G3_Nature_Dunes") || P("G3_Desert_Sand")) return Loose;
            return None;
        }

        /// <summary>Prints show on this class (loose and wet sand; never on paths, rock or anything else).</summary>
        public static bool TakesPrints(int cls) => cls == Loose || cls == Wet;

        /// <summary>Wind ripples on this class (dry loose sand only: wet and trodden sand do not ripple).</summary>
        public static bool Ripples(int cls) => cls == Loose;

        /// <summary>Desert-region weight of a manifest region name (Varant 1). Myrtana's coast sand stays as it was.</summary>
        public static float RegionWeight(string region) =>
            region != null && region.IndexOf("varant", StringComparison.OrdinalIgnoreCase) >= 0 ? 1f : 0f;

        /// <summary>
        /// Grows a region map (bytes, row-major w x h) by one cell: each cell takes the largest value of its 3x3 neighbourhood.
        /// The desert map is read bilinear (a 250 m ramp centred on the border); grown, the border cells themselves are full desert.
        /// </summary>
        public static void Dilate(byte[] map, int w, int h)
        {
            var src = (byte[])map.Clone();
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    byte m = 0;
                    for (int j = -1; j <= 1; j++)
                        for (int i = -1; i <= 1; i++)
                        {
                            int xx = x + i, zz = z + j;
                            if (xx < 0 || zz < 0 || xx >= w || zz >= h) continue;
                            if (src[zz * w + xx] > m) m = src[zz * w + xx];
                        }
                    map[z * w + x] = m;
                }
        }

        // ------------------------------------------------------------------ wind: storm share, print fill clock

        /// <summary>A G3 weather state that blows sand: Sandstormy(Varant).</summary>
        public static bool IsSandstorm(string state) =>
            state != null && state.IndexOf("sandstorm", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Share (0..1) of sandstorm states in G3Environment's state mix (weights normalized); empty → 0.</summary>
        public static float StormShare(IReadOnlyList<KeyValuePair<string, float>> mix)
        {
            if (mix == null || mix.Count == 0) return 0;
            float sum = 0, storm = 0;
            for (int i = 0; i < mix.Count; i++)
            {
                float w = Math.Max(0f, mix[i].Value);
                sum += w;
                if (IsSandstorm(mix[i].Key)) storm += w;
            }
            return sum > 0 ? Clamp01(storm / sum) : 0;
        }

        /// <summary>The two-state form (weather state, blend state, blend 0..1 towards the blend state).</summary>
        public static float StormShare(string state, string blendState, float blend)
        {
            float b = string.IsNullOrEmpty(blendState) ? 0 : Clamp01(blend);
            return (IsSandstorm(state) ? 1 - b : 0) + (IsSandstorm(blendState) ? b : 0);
        }

        /// <summary>
        /// Fill rate of sand prints (refill units per second): <c>sandTrailRefill</c> s in calm air, faster with the wind
        /// (G3Wind strength above a light breeze: up to 2.5x), <c>sandTrailRefillStorm</c> s in a full sandstorm. Sand fills in
        /// far faster than snow (dry grains creep and saltate; snow needs snowfall).
        /// </summary>
        public static float FillRate(float storm, float wind)
        {
            float calm = 1f / S.sandTrailRefill, full = 1f / S.sandTrailRefillStorm;
            float windK = 1f + 1.5f * Clamp01((wind - 0.2f) / 0.8f);
            return Math.Max(calm * windK, calm + Clamp01(storm) * (full - calm));
        }

        /// <summary>The sand print clock after dt (one unit = one full fill-in, as the snow clock; <see cref="SnowV2.Fade"/>).</summary>
        public static double ClockStep(double clock, float storm, float wind, float dt) => clock + Math.Max(dt, 0) * FillRate(storm, wind);

        /// <summary>Seconds until a fresh sand print has filled in completely at this weather (age 1).</summary>
        public static float FillSeconds(float storm, float wind) => 1f / FillRate(storm, wind);

        /// <summary>Print depth relative to the snow print depth (the shared grid stores depth 0..1 of <c>snowTrailDepth</c>).</summary>
        public static float PrintDepthRel(bool boot) =>
            Clamp01(S.sandTrailDepth / Math.Max(S.snowTrailDepth, 1e-3f)) * (boot ? 1f : 0.8f);

        /// <summary>Kind offset of a sand print in the shared print grid (kind 4..6 = sand boot / paw / oval; shader: kind &gt; 3.5).</summary>
        public const int SandKind = 4;

        /// <summary>The print shape kind of a stored kind (sand prints: minus <see cref="SandKind"/>).</summary>
        public static int BaseKind(int kind) => kind >= SandKind ? kind - SandKind : kind;

        /// <summary>A stored kind is a sand print (its clock is the sand clock).</summary>
        public static bool IsSandPrint(float kind) => kind > SandKind - 0.5f;

        // ------------------------------------------------------------------ wind ripples (mirrors G3SandRipple*, sand-2)

        /// <summary>Ripple tile resolution (px per side; <see cref="SandRipples"/>, generated on a worker at start).</summary>
        public const int RippleSize = 1024;

        /// <summary>Metres covered by one ripple tile (field A) at wavelength <paramref name="wave"/> (m): 128 crests.</summary>
        public static float RippleTile(float wave) => wave * SandRipples.CrestsPerTile(RippleSize);

        /// <summary>Peak slope of ripples of half-height <paramref name="height"/> and wavelength <paramref name="wave"/> (m): 2 pi h / wave.</summary>
        public static float PeakSlope(float height, float wave) => (float)(2 * Math.PI) * height / Math.Max(wave, 1e-4f);

        /// <summary>pcg3d (Jarzynski and Olano 2020) as <see cref="SnowV2.GrainHash"/>: one number in [0, 1) of an integer cell.</summary>
        public static float Hash(int x, int y, uint salt) => SnowV2.GrainHash(x, y, salt).r0;

        /// <summary>
        /// Value noise in [0, 1) with its gradient (mirrors G3SandNoiseD): integer-hashed lattice (no sin-hash precision loss
        /// far from the origin, fix-194), smoothstep interpolation.
        /// </summary>
        public static (float v, float dx, float dy) NoiseD(float x, float y, uint salt)
        {
            float fx0 = (float)Math.Floor(x), fy0 = (float)Math.Floor(y);
            int ix = (int)fx0, iy = (int)fy0;
            float fx = x - fx0, fy = y - fy0;
            float ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
            float dux = 6 * fx * (1 - fx), duy = 6 * fy * (1 - fy);
            float a = Hash(ix, iy, salt), b = Hash(ix + 1, iy, salt), c = Hash(ix, iy + 1, salt), d = Hash(ix + 1, iy + 1, salt);
            float v = a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
            return (v, ((b - a) + (a - b - c + d) * uy) * dux, ((c - a) + (a - b - c + d) * ux) * duy);
        }

        /// <summary>Field B: the tile 1.37x larger (an irrational-looking ratio: the two never repeat together) turned 9 degrees.</summary>
        public const float FieldBScale = 1.37f, FieldBAngleDeg = 9f;

        /// <summary>sand-3 noise tile: cells per side (period) and texels per cell (G3SandNoiseD's texture path).</summary>
        public const int NoiseTileCells = 128, NoiseTileRes = 4;

        /// <summary>
        /// sand-3: <see cref="NoiseD"/> as a periodic tile for the GPU (G3SandNoiseD): texel (x, y) holds the noise at cell
        /// coordinates (x, y) / <see cref="NoiseTileRes"/> with the lattice wrapped every <see cref="NoiseTileCells"/> cells,
        /// RGB = value, d/dx, d/dy (per cell); 4 floats per texel, row-major from y = 0. Salt 0 (the shader offsets per salt).
        /// </summary>
        public static float[] NoiseTile()
        {
            int c = NoiseTileCells, r = NoiseTileRes, n = c * r;
            var d = new float[n * n * 4];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float px = (float)x / r, py = (float)y / r;
                    int ix = x / r, iy = y / r;
                    float fx = px - ix, fy = py - iy;
                    float ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
                    float dux = 6 * fx * (1 - fx), duy = 6 * fy * (1 - fy);
                    int x1 = (ix + 1) % c, y1 = (iy + 1) % c;
                    float a = Hash(ix, iy, 0), b = Hash(x1, iy, 0), cc = Hash(ix, y1, 0), dd = Hash(x1, y1, 0);
                    float k = a - b - cc + dd;
                    int o = (y * n + x) * 4;
                    d[o] = a + (b - a) * ux + (cc - a) * uy + k * ux * uy;
                    d[o + 1] = ((b - a) + k * uy) * dux;
                    d[o + 2] = ((cc - a) + k * ux) * duy;
                    d[o + 3] = 1;
                }
            return d;
        }

        /// <summary>
        /// Low-frequency domain warp of a ripple field at (ra, rc) m in the field's wind frame (wavelength <paramref name="wave"/>):
        /// the along-wind displacement wA bends the crests (it varies across the wind: +-15 degrees) and stretches / compresses
        /// the wavelength (it varies along: +-15 %); wC shifts across. Returns the displacement and its exact derivatives (the
        /// shader turns the tile's slopes back to the world with this Jacobian). Mirrors G3SandRippleWarp.
        /// </summary>
        public static (float wA, float wC, float dAa, float dAc, float dCa, float dCc) RippleWarp(float ra, float rc, float wave, uint salt)
        {
            float l1 = wave * 36f, a1 = wave * 8.2f, l2 = wave * 15f, a2 = wave * 1.9f, l3 = wave * 50f, a3 = wave * 7f;
            var n1 = NoiseD(ra / l1, rc / l1, salt);
            var n2 = NoiseD(ra / l2 + 7.7f, rc / l2, salt + 5u);
            var n3 = NoiseD(ra / l3 + 3.3f, rc / l3, salt + 9u);
            return ((n1.v - 0.5f) * a1 + (n2.v - 0.5f) * a2, (n3.v - 0.5f) * a3,
                    n1.dx * a1 / l1 + n2.dx * a2 / l2, n1.dy * a1 / l1 + n2.dy * a2 / l2,
                    n3.dx * a3 / l3, n3.dy * a3 / l3);
        }

        /// <summary>Weight (0 field A .. 1 field B) of the two warped ripple fields at (along, across) m: ~6 m patches. Mirrors the shader.</summary>
        public static float RippleBlend(float along, float across) =>
            SnowV2.Smooth(0.3f, 0.7f, NoiseD(along / 6.3f + 31f, across / 6.3f, 21u).v);

        /// <summary>
        /// Ripple amplitude patches (0.25..1) at (along, across) m: ripples cover about <paramref name="coverage"/> of open
        /// sand at full height, the rest is nearly smooth (two noise octaves, 9 m and 23 m). Mirrors G3SandRipplePatch.
        /// </summary>
        public static float RipplePatch(float along, float across, float coverage)
        {
            float m = NoiseD(along / 9f, across / 9f, 41u).v * 0.65f + NoiseD(along / 23f, across / 23f, 43u).v * 0.35f;
            float t = 1 - Clamp01(coverage);
            return 0.25f + 0.75f * SnowV2.Smooth(t - 0.12f, t + 0.12f, m);
        }

        /// <summary>
        /// Ripple fade by the loose-sand share of a pixel's layer blend: only the core of a loose-sand layer ripples, never
        /// its blend band (where G3 paints grass, gravel, rock or path round the sand). Mirrors the shader.
        /// </summary>
        public static float CoreFade(float looseShare) => SnowV2.Smooth(0.7f, 0.98f, looseShare);

        /// <summary>
        /// Ripple strength (0..1) on a surface of geometric normal (nx, ny, nz) for wind (wx, wz): flat and windward ground
        /// keeps them; slopes steeper than ~18 deg lose them (none beyond the angle of repose, ~34 deg: avalanche faces), and
        /// lee faces (downhill along the wind) are smoothed out. Mirrors G3SandRippleSlope.
        /// </summary>
        public static float SlopeFade(float nx, float ny, float nz, float wx, float wz)
        {
            float flat = SnowV2.Smooth(0.83f, 0.95f, ny);
            float lee = nx * wx + nz * wz;   // the normal leans downhill: > 0 = the slope falls away downwind
            return flat * (1 - 0.8f * SnowV2.Smooth(0.05f, 0.25f, lee));
        }

        /// <summary>
        /// Ripple fade by the pixel footprint (m): an octave of wavelength <paramref name="wave"/> is drawn while a pixel is
        /// under ~a quarter of it and gone at ~half (no shimmer, no moire). Mirrors the shader.
        /// </summary>
        public static float FootprintFade(float footprint, float wave) => 1 - SnowV2.Smooth(0.15f, 0.35f, footprint / Math.Max(wave, 1e-4f));

        /// <summary>Ripple fade by camera distance (m): full to 10 m, gone by 28 m (the dunes' shapes carry the distance). Mirrors the shader.</summary>
        public static float DistanceFade(float dist) => 1 - SnowV2.Smooth(10f, 28f, dist);

        /// <summary>World wind direction (unit x, z) of an azimuth in degrees (G3Wind: 0 = towards +Z, 90 = towards +X).</summary>
        public static (float x, float z) WindDir(float azimuthDeg)
        {
            double a = azimuthDeg * Math.PI / 180;
            return ((float)Math.Sin(a), (float)Math.Cos(a));
        }

        /// <summary>World (x, z) of a point → (along the wind, across it).</summary>
        public static (float along, float across) ToWind(float x, float z, float wx, float wz) => (x * wx + z * wz, -x * wz + z * wx);

        // ------------------------------------------------------------------ wisps

        /// <summary>fx-1: hero walk / sprint speeds (G3Player) between which the footstep dust grows.</summary>
        public const float DustWalkSpeed = 1.6f, DustSprintSpeed = 6f;

        /// <summary>fx-1: below this body speed (m/s) a step kicks no dust (dancing, idling, turning on the spot).</summary>
        public const float DustMinSpeed = 0.8f;

        /// <summary>
        /// fx-1: footstep dust amount by the body speed (m/s): <paramref name="walk"/> (json sandDustWalk) at a walk and below,
        /// 1 at a sprint, eased between (a run ~0.6).
        /// </summary>
        public static float DustBySpeed(float speed, float walk)
        {
            if (speed < DustMinSpeed) return 0;
            float k = (speed - DustWalkSpeed) / (DustSprintSpeed - DustWalkSpeed);
            k = k < 0 ? 0 : k > 1 ? 1 : k;
            return walk + (1 - walk) * (float)Math.Pow(k, 1.3);
        }

        /// <summary>
        /// Amount (0..1) of wind-blown sand wisps near the ground: none in a light breeze (G3Wind strength ~0.1-0.4 with
        /// gusts in Sunny(Varant)), some in strong gusts, full in a sandstorm. Mirrors nothing (CPU only).
        /// </summary>
        public static float WispAmount(float wind, float storm) =>
            Clamp01(Math.Max(Math.Max(S.sandWispCalm, S.sandWispCalm + Clamp01((wind - 0.45f) / 0.6f) * (0.75f - S.sandWispCalm)), Clamp01(storm) * 1.0f)) * S.sandWisps;

        /// <summary>
        /// Heat haze strength (0..2): json <c>sandHeatHaze</c> x the desert at the camera x the sun's height (none below 8 degrees -
        /// night, dawn, dusk -, full from 35 degrees: the ground is hot) x clear air (a sandstorm hides it).
        /// </summary>
        public static float HeatHaze(float sunElevationDeg, float desert, float storm) =>
            S.sandHeatHaze * Clamp01(desert) * SnowV2.Smooth(8f, 35f, sunElevationDeg) * (1 - Clamp01(storm));

        // ------------------------------------------------------------------ globals

        /// <summary>_G3Sand: x on (1), y ripple normal strength, z grain strength, w glint strength.</summary>
        public static float[] SandGlobal(bool on) => on ? new[] { 1f, S.sandRipples, S.sandGrain, S.sandGlint } : new float[4];

        /// <summary>_G3Sand2: xy prevailing wind (unit world x z), z ripple wavelength (m), w ripple half-height (m).</summary>
        public static float[] Sand2Global(float wx, float wz) => new[] { wx, wz, S.sandRippleWave, S.sandRippleHeight };

        /// <summary>_G3Sand5: x ripple tile (m), y peak slope, z patch coverage, w self-shadow strength (0 = none).</summary>
        public static float[] Sand5Global => new[] { RippleTile(S.sandRippleWave), PeakSlope(S.sandRippleHeight, S.sandRippleWave), S.sandRippleCoverage, S.sandRippleShadow };

        /// <summary>
        /// _G3Sand7 (CC0 sand micro tile, section 22.8): x 1 / tile size (1/m; 0 = no tile), y normal strength, z albedo factor
        /// strength, w parallax height (m). Off (all 0) without a tile or with json sandMicro 0.
        /// </summary>
        public static float[] Sand7Global(bool hasTile, float sizeM, float heightM) =>
            hasTile && S.sandMicro > 0 && sizeM > 0.05f
                ? new[] { 1f / sizeM, 0.8f * S.sandMicro, 0.5f * S.sandMicro, heightM * S.sandMicro }
                : new float[4];

        /// <summary>_G3Sand6: x broad soft sheen, y backlit rim sheen, z macro tint variation, w ripple parallax (0..1).</summary>
        /// <summary>sand-3 _G3Sand8: x 1 / fine size, y 1 / (mid size x sandScanMidScale), z 1 / coarse size (1/m), w scan share
        /// (0 without the set or with json sandScan 0).</summary>
        public static float[] Sand8Global(bool hasSet, float fineM, float midM, float coarseM) =>
            hasSet && S.sandScan > 0 && fineM > 0.05f && midM > 0.05f && coarseM > 0.05f
                ? new[] { 1f / fineM, 1f / (midM * S.sandScanMidScale), 1f / coarseM, S.sandScan }
                : new float[4];

        /// <summary>sand-3 _G3Sand9: x coarse amount, y grade, z brightness, w windward lightening.</summary>
        public static float[] Sand9Global => new[] { S.sandCoarse, S.sandGrade, S.sandBright, S.sandWindward };

        /// <summary>sand-3 _G3Sand10: x diffuse contrast, y backscatter, z scan normal strength, w scan parallax height (m).</summary>
        public static float[] Sand10Global(float fineHeightM) => new[] { S.sandDiffuseContrast, S.sandBackscatter, S.sandScanNormal, fineHeightM * S.sandScanParallax };

        /// <summary>sand-3 _G3Sand11: x mega ripples, y their scale, z ripple far fade end (m), w sand on meshes.</summary>
        public static float[] Sand11Global => new[] { S.sandMegaRipples, S.sandMegaScale, S.sandRippleFar, S.sandEdge };

        /// <summary>sand-3 _G3Sand12: x macro value, y glint range (m), z POM on (1), w scan colour contrast.</summary>
        public static float[] Sand12Global => new[] { S.sandMacroValue, S.sandGlintRange, S.sandPom ? 1f : 0f, S.sandScanContrast };

        /// <summary>fx-1 _G3Sand13: x mega-ripple strength kept at the camera, y extra cut there at low sun, z mega warp scale, w crest breaks.</summary>
        public static float[] Sand13Global => new[] { S.sandMegaNear, S.sandMegaLowSun, S.sandRippleWarp, S.sandRippleBreak };

        /// <summary>
        /// fx-1: mega-ripple strength factor by camera distance (m) and sun elevation (sin): <c>sandMegaNear</c> at the camera,
        /// full from 16 m; at low sun (below ~25 deg, grazing light turned the near field into a grating) cut further by
        /// <c>sandMegaLowSun</c> within 12 m. Mirrors G3SandMegaNear.
        /// </summary>
        public static float MegaNear(float dist, float sunUp, float near, float lowSun)
        {
            float k = near + (1 - near) * SnowV2.Smooth(3f, 16f, dist);
            float low = 1 - SnowV2.Smooth(0.18f, 0.45f, sunUp);
            return k * (1 - lowSun * low * (1 - SnowV2.Smooth(4f, 12f, dist)));
        }

        /// <summary>sand-3: the 10 cm ripples' distance fade (from 0.36 x sandRippleFar to sandRippleFar; G3SandDistFade).</summary>
        public static float FarFade(float dist) => 1 - SnowV2.Smooth(S.sandRippleFar * 0.36f, S.sandRippleFar, dist);

        /// <summary>sand-3 G3SandGrade: G3's colour towards golden sand at the same luminance (linear rgb), x brightness.</summary>
        public static (float r, float g, float b) Grade(float r, float g, float b)
        {
            float l = 0.2126f * r + 0.7152f * g + 0.0722f * b, k = Clamp01(S.sandGrade), m = S.sandBright;
            return ((r + (l * 1.60f - r) * k) * m, (g + (l * 0.89f - g) * k) * m, (b + (l * 0.38f - b) * k) * m);
        }

        public static float[] Sand6Global => new[] { S.sandSheen, S.sandRim, S.sandMacroTint, S.sandRippleParallax };

        /// <summary>_G3Sand3: x grain tile (m), y grain normal strength, z glint density, w CC0 coast-sand share kept in the desert.</summary>
        public static float[] Sand3Global => new[] { S.sandGrainTile, S.sandGrainNormal, S.sandGlintDensity, S.sandCc0 };

        /// <summary>_G3Sand4: smoothness of x loose (dry, matte), y wet shore sand, z packed path sand; w unused.</summary>
        public static float[] Sand4Global => new[] { S.sandSmoothness, 0.3f, Math.Max(S.sandSmoothness, 0.1f), 0f };

        /// <summary>_G3SandTrail: x prints on, y sand clock now (wrapped), z depth relative to the snow print depth, w print darkening.</summary>
        public static float[] TrailGlobal(bool on, float clockWrapped) => on ? new[] { 1f, clockWrapped, PrintDepthRel(true), S.sandPrintShade } : new float[4];
    }
}
