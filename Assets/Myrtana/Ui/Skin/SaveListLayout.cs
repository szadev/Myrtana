using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// The save/load list of <c>IDD_MENU_FILES_2</c> (<c>G3MenuFilesPage</c>). G3's list control
    /// <c>IDC_LISTCTRL_MENU_FILES_2</c> (337,89 – 889,641: 552 × 552) draws LargeIcon items of 530 × 106 at a 110 px pitch
    /// (the 128 × 128 save picture, three text lines), so only five saves show at once. fix-107 made ten 52 px rows;
    /// fix-112 [owner]: a bit bigger - seven 75 px rows at a 78 px pitch, a 66 px picture, the name 18 px bold, the date and
    /// the game time / place 15 px. On the Load page the quicksave and the autosave share the first line (<see cref="SaveListGrid"/>):
    /// the quicksave in the left half, the autosave in the right one, a vertical divider between them; each half has its
    /// own picture and three lines (name, date, game time and place). The control keeps its rect and G3's 17 px scroll
    /// bar (<see cref="G3ScrollRules"/>) at its right edge.
    /// </summary>
    public static class SaveListLayout
    {
        /// <summary>Size of <c>IDC_LISTCTRL_MENU_FILES_2</c> (res rect 337,89 – 889,641).</summary>
        public const float Width = 552f, Height = 552f;

        /// <summary>G3's own item size and pitch (ListCtrl.LargeIcon.Size 530,106 / Spacing 530,110).</summary>
        public const float G3RowHeight = 106f, G3Pitch = 110f;

        public const float RowHeight = 75f, RowSpacing = 3f, Pitch = RowHeight + RowSpacing;

        /// <summary>The save's picture (square, as G3's 128 × 128 image) at the row's left, centred vertically.</summary>
        public const float ThumbSize = 66f, ThumbLeft = 5f;
        public static float ThumbTop => (RowHeight - ThumbSize) / 2f;

        /// <summary>Left edge of the text lines (after the picture).</summary>
        public const float TextLeft = ThumbLeft + ThumbSize + 10f;

        /// <summary>A full row: the name line (18 px bold) with the date (15 px) at its right, the detail line (15 px) below.</summary>
        public const int NameSize = 18, DetailSize = 15, DateSize = 15;
        public const float NameTop = 12f, DetailTop = 40f, DateRight = 10f, DateWidth = 140f;

        /// <summary>A half row (quicksave / autosave): name, date and detail on three lines.</summary>
        public const float HalfNameTop = 4f, HalfDateTop = 28f, HalfDetailTop = 50f;

        /// <summary>The vertical divider between the two halves of the first line.</summary>
        public const float DividerWidth = 2f;

        /// <summary>Width of the rows: the control less G3's scroll bar.</summary>
        public static float RowWidth => Width - G3ScrollRules.Width;

        /// <summary>Width of one half of the split first line.</summary>
        public static float HalfWidth => (RowWidth - DividerWidth) / 2f;

        /// <summary>Left edge of a cell in column <paramref name="column"/> (-1 full row, 0 left half, 1 right half).</summary>
        public static float CellLeft(int column) => column == 1 ? HalfWidth + DividerWidth : 0f;

        public static float CellWidth(int column) => column < 0 ? RowWidth : HalfWidth;

        /// <summary>Whole rows that fit into a list of <paramref name="height"/> px.</summary>
        public static int VisibleRows(float height) => height <= 0 ? 0 : (int)((height + RowSpacing) / Pitch);

        /// <summary>Rows G3's layout showed in the same control (five).</summary>
        public static int G3VisibleRows => (int)((Height + G3Pitch - G3RowHeight) / G3Pitch);
    }

    /// <summary>Where an item of the save list sits: its line and column (-1 the whole line, 0 the left half, 1 the right half).</summary>
    public readonly struct SaveListCell
    {
        public readonly int Line, Column;
        public SaveListCell(int line, int column) { Line = line; Column = column; }
        public bool IsHalf => Column >= 0;
        public override string ToString() => $"{Line}:{Column}";
    }

    /// <summary>
    /// fix-112 [owner]: the list's items on lines. An item that wants the left half starts a line; one that wants the
    /// right half joins the line before it when that line holds only a left half, else it starts a line of its own (an
    /// autosave without a quicksave stays in the right half). Every other item takes a whole line. Up/Down move between
    /// lines (on a split line to the half of the same side, else the remembered side), Left/Right between the halves.
    /// </summary>
    public sealed class SaveListGrid
    {
        readonly SaveListCell[] _cells;
        readonly List<int[]> _lines = new List<int[]>(); // per line: item of column -1/0 at [0], column 1 at [1] (or -1)

        /// <param name="columns">Per item: -1 whole line, 0 left half, 1 right half.</param>
        public SaveListGrid(IReadOnlyList<int> columns)
        {
            _cells = new SaveListCell[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                int col = columns[i] < 0 ? -1 : columns[i] > 0 ? 1 : 0;
                if (col == 1 && i > 0 && _cells[i - 1].Column == 0 && _lines[_lines.Count - 1][1] < 0)
                {
                    _lines[_lines.Count - 1][1] = i;
                    _cells[i] = new SaveListCell(_lines.Count - 1, 1);
                    continue;
                }
                _lines.Add(col == 1 ? new[] { -1, i } : new[] { i, -1 });
                _cells[i] = new SaveListCell(_lines.Count - 1, col);
            }
        }

        public int Count => _cells.Length;
        public int LineCount => _lines.Count;
        public SaveListCell this[int item] => _cells[item];

        /// <summary>The item in <paramref name="column"/> of <paramref name="line"/> (a whole-line item for any column), or -1.</summary>
        public int At(int line, int column)
        {
            if (line < 0 || line >= _lines.Count) return -1;
            var l = _lines[line];
            if (l[0] >= 0 && _cells[l[0]].Column < 0) return l[0];
            return column == 1 ? l[1] : l[0];
        }

        /// <summary>
        /// fix-222: the items on lines <paramref name="firstLine"/> … <paramref name="firstLine"/> + <paramref name="lineCount"/> − 1
        /// widened by <paramref name="margin"/> lines each way (clamped), top to bottom, left before right: the rows whose
        /// pictures the list loads (the visible ones and the next scroll's).
        /// </summary>
        public List<int> ItemsOnLines(int firstLine, int lineCount, int margin = 0)
        {
            var items = new List<int>();
            int from = Math.Max(0, firstLine - Math.Max(0, margin));
            int to = Math.Min(_lines.Count - 1, firstLine + lineCount - 1 + Math.Max(0, margin));
            for (int line = from; line <= to; line++)
                foreach (int item in _lines[line])
                    if (item >= 0) items.Add(item);
            return items;
        }

        /// <summary>The item <paramref name="lines"/> lines up (-) or down (+) from <paramref name="item"/> (clamped), keeping the side.</summary>
        public int MoveLines(int item, int lines, int preferredColumn)
        {
            if (_cells.Length == 0) return -1;
            if (item < 0) return 0;
            int line = Math.Max(0, Math.Min(_lines.Count - 1, _cells[item].Line + lines));
            int side = _cells[item].IsHalf ? _cells[item].Column : preferredColumn < 0 ? 0 : preferredColumn;
            int target = At(line, side);
            return target >= 0 ? target : At(line, 1 - side);
        }

        /// <summary>The other half of a split line (<paramref name="dir"/> -1 left, +1 right), else <paramref name="item"/>.</summary>
        public int MoveSide(int item, int dir)
        {
            if (item < 0 || item >= _cells.Length || !_cells[item].IsHalf) return item;
            int target = At(_cells[item].Line, dir < 0 ? 0 : 1);
            return target >= 0 ? target : item;
        }
    }
}
