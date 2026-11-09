using System;

namespace G3.Audio
{
    /// <summary>
    /// Minimal serializable vector for the engine-free audio logic (metres, Unity axes = G3 axes × 0.01).
    /// Unity serializes it like its own Vector3; the Unity side converts with <c>new Vector3(v.x, v.y, v.z)</c>.
    /// </summary>
    [Serializable]
    public struct V3
    {
        public float x, y, z;

        public V3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static V3 operator -(V3 a, V3 b) => new V3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static V3 operator +(V3 a, V3 b) => new V3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static V3 operator *(V3 a, float k) => new V3(a.x * k, a.y * k, a.z * k);

        public float Dot(V3 o) => x * o.x + y * o.y + z * o.z;
        public float Length => (float)Math.Sqrt(x * x + y * y + z * z);

        public V3 Normalized
        {
            get
            {
                float l = Length;
                return l > 1e-6f ? this * (1f / l) : new V3(0, 0, 0);
            }
        }

        public override string ToString() => $"({x:F1}, {y:F1}, {z:F1})";
    }
}
