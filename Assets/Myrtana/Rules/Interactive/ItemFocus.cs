using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// How far the hero's normal-mode focus (empty hands, PS_Normal) reaches for world items (fix-254 follow-up).
    /// G3 (PC_Hero gCFocus_PS, CP 1.75) [data]: NormalModeItemScanRange 300 cm, NormalModeItemScanAzi 45°.
    /// gCFocus_PS::FocusCheck [Game.dll 0x1006d7c0, Steam CP 1.75 image] measures in the plane from the hero entity's
    /// position to the candidate's (the candidate's height set to the hero's, fix-90) and takes off the candidate's own
    /// size: d² − h² against the range, h = half the larger horizontal side of its world box (clamped at 0), the same test
    /// for characters, items and interactive objects with their own ranges (500 / 300 / 350 cm). Ours had a flat 2.2 m
    /// to the item's pivot since the first gameplay slice (a67c7f23, a placeholder without a source).
    /// </summary>
    public static class ItemFocus
    {
        /// <summary>NormalModeItemScanRange of PC_Hero, metres [data 300 cm].</summary>
        public const float ScanRangeM = 3f;

        /// <summary>
        /// The largest planar distance (metres, hero position to the item's pivot) at which an item whose world box has the
        /// horizontal half sides <paramref name="halfX"/> and <paramref name="halfZ"/> is in reach: d² − h² ≤ range².
        /// </summary>
        public static float ReachM(float halfX, float halfZ)
        {
            float h = Math.Max(0f, Math.Max(halfX, halfZ));
            return (float)Math.Sqrt(ScanRangeM * ScanRangeM + h * h);
        }

        /// <summary>Whether an item <paramref name="planarDistanceM"/> away (in the plane) with that box is in reach.</summary>
        public static bool InReach(float planarDistanceM, float halfX, float halfZ) =>
            planarDistanceM * planarDistanceM - Math.Max(0f, Math.Max(halfX, halfZ)) * Math.Max(0f, Math.Max(halfX, halfZ)) <= ScanRangeM * ScanRangeM;
    }
}
