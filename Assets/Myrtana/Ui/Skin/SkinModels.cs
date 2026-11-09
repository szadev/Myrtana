using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// What one item tile shows (inventory, trade, loot, quick slots). Screens map their view-model rows to it; the
    /// skin decides the look (Docs/formats-gui.md §6.5 tile overlays).
    /// </summary>
    public sealed class ItemTileModel
    {
        /// <summary><c>gCItem_PS.Texture</c> of the item ("IT_Apple.tga"); empty → category icon.</summary>
        public string Texture { get; set; }
        public ItemIconCategory Category { get; set; }
        public long Amount { get; set; } = 1;
        public ItemQualityFlags Quality { get; set; }
        /// <summary>Quick-slot index 0…9 or −1.</summary>
        public int QuickSlot { get; set; } = -1;
        public bool Equipped { get; set; }
        /// <summary>Spell books / skills: false shows the grey icon.</summary>
        public bool Learned { get; set; } = true;
        /// <summary>Trade tiles show only the count (no quality icon, no quick-slot digit).</summary>
        public bool CountOnly { get; set; }
    }

    /// <summary>One row of the item panel stats table.</summary>
    public sealed class ItemStatRow
    {
        public ItemStatKind Kind { get; set; }
        public string Label { get; set; }
        public string Value { get; set; }
        /// <summary>Optional 16×16 icon image (quality rows); the label then starts 18 px further right.</summary>
        public string Icon { get; set; }

        public ItemStatRow() { }
        public ItemStatRow(ItemStatKind kind, string label, string value, string icon = null)
        { Kind = kind; Label = label; Value = value; Icon = icon; }
    }

    /// <summary>Content of the item panel (IDD_VIEW_ITEM_2): follows the selected stack, it is not a hover tooltip.</summary>
    public sealed class ItemPanelModel
    {
        public string Name { get; set; }
        public string Texture { get; set; }
        public ItemIconCategory Category { get; set; }
        public bool Learned { get; set; } = true;
        public string Description { get; set; }
        /// <summary>Rows in display order (requirements, damage, qualities, protections, modifiers, mana, gold).</summary>
        public List<ItemStatRow> Rows { get; set; } = new List<ItemStatRow>();
    }
}
