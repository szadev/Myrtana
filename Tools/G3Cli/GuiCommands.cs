using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using G3.Formats.Gui;
using G3.Formats.Items;
using G3.Formats.Pak;

static partial class Program
{
    /// <summary>G3Cli gui &lt;sub&gt; — original GUI data: g3.gui.res, theme_g3.thm, g3.igl, atlases (Docs/formats-gui.md).</summary>
    static int GuiCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "validate";
        switch (sub)
        {
            case "validate": return GuiValidate(data);
            case "dialogs": return GuiDialogs(data);
            case "dump": return GuiDump(data, args[2]);
            case "theme": return GuiThemeDump(data, args.Length > 2 ? args[2] : null);
            case "images": return GuiImages(data, args.Length > 2 ? args[2] : null);
            case "png": return GuiPng(data, args[2], args.Skip(3).ToArray());
        }
        Console.WriteLine("usage: gui validate | dialogs | dump <IDD_name> | theme [filter] | images [filter] | png <outDir> <image|theme:element|atlas:file|cursor:name>...");
        return 1;
    }

    sealed class GuiData : IDisposable
    {
        public G3FileSystem Fs;
        public GuiResource Res;
        public GuiTheme Theme;
        public ImageGroupList Images;
        readonly Dictionary<string, (byte[] rgba, int w, int h)> _atlas = new Dictionary<string, (byte[], int, int)>(StringComparer.OrdinalIgnoreCase);

        public static GuiData Load(string data)
        {
            var d = new GuiData { Fs = new G3FileSystem(data, "gui", "Library") };
            d.Res = GuiResource.Read(d.Need("g3.gui.res"));
            d.Theme = GuiTheme.Read(d.Need("theme_g3.thm"));
            d.Images = ImageGroupList.Read(d.Need("g3.igl"));
            return d;
        }

        public byte[] Need(string file) => (Fs.GetByName(file) ?? throw new FileNotFoundException(file)).Read();

        public (byte[] rgba, int w, int h) Atlas(string texturePath)
        {
            string f = Path.GetFileName(texturePath.Replace('\\', '/'));
            if (!_atlas.TryGetValue(f, out var a))
            {
                var dds = DdsImage.Read(Need(f));
                a = (dds.ToRgba(), dds.Width, dds.Height);
                _atlas[f] = a;
            }
            return a;
        }

        public void Dispose() => Fs.Dispose();
    }

    static int GuiValidate(string data)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var d = GuiData.Load(data);
        int bad = 0;
        Console.WriteLine($"g3.gui.res: {d.Res.Controls.Count} controls, {d.Res.Dialogs.Count} dialogs, " +
                          $"{d.Res.Dialogs.Sum(x => x.Descendants().Count(n => n.Tag == "window"))} placed windows");
        foreach (var g in d.Res.Dialogs.SelectMany(x => x.Descendants().Prepend(x)).Where(n => n.Tag == "window").GroupBy(n => n.WindowClass).OrderByDescending(g => g.Count()))
            Console.Write($"{g.Key} {g.Count()}  ");
        Console.WriteLine();
        foreach (var dlg in d.Res.Dialogs)
            foreach (var w in dlg.Descendants().Where(n => n.Tag == "window"))
                if (d.Res.ControlFor(w) == null) { bad++; Console.WriteLine($"  {dlg.ResName}: window id {w.Id} has no control entry"); }

        // every bitmap referenced by the layout must be in the image group list
        var bitmaps = d.Res.Dialogs.SelectMany(x => x.Descendants().Prepend(x)).Select(n => n.Bitmap ?? n.Get("GUI.TabCtrl.Image.File")
                      ?? n.Get("GUI.ProgressBar.Bitmap.Bar") ?? n.Get("GUI.ProgressBar.Bitmap.Track")).Where(b => !string.IsNullOrEmpty(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = bitmaps.Where(b => d.Images.Find(b) == null).ToList();
        Console.WriteLine($"layout bitmaps: {bitmaps.Count} distinct, resolved {bitmaps.Count - missing.Count}");
        foreach (var m in missing) Console.WriteLine("  unresolved: " + m);

        Console.WriteLine($"theme '{d.Theme.Name}': {d.Theme.Metrics.Count} metrics, {d.Theme.Cursors.Count} cursors, {d.Theme.Elements.Count} elements, " +
                          $"fonts {string.Join(", ", d.Theme.Elements.GroupBy(e => e.Font.ToString()).Select(g => $"{g.Key} x{g.Count()}"))}");
        var themeAtlas = d.Atlas(d.Theme.Elements[0].Texture);
        foreach (var e in d.Theme.Elements)
            if (e.Rect.Right > themeAtlas.w || e.Rect.Bottom > themeAtlas.h) { bad++; Console.WriteLine("  theme rect outside atlas: " + e); }

        int imgs = 0;
        foreach (var g in d.Images.Groups)
        {
            Console.WriteLine($"igl {g.Name,-16} {g.Images.Count,4} images, atlases {string.Join(" ", g.Images.Select(i => i.AtlasFile).Distinct().OrderBy(x => x).Select(x => x.Replace(g.PackBaseName + "_", "").Replace(".dds", "")))}");
            foreach (var i in g.Images)
            {
                imgs++;
                var a = d.Atlas(i.Texture);
                if (i.Rect.Right > a.w || i.Rect.Bottom > a.h) { bad++; Console.WriteLine("  image rect outside atlas: " + i); }
            }
        }
        var unused = d.Fs.Files.Where(e => e.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            .Select(e => Path.GetFileName(e.Path)).Where(f => !f.StartsWith("theme_") && !d.Images.AllImages.Any(i => i.AtlasFile.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
        Console.WriteLine($"igl images {imgs}; atlases without index entries: {(unused.Count == 0 ? "none" : string.Join(", ", unused))}");

        // item icons
        var cat = ItemCatalog.Build(LoadTemplates(data));
        var withIcon = cat.Items.Where(i => !string.IsNullOrEmpty(i.Icon)).ToList();
        var noIcon = withIcon.Where(i => d.Images.Find(i.Icon) == null).ToList();
        Console.WriteLine($"item templates with an icon: {withIcon.Count}, resolved in g3.igl: {withIcon.Count - noIcon.Count}");
        foreach (var i in noIcon.Take(15)) Console.WriteLine($"  icon not packed: {i.Name} {i.Icon}");
        Console.WriteLine($"{(bad == 0 ? "OK" : bad + " problems")} in {sw.ElapsedMilliseconds} ms");
        return bad == 0 ? 0 : 2;
    }

    static int GuiDialogs(string data)
    {
        using var d = GuiData.Load(data);
        foreach (var x in d.Res.Dialogs)
            Console.WriteLine($"{x.ResName,-40} {x.GfcClass,-36} {x.Placement,-26} {x.Descendants().Count(n => n.Tag == "window"),3} ctl  {Short(x.Bitmap)}");
        return 0;
    }

    static int GuiDump(string data, string name)
    {
        using var d = GuiData.Load(data);
        if (!d.Res.DialogsByName.TryGetValue(name, out var dlg)) { Console.WriteLine("no dialog " + name); return 1; }
        Console.WriteLine($"{dlg.ResName} {dlg.GfcClass} {dlg.Placement} anchor {dlg.Anchor} {Short(dlg.Bitmap)}");
        void Rec(GuiNode n, int depth)
        {
            foreach (var c in n.Children)
            {
                string pad = new string(' ', depth * 2);
                if (c.Tag != "window")
                {
                    Console.WriteLine($"{pad}<{c.Tag}> " + string.Join(" ", c.Attributes.Select(a => $"{a.Key.Replace("GUI.TabCtrl.", "")}={a.Value}")));
                    continue;
                }
                var ctl = d.Res.ControlFor(c);
                var extra = c.Attributes.Where(a => !a.Key.StartsWith("GUI.Window.") && a.Key != "GUI.View.Bitmap" && a.Key != "GUI.Static.Bitmap")
                    .Select(a => $"{a.Key.Replace("GUI.", "")}={a.Value}");
                var font = c.Font;
                string bmp = c.Bitmap;
                var img = bmp != null ? d.Images.Find(bmp) : null;
                Console.WriteLine($"{pad}{c.WindowClass,-12} {ctl?.ResName ?? "?",-44} {c.Placement,-26} anc={(int)c.Anchor,-2} style={c.Style:X8}" +
                                  $"{(font != null ? " font=" + font : "")}{(string.IsNullOrEmpty(c.Name) || c.Name == "Generic Window" ? "" : $" \"{c.Name}\"")}" +
                                  $"{(bmp != null ? $" bmp={Short(bmp)}{(img != null ? $" [{img.AtlasFile} {img.Rect}]" : " [unresolved]")}" : "")} {string.Join(" ", extra)}");
                Rec(c, depth + 1);
            }
        }
        Rec(dlg, 1);
        return 0;
    }

    static int GuiThemeDump(string data, string filter)
    {
        using var d = GuiData.Load(data);
        var t = d.Theme;
        Console.WriteLine($"theme {t.Name}; metrics: " + string.Join(", ", t.Metrics.Select(m => $"{m.Key}={m.Value}")));
        foreach (var c in t.Cursors) Console.WriteLine($"cursor {c.Name,-8} {c.Rect} colour {c.Color} hotspot {c.HotSpotX},{c.HotSpotY}");
        foreach (var e in t.Elements.Where(e => filter == null || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"{e.Name,-36} {e.Rect,-28} fmt={e.TextFormat} draw={e.DrawMode} font={e.Font}");
            Console.WriteLine("    text  " + string.Join(" ", Enumerable.Range(0, 6).Select(s => $"{(GuiState)s}={e.TextColor[s]}")));
            Console.WriteLine("    blend " + string.Join(" ", Enumerable.Range(0, 6).Select(s => $"{(GuiState)s}={e.BlendColor[s]}")));
        }
        return 0;
    }

    static int GuiImages(string data, string filter)
    {
        using var d = GuiData.Load(data);
        foreach (var i in d.Images.AllImages.Where(i => filter == null || i.SourcePath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{i.Group.Name,-16} {i.FileName,-48} {i.AtlasFile,-28} {i.Rect}");
        return 0;
    }

    /// <summary>Export images as PNG: an igl image name, "theme:element", "cursor:Pointer" or "atlas:file.dds".</summary>
    static int GuiPng(string data, string outDir, string[] names)
    {
        using var d = GuiData.Load(data);
        Directory.CreateDirectory(outDir);
        foreach (var n in names)
        {
            byte[] rgba; int w, h; string file;
            if (n.StartsWith("atlas:"))
            {
                var a = d.Atlas(n.Substring(6)); rgba = a.rgba; w = a.w; h = a.h;
                file = Path.GetFileNameWithoutExtension(n.Substring(6));
            }
            else
            {
                string tex; GuiRect r;
                if (n.StartsWith("theme:")) { var e = d.Theme.Get(n.Substring(6)) ?? throw new ArgumentException(n); tex = e.Texture; r = e.Rect; file = "theme_" + e.Name; }
                else if (n.StartsWith("cursor:")) { var c = d.Theme.Cursors.First(x => x.Name.Equals(n.Substring(7), StringComparison.OrdinalIgnoreCase)); tex = c.Texture; r = c.Rect; file = "cursor_" + c.Name; }
                else { var i = d.Images.Find(n) ?? throw new ArgumentException("image not found: " + n); tex = i.Texture; r = i.Rect; file = Path.GetFileNameWithoutExtension(i.FileName); }
                var a = d.Atlas(tex);
                rgba = DdsImage.Crop(a.rgba, a.w, r); w = r.Width; h = r.Height;
            }
            string path = Path.Combine(outDir, file + ".png");
            WritePng(path, rgba, w, h);
            Console.WriteLine($"{path} {w}x{h}");
        }
        return 0;
    }

    static string Short(string p) => p == null ? "" : p.Substring(Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/')) + 1);

    /// <summary>RGBA8 PNG (<see cref="G3.Formats.Image.Png.Encode"/>).</summary>
    static void WritePng(string path, byte[] rgba, int w, int h) => File.WriteAllBytes(path, G3.Formats.Image.Png.Encode(rgba, w, h));

}
