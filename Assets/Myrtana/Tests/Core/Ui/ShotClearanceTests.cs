using Myrtana.Ui.Dialog;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-84 item 5: the dialog camera stops before level geometry seen from either side.</summary>
    public class ShotClearanceTests
    {
        [Test]
        public void ClearWay_KeepsThePresetDistance()
        {
            Assert.That(ShotClearance.Distance(2.5f, 2.5f, -1, 0.4f), Is.EqualTo(2.5f));
            Assert.That(ShotClearance.FreeShare(2.5f, 2.5f, -1), Is.EqualTo(1f));
        }

        [Test]
        public void WallSeenFromTheTarget_PullsInWithMargin()
        {
            Assert.That(ShotClearance.Distance(2.5f, 1.2f, -1, 0.4f), Is.EqualTo(1.2f - ShotClearance.WallMargin).Within(1e-5f));
        }

        [Test]
        public void OneSidedCeiling_OnlyTheInwardTraceSees_StillBlocks()
        {
            // cave rock whose faces point away from the heads: the outward trace passes, the trace back from the
            // wished position (above the cave) meets the rock 1.9 m out, i.e. 0.6 m from the target
            float d = ShotClearance.Distance(2.5f, 2.5f, 1.9f, 0.4f);
            Assert.That(d, Is.EqualTo(0.6f - ShotClearance.WallMargin).Within(1e-5f));
            Assert.That(d, Is.LessThan(0.6f));
            Assert.That(ShotClearance.FreeShare(2.5f, 2.5f, 1.9f), Is.LessThan(0.75f), "the director skips such a preset");
        }

        [Test]
        public void WishedPositionInsideRock_FallsBackToTheMinimum()
        {
            Assert.That(ShotClearance.Distance(2.5f, 2.5f, 0, 0.4f), Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(ShotClearance.Distance(0.3f, 0.3f, 0, 0.4f), Is.EqualTo(0.3f).Within(1e-5f), "never beyond a short preset");
        }

        [Test]
        public void BrokenInputs_NeverFlyAway()
        {
            Assert.That(ShotClearance.Distance(80f, 80f, -1, 0.4f), Is.EqualTo(ShotClearance.MaxDistance));
            Assert.That(ShotClearance.Distance(float.NaN, 1, -1, 0.4f), Is.EqualTo(0));
        }
    }
}
