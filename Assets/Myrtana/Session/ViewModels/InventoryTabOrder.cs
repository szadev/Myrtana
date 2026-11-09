using System.Collections.Generic;

namespace Myrtana.Session
{
    /// <summary>
    /// The five category tabs of the hero's inventory, loot and trade pages in G3's insertion order (gCHUDTabCtrlInventory,
    /// Game.dll 100a5840: InsertItem(i) with the category of table 102bb7cc = {4,3,2,1,0}) = Misc, Alchemy, Artefacts,
    /// Armour, Weapons, and SetCurSel(4) = Weapons (10082cf0). The control's style lays index 0 out at the right edge
    /// (<c>Myrtana.Ui.Skin.TabCtrlStyle</c>), so the player sees Weapons | Armour | Artefacts | Food | Misc with Weapons,
    /// the leftmost tab, open (fix-85). Books and letters are listed under Misc.
    /// </summary>
    public static class InventoryTabOrder
    {
        public static readonly IReadOnlyList<(string icon, ItemPage[] pages)> Tabs = new[]
        {
            ("G3_HUD_Icon_Category_Misc.tga", new[] { ItemPage.Misc, ItemPage.Books }),
            ("G3_HUD_Icon_Category_Food.tga", new[] { ItemPage.Alchemy }),
            ("G3_HUD_Icon_Category_Artefact.tga", new[] { ItemPage.Artefacts }),
            ("G3_HUD_Icon_Category_Armor.tga", new[] { ItemPage.Armor }),
            ("G3_HUD_Icon_Category_Weapon.tga", new[] { ItemPage.Weapons }),
        };

        /// <summary>The tab selected when the page opens (Weapons).</summary>
        public const int DefaultTab = 4;
    }
}
