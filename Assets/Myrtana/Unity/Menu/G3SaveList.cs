using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The save/load list of the files page (fix-107 / fix-112 [owner], <see cref="SaveListLayout"/>): theme rows
    /// (<c>listctrl_item_details</c>: soft bar on hover, the red bar when selected, here with a gold outline) with the save's
    /// picture, the name and the date on the first line, game time and place on the second. It scrolls by whole rows
    /// with G3's own scroll bar (<see cref="G3ScrollBar"/>: arrows, track, thumb; wheel 3 rows per notch, as GUI.dll), so
    /// there is one bar and no UI Toolkit scroller. Items sit on lines (<see cref="SaveListGrid"/>): the Load page's
    /// quicksave and autosave share the first line, split by a vertical divider. One click selects, a double click
    /// activates; keys: Up/Down, Page Up/Down, Home/End move the selection by lines, Left/Right between the halves of the
    /// split line, Enter activates, Delete asks the page to delete.
    /// </summary>
    public sealed class G3SaveList : VisualElement, G3PadFocus.IComposite
    {
        readonly VisualElement _viewport, _content;
        readonly G3ScrollBar _bar;
        readonly List<G3ListRow> _rows = new List<G3ListRow>();
        readonly List<VisualElement> _dividers = new List<VisualElement>();
        SaveListGrid _grid = new SaveListGrid(new int[0]);
        int _count, _selected = -1, _first, _side;

        public int VisibleRows { get; }
        public int Count => _count;
        public int SelectedIndex => _selected;
        /// <summary>First visible line.</summary>
        public int FirstRow => _first;
        public SaveListGrid Grid => _grid;
        public G3ScrollBar ScrollBar => _bar;
        public IReadOnlyList<G3ListRow> Rows => _rows;

        /// <summary>Selection moved (index or −1).</summary>
        public event Action<int> SelectionChanged;
        /// <summary>A row was chosen (double click, Enter).</summary>
        public event Action<int> Activated;
        /// <summary>The Delete key on a selected row.</summary>
        public event Action<int> DeleteRequested;
        /// <summary>fix-222: the first visible line changed (the page loads the pictures of the rows now in view).</summary>
        public event Action<int> Scrolled;

        /// <summary>Keys are ignored while this returns true (a confirm box is open).</summary>
        public Func<bool> KeysBlocked { get; set; }

        public G3SaveList(float width = SaveListLayout.Width, float height = SaveListLayout.Height)
        {
            AddToClassList("g3-save-list");
            style.position = Position.Absolute;
            style.left = 0;
            style.top = 0;
            style.width = width;
            style.height = height;
            focusable = true;
            pickingMode = PickingMode.Position;
            VisibleRows = SaveListLayout.VisibleRows(height);

            _viewport = new VisualElement { pickingMode = PickingMode.Ignore };
            _viewport.style.position = Position.Absolute;
            _viewport.style.left = 0;
            _viewport.style.top = 0;
            _viewport.style.width = width - G3ScrollRules.Width;
            _viewport.style.height = height;
            _viewport.style.overflow = Overflow.Hidden;
            Add(_viewport);
            _content = new VisualElement { pickingMode = PickingMode.Ignore };
            _content.style.position = Position.Absolute;
            _content.style.left = 0;
            _content.style.top = 0;
            _content.style.width = width - G3ScrollRules.Width;
            _viewport.Add(_content);

            _bar = new G3ScrollBar(Mathf.RoundToInt(height));
            _bar.Scrolled += ShowRow;
            Add(_bar);

            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<KeyDownEvent>(OnKey);
            RegisterCallback<PointerDownEvent>(_ => Focus());
            G3PadFocus.MakeComposite(this, this);
        }

        /// <summary>Makes <paramref name="count"/> whole-line rows; see <see cref="SetLayout"/>.</summary>
        public void SetCount(int count) => SetLayout(Enumerable.Repeat(-1, count).ToArray());

        /// <summary>
        /// Makes one row per item (reusing the old ones) placed by <paramref name="columns"/> (-1 whole line, 0 left half,
        /// 1 right half; <see cref="SaveListGrid"/>); the page fills them with <see cref="Bind"/>.
        /// </summary>
        public void SetLayout(IReadOnlyList<int> columns)
        {
            int count = columns.Count;
            _grid = new SaveListGrid(columns);
            while (_rows.Count < count) _rows.Add(NewRow(_rows.Count));
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                row.style.display = i < count ? DisplayStyle.Flex : DisplayStyle.None;
                row.Selected = false;
                Outline(row, false);
                if (i >= count) continue;
                var cell = _grid[i];
                row.style.top = cell.Line * SaveListLayout.Pitch;
                row.style.left = SaveListLayout.CellLeft(cell.Column);
                row.style.width = SaveListLayout.CellWidth(cell.Column);
            }
            // a vertical divider in the middle of every split line
            int d = 0;
            for (int line = 0; line < _grid.LineCount; line++)
            {
                int item = _grid.At(line, 0) >= 0 ? _grid.At(line, 0) : _grid.At(line, 1);
                if (item < 0 || !_grid[item].IsHalf) continue;
                if (d == _dividers.Count) _dividers.Add(NewDivider());
                var div = _dividers[d++];
                div.style.display = DisplayStyle.Flex;
                div.style.top = line * SaveListLayout.Pitch + 4f;
            }
            for (; d < _dividers.Count; d++) _dividers[d].style.display = DisplayStyle.None;
            _count = count;
            _content.style.height = Math.Max(0, _grid.LineCount * SaveListLayout.Pitch - SaveListLayout.RowSpacing);
            _selected = -1;
            _side = 0;
            ShowRow(_first);
        }

        VisualElement NewDivider()
        {
            var div = new VisualElement { name = "divider", pickingMode = PickingMode.Ignore };
            div.AddToClassList("g3-save-divider");
            div.style.position = Position.Absolute;
            div.style.left = SaveListLayout.HalfWidth;
            div.style.width = SaveListLayout.DividerWidth;
            div.style.height = SaveListLayout.RowHeight - 8f;
            var c = G3Palette.Gold.ToColor();
            c.a = 0.55f;
            div.style.backgroundColor = c;
            _content.Add(div);
            return div;
        }

        /// <summary>Texts and picture of row <paramref name="index"/>; a null picture leaves an empty dark frame (none for the new slot).</summary>
        public void Bind(int index, string name, string detail, string date, Texture2D picture, bool showFrame)
        {
            var row = _rows[index];
            row.Text = name ?? "";
            var detailLabel = row.Q<Label>("detail");
            detailLabel.text = detail ?? "";
            var dateLabel = row.Q<Label>("date");
            dateLabel.text = date ?? "";
            bool half = index < _count && _grid[index].IsHalf;
            if (half)
            {
                // a half of the split line: name, date and game time / place on three lines
                row.Label.style.top = SaveListLayout.HalfNameTop;
                row.Label.style.right = SaveListLayout.DateRight;
                dateLabel.style.left = SaveListLayout.TextLeft;
                dateLabel.style.width = StyleKeyword.Auto;
                dateLabel.style.top = SaveListLayout.HalfDateTop;
                dateLabel.style.unityTextAlign = TextAnchor.UpperLeft;
                detailLabel.style.top = SaveListLayout.HalfDetailTop;
            }
            else
            {
                // a row without a second line (the new slot) has its name in the middle
                row.Label.style.top = string.IsNullOrEmpty(detail) ? (SaveListLayout.RowHeight - G3Text.LineHeight(SaveListLayout.NameSize)) / 2f : SaveListLayout.NameTop;
                row.Label.style.right = SaveListLayout.DateRight + SaveListLayout.DateWidth;
                dateLabel.style.left = StyleKeyword.Auto;
                dateLabel.style.width = SaveListLayout.DateWidth;
                dateLabel.style.top = SaveListLayout.NameTop + 2;
                dateLabel.style.unityTextAlign = TextAnchor.UpperRight;
                detailLabel.style.top = SaveListLayout.DetailTop;
            }
            SetPicture(index, picture, showFrame);
        }

        /// <summary>Only the picture of row <paramref name="index"/> (fix-222: it arrives after the row, read in the background).</summary>
        public void SetPicture(int index, Texture2D picture, bool showFrame)
        {
            if (index < 0 || index >= _rows.Count) return;
            var thumb = _rows[index].Q("thumb");
            thumb.style.backgroundImage = picture != null ? new StyleBackground(picture) : new StyleBackground(StyleKeyword.None);
            thumb.style.display = picture != null || showFrame ? DisplayStyle.Flex : DisplayStyle.None;
        }

        G3ListRow NewRow(int index)
        {
            var row = new G3ListRow { Index = index, pickingMode = PickingMode.Position };
            row.AddToClassList("g3-save-row");
            row.style.position = Position.Absolute;
            row.style.left = 0;
            row.style.top = index * SaveListLayout.Pitch;
            row.style.width = SaveListLayout.RowWidth;
            row.style.height = SaveListLayout.RowHeight;
            row.style.paddingLeft = row.style.paddingRight = 0;

            var thumb = new VisualElement { name = "thumb", pickingMode = PickingMode.Ignore };
            thumb.style.position = Position.Absolute;
            thumb.style.left = SaveListLayout.ThumbLeft;
            thumb.style.top = SaveListLayout.ThumbTop;
            thumb.style.width = thumb.style.height = SaveListLayout.ThumbSize;
            thumb.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f); // the frame of a save without a picture
            row.Add(thumb);

            var name = row.Label;
            name.style.position = Position.Absolute;
            name.style.left = SaveListLayout.TextLeft;
            name.style.top = SaveListLayout.NameTop;
            name.style.right = SaveListLayout.DateRight + SaveListLayout.DateWidth;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.style.unityTextAlign = TextAnchor.UpperLeft;
            G3Text.Font(name, SaveListLayout.NameSize, bold: true);

            var detail = new Label { name = "detail", pickingMode = PickingMode.Ignore };
            detail.style.position = Position.Absolute;
            detail.style.left = SaveListLayout.TextLeft;
            detail.style.top = SaveListLayout.DetailTop;
            detail.style.right = SaveListLayout.DateRight;
            detail.style.whiteSpace = WhiteSpace.NoWrap;
            detail.style.overflow = Overflow.Hidden;
            detail.style.textOverflow = TextOverflow.Ellipsis;
            detail.style.unityTextAlign = TextAnchor.UpperLeft;
            G3Text.Font(detail, SaveListLayout.DetailSize, bold: true);
            row.Add(detail);

            var date = new Label { name = "date", pickingMode = PickingMode.Ignore };
            date.style.position = Position.Absolute;
            date.style.right = SaveListLayout.DateRight;
            date.style.top = SaveListLayout.NameTop + 2;
            date.style.width = SaveListLayout.DateWidth;
            date.style.unityTextAlign = TextAnchor.UpperRight;
            G3Text.Font(date, SaveListLayout.DateSize);
            date.style.color = G3Palette.Label.ToColor();
            row.Add(date);

            row.RegisterCallback<ClickEvent>(e =>
            {
                if (e.button != 0 || row.Index >= _count) return;
                Select(row.Index);
                if (e.clickCount >= 2) Activated?.Invoke(row.Index);
                e.StopPropagation();
            });
            _content.Add(row);
            return row;
        }

        /// <summary>The selected row's outline: a 1 px gold frame on top of the theme's red bar.</summary>
        static void Outline(VisualElement row, bool on)
        {
            float w = on ? 1f : 0f;
            row.style.borderTopWidth = row.style.borderBottomWidth = row.style.borderLeftWidth = row.style.borderRightWidth = w;
            var c = G3Palette.Gold.ToColor();
            row.style.borderTopColor = row.style.borderBottomColor = row.style.borderLeftColor = row.style.borderRightColor = c;
        }

        public void Select(int index, bool notify = true)
        {
            index = _count == 0 ? -1 : Mathf.Clamp(index, -1, _count - 1);
            if (index == _selected) return;
            if (_selected >= 0 && _selected < _rows.Count)
            {
                _rows[_selected].Selected = false;
                Outline(_rows[_selected], false);
            }
            _selected = index;
            if (_selected >= 0)
            {
                _rows[_selected].Selected = true;
                Outline(_rows[_selected], true);
                if (_grid[_selected].IsHalf) _side = _grid[_selected].Column;
                ShowRow(G3ScrollRules.Reveal(_first, _grid[_selected].Line, _grid.LineCount, VisibleRows));
            }
            if (notify) SelectionChanged?.Invoke(_selected);
        }

        /// <summary>Scrolls so that line <paramref name="row"/> is the first visible line (clamped).</summary>
        public void ShowRow(int row)
        {
            _first = G3ScrollRules.Clamp(row, _grid.LineCount, VisibleRows);
            _content.style.top = -_first * SaveListLayout.Pitch;
            _bar.Set(_grid.LineCount, VisibleRows, _first);
            Scrolled?.Invoke(_first);
        }

        void OnWheel(WheelEvent e)
        {
            if (e.delta.y == 0) return;
            ShowRow(_first + (e.delta.y > 0 ? G3ScrollRules.WheelRows : -G3ScrollRules.WheelRows));
            e.StopPropagation();
        }

        // gamepad-1: the pad focus moves the list's own selection first (G3PadFocus.IComposite)
        bool G3PadFocus.IComposite.Move(G3.Player.NavDir dir)
        {
            if (KeysBlocked != null && KeysBlocked()) return true;
            int before = _selected;
            switch (dir)
            {
                case G3.Player.NavDir.Up: Select(_grid.MoveLines(_selected, -1, _side)); break;
                case G3.Player.NavDir.Down: Select(_selected < 0 ? 0 : _grid.MoveLines(_selected, 1, _side)); break;
                case G3.Player.NavDir.Left: Select(_grid.MoveSide(_selected, -1)); break;
                case G3.Player.NavDir.Right: Select(_grid.MoveSide(_selected, 1)); break;
            }
            return _selected != before;
        }

        void G3PadFocus.IComposite.Accept()
        {
            if (KeysBlocked != null && KeysBlocked()) return;
            if (_selected >= 0) Activated?.Invoke(_selected);
        }

        void G3PadFocus.IComposite.Tertiary()
        {
            if (KeysBlocked != null && KeysBlocked()) return;
            if (_selected >= 0) DeleteRequested?.Invoke(_selected);
        }

        void OnKey(KeyDownEvent e)
        {
            if (KeysBlocked != null && KeysBlocked()) return;
            int page = Math.Max(1, VisibleRows - 1);
            switch (e.keyCode)
            {
                case KeyCode.UpArrow: Select(_grid.MoveLines(_selected, -1, _side)); break;
                case KeyCode.DownArrow: Select(_selected < 0 ? 0 : _grid.MoveLines(_selected, 1, _side)); break;
                case KeyCode.PageUp: Select(_grid.MoveLines(_selected, -page, _side)); break;
                case KeyCode.PageDown: Select(_selected < 0 ? 0 : _grid.MoveLines(_selected, page, _side)); break;
                case KeyCode.LeftArrow: Select(_grid.MoveSide(_selected, -1)); break;
                case KeyCode.RightArrow: Select(_grid.MoveSide(_selected, 1)); break;
                case KeyCode.Home: Select(0); break;
                case KeyCode.End: Select(_count - 1); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_selected >= 0) Activated?.Invoke(_selected);
                    break;
                case KeyCode.Delete:
                    if (_selected >= 0) DeleteRequested?.Invoke(_selected);
                    break;
                default: return;
            }
            e.StopPropagation();
        }
    }
}
