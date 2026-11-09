using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Gui;
using Myrtana.Ui.Skin;

namespace G3.Unity.Editor.Gui
{
    /// <summary>
    /// Converts the decoded original GUI data (g3.gui.res, theme_g3.thm, g3.igl) into the engine-neutral
    /// <see cref="GuiSkinData"/> the runtime skin reads. Engine-free on purpose (no UnityEngine use).
    /// </summary>
    public static class GuiSkinDataBuilder
    {
        /// <summary>Image key prefix of theme element crops (asset folder "Theme").</summary>
        public const string ThemeImagePrefix = "theme:";

        /// <param name="slices">9-slice border per theme element name (only frame images), may be empty.</param>
        public static GuiSkinData Build(GuiResource res, GuiTheme theme, ImageGroupList images, IReadOnlyDictionary<string, int[]> slices)
        {
            var data = new GuiSkinData { Source = "gui.pak (g3.gui.res, theme_g3.thm) + Library.pak/g3.igl" };

            data.Theme.Name = theme.Name;
            foreach (var m in theme.Metrics) data.Theme.Metrics[m.Key] = m.Value;
            foreach (var e in theme.Elements)
            {
                var el = new ThemeElementData
                {
                    Name = e.Name, Image = ThemeImagePrefix + e.Name, Width = e.Rect.Width, Height = e.Rect.Height,
                    TextFormat = e.TextFormat, DrawMode = e.DrawMode,
                    Slice = slices != null && slices.TryGetValue(e.Name, out var s) ? s : null,
                };
                for (int i = 0; i < 6; i++)
                    el.States.Add(new StateColors { Text = Hex(e.TextColor[i]), Blend = Hex(e.BlendColor[i]) });
                data.Theme.Elements.Add(el);
            }
            foreach (var c in theme.Cursors)
                data.Theme.Cursors.Add(new ThemeCursorData
                    { Name = c.Name, Image = ThemeImagePrefix + "cursor_" + c.Name, Color = Hex(c.Color), HotSpotX = c.HotSpotX, HotSpotY = c.HotSpotY });

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in images.AllImages)
                if (seen.Add(i.FileName))
                    data.Images.Add(new ImageData { Name = i.FileName, Group = i.Group.Name, Width = i.Rect.Width, Height = i.Rect.Height });

            foreach (var dlg in res.Dialogs)
                data.Screens.Add(Window(res, images, dlg));
            return data;
        }

        static WindowData Window(GuiResource res, ImageGroupList images, GuiNode n)
        {
            var r = n.Placement;
            var w = new WindowData
            {
                Class = n.WindowClass,
                GfcClass = n.GfcClass,
                ResName = res.ResNameOf(n),
                Rect = new[] { r.Left, r.Top, r.Right, r.Bottom },
                Anchor = (int)n.Anchor,
                Style = n.Style,
            };
            string text = n.Name;
            if (!string.IsNullOrEmpty(text) && text != "Generic Window") w.Text = text;
            var f = n.Font;
            if (f != null) w.Font = new FontData { Size = f.PixelHeight, Bold = f.Bold, Italic = f.Italic };
            string bmp = n.Bitmap;
            if (!string.IsNullOrEmpty(bmp)) w.Image = images.Find(bmp)?.FileName ?? FileName(bmp);
            string tc = n.Get("GUI.Static.TextColor");
            if (tc != null && long.TryParse(tc, out long cr)) w.TextColor = Hex(GuiColor.FromColorRef((uint)cr, 255));

            foreach (var a in n.Attributes)
            {
                if (!a.Key.StartsWith("GUI.", StringComparison.Ordinal) || a.Key.StartsWith("GUI.Window.", StringComparison.Ordinal)) continue;
                if (a.Key == "GUI.View.Bitmap" || a.Key == "GUI.Static.Bitmap" || a.Key == "GUI.Static.TextColor") continue;
                (w.Attributes ??= new Dictionary<string, string>())[a.Key.Substring(4)] = a.Value;
            }
            foreach (var c in n.Children)
            {
                if (c.Tag == "window") (w.Children ??= new List<WindowData>()).Add(Window(res, images, c));
                else if (c.Tag == "item")
                    (w.Items ??= new List<Dictionary<string, string>>()).Add(c.Attributes.ToDictionary(
                        a => a.Key.Substring(a.Key.LastIndexOf('.') + 1), a => a.Value));
            }
            return w;
        }

        static string Hex(GuiColor c) => c.Hex;

        static string FileName(string path) => path.Substring(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1);
    }
}
