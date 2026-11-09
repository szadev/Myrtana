using System;
using System.Collections.Generic;
using System.Globalization;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>One tab: theme <c>tabctrl_hor_tab_center</c> / <c>tabctrl_ver_tab_center</c> with an icon or a text.</summary>
    public class G3Tab : G3Part
    {
        public G3Image Icon { get; }
        public Label Label { get; }
        public int Index { get; set; }

        public G3Tab(bool vertical, string iconImage = null, string text = null)
            : base(vertical ? "tabctrl_ver_tab_center" : "tabctrl_hor_tab_center")
        {
            AddToClassList("g3-tab");
            if (!string.IsNullOrEmpty(iconImage))
            {
                Icon = new G3Image(iconImage);
                Icon.AddToClassList("g3-tab__icon");
                Add(Icon);
            }
            if (!string.IsNullOrEmpty(text))
            {
                Label = new Label(text) { pickingMode = PickingMode.Ignore };
                Label.AddToClassList("g3-tab__label");
                Add(Label);
                G3Text.Font(Label, 16, bold: true);
            }
        }
    }

    /// <summary>
    /// G3 tab control (inventory categories 5×48×26 with 16² icons; page sidebar 46×46 tabs with 32² icons and an
    /// 18 px gap; quest status tabs 138×44 text only). Sizes come from the layout (<see cref="FromLayout"/>).
    /// </summary>
    public class G3TabBar : VisualElement
    {
        readonly List<G3Tab> _tabs = new List<G3Tab>();
        int _selected = -1;

        public bool Vertical { get; }
        /// <summary>
        /// Window style bit 0x2: the tabs run from the right (bottom) edge, index 0 first (<see cref="TabCtrlStyle"/>). The
        /// inventory, loot, trade and documents tab controls have it; the inventory inserts its categories Misc..Weapons,
        /// so Weapons ends up leftmost (fix-85).
        /// </summary>
        public bool Reversed { get; }
        public int ItemWidth { get; set; } = 48;
        public int ItemHeight { get; set; } = 26;
        public int Spacing { get; set; } = 2;
        public int IconSize { get; set; } = 16;

        public event Action<int> SelectionChanged;
        /// <summary>A click on the tab that is already selected (no selection change; the inventory tabs cycle the sort, fix-92).</summary>
        public event Action<int> Reselected;
        public IReadOnlyList<G3Tab> Tabs => _tabs;

        public G3TabBar(bool vertical, bool reversed = false)
        {
            Vertical = vertical;
            Reversed = reversed;
            AddToClassList("g3-tabbar");
            style.flexDirection = vertical
                ? reversed ? FlexDirection.ColumnReverse : FlexDirection.Column
                : reversed ? FlexDirection.RowReverse : FlexDirection.Row;
        }

        /// <summary>
        /// Tab bar sized like a TabCtrl window of the layout (TabCtrl.ItemSize/ItemSpacing/IconSize; vertical when style bit
        /// 0x80 is set [inferred]; laid out from the far edge when style bit 0x2 is set, <see cref="Reversed"/>).
        /// </summary>
        public static G3TabBar FromLayout(WindowData w)
        {
            var bar = new G3TabBar(TabCtrlStyle.IsVertical(w.Style), TabCtrlStyle.IsReversed(w.Style));
            if (TryPair(w.Attribute("TabCtrl.ItemSize"), out int iw, out int ih)) { bar.ItemWidth = iw; bar.ItemHeight = ih; }
            if (TryPair(w.Attribute("TabCtrl.ItemSpacing"), out int sx, out int sy)) bar.Spacing = bar.Vertical ? sy : sx;
            if (TryPair(w.Attribute("TabCtrl.IconSize"), out int isz, out _)) bar.IconSize = isz;
            return bar;
        }

        /// <summary>The index of the tab <paramref name="steps"/> places to the right (down) of the selected one on screen, wrapping.</summary>
        public int VisualStep(int steps) => TabCtrlStyle.Step(_selected, _tabs.Count, steps, Reversed);

        public G3Tab AddTab(string iconImage = null, string text = null)
        {
            var tab = new G3Tab(Vertical, iconImage, text) { Index = _tabs.Count };
            tab.style.width = ItemWidth;
            tab.style.height = ItemHeight;
            if (_tabs.Count > 0)
            {
                if (Vertical) { if (Reversed) tab.style.marginBottom = Spacing; else tab.style.marginTop = Spacing; }
                else if (Reversed) tab.style.marginRight = Spacing;
                else tab.style.marginLeft = Spacing;
            }
            if (tab.Icon != null) { tab.Icon.style.width = IconSize; tab.Icon.style.height = IconSize; }
            int index = tab.Index;
            tab.RegisterCallback<ClickEvent>(_ =>
            {
                if (index == _selected) Reselected?.Invoke(index);
                else Select(index);
            });
            _tabs.Add(tab);
            Add(tab);
            return tab;
        }

        public int SelectedIndex { get => _selected; set => Select(value); }

        public void Select(int index, bool notify = true)
        {
            if (index == _selected || index < -1 || index >= _tabs.Count) return;
            if (_selected >= 0) _tabs[_selected].Selected = false;
            _selected = index;
            if (_selected >= 0) _tabs[_selected].Selected = true;
            if (notify) SelectionChanged?.Invoke(_selected);
        }

        static bool TryPair(string s, out int a, out int b)
        {
            a = b = 0;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split(',');
            return p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a)
                   && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b);
        }
    }
}
