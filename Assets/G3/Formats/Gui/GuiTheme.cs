using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using G3.Formats.Genome;

namespace G3.Formats.Gui
{
    /// <summary>8-bit RGBA colour.</summary>
    public readonly struct GuiColor
    {
        public readonly byte R, G, B, A;
        public GuiColor(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }

        /// <summary>From a Win32 COLORREF (0x00BBGGRR) plus a separate alpha.</summary>
        public static GuiColor FromColorRef(uint c, uint a) =>
            new GuiColor((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF), (byte)Math.Min(a, 255u));

        public string Hex => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
        public override string ToString() => Hex;
    }

    /// <summary>Visual state of a themed widget, in file order.</summary>
    public enum GuiState { Normal = 0, Disabled = 1, Hidden = 2, Focus = 3, MouseOver = 4, Pressed = 5 }

    public sealed class GuiThemeCursor
    {
        public string Name;          // Pointer, Grab, Wait, HSize, VSize, TLSize, TRSize
        public bool Valid;
        public string Texture;       // atlas path
        public GuiRect Rect;         // in the atlas
        public GuiColor Color;
        public int HotSpotX, HotSpotY;
    }

    public sealed class GuiThemeElement
    {
        /// <summary>Key derived from the source bitmap, e.g. "listctrl_item_details_back".</summary>
        public string Name;
        public string SourceBitmap;
        public string Texture;
        public GuiRect Rect;
        /// <summary>Text alignment flags (26 for almost everything, 27 for push buttons = horizontally centred).</summary>
        public int TextFormat;
        /// <summary>How the image is drawn: 0 borders, 4 bars/edits/list items, 5 icons and most widgets, 36/37 menu bars.</summary>
        public int DrawMode;
        /// <summary>Per state (indexed by <see cref="GuiState"/>): colour of the text.</summary>
        public readonly GuiColor[] TextColor = new GuiColor[6];
        /// <summary>Per state: colour the image is multiplied with (alpha 0 = image not drawn in that state).</summary>
        public readonly GuiColor[] BlendColor = new GuiColor[6];
        public GuiFont Font;
        public override string ToString() => $"{Name} {Rect}";
    }

    /// <summary>
    /// data/gui/themes/g3/compiled/theme_g3.thm (gui.pak): the compiled GUI.dll widget theme. GENOMFLE container
    /// (Latin-1 string table indices for names/paths). Layout (Docs/formats-gui.md §3):
    /// u32 version(1), u16 name, u32 10, u32 1, u32 metricCount(58) + i32 metrics, u32 cursorCount(7) + cursors (42 B),
    /// u32 elementCount(163) + elements (u32 2, u16 source, u16 texture, rect 4×i32, i32 textFormat, i32 drawMode,
    /// i32 stateCount(6), 6 × (text colour, blend colour) where each colour = u32 0x101B0088, u32 COLORREF, u32 alpha,
    /// font = u32 1, u16 byte length + UTF-16 face, i32 height, width, weight, charset, outPrecision, quality,
    /// pitchAndFamily, i32 1, u8 italic?). The atlas is the single 2048x2048 DXT3 theme_g3_00.dds.
    /// </summary>
    public sealed class GuiTheme
    {
        /// <summary>Names of the 58 metrics in file order (GUI.dll string table, reversed listing).</summary>
        public static readonly string[] MetricNames =
        {
            "Screen.X", "Screen.Y", "Cursor.X", "Cursor.Y", "Border.X", "Border.Y", "Frame.Fixed.X", "Frame.Fixed.Y",
            "Frame.Size.X", "Frame.Size.Y", "Menu.Y", "Caption.Y", "Caption.Icon.X", "Caption.Icon.Y",
            "Caption.Button.X", "Caption.Button.Y", "VScroll.Track.X", "VScroll.Thumb.Y", "VScroll.Arrow.Y",
            "HScroll.Track.Y", "HScroll.Thumb.X", "HScroll.Arrow.X", "CheckBox.Box.X", "CheckBox.Box.Y",
            "RadioButton.Box.X", "RadioButton.Box.Y",
            "Border.Window.X", "Border.Window.Y", "Border.Desktop.X", "Border.Desktop.Y", "Border.Dialog.X", "Border.Dialog.Y",
            "Border.Frame.X", "Border.Frame.Y", "Border.Control.X", "Border.Control.Y", "Border.Static.X", "Border.Static.Y",
            "Border.Button.X", "Border.Button.Y", "Border.CheckBox.X", "Border.CheckBox.Y", "Border.RadioButton.X",
            "Border.RadioButton.Y", "Border.Slider.X", "Border.Slider.Y", "Border.ProgressBar.X", "Border.ProgressBar.Y",
            "Border.ScrollBar.X", "Border.ScrollBar.Y", "Border.ListBox.X", "Border.ListBox.Y", "Border.EditBox.X",
            "Border.EditBox.Y", "Border.ToolBar.X", "Border.ToolBar.Y", "Menu.Icon.X", "Menu.Icon.Y",
        };

