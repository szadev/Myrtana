using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Layout of a G3 TabCtrl window by its style bits (GUI.dll, style decoded into the control at 1001e700, laid out at
    /// 1001f060 [fix-85]): 0x80 = vertical; 0x2 = the cursor starts at the right (bottom) edge and moves left (up), so
    /// tab index 0 is drawn rightmost (bottom-most). The inventory, loot, trade and documents tab controls have style
    /// 0x10000502; the page sidebar 0x10000580 and the quest status tabs 0x10000500 run from the left (top).
    /// </summary>
    public static class TabCtrlStyle
    {
        public const uint Vertical = 0x80, FromFarEdge = 0x2;

        public static bool IsVertical(uint style) => (style & Vertical) != 0;

        public static bool IsReversed(uint style) => (style & FromFarEdge) != 0;

        /// <summary>The tab indexes as drawn from the left (top).</summary>
        public static IReadOnlyList<int> DrawOrder(int count, bool reversed)
        {
            var order = new int[Math.Max(0, count)];
            for (int i = 0; i < order.Length; i++) order[i] = reversed ? order.Length - 1 - i : i;
            return order;
        }

        /// <summary>
        /// The tab <paramref name="steps"/> places to the right (down) on screen from <paramref name="selected"/>, wrapping
        /// (−1 when there are no tabs; no selection counts as index 0).
        /// </summary>
        public static int Step(int selected, int count, int steps, bool reversed)
        {
            if (count <= 0) return -1;
            int d = reversed ? -steps : steps;
            return ((Math.Max(0, selected) + d) % count + count) % count;
        }
    }
}
