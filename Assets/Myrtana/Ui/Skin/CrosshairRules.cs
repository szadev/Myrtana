using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// The aiming crosshair of the HUD (<c>gCHUDCrossHair</c>, Docs/formats-gui.md §6.3): <c>G3_HUD_Crossshair.tga</c>
    /// (40×40) at the screen centre, drawn 8…32 reference pixels wide. The player state scripts show it while a bow or
    /// crossbow aims and hide it when the stance goes back to walking; spells have none. Its size follows the distance
    /// from the weapon to the point under the screen centre (OnGetAimAngle): near targets get a big cross, far ones a small.
    /// </summary>
    public static class CrosshairRules
    {
        public const string Image = "G3_HUD_Crossshair.tga";
        public const int MinPixels = 8, MaxPixels = 32;
        /// <summary>Aim distance clamp of OnGetAimAngle, metres (100…4000 G3 units).</summary>
        public const float NearMetres = 1f, FarMetres = 40f;
        /// <summary>Show/hide is an AnimateWindow alpha blend of 200 ms.</summary>
        public const float FadeSeconds = 0.2f;

        /// <summary>SetCrossHairSize argument: 1 − clamp(d, 100, 4000)/4000, d in G3 units (cm).</summary>
        public static float SizeFactor(float distanceMetres)
        {
            if (float.IsNaN(distanceMetres)) distanceMetres = FarMetres;
            float d = Math.Min(FarMetres, Math.Max(NearMetres, distanceMetres));
            return 1f - d / FarMetres;
        }

        /// <summary>Edge length in reference pixels: 8 + 24·t, rounded (gCHUDCrossHair clamps t to 0…1).</summary>
        public static int SizePixels(float distanceMetres)
        {
            float t = Math.Min(1f, Math.Max(0f, SizeFactor(distanceMetres)));
            return (int)Math.Round(MinPixels + (MaxPixels - MinPixels) * t, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Whether the crosshair is up. PS_Ranged_Aim shows it; it stays through the power aim, the shot and — with the
        /// button still held — the reload into the next aim (PS_Ranged_Aim_Reload); PS_Ranged_Walk, sneaking, putting
        /// the weapon away and any reset (hit, knock-down, death) hide it.
        /// </summary>
        public static bool Visible(bool rangedDrawn, bool canFight, bool inAim, bool inShot, bool aimHeld, bool reloading) =>
            rangedDrawn && canFight && (inAim || inShot || aimHeld && reloading);

        /// <summary>Opacity after <paramref name="dt"/> seconds of fading towards shown or hidden (linear, 200 ms).</summary>
        public static float Fade(float alpha, bool shown, float dt)
        {
            float step = FadeSeconds > 0 ? dt / FadeSeconds : 1f;
            return shown ? Math.Min(1f, alpha + step) : Math.Max(0f, alpha - step);
        }
    }
}
