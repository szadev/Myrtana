using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// World ↔ map picture transform of a map (G3 <c>gCMap_PS::CalcUV</c>, Docs/map-teleport.md §1.3): u runs from the
    /// left edge (0) to the right edge (1), v from the top (north, 0) to the bottom (1). Only x and z count.
    /// </summary>
    public readonly struct MapProjection
    {
        public MapProjection(float left, float top, float right, float bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public float Left { get; }
        public float Top { get; }
        public float Right { get; }
        public float Bottom { get; }

        /// <summary>False for a degenerate map (zero width or height), as G3's CalcUV.</summary>
        public bool IsValid => Right != Left && Top != Bottom;

        /// <summary>Map coordinates of a world point; points outside the map fall outside 0…1 (not clamped).</summary>
        public bool TryToMap(float x, float z, out float u, out float v)
        {
            u = v = 0f;
            if (!IsValid) return false;
            u = (x - Left) / (Right - Left);
            v = (Top - z) / (Top - Bottom);
            return true;
        }

        /// <summary>The world point (x, z) under the map coordinates (u, v).</summary>
        public (float x, float z) ToWorld(float u, float v) => (Left + u * (Right - Left), Top - v * (Top - Bottom));

        /// <summary>Inclusive box test on x/z (G3 <c>gCMap_PS::Contains</c>).</summary>
        public bool Contains(float x, float z) =>
            Math.Min(Left, Right) <= x && x <= Math.Max(Left, Right) && Math.Min(Top, Bottom) <= z && z <= Math.Max(Top, Bottom);

        /// <summary>Squared length of the map's diagonal: the larger map wins when several contain the hero.</summary>
        public float DiagonalSquared => (Right - Left) * (Right - Left) + (Top - Bottom) * (Top - Bottom);

        /// <summary>
        /// fix-126: the projection of a picture whose world extent is not stored (the quest log's world map,
        /// <c>G3_HUD_View_World_Back</c>), fitted from points known in both: picture px (<paramref name="pairs"/> px, py; y
        /// down) and world x / z. Least squares per axis (px = a·x + b, py = c·z + d); u = px / width, v = py / height.
        /// False with fewer than two distinct points on an axis.
        /// </summary>
        public static bool TryFit(System.Collections.Generic.IReadOnlyList<(float px, float py, float x, float z)> pairs, float width, float height,
                                  out MapProjection projection)
        {
            projection = default;
            if (pairs == null || pairs.Count < 2 || width <= 0f || height <= 0f) return false;
            if (!Line(pairs, p => p.x, p => p.px, out double a, out double b) || !Line(pairs, p => p.z, p => p.py, out double c, out double d)) return false;
            projection = new MapProjection((float)(-b / a), (float)(-d / c), (float)((width - b) / a), (float)((height - d) / c));
            return projection.IsValid;
        }

        static bool Line(System.Collections.Generic.IReadOnlyList<(float px, float py, float x, float z)> pairs,
                         Func<(float px, float py, float x, float z), float> from, Func<(float px, float py, float x, float z), float> to,
                         out double slope, out double offset)
        {
            double n = pairs.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var p in pairs)
            {
                double x = from(p), y = to(p);
                sx += x;
                sy += y;
                sxx += x * x;
                sxy += x * y;
            }
            double den = n * sxx - sx * sx;
            slope = offset = 0;
            if (Math.Abs(den) < 1e-9) return false;
            slope = (n * sxy - sx * sy) / den;
            offset = (sy - slope * sx) / n;
            return Math.Abs(slope) > 1e-12;
        }

        /// <summary>
        /// Clockwise rotation of the hero arrow on the map, in degrees, for a horizontal forward (x, z): 0 faces north
        /// (map up), 90 east. G3 measures the same angle as acos(f·+Z) signed by the side.
        /// </summary>
        public static float HeadingDegrees(float forwardX, float forwardZ) =>
            forwardX == 0f && forwardZ == 0f ? 0f : (float)(Math.Atan2(forwardX, forwardZ) * 180.0 / Math.PI);
    }
}
