using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// Window frame of the theme: <c>window_back</c> plus the eight 12 px bronze border pieces
    /// (<c>border_window_topleft</c> … <c>bottomright</c>). Used for generic framed boxes (tooltips, confirm dialogs
    /// without their own bitmap). <see cref="Content"/> receives the children.
    /// </summary>
    public class G3Frame : VisualElement
    {
        public VisualElement Content { get; }

        public G3Frame()
        {
            AddToClassList("g3-frame");
            AddToClassList(ThemeUssWriter.PartClass("window"));
            foreach (var piece in new[] { "top", "bottom", "left", "right", "topleft", "topright", "bottomleft", "bottomright" })
            {
                var p = new VisualElement { pickingMode = PickingMode.Ignore, name = piece };
                p.AddToClassList(ThemeUssWriter.PartClass("border_window_" + piece));
                p.AddToClassList("g3-frame__" + piece);
                Add(p);
            }
            Content = new VisualElement { name = "content" };
            Content.AddToClassList("g3-frame__content");
            Add(Content);
        }
    }

    /// <summary>Small text tooltip on the theme <c>tooltipctrl_main</c> box (used by the GUI toolkit for tab hints).</summary>
    public class G3Tooltip : G3Part
    {
        public Label Label { get; }

        public G3Tooltip(string text) : base("tooltipctrl_main")
        {
            AddToClassList("g3-tooltip");
            Label = new Label(text) { pickingMode = PickingMode.Ignore };
            Add(Label);
            G3Text.Font(Label, G3FontMap.ThemeText); // tooltipctrl_main: the theme text font
        }
    }

    /// <summary>
    /// Subtitle box (<c>IDD_VIEW_TALK_2</c>, 570×81, <c>G3_HUD_View_Talk_Back</c>): label 526×54, 14 px bold, gold,
    /// centred when the line fits on one line, otherwise left-aligned, wrapped and top-aligned. The speaker name is
    /// not shown. Lines are set <see cref="LineAdvance"/> apart (the res sample line of ~130 characters fills the
    /// label in the original; Gothic3's own 2.16 em line height would not fit two lines). A longer text grows the
    /// box downwards (the frame is 9-sliced) instead of clipping its first line.
    /// </summary>
    public class G3TalkBox : VisualElement
    {
        public const string Layout = "IDD_VIEW_TALK_2";
        /// <summary>Distance of wrapped subtitle lines in reference pixels (14 px font).</summary>
        public const int LineAdvance = 20;
        const int FrameSlice = 4;

        readonly VisualElement _view;
        readonly Label _label;
        readonly float _viewHeight, _labelHeight;
        string _text;

        public G3TalkBox(G3Skin skin)
        {
            AddToClassList("g3-talk");
            pickingMode = PickingMode.Ignore;
            var screen = skin.Catalog.Screen(Layout);
            _view = screen == null ? null : G3LayoutBuilder.Build(skin, screen);
            if (_view == null) return;
            _viewHeight = screen.Height;
            style.width = _view.style.width;
            style.height = _view.style.height;
            _view.style.position = Position.Absolute;
            var background = _view.Q("background");
            if (background != null)
            {
                background.style.unitySliceLeft = background.style.unitySliceRight = FrameSlice;
                background.style.unitySliceTop = background.style.unitySliceBottom = FrameSlice;
            }
            Add(_view);
            _label = _view.Q<Label>("IDC_LABEL_TALK_2");
            if (_label == null) return;
            _labelHeight = _label.userData is WindowData w ? w.Rect[3] - w.Rect[1] : 54;
            _label.RegisterCallback<GeometryChangedEvent>(_ => Realign());
        }

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                if (_label == null) return;
                _label.text = string.IsNullOrEmpty(value) ? string.Empty : $"<line-height={LineAdvance}>{value}";
                Realign();
            }
        }

        void Realign()
        {
            float width = _label.resolvedStyle.width;
            if (float.IsNaN(width) || width <= 0) return;
            // the label wraps even when measured with an undefined width: compare the wrapped height with one line
            float height = _label.MeasureTextSize(_label.text, width, MeasureMode.Exactly, 0, MeasureMode.Undefined).y;
            float line = _label.MeasureTextSize("Ag", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).y;
            bool fits = height < line * 1.5f;
            _label.style.unityTextAlign = fits ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft;
            float grow = Mathf.Max(0f, Mathf.Ceil(height - _labelHeight));
            _label.style.height = _labelHeight + grow;
            _view.style.height = _viewHeight + grow;
            style.height = _viewHeight + grow;
        }
    }
}
