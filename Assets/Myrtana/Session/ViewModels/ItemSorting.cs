using System;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>
    /// How the rows inside each inventory group are ordered [owner]. The groups always keep G3's category order; G3 is
    /// the original order (<see cref="ItemOrder"/>). There is no weight mode: G3 items have no weight.
    /// </summary>
    public enum ItemSortMode { G3, Name, Value, Newest }

    /// <summary>The owner's sort modes on top of G3's order (inventory, trade and loot lists).</summary>
    public static class ItemSorting
    {
        static readonly ItemSortMode[] Cycle = { ItemSortMode.G3, ItemSortMode.Name, ItemSortMode.Value, ItemSortMode.Newest };

        /// <summary>The mode after <paramref name="mode"/> (a click on the selected category tab, or O, cycles through all four).</summary>
        public static ItemSortMode Next(ItemSortMode mode) => Cycle[(Array.IndexOf(Cycle, mode) + 1) % Cycle.Length];

        /// <summary>A stored mode (PlayerPrefs string) back; anything unknown is G3.</summary>
        public static ItemSortMode Parse(string value) =>
            Enum.TryParse(value, out ItemSortMode m) && Array.IndexOf(Cycle, m) >= 0 ? m : ItemSortMode.G3;

        /// <summary>
        /// Two rows in <paramref name="mode"/>: first G3's category (groups never mix), then by shown name (culture order),
        /// by gold in this list (higher first) or by acquisition (newest first); ties and <see cref="ItemSortMode.G3"/> use
        /// G3's own compare.
        /// </summary>
        public static int Compare(ItemSortMode mode, ItemSortKey a, ItemSortKey b)
        {
            int c = ItemOrder.CategoryRank(a.Def.Category).CompareTo(ItemOrder.CategoryRank(b.Def.Category));
            if (c != 0) return c;
            switch (mode)
            {
                case ItemSortMode.Name: c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                case ItemSortMode.Value: c = b.Price.CompareTo(a.Price); break;
                case ItemSortMode.Newest: c = b.Acquired.CompareTo(a.Acquired); break;
            }
            return c != 0 ? c : ItemOrder.Compare(a, b);
        }

        /// <summary>
        /// The G3 theme part drawn as a small sort mark on the selected category tab: none in G3 order (the tabs look
        /// exactly as in the original), the list header's sort arrows for name (ascending) and value (highest first), the
        /// scroll bar's left arrow for newest first. Only the part's <c>_front</c> layer (the framed triangle) is drawn.
        /// </summary>
        public static string GlyphPart(ItemSortMode mode)
        {
            switch (mode)
            {
                case ItemSortMode.Name: return "headerctrl_arrow_up";
                case ItemSortMode.Value: return "headerctrl_arrow_down";
                case ItemSortMode.Newest: return "scrollbar_hor_arrow_left";
                default: return null;
            }
        }
    }
}
