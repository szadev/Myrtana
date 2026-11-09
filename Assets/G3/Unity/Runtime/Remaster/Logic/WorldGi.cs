using System;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free maths of the remaster GI (Docs/remaster-world.md section 16, agent remaster-gi): a camera-centred
    /// radiance volume filled from the screen (one thread per voxel projects its centre into the frame: a surface there
    /// stores the lit colour, a deeper surface marks the voxel empty, a closer one leaves it as it was; HSGI, GDC 2023),
    /// a coarser grid of irradiance probes (ambient cube, 6 directions) that trace rays through that volume (hits = the
    /// radiance the surfaces really reflect, misses = G3's sky ambient, unseen space under the occluder map = solid), and
    /// the light-ratio pass that replaces G3's flat ambient by the probes' irradiance. Both grids are toroidal (a voxel at
    /// world index w lives at storage index w mod dims), so a camera move clears only the slabs that entered.
    /// Mirrored by G3RemasterGi.compute and Hidden/G3/Remaster/LightRatio (tested here).
    /// </summary>
    public static class WorldGi
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        /// <summary>Voxel grid size (x, y, z); probes are one per 2×2×2 voxels.</summary>
        public const int VoxX = 128, VoxY = 64, VoxZ = 128;
        public const int ProbeX = VoxX / 2, ProbeY = VoxY / 2, ProbeZ = VoxZ / 2;

        /// <summary>
        /// remaster-gi2 (section 17): the "Modern lighting" row (Options -> Remaster, under Lighting; default off), set by
        /// <c>G3WorldGi.SetModern</c> from the stored choice. Engine-free copy of the switch for the maths below.
        /// </summary>
        public static bool ModernOption
        {
            get => ModernMode != 0;
            set => ModernMode = value ? (ModernMode == 0 ? 1 : ModernMode) : 0;
        }

        /// <summary>
        /// lighting-new (section 21): the row's three values, 0 = off, 1 = "Мягкое" / Soft (section 17 + 19, the
        /// matched exposure), 2 = "Новое" / New (G3's flat fill replaced by sky light x visibility + bounce, auto-exposure,
        /// filmic tonemapper; <see cref="WorldLightNew"/>).
        /// </summary>
        public static int ModernMode;

        /// <summary>True when Modern lighting is applied (the row on, JSON v2, GI strength above 0; the Lighting part is checked by the caller).</summary>
        public static bool Modern => ModernOption && S.v2 && S.v2GiStrength > 0;

        /// <summary>lighting-new: the "New" value of the row is applied (Modern is true as well: GI + SSGI run).</summary>
        public static bool New => Modern && ModernMode == 2;

        /// <summary>lighting-new: Soft Modern lighting (section 17's matched exposure; false under New).</summary>
        public static bool Soft => Modern && ModernMode == 1;

        /// <summary>True when the GI part of the light pass runs (Lighting part on, JSON v2 + v2Gi, or Modern lighting).</summary>
        public static bool Wanted => S.v2 && (S.v2Gi || ModernOption) && S.v2GiStrength > 0;

        /// <summary>Bounce gain of the probe trace (Modern lighting has its own).</summary>
        public static float Gain => New ? S.v2NewGain : Modern ? S.v2ModernGain : S.v2GiGain;

        /// <summary>Bounce hue strength on the fill (Modern lighting has its own).</summary>
        public static float Tint => Modern ? S.v2ModernTint : S.v2GiTint;

        /// <summary>
        /// Cosine-weighted share of a face's hemisphere above a flat open horizon for normal elevation <paramref name="ny"/>
        /// (−1..1): (1 + ny) / 2, at least 0.25. The probes' traced visibility is divided by it, so an open meadow reads
        /// 1 on every face (G3's ambient cube already holds the horizon) and only real occluders take the fill away.
        /// </summary>
        public static float OpenVisibility(float ny) => Math.Max(0.25f, 0.5f * (1 + Clamp(ny, -1, 1)));

        /// <summary>The traced visibility relative to an open field (0..1).</summary>
        public static float RelativeVisibility(float vis, float ny) => Clamp01(Clamp01(vis) / OpenVisibility(ny));

        /// <summary>
        /// Modern lighting's ambient (per channel; <paramref name="a"/> = G3's sky-facing fill, <paramref name="ground"/> its
        /// ground lobe): fill x <c>v2ModernAmbient</c>, sky-facing part occluded by the visibility blended from the map
        /// estimate (outside the probe window, weight <paramref name="w"/> 0) to the traced one, + the traced bounce (gain
        /// applied by the trace) where the probes are valid, + the screen bounce (scaled like the fill) where they are not.
        /// </summary>
        public static float ModernAmbient(float a, float ground, float mapOcclusion, float giVis, float ny, float giHit, float screenBounce, float w)
        {
            float occ = Clamp01(S.v2ModernOcclusion);
            float vMap = 1 - occ * Clamp01(mapOcclusion);
            float vGi = 1 - occ * (1 - RelativeVisibility(giVis, ny));
            float v = vMap + (vGi - vMap) * Clamp01(w);
            float k = S.v2ModernAmbient;
            return k * SkyDirection(ny) * (Math.Max(a, 0) * v + Math.Max(ground, 0)) + Clamp01(w) * Math.Max(giHit, 0) + (1 - Clamp01(w)) * k * Math.Max(screenBounce, 0);
        }

        /// <summary>Directional sky of Modern lighting: the fill x (1 + v2ModernSky x ny), at least 0.15 (light from above).</summary>
        public static float SkyDirection(float ny) => Math.Max(0.15f, 1 + Clamp01(S.v2ModernSky) * Clamp(ny, -1, 1));

        /// <summary>
        /// The exposure that keeps the frame's mean: <paramref name="meanOld"/> = mean luminance of the opaque frame as the
        /// remaster draws it without Modern lighting, <paramref name="meanNew"/> with it (both measured on the same frame, so
        /// no feedback; means of the displayed value: sRGB encoding minus the grade's 0.01 black crush, <see cref="DisplayLum"/>);
        /// the shown mean responds to an exposure e about as e^0.7 in G3's dark range, so (old / new)^(1.43 x
        /// <c>v2ModernExposure</c>), clamped to [min, max]; 1 for an empty frame. The shader then eases it over ~0.4 s.
        /// </summary>
        public static float Exposure(float meanOld, float meanNew)
        {
            if (!(meanOld > 1e-4f) || !(meanNew > 1e-4f)) return 1;
            float e = (float)Math.Pow(meanOld / meanNew, 1.43f * Clamp01(S.v2ModernExposure));
            return Clamp(e, S.v2ModernMinExposure, Math.Max(S.v2ModernMinExposure, S.v2ModernMaxExposure));
        }

        /// <summary>The displayed value of a linear luminance (sRGB encoding, minus 0.01 black crush), as the exposure sums.</summary>
        public static float DisplayLum(float l)
        {
            l = Clamp(l, 0, 64);
            float s = l < 0.0031308f ? 12.92f * l : 1.055f * (float)Math.Pow(l, 1 / 2.4) - 0.055f;
            return Math.Max(s - 0.01f, 0);
        }

        /// <summary>_G3GiModern: x fill scale (0 = Modern lighting off), y occlusion strength, z min exposure, w max exposure; _G3GiModern2.x exposure match.</summary>
        public static float[] ModernGlobal() => Soft
            ? new[] { S.v2ModernAmbient, S.v2ModernOcclusion, S.v2ModernMinExposure, Math.Max(S.v2ModernMinExposure, S.v2ModernMaxExposure), S.v2ModernExposure }
            : new float[5];

        /// <summary>Euclidean modulo (always 0..m-1).</summary>
        public static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

        /// <summary>
        /// Voxel-grid origin (world voxel index of the window's min corner) for a camera position: the camera in the middle,
        /// snapped to whole probes (2 voxels) and to <paramref name="step"/> probes, so the window moves in steps and a
        /// move clears whole slabs. y keeps the camera at 40 % of the height (more room above for roofs and crowns).
        /// </summary>
        public static (int x, int y, int z) Origin(float camX, float camY, float camZ, float voxel, int step = 2)
        {
            int q = 2 * Math.Max(1, step);
            int Snap(float c, int dim, float frac) => (int)Math.Floor((Math.Floor(c / voxel) - dim * frac) / q) * q;
            return (Snap(camX, VoxX, 0.5f), Snap(camY, VoxY, 0.4f), Snap(camZ, VoxZ, 0.5f));
        }

        /// <summary>The world index stored at storage index <paramref name="s"/> for a window starting at <paramref name="origin"/>.</summary>
        public static int WorldIndex(int s, int origin, int dim) => origin + Mod(s - origin, dim);

        /// <summary>
        /// True when storage cell <paramref name="s"/> (one axis) holds a world index that was not inside the previous window
        /// (it entered with the scroll and must be cleared). Per axis; a cell is new when any axis is new.
        /// </summary>
        public static bool Entered(int s, int oldOrigin, int newOrigin, int dim)
        {
            int w = WorldIndex(s, newOrigin, dim);
            return w < oldOrigin || w >= oldOrigin + dim;
        }

        /// <summary>What the frame says about a voxel (pass 1, injection).</summary>
        public enum Seen { Hidden = 0, Surface = 1, Empty = 2, Outside = 3 }

        /// <summary>
        /// Injection test of a voxel whose centre lies at view depth <paramref name="voxelDepth"/> and projects onto a pixel
        /// with scene depth <paramref name="sceneDepth"/> (both linear, m; sky = +inf): the surface lies inside the voxel
        /// (|Δ| ≤ ½ voxel × 1.2), the surface is behind it (the ray passed through: empty) or in front (hidden, keep).
        /// </summary>
        public static Seen Classify(float sceneDepth, float voxelDepth, float voxel, bool onScreen)
        {
            if (!onScreen || voxelDepth <= 0.3f) return Seen.Outside;
            float h = voxel * 0.6f;
            float d = sceneDepth - voxelDepth;
            if (Math.Abs(d) <= h) return Seen.Surface;
            return d > h ? Seen.Empty : Seen.Hidden;
        }

        /// <summary>
        /// The voxel after one observation: radiance (r, g, b) and occupancy a (−1 = never seen, 0 empty .. 1 solid),
        /// blended at <paramref name="rate"/>; the first observation is taken as it is.
        /// </summary>
        public static (float r, float g, float b, float a) Inject((float r, float g, float b, float a) old, Seen seen, float r, float g, float b, float rate)
        {
            if (seen == Seen.Hidden || seen == Seen.Outside) return old;
            bool first = old.a < 0;
            if (seen == Seen.Empty) return first ? (0, 0, 0, 0) : (old.r, old.g, old.b, old.a * (1 - rate));
            if (first) return (r, g, b, 1);
            return (old.r + (r - old.r) * rate, old.g + (g - old.g) * rate, old.b + (b - old.b) * rate, old.a + (1 - old.a) * rate);
        }

        /// <summary>The 6 ambient-cube directions (+x −x +y −y +z −z).</summary>
        public static readonly float[][] Faces =
        {
            new[] { 1f, 0, 0 }, new[] { -1f, 0, 0 }, new[] { 0, 1f, 0 }, new[] { 0, -1f, 0 }, new[] { 0, 0, 1f }, new[] { 0, 0, -1f },
        };

        /// <summary>Ray <paramref name="i"/> of <paramref name="n"/> on a Fibonacci sphere (unit vector, even coverage).</summary>
        public static (float x, float y, float z) Fibonacci(int i, int n)
        {
            double golden = Math.PI * (3 - Math.Sqrt(5));
            double y = 1 - (i + 0.5) * 2.0 / n;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            double a = golden * i;
            return ((float)(Math.Cos(a) * r), (float)y, (float)(Math.Sin(a) * r));
        }

        /// <summary>
        /// Irradiance of one ambient-cube face from n rays spread evenly over the sphere (Unity's convention, without 1/π:
        /// a constant radiance L gives L): 4 × mean(L × max(0, cos)).
        /// </summary>
        public static float FaceIrradiance(float[] face, (float x, float y, float z)[] dirs, float[] radiance)
        {
            double sum = 0;
            for (int i = 0; i < dirs.Length; i++)
            {
                float c = face[0] * dirs[i].x + face[1] * dirs[i].y + face[2] * dirs[i].z;
                if (c > 0) sum += radiance[i] * c;
            }
            return dirs.Length == 0 ? 0 : (float)(4 * sum / dirs.Length);
        }

        /// <summary>Evaluates an ambient cube for normal n (squared-normal weights, as URP's / Valve's ambient cube).</summary>
        public static float EvalCube(float[] cube, float nx, float ny, float nz)
        {
            float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-6f) return 0;
            nx /= l; ny /= l; nz /= l;
            return nx * nx * (nx >= 0 ? cube[0] : cube[1]) + ny * ny * (ny >= 0 ? cube[2] : cube[3]) + nz * nz * (nz >= 0 ? cube[4] : cube[5]);
        }

        /// <summary>
        /// A probe whose rays mostly hit a surface right away sits inside a wall, rock or the terrain: its irradiance is not
        /// used (the neighbours' fill it in). <paramref name="nearHits"/> of <paramref name="rays"/> hit within one voxel.
        /// </summary>
        public static bool Inside(int nearHits, int rays) => rays > 0 && nearHits > rays * 0.4f;

        /// <summary>Temporal blend of a probe face: the first update is taken as it is, later ones at 1 − hysteresis.</summary>
        public static float Hysteresis(float old, float now, float hysteresis, bool first) => first ? now : old + (now - old) * (1 - hysteresis);

        /// <summary>
        /// Weight of the probes at a point (0..1): fades out over the outer 3 probes of the window (any axis), times the
        /// probe confidence (how settled it is).
        /// </summary>
        public static float Weight(float px, float py, float pz, (int x, int y, int z) originVox, float voxel, float confidence)
        {
            float sp = voxel * 2;
            float ox = originVox.x * voxel, oy = originVox.y * voxel, oz = originVox.z * voxel;
            float Edge(float p, float o, int n) { float u = (p - o) / sp; return Clamp01(Math.Min(u - 0.5f, n - 0.5f - u) / 3f); }
            return Edge(px, ox, ProbeX) * Edge(py, oy, ProbeY) * Edge(pz, oz, ProbeZ) * Clamp01(confidence);
        }

        /// <summary>
        /// The ratio with GI: the ambient part of the numerator becomes a blend (weight w × strength) of the previous
        /// estimate (G3's ambient × map visibility + screen bounce) and the probes' light = G3's ambient occluded by the
        /// traced sky visibility <paramref name="giVis"/> (at the sky-occlusion <paramref name="occStrength"/>: G3's fill is
        /// authored, a cave keeps 1 − strength of it) + the traced bounce <paramref name="giHit"/> (coloured, torches
        /// included); clamped to [floor, max] (max a little above 1: a sunlit valley may bounce more than G3's flat fill).
        /// <paramref name="a"/> stands for G3's sky-facing lobes here; the shader keeps G3's ground lobe (its authored
        /// bounce from below) unoccluded in both estimates, as the map estimate always did.
        /// </summary>
        public static float Ratio(float a, float d, float l, float v, float c, float bounce, float giVis, float giHit, float w, float occStrength, float floor, float max, float keep = 0)
        {
            a = Math.Max(a, 0); d = Math.Max(d, 0); l = Math.Max(l, 0);
            float sum = a + d + l;
            if (sum < 1e-6f) return 1;
            float old = a * Clamp01(v) + Math.Max(bounce, 0);
            float gi = a * (1 - Clamp01(occStrength) * (1 - Clamp01(giVis))) + Math.Max(giHit, 0);
            float amb = Math.Max(old + (gi - old) * Clamp01(w), old * Clamp01(keep));   // never below keep x the old estimate
            float r = (amb + d * Clamp01(c) + l) / sum;
            return Math.Max(Math.Min(r, max), floor);
        }

        /// <summary>
        /// Per probe face from its rays (unit directions spread over the sphere): the bounce irradiance (4 × mean of hit
        /// radiance × cos, Unity's convention) and the sky visibility (cos-weighted share of the escape transmittance).
        /// </summary>
        public static (float hit, float vis) Face(float[] face, (float x, float y, float z)[] dirs, float[] hitRadiance, float[] escape)
        {
            double h = 0, v = 0, cw = 0;
            for (int i = 0; i < dirs.Length; i++)
            {
                float c = face[0] * dirs[i].x + face[1] * dirs[i].y + face[2] * dirs[i].z;
                if (c <= 0) continue;
                h += hitRadiance[i] * c; v += escape[i] * c; cw += c;
            }
            return (dirs.Length == 0 ? 0 : (float)(4 * h / dirs.Length), cw > 0 ? (float)(v / cw) : 1);
        }

        /// <summary>Probe updates per frame: every probe is traced once per <c>v2GiCadence</c> frames.</summary>
        public static int ProbesPerFrame => ProbeX * ProbeY * ProbeZ / Math.Max(1, S.v2GiCadence);

        /// <summary>Estimated GPU cost (ms, RTX 3080 class): injection (half the voxels per frame), probe tracing, dilate.</summary>
        public static float CostMs()
        {
            if (!Wanted) return 0;
            float inject = VoxX * VoxY * VoxZ * 0.5f * 1.0e-7f;                       // ~0.05 ms
            float trace = ProbesPerFrame * S.v2GiRays * (S.v2GiRayLength / S.v2GiVoxel) * 4e-9f;   // ~0.3 ms
            float dilate = ProbeX * ProbeY * ProbeZ * 2e-7f;                          // ~0.03 ms
            return inject + trace + dilate + 0.05f;
        }

        // ------------------------------------------------------------------ cloud shadows (section 16.4)

        /// <summary>
        /// Where the sun ray from a ground point crosses height <paramref name="heightM"/> above it: the horizontal offset
        /// (m) along the sun direction (sx, sy, sz unit, towards the sun). Low sun is capped at 8 km.
        /// </summary>
        public static (float dx, float dz) SunOffset(float sx, float sy, float sz, float heightM)
        {
            float t = heightM / Math.Max(sy, 0.12f);
            t = Math.Min(t, 8000f);
            return (sx * t, sz * t);
        }

        /// <summary>Sun transmittance through the cloud slab's optical depth, softened by strength (0 = no cloud shadows).</summary>
        public static float CloudShadow(float opticalDepth, float strength) => 1 - Clamp01(strength) * (1 - (float)Math.Exp(-Math.Max(opticalDepth, 0)));

        /// <summary>_G3GiParams: x strength (0 = off), y voxel size (m), z max ratio, w keep (min part of the old ambient estimate).</summary>
        public static float[] GiGlobal(int frame) => Wanted ? new[] { S.v2GiStrength, S.v2GiVoxel, S.v2GiMaxRatio, Modern ? 0 : S.v2GiKeep } : new float[4];

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
