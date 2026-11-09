using System;

namespace G3.Player
{
    /// <summary>
    /// Pure rules for the third-person camera's pivot height (no engine types; tested in Tools/G3Tests), fix-60.
    /// G3's camera is based on the hero's head (gCCameraAI_PS UpdatePlayerPosition: <c>GetHeadMat</c> plus the mode's
    /// CamOffset, Docs/hero.md "Camera"). The drawn stances crouch: the G3 motions keep the feet on the ground but lower the
    /// pelvis and the head (Hero_Stand_None_None_P0_Move_Stand head 177.9 cm; None_1H 168.5, None_2H / None_Staff 165.2,
    /// None_Fist 161.0, Bow_None / CrossBow_None 173.3, 1H_1H 169.8, Shield_1H P1 165.8). With a pivot at a fixed height
    /// above the feet the hero dropped by 4–17 cm in the picture on every draw ("drawing a weapon lowers the hero"); the
    /// pivot follows the stance's head height instead, so the hero keeps his place on screen as in G3.
    /// </summary>
    public static class CameraPivotRules
    {
        /// <summary>The hero's head bone above his feet in the unarmed stand (m): the pivot height is measured from it.</summary>
        public const float RestHeadHeight = 1.7786f;
        /// <summary>Limits of the head offset (m): a lying or sitting hero does not pull the camera into the ground.</summary>
        public const float MaxDrop = 0.6f, MaxRise = 0.15f;
        /// <summary>Time constant of the head offset's low-pass (s): stance changes (a draw takes 0.3–0.6 s) come through,
        /// the walk and run head bob (about 2 steps per second) is mostly filtered out.</summary>
        public const float HeadFollowSeconds = 0.18f;

        /// <summary>How far the pivot moves with the head (m, negative = lower) for a head at <paramref name="headAboveFeet"/>.</summary>
        public static float HeadOffset(float headAboveFeet) => Math.Clamp(headAboveFeet - RestHeadHeight, -MaxDrop, MaxRise);

        /// <summary>One low-pass step of the head offset towards <paramref name="target"/>.</summary>
        public static float Follow(float current, float target, float dt, float seconds = HeadFollowSeconds) =>
            seconds <= 0f || dt <= 0f ? (dt <= 0f ? current : target) : current + (target - current) * (1f - (float)Math.Exp(-dt / seconds));
    }
}
