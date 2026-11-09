using System;
using System.Numerics;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free detail height of the terrain relief (Docs/remaster-world.md section 13): a scalar offset along the
    /// smooth surface normal, evaluated per point (no simulation, no neighbours), so any vertex of any cell gets the same
    /// value for the same position and normal. Three terms, all zero-mean:
    /// <list type="bullet">
    /// <item>gullies: erosion-like stripes that run down the fall line, after the "erosion filter" idea (Clay John / Fewes
    /// on Shadertoy; Rune Skovbo Johansen, "Fast and Gorgeous Erosion Filter", 2026): per grid cell a jittered pivot,
    /// a cosine wave across the slope, the cells blended and soft-normalised; each octave's slope bends the next octave's
    /// direction (straight gullies). On vertical faces the fall line is vertical, so the same term makes vertical grooves.</item>
    /// <item>strata: horizontal rock bands on steep faces (sine in height, warped by low-frequency noise).</item>
    /// <item>lumps: 3D value-noise fBm (clods, small bumps).</item>
    /// </list>
    /// Every octave is band-limited by the local sampling spacing (<see cref="Band"/>): a wavelength below ~2 samples is
    /// faded out, so a coarse far mesh gets only the large forms and a dense near mesh the fine ones (no aliasing).
    /// </summary>
    public static class ReliefNoise
    {
        const float TwoPi = 6.2831853f;

        /// <summary>Weight of an octave of wavelength <paramref name="lambda"/> at sampling <paramref name="spacing"/>: 0 below 2 samples, 1 from 3.5.</summary>
        public static float Band(float lambda, float spacing)
        {
            float t = (lambda / Math.Max(spacing, 1e-4f) - 2f) / 1.5f;
            return Smooth(t);
        }

        public static float Smooth(float t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        public static float SmoothStep(float a, float b, float x) => Smooth((x - a) / (b - a));

        static uint Hash(int x, int y, int z, uint seed)
        {
            unchecked
            {
                uint h = seed * 0x9E3779B1u;
                h ^= (uint)x * 0x85EBCA77u; h = (h << 13) | (h >> 19); h *= 0xC2B2AE3Du;
                h ^= (uint)y * 0x27D4EB2Fu; h = (h << 13) | (h >> 19); h *= 0x165667B1u;
                h ^= (uint)z * 0x9E3779B9u; h = (h << 13) | (h >> 19); h *= 0x85EBCA6Bu;
                h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
                return h;
            }
        }

        static float Hash01(int x, int y, int z, uint seed) => (Hash(x, y, z, seed) & 0xFFFFFF) / 16777216f;

        /// <summary>3D value noise in [-1, 1] (quintic fade).</summary>
        public static float Value3(float x, float y, float z, uint seed)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
            float fx = x - ix, fy = y - iy, fz = z - iz;
            float ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10), uy = fy * fy * fy * (fy * (fy * 6 - 15) + 10), uz = fz * fz * fz * (fz * (fz * 6 - 15) + 10);
            float c000 = Hash01(ix, iy, iz, seed), c100 = Hash01(ix + 1, iy, iz, seed);
            float c010 = Hash01(ix, iy + 1, iz, seed), c110 = Hash01(ix + 1, iy + 1, iz, seed);
            float c001 = Hash01(ix, iy, iz + 1, seed), c101 = Hash01(ix + 1, iy, iz + 1, seed);
            float c011 = Hash01(ix, iy + 1, iz + 1, seed), c111 = Hash01(ix + 1, iy + 1, iz + 1, seed);
            float x00 = c000 + (c100 - c000) * ux, x10 = c010 + (c110 - c010) * ux;
            float x01 = c001 + (c101 - c001) * ux, x11 = c011 + (c111 - c011) * ux;
            float y0 = x00 + (x10 - x00) * uy, y1 = x01 + (x11 - x01) * uy;
            return (y0 + (y1 - y0) * uz) * 2 - 1;
        }

        /// <summary>
        /// Erosion gullies at <paramref name="p"/> (world XZ, m) on ground whose downhill gradient is <paramref name="gradient"/>
        /// (dh/dx, dh/dz of the smooth surface). Result about [-1, 1]: + ridge, - gully. <paramref name="largest"/> = wavelength of the
        /// first octave (m); each next one is half. Octaves below the sampling <paramref name="spacing"/> are faded (<see cref="Band"/>).
        /// </summary>
        public static float Gullies(Vector2 p, Vector2 gradient, float largest, int octaves, float spacing)
        {
            var raw = _raw ?? (_raw = new float[16]);
            GulliesRaw(p, gradient, largest, octaves, raw);
            float h = 0, lambda = largest;
            for (int o = 0; o < octaves && o < raw.Length; o++, lambda *= 0.5f) h += Band(lambda, spacing) * raw[o];
            return h;
        }

        [ThreadStatic] static float[] _raw;

        /// <summary>
        /// fix-171: the gullies' octaves without the band limit: <paramref name="raw"/>[o] = octave o's share of
        /// <see cref="Gullies"/> at full weight (wavelength <paramref name="largest"/> / 2^o). The sum is linear in the band
        /// weights (the direction bending does not depend on them), so a shader can fade the fine octaves itself.
        /// </summary>
        public static void GulliesRaw(Vector2 p, Vector2 gradient, float largest, int octaves, float[] raw, float minLambda = 0)
        {
            float norm = 0, amp = 1, lambda = largest;
            Vector2 g = gradient;
            for (int o = 0; o < octaves; o++, amp *= 0.55f) norm += amp;
            amp = 1;
            for (int o = 0; o < octaves; o++, lambda *= 0.5f, amp *= 0.55f)
            {
                if (o < raw.Length) raw[o] = 0;
                if (lambda < 0.6f || lambda < minLambda) continue;   // never sampled finely enough / not wanted (shorter ones follow)
                float f = 1f / lambda;
                float gl = g.Length();
                Vector2 d = gl > 1e-4f ? g / gl : new Vector2(1, 0);
                var across = new Vector2(-d.Y, d.X);
                float px = p.X * f, pz = p.Y * f;
                int cx = (int)Math.Floor(px), cz = (int)Math.Floor(pz);
                float sc = 0, ss = 0, sw = 0;
                for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    int qx = cx + i, qz = cz + j;
                    float jx = qx + 0.15f + 0.7f * Hash01(qx, qz, o, 11u), jz = qz + 0.15f + 0.7f * Hash01(qx, qz, o, 23u);
                    float rx = px - jx, rz = pz - jz;
                    float d2 = rx * rx + rz * rz;
                    float w = 1 - d2 / 2.25f;
                    if (w <= 0) continue;
                    w *= w;
                    float ph = (rx * across.X + rz * across.Y) * TwoPi;
                    sc += w * (float)Math.Cos(ph);
                    ss += w * (float)Math.Sin(ph);
                    sw += w;
                }
                if (sw <= 1e-6f) continue;
                sc /= sw; ss /= sw;
                float len = (float)Math.Sqrt(sc * sc + ss * ss);
                float k = Math.Min(1f, 2f * len) / Math.Max(len, 1e-4f);   // soft normalisation (no loops where waves cancel)
                sc *= k; ss *= k;
                if (o < raw.Length) raw[o] = amp * sc / norm;
                // straight gullies: the sign of the wave's slope bends the next octave's direction
                g += across * (Math.Sign(ss) * 0.5f * amp * Math.Max(gl, 0.2f));
            }
        }

        /// <summary>Rock strata: horizontal bands (wavelength <paramref name="lambda"/> m in height), warped, about [-1, 1].</summary>
        public static float Strata(Vector3 p, float lambda, float spacing)
        {
            float band = Band(lambda, spacing);
            if (band <= 0) return 0;
            float warp = Value3(p.X * 0.06f, p.Y * 0.02f, p.Z * 0.06f, 5u) * 1.6f + Value3(p.X * 0.3f, p.Y * 0.1f, p.Z * 0.3f, 6u) * 0.25f;
            float s = (float)Math.Sin((p.Y / lambda + warp) * TwoPi);
            // sharper ledges: bias towards the crests
            s = s * Math.Abs(s) * 0.6f + s * 0.4f;
            return s * band;
        }

        /// <summary>Lumps: 3D value-noise fBm with the largest wavelength <paramref name="largest"/> m, about [-1, 1].</summary>
        public static float Lumps(Vector3 p, float largest, int octaves, float spacing)
        {
            var raw = _raw ?? (_raw = new float[16]);
            LumpsRaw(p, largest, octaves, raw);
            float h = 0, lambda = largest;
            for (int o = 0; o < octaves && o < raw.Length; o++, lambda *= 0.5f) h += Band(lambda, spacing) * raw[o];
            return h;
        }

        /// <summary>fix-171: the lumps' octaves at full weight (<see cref="GulliesRaw"/>).</summary>
        public static void LumpsRaw(Vector3 p, float largest, int octaves, float[] raw, float minLambda = 0)
        {
            float norm = 0, amp = 1, lambda = largest;
            for (int o = 0; o < octaves; o++, amp *= 0.5f) norm += amp;
            amp = 1;
            for (int o = 0; o < octaves; o++, lambda *= 0.5f, amp *= 0.5f)
            {
                if (o >= raw.Length) break;
                float f = 1f / lambda;
                raw[o] = lambda < 0.6f || lambda < minLambda ? 0 : amp * Value3(p.X * f, p.Y * f, p.Z * f, 40u + (uint)o) / norm;
            }
        }

        /// <summary>
        /// Swells: broad hummocks and hollows of walkable ground (XZ only: a height field), value-noise fBm with a low-frequency
        /// domain warp (no grid look), largest wavelength <paramref name="largest"/> m, each next octave half and 0.45 as strong;
        /// about [-1, 1], band-limited like the rest.
        /// </summary>
        public static float Swells(Vector2 p, float largest, int octaves, float spacing)
        {
            var raw = _raw ?? (_raw = new float[16]);
            SwellsRaw(p, largest, octaves, raw);
            float h = 0, lambda = largest;
            for (int o = 0; o < octaves && o < raw.Length; o++, lambda *= 0.5f) h += Band(lambda, spacing) * raw[o];
            // value noise rarely reaches its range: stretched towards [-1, 1] (the 1.6 is in the raw octaves)
            return Math.Max(-1f, Math.Min(1f, h));
        }

        /// <summary>fix-171: the swells' octaves at full weight, the stretch (x1.6) included, unclamped (<see cref="GulliesRaw"/>).</summary>
        public static void SwellsRaw(Vector2 p, float largest, int octaves, float[] raw, float minLambda = 0)
        {
            float f0 = 1f / largest;
            float wx = Value3(p.X * f0 * 0.5f, 0.31f, p.Y * f0 * 0.5f, 71u), wz = Value3(p.X * f0 * 0.5f, 0.77f, p.Y * f0 * 0.5f, 72u);
            float x = p.X + wx * largest * 0.35f, z = p.Y + wz * largest * 0.35f;
            float norm = 0, amp = 1, lambda = largest;
            for (int o = 0; o < octaves; o++, amp *= 0.45f) norm += amp;
            amp = 1;
            for (int o = 0; o < octaves; o++, lambda *= 0.5f, amp *= 0.45f)
            {
                if (o >= raw.Length) break;
                float f = 1f / lambda;
                raw[o] = lambda < 0.6f || lambda < minLambda ? 0 : 1.6f * amp * Value3(x * f, 0.5f + o * 1.7f, z * f, 60u + (uint)o) / norm;
            }
        }
    }
}
