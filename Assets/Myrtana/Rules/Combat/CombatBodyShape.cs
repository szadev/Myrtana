using System;

namespace Myrtana.Rules
{
    /// <summary>A capsule in a fighter's frame (metres; y up, z forward): axis 1 = vertical, 2 = along the body.</summary>
    public readonly struct BodyCapsule
    {
        public readonly int Axis;
        public readonly float Radius, Length, CenterY, CenterZ;

        public BodyCapsule(int axis, float radius, float length, float centerY, float centerZ)
        {
            Axis = axis; Radius = radius; Length = length; CenterY = centerY; CenterZ = centerZ;
        }

        /// <summary>Highest point above the feet.</summary>
        public float Top => Axis == 1 ? CenterY + Length * 0.5f : CenterY + Radius;

        public override string ToString() => $"axis {Axis} r {Radius:0.00} len {Length:0.00} centre ({CenterY:0.00}, {CenterZ:0.00})";
    }

    /// <summary>
    /// The body capsule of a fighter, from the extent of its skeleton's joints (plus flesh): spacing, bites, diagnostics.
    /// Melee hits use the separate, much taller weapon trigger (<see cref="MeleeHitGeometry.Trigger"/>).
    /// </summary>
    public static class CombatBodyShape
    {
        /// <summary>A body longer than this times its height is a long (four-legged) animal.</summary>
        public const float LongRatio = 1.2f;

        /// <summary>
        /// The body capsule for joint extents (<paramref name="width"/> x, <paramref name="height"/> = top above the feet,
        /// <paramref name="length"/> z, <paramref name="centerZ"/> = middle along z): long animals lie along the body,
        /// upright bodies stand.
        /// </summary>
        public static BodyCapsule Body(float width, float height, float length, float centerZ)
        {
            height = Clamp(height, 0.4f, 8f);
            if (IsLong(height, length))
            {
                float r = Clamp(Math.Min(width, height) * 0.5f, 0.15f, 2f);
                return new BodyCapsule(2, r, Math.Max(length * 0.9f, r * 2), height * 0.5f, centerZ);
            }
            float radius = Clamp(Math.Max(width, length) * 0.3f, 0.2f, 2f);
            return new BodyCapsule(1, radius, Math.Max(height, radius * 2), height * 0.5f, 0f);
        }

        public static bool IsLong(float height, float length) => length > height * LongRatio;

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
