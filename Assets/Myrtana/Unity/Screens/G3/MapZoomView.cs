using System;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-225 [owner, QoL — G3's maps have no zoom or pan; Docs/map-teleport.md §1.2]: a map view that zooms and pans.
    /// <see cref="Viewport"/> (filling its slot, clipping) holds <see cref="Content"/>, which the zoom sizes to
    /// zoom × the viewport and moves by the pan (<see cref="MapZoom"/>). Everything inside the content placed by percent
    /// of it (the map picture, markers, labels, the hero arrow, the port's marks) follows the zoom; icons and text keep
    /// their pixel size, so they stay crisp. Input: mouse wheel zooms around the cursor (eased), a drag with the left or
    /// right button pans (a press that does not move stays a click: the quest log's place buttons), a double-click on
    /// the map resets, +/− zoom around the centre, Home / 0 resets. Gamepad (gamepad-1): <see cref="TickPad"/>
    /// (LT/RT zoom, right stick pan, R3 reset) and <see cref="Reveal"/> (keeps the pad's focus in view).
    /// </summary>
    public sealed class MapZoomView
    {
        /// <summary>Pointer travel (view px) before a press becomes a drag.</summary>
        const float DragThreshold = 4f;

        readonly MapZoom _zoom = new MapZoom();
        int _pointer = -1, _button = -1;
        bool _dragging;
        Vector2 _pressAt, _last;

        public MapZoomView(VisualElement slot)
        {
            Viewport = new VisualElement { name = "map-zoom-viewport", pickingMode = PickingMode.Position };
            Viewport.AddToClassList("g3-fill-parent");
            Viewport.style.overflow = Overflow.Hidden;
            Content = new VisualElement { name = "map-zoom-content", pickingMode = PickingMode.Ignore };
            Content.style.position = Position.Absolute;
            Content.style.left = Content.style.top = 0f;
            Content.style.width = Content.style.height = Length.Percent(100f);
            Viewport.Add(Content);
            slot?.Add(Viewport);

            Viewport.RegisterCallback<GeometryChangedEvent>(_ => Resize());
            Viewport.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            Viewport.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            Viewport.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
            Viewport.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            Viewport.RegisterCallback<PointerCaptureOutEvent>(e =>
            {
                if (e.target == Viewport) EndDrag(); // not a place button's capture taken by the drag
            });
            Content.RegisterCallback<GeometryChangedEvent>(_ => Changed?.Invoke());
        }

        /// <summary>The clipping element filling the map slot.</summary>
        public VisualElement Viewport { get; }

        /// <summary>The zoomed and panned map content (add the map's picture and layers here).</summary>
        public VisualElement Content { get; }

        /// <summary>Current zoom (1 = the whole map; the dev probe <c>mapzoom</c>).</summary>
        public float Zoom => _zoom.Zoom;

        /// <summary>The zoom state (tests, probes).</summary>
        public MapZoom State => _zoom;

        /// <summary>Raised after the content's laid-out size or place changed (marks grouped in view pixels re-place).</summary>
        public event Action Changed;

        /// <summary>Zoom <paramref name="steps"/> steps in (+) or out (−) around the view's centre (keys, gamepad).</summary>
        public void ZoomStep(int steps)
        {
            if (steps == 0) return;
            _zoom.StepAtCentre(steps);
        }

        /// <summary>Zoom around a viewport point (local px).</summary>
        public void ZoomStepAt(int steps, Vector2 viewPoint) => _zoom.StepAt(steps, viewPoint.x, viewPoint.y);

        /// <summary>Pans the map by view pixels (a stick: pass the per-frame step; positive moves the content right/down).</summary>
        public void Pan(Vector2 viewPixels)
        {
            _zoom.PanBy(viewPixels.x, viewPixels.y);
            Apply();
        }

        /// <summary>Back to the whole map (eased; <paramref name="immediate"/> for a fresh open).</summary>
        public void ResetZoom(bool immediate = false)
        {
            EndDrag();
            _zoom.Reset(immediate);
            Apply();
        }

        /// <summary>Per frame while the page is open: the easing and the keyboard (+/−, Home/0).</summary>
        public void Tick(Keyboard keyboard)
        {
            if (keyboard != null && Viewport.resolvedStyle.display != DisplayStyle.None)
            {
                if (keyboard.equalsKey.wasPressedThisFrame || keyboard.numpadPlusKey.wasPressedThisFrame) ZoomStep(1);
                if (keyboard.minusKey.wasPressedThisFrame || keyboard.numpadMinusKey.wasPressedThisFrame) ZoomStep(-1);
                if (keyboard.homeKey.wasPressedThisFrame || keyboard.digit0Key.wasPressedThisFrame
                    || keyboard.numpad0Key.wasPressedThisFrame) ResetZoom();
            }
            if (_zoom.Tick(Time.unscaledDeltaTime)) Apply();
        }

        /// <summary>
        /// gamepad-1 (Docs/gamepad.md §4): per frame while the page is open — RT zooms in, LT out (around
        /// <paramref name="anchor"/>, a viewport point, else the centre), the right stick pans while zoomed, R3 resets.
        /// True when the right stick was taken for the pan (else the page may use it, e.g. to scroll a text).
        /// </summary>
        public bool TickPad(Vector2? anchor = null)
        {
            if (Viewport.resolvedStyle.display == DisplayStyle.None || !G3.Unity.G3Pad.Connected) return false;
            int steps = (G3.Unity.G3Pad.Ui(G3.Player.UiCommand.PaneNext) ? 1 : 0) - (G3.Unity.G3Pad.Ui(G3.Player.UiCommand.PanePrev) ? 1 : 0);
            if (steps != 0)
            {
                if (anchor is Vector2 at) ZoomStepAt(steps, at);
                else ZoomStep(steps);
            }
            if (G3.Unity.G3Pad.Pressed(G3.Player.PadButton.R3)) ResetZoom();
            if (!_zoom.IsZoomed) return false;
            var stick = G3.Unity.G3Pad.RightStick;
            if (stick == Vector2.zero) return true;
            var (dx, dy) = MapZoom.StickPan(stick.x, stick.y, Time.unscaledDeltaTime);
            Pan(new Vector2(dx, dy));
            return true;
        }

        /// <summary>gamepad-1: pans so that <paramref name="rect"/> (viewport px) lies inside the view (with a margin).</summary>
        public void Reveal(Rect rect, float margin = 8f)
        {
            if (!_zoom.IsZoomed || float.IsNaN(rect.x) || float.IsNaN(rect.y)) return;
            float w = _zoom.ViewWidth, h = _zoom.ViewHeight;
            float dx = rect.xMin < margin ? margin - rect.xMin : rect.xMax > w - margin ? w - margin - rect.xMax : 0f;
            float dy = rect.yMin < margin ? margin - rect.yMin : rect.yMax > h - margin ? h - margin - rect.yMax : 0f;
            if (dx != 0f || dy != 0f) Pan(new Vector2(dx, dy));
        }

        void Resize()
        {
            var size = Viewport.contentRect.size;
            if (!(size.x > 1f) || !(size.y > 1f)) return;
            _zoom.SetView(size.x, size.y);
            Apply();
        }

        void Apply()
        {
            if (_zoom.IsZoomed || _zoom.Animating)
            {
                Content.style.left = _zoom.OffsetX;
                Content.style.top = _zoom.OffsetY;
                Content.style.width = _zoom.ContentWidth;
                Content.style.height = _zoom.ContentHeight;
            }
            else
            {
                // the whole map: follow the slot exactly (no rounding drift from cached sizes)
                Content.style.left = Content.style.top = 0f;
                Content.style.width = Content.style.height = Length.Percent(100f);
            }
        }

        Vector2 Local(Vector2 panelPosition) => Viewport.WorldToLocal(panelPosition);

        void OnWheel(WheelEvent e)
        {
            if (Mathf.Approximately(e.delta.y, 0f)) return;
            var at = Local(e.mousePosition);
            _zoom.StepAt(e.delta.y < 0f ? 1 : -1, at.x, at.y);
            e.StopPropagation();
        }

        void OnPointerDown(PointerDownEvent e)
        {
            if (e.button != 0 && e.button != 1) return;
            if (e.button == 0 && e.clickCount == 2 && e.target == Viewport)
            {
                ResetZoom();
                e.StopPropagation();
                return;
            }
            if (_pointer >= 0) return;
            _pointer = e.pointerId;
            _button = e.button;
            _dragging = false;
            _pressAt = _last = Local(e.position);
        }

        void OnPointerMove(PointerMoveEvent e)
        {
            if (e.pointerId != _pointer) return;
            if ((e.pressedButtons & (1 << _button)) == 0)
            {
                EndDrag(); // released outside the panel
                return;
            }
            var at = Local(e.position);
            if (!_dragging)
            {
                if ((at - _pressAt).sqrMagnitude < DragThreshold * DragThreshold) return;
                if (!_zoom.IsZoomed) return; // nothing to pan at 1×: leave the press to the buttons
                _dragging = true;
                Viewport.CapturePointer(_pointer); // a drag is no click on a place button
            }
            var delta = at - _last;
            _last = at;
            _zoom.PanBy(delta.x, delta.y);
            Apply();
            e.StopPropagation();
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (e.pointerId != _pointer || e.button != _button) return;
            bool dragged = _dragging;
            EndDrag();
            if (dragged) e.StopPropagation();
        }

        void EndDrag()
        {
            if (_pointer >= 0 && Viewport.HasPointerCapture(_pointer)) Viewport.ReleasePointer(_pointer);
            _pointer = _button = -1;
            _dragging = false;
        }
    }
}
