using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>What a G3 panel holds; decides how the player's GUI size applies to it (<see cref="UiScaleRules"/>).</summary>
    public enum GuiPanelKind
    {
        /// <summary>HUD windows anchored to the screen edges and corners (bars, compass, quick bar, messages, focus
        /// name): fit scale × GUI size, so they may grow beyond the fit.</summary>
        Hud,
        /// <summary>Pages and boxes laid out in the 1024×768 coordinate system (inventory, character, documents, trade,
        /// loot, stations, dialog, tutorial, confirm/amount, the drag icon): fit scale × GUI size, but never larger
        /// than the fit, so the 4:3 layout always stays on screen.</summary>
        Page,
        /// <summary>Full-screen 4:3 screens (title / Esc menu, loading screen, story slideshow, videos): always the fit,
        /// independent of the GUI size.</summary>
        Screen,
    }

    /// <summary>
    /// Panel scale of the G3 GUI. The original never scales its 1024×768 GUI (formats-gui §7: windows only move with
    /// their anchors), which makes it tiny at modern resolutions. Here 100 % is the 1024×768 coordinate system scaled
    /// to the screen height (to the width on screens narrower than 4:3, <see cref="FitScale"/>), as formats-gui §8
    /// recommends; windows still keep their original anchors inside the wider panel. The player's GUI size (menu
    /// Options → Video) is a multiplier around that (Docs/ui-skin.md "Runtime").
    /// </summary>
    public static class UiScaleRules
    {
        public const int ReferenceWidth = 1024, ReferenceHeight = 768;

        /// <summary>Screen pixels per reference pixel of a <paramref name="kind"/> panel at GUI size
        /// <paramref name="percent"/>.</summary>
        public static float PanelScale(GuiPanelKind kind, int percent, int screenWidth, int screenHeight)
        {
            float fit = FitScale(screenWidth, screenHeight);
            float size = Math.Max(1, percent) / 100f;
            switch (kind)
            {
                case GuiPanelKind.Hud: return fit * size;
                case GuiPanelKind.Page: return fit * Math.Min(1f, size);
                default: return fit;
            }
        }

        /// <summary>
        /// Screen pixels per reference pixel at 100 %: the 1024×768 frame as large as fits, i.e. scaled to the screen
        /// height (1440×1080 at 1080p) or, on screens narrower than 4:3, to the width.
        /// </summary>
        public static float FitScale(int screenWidth, int screenHeight)
        {
            if (screenWidth <= 0 || screenHeight <= 0) return 1f;
            return Math.Min(screenWidth / (float)ReferenceWidth, screenHeight / (float)ReferenceHeight);
        }
    }
}
