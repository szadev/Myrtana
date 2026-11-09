using G3.Formats.Trees;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>LODGroup layout of the original trees (G3SpeedTreeImporter → TreeLodPlan).</summary>
    public class TreeLodPlanTests
    {
        static readonly TreeLodPlan.Settings Defaults = new TreeLodPlan.Settings();

        [Test]
        public void Heights_StrictlyDecrease_EndAtCullHeight_AndStayBelowOne()
        {
            var p = TreeLodPlan.Build(new[] { 0f, 0.25f, 0.5f, 0.75f }, true, 28.7f, 10f, 400f, Defaults);
            Assert.AreEqual(5, p.Heights.Length);
            Assert.Less(p.Heights[0], 1f);
            for (int j = 1; j < p.Heights.Length; j++) Assert.Less(p.Heights[j], p.Heights[j - 1]);
            Assert.AreEqual(Defaults.CullScreenHeight, p.Heights[4], 1e-6f);
        }

        [Test]
        public void Distances_FollowEngineRange_WithMinimumSteps()
        {
            // Small bush: the engine range is tiny, the minimum first switch and step take over.
            var p = TreeLodPlan.Build(new[] { 0f, 0.5f, 0.75f }, true, 1.5f, 1f, 5f, Defaults);
            Assert.AreEqual(1f, p.Distances[0], 1e-4f);
            Assert.AreEqual(25f, p.Distances[1], 1e-4f);
            Assert.AreEqual(35f, p.Distances[2], 1e-4f);
            Assert.AreEqual(120f, p.BillboardDistance, 1e-4f);

            // Large tree: engine distances between near and far.
            var big = TreeLodPlan.Build(new[] { 0f, 0.5f }, true, 30f, 100f, 500f, Defaults);
            Assert.AreEqual(300f, big.Distances[1], 1e-3f);
            Assert.AreEqual(500f, big.BillboardDistance, 1e-3f);
        }

        [Test]
        public void TransitionHeight_MatchesDistanceAtReferenceFov()
        {
            var p = TreeLodPlan.Build(new[] { 0f, 0.5f }, true, 30f, 100f, 500f, Defaults);
            // Height(d) = size / (2 d tan(fov/2)); LOD0 hands over at 300 m, LOD1 at the billboard (500 m).
            double tan = System.Math.Tan(30 * System.Math.PI / 180);
            Assert.AreEqual(30 / (2 * 300 * tan), p.Heights[0], 1e-5);
            Assert.AreEqual(30 / (2 * 500 * tan), p.Heights[1], 1e-5);
        }

        [Test]
        public void WithoutBillboard_LastGeometryLevelIsCulled()
        {
            var p = TreeLodPlan.Build(new[] { 0f, 0.5f }, false, 10f, 50f, 200f, Defaults);
            Assert.AreEqual(2, p.Heights.Length);
            Assert.AreEqual(Defaults.CullScreenHeight, p.Heights[1], 1e-6f);
        }

        [Test]
        public void HugeTreeUpClose_IsRescaledBelowOne()
        {
            var p = TreeLodPlan.Build(new[] { 0f, 0.1f }, true, 60f, 0f, 30f, Defaults);
            Assert.Less(p.Heights[0], 1f);
            Assert.Less(p.Heights[1], p.Heights[0]);
        }

        [Test]
        public void FadeWidths_AreTheDitheredBand_ForEveryLod()
        {
            var s = new TreeLodPlan.Settings { CrossFadeWidth = 0.2f };
            var p = TreeLodPlan.Build(new[] { 0f, 0.5f }, true, 10f, 50f, 200f, s);
            Assert.AreEqual(new[] { 0.2f, 0.2f, 0.2f }, p.FadeWidths);
            s.CrossFadeWidth = 3f;
            Assert.AreEqual(1f, TreeLodPlan.Build(new[] { 0f }, false, 10f, 50f, 200f, s).FadeWidths[0]);
        }

        [Test]
        public void LodDistanceScale_ScalesEngineDistances()
        {
            var s = new TreeLodPlan.Settings { LodDistanceScale = 0.5f };
            var p = TreeLodPlan.Build(new[] { 0f, 0.5f }, true, 30f, 100f, 1000f, s);
            Assert.AreEqual(275f, p.Distances[1], 1e-3f);
            Assert.AreEqual(500f, p.BillboardDistance, 1e-3f);
        }
    }
}
