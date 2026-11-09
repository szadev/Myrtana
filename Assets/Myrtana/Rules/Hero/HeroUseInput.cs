namespace Myrtana.Rules
{
    /// <summary>The hero's action inputs in the world (not on an open page).</summary>
    public enum HeroInput
    {
        /// <summary>The port's extra use key E (G3 binds E to camera rotation; kept as a convenience).</summary>
        UseKey,
        /// <summary>G3 session key Use1 "Aktion 1": the left mouse button.</summary>
        Primary,
        /// <summary>G3 session key Use2 "Aktion 2": the right mouse button.</summary>
        Secondary,
    }

    /// <summary>What a press of a <see cref="HeroInput"/> does.</summary>
    public enum HeroInputRoute
    {
        /// <summary>Nothing (a page has the cursor, or no rule takes the press).</summary>
        None,
        /// <summary>Use the focus: talk, loot a body, take the item, use the object (G3 PS_Normal_Loop action Use).</summary>
        Use,
        /// <summary>CP quick loot: empty the focused chest or body (CP action 0x90).</summary>
        QuickLoot,
        /// <summary>The drawn stance takes the press (attack, parade, shot, cast; <c>PlayerCombat</c>).</summary>
        Combat,
    }

    /// <summary>
    /// Routes the hero's action presses as G3 does. Default keys (gCSessionKeys::CreateDefaultKeys, Game.dll; the same in
    /// CP 1.75): Use1 "Aktion 1" = left mouse button, Use2 "Aktion 2" = right mouse button, no keyboard key for either.
    /// OnPlayerAction (Script_Game 0x10077710): in PS_Normal (no weapon, bow or spell drawn) the press edge of Use1
    /// (IsPressed and not IsPressedBefore) queues the Use action on the focus; in PS_Melee/PS_Ranged/PS_Magic the same
    /// key attacks, shoots or casts, and the focus is not used. OnPlayerSecondaryAction: Use2 in PS_Normal is the CP quick
    /// loot (ge3.ini [Game] QuickLoot), in a drawn stance the parade. Empty hands are PS_Normal: a click never starts a
    /// fist attack unless the fists are drawn.
    /// </summary>
    public static class HeroUseInput
    {
        /// <param name="input">The pressed input (on the press, G3's IsPressed and not IsPressedBefore).</param>
        /// <param name="stanceDrawn">A weapon, bow, spell or the fists are drawn, or a draw is under way.</param>
        /// <param name="cursorLocked">The mouse steers the camera (no page or menu has the cursor).</param>
        /// <param name="quickLoot">The CP QuickLoot option is on.</param>
        /// <param name="takeAllChord">
        /// fix-205 [owner] QoL, not G3: the quick-loot chord is held (Ctrl or the right button with the left button,
        /// <c>G3.Player.QuickLootChord</c>): Use1 takes everything instead of opening the loot page.
        /// </param>
        public static HeroInputRoute Route(HeroInput input, bool stanceDrawn, bool cursorLocked, bool quickLoot, bool takeAllChord = false)
        {
            if (input == HeroInput.UseKey) return HeroInputRoute.Use; // the port's key works in every stance, as before
            if (!cursorLocked) return HeroInputRoute.None;            // a click on a page belongs to the page
            if (stanceDrawn) return HeroInputRoute.Combat;
            if (input == HeroInput.Primary) return takeAllChord ? HeroInputRoute.QuickLoot : HeroInputRoute.Use;
            return quickLoot ? HeroInputRoute.QuickLoot : HeroInputRoute.None;
        }
    }
}
