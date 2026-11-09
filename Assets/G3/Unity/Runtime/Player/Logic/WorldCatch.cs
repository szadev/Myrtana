using System;

namespace G3.Player
{
    /// <summary>
    /// Catching a hero who fell out of the world (a teleport or a seam put him under the terrain). G3's collision has no
    /// holes, so the original never needed this [interp]: after <see cref="MinFallSeconds"/> of falling with nothing at all
    /// below him (or once below <see cref="WorldFloorY"/>), he is put on the surface just above him (the one he fell
    /// through, <see cref="LowestSurfaceAbove"/>), else back where he last stood. A real fall always has ground below, so ordinary falls (cliffs, under bridges, caves) are never caught.
    /// </summary>
    public static class WorldCatch
    {
        /// <summary>Seconds of falling before the check (a jump or a step off a ledge never reaches it in the void).</summary>
        public const float MinFallSeconds = 1f;
        /// <summary>How far down the ground is looked for (m).</summary>
        public const float GroundProbe = 400f;
        /// <summary>No part of Myrtana lies this low (m).</summary>
        public const float WorldFloorY = -500f;

        public static bool ShouldCatch(float fallSeconds, bool groundBelow, float feetY) =>
            feetY < WorldFloorY || fallSeconds >= MinFallSeconds && !groundBelow;

        /// <summary>A cast restarts this far below the surface it found (m).</summary>
        public const float SurfaceStep = 0.05f;

        /// <summary>
        /// The lowest surface above the feet, found by casting down from <paramref name="startY"/> again and again just
        /// below each surface hit (a mesh collider gives one hit per cast, the topmost). <paramref name="castDown"/> casts
        /// from a height down to the feet and returns the hit height, or null. Null when nothing lies above.
        /// fix-38 (r27 trelis_01): the topmost surface put the hero who fell under Trelis's ground onto a rock 64 m above
        /// the escort, and the guide died alone below.
        /// </summary>
        public static float? LowestSurfaceAbove(float feetY, float startY, Func<float, float?> castDown, int maxCasts = 24)
        {
            if (castDown == null) return null;
            float? best = null;
            float from = startY;
            for (int i = 0; i < maxCasts && from > feetY; i++)
            {
                var hit = castDown(from);
                if (hit == null || hit.Value < feetY) break;
                best = hit.Value;
                from = hit.Value - SurfaceStep;
            }
            return best;
        }
    }
}
