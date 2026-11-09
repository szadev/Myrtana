using System;
using System.Text.Json.Serialization;

namespace Myrtana.Simulation
{
    /// <summary>
    /// A position in the world (metres, Unity axes: x east, y up, z north) and a heading (degrees clockwise from +z, seen
    /// from above). Upright characters need no more than that.
    /// </summary>
    public readonly struct WorldPose : IEquatable<WorldPose>
    {
        [JsonConstructor]
        public WorldPose(float x, float y, float z, float yaw)
        {
            X = x;
            Y = y;
            Z = z;
            Yaw = yaw;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float Yaw { get; }

        public float DistanceTo(WorldPose other)
        {
            float dx = other.X - X, dy = other.Y - Y, dz = other.Z - Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public float HorizontalDistanceTo(WorldPose other)
        {
            float dx = other.X - X, dz = other.Z - Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Heading from this pose towards <paramref name="other"/> (this pose's yaw when they coincide).</summary>
        public float YawTowards(WorldPose other)
        {
            float dx = other.X - X, dz = other.Z - Z;
            return dx * dx + dz * dz < 1e-6f ? Yaw : (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
        }

        /// <summary>Point on the straight line from <paramref name="a"/> to <paramref name="b"/>, facing along the line.</summary>
        public static WorldPose Along(WorldPose a, WorldPose b, float t)
        {
            if (t >= 1) return b;
            if (t < 0) t = 0;
            return new WorldPose(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.YawTowards(b));
        }

        public WorldPose WithYaw(float yaw) => new WorldPose(X, Y, Z, yaw);

        public bool Equals(WorldPose o) => X == o.X && Y == o.Y && Z == o.Z && Yaw == o.Yaw;
        public override bool Equals(object obj) => obj is WorldPose o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, Yaw);
        public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1}) {Yaw:F0}°";
    }
}
