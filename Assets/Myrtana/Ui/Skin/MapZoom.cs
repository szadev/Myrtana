using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// fix-225 [owner, QoL — G3's maps have no zoom or pan]: the zoom and pan of a map view. The map content is drawn at
    /// <see cref="Zoom"/> × the view's size with its top-left corner at (<see cref="OffsetX"/>, <see cref="OffsetY"/>) in
    /// view pixels; the content always covers the whole view (pan clamped to the map's bounds, zoom
    /// <see cref="MinZoom"/>–<see cref="MaxZoom"/>). Zooming keeps the map point under its anchor (the cursor) where it is,
    /// eased towards the target zoom by <see cref="Tick"/> (<see cref="Sharpness"/>), the anchor held at every step. Map
    /// points are fractions of the map (u, v in 0–1, as <c>MapProjection</c> gives them); markers placed by those
    /// fractions follow the content, their icons keep their pixel size.
    /// </summary>
    public sealed class MapZoom
    {
        public const float MinZoom = 1f, MaxZoom = 4f;
        /// <summary>Zoom factor of one wheel notch or one +/- key press.</summary>
        public const float Step = 1.25f;
        /// <summary>Easing rate (1/s): the zoom covers 1 − e^(−Sharpness·t) of the way to its target in t seconds.</summary>
        public const float Sharpness = 14f;
        const float SnapRatio = 0.002f;

        float _width = 1f, _height = 1f;
        float _target = MinZoom;
        // the anchor of the running zoom: view point and the map point that stays under it
        float _anchorX, _anchorY, _anchorU, _anchorV;

        /// <summary>Current (eased) zoom.</summary>
        public float Zoom { get; private set; } = MinZoom;

        /// <summary>The zoom <see cref="Tick"/> eases towards.</summary>
        public float TargetZoom => _target;

        /// <summary>Content top-left in view pixels (≤ 0).</summary>
        public float OffsetX { get; private set; }
        public float OffsetY { get; private set; }

        public float ViewWidth => _width;
        public float ViewHeight => _height;

        /// <summary>Content size in view pixels.</summary>
        public float ContentWidth => Zoom * _width;
        public float ContentHeight => Zoom * _height;

        /// <summary>The zoom is still easing towards its target.</summary>
        public bool Animating { get; private set; }

        /// <summary>Zoomed in (or zooming in): the view shows part of the map.</summary>
        public bool IsZoomed => Zoom > MinZoom + 1e-4f || _target > MinZoom + 1e-4f;

        /// <summary>The view's size changed (layout, resolution): the same part of the map stays in view.</summary>
        public void SetView(float width, float height)
        {
            if (!(width > 0f) || !(height > 0f) || float.IsInfinity(width) || float.IsInfinity(height)) return;
            if (width == _width && height == _height) return;
            float sx = width / _width, sy = height / _height;
            OffsetX *= sx;
            OffsetY *= sy;
            _anchorX *= sx;
            _anchorY *= sy;
            _width = width;
            _height = height;
            Clamp();
        }

        /// <summary>View point of map point (<paramref name="u"/>, <paramref name="v"/>).</summary>
        public (float x, float y) ToView(float u, float v) => (OffsetX + u * ContentWidth, OffsetY + v * ContentHeight);

        /// <summary>Map point (fractions) under view point (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public (float u, float v) ToMap(float x, float y) => ((x - OffsetX) / ContentWidth, (y - OffsetY) / ContentHeight);

        /// <summary>Zooms by <paramref name="factor"/> around view point (<paramref name="x"/>, <paramref name="y"/>), eased.</summary>
        public void ZoomAt(float factor, float x, float y, bool immediate = false)
        {
            if (!(factor > 0f) || float.IsInfinity(factor)) return;
            ZoomTo(_target * factor, x, y, immediate);
        }

        /// <summary>Zooms to <paramref name="zoom"/> (clamped) around view point (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public void ZoomTo(float zoom, float x, float y, bool immediate = false)
        {
            if (float.IsNaN(zoom) || float.IsNaN(x) || float.IsNaN(y)) return;
            _target = Math.Clamp(zoom, MinZoom, MaxZoom);
            _anchorX = Math.Clamp(x, 0f, _width);
            _anchorY = Math.Clamp(y, 0f, _height);
            (_anchorU, _anchorV) = ToMap(_anchorX, _anchorY);
            Animating = true;
            if (immediate) Finish();
        }

        /// <summary>One zoom step in (+1) or out (−1) per wheel notch / key press, around (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public void StepAt(int steps, float x, float y) => ZoomAt((float)Math.Pow(Step, steps), x, y);

        /// <summary>One zoom step around the view's centre (keys, gamepad).</summary>
        public void StepAtCentre(int steps) => StepAt(steps, _width / 2f, _height / 2f);

        /// <summary>Back to the whole map (eased).</summary>
        public void Reset(bool immediate = false) => ZoomTo(MinZoom, _width / 2f, _height / 2f, immediate);

        /// <summary>Moves the content by (<paramref name="dx"/>, <paramref name="dy"/>) view pixels (a drag), clamped to the map.</summary>
        public void PanBy(float dx, float dy)
        {
            if (float.IsNaN(dx) || float.IsNaN(dy)) return;
            OffsetX += dx;
            OffsetY += dy;
            Clamp();
            if (Animating) (_anchorU, _anchorV) = ToMap(_anchorX, _anchorY); // the running zoom keeps the dragged point
        }

        /// <summary>gamepad-1: view pixels per second a fully deflected stick pans the map.</summary>
        public const float StickPanSpeed = 700f;

        /// <summary>
        /// gamepad-1: the pan of one frame for a stick (x right, y up, dead-zoned): the view travels the way the stick
        /// points, so the content moves the other way (stick right → content left; stick up → content down).
        /// </summary>
        public static (float dx, float dy) StickPan(float x, float y, float deltaSeconds)
        {
            if (!(deltaSeconds > 0f)) return (0f, 0f);
            float k = StickPanSpeed * deltaSeconds;
            return (-x * k, y * k);
        }

        /// <summary>Eases the zoom by <paramref name="deltaSeconds"/>; true while it changed something.</summary>
        public bool Tick(float deltaSeconds)
        {
            if (!Animating) return false;
            float k = deltaSeconds > 0f ? 1f - (float)Math.Exp(-Sharpness * deltaSeconds) : 0f;
            float zoom = Zoom + (_target - Zoom) * k;
            if (Math.Abs(_target - zoom) <= SnapRatio * _target)
            {
                Finish();
                return true;
            }
            Apply(zoom);
            return true;
        }

        /// <summary>Jumps to the target zoom.</summary>
        public void Finish()
        {
            Apply(_target);
            Animating = false;
        }

        void Apply(float zoom)
        {
            Zoom = zoom;
            OffsetX = _anchorX - _anchorU * ContentWidth;
            OffsetY = _anchorY - _anchorV * ContentHeight;
            Clamp();
        }

        void Clamp()
        {
            OffsetX = Math.Clamp(OffsetX, _width - ContentWidth, 0f);
            OffsetY = Math.Clamp(OffsetY, _height - ContentHeight, 0f);
        }
    }
}
