using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-225 [owner, QoL]: the map views' zoom and pan — cursor-anchored zoom, clamping, the marker transform.</summary>
    public sealed class MapZoomTests
    {
        const float Eps = 1e-3f;

        static MapZoom View(float w = 400f, float h = 300f)
        {
            var z = new MapZoom();
            z.SetView(w, h);
            return z;
        }

        [Test]
        public void Starts_WholeMap()
        {
            var z = View();
            Assert.That(z.Zoom, Is.EqualTo(1f));
            Assert.That(z.OffsetX, Is.EqualTo(0f));
            Assert.That(z.OffsetY, Is.EqualTo(0f));
            Assert.That(z.IsZoomed, Is.False);
            Assert.That(z.ToView(0.5f, 0.25f).x, Is.EqualTo(200f).Within(Eps));
            Assert.That(z.ToView(0.5f, 0.25f).y, Is.EqualTo(75f).Within(Eps));
        }

        [Test]
        public void ZoomAt_KeepsTheMapPointUnderTheCursor()
        {
            var z = View();
            float cx = 100f, cy = 210f;
            var (u, v) = z.ToMap(cx, cy);
            z.ZoomTo(2.5f, cx, cy, immediate: true);
            Assert.That(z.Zoom, Is.EqualTo(2.5f).Within(Eps));
            var (x, y) = z.ToView(u, v);
            Assert.That(x, Is.EqualTo(cx).Within(Eps));
            Assert.That(y, Is.EqualTo(cy).Within(Eps));
            Assert.That(z.ContentWidth, Is.EqualTo(1000f).Within(Eps));
        }

        [Test]
        public void ZoomAt_ComposesAroundDifferentCursors()
        {
            var z = View();
            z.ZoomTo(2f, 300f, 100f, immediate: true);
            float cx = 50f, cy = 250f;
            var (u, v) = z.ToMap(cx, cy);
            z.ZoomAt(1.5f, cx, cy, immediate: true);
            Assert.That(z.Zoom, Is.EqualTo(3f).Within(Eps));
            var (x, y) = z.ToView(u, v);
            Assert.That(x, Is.EqualTo(cx).Within(Eps));
            Assert.That(y, Is.EqualTo(cy).Within(Eps));
        }

        [Test]
        public void Easing_HoldsTheAnchorAtEveryStep_AndReachesTheTarget()
        {
            var z = View();
            float cx = 320f, cy = 60f;
            var (u, v) = z.ToMap(cx, cy);
            z.StepAt(3, cx, cy);
            Assert.That(z.TargetZoom, Is.EqualTo(1.25f * 1.25f * 1.25f).Within(Eps));
            Assert.That(z.Animating, Is.True);
            float last = z.Zoom;
            for (int i = 0; i < 200 && z.Animating; i++)
            {
                Assert.That(z.Tick(1f / 60f), Is.True);
                Assert.That(z.Zoom, Is.GreaterThanOrEqualTo(last), "zooming in grows monotonically");
                last = z.Zoom;
                var (x, y) = z.ToView(u, v);
                Assert.That(x, Is.EqualTo(cx).Within(0.01f));
                Assert.That(y, Is.EqualTo(cy).Within(0.01f));
            }
            Assert.That(z.Animating, Is.False, "settles within a few frames");
            Assert.That(z.Zoom, Is.EqualTo(z.TargetZoom));
            Assert.That(z.Tick(1f / 60f), Is.False);
        }

        [Test]
        public void Zoom_ClampedToOneToFour()
        {
            var z = View();
            z.StepAt(20, 10f, 10f);
            Assert.That(z.TargetZoom, Is.EqualTo(MapZoom.MaxZoom));
            z.StepAt(-40, 10f, 10f);
            Assert.That(z.TargetZoom, Is.EqualTo(MapZoom.MinZoom));
            z.Finish();
            Assert.That(z.OffsetX, Is.EqualTo(0f));
            Assert.That(z.OffsetY, Is.EqualTo(0f));
        }

        [Test]
        public void ZoomAtTheEdge_KeepsTheMapCoveringTheView()
        {
            var z = View();
            z.ZoomTo(3f, 0f, 0f, immediate: true);
            Assert.That(z.OffsetX, Is.EqualTo(0f).Within(Eps), "top-left corner stays put");
            Assert.That(z.OffsetY, Is.EqualTo(0f).Within(Eps));
            z.ZoomTo(1.5f, 400f, 300f, immediate: true); // out around the far corner: still covering
            Assert.That(z.OffsetX, Is.LessThanOrEqualTo(0f));
            Assert.That(z.OffsetX + z.ContentWidth, Is.GreaterThanOrEqualTo(400f - Eps));
            Assert.That(z.OffsetY + z.ContentHeight, Is.GreaterThanOrEqualTo(300f - Eps));
        }

        [Test]
        public void Pan_ClampedToTheMapBounds()
        {
            var z = View();
            z.PanBy(50f, -50f);
            Assert.That(z.OffsetX, Is.EqualTo(0f), "no pan at 1×");
            Assert.That(z.OffsetY, Is.EqualTo(0f));
            z.ZoomTo(2f, 200f, 150f, immediate: true);
            Assert.That(z.OffsetX, Is.EqualTo(-200f).Within(Eps));
            Assert.That(z.OffsetY, Is.EqualTo(-150f).Within(Eps));
            z.PanBy(30f, -20f);
            Assert.That(z.OffsetX, Is.EqualTo(-170f).Within(Eps));
            Assert.That(z.OffsetY, Is.EqualTo(-170f).Within(Eps));
            z.PanBy(1000f, 1000f);
            Assert.That(z.OffsetX, Is.EqualTo(0f));
            Assert.That(z.OffsetY, Is.EqualTo(0f));
            z.PanBy(-5000f, -5000f);
            Assert.That(z.OffsetX, Is.EqualTo(-400f).Within(Eps), "right edge of the map on the view's right edge");
            Assert.That(z.OffsetY, Is.EqualTo(-300f).Within(Eps));
        }

        [Test]
        public void MarkerTransform_ScalesPositions_ToMapIsTheInverse()
        {
            var z = View();
            z.ZoomTo(4f, 100f, 100f, immediate: true);
            z.PanBy(-37f, 12f);
            foreach (var (u, v) in new[] { (0f, 0f), (1f, 1f), (0.3f, 0.7f), (0.25f, 0.33f) })
            {
                var (x, y) = z.ToView(u, v);
                Assert.That(x, Is.EqualTo(z.OffsetX + u * 1600f).Within(Eps));
                Assert.That(y, Is.EqualTo(z.OffsetY + v * 1200f).Within(Eps));
                var (bu, bv) = z.ToMap(x, y);
                Assert.That(bu, Is.EqualTo(u).Within(1e-5f));
                Assert.That(bv, Is.EqualTo(v).Within(1e-5f));
            }
            // two markers 0.1 of the map apart are 4× further apart on screen than at 1×
            Assert.That(z.ToView(0.6f, 0f).x - z.ToView(0.5f, 0f).x, Is.EqualTo(160f).Within(Eps));
        }

        [Test]
        public void Reset_BackToTheWholeMap()
        {
            var z = View();
            z.ZoomTo(3.2f, 50f, 280f, immediate: true);
            z.PanBy(-80f, 40f);
            z.Reset();
            for (int i = 0; i < 200 && z.Animating; i++) z.Tick(1f / 60f);
            Assert.That(z.Zoom, Is.EqualTo(1f));
            Assert.That(z.OffsetX, Is.EqualTo(0f).Within(Eps));
            Assert.That(z.OffsetY, Is.EqualTo(0f).Within(Eps));
            Assert.That(z.IsZoomed, Is.False);
        }

        [Test]
        public void PanWhileEasing_KeepsTheDraggedPoint()
        {
            var z = View();
            z.StepAt(4, 200f, 150f);
            z.Tick(1f / 60f);
            z.PanBy(-10f, -10f);
            var (u, v) = z.ToMap(200f, 150f);
            for (int i = 0; i < 200 && z.Animating; i++) z.Tick(1f / 60f);
            var (x, y) = z.ToView(u, v);
            Assert.That(x, Is.EqualTo(200f).Within(0.01f));
            Assert.That(y, Is.EqualTo(150f).Within(0.01f));
        }

        [Test]
        public void ViewResize_KeepsTheSamePartOfTheMap()
        {
            var z = View();
            z.ZoomTo(2f, 300f, 200f, immediate: true);
            var (u, v) = z.ToMap(0f, 0f);
            z.SetView(800f, 600f);
            var (u2, v2) = z.ToMap(0f, 0f);
            Assert.That(u2, Is.EqualTo(u).Within(1e-5f));
            Assert.That(v2, Is.EqualTo(v).Within(1e-5f));
            Assert.That(z.ContentWidth, Is.EqualTo(1600f).Within(Eps));
        }

        [Test]
        public void BadInput_Ignored()
        {
            var z = View();
            z.SetView(0f, float.NaN);
            z.ZoomAt(float.NaN, 1f, 1f);
            z.ZoomAt(-2f, 1f, 1f);
            z.PanBy(float.NaN, 3f);
            Assert.That(z.ViewWidth, Is.EqualTo(400f));
            Assert.That(z.TargetZoom, Is.EqualTo(1f));
            Assert.That(z.OffsetX, Is.EqualTo(0f));
        }
    
        [Test]
        public void StickPan_MovesViewTheStickWay()
        {
            var z = View();
            z.ZoomTo(2f, 200f, 150f, immediate: true);
            float x0 = z.OffsetX, y0 = z.OffsetY;
            var (dx, dy) = MapZoom.StickPan(1f, 1f, 0.1f);
            Assert.That(dx, Is.EqualTo(-MapZoom.StickPanSpeed * 0.1f).Within(Eps));
            Assert.That(dy, Is.EqualTo(MapZoom.StickPanSpeed * 0.1f).Within(Eps));
            z.PanBy(dx, dy);
            Assert.That(z.OffsetX, Is.LessThan(x0)); // content left: the view shows more of the right
            Assert.That(z.OffsetY, Is.GreaterThanOrEqualTo(y0));
            Assert.That(MapZoom.StickPan(1f, 0f, 0f), Is.EqualTo((0f, 0f)));
        }
}
}
