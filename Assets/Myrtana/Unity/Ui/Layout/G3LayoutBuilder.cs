using System;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Layout
{
    /// <summary>
    /// Builds UI Toolkit trees from the original screen layouts (g3.gui.res → <see cref="WindowData"/>): every window
    /// becomes an absolutely placed element named after its IDC/IDD resource name, placed by the GUI.dll anchoring rule
    /// (<see cref="GuiLayoutRules"/>). Statics become labels or pictures, buttons <see cref="G3Button"/>s; list, tab,
    /// slider and other interactive controls become empty <b>slots</b> (class <c>g3-slot</c>) that a screen presenter
    /// fills with G3 components bound to its view model — so layout stays data, behaviour stays code.
    /// Pages (<c>IDD_PAGE_*</c>) contain <c>IDC_VIEW_*</c> placeholders; with <see cref="Options.ExpandViews"/> the
    /// matching <c>IDD_VIEW_*</c> layout is built into them.
    /// </summary>
    public static class G3LayoutBuilder
    {
        public const string SlotClass = "g3-slot";
        const uint VisibleStyle = 0x10000000;

        public sealed class Options
        {
            /// <summary>Show the German design-time texts of labels and buttons (layout previews only).</summary>
            public bool DesignTexts;
            /// <summary>Build IDD_VIEW_* layouts into the IDC_VIEW_* placeholders of a page.</summary>
            public bool ExpandViews = true;
            /// <summary>Outline empty slots (layout previews only).</summary>
            public bool OutlineSlots;
        }

        static readonly Options Defaults = new Options();

        /// <summary>A screen (IDD_…) of its reference size with its background; null if the layout is unknown.</summary>
        public static VisualElement Build(G3Skin skin, string screenName, Options options = null)
        {
            var w = skin.Catalog.Screen(screenName);
            return w == null ? null : Build(skin, w, options);
        }

        public static VisualElement Build(G3Skin skin, WindowData screen, Options options = null)
        {
            options ??= Defaults;
            var root = new VisualElement { name = screen.ResName, userData = screen, pickingMode = PickingMode.Ignore };
            root.AddToClassList("g3-screen");
            root.style.width = screen.Width;
            root.style.height = screen.Height;
            if (!string.IsNullOrEmpty(screen.Image))
            {
                var bg = new G3Image(screen.Image);
                bg.AddToClassList("g3-fill-parent");
                bg.name = "background";
                root.Add(bg);
            }
            AddChildren(skin, root, screen, options);
            return root;
        }

        static void AddChildren(G3Skin skin, VisualElement parent, WindowData w, Options options)
        {
            if (w.Children == null) return;
            foreach (var c in w.Children)
            {
                var e = Create(skin, c, options);
                e.name = c.ResName;
                e.userData = c;
                Place(e, c, w.Width, w.Height);
                if ((c.Style & VisibleStyle) == 0) e.style.display = DisplayStyle.None;
                parent.Add(e);
            }
        }

        static VisualElement Create(G3Skin skin, WindowData c, Options options)
        {
            switch (c.Class)
            {
                case "Static":
                    if (options.ExpandViews && c.ResName != null && c.ResName.StartsWith("IDC_VIEW_", StringComparison.Ordinal))
                    {
                        var view = skin.Catalog.Screen("IDD_" + c.ResName.Substring(4));
                        if (view != null)
                        {
                            var built = Build(skin, view, options);
                            built.style.width = StyleKeyword.Null;
                            built.style.height = StyleKeyword.Null;
                            return built;
                        }
                        return Slot(c, options);
                    }
                    if (!string.IsNullOrEmpty(c.Image)) return new G3Image(c.Image);
                    return StaticLabel(skin, c, options);
                case "Button":
                {
                    var b = new G3Button(options.DesignTexts ? c.Text : null);
                    if (!string.IsNullOrEmpty(c.Image)) b.WithIcon(c.Image);
                    if (c.Font != null) G3Text.Font(b, c.Font);
                    else G3Text.Font(b, G3FontMap.ThemeButton); // no res font: the button plate's theme font
                    return b;
                }
                case "ProgressBar":
                {
                    var p = new VisualElement { pickingMode = PickingMode.Ignore };
                    p.AddToClassList(SlotClass);
                    var fill = new VisualElement { name = "fill", pickingMode = PickingMode.Ignore };
                    fill.AddToClassList(ThemeUssWriter.PartClass("progressbar_hor_progress"));
                    fill.AddToClassList("g3-fill-parent");
                    if (uint.TryParse(c.Attribute("ProgressBar.Color.Bar"), out uint argb))
                        fill.style.unityBackgroundImageTintColor = (Color)new Color32((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, 255);
                    p.Add(fill);
                    return p;
                }
                default:
                    return Slot(c, options);
            }
        }

        static VisualElement Slot(WindowData c, Options options)
        {
            var s = new VisualElement { pickingMode = PickingMode.Ignore };
            s.AddToClassList(SlotClass);
            s.AddToClassList(SlotClass + "--" + (c.Class ?? "window").ToLowerInvariant());
            if (options.OutlineSlots) s.AddToClassList(SlotClass + "--outlined");
            return s;
        }

        static Label StaticLabel(G3Skin skin, WindowData c, Options options)
        {
            var label = new Label(options.DesignTexts ? c.Text : string.Empty) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("g3-static");
            if (c.Font != null) G3Text.Font(label, c.Font);
            else G3Text.Font(label, G3FontMap.ThemeText); // no res font: the static_text theme font
            G3Text.Align(label, new StaticTextFormat(c.Style));
            label.style.color = SkinColor.TryParse(c.TextColor, out var tc)
                ? tc.ToColor()
                : skin.ThemeText("static_text", WidgetState.Normal, G3Palette.Label.ToColor());
            return label;
        }

        /// <summary>Absolute placement of <paramref name="e"/> from the window's reference rect and anchors.</summary>
        public static void Place(VisualElement e, WindowData w, int parentWidth, int parentHeight)
        {
            var a = (AnchorFlags)w.Anchor;
            e.style.position = Position.Absolute;
            Axis(GuiLayoutRules.Horizontal(w.Rect[0], w.Rect[2], parentWidth, a), true, e.style);
            Axis(GuiLayoutRules.Vertical(w.Rect[1], w.Rect[3], parentHeight, a), false, e.style);
        }

        static void Axis(AxisPlacement p, bool horizontal, IStyle s)
        {
            StyleLength start = StyleKeyword.Null, end = StyleKeyword.Null, size = StyleKeyword.Null, margin = StyleKeyword.Null;
            switch (p.Mode)
            {
                case AxisMode.Start: start = p.Start; size = p.Size; break;
                case AxisMode.End: end = p.End; size = p.Size; break;
                case AxisMode.Stretch: start = p.Start; end = p.End; break;
                case AxisMode.Center: start = Length.Percent(50); margin = p.CenterOffset; size = p.Size; break;
            }
            if (horizontal) { s.left = start; s.right = end; s.width = size; s.marginLeft = margin; }
            else { s.top = start; s.bottom = end; s.height = size; s.marginTop = margin; }
        }
    }
}
