namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Sorting orders of the in-game UI documents (Docs/ui-skin.md). G3 draws its HUD page (<c>IDD_PAGE_MAIN_2</c>) first
    /// and the overlay pages over it (<c>gCHUDDialogMain2</c>, formats-gui §6.1), so the game messages lie under an open
    /// page: hidden behind its panels, visible beside them (fix-116). The port keeps the HUD's interactive parts (page
    /// sidebar, quick bar drag) above the pages so they take clicks, but puts the messages on their own document below.
    /// </summary>
    public static class GuiLayers
    {
        /// <summary>The plain HUD document.</summary>
        public const int PlainHud = 100;
        /// <summary>HP/SP/MP and focus bars (VitalsHud).</summary>
        public const int Vitals = 101;
        /// <summary>The game messages (G3MessageArea): under the pages, as in G3.</summary>
        public const int HudMessages = 102;
        /// <summary>The G3-look pages (inventory, map, character …).</summary>
        public const int Pages = 105;
        /// <summary>The G3 HUD (compass, quick bar, page sidebar, focus name): above the pages so the sidebar takes clicks.</summary>
        public const int Hud = 106;
    }
}
