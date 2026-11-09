using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// fix-243 (owner: picking a small iron-bound chest, the hero knelt inside it). Where the hero kneels to use a chest.
    /// G3 (CP 1.75 data, <c>G3Cli anim actor G3_Chest_Body_*</c>): all five chest actors (Chest, Chest_02, Chest_03,
    /// ChestPod_01, ChestPod_02; the 1,066 chests of the world) carry the placement dummy
    /// <see cref="ObjectUseParts.ExactDummy"/> at (0, 0, 140.882) cm in front of the actor origin, the chest's ROOT 135.6 cm
    /// behind it; the engine aligns the user to that dummy facing the chest (its −Z). The hero's kneel motions move his body
    /// forward of his own origin (<c>Hero_Kneel_*_Lockpick_Loop</c> / <c>OpenChest_*</c>: pelvis 33 cm ahead, hands further),
    /// so his origin must stay that far back: before, he knelt 0.85 m from the chest's mesh centre on whatever side he came
    /// from, and the kneel pushed his legs and body through the front (a 43 cm half-deep chest). Pure: positions in metres,
    /// yaw in degrees (0 = +Z, 90 = +X).
    /// </summary>
    public static class ChestUsePose
    {
        /// <summary>The dummy's distance in front of the chest actor's origin, m (140.882 cm, identical in all five actors).</summary>
        public const float KneelDistance = 1.40882f;

        /// <summary>
        /// The hero's kneel point and facing for a chest at <paramref name="origin"/> whose actor +Z is
        /// <paramref name="forward"/> (the object's forward, scaled by <paramref name="scale"/>): the dummy, facing the chest.
        /// </summary>
        public static (float x, float y, float z, float yaw) Spot(float[] origin, float[] forward, float scale)
        {
            float fx = forward[0], fz = forward[2], len = (float)Math.Sqrt(fx * fx + fz * fz);
            if (len < 1e-4f)
            {
                fx = 0f;
                fz = 1f;
            }
            else
            {
                fx /= len;
                fz /= len;
            }
            float d = KneelDistance * (scale > 0f ? scale : 1f);
            float x = origin[0] + fx * d, z = origin[2] + fz * d;
            return (x, origin[1], z, FaceYaw(x, z, origin[0], origin[2], YawOf(-fx, -fz)));
        }

        /// <summary>The yaw looking from (<paramref name="fromX"/>, <paramref name="fromZ"/>) at (<paramref name="toX"/>,
        /// <paramref name="toZ"/>); <paramref name="fallback"/> when they (nearly) coincide.</summary>
        public static float FaceYaw(float fromX, float fromZ, float toX, float toZ, float fallback)
        {
            float dx = toX - fromX, dz = toZ - fromZ;
            return dx * dx + dz * dz < 1e-6f ? fallback : YawOf(dx, dz);
        }

        /// <summary>Yaw of a horizontal direction, degrees in [0, 360).</summary>
        public static float YawOf(float x, float z)
        {
            float yaw = (float)(Math.Atan2(x, z) * 180.0 / Math.PI);
            return yaw < 0f ? yaw + 360f : yaw;
        }
    }
}
