using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// fix-103: the hero's use spot must lie outside the used object's collision (and the world's). The spot the use asks
    /// for (an NPC slot, a mesh offset, or <c>distance</c> m in front of the object's bounds centre on the hero's side) can
    /// fall inside a large object: a Beliar shrine's slab is deeper than the 0.9 m a shrine asks for, so the approach got
    /// stuck at the slab, gave up and teleported the hero into it (the owner was locked inside the shrine). G3 avoids that
    /// with the object's interaction pose and the collision switched off for the walk (<c>_AI_Goto_Ext</c>); here the spot is
    /// moved out instead: along the line from the object to the spot first, then turned round the object, the first spot
    /// where the hero's body is free (and, in the first pass, on the walkable mesh). Pure: the Unity side answers the probes.
    /// </summary>
    public static class UseSpotClearance
    {
        /// <summary>
        /// The hero's core round his axis, m [interp]: a spot is embedded when the core (above step height) meets the
        /// collision. G3 poses users with the body against the object (an alchemy table, a water barrel, a cauldron: 45 of
        /// 45 alchemy slots overlap the full 0.35 m capsule, offline scan <c>collision usespots</c>); such a shallow overlap
        /// is resolved by the character controller and keeps the pose, a core inside the object is not.
        /// </summary>
        public const float CoreRadius = 0.15f;

        /// <summary>Step and furthest push outwards from the asked spot, m [interp].</summary>
        public const float Step = 0.1f, MaxPush = 2.5f;

        /// <summary>Turns round the object tried after the straight push, deg (the hero's side first).</summary>
        static readonly float[] Turns = { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 100f, -100f, 135f, -135f, 180f };

        /// <summary>
        /// The nearest free spot to (<paramref name="x"/>, <paramref name="z"/>) going away from the object centre
        /// (<paramref name="cx"/>, <paramref name="cz"/>). <paramref name="free"/> says whether the hero's body fits at a
        /// point; <paramref name="walkable"/> (optional) whether it is on the walkable mesh: a first pass needs both, a
        /// second only room. False when no spot within <see cref="MaxPush"/> is free: the caller keeps the hero where he is.
        /// </summary>
        public static bool TryClear(float x, float z, float cx, float cz, Func<float, float, bool> free, Func<float, float, bool> walkable,
                                    out float rx, out float rz, out int tried)
        {
            tried = 0;
            rx = x;
            rz = z;
            float dx = x - cx, dz = z - cz, r = (float)Math.Sqrt(dx * dx + dz * dz);
            if (r < 1e-3f)
            {
                dx = 0f;
                dz = 1f;
                r = 0f;
            }
            else
            {
                dx /= r;
                dz /= r;
            }
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 0 && walkable == null) continue;
                foreach (float turn in Turns)
                {
                    double a = turn * Math.PI / 180.0;
                    float ux = (float)(dx * Math.Cos(a) + dz * Math.Sin(a)), uz = (float)(-dx * Math.Sin(a) + dz * Math.Cos(a));
                    // the asked radius first (turn 0: the asked spot itself), then outwards
                    for (float push = 0f; push <= MaxPush + 1e-4f; push += Step)
                    {
                        float px = cx + ux * (r + push), pz = cz + uz * (r + push);
                        tried++;
                        if (!free(px, pz)) continue;
                        if (pass == 0 && !walkable(px, pz)) continue;
                        rx = px;
                        rz = pz;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// A hero's footprint circle (radius <paramref name="radius"/>) at (<paramref name="x"/>, <paramref name="z"/>)
        /// overlaps an oriented box footprint (centre, half extents along its own right/forward axes, yaw deg): the offline
        /// scan's test of a use spot against an object's collision box.
        /// </summary>
        public static bool CircleHitsBox(float x, float z, float radius, float bx, float bz, float halfRight, float halfForward, float yawDeg)
        {
            double a = yawDeg * Math.PI / 180.0;
            float fx = (float)Math.Sin(a), fz = (float)Math.Cos(a); // forward
            float rx = fz, rz = -fx;                                 // right
            float px = x - bx, pz = z - bz;
            float lr = px * rx + pz * rz, lf = px * fx + pz * fz;
            float qx = Math.Max(-halfRight, Math.Min(halfRight, lr)), qf = Math.Max(-halfForward, Math.Min(halfForward, lf));
            float ex = lr - qx, ef = lf - qf;
            return ex * ex + ef * ef < radius * radius;
        }
    }
}
