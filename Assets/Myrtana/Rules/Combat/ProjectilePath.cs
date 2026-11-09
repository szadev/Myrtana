using System;

namespace Myrtana.Rules
{
    /// <summary>A point or direction in metres (pure; the Unity side converts its vectors).</summary>
    public readonly struct P3
    {
        public readonly float X, Y, Z;
        public P3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static P3 operator +(P3 a, P3 b) => new P3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static P3 operator -(P3 a, P3 b) => new P3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static P3 operator *(P3 a, float k) => new P3(a.X * k, a.Y * k, a.Z * k);
        public static float Dot(P3 a, P3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public float Length => (float)Math.Sqrt(Dot(this, this));
    }

    /// <summary>
    /// Where an arrow, bolt or spell missile meets a body on one step of its flight (fix-68). G3 projectiles are physics
    /// bodies that collide with every character's movement capsule (shape group 6, Docs/combat.md §9.8) and with the
    /// level, so the first thing on the path takes the arrow: an orc between the shooter and the hero gets it. Bodies are
    /// tested as capsules (axis segment + radius) along the step's segment; the entry point's fraction of the step is
    /// returned so the nearest of all bodies and the level can win.
    /// </summary>
    public static class ProjectilePath
    {
        /// <summary>
        /// Fraction (0..1) of the segment <paramref name="from"/>→<paramref name="to"/> where it enters the capsule
        /// <paramref name="a"/>–<paramref name="b"/> of radius <paramref name="radius"/>, or null when it does not touch
        /// it. A segment that starts inside the capsule touches at 0.
        /// </summary>
        public static float? Enter(P3 from, P3 to, P3 a, P3 b, float radius)
        {
            var d = to - from;
            float r2 = radius * radius;
            if (DistanceSq(from, a, b) <= r2) return 0f;
            if (P3.Dot(d, d) < 1e-12f) return null;
            ClosestBetweenSegments(from, to, a, b, out float s, out _);
            if (DistanceSq(from + d * s, a, b) > r2) return null;
            // The distance of the moving point to the capsule's axis is convex along the path: the entry is the one crossing
            // of the radius before the closest approach (bisection, well under a millimetre for a step of a few metres).
            float lo = 0f, hi = s;
            for (int i = 0; i < 24; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (DistanceSq(from + d * mid, a, b) <= r2) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        static float DistanceSq(P3 p, P3 q)
        {
            var v = p - q;
            return P3.Dot(v, v);
        }

        static float DistanceSq(P3 p, P3 a, P3 b)
        {
            var ab = b - a;
            float l2 = P3.Dot(ab, ab);
            float t = l2 > 1e-12f ? Clamp01(P3.Dot(p - a, ab) / l2) : 0f;
            return DistanceSq(p, a + ab * t);
        }

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>Closest points of two segments (Ericson, Real-Time Collision Detection 5.1.9): s on p1–q1, t on p2–q2.</summary>
        static void ClosestBetweenSegments(P3 p1, P3 q1, P3 p2, P3 q2, out float s, out float t)
        {
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            float a = P3.Dot(d1, d1), e = P3.Dot(d2, d2), f = P3.Dot(d2, r);
            const float eps = 1e-9f;
            if (a <= eps && e <= eps) { s = t = 0; return; }
            if (a <= eps)
            {
                s = 0;
                t = Clamp01(f / e);
                return;
            }
            float c = P3.Dot(d1, r);
            if (e <= eps)
            {
                t = 0;
                s = Clamp01(-c / a);
                return;
            }
            float b = P3.Dot(d1, d2);
            float denom = a * e - b * b;
            s = denom > eps ? Clamp01((b * f - c * e) / denom) : 0f;
            t = (b * s + f) / e;
            if (t < 0)
            {
                t = 0;
                s = Clamp01(-c / a);
            }
            else if (t > 1)
            {
                t = 1;
                s = Clamp01((b - c) / a);
            }
        }
    }
}
