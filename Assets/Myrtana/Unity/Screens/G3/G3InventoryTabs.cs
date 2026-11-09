using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The five category tabs of the hero's inventory (inventory, loot and trade pages) with their 16² category icons, in
    /// G3's insertion order <see cref="InventoryTabOrder"/>; the TabCtrl's style draws index 0 rightmost, so the player
    /// sees Weapons | Armour | Artefacts | Food | Misc and the page opens on Weapons, the leftmost tab (fix-85). A click on
    /// the selected tab cycles the owner's sort mode (<see cref="G3SortToggle"/>, fix-92), shown as a small theme arrow on
    /// that tab (none in G3 order).
    /// </summary>
    public sealed class G3InventoryTabs
    {
        static IReadOnlyList<(string icon, ItemPage[] pages)> Order => InventoryTabOrder.Tabs;
        const int WeaponsTab = InventoryTabOrder.DefaultTab;

        readonly G3TabBar _bar;
        VisualElement _mark;
        ItemSortMode _markMode;
        int _markTab = -1;

        public G3InventoryTabs(VisualElement slot)
        {
            _bar = slot?.userData is WindowData w ? G3TabBar.FromLayout(w) : new G3TabBar(false, reversed: true);
            foreach (var (icon, _) in Order) _bar.AddTab(icon);
            if (slot != null)
            {
                slot.pickingMode = PickingMode.Position;
                slot.Add(_bar);
            }
            _bar.Select(WeaponsTab, notify: false);
            _bar.SelectionChanged += _ =>
            {
                ShowSort();
                Changed?.Invoke();
            };
            _bar.Reselected += _ => CycleSort();
            ShowSort();
        }

        /// <summary>Another tab was selected (re-list).</summary>
        public event Action Changed;
        /// <summary>The sort mode changed (re-list, keep the selection).</summary>
        public event Action SortChanged;

        public void Reset()
        {
            _bar.Select(WeaponsTab, notify: false);
            ShowSort();
        }

        /// <summary>The next sort mode (a click on the selected tab, the inventory's O key).</summary>
        public void CycleSort()
        {
            G3SortToggle.Cycle();
            ShowSort();
            SortChanged?.Invoke();
        }

        /// <summary>The sort mark of the selected tab (dev runner): its theme part, or null in G3 order.</summary>
        public string SortMark => _mark == null ? null : ItemSorting.GlyphPart(_markMode);

        /// <summary>Clicks the selected tab as the mouse does (dev runner).</summary>
        public void ClickSelected()
        {
            var tab = _bar.SelectedIndex >= 0 ? _bar.Tabs[_bar.SelectedIndex] : null;
            if (tab == null) return;
            using (var e = ClickEvent.GetPooled())
            {
                e.target = tab;
                tab.SendEvent(e);
            }
        }

        /// <summary>Puts the mark of the current mode on the selected tab (pages call it from Render: the mode is shared).</summary>
        public void ShowSort()
        {
            var mode = G3SortToggle.Mode;
            int tab = _bar.SelectedIndex;
            if (_markTab == tab && _markMode == mode && (_mark != null) == (ItemSorting.GlyphPart(mode) != null)) return;
            _mark?.RemoveFromHierarchy();
            _mark = tab >= 0 ? G3SortToggle.Glyph(mode) : null;
            if (_mark != null) _bar.Tabs[tab].Add(_mark);
            _markTab = tab;
            _markMode = mode;
        }

        /// <summary>The tab <paramref name="delta"/> places to the right (+) or left (−) on screen (keyboard).</summary>
        public void Move(int delta) => _bar.Select(_bar.VisualStep(delta));

        /// <summary>The title of the selected tab (HL_INV_*).</summary>
        public string Title(GameSession session) => ItemLists.PageTitle(session, Order[_bar.SelectedIndex].pages[0]);

        /// <summary>The rows of the selected tab.</summary>
        public IReadOnlyList<ItemRow> Filter(IReadOnlyList<ItemGroup> groups)
        {
            var pages = Order[_bar.SelectedIndex].pages;
            return groups.Where(g => pages.Contains(g.Page)).SelectMany(g => g.Rows).ToList();
        }
    }
}
