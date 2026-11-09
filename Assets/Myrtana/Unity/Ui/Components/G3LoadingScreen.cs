using System;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// The G3 loading screen <c>IDD_MENU_LOADING_2</c> (Docs/formats-gui.md §6.12): <c>G3_HUD_View_Loading_Back</c>
    /// (1024×768), one of the shuffled <c>G3_HUD_Slide_NN</c> at 288,320 (356×264) and the progress bar at 254,623
    /// (592×20) with <c>G3_HUD_Icon_Misc_Progress_Loading</c> stretched over the filled part. No text. The original
    /// stretches the whole screen to the window; here the 4:3 picture keeps its aspect, centred, and wider panels show
    /// the same picture blurred and darkened at the sides, with a soft shadow along the picture's edges.
    /// </summary>
    public class G3LoadingScreen : VisualElement
    {
        public const string Background = "G3_HUD_View_Loading_Back.tga";
        public const string BarImage = "G3_HUD_Icon_Misc_Progress_Loading.tga";
        public const string TrackImage = "G3_HUD_View_Empty.tga";
        const int Width = 1024, Height = 768;
        static readonly RectInt SlideRect = new RectInt(288, 320, 356, 264);
        static readonly RectInt BarRect = new RectInt(254, 623, 592, 20);

        readonly VisualElement _frame;
        readonly G3Image _slide;
        readonly VisualElement _fill;
        readonly int _slideCount;
        LoadingSlideShow _show;

        public G3LoadingScreen()
        {
            AddToClassList("g3-loading");
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = style.top = style.right = style.bottom = 0;
            style.backgroundColor = Color.black;

            // Wider panels: the same picture, blurred and darkened, covers the sides; a soft shadow keeps the border crisp.
            var back = new G3Image(Background);
            var sides = Fill(new VisualElement { pickingMode = PickingMode.Ignore });
            var blurred = G3Backdrop.Blurred(G3Skin.Load()?.Sprite(Background), G3Backdrop.SideBrightness);
            if (blurred != null)
            {
                sides.style.backgroundImage = blurred;
                sides.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100)); // stretched, like the original
            }
            Add(sides);

            _frame = Placed(this, new RectInt(0, 0, Width, Height));
            _frame.style.left = Length.Percent(50);
            _frame.style.translate = new Translate(Length.Percent(-50), 0);
            _frame.style.transformOrigin = new TransformOrigin(Length.Percent(50), 0);
            G3Backdrop.AddEdgeShadows(_frame, Width, Height);
            _frame.Add(Fill(back));

            _slide = new G3Image();
            Placed(_frame, SlideRect).Add(Fill(_slide));

            var bar = Placed(_frame, BarRect);
            bar.style.overflow = Overflow.Hidden;
            bar.Add(Fill(new G3Image(TrackImage)));
            _fill = new G3Image(BarImage);
            _fill.style.position = Position.Absolute;
            _fill.style.left = _fill.style.top = _fill.style.bottom = 0;
            _fill.style.width = 0;
            bar.Add(_fill);

            _slideCount = CountSlides();
            RegisterCallback<GeometryChangedEvent>(_ => Fit());
            Begin(Environment.TickCount);
        }

        /// <summary>A new load: new slide order, empty bar.</summary>
        public void Begin(int seed)
        {
            _show = new LoadingSlideShow(_slideCount, new System.Random(seed));
            _slide.Image = _show.Current >= 0 ? LoadingSlideShow.SlideName(_show.Current) : null;
            _fill.style.width = 0;
        }

        /// <summary>0…1; the bar and the slide only move forward.</summary>
        public void SetProgress(float progress)
        {
            if (_show.Advance(progress)) _slide.Image = LoadingSlideShow.SlideName(_show.Current);
            _fill.style.width = Length.Percent(_show.Position);
        }

        /// <summary>
        /// The picture fills the panel height (or its width when narrower than 4:3), centred: the original stretches the
        /// loading screen to the window, independent of the GUI size.
        /// </summary>
        void Fit()
        {
            float w = layout.width, h = layout.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0 || h <= 0) return;
            float s = Mathf.Min(w / Width, h / Height);
            _frame.style.scale = new Scale(new Vector3(s, s, 1f));
            _frame.style.top = (h - Height * s) / 2;
        }

        /// <summary>Slides are probed 00, 01 … until one is missing, as the original does (CP: 00–29).</summary>
        static int CountSlides()
        {
            var skin = G3Skin.Load();
            int n = 0;
            while (skin != null && n < 100 && skin.Sprite(LoadingSlideShow.SlideName(n)) != null) n++;
            return n;
        }

        static VisualElement Placed(VisualElement parent, RectInt r)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.style.position = Position.Absolute;
            e.style.left = r.x;
            e.style.top = r.y;
            e.style.width = r.width;
            e.style.height = r.height;
            parent.Add(e);
            return e;
        }

        static VisualElement Fill(VisualElement e)
        {
            e.AddToClassList("g3-fill-parent");
            return e;
        }
    }
}
