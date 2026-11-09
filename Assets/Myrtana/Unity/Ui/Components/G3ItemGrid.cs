using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Myrtana.Ui.Skin;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// Tile view of a G3 list control (inventory 5×10, trade 5×8/5×7, loot 5×10, offer pools 6×3, quick slots 10×1) as
    /// GUI.dll draws it (<see cref="TileGrid"/>): 46 px cells at a 48 px pitch from the control's top-left corner, columns
    /// = (width − 17 px scroll bar) / 48, empty cells drawn (DrawEmptyItems). Cells are placed absolutely (no flex
    /// wrapping: at non-integer panel scales an exactly fitting row wrapped its last cell, leaving 4 columns of 5 and 9
    /// quick slots of 10). A scrollable grid shows the G3 scroll bar (<see cref="G3ScrollBar"/>) at its right edge at all
    /// times; it scrolls by whole rows (wheel: 3 rows per notch, arrows, track, thumb). Selection is one tile; double
    /// click / Enter activates. Tiles can be dragged when <see cref="DragEnabled"/> (<see cref="G3ItemDrag"/>); what a
    /// drop does and the right-button "move one" accelerator belong to the screen presenter (they talk to view models).
    /// Hovering a stack selects it (<see cref="HoverSelects"/>, fix-75 [owner]: in G3 the item panel shows the stack under
    /// the mouse and the digit keys put that stack on the quick bar; the RE found no LVN_HOTTRACK handler, HotTrack=false,
    /// so this rests on the owner's play of CP 1.75): the panel follows the selection, which the mouse or the keys move,
    /// whichever acted last; leaving the grid keeps the last selection. No delay.
    /// </summary>
    public class G3ItemGrid : VisualElement
    {
        public const int Pitch = TileGrid.Pitch;

        readonly VisualElement _viewport;
        readonly VisualElement _content;
        readonly G3ScrollBar _bar;
        readonly List<G3ItemTile> _tiles = new List<G3ItemTile>();
        int _selected = -1;
        int _firstRow;

        public int Columns { get; private set; }
        public int VisibleRows { get; private set; }
        /// <summary>First visible row (the scroll position).</summary>
        public int FirstRow => _firstRow;
        public G3ScrollBar ScrollBar => _bar;
        public string ThousandSuffix { get; set; } = "k";
        public string MillionSuffix { get; set; } = "m";

        /// <summary>Moving the mouse onto a stack selects it (inventory, trade, loot; not the quick bar).</summary>
        public bool HoverSelects { get; set; } = true;

        public event Action<int> SelectionChanged;
        public event Action<int> Activated;
        /// <summary>A tile of this grid was dragged and let go (over another grid, a cell of this one, or nothing).</summary>
        public event Action<ItemDrop> Dropped;
        /// <summary>Its tiles with items can be dragged.</summary>
        public bool DragEnabled { get; set; }
        string _dropRole;
        /// <summary>
        /// What kind of list this is for drops ("quickslots", "inventory", "trade.hero" …); null: drops on it are not
        /// reported as onto a grid.
        /// </summary>
        public string DropRole
        {
            get => _dropRole;
            set
            {
                _dropRole = value;
                if (value != null && panel != null) G3ItemDrag.Register(this);
                else if (value == null) G3ItemDrag.Unregister(this);
            }
        }

        internal void RaiseDropped(ItemDrop drop) => Dropped?.Invoke(drop);

        /// <summary>A drop at <paramref name="screen"/> landed on one of <paramref name="page"/>'s displayed views (fix-85).</summary>
        public static bool OverViews(VisualElement page, Vector2 screen) => G3ItemDrag.OverViews(page, screen);

        /// <summary>The screen point lies on any shown UI document's pickable element (<see cref="G3ItemDrag.OverUi"/>).</summary>
        public static bool OverUi(Vector2 screen) => G3ItemDrag.OverUi(screen, out _);

        /// <summary>As <see cref="OverUi(Vector2)"/>, naming the element found (dev runner log).</summary>
        public static bool OverUi(Vector2 screen, out string what) => G3ItemDrag.OverUi(screen, out what);

        /// <summary>The drop grid and cell under a screen point, as a release finds them.</summary>
        public static (G3ItemGrid grid, int index) TargetAt(Vector2 screen) => G3ItemDrag.TargetAt(screen);
        public IReadOnlyList<G3ItemTile> Tiles => _tiles;

        /// <param name="width">Control width (reference px); a 17 px scroll bar is reserved when <paramref name="scrollable"/>.</param>
        public G3ItemGrid(int width, int height, bool scrollable = true, int scrollBar = G3ScrollRules.Width)
        {
            AddToClassList("g3-grid");
            style.width = width;
            style.height = height;
            int bar = scrollable ? scrollBar : 0;
            Columns = TileGrid.Columns(width, bar);
            VisibleRows = TileGrid.Rows(height);
            _viewport = new VisualElement { pickingMode = PickingMode.Ignore };
            _viewport.AddToClassList("g3-grid__viewport");
            _viewport.style.position = Position.Absolute;
            _viewport.style.left = 0;
            _viewport.style.top = 0;
            _viewport.style.width = width - bar;
            _viewport.style.height = height;
            _viewport.style.overflow = Overflow.Hidden;
            Add(_viewport);
            _content = new VisualElement { pickingMode = PickingMode.Ignore };
            _content.AddToClassList("g3-grid__content");
            _content.style.position = Position.Absolute;
            _content.style.left = 0;
            _content.style.top = 0;
            _content.style.width = Columns * Pitch;
            _viewport.Add(_content);
            if (scrollable)
            {
                _bar = new G3ScrollBar(height);
                _bar.Scrolled += row => ShowRow(row);
                Add(_bar);
                RegisterCallback<WheelEvent>(OnWheel);
            }
            focusable = true;
            RegisterCallback<KeyDownEvent>(OnKey);
            RegisterCallback<AttachToPanelEvent>(_ => { if (_dropRole != null) G3ItemDrag.Register(this); });
            RegisterCallback<DetachFromPanelEvent>(_ => G3ItemDrag.Unregister(this));
        }

        /// <summary>Shows the stacks; cells beyond them up to the visible area stay empty but drawn.</summary>
        public void SetItems(IReadOnlyList<ItemTileModel> items)
        {
            int cells = Math.Max(items.Count, Columns * VisibleRows);
            cells = (cells + Columns - 1) / Columns * Columns;
            while (_tiles.Count < cells)
            {
                var tile = new G3ItemTile { Index = _tiles.Count };
                int index = tile.Index;
                var (x, y) = TileGrid.CellOrigin(index, Columns);
                tile.style.position = Position.Absolute;
                tile.style.left = x;
                tile.style.top = y;
                tile.RegisterCallback<ClickEvent>(e =>
                {
                    if (index >= Count) return;
                    Select(index);
                    if (e.clickCount >= 2) Activated?.Invoke(index);
                });
                // PointerMove, not PointerEnter: a re-render under a still mouse must not take the keys' selection away
                tile.RegisterCallback<PointerMoveEvent>(_ =>
                {
                    if (HoverSelects && index < Count && index != _selected && !G3ItemDrag.Dragging) Select(index);
                });
                G3ItemDrag.Attach(this, tile);
                _tiles.Add(tile);
                _content.Add(tile);
            }
            for (int i = 0; i < _tiles.Count; i++)
            {
                _tiles[i].style.display = i < cells ? DisplayStyle.Flex : DisplayStyle.None;
                _tiles[i].Bind(i < items.Count ? items[i] : null, ThousandSuffix, MillionSuffix);
            }
            Count = items.Count;
            _content.style.height = cells / Columns * Pitch;
            if (_selected >= Count) Select(-1);
            ShowRow(_firstRow);
        }

        /// <summary>Rows the items fill: the scroll range (empty cells drawn below them do not count, as in G3).</summary>
        public int ItemRows => TileGrid.ItemRows(Count, Columns);

        /// <summary>Scrolls so that <paramref name="row"/> is the first visible row (clamped).</summary>
        public void ShowRow(int row)
        {
            _firstRow = _bar != null ? G3ScrollRules.Clamp(row, ItemRows, VisibleRows) : 0;
            _content.style.top = -_firstRow * Pitch;
            _bar?.Set(ItemRows, VisibleRows, _firstRow);
        }

        void OnWheel(WheelEvent e)
        {
            if (e.delta.y == 0) return;
            ShowRow(_firstRow + (e.delta.y > 0 ? G3ScrollRules.WheelRows : -G3ScrollRules.WheelRows));
            e.StopPropagation();
        }

        public int Count { get; private set; }

        public int SelectedIndex { get => _selected; set => Select(value); }

        public void Select(int index, bool notify = true)
        {
            index = Mathf.Clamp(index, -1, Count - 1);
            if (index == _selected) return;
            if (_selected >= 0) _tiles[_selected].Selected = false;
            _selected = index;
            if (_selected >= 0)
            {
                _tiles[_selected].Selected = true;
                ScrollToSelected();
            }
            if (notify) SelectionChanged?.Invoke(_selected);
        }

        void ScrollToSelected()
        {
            if (_selected >= 0) ShowRow(G3ScrollRules.Reveal(_firstRow, _selected / Columns, ItemRows, VisibleRows));
        }

        void OnKey(KeyDownEvent e)
        {
            int s = Math.Max(_selected, 0);
            switch (e.keyCode)
            {
                case KeyCode.LeftArrow: Select(s - 1); break;
                case KeyCode.RightArrow: Select(s + 1); break;
                case KeyCode.UpArrow: Select(s - Columns >= 0 ? s - Columns : s); break;
                case KeyCode.DownArrow: Select(s + Columns < Count ? s + Columns : s); break;
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
