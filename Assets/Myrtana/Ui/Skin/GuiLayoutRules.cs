using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>GUI.Window.Anchor flags (Docs/formats-gui.md §2, §7).</summary>
    [Flags]
    public enum AnchorFlags { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    public enum AxisMode
    {
        /// <summary>Fixed distance to the start (left/top) edge.</summary>
        Start,
        /// <summary>Fixed distance to the end (right/bottom) edge.</summary>
        End,
        /// <summary>Both distances fixed: the window stretches.</summary>
        Stretch,
        /// <summary>Neither: the window keeps its offset from the parent's centre.</summary>
        Center,
    }

    /// <summary>Placement of a window along one axis inside its parent, in reference pixels.</summary>
    public readonly struct AxisPlacement
    {
        public readonly AxisMode Mode;
        /// <summary>Distance from the parent's start edge (reference layout).</summary>
        public readonly int Start;
        /// <summary>Distance from the parent's end edge (reference layout).</summary>
        public readonly int End;
        public readonly int Size;
        /// <summary>Start edge relative to the parent's centre (for <see cref="AxisMode.Center"/>).</summary>
        public readonly int CenterOffset;

        public AxisPlacement(AxisMode mode, int start, int end, int size, int centerOffset)
        { Mode = mode; Start = start; End = end; Size = size; CenterOffset = centerOffset; }

        /// <summary>Start position and size inside a parent of <paramref name="parentSize"/> (original GUI.dll rule).</summary>
        public (float start, float size) Resolve(float parentSize)
        {
            switch (Mode)
            {
                case AxisMode.End: return (parentSize - End - Size, Size);
                case AxisMode.Stretch: return (Start, Math.Max(0, parentSize - Start - End));
                case AxisMode.Center: return (parentSize / 2f + CenterOffset, Size);
                default: return (Start, Size);
            }
        }
    }

    /// <summary>
    /// The anchoring rule of GUI.dll (§7): when the parent grows, right-only windows move, left+right stretch, left-only
    /// stay, neither stay centred; the same vertically. g3.gui.res writes the flags explicitly, so 0 means centred on both
    /// axes (page sidebar); GUI.dll's default left+top (5) only applies to windows without the attribute.
    /// </summary>
    public static class GuiLayoutRules
    {
        public static AxisPlacement Horizontal(int left, int right, int parentWidth, AnchorFlags a) =>
            Axis(left, right, parentWidth, (a & AnchorFlags.Left) != 0, (a & AnchorFlags.Right) != 0);

        public static AxisPlacement Vertical(int top, int bottom, int parentHeight, AnchorFlags a) =>
            Axis(top, bottom, parentHeight, (a & AnchorFlags.Top) != 0, (a & AnchorFlags.Bottom) != 0);

        static AxisPlacement Axis(int start, int end, int parent, bool toStart, bool toEnd)
        {
            int size = end - start, fromEnd = parent - end;
            AxisMode mode = toStart && !toEnd ? AxisMode.Start
                : toStart ? AxisMode.Stretch
                : toEnd ? AxisMode.End
                : AxisMode.Center;
            return new AxisPlacement(mode, start, fromEnd, size, start - parent / 2);
        }
    }

    public enum TextAlign { Left = 0, Center = 1, Right = 2 }

    /// <summary>
    /// Text layout of a Static from its GUI.Window.Style bits [inferred from the data: 0x24 left labels, 0x25 centred
    /// titles, 0x26 right-aligned values, 0x05 the subtitle box, 0x00 wrapped descriptions, 0x15 pictures].
    /// </summary>
    public readonly struct StaticTextFormat
    {
        public readonly TextAlign Align;
        public readonly bool VerticalCenter;
        public readonly bool SingleLine;
        public readonly bool Picture;

        public StaticTextFormat(uint style)
        {
            int h = (int)(style & 3);
            Align = h == 1 ? TextAlign.Center : h == 2 ? TextAlign.Right : TextAlign.Left;
            VerticalCenter = (style & 0x4) != 0;
            Picture = (style & 0x10) != 0;
            SingleLine = (style & 0x20) != 0;
        }
    }
}

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Tile-view geometry of G3 list controls (GUI.dll tile layout FUN_1002bf30): 46 px cells at a 48 px pitch from the
    /// client's top-left corner, a new row when the next cell would pass the client width, so columns =
    /// (width − scroll bar) / 48; the visible page = client height / 48 rows. The vertical scroll bar of a tile list is
    /// always shown (ShowScrollBar(SB_VERT, TRUE)), so the client is 17 px narrower. Every tile list of the layouts:
    /// inventory/loot 258×482 / 260×480 → 5×10, vendor 260×384 → 5×8, player trade 260×336 → 5×7, offer pools 306×144 → 6×3,
    /// quick slots 480×48 (no scroll bar) → 10×1.
    /// </summary>
    public static class TileGrid
    {
        public const int Cell = 46, Pitch = 48;

        /// <summary>WS_VSCROLL in GUI.Window.Style: the list owns a vertical scroll bar (all inventory, loot and trade
        /// lists; not the skill, spell or quick-slot grids).</summary>
        public static bool HasVerticalScrollBar(uint style) => (style & 0x200000) != 0;

        public static int Columns(int width, int scrollBar, int pitch = Pitch) => System.Math.Max(1, (width - scrollBar) / pitch);
        public static int Rows(int height, int pitch = Pitch) => System.Math.Max(1, height / pitch);

        /// <summary>Rows the items fill (the scroll range of the list; empty cells drawn beyond them do not count).</summary>
        public static int ItemRows(int count, int columns) => count <= 0 ? 0 : (count + columns - 1) / columns;

        /// <summary>Top-left of cell <paramref name="index"/> inside the scrolled content (reference px).</summary>
        public static (int x, int y) CellOrigin(int index, int columns, int pitchX = Pitch, int pitchY = Pitch) =>
            (index % columns * pitchX, index / columns * pitchY);
    }

    /// <summary>Where the thumb of a scroll bar sits along its length (reference px from the bar's start edge).</summary>
    public readonly struct ScrollThumb
    {
        public readonly bool Visible;
        public readonly int Offset;
        public readonly int Length;

        public ScrollThumb(bool visible, int offset, int length) { Visible = visible; Offset = offset; Length = length; }

        public override string ToString() => Visible ? $"thumb {Offset}+{Length}" : "no thumb";
    }

    /// <summary>
    /// The G3 scroll bar (GUI.dll <c>CFFGUIScrollBar</c>, Win32 SCROLLINFO semantics), in list rows: range min 0 … max =
    /// rows of items, page = visible rows, position = first visible row (0 … rows − page). The bar is 17 px wide (theme
    /// metric 16) with square arrow buttons at both ends; the track is the length between them. The thumb shows only while
    /// the page is smaller than the range; its length is track × page / range, at least 8 px (FUN_1001c290, init 8), and it
    /// sits at (track − length) × position / (range − page) after the first arrow. The mouse wheel scrolls the system's
    /// wheel lines (SPI_GETWHEELSCROLLLINES, 3) per notch; an arrow scrolls one row, a click in the track one page.
    /// </summary>
    public static class G3ScrollRules
    {
        public const int Width = 17, MinThumb = 8, WheelRows = 3;

        public static int Track(int length) => System.Math.Max(0, length - 2 * Width);

        public static int MaxPosition(int range, int page) => System.Math.Max(0, range - page);

        public static int Clamp(int position, int range, int page) => System.Math.Max(0, System.Math.Min(position, MaxPosition(range, page)));

        public static ScrollThumb Thumb(int length, int range, int page, int position)
        {
            if (page < 0 || page >= range) return new ScrollThumb(false, 0, 0);
            int track = Track(length);
            int size = System.Math.Max(MinThumb, track * page / range);
            if (size > track) size = track;
            position = Clamp(position, range, page);
            return new ScrollThumb(true, (track - size) * position / (range - page) + Width, size);
        }

        /// <summary>The position for a thumb dragged so that it starts at <paramref name="thumbOffset"/> (nearest row).</summary>
        public static int PositionAt(int length, int range, int page, int thumbOffset)
        {
            var t = Thumb(length, range, page, 0);
            int free = Track(length) - t.Length;
            if (!t.Visible || free <= 0) return 0;
            int pos = (int)System.Math.Round((double)(thumbOffset - Width) * (range - page) / free);
            return Clamp(pos, range, page);
        }

        /// <summary>The position that keeps <paramref name="row"/> visible, moving as little as possible.</summary>
        public static int Reveal(int position, int row, int range, int page)
        {
            if (row < 0) return Clamp(position, range, page);
            if (row < position) position = row;
            else if (page > 0 && row >= position + page) position = row - page + 1;
            return Clamp(position, range, page);
        }
    }
}
