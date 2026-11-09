using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace G3.Formats.Gui
{
    /// <summary>Left/top/right/bottom rectangle in GUI pixels (right/bottom exclusive).</summary>
    public readonly struct GuiRect
    {
        public readonly int Left, Top, Right, Bottom;
        public GuiRect(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"{Left},{Top},{Right},{Bottom} ({Width}x{Height})";

        /// <summary>Parses "l,t,r,b"; returns false for anything else.</summary>
        public static bool TryParse(string s, out GuiRect r)
        {
            r = default;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split(',');
            if (p.Length != 4) return false;
            var v = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return false;
            r = new GuiRect(v[0], v[1], v[2], v[3]);
            return true;
        }
    }

    /// <summary>
    /// Anchor flags of a window (GUI.Window.Anchor). Observed in g3.gui.res: compass 9 (left+bottom), status bars 10
    /// (right+bottom), focus bar 4 (top), quick slots 8 (bottom), full views 5 (left+top), message area 15 (all sides).
    /// </summary>
    [Flags]
    public enum GuiAnchor { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    /// <summary>Font of a window (Win32 LOGFONT fields, stored as GUI.Window.Font.*).</summary>
    public sealed class GuiFont
    {
        public string Face;
        /// <summary>LOGFONT lfHeight: negative = character height in pixels (stored in the file as an unsigned 32-bit number).</summary>
        public int Height;
        public int Width;
        public int Weight;
        public bool Italic;
        public int CharSet;
        public int Quality;
        public int PixelHeight => Math.Abs(Height);
        public bool Bold => Weight >= 600;
        public override string ToString() => $"{Face} {PixelHeight}px w{Weight}{(Italic ? " italic" : "")}";
    }

    /// <summary>One element of the resource tree: "window", "item", "image" or a section node.</summary>
    public sealed class GuiNode
    {
        public string Tag;
        public readonly Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<GuiNode> Children = new List<GuiNode>();

        public string Get(string key) => Attributes.TryGetValue(key, out var v) ? v : null;

        public int GetInt(string key, int fallback = 0)
        {
            var v = Get(key);
            if (v == null) return fallback;
            // Values are written unsigned (e.g. -19 as 4294967277); wrap them back to signed 32-bit.
            if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return unchecked((int)l);
            return fallback;
        }

        public bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase) || Get(key) == "1";

        // ---- typed accessors for "window" nodes ----
        public string WindowClass => Get("GUI.Window.Class");
        public string Name => Get("GUI.Window.Name");
        public string ResName => Get("ResName");
        public int Id => GetInt("GUI.Window.ID", -1);
        public int ResId => GetInt("ResID", -1);
        public uint Style => unchecked((uint)GetInt("GUI.Window.Style"));
        public GuiAnchor Anchor => (GuiAnchor)GetInt("GUI.Window.Anchor", (int)(GuiAnchor.Left | GuiAnchor.Top));
        public int Dock => GetInt("GUI.Window.Dock");
        public string GfcClass => Get("GFC.Class");
        public string GfcVariable => Get("GFC.Variable");
        public GuiRect Placement => GuiRect.TryParse(Get("GUI.Window.Placement"), out var r) ? r : default;

        /// <summary>Background (GUI.View.Bitmap) or picture (GUI.Static.Bitmap) as a Genome path, e.g. "\data\gui\...\x.tga".</summary>
        public string Bitmap => Get("GUI.View.Bitmap") ?? Get("GUI.Static.Bitmap");

        public GuiFont Font
        {
            get
            {
                var face = Get("GUI.Window.Font.Face");
                if (face == null) return null;
                return new GuiFont
                {
                    Face = face,
                    Height = GetInt("GUI.Window.Font.Height"),
                    Width = GetInt("GUI.Window.Font.Width"),
                    Weight = GetInt("GUI.Window.Font.Weight"),
                    Italic = GetInt("GUI.Window.Font.Italic") != 0,
                    CharSet = GetInt("GUI.Window.Font.CharSet"),
                    Quality = GetInt("GUI.Window.Font.Quality"),
                };
            }
        }

        public IEnumerable<GuiNode> Descendants()
        {
            foreach (var c in Children)
            {
                yield return c;
                foreach (var d in c.Descendants()) yield return d;
            }
        }

        public override string ToString() => $"{Tag} {WindowClass} {ResName ?? Name} {Placement}";
    }

    /// <summary>
    /// data/gui/resources/g3.gui.res (gui.pak): the GUI resource database. XML-like text (no XML declaration, one
    /// attribute per line, CRLF, Latin-1). Root "resources" holds sections user, controls, dialogs, bitmaps, cursors,
    /// themes; "controls" is a flat list of every control (with ResName/GFC info), "dialogs" holds every screen as a
    /// window tree whose children reference controls by GUI.Window.ID. Coordinates are in a 1024x768 reference space.
    /// See Docs/formats-gui.md §2.
    /// </summary>
    public sealed class GuiResource
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public GuiNode Root;
        public readonly List<GuiNode> Controls = new List<GuiNode>();
        public readonly List<GuiNode> Dialogs = new List<GuiNode>();
        public readonly Dictionary<int, GuiNode> ControlsById = new Dictionary<int, GuiNode>();
        public readonly Dictionary<string, GuiNode> DialogsByName = new Dictionary<string, GuiNode>(StringComparer.OrdinalIgnoreCase);

        public static GuiResource Read(byte[] data) => Parse(Latin1.GetString(data));

        public static GuiResource Parse(string text)
        {
            var res = new GuiResource { Root = ParseTree(text) };
            var resources = res.Root.Children.Find(c => c.Tag == "resources") ?? throw new InvalidDataException("g3.gui.res: no <resources>");
            foreach (var section in resources.Children)
            {
                if (section.Tag == "controls")
                    foreach (var w in section.Children)
                    {
                        res.Controls.Add(w);
                        if (w.ResId >= 0) res.ControlsById[w.ResId] = w;
                    }
                else if (section.Tag == "dialogs")
                    foreach (var w in section.Children)
                    {
                        res.Dialogs.Add(w);
                        if (w.ResName != null) res.DialogsByName[w.ResName] = w;
                    }
            }
            return res;
        }

        /// <summary>Control definition (ResName, GFC class/variable) for a window placed in a dialog; null if none.</summary>
        public GuiNode ControlFor(GuiNode window) => ControlsById.TryGetValue(window.Id, out var c) ? c : null;

        /// <summary>ResName of a placed window via its control definition ("IDC_..."), or null.</summary>
        public string ResNameOf(GuiNode window) => window.ResName ?? ControlFor(window)?.ResName;

        static GuiNode ParseTree(string s)
        {
            var root = new GuiNode { Tag = "#root" };
            var stack = new Stack<GuiNode>();
            stack.Push(root);
            int i = 0, n = s.Length;
            while (i < n)
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) break;
                int p = lt + 1;
                bool close = p < n && s[p] == '/';
                if (close) p++;
                int nameStart = p;
                while (p < n && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
                string tag = s.Substring(nameStart, p - nameStart);
                if (close)
                {
                    int gt = s.IndexOf('>', p);
                    if (stack.Count > 1) stack.Pop();
                    i = gt < 0 ? n : gt + 1;
                    continue;
                }
                var node = new GuiNode { Tag = tag };
                // attributes until '>' (values are quoted and never contain '"')
                while (p < n && s[p] != '>')
                {
                    if (char.IsWhiteSpace(s[p])) { p++; continue; }
                    int k = p;
                    while (p < n && s[p] != '=' && s[p] != '>' && !char.IsWhiteSpace(s[p])) p++;
                    string key = s.Substring(k, p - k);
                    while (p < n && char.IsWhiteSpace(s[p])) p++;
                    if (p < n && s[p] == '=')
                    {
                        p++;
                        while (p < n && char.IsWhiteSpace(s[p])) p++;
                        if (p < n && s[p] == '"')
                        {
                            int end = s.IndexOf('"', p + 1);
                            if (end < 0) throw new InvalidDataException($"g3.gui.res: unterminated value at {p}");
                            node.Attributes[key] = s.Substring(p + 1, end - p - 1);
                            p = end + 1;
                        }
                    }
                }
                stack.Peek().Children.Add(node);
                stack.Push(node);
                i = p + 1;
            }
            return root;
        }
    }
}
