using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Skin
{
    /// <summary>
    /// Text styling helpers: G3 font sizes/styles, alignment from Static style bits, shadows. Sizes are res font sizes
    /// (reference pixels, the em of the original's Times-sized res fonts); the skin scales the Gothic3 face to match
    /// (<see cref="G3FontMetrics"/>). Fonts the original creates in code with a cell height convert with
    /// <see cref="CellFont"/>.
    /// </summary>
    public static class G3Text
    {
        public static void Font(VisualElement e, float size, bool bold = false, bool italic = false)
        {
            e.style.fontSize = size;
            e.style.unityFontStyleAndWeight = bold && italic ? FontStyle.BoldAndItalic : bold ? FontStyle.Bold : italic ? FontStyle.Italic : FontStyle.Normal;
        }

        public static void Font(VisualElement e, FontData f)
        {
            if (f != null) Font(e, f.Size, f.Bold, f.Italic);
        }

        /// <summary>An original font from the central mapping (<see cref="G3FontMap"/>): res/theme em or code cell height.</summary>
        public static void Font(VisualElement e, G3Font f)
        {
            if (f != null) Font(e, f.PortSize, f.Bold, f.Italic);
        }

        /// <summary>
        /// A multi-line res text with the original's line distance (<see cref="G3FontMetrics.ParagraphLineHeight"/>) at res
        /// size <paramref name="size"/>, as rich text for a <see cref="TextElement"/>.
        /// </summary>
        public static string Paragraph(string text, float size) =>
            string.IsNullOrEmpty(text) ? string.Empty : $"<line-height={G3FontMetrics.ParagraphLineHeight(size)}px>{text}";

        /// <summary>A font the original creates in code with a positive LOGFONT (cell) height, e.g. the 24 px focus name.</summary>
        public static void CellFont(VisualElement e, int cellHeight, bool bold = false) =>
            Font(e, G3FontMetrics.FromCellHeight(cellHeight), bold);

        public static void Align(VisualElement e, StaticTextFormat f)
        {
            bool middle = f.VerticalCenter || f.SingleLine;
            e.style.unityTextAlign = f.Align == TextAlign.Center
                ? (middle ? TextAnchor.MiddleCenter : TextAnchor.UpperCenter)
                : f.Align == TextAlign.Right
                    ? (middle ? TextAnchor.MiddleRight : TextAnchor.UpperRight)
                    : (middle ? TextAnchor.MiddleLeft : TextAnchor.UpperLeft);
            e.style.whiteSpace = f.SingleLine ? WhiteSpace.NoWrap : WhiteSpace.Normal;
        }

        /// <summary>The black 1-px drop shadow G3 draws under tile and table texts.</summary>
        public static void Shadow(VisualElement e)
        {
            e.style.textShadow = new TextShadow { offset = new Vector2(1, 1), blurRadius = 0, color = UnityEngine.Color.black };
        }

        /// <summary>One text line of the scaled Gothic3 face at res size <paramref name="size"/> (row height of lists and tables).</summary>
        public static float LineHeight(float size) => Mathf.Ceil(G3FontMetrics.LineHeight(size));

        public static void Color(VisualElement e, SkinColor c) => e.style.color = c.ToColor();

        /// <summary>
        /// Draws a single-line text smaller (down to <paramref name="minSize"/>) when it is wider than
        /// <paramref name="maxWidth"/> at <paramref name="size"/> — the G3 layouts are sized for the short German texts.
        /// Call once the element is in a panel (it measures with the resolved font).
        /// </summary>
        public static void FitWidth(TextElement e, float maxWidth, float size, float minSize = 11)
        {
            e.style.fontSize = size;
            float width = e.MeasureTextSize(e.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            if (width > maxWidth && width > 0) e.style.fontSize = Mathf.Max(minSize, size * maxWidth / width);
        }

        /// <summary>Width of a text element's current text in its current style.</summary>
        public static float TextWidth(TextElement e) =>
            e.MeasureTextSize(e.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
    }
}
