using System;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>perf-22d: far trees drop their geometry renderers only where the LODGroup shows the billboard anyway.</summary>
    public class TreeFarLodTests
    {
        static float UnityScreenHeight(float size, float distance, float fov, float lodBias) =>
            (float)(size / (2.0 * distance * Math.Tan(fov * 0.5 * Math.PI / 180.0))) * lodBias;

        [Test]
        public void BillboardDistance_MatchesUnityLodMaths()
        {
            // 20 m fir, billboard below 3 % screen height, 60° FOV, lodBias 2 (the game's)
            float k = TreeFarLod.Coefficient(20f, 0.03f);
            float f = TreeFarLod.CameraFactor(60f, 2f);
            float d = TreeFarLod.BillboardDistance(k, f);
            Assert.AreEqual(0.03f, UnityScreenHeight(20f, d, 60f, 2f), 1e-4f);
            Assert.AreEqual(1154.7f, d, 0.5f);
        }

        [Test]
        public void Geometry_OnInsideMargin_OffOnlyBeyondHysteresis()
        {
            float k = TreeFarLod.Coefficient(10f, 0.05f), f = TreeFarLod.CameraFactor(60f, 1f);
            float bb = TreeFarLod.BillboardDistance(k, f), on = TreeFarLod.OnDistance(k, f), off = TreeFarLod.OffDistance(k, f);
            Assert.Greater(on, bb * 1.2f);
            Assert.Greater(off, on);
            // off state: comes on inside the on distance only
            Assert.IsTrue(TreeFarLod.WantGeometry(false, on - 1f, k, f));
            Assert.IsFalse(TreeFarLod.WantGeometry(false, on + 1f, k, f));
            // on state: stays on through the hysteresis band
            Assert.IsTrue(TreeFarLod.WantGeometry(true, off - 1f, k, f));
            Assert.IsFalse(TreeFarLod.WantGeometry(true, off + 1f, k, f));
            // the camera inside the billboard distance is urgent, the margin band is not
            Assert.IsTrue(TreeFarLod.Urgent(bb, k, f));
            Assert.IsFalse(TreeFarLod.Urgent(on - 1f, k, f));
        }

        [Test]
        public void WiderFov_OrLowerLodBias_BringsTheBillboardCloser()
        {
            float k = TreeFarLod.Coefficient(15f, 0.04f);
            float d60 = TreeFarLod.BillboardDistance(k, TreeFarLod.CameraFactor(60f, 2f));
            Assert.Less(TreeFarLod.BillboardDistance(k, TreeFarLod.CameraFactor(75f, 2f)), d60);
            Assert.Less(TreeFarLod.BillboardDistance(k, TreeFarLod.CameraFactor(60f, 0.8f)), d60);
        }

        [Test]
        public void MissingTransition_NeverBillboardOnly()
        {
            float k = TreeFarLod.Coefficient(10f, 0f);
            Assert.IsTrue(float.IsInfinity(k));
            Assert.IsTrue(TreeFarLod.WantGeometry(true, 1e6f, k, TreeFarLod.CameraFactor(60f, 1f)));
        }
    }
}
