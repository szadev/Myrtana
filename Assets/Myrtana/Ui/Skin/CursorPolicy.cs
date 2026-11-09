namespace Myrtana.Ui.Skin
{
    /// <summary>What the mouse is for right now (<see cref="CursorPolicy.Evaluate"/>).</summary>
    public enum CursorRole
    {
        /// <summary>Normal play: no pointer, the cursor is locked and the mouse turns the hero and the camera.</summary>
        Gameplay,
        /// <summary>No pointer and no mouse look: a spoken line, a video, a loading screen, an action before a page.</summary>
        Hidden,
        /// <summary>A page or menu takes the mouse: the pointer is shown and free.</summary>
        Pointer,
    }

    /// <summary>The UI state the cursor follows, read every frame (<see cref="CursorPolicy"/>).</summary>
    public struct CursorUi
    {
        /// <summary>A loading screen is up (G3 gCHUDFileManager hides the cursor until the world is running).</summary>
        public bool Loading;
        /// <summary>A full-screen video plays: logos, intro, credits (G3 gCHUDMenuIntro2 / Credits2 hide the cursor).</summary>
        public bool Video;
        /// <summary>The story slideshow page is shown (G3 gCHUDPageSlideshow2 hides the cursor).</summary>
        public bool Slideshow;
        /// <summary>The main/game menu or one of its pages and questions (options, saves, title).</summary>
        public bool Menu;
        /// <summary>A HUD page: inventory, trade, loot, character, spells, map, quests, a crafting station.</summary>
        public bool Page;
        /// <summary>The dialog choice list is on screen (not a spoken line, not the wait before the list).</summary>
        public bool DialogChoices;
        /// <summary>The tutorial box with its OK button and check box.</summary>
        public bool Tip;
        /// <summary>A dev tool needs the pointer (the dev console, fly mode's right-button look).</summary>
        public bool Dev;
        /// <summary>Something holds the hero's controls (a conversation, a use, a knock-down …).</summary>
        public bool ControlsLocked;
    }

    /// <summary>
    /// When the mouse pointer is shown (fix-87). G3 draws its GUI cursor only for pages and menus that take mouse input —
    /// the menus, the HUD pages and the dialog choice list (gCDialogManagerCtrl); it hides it explicitly for the intro and
    /// credit videos (gCHUDMenuIntro2/Credits2 ShowCursor(false) while the AnimateCtrl plays), the epilogue slideshow
    /// (gCHUDPageSlideshow2) and the load of a game (gCHUDFileManager::NewGame: ShowCursor(false) … Session Start …
    /// ShowCursor(true)). In play the mouse turns the camera and there is no pointer; a spoken line, a cutscene and the
    /// player-state motion before a page (the walk to a chest and its opening, lying down before the bed's dialog)
    /// show none either: the page or list is not up yet.
    /// </summary>
    public static class CursorPolicy
    {
        public static CursorRole Evaluate(in CursorUi ui)
        {
            if (ui.Dev) return CursorRole.Pointer;
            if (ui.Loading || ui.Video || ui.Slideshow) return CursorRole.Hidden;
            if (ui.Menu || ui.Tip || ui.Page || ui.DialogChoices) return CursorRole.Pointer;
            return ui.ControlsLocked ? CursorRole.Hidden : CursorRole.Gameplay;
        }

        /// <summary>The pointer is drawn.</summary>
        public static bool Visible(CursorRole mode) => mode == CursorRole.Pointer;

        /// <summary>The cursor is locked to the window centre (free only while a pointer is shown).</summary>
        public static bool Locked(CursorRole mode) => mode != CursorRole.Pointer;

        /// <summary>The mouse belongs to the game: mouse look, attacks and use buttons act (only in play).</summary>
        public static bool Captured(CursorRole mode) => mode == CursorRole.Gameplay;

        /// <summary>Lower-case name for the dev runner (probe <c>cursor</c>).</summary>
        public static string Name(CursorRole mode) =>
            mode == CursorRole.Pointer ? "pointer" : mode == CursorRole.Hidden ? "hidden" : "gameplay";
    }
}
