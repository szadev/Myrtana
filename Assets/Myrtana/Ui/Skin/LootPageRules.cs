namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Rules of the loot page <c>IDD_PAGE_LOOT_2</c> that do not need Unity (Docs/ui-skin.md "Loot").
    /// </summary>
    public static class LootPageRules
    {
        /// <summary>
        /// Take all closes the page, for the button as for the CP session key U (owner decision, fix-40; CP 1.75
        /// OnHUDHandleInput page 7: U takes everything and closes). The original button (gCHUDPageLoot2 → 100a0810) only
        /// moved the stacks and left the page open.
        /// </summary>
        public const bool TakeAllCloses = true;

        /// <summary>Take all is enabled only while the pool list has stacks (gCHUDPageLoot2 vslot24/25/70: EnableWindow(count != 0)).</summary>
        public static bool TakeAllEnabled(int poolStacks) => poolStacks > 0;
    }
}
