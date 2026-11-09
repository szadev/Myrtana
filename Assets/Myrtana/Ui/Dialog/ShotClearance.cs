using System;

namespace Myrtana.Ui.Dialog
{
    /// <summary>
    /// How far the dialog camera may stand from the point it looks at when the level is in the way (fix-84: in
    /// Reddock's cave the camera went through the ceiling). G3's camera traces between its target and the wished
    /// position and keeps off walls (gCCameraAI_PS::DialogPresetIsValid / CorrectCameraPosition / MaintainWallDistance,
    /// Game 0x1002dca0 / 0x10026b80 / 0x1002aa10); here the way is traced both ways — from the target out
    /// (<paramref name="outwardFree"/>, the first surface facing the target) and from the wished position back in
    /// (<paramref name="inwardHit"/>, the first surface facing the camera: one-sided rock seen from inside a cave or
    /// a mountain shell the outward trace cannot see) — and the camera stops a margin before the nearer one.
    /// </summary>
    public static class ShotClearance
    {
        /// <summary>The camera stays this far in front of a blocking surface, m (G3 MaintainWallDistance: 17.5 cm).</summary>
        public const float WallMargin = 0.175f;
        /// <summary>Never farther from the target than this, m (no preset needs more; guards against broken inputs).</summary>
        public const float MaxDistance = 6f;

        /// <param name="range">Distance target → wished camera position, m.</param>
        /// <param name="outwardFree">Free distance traced from the target towards the camera (= range when clear).</param>
        /// <param name="inwardHit">Distance traced from the wished position towards the target to the first surface
        /// (negative = none; 0 = the wished position is inside geometry).</param>
        /// <param name="minDistance">The camera keeps at least this distance (or the whole range when shorter).</param>
        /// <returns>The distance from the target to place the camera at.</returns>
        public static float Distance(float range, float outwardFree, float inwardHit, float minDistance)
        {
            if (!(range > 0)) return 0;
            float free = Math.Min(range, Math.Max(0, outwardFree));
            bool blocked = free < range;
            if (inwardHit == 0) free = 0; // inside rock, its surface unknown: as close as allowed
            else if (inwardHit > 0 && inwardHit < range)
            {
                free = Math.Min(free, range - inwardHit);
                blocked = true;
            }
            if (blocked) free -= WallMargin;
            free = Math.Min(free, MaxDistance);
            return Math.Max(free, Math.Min(minDistance, range));
        }

        /// <summary>The share of the wished distance that stays free (a preset below the director's threshold is skipped).</summary>
        public static float FreeShare(float range, float outwardFree, float inwardHit) =>
            range > 0 ? Distance(range, outwardFree, inwardHit, 0) / range : 1;
    }
}
