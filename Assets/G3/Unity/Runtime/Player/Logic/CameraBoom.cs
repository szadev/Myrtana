using System;

namespace G3.Player
{
    /// <summary>
    /// Pure rules of the third-person camera boom (no engine types; tested in Tools/G3Tests): which sweep hits may pull
    /// the camera in, and how a boom length follows the free length (in at once, smoothly back out, frame-rate
    /// independent). Used by G3PlayerCamera for the shoulder leg and the back leg.
    /// </summary>
    public static class CameraBoom
    {
        /// <summary>
        /// Whether a sweep hit pulls the camera in. Not: hits the sweep started inside of (distance 0, as a single
        /// sphere cast ignores them), the hero's own colliders, and obstacles narrower than
        /// <paramref name="minWidth"/> in plan view (posts, poles, thin trunks, small props) — those cross the boom for
        /// a few frames while running and would snap the camera in and out (G3's camera filters the shapes it
        /// collides with as well).
        /// </summary>
        public static bool Blocks(float hitDistance, bool ownBody, float planWidth, float minWidth) =>
            hitDistance > 0f && !ownBody && planWidth >= minWidth;

        /// <summary>Plan-view width of a world-space bounding box (the larger horizontal side).</summary>
        public static float PlanWidth(float sizeX, float sizeZ) => Math.Max(sizeX, sizeZ);

        /// <summary>
        /// Next boom length: shorter free length → at once (no wall clipping); longer → critically damped ease with
        /// <paramref name="smoothTime"/> (velocity kept in <paramref name="velocity"/>), never overshooting.
        /// <paramref name="current"/> &lt; 0 = not placed yet → snaps.
        /// </summary>
        public static float Follow(float current, float free, ref float velocity, float smoothTime, float dt)
        {
            if (current < 0f || free <= current) { velocity = 0f; return free; }
            if (dt <= 0f) return current;
            return SmoothDamp(current, free, ref velocity, Math.Max(1e-4f, smoothTime), dt);
        }

        /// <summary>Critically damped spring towards <paramref name="target"/> (the usual closed-form approximation).</summary>
        public static float SmoothDamp(float current, float target, ref float velocity, float smoothTime, float dt)
        {
            float omega = 2f / smoothTime;
            float x = omega * dt;
            float decay = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float temp = (velocity + omega * change) * dt;
            velocity = (velocity - omega * temp) * decay;
            float next = target + (change + temp) * decay;
            if ((target - current > 0f) == (next > target)) { next = target; velocity = 0f; }
            return next;
        }

        /// <summary>Yaw offsets (degrees) tried for a cramped object use, the hero's own back first.</summary>
        public static readonly float[] UseOrbitYaws = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };

        /// <summary>A boom shorter than this share of the wished distance counts as cramped.</summary>
        public const float CrampedShare = 0.67f;

        /// <summary>
        /// [interp] The camera side for an object use in a cramped spot (fix-46, r28 par_misc: the water pipe in a hut, a
        /// wall right behind the sitting hero pulled the boom under 0.6 m, the body was hidden and the view showed the wall):
        /// the index of <paramref name="free"/> (free boom lengths for <see cref="UseOrbitYaws"/>) to use. The hero's back
        /// (index 0, or <paramref name="current"/> when it still holds) while it is not cramped; else the freest side,
        /// switching only for a clearly longer boom (0.5 m) so the camera does not hop between equal sides.
        /// </summary>
        public static int UseOrbit(float[] free, float wanted, int current)
        {
            if (free == null || free.Length == 0) return 0;
            if (current < 0 || current >= free.Length) current = 0;
            float enough = wanted * CrampedShare;
            if (free[0] >= enough) return 0;
            if (free[current] >= enough) return current;
            int best = current;
            for (int i = 0; i < free.Length; i++)
                if (free[i] > free[best] + 0.5f) best = i;
            return best;
        }

        /// <summary>Debug: whether a change of the camera distance between two frames counts as a jump.</summary>
        public static bool IsJump(float previous, float current, float threshold) =>
            previous >= 0f && Math.Abs(current - previous) > threshold;
    }
}
