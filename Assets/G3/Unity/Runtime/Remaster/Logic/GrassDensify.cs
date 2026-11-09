using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// v2 grass density (Docs/remaster-world.md sections 10.3 and 10.16): extra plants seeded from G3's own vegetation
    /// placement, as a filler matched to the local G3 mix, never a uniform carpet. G3's plants all stay; the copies
    /// <list type="bullet">
    /// <item>come only from plants (grass, herbs/flowers, ferns, underbrush, crops): stones, branches, blood and roots
    ///   are never copied (<see cref="KindOf"/>);</item>
    /// <item>take their species from the G3 plants around the anchor (within <see cref="Options.MixRadius"/>), weighted
    ///   by sqrt(count) (rare local species gain a little against the dominant tuft) times a per-species low-frequency
    ///   patch field: species form colonies, nothing appears that G3 did not put there;</item>
    /// <item>copy that neighbour's colour, size and tilt (G3's own per-plant colour = the local palette, dry/green as G3
    ///   placed them), plus a brightness jitter and a patch-correlated height field (tall and short stands);</item>
    /// <item>come in clumps: the number of copies per G3 plant follows a patch field (some ground keeps G3's own
    ///   density, other patches get thicker); the mean stays about (density - 1) copies per copyable plant.</item>
    /// </list>
    /// The height of a copy comes from a least-squares plane through the neighbouring G3 plants (they sit on the
    /// terrain); a copy is skipped where that plane does not fit (rough or steep ground). Deterministic, engine-free, tested.
    /// </summary>
    public static class GrassDensify
    {
        /// <summary>What a vegetation mesh is, for the filler.</summary>
        public enum Kind : byte
        {
            /// <summary>Not copied: stones, pebbles, branches, blood, roots, unknown.</summary>
            None,
            Grass,
            /// <summary>Herbs and flowers (G3's flower cards).</summary>
            Herb,
            /// <summary>Ferns, underbrush, small bushes.</summary>
            Low,
            /// <summary>Dry grass, crops (tall cereal-like grass).</summary>
            Dry,
        }

        /// <summary>Kind of a G3 vegetation mesh from its name (G3_Brush_Plant_Grass_05, G3_Brush_Object_Stone_Small_01, ...).</summary>
        public static Kind KindOf(string meshName)
        {
            if (string.IsNullOrEmpty(meshName)) return Kind.None;
            string n = meshName;
            bool Has(string s) => n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has("Stone") || Has("Branch") || Has("Blood") || Has("Roots") || Has("Rock") || Has("Pebble")) return Kind.None;
            if (Has("DryGrass") || Has("Crops")) return Kind.Dry;
            if (Has("Grass")) return Kind.Grass;
            if (Has("Herb") || Has("Flower") || Has("SmallPlants")) return Kind.Herb;
            if (Has("Fern") || Has("Underbrush") || Has("Bush")) return Kind.Low;
            return Kind.None;
        }

        public struct Extra
        {
            public float X, Y, Z;
            /// <summary>Index of the G3 plant whose mesh, colour, size and tilt the copy takes (the anchor or a neighbour of it).</summary>
            public int Source;
            /// <summary>Index of the G3 plant the copy was placed around.</summary>
            public int Anchor;
            public float Yaw;      // radians
            public float Scale;    // x the source's scale
            public float Bright;   // x the source's colour (rgb)
        }

        public sealed class Options
        {
            public float Density = 2.5f;          // plants per copyable G3 plant on average (>= 1)
            public float NeighbourRadius = 3f;    // m, plane fit neighbourhood
            public float MixRadius = 6f;          // m, species/colour palette neighbourhood
            public float MinJitter = 0.25f;       // m
            public float MaxJitter = 1.5f;        // m
            public float MaxResidual = 0.3f;      // m, plane fit tolerance
            public float MaxSlope = 1.2f;         // rise per metre
            public float ClumpSize = 9f;          // m, size of the denser / G3-only patches
            public float ClumpContrast = 1f;      // 0 = uniform, 1 = patches from 0 to 2x the mean extra count
            public float SpeciesPatch = 7f;       // m, size of the species colonies
            public float HeightPatch = 6f;        // m, size of the tall / short stands
            public float HeightVariation = 0.2f;  // +- fraction from the stands field
            public float BrightVariation = 0.06f; // +- fraction of colour brightness per copy
            public uint Seed = 0x9E3779B9u;
        }

        /// <summary>Every plant one species (grass), copied as itself.</summary>
        public static List<Extra> Generate(float[] xyz, int count, Options o) => Generate(xyz, count, o, null, null);

        /// <summary>
        /// Extra plants for G3 plants at <paramref name="xyz"/> (x, y, z triplets, metres, y up), <paramref name="count"/> of
        /// them. <paramref name="species"/>: per plant the mesh index (null = all one species, copied as itself);
        /// <paramref name="kinds"/>: per mesh index its <see cref="Kind"/> (null = all grass).
        /// </summary>
        public static List<Extra> Generate(float[] xyz, int count, Options o, int[] species, Kind[] kinds)
        {
            var result = new List<Extra>();
            if (xyz == null || count <= 0 || o.Density <= 1.0001f) return result;
            count = Math.Min(count, xyz.Length / 3);
            Kind KindAt(int i)
            {
                if (species == null || kinds == null) return Kind.Grass;
                int s = species[i];
                return s >= 0 && s < kinds.Length ? kinds[s] : Kind.None;
            }

            // grid of all plants (the ground plane uses every G3 plant: stones and branches sit on the ground too)
            float cell = Math.Max(o.NeighbourRadius, 0.5f);
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < count; i++)
            {
                long k = Key(xyz[i * 3], xyz[i * 3 + 2], cell);
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(8);
                l.Add(i);
            }

            float extraPer = o.Density - 1;
            var nb = new List<int>(32);
            var mix = new List<int>(64);
            var mixW = new List<float>(64);
            var perSpecies = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                if (KindAt(i) == Kind.None) continue;
                float sx = xyz[i * 3], sy = xyz[i * 3 + 1], sz = xyz[i * 3 + 2];
                uint h = Hash((uint)i ^ o.Seed);
                // clumps: the extra count per plant follows a patch field, mean ~extraPer
                float clump = 1 + o.ClumpContrast * (2 * Smooth(Noise(sx, sz, o.ClumpSize, o.Seed ^ 0xC1u)) - 1);
                float want = extraPer * Math.Max(0, clump);
                int n = (int)Math.Floor(want);
                if (Rand(ref h) < want - n) n++;
                if (n == 0) continue;

                Neighbours(grid, xyz, sx, sz, o.NeighbourRadius, cell, nb);
                double sum = 0; int m = 0;
                foreach (int j in nb)
                {
                    if (j == i) continue;
                    float dx = xyz[j * 3] - sx, dz = xyz[j * 3 + 2] - sz;
                    sum += Math.Sqrt(dx * dx + dz * dz); m++;
                }
                float spacing = m > 0 ? (float)(sum / m) : o.MinJitter;
                float radius = Clamp(spacing * 0.6f, o.MinJitter, o.MaxJitter);
                if (!FitPlane(xyz, nb, out float a, out float b, out float c, out float residual)) { a = 0; b = 0; c = sy; residual = m == 0 ? 0 : float.MaxValue; }
                bool planeOk = nb.Count >= 3 && residual <= o.MaxResidual && Math.Sqrt(a * a + b * b) <= o.MaxSlope;
                bool lone = m == 0;
                if (!planeOk && !lone) continue;

                // local palette: copyable plants within MixRadius; per plant weight patch_s / sqrt(n_s), so a species
                // gets sqrt(n_s) x its colony field (the dominant tuft gives up a little to the rare herbs around it)
                mix.Clear(); mixW.Clear(); perSpecies.Clear();
                if (species != null)
                {
                    Neighbours(grid, xyz, sx, sz, o.MixRadius, cell, mix);
                    for (int q = mix.Count - 1; q >= 0; q--)
                        if (KindAt(mix[q]) == Kind.None) mix.RemoveAt(q);
                    foreach (int j in mix) { perSpecies.TryGetValue(species[j], out int cnt); perSpecies[species[j]] = cnt + 1; }
                    float total = 0;
                    foreach (int j in mix)
                    {
                        int s = species[j];
                        float patch = 0.15f + 1.7f * Smooth(Noise(sx, sz, o.SpeciesPatch, o.Seed ^ ((uint)s * 0x9E37u + 0x51u)));
                        total += patch / (float)Math.Sqrt(perSpecies[s]);
                        mixW.Add(total);
                    }
                }
                float stand = 1 + o.HeightVariation * (2 * Smooth(Noise(sx, sz, o.HeightPatch, o.Seed ^ 0x7A11u)) - 1);

                for (int e = 0; e < n; e++)
                {
                    double ang = Rand(ref h) * Math.PI * 2;
                    double r = lone ? o.MinJitter * Rand(ref h) : radius * Math.Sqrt(0.15 + 0.85 * Rand(ref h));
                    float x = sx + (float)(Math.Cos(ang) * r), z = sz + (float)(Math.Sin(ang) * r);
                    float y = lone ? sy : a * x + b * z + c;
                    int src = i;
                    if (mix.Count > 0)
                    {
                        float pick = Rand(ref h) * mixW[mixW.Count - 1];
                        int lo = 0, hi = mixW.Count - 1;
                        while (lo < hi) { int mid = (lo + hi) / 2; if (mixW[mid] < pick) lo = mid + 1; else hi = mid; }
                        src = mix[lo];
                    }
                    result.Add(new Extra
                    {
                        X = x, Y = y, Z = z, Source = src, Anchor = i,
                        Yaw = (float)(Rand(ref h) * Math.PI * 2),
                        Scale = stand * (0.9f + 0.2f * Rand(ref h)),
                        Bright = 1 + o.BrightVariation * (2 * Rand(ref h) - 1),
                    });
                }
            }
            return result;
        }

        /// <summary>Least-squares plane y = a·x + b·z + c through the points; residual = max |error| (m).</summary>
        public static bool FitPlane(float[] xyz, List<int> idx, out float a, out float b, out float c, out float residual)
        {
            a = b = c = 0; residual = float.MaxValue;
            if (idx.Count < 3) return false;
            double mx = 0, my = 0, mz = 0;
            foreach (int j in idx) { mx += xyz[j * 3]; my += xyz[j * 3 + 1]; mz += xyz[j * 3 + 2]; }
            mx /= idx.Count; my /= idx.Count; mz /= idx.Count;
            double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
            foreach (int j in idx)
            {
                double x = xyz[j * 3] - mx, y = xyz[j * 3 + 1] - my, z = xyz[j * 3 + 2] - mz;
                sxx += x * x; sxz += x * z; szz += z * z; sxy += x * y; szy += z * y;
            }
            double det = sxx * szz - sxz * sxz;
            if (Math.Abs(det) < 1e-6) return false;   // collinear
            double aa = (sxy * szz - szy * sxz) / det, bb = (szy * sxx - sxy * sxz) / det;
            a = (float)aa; b = (float)bb; c = (float)(my - aa * mx - bb * mz);
            double r = 0;
            foreach (int j in idx) r = Math.Max(r, Math.Abs(xyz[j * 3 + 1] - (a * xyz[j * 3] + b * xyz[j * 3 + 2] + c)));
            residual = (float)r;
            return true;
        }

        /// <summary>Smooth value noise 0..1 with features of about <paramref name="size"/> metres.</summary>
        public static float Noise(float x, float z, float size, uint seed)
        {
            float fx = x / Math.Max(size, 0.01f), fz = z / Math.Max(size, 0.01f);
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            float tx = fx - ix, tz = fz - iz;
            tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
            float V(int a, int b) => (Hash((uint)a * 0x8DA6B343u ^ (uint)b * 0xD8163841u ^ seed) >> 8) * (1f / 16777216f);
            float v0 = V(ix, iz) + (V(ix + 1, iz) - V(ix, iz)) * tx;
            float v1 = V(ix, iz + 1) + (V(ix + 1, iz + 1) - V(ix, iz + 1)) * tx;
            return v0 + (v1 - v0) * tz;
        }

        // value noise piles up around 0.5: stretch it so the patches really reach both ends (symmetric: mean stays 0.5)
        static float Smooth(float v) { v = Clamp((v - 0.5f) * 1.8f + 0.5f, 0, 1); return v * v * (3 - 2 * v); }

        static void Neighbours(Dictionary<long, List<int>> grid, float[] xyz, float x, float z, float radius, float cell, List<int> outList)
        {
            outList.Clear();
            int cx = (int)Math.Floor(x / cell), cz = (int)Math.Floor(z / cell);
            int reach = (int)Math.Ceiling(radius / cell);
            float r2 = radius * radius;
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (!grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out var l)) continue;
                    foreach (int j in l)
                    {
                        float ex = xyz[j * 3] - x, ez = xyz[j * 3 + 2] - z;
                        if (ex * ex + ez * ez <= r2) outList.Add(j);
                    }
                }
        }

        static long Key(float x, float z, float cell) => ((long)(int)Math.Floor(x / cell) << 32) ^ (uint)(int)Math.Floor(z / cell);

        static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
            return x;
        }

        static float Rand(ref uint state)
        {
            state = Hash(state + 0x6D2B79F5u);
            return (state >> 8) * (1f / 16777216f);
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
