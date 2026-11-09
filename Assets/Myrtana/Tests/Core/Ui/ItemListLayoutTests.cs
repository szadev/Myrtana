using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-40: tile lists, their scroll bar and the page sidebar against the original layouts (res.txt rects of
    /// IDD_VIEW_LOOT_POOL_2, IDD_VIEW_LOOT_INVENTORY_2, IDD_PAGE_INVENTORY_2, IDD_VIEW_BAR_PAGES_2, IDD_VIEW_BAR_QUICKSLOTS_2)
    /// and the tile layout and scroll bar maths of GUI.dll (FUN_1002bf30, FUN_1001c290).
    /// </summary>
    public class ItemListLayoutTests
    {
        const int Bar = G3ScrollRules.Width;

        [TestCase(260, 480, 5, 10, TestName = "LootPool_26_88_286_568")]
        [TestCase(258, 482, 5, 10, TestName = "LootInventory_28_88_286_570")]
        [TestCase(260, 384, 5, 8, TestName = "Vendor")]
        [TestCase(260, 336, 5, 7, TestName = "PlayerTrade")]
        [TestCase(306, 144, 6, 3, TestName = "OfferPool_64_10_370_154")]
        public void TileLists_ColumnsAndRows(int w, int h, int columns, int rows)
        {
            Assert.That(TileGrid.Columns(w, Bar), Is.EqualTo(columns));
            Assert.That(TileGrid.Rows(h), Is.EqualTo(rows));
            // the cells end before the scroll bar, which sits at the control's right edge (a few px gap, not a column)
            int cellsRight = (columns - 1) * TileGrid.Pitch + TileGrid.Cell;
            Assert.That(cellsRight, Is.LessThanOrEqualTo(w - Bar));
            Assert.That(w - Bar - cellsRight, Is.LessThan(TileGrid.Pitch), "no empty column between grid and scroll bar");
        }

        [Test]
        public void ScrollBar_FromTheListStyle()
        {
            Assert.That(TileGrid.HasVerticalScrollBar(0x10200404), Is.True, "inventory, loot, trade lists");
            Assert.That(TileGrid.HasVerticalScrollBar(268436484), Is.False, "quick slots");
        }

        [Test]
        public void LootPool_GridAndBarGeometry()
        {
            // 260 px list: cells x 0..238 (5 x 48 - 2), bar 243..260, so 5 px from the last cell to the bar
            Assert.That(TileGrid.CellOrigin(4, 5), Is.EqualTo((192, 0)));
            Assert.That(TileGrid.CellOrigin(5, 5), Is.EqualTo((0, 48)));
            Assert.That(260 - Bar - (192 + TileGrid.Cell), Is.EqualTo(5));
        }

        [Test]
        public void QuickBar_HasTenCellsInOneRow()
        {
            // IDC_LISTCTRL_QUICKSLOTS_2 22,5-502,53 (480 px, no scroll bar, spacing 48,46): slots 1..9 and 0
            Assert.That(TileGrid.Columns(480, 0), Is.EqualTo(10));
            Assert.That(TileGrid.Rows(48, 46), Is.EqualTo(1));
            var (x, y) = TileGrid.CellOrigin(9, 10);
            Assert.That((x, y), Is.EqualTo((432, 0)));
            Assert.That(x + TileGrid.Cell, Is.LessThanOrEqualTo(480), "the 10th cell fits, no wrap");
        }

        [Test]
        public void ItemRows_CountOnlyFilledRows()
        {
            Assert.That(TileGrid.ItemRows(0, 5), Is.EqualTo(0));
            Assert.That(TileGrid.ItemRows(2, 5), Is.EqualTo(1));
            Assert.That(TileGrid.ItemRows(50, 5), Is.EqualTo(10));
            Assert.That(TileGrid.ItemRows(51, 5), Is.EqualTo(11));
        }

        [Test]
        public void Thumb_HiddenWhileEverythingFits()
        {
            // two stacks in the loot pool: 1 row of 10 visible, no thumb (it filled the whole track before fix-40)
            Assert.That(G3ScrollRules.Thumb(480, TileGrid.ItemRows(2, 5), 10, 0).Visible, Is.False);
            Assert.That(G3ScrollRules.Thumb(480, 10, 10, 0).Visible, Is.False, "exactly one page");
            Assert.That(G3ScrollRules.Thumb(480, 0, 10, 0).Visible, Is.False, "empty list");
        }

        [Test]
        public void Thumb_SizeAndPlaceFollowGuiDll()
        {
            // 480 px bar: track 480 - 2 x 17 = 446; 12 rows, page 10: 446 x 10 / 12 = 371
            Assert.That(G3ScrollRules.Track(480), Is.EqualTo(446));
            var top = G3ScrollRules.Thumb(480, 12, 10, 0);
            Assert.That((top.Visible, top.Offset, top.Length), Is.EqualTo((true, 17, 371)));
            var bottom = G3ScrollRules.Thumb(480, 12, 10, 2);
            Assert.That(bottom.Offset + bottom.Length, Is.EqualTo(480 - Bar), "last position: thumb meets the down arrow");
            var half = G3ScrollRules.Thumb(480, 30, 10, 10);
            Assert.That((half.Offset, half.Length), Is.EqualTo((17 + (446 - 148) * 10 / 20, 148)));
        }

        [Test]
        public void Thumb_HasAMinimumOf8Px()
        {
            Assert.That(G3ScrollRules.Thumb(480, 1000, 10, 0).Length, Is.EqualTo(G3ScrollRules.MinThumb));
        }

        [Test]
        public void Position_ClampsAndRoundTripsThroughTheThumb()
        {
            Assert.That(G3ScrollRules.Clamp(-3, 12, 10), Is.EqualTo(0));
            Assert.That(G3ScrollRules.Clamp(9, 12, 10), Is.EqualTo(2));
            Assert.That(G3ScrollRules.Clamp(4, 1, 10), Is.EqualTo(0), "nothing to scroll");
            for (int pos = 0; pos <= 20; pos++)
            {
                var t = G3ScrollRules.Thumb(480, 30, 10, pos);
                Assert.That(G3ScrollRules.PositionAt(480, 30, 10, t.Offset), Is.EqualTo(pos), "pos " + pos);
            }
        }

        [Test]
        public void Reveal_ScrollsTheLeastToShowARow()
        {
            Assert.That(G3ScrollRules.Reveal(0, 3, 20, 10), Is.EqualTo(0));
            Assert.That(G3ScrollRules.Reveal(0, 12, 20, 10), Is.EqualTo(3));
            Assert.That(G3ScrollRules.Reveal(8, 2, 20, 10), Is.EqualTo(2));
            Assert.That(G3ScrollRules.Reveal(5, 19, 20, 10), Is.EqualTo(10));
        }

        [Test]
        public void Sidebar_OnlyOnSheets()
        {
            Assert.That(HudVisibility.Of(HudPage.Sheet) & HudParts.Sidebar, Is.EqualTo(HudParts.Sidebar));
            Assert.That(HudVisibility.Of(HudPage.Exclusive), Is.EqualTo(HudParts.None), "trade, loot, crafting");
            Assert.That(HudVisibility.Of(HudPage.Fry) & HudParts.Sidebar, Is.EqualTo(HudParts.None));
            Assert.That(HudVisibility.Of(HudPage.Fry), Is.EqualTo(HudParts.FocusBar | HudParts.Vitals | HudParts.QuickSlots | HudParts.Compass));
        }

        // panel widths in reference px: 4:3, 16:10, 16:9, 21:9 (2560x1080); 5:4 is width-bound and stays 1024
        [TestCase(1024)]
        [TestCase(1229)]
        [TestCase(1365)]
        [TestCase(1820)]
        public void Sidebar_TouchesTheInventoryWithoutOverlap(int panelWidth)
        {
            // IDD_PAGE_INVENTORY_2 "Inventory" 634..946 (anchor 0), IDD_VIEW_BAR_PAGES_2 946..1008 (anchor 0): both centred
            var inv = GuiLayoutRules.Horizontal(634, 946, 1024, AnchorFlags.None).Resolve(panelWidth);
            var bar = GuiLayoutRules.Horizontal(946, 1008, 1024, AnchorFlags.None).Resolve(panelWidth);
            Assert.That(inv.start + inv.size, Is.EqualTo(bar.start).Within(0.01f));
            Assert.That(bar.start + bar.size, Is.LessThanOrEqualTo(panelWidth));
        }

        [TestCase(1024)]
        [TestCase(1365)]
        public void Sidebar_WouldCoverTheLootInventory(int panelWidth)
        {
            // why loot (page 7) hides it: IDD_VIEW_LOOT_INVENTORY_2 688..1000 reaches under the sidebar 946..1008
            var loot = GuiLayoutRules.Horizontal(688, 1000, 1024, AnchorFlags.None).Resolve(panelWidth);
            var bar = GuiLayoutRules.Horizontal(946, 1008, 1024, AnchorFlags.None).Resolve(panelWidth);
            Assert.That(loot.start + loot.size, Is.GreaterThan(bar.start));
        }

        [Test]
        public void LootTakeAll_ClosesAndNeedsStacks()
        {
            Assert.That(LootPageRules.TakeAllCloses, Is.True);
            Assert.That(LootPageRules.TakeAllEnabled(0), Is.False);
            Assert.That(LootPageRules.TakeAllEnabled(2), Is.True);
        }
    }
}
