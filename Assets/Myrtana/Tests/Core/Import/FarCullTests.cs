using System;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>perf-2: far objects stop casting beyond the shadow reach and small ones stop rendering where the drawer culls them anyway.</summary>
    public class FarCullTests
    {
        /// <summary>The GPU Resident Drawer's small-mesh test (InstanceCuller.CalculateLODVisibility): culled when
        /// size / h &lt; distance · 2 · tan(fov/2) / lodBias.</summary>
        static bool DrawerCulls(float size, float distance, float percent, float fov, float lodBias) =>
            size / (percent * 0.01f) < distance * 2.0 * Math.Tan(fov * 0.5 * Math.PI / 180.0) / lodBias;

        [Test]
        public void Hidden_OnlyWhereTheDrawerCullsAnyway()
        {
            float f = TreeFarLod.CameraFactor(60f, 2f);
            foreach (float size in new[] { 0.2f, 1f, 3f, 12f })
            foreach (float pct in new[] { 0.2f, 0.8f, 2f })
            {
                float k = FarCull.SmallMeshCoefficient(size, pct);
                for (float d = 1f; d < 20000f; d *= 1.07f)
                    if (!FarCull.WantRendered(false, d, k, f))   // stays hidden here
                        Assert.That(DrawerCulls(size, d, pct, 60f, 2f), $"size {size} pct {pct} d {d}: hidden but the drawer would draw it");
            }
        }

        [Test]
        public void Transparent_StricterShare_OffStaysOff()
        {
            Assert.AreEqual(2f, FarCull.TransparentPercentage(0.2f), 1e-6f);
            Assert.AreEqual(3.2f, FarCull.TransparentPercentage(0.8f), 1e-6f);
            Assert.AreEqual(0f, FarCull.TransparentPercentage(0f));
            // a 2 m spider web at the game's lodBias 2 / 60 degrees: hidden from ~240 m (was drawn up to 800 m)
            float k = FarCull.SmallMeshCoefficient(2f, FarCull.TransparentPercentage(0.2f));
            float f = TreeFarLod.CameraFactor(60f, 2f);
            Assert.That(FarCull.WantRendered(false, 250f, k, f), Is.False);
            Assert.That(FarCull.WantRendered(true, 150f, k, f), Is.True);
        }

        [Test]
        public void SmallMeshOff_NeverHidden()
        {
            float k = FarCull.SmallMeshCoefficient(0.1f, 0f);
            Assert.That(FarCull.WantRendered(true, 1e6f, k, TreeFarLod.CameraFactor(60f, 2f)), Is.True);
            Assert.That(FarCull.UrgentRender(1e6f, k, 1f), Is.True);
        }

        [Test]
        public void Shadow_OnInsideReach_OffBeyond_WithHysteresis()
        {
            // 2 m crate, shadow distance 90 m (remaster High): reach 90 + 30 + 8 = 128 m
            Assert.AreEqual(128f, FarCull.ShadowOnDistance(2f, 90f), 1e-3f);
            Assert.That(FarCull.WantShadow(false, 127f, 2f, 90f), Is.True);
            Assert.That(FarCull.WantShadow(false, 129f, 2f, 90f), Is.False);
            Assert.That(FarCull.WantShadow(true, 140f, 2f, 90f), Is.True, "hysteresis keeps it on");
            Assert.That(FarCull.WantShadow(true, 151f, 2f, 90f), Is.False);
            // a 40 m tower keeps its long shadow far out
            Assert.That(FarCull.WantShadow(false, 275f, 40f, 90f), Is.True);
            // shadows off (distance 0) or short: point-light reach still covered
            Assert.That(FarCull.ShadowOnDistance(1f, 0f), Is.GreaterThanOrEqualTo(FarCull.MinShadowDistance + FarCull.ShadowMarginMetres));
            Assert.That(FarCull.UrgentShadow(100f, 2f, 90f), Is.True);
            Assert.That(FarCull.UrgentShadow(125f, 2f, 90f), Is.False);
        }
    }
}
