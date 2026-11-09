using System;
using System.Collections.Generic;
using Myrtana.Session;
using Myrtana.Unity.Ui.Skin;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// A scrollable, grouped item list with one selected row (inventory, merchant stock): icon, name, amount, price and
    /// a mark for worn items. The selection survives redraws while the item is still listed.
    /// </summary>
    public sealed class ItemListView
    {
        readonly ScrollView _scroll;
        readonly List<ItemRow> _rows = new List<ItemRow>();
        readonly List<VisualElement> _elements = new List<VisualElement>();

        public ItemListView(ScrollView scroll)
        {
            _scroll = scroll;
        }

        /// <summary>A row was clicked (selection changed) or double-clicked (activated).</summary>
        public event Action<ItemRow> Selected, Activated;

        public ItemRow Current { get; private set; }

        public bool ShowPrices { get; set; }

        public void Show(IReadOnlyList<ItemGroup> groups, string emptyText)
        {
            string keep = Current?.Item;
            _scroll.Clear();
            _rows.Clear();
            _elements.Clear();
            foreach (var group in groups)
            {
                var title = new Label(group.Title);
                title.AddToClassList("screen-section-title");
                _scroll.Add(title);
                foreach (var row in group.Rows) AddRow(row);
            }
            if (_rows.Count == 0)
            {
                var empty = new Label(emptyText);
                empty.AddToClassList("screen-empty");
                _scroll.Add(empty);
            }
            int index = _rows.FindIndex(r => r.Item == keep);
            Select(index >= 0 ? index : 0);
        }

        /// <summary>The item's inventory icon from the imported G3 GUI atlases; null before the GUI import.</summary>
        public static UnityEngine.Sprite ItemIcon(string icon) => icon != null ? G3Skin.Load()?.Sprite(icon) : null;

        public void Move(int delta)
        {
            if (_rows.Count == 0) return;
            int index = _rows.IndexOf(Current);
            Select((index + delta + _rows.Count) % _rows.Count);
        }

        void AddRow(ItemRow row)
        {
            var line = new VisualElement();
            line.AddToClassList("item-row");
            line.EnableInClassList("item-row--worn", row.Worn);
            var icon = new Image { sprite = ItemIcon(row.Icon) };
            icon.AddToClassList("item-row-icon");
            line.Add(icon);
            var name = new Label(row.Amount > 1 ? $"{row.Name}  ×{row.Amount}" : row.Name);
            name.AddToClassList("item-row-name");
            line.Add(name);
            if (ShowPrices)
            {
                var price = new Label(row.Price.ToString());
                price.AddToClassList("item-row-price");
                line.Add(price);
            }
            int index = _rows.Count;
            line.RegisterCallback<ClickEvent>(e =>
            {
                Select(index);
                if (e.clickCount >= 2) Activated?.Invoke(row);
            });
            _scroll.Add(line);
            _rows.Add(row);
            _elements.Add(line);
        }

        void Select(int index)
        {
            Current = index >= 0 && index < _rows.Count ? _rows[index] : null;
            for (int i = 0; i < _elements.Count; i++) _elements[i].EnableInClassList("item-row--selected", i == index);
            if (Current != null) _scroll.ScrollTo(_elements[index]);
            Selected?.Invoke(Current);
        }
    }
}
