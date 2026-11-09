using System;
using System.Collections.Generic;
using Myrtana.Ui.Dialog;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// Vertical G3 text list (details view, full-row select) with the theme scroll bar: dialog choices, quest titles,
    /// save slots. Behaviour switches cover the original variants: the dialog list selects on hover
    /// (<see cref="HoverSelects"/>) and activates with one click; Up/Down/Enter move and activate (no wrap-around).
    /// Screen-specific keys (dialog Esc → End choice) stay in the presenter. A click activates only the row it was
    /// pressed and released on, pressed after the rows appeared (<see cref="ChoiceClickGate"/>): a click beside the
    /// rows, or the line-skip click whose press lands on rows created in the same frame, never runs the highlight.
    /// </summary>
    public class G3ListView : VisualElement
    {
        /// <summary>Row height in reference pixels: one line of the 16 px row font (GDI tmHeight, ≈ 35 px).</summary>
        public static float DefaultRowHeight => G3Text.LineHeight(G3ListRow.FontSize);

        readonly ScrollView _scroll;
        readonly List<G3ListRow> _rows = new List<G3ListRow>();
        readonly ChoiceClickGate _clicks = new ChoiceClickGate();
        int _selected = -1;
        bool _laidOut;
        int _firstRow, _visibleRows;
        bool _snapping, _holdScroll;

        public bool HoverSelects { get; set; }
        public bool ActivateOnSingleClick { get; set; } = true;
        public float RowHeight { get; set; } = DefaultRowHeight;

        /// <summary>Gap below each row (G3 icon-view lists: 46 px items at the 48 px <c>LargeIcon.Spacing</c> pitch).</summary>
        public float RowSpacing { get; set; }

        /// <summary>
        /// Whole rows only (fix-125, station lists): the viewport is cut to a whole number of row pitches and the list
        /// scrolls by rows as GUI.dll's list scroll bar (units are rows; wheel 3 rows a notch = SPI_GETWHEELSCROLLLINES,
        /// arrows and thumb snap to a row), so no row is ever half visible.
        /// </summary>
        public bool SnapToRows { get; set; }

        float Pitch => RowHeight + RowSpacing;

        const int WheelRows = 3;

        /// <summary>Rows fully shown in a <see cref="SnapToRows"/> list (0 before the first layout).</summary>
        public int VisibleRows => _visibleRows;

        /// <summary>Index of the top row of a <see cref="SnapToRows"/> list.</summary>
        public int FirstRow => _firstRow;

        /// <summary>Selection moved (index or −1).</summary>
        public event Action<int> SelectionChanged;
        /// <summary>A row was chosen (click / double click / Enter).</summary>
        public event Action<int> Activated;

        public IReadOnlyList<G3ListRow> Rows => _rows;
        public ScrollView ScrollView => _scroll;

        public G3ListView()
        {
            AddToClassList("g3-list");
            focusable = true;
            _scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto,
            };
            _scroll.AddToClassList("g3-list__scroll");
            Add(_scroll);
            _scroll.RegisterCallback<GeometryChangedEvent>(OnFirstLayout);
            RegisterCallback<KeyDownEvent>(OnKey);
            RegisterCallback<GeometryChangedEvent>(OnListGeometry);
            _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => ApplyFirstRow());
            _scroll.verticalScroller.valueChanged += OnScrolled;
            RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
        }

        /// <summary>
        /// The list's res style has WS_VSCROLL: GUI.dll shows its vertical bar at all times (ShowScrollBar in the list
        /// layout); the thumb only appears once the rows overflow (G3Skin.uss hides the dragger of a disabled scroller).
        /// Lists without the style (the dialog choices) keep the bar only while needed.
        /// </summary>
        public bool AlwaysShowScrollBar
        {
            get => _scroll.verticalScrollerVisibility == ScrollerVisibility.AlwaysVisible;
            set => _scroll.verticalScrollerVisibility = value ? ScrollerVisibility.AlwaysVisible : ScrollerVisibility.Auto;
        }

        public void SetItems(IReadOnlyList<string> texts)
        {
            int keepFirst = _firstRow;
            _snapping = true; // the emptied content resets the scroller: keep the top row for the new rows
            _scroll.Clear();
            _rows.Clear();
            for (int i = 0; i < texts.Count; i++)
            {
                var row = new G3ListRow(texts[i]) { Index = i };
                row.style.minHeight = RowHeight;
                if (SnapToRows) PlaceRow(row, i);
                else if (RowSpacing > 0) row.style.marginBottom = RowSpacing;
                int index = i;
                row.RegisterCallback<PointerEnterEvent>(_ => { if (HoverSelects) Select(index); });
                row.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) _clicks.Press(index, Time.frameCount); });
                row.RegisterCallback<ClickEvent>(e =>
                {
                    if (!_clicks.Release(index)) return;
                    Select(index);
                    if (ActivateOnSingleClick || e.clickCount >= 2) Activated?.Invoke(index);
                });
                _rows.Add(row);
                _scroll.Add(row);
            }
            // fix-154: the content is as tall as the rows' pitches plus one row of slack below the last row (the ScrollView
            // clamps its offset to the content; the list's own clamp in ApplyFirstRow keeps the last page full)
            if (SnapToRows) _scroll.contentContainer.style.height = (texts.Count + 1) * Pitch;
            _snapping = false;
            _firstRow = keepFirst;
            _selected = -1;
            _clicks.Shown(Time.frameCount);
            _holdScroll = true; // the default first-row selection must not move a kept scroll position
            if (_rows.Count > 0) Select(0, notify: false);
            _holdScroll = false;
            if (SnapToRows) _scroll.schedule.Execute(ApplyFirstRow);
        }

        /// <summary>
        /// fix-154: a <see cref="SnapToRows"/> row sits at its own pitch (top = index × pitch). Stacked rows drifted: layout
        /// snaps the 2 px gap to whole device pixels (scale 1080/768: 2.8125 → 3 px = 2.13), so row n stood n × 0.13 px
        /// lower than n × pitch (row 35: 1684.6 for an offset of 1680) and the last page showed a sliver of the row above
        /// and cut its bottom row. Placed rows round each edge on its own: no drift, the offset n × pitch always hits row n.
        /// </summary>
        void PlaceRow(G3ListRow row, int index)
        {
            row.style.position = Position.Absolute;
            row.style.left = 0;
            row.style.right = 0;
            row.style.top = index * Pitch;
            row.style.height = RowHeight;
            row.style.marginBottom = 0;
        }

        public int SelectedIndex
        {
            get => _selected;
            set => Select(value);
        }

        public void Select(int index, bool notify = true)
        {
            index = _rows.Count == 0 ? -1 : Mathf.Clamp(index, -1, _rows.Count - 1);
            if (index == _selected) return;
            if (_selected >= 0 && _selected < _rows.Count) _rows[_selected].Selected = false;
            _selected = index;
            if (_selected >= 0)
            {
                _rows[_selected].Selected = true;
                ScrollToSelected();
            }
            if (notify) SelectionChanged?.Invoke(_selected);
        }

        void OnFirstLayout(GeometryChangedEvent e)
        {
            _scroll.UnregisterCallback<GeometryChangedEvent>(OnFirstLayout);
            _laidOut = true;
            ScrollToSelected();
        }

        /// <summary>ScrollView.ScrollTo throws before the first layout; a selection made earlier is scrolled to then.</summary>
        void ScrollToSelected()
        {
            if (_holdScroll || _selected < 0) return;
            if (SnapToRows)
            {
                if (_selected < _firstRow) _firstRow = _selected;
                else if (_visibleRows > 0 && _selected >= _firstRow + _visibleRows) _firstRow = _selected - _visibleRows + 1;
                ApplyFirstRow();
                return;
            }
            if (_laidOut && _scroll.panel != null) _scroll.ScrollTo(_rows[_selected]);
        }

        /// <summary>Cuts the viewport to whole rows (the slot's remainder stays empty below).</summary>
        void OnListGeometry(GeometryChangedEvent e)
        {
            if (!SnapToRows) return;
            float height = e.newRect.height;
            if (float.IsNaN(height) || height <= 0) return;
            int visible = Math.Max(1, Mathf.FloorToInt((height + 0.5f) / Pitch));
            if (visible == _visibleRows && _scroll.style.height.value.value == visible * Pitch) return;
            _visibleRows = visible;
            _scroll.style.flexGrow = 0;
            _scroll.style.height = visible * Pitch;
            _scroll.verticalPageSize = Pitch;
            ScrollToSelected();
        }

        /// <summary>The top row's offset, clamped so the last page is full.</summary>
        void ApplyFirstRow()
        {
            if (!SnapToRows || _visibleRows <= 0) return;
            _firstRow = Mathf.Clamp(_firstRow, 0, Math.Max(0, _rows.Count - _visibleRows));
            float y = _firstRow * Pitch;
            if (Mathf.Abs(_scroll.scrollOffset.y - y) < 0.01f) return;
            _snapping = true;
            _scroll.scrollOffset = new Vector2(0, y);
            _snapping = false;
        }

        /// <summary>The thumb or the scroll bar arrows moved: snap to the nearest row.</summary>
        void OnScrolled(float value)
        {
            if (!SnapToRows || _snapping || _visibleRows <= 0) return;
            _firstRow = Mathf.RoundToInt(value / Pitch);
            ApplyFirstRow();
        }

        void OnWheel(WheelEvent e)
        {
            if (!SnapToRows || _visibleRows <= 0 || Mathf.Approximately(e.delta.y, 0)) return;
            _firstRow += e.delta.y > 0 ? WheelRows : -WheelRows;
            ApplyFirstRow();
            e.StopPropagation();
        }

        void OnKey(KeyDownEvent e)
        {
            switch (e.keyCode)
            {
                case KeyCode.UpArrow: Select(Math.Max(0, _selected - 1)); break;
                case KeyCode.DownArrow: Select(Math.Min(_rows.Count - 1, _selected + 1)); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_selected >= 0) Activated?.Invoke(_selected);
                    break;
                default: return;
            }
            e.StopPropagation();
        }
    }
}
