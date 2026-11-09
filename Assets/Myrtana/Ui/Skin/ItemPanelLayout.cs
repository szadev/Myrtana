using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// How the item panel (IDD_VIEW_ITEM_2) makes room for a long description. G3 draws ITEMDESC_* into the fixed
    /// IDC_LABEL_ITEM_DESC_2 box (90,40–264,106: 174×66, TNR 13 bold italic, word-wrapped static; no shrink or scroll code
    /// exists for it [gCHUDViewItem2 fill 1007edb0 only sets the text]) and the original texts are written to fit four
    /// lines there. Our faces are wider (Cyrillic substitutes), so a text can need a fifth or sixth line. Then the box grows
    /// downwards and the stats table (IDC_TABLE_ITEM_STATS_2, 18,110–264,328) moves down by the same amount, so the whole
    /// description stays readable and nothing overlaps [I: owner wants the full text; G3 never needs this].
    /// </summary>
    public static class ItemPanelLayout
    {
        /// <summary>Height of the description box in the res layout.</summary>
        public const float DescriptionBoxHeight = 66;
        /// <summary>Description line height at 13 px (four lines fit the box, as in the original).</summary>
        public const float DescriptionLineHeight = 15;
        /// <summary>Space the table needs at least (two rows of 14 px text and a gap) when it moves down.</summary>
        public const float MinTableHeight = 40;

        /// <summary>
        /// How far the stats table moves down for a description of <paramref name="textHeight"/> pixels: 0 when it fits
        /// the box, else the overflow rounded up, but never so far that less than <see cref="MinTableHeight"/> of the
        /// <paramref name="tableHeight"/> is left.
        /// </summary>
        public static float TableShift(float textHeight, float tableHeight)
        {
            if (float.IsNaN(textHeight) || textHeight <= DescriptionBoxHeight) return 0;
            float shift = (float)Math.Ceiling(textHeight - DescriptionBoxHeight);
            return Math.Max(0, Math.Min(shift, tableHeight - MinTableHeight));
        }
    }
}