        public static readonly string[] CursorNames = { "Pointer", "Grab", "Wait", "HSize", "VSize", "TLSize", "TRSize" };

        const uint ColorTag = 0x101B0088;

        public string Name;
        public readonly Dictionary<string, int> Metrics = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<GuiThemeCursor> Cursors = new List<GuiThemeCursor>();
        public readonly List<GuiThemeElement> Elements = new List<GuiThemeElement>();
        readonly Dictionary<string, GuiThemeElement> _byName = new Dictionary<string, GuiThemeElement>(StringComparer.OrdinalIgnoreCase);

        public GuiThemeElement Get(string name) => _byName.TryGetValue(name, out var e) ? e : null;

        public static GuiTheme Read(byte[] data)
        {
            var g = new GenomeReader(data);
            if (!g.IsGenomFile) throw new InvalidDataException("theme: not a GENOMFLE file");
            var r = g.R;
            var t = new GuiTheme();
            if (r.ReadUInt32() != 1) throw new InvalidDataException("theme: unsupported version");
            t.Name = g.Str();
            r.ReadUInt32(); r.ReadUInt32();
            int metrics = r.ReadInt32();
            for (int i = 0; i < metrics; i++)
            {
                int v = r.ReadInt32();
                t.Metrics[i < MetricNames.Length ? MetricNames[i] : "Metric" + i] = v;
            }
            int cursors = r.ReadInt32();
            for (int i = 0; i < cursors; i++)
            {
                var c = new GuiThemeCursor { Name = i < CursorNames.Length ? CursorNames[i] : "Cursor" + i };
                c.Valid = r.ReadUInt32() != 0;
                c.Texture = g.Str();
                c.Rect = ReadRect(r);
                c.Color = ReadColor(r);
                c.HotSpotX = r.ReadInt32();
                c.HotSpotY = r.ReadInt32();
                t.Cursors.Add(c);
            }
            int elements = r.ReadInt32();
            for (int i = 0; i < elements; i++)
            {
                long start = g.Position;
                if (r.ReadUInt32() != 2) throw new InvalidDataException($"theme: element {i} has an unknown version");
                var e = new GuiThemeElement { SourceBitmap = g.Str(), Texture = g.Str() };
                e.Name = ElementKey(e.SourceBitmap);
                e.Rect = ReadRect(r);
                e.TextFormat = r.ReadInt32();
                e.DrawMode = r.ReadInt32();
                int states = r.ReadInt32();
                if (states != 6) throw new InvalidDataException($"theme: element {i} has {states} states");
                for (int s = 0; s < 6; s++)
                {
                    e.TextColor[s] = ReadColor(r);
                    e.BlendColor[s] = ReadColor(r);
                }
                r.ReadUInt32();
                int faceBytes = r.ReadUInt16();
                var f = new GuiFont { Face = Encoding.Unicode.GetString(r.ReadBytes(faceBytes)) };
                f.Height = r.ReadInt32();
                f.Width = r.ReadInt32();
                f.Weight = r.ReadInt32();
                f.CharSet = r.ReadInt32();
                r.ReadInt32();                 // out precision
                f.Quality = r.ReadInt32();
                r.ReadInt32();                 // pitch and family
                r.ReadInt32();                 // always 1 (meaning unknown)
                f.Italic = r.ReadByte() != 0;  // always 0 in theme_g3
                e.Font = f;
                t.Elements.Add(e);
                t._byName[e.Name] = e;
            }
            if (g.Position != g.PayloadEnd)
                throw new InvalidDataException($"theme: {g.PayloadEnd - g.Position} unread bytes");
            return t;
        }

        static GuiRect ReadRect(BinaryReader r) => new GuiRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        static GuiColor ReadColor(BinaryReader r)
        {
            if (r.ReadUInt32() != ColorTag) throw new InvalidDataException("theme: colour tag mismatch");
            uint c = r.ReadUInt32();
            return GuiColor.FromColorRef(c, r.ReadUInt32());
        }

        /// <summary>"/data/gui/themes/g3/elements/theme_g3_element_listctrl_main_back.bmp" → "listctrl_main_back".</summary>
        public static string ElementKey(string path)
        {
            string f = path.Replace('\\', '/');
            f = f.Substring(f.LastIndexOf('/') + 1);
            int dot = f.LastIndexOf('.');
            if (dot > 0) f = f.Substring(0, dot);
            int k = f.IndexOf("_element_", StringComparison.OrdinalIgnoreCase);
            return k >= 0 ? f.Substring(k + 9) : f;
        }
    }
}
