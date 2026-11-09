using System;

namespace G3.Formats.Animation
{
    /// <summary>Plain vector (engine space: centimetres, left-handed, Y up).</summary>
    public struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, float s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public static V3 Lerp(V3 a, V3 b, float t) => a + (b - a) * t;
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    /// <summary>
    /// Quaternion (x, y, z, w) as stored by EMotionFX. Hamilton product; a node's world rotation is
    /// <c>parent * local</c> and a vector is rotated by <c>q v q⁻¹</c> — the same convention as UnityEngine.Quaternion,
    /// so values can be copied 1:1 (Docs/formats-animation.md §4).
    /// </summary>
    public struct Q4
    {
        public float X, Y, Z, W;
        public Q4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Q4 Identity = new Q4(0, 0, 0, 1);

        public static Q4 operator *(Q4 a, Q4 b) => new Q4(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public Q4 Conjugate => new Q4(-X, -Y, -Z, W);
        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
        public static float Dot(Q4 a, Q4 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        public Q4 Normalized
        {
            get
            {
                float l = Length;
                return l > 1e-6f ? new Q4(X / l, Y / l, Z / l, W / l) : Identity;
            }
        }

        public V3 Rotate(V3 v)
        {
            // v' = v + 2w(q×v) + 2 q×(q×v)
            float tx = 2 * (Y * v.Z - Z * v.Y), ty = 2 * (Z * v.X - X * v.Z), tz = 2 * (X * v.Y - Y * v.X);
            return new V3(v.X + W * tx + (Y * tz - Z * ty), v.Y + W * ty + (Z * tx - X * tz), v.Z + W * tz + (X * ty - Y * tx));
        }

        /// <summary>Normalised linear interpolation along the shorter arc.</summary>
        public static Q4 Nlerp(Q4 a, Q4 b, float t)
        {
            float s = Dot(a, b) < 0 ? -1 : 1;
            return new Q4(a.X + (b.X * s - a.X) * t, a.Y + (b.Y * s - a.Y) * t, a.Z + (b.Z * s - a.Z) * t, a.W + (b.W * s - a.W) * t).Normalized;
        }

        public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####}, {W:0.####})";
    }
}
