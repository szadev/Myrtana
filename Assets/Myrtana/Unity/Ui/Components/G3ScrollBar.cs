using System;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// The vertical G3 scroll bar of a list (theme parts <c>scrollbar_ver_track/thumb/arrow_up/arrow_down</c>), in rows:
    /// 17 px wide, square arrows at both ends, thumb sized and placed by <see cref="G3ScrollRules"/> (hidden while
    /// everything fits; the track and arrows stay, as in G3's tile lists). An arrow scrolls one row (repeating while held),
    /// a click in the track one page, the thumb can be dragged. The owner sets <see cref="Set"/> and listens to
    /// <see cref="Scrolled"/>.
    /// </summary>
    public class G3ScrollBar : VisualElement
    {
        const long RepeatDelayMs = 330, RepeatMs = 50;

        readonly int _length;
        readonly G3Part _track, _thumb, _up, _down;
        IVisualElementScheduledItem _repeat;
        int _dragPointer = -1;
        float _dragGrab;

        public int Range { get; private set; }
        public int Page { get; private set; }
        public int Position { get; private set; }
        public ScrollThumb Thumb { get; private set; }

        /// <summary>The user moved the bar (new first row).</summary>
        public event Action<int> Scrolled;

        /// <param name="length">Height of the bar (reference px).</param>
        public G3ScrollBar(int length)
        {
            _length = length;
            AddToClassList("g3-scrollbar");
            style.position = UnityEngine.UIElements.Position.Absolute;
            style.width = G3ScrollRules.Width;
            style.height = length;
            style.top = 0;
            style.right = 0;
            pickingMode = PickingMode.Position;

            _track = Part("scrollbar_ver_track", 0, length);
            _track.RegisterCallback<PointerDownEvent>(OnTrackDown);
            _up = Part("scrollbar_ver_arrow_up", 0, G3ScrollRules.Width);
            _down = Part("scrollbar_ver_arrow_down", length - G3ScrollRules.Width, G3ScrollRules.Width);
            HoldRepeat(_up, -1);
            HoldRepeat(_down, +1);
            _thumb = Part("scrollbar_ver_thumb", 0, 0);
            _thumb.RegisterCallback<PointerDownEvent>(OnThumbDown);
            _thumb.RegisterCallback<PointerMoveEvent>(OnThumbMove);
            _thumb.RegisterCallback<PointerUpEvent>(OnThumbUp);
            _thumb.RegisterCallback<PointerCaptureOutEvent>(_ => _dragPointer = -1);
            Set(0, 0, 0);
        }

        G3Part Part(string part, int top, int height)
        {
            var p = new G3Part(part) { pickingMode = PickingMode.Position };
            p.style.position = UnityEngine.UIElements.Position.Absolute;
            p.style.left = 0;
            p.style.width = G3ScrollRules.Width;
            p.style.top = top;
            p.style.height = height;
            Add(p);
            return p;
        }

        /// <summary>Range = rows of items, page = visible rows, position = first visible row (no event).</summary>
        public void Set(int range, int page, int position)
        {
            Range = Math.Max(0, range);
            Page = Math.Max(0, page);
            Position = G3ScrollRules.Clamp(position, Range, Page);
            Thumb = G3ScrollRules.Thumb(_length, Range, Page, Position);
            _thumb.style.display = Thumb.Visible ? DisplayStyle.Flex : DisplayStyle.None;
            _thumb.style.top = Thumb.Offset;
            _thumb.style.height = Thumb.Length;
        }

        /// <summary>Moves to a row as the user would (clamped; raises <see cref="Scrolled"/> when it changes).</summary>
        public void ScrollTo(int position)
        {
            int p = G3ScrollRules.Clamp(position, Range, Page);
            if (p == Position) return;
            Set(Range, Page, p);
            Scrolled?.Invoke(p);
        }

        void HoldRepeat(VisualElement arrow, int step)
        {
            arrow.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                ScrollTo(Position + step);
                _repeat?.Pause();
                _repeat = schedule.Execute(() => ScrollTo(Position + step)).StartingIn(RepeatDelayMs).Every(RepeatMs);
                e.StopPropagation();
            });
            arrow.RegisterCallback<PointerUpEvent>(_ => _repeat?.Pause());
            arrow.RegisterCallback<PointerLeaveEvent>(_ => _repeat?.Pause());
        }

        void OnTrackDown(PointerDownEvent e)
        {
            if (e.button != 0 || !Thumb.Visible) return;
            float y = e.localPosition.y;
            if (y < Thumb.Offset) ScrollTo(Position - Math.Max(1, Page));
            else if (y > Thumb.Offset + Thumb.Length) ScrollTo(Position + Math.Max(1, Page));
            e.StopPropagation();
        }

        void OnThumbDown(PointerDownEvent e)
        {
            if (e.button != 0) return;
            _dragPointer = e.pointerId;
            _dragGrab = e.localPosition.y;
            _thumb.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        void OnThumbMove(PointerMoveEvent e)
        {
            if (e.pointerId != _dragPointer) return;
            // thumb-local y → bar-local start of the thumb
            float top = this.WorldToLocal(_thumb.LocalToWorld(new Vector2(0, e.localPosition.y))).y - _dragGrab;
            ScrollTo(G3ScrollRules.PositionAt(_length, Range, Page, Mathf.RoundToInt(top)));
            e.StopPropagation();
        }

        void OnThumbUp(PointerUpEvent e)
        {
            if (e.pointerId != _dragPointer) return;
            _dragPointer = -1;
            _thumb.ReleasePointer(e.pointerId);
            e.StopPropagation();
        }
    }
}
