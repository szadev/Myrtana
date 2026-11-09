using System.Collections.Generic;
using G3.Player;
using G3.Unity;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// gamepad-1 (Docs/gamepad.md §5): one pad button drawn without font or texture — a dark disc or rounded box with the
    /// family's name in it; PlayStation face buttons as their shapes (cross, circle, square, triangle, Painter2D).
    /// </summary>
    public sealed class G3PadGlyph : VisualElement
    {
        const float Size = 26f;
        readonly Label _label;
        PadButton _button;
        PadFamily _family;

        public G3PadGlyph()
        {
            pickingMode = PickingMode.Ignore;
            style.height = Size;
            style.minWidth = Size;
            style.justifyContent = Justify.Center;
            style.alignItems = Align.Center;
            style.backgroundColor = new Color(0.08f, 0.07f, 0.06f, 0.9f);
            style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 2f;
            _label = new Label { pickingMode = PickingMode.Ignore };
            _label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _label.style.color = new Color(0.92f, 0.9f, 0.85f);
            _label.style.paddingLeft = _label.style.paddingRight = 4;
            G3Text.Font(_label, 14, bold: true);
            Add(_label);
            generateVisualContent += Draw;
        }

        bool Shape => _family == PadFamily.PlayStation && _button >= PadButton.A && _button <= PadButton.Y;
        bool Round => _button >= PadButton.A && _button <= PadButton.Y || _button == PadButton.L3 || _button == PadButton.R3;

        public void Set(PadButton button, PadFamily family)
        {
            if (button == _button && family == _family) return;
            _button = button;
            _family = family;
            var (r, g, b) = PadGlyphs.FaceColour(button, family);
            var c = new Color(r, g, b);
            style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor = c;
            float radius = Round ? Size * 0.5f : 5f;
            style.borderTopLeftRadius = style.borderTopRightRadius = style.borderBottomLeftRadius = style.borderBottomRightRadius = radius;
            _label.text = Shape ? "" : Short(button, family);
            _label.style.color = button >= PadButton.A && button <= PadButton.Y && family != PadFamily.Generic ? c : new Color(0.92f, 0.9f, 0.85f);
            style.width = Round ? Size : StyleKeyword.Auto;
            MarkDirtyRepaint();
        }

        /// <summary>The text inside the glyph (stick and d-pad names shortened).</summary>
        static string Short(PadButton b, PadFamily f)
        {
            switch (b)
            {
                case PadButton.DUp: case PadButton.DDown: case PadButton.DLeft: case PadButton.DRight: return "D-pad";
                case PadButton.LsUp: case PadButton.LsDown: case PadButton.LsLeft: case PadButton.LsRight: return f == PadFamily.PlayStation ? "L" : "LS";
                case PadButton.RsUp: case PadButton.RsDown: case PadButton.RsLeft: case PadButton.RsRight: return f == PadFamily.PlayStation ? "R" : "RS";
                default: return PadGlyphs.Name(b, f);
            }
        }

        void Draw(MeshGenerationContext ctx)
        {
            if (!Shape) return;
            var p = ctx.painter2D;
            var (r, g, b) = PadGlyphs.FaceColour(_button, _family);
            p.strokeColor = new Color(r, g, b);
            p.lineWidth = 2.2f;
            var rect = contentRect;
            var c = rect.center;
            float s = Mathf.Min(rect.width, rect.height) * 0.26f;
            p.BeginPath();
            switch (_button)
            {
                case PadButton.A: // cross
                    p.MoveTo(c + new Vector2(-s, -s));
                    p.LineTo(c + new Vector2(s, s));
                    p.MoveTo(c + new Vector2(s, -s));
                    p.LineTo(c + new Vector2(-s, s));
                    break;
                case PadButton.B: // circle
                    p.Arc(c, s, Angle.Degrees(0f), Angle.Degrees(360f));
                    break;
                case PadButton.X: // square
                    p.MoveTo(c + new Vector2(-s, -s));
                    p.LineTo(c + new Vector2(s, -s));
                    p.LineTo(c + new Vector2(s, s));
                    p.LineTo(c + new Vector2(-s, s));
                    p.ClosePath();
                    break;
                case PadButton.Y: // triangle
                    p.MoveTo(c + new Vector2(0f, -s * 1.1f));
                    p.LineTo(c + new Vector2(s * 1.05f, s * 0.8f));
                    p.LineTo(c + new Vector2(-s * 1.05f, s * 0.8f));
                    p.ClosePath();
                    break;
            }
            p.Stroke();
        }
    }

    /// <summary>
    /// gamepad-1: the pad hint bar at the bottom of a page or the menu (<see cref="PadHints"/>): glyph + action per entry,
    /// shown only while the pad is the device in use.
    /// </summary>
    public sealed class G3PadHintBar : VisualElement
    {
        readonly List<(G3PadGlyph glyph, Label text)> _items = new List<(G3PadGlyph, Label)>();
        PadHintPage? _page;
        PadFamily _family;
        bool _russian;

        public G3PadHintBar()
        {
            name = "g3-pad-hints";
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.bottom = 6;
            style.flexDirection = FlexDirection.Row;
            style.justifyContent = Justify.Center;
            style.alignItems = Align.Center;
            style.display = DisplayStyle.None;
        }

        /// <summary>Per frame: shows the entries of <paramref name="page"/> while the pad is in use.</summary>
        public void Tick(PadHintPage page, string language)
        {
            bool show = G3Pad.PadIsLast && G3Pad.Connected;
            style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;
            bool ru = language == "Russian";
            var family = G3Pad.Family;
            if (_page == page && family == _family && ru == _russian) return;
            _page = page;
            _family = family;
            _russian = ru;
            var hints = PadHints.For(page);
            while (_items.Count < hints.Count)
            {
                var glyph = new G3PadGlyph();
                var text = new Label { pickingMode = PickingMode.Ignore };
                G3Text.Font(text, 16, bold: true);
                text.style.color = new Color(0.92f, 0.88f, 0.78f);
                text.style.marginLeft = 5;
                text.style.marginRight = 18;
                text.style.unityTextAlign = TextAnchor.MiddleLeft;
                Add(glyph);
                Add(text);
                _items.Add((glyph, text));
            }
            for (int i = 0; i < _items.Count; i++)
            {
                bool used = i < hints.Count;
                _items[i].glyph.style.display = _items[i].text.style.display = used ? DisplayStyle.Flex : DisplayStyle.None;
                if (!used) continue;
                _items[i].glyph.Set(hints[i].button, family);
                _items[i].text.text = ru ? hints[i].ru : hints[i].en;
            }
            BringToFront();
        }
    }
}
