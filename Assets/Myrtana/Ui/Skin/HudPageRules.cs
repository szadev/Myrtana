using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>What covers the game view, as far as the HUD cares (G3 page numbers in Docs/formats-gui.md §6.1).</summary>
    public enum HudPage
    {
        /// <summary>No page: exploring, fighting.</summary>
        None,
        /// <summary>Pages 0–4: inventory, spells, documents/map, character, quests.</summary>
        Sheet,
        /// <summary>Trade, loot, crafting, slideshow: the page takes the whole screen (no HUD, no page sidebar).</summary>
        Exclusive,
        Dialog,
        /// <summary>Page 14, the frying pan (a transform station): the play HUD stays, only the sidebar is off.</summary>
        Fry,
    }

    /// <summary>The parts of the HUD page <c>IDD_PAGE_MAIN_2</c> that pages switch on and off.</summary>
    [Flags]
    public enum HudParts
    {
        None = 0,
        FocusBar = 1,
        /// <summary>The hero's HP, SP and MP bars.</summary>
        Vitals = 2,
        QuickSlots = 4,
        Sidebar = 8,
        Compass = 16,
        /// <summary>The focus name above the focused entity (shown together with the focus bar).</summary>
        FocusName = 32,
    }

    public static class HudVisibility
    {
        /// <summary>
        /// The HUD parts a page leaves visible (<c>gCHUDPageMain2::OnPageChanged</c>): playing shows all but the sidebar,
        /// sheets (pages 0–4) show bars, quick slots, sidebar and compass, a dialog only the focus bar, exclusive pages
        /// (trade 5, loot 7, crafting 8–13, slideshow 16) nothing — so the page sidebar never covers the loot or trade
        /// inventory, which reach to x 1000 / 994 under the sidebar's 946–1008 — and the frying pan (14) the play HUD
        /// without the focus name (OnPageChanged table 102d31c0).
        /// Game messages are not part of the table; they always show.
        /// </summary>
        public static HudParts Of(HudPage page)
        {
            switch (page)
            {
                case HudPage.None: return HudParts.FocusBar | HudParts.FocusName | HudParts.Vitals | HudParts.QuickSlots | HudParts.Compass;
                case HudPage.Sheet: return HudParts.Vitals | HudParts.QuickSlots | HudParts.Sidebar | HudParts.Compass;
                case HudPage.Dialog: return HudParts.FocusBar;
                case HudPage.Fry: return HudParts.FocusBar | HudParts.Vitals | HudParts.QuickSlots | HudParts.Compass;
                default: return HudParts.None;
            }
        }
    }

    /// <summary>
    /// The player's HUD switches (Docs/ui-skin.md "HUD toggles"). LEFT CTRL+F1 (the engine's "Toggle HUD", Ini/controls.ini)
    /// hides the whole HUD layer: status bars, quick slots, focus bar and compass. The session key ToggleHUD
    /// (<c>HUD_SessionKey_ToggleHUD</c>, default Scroll Lock) steps through the CP Update Pack cycle: all, without the quick
    /// slots, also without the compass, also without the status bars, all again. Game messages, the focus name and the
    /// page sidebar are not affected.
    /// </summary>
    public sealed class HudToggle
    {
        public const int Steps = 4;

        /// <summary>LEFT CTRL+F1 state: the HUD layer is off.</summary>
        public bool LayerHidden { get; private set; }

        /// <summary>ToggleHUD step 0…3.</summary>
        public int Step { get; private set; }

        public void ToggleLayer() => LayerHidden = !LayerHidden;

        public void NextStep() => Step = (Step + 1) % Steps;

        /// <summary>The parts these switches leave on (AND it with <see cref="HudVisibility.Of"/>).</summary>
        public HudParts Mask
        {
            get
            {
                var all = HudParts.FocusBar | HudParts.Vitals | HudParts.QuickSlots | HudParts.Sidebar | HudParts.Compass | HudParts.FocusName;
                if (LayerHidden) return all & ~(HudParts.Vitals | HudParts.QuickSlots | HudParts.FocusBar | HudParts.Compass);
                if (Step >= 1) all &= ~HudParts.QuickSlots;
                if (Step >= 2) all &= ~HudParts.Compass;
                if (Step >= 3) all &= ~HudParts.Vitals;
                return all;
            }
        }
    }

    /// <summary>The HUD compass (§6.3): the rose image stays, the needle rose turns so its lily points north (world +Z).</summary>
    public static class CompassRules
    {
        /// <summary>
        /// Clockwise screen rotation of the needle in degrees for a camera whose flat forward vector is (x, z):
        /// facing north 0, facing east −90 (north is to the left), facing west +90, facing south ±180.
        /// </summary>
        public static float NeedleDegrees(float forwardX, float forwardZ)
        {
            if (forwardX == 0f && forwardZ == 0f) return 0f;
            return (float)(-Math.Atan2(forwardX, forwardZ) * 180.0 / Math.PI);
        }
    }
}
