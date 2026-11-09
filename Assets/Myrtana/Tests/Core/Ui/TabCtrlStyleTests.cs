using System.Linq;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-85: G3 TabCtrl style bit 0x2 lays the tabs out from the right edge; the inventory opens on Weapons, leftmost.</summary>
    public class TabCtrlStyleTests
    {
        [TestCase(0x10000502u, true, false)]  // inventory, loot, trade, documents
        [TestCase(0x10000500u, false, false)] // quest status
        [TestCase(0x10000580u, false, true)]  // page sidebar
        public void Style_Bits(uint style, bool reversed, bool vertical)
        {
            Assert.That(TabCtrlStyle.IsReversed(style), Is.EqualTo(reversed));
            Assert.That(TabCtrlStyle.IsVertical(style), Is.EqualTo(vertical));
        }

        [Test]
        public void InventoryTabs_ReadWeaponsFirst_AndOpenOnTheLeftmost()
        {
            var drawn = TabCtrlStyle.DrawOrder(InventoryTabOrder.Tabs.Count, TabCtrlStyle.IsReversed(0x10000502u));
            var pages = drawn.Select(i => InventoryTabOrder.Tabs[i].pages[0]).ToArray();
            Assert.That(pages, Is.EqualTo(new[] { ItemPage.Weapons, ItemPage.Armor, ItemPage.Artefacts, ItemPage.Alchemy, ItemPage.Misc }));
            Assert.That(drawn[0], Is.EqualTo(InventoryTabOrder.DefaultTab));
            Assert.That(InventoryTabOrder.Tabs[InventoryTabOrder.DefaultTab].icon, Is.EqualTo("G3_HUD_Icon_Category_Weapon.tga"));
        }

        [Test]
        public void InventoryTabs_FollowTheListGroupOrder()
        {
            // fix-75 lists the groups Weapon, Armour, Artefact, Alchemy, Misc, Written: the tabs read the same way.
            var drawn = TabCtrlStyle.DrawOrder(InventoryTabOrder.Tabs.Count, true).Select(i => InventoryTabOrder.Tabs[i].pages).ToArray();
            var flat = drawn.SelectMany(p => p).ToArray();
            Assert.That(flat, Is.EqualTo(new[] { ItemPage.Weapons, ItemPage.Armor, ItemPage.Artefacts, ItemPage.Alchemy, ItemPage.Misc, ItemPage.Books }));
        }

        [TestCase(4, 1, true, 3)]   // Weapons (index 4, leftmost) → right = Armour (3)
        [TestCase(0, 1, true, 4)]   // Misc (rightmost) → wraps to Weapons
        [TestCase(4, -1, true, 0)]  // Weapons ← wraps to Misc
        [TestCase(0, 1, false, 1)]
        [TestCase(-1, 1, false, 1)]
        public void Step_MovesOnScreen(int selected, int steps, bool reversed, int expected) =>
            Assert.That(TabCtrlStyle.Step(selected, 5, steps, reversed), Is.EqualTo(expected));
    }
}
