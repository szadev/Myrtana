using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// HUD status bar (HP, SP, MP and the focus bar; Docs/formats-gui.md §6.3): 201×19 <c>G3_HUD_StatusBar_Back</c>,
    /// fill at 12,4 (176×12) = theme <c>progressbar_hor_progress</c> (white gradient) tinted per bar, no track; value
    /// changes animate over 0.5 s.
    /// </summary>
    public class G3StatusBar : VisualElement
    {
        public const string Background = "G3_HUD_StatusBar_Back.tga";
        public const int Width = 201, Height = 19;
        static readonly RectInt FillRect = new RectInt(12, 4, 176, 12);

        readonly VisualElement _fill;
        readonly StatusBarKind _kind;
        float _shown, _from, _target, _t = 1f;
        IVisualElementScheduledItem _anim;
        bool _poisoned, _exhausted;

        public G3StatusBar(StatusBarKind kind)
        {
            _kind = kind;
            AddToClassList("g3-statusbar");
            pickingMode = PickingMode.Ignore;
            style.width = Width;
            style.height = Height;
            var back = new G3Image(Background);
            back.AddToClassList("g3-fill-parent");
            Add(back);

            var track = new VisualElement { pickingMode = PickingMode.Ignore };
            track.style.position = Position.Absolute;
            track.style.left = FillRect.x;
            track.style.top = FillRect.y;
            track.style.width = FillRect.width;
            track.style.height = FillRect.height;
            track.style.overflow = Overflow.Hidden;
            Add(track);

            _fill = new VisualElement { pickingMode = PickingMode.Ignore };
            _fill.AddToClassList(ThemeUssWriter.PartClass("progressbar_hor_progress"));
            _fill.AddToClassList("g3-statusbar__fill");
            _fill.style.height = Length.Percent(100);
            track.Add(_fill);
            UpdateTint();
            Apply(0);
        }

        public bool Poisoned { get => _poisoned; set { if (_poisoned == value) return; _poisoned = value; UpdateTint(); } }
        public bool Exhausted { get => _exhausted; set { if (_exhausted == value) return; _exhausted = value; UpdateTint(); } }

        /// <summary>Sets the value; animates over 0.5 s unless <paramref name="immediate"/>.</summary>
        public void SetValue(int current, int maximum, bool immediate = false)
        {
            _target = StatusBarColors.Fraction(current, maximum);
            if (immediate || panel == null)
            {
                _t = 1f;
                Apply(_target);
                return;
            }
            _from = _shown;
            _t = 0f;
            _anim ??= schedule.Execute(Step).Every(16);
            _anim.Resume();
        }

        void Step(TimerState ts)
        {
            _t = Mathf.Min(1f, _t + ts.deltaTime / 1000f / StatusBarColors.AnimationSeconds);
            Apply(Mathf.Lerp(_from, _target, _t));
            if (_t >= 1f) _anim.Pause();
        }

        void Apply(float fraction)
        {
            _shown = fraction;
            _fill.style.width = Length.Percent(fraction * 100f);
        }

        void UpdateTint() =>
            _fill.style.unityBackgroundImageTintColor = StatusBarColors.Fill(_kind, _poisoned, _exhausted).ToColor();
    }
}
