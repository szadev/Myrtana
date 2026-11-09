using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>How a HUD part appears (Win32 AnimateWindow flags G3 uses).</summary>
    public enum G3FadeKind
    {
        /// <summary>AW_BLEND: alpha fade (pages, compass, log, crosshair).</summary>
        Blend,
        /// <summary>AW_CENTER: grows out of / shrinks into its centre (status bars, quick slots, focus bar, sidebar, subtitles).</summary>
        Center,
    }

    /// <summary>
    /// Shows and hides an element over G3's 200 ms window animation (gCHUDPage::Open/Close, gCGUIManager::OnPageChanged)
    /// on real time, so it also runs while the game is paused. While it fades out the element still draws but takes no
    /// clicks (a transparent blocker covers it), and it is display:none once gone.
    /// </summary>
    public sealed class G3Fade
    {
        public const float Seconds = 0.2f;

        readonly VisualElement _element;
        readonly G3FadeKind _kind;
        VisualElement _blocker;
        IVisualElementScheduledItem _job;
        float _t;          // 0 hidden … 1 shown
        double _lastTime;

        public G3Fade(VisualElement element, G3FadeKind kind = G3FadeKind.Blend, bool shown = false)
        {
            _element = element;
            _kind = kind;
            Shown = shown;
            _t = shown ? 1f : 0f;
            Apply();
        }

        /// <summary>The state it is going to (true while fading in or shown).</summary>
        public bool Shown { get; private set; }

        /// <summary>Still drawn (shown, or fading in or out).</summary>
        public bool Visible => _t > 0f;

        /// <summary>Fades in or out; <paramref name="instant"/> jumps to the end.</summary>
        public void Show(bool show, bool instant = false)
        {
            if (show == Shown && (!instant || _t == (show ? 1f : 0f))) return;
            Shown = show;
            if (instant || _element.panel == null)
            {
                _t = show ? 1f : 0f;
                _job?.Pause();
                Apply();
                return;
            }
            _lastTime = Time.realtimeSinceStartupAsDouble;
            Apply();
            if (_job == null) _job = _element.schedule.Execute(Step).Every(0);
            else _job.Resume();
        }

        void Step()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            float dt = (float)(now - _lastTime);
            _lastTime = now;
            _t = Mathf.MoveTowards(_t, Shown ? 1f : 0f, dt / Seconds);
            Apply();
            if (_t == (Shown ? 1f : 0f)) _job.Pause();
        }

        void Apply()
        {
            _element.style.display = _t > 0f || Shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (_kind == G3FadeKind.Blend) _element.style.opacity = _t >= 1f ? StyleKeyword.Null : _t;
            else _element.style.scale = _t >= 1f ? StyleKeyword.Null : new Scale(new Vector3(_t, _t, 1f));
            bool fadingOut = !Shown && _t > 0f;
            if (fadingOut && _blocker == null)
            {
                _blocker = new VisualElement { name = "fade-blocker", pickingMode = PickingMode.Position };
                _blocker.style.position = Position.Absolute;
                _blocker.style.left = _blocker.style.top = _blocker.style.right = _blocker.style.bottom = 0;
            }
            if (_blocker == null) return;
            if (fadingOut && _blocker.parent != _element) _element.Add(_blocker);
            else if (fadingOut) _blocker.BringToFront();
            else if (_blocker.parent != null) _blocker.RemoveFromHierarchy();
        }
    }
}
