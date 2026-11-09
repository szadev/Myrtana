using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Lookups over <see cref="GuiSkinData"/>: theme elements by key, images by name, screens by IDD name. Image names
    /// follow the engine rule (Docs/formats-gui.md §4): file name only, case-insensitive; the extension is optional, so
    /// "IT_Apple", "IT_Apple.tga" and "\data\gui\…\it_apple.tga" are the same image.
    /// </summary>
    public sealed class GuiSkinCatalog : IImageCatalog
    {
        readonly Dictionary<string, ThemeElementData> _elements = new Dictionary<string, ThemeElementData>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ImageData> _images = new Dictionary<string, ImageData>(StringComparer.Ordinal);
        readonly Dictionary<string, WindowData> _screens = new Dictionary<string, WindowData>(StringComparer.OrdinalIgnoreCase);

        public GuiSkinData Data { get; }
        public ThemeData Theme => Data.Theme;

        public GuiSkinCatalog(GuiSkinData data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            foreach (var e in data.Theme.Elements) _elements[e.Name] = e;
            foreach (var i in data.Images)
            {
                string key = ImageKey(i.Name);
                if (!_images.ContainsKey(key)) _images[key] = i;
            }
            foreach (var s in data.Screens)
                if (s.ResName != null) _screens[s.ResName] = s;
        }

        public ThemeElementData Element(string name) => name != null && _elements.TryGetValue(name, out var e) ? e : null;

        public WindowData Screen(string resName) => resName != null && _screens.TryGetValue(resName, out var s) ? s : null;

        public IEnumerable<WindowData> Screens => Data.Screens;

        public ImageData Image(string name) => name != null && _images.TryGetValue(ImageKey(name), out var i) ? i : null;

        public bool Contains(string imageName) => Image(imageName) != null;

        /// <summary>Normalised image key: file name without folder and extension, lower case ("it_apple").</summary>
        public static string ImageKey(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return string.Empty;
            int slash = Math.Max(nameOrPath.LastIndexOf('\\'), nameOrPath.LastIndexOf('/'));
            string f = slash >= 0 ? nameOrPath.Substring(slash + 1) : nameOrPath;
            int dot = f.LastIndexOf('.');
            if (dot > 0) f = f.Substring(0, dot);
            return f.ToLowerInvariant();
        }
    }

    /// <summary>Which packed images exist (implemented by the skin catalog; tests use a set).</summary>
    public interface IImageCatalog
    {
        bool Contains(string imageName);
    }
}
