using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-75: dropped weapons lie flat (G3 physics settles a dropped item on its widest side).</summary>
    public class ItemRestPoseTests
    {
        [TestCase(0.06f, 1.10f, 0.20f, 0, 1)]  // a sword whose blade runs along local Y, flat along X
        [TestCase(2.20f, 0.08f, 0.10f, 1, 0)]  // a spear along local X
        [TestCase(0.12f, 0.30f, 1.40f, 0, 2)]  // a bow along local Z
        [TestCase(0.3f, 0.3f, 0.3f, 0, 1)]     // a cube: ties keep the lower index
        public void Axes_ThinnestUp_LongestLevel(float x, float y, float z, int thin, int length)
        {
            ItemRestPose.Axes(x, y, z, out int t, out int l);
            Assert.That(t, Is.EqualTo(thin));
            Assert.That(l, Is.EqualTo(length));
        }

        [Test]
        public void CentreHeight_IsHalfTheThinnestExtent_PlusClearance()
        {
            Assert.That(ItemRestPose.CentreHeight(0.06f, 1.1f, 0.2f), Is.EqualTo(0.03f + ItemRestPose.Clearance).Within(1e-6));
            Assert.That(ItemRestPose.CentreHeight(2.2f, 0.08f, 0.1f), Is.EqualTo(0.04f + ItemRestPose.Clearance).Within(1e-6));
        }

        [Test]
        public void Lift_RaisesTheItemOverAHigherEnd_NeverLowers()
        {
            Assert.That(ItemRestPose.Lift(0f, 0.01f, 0f, 0.01f), Is.Zero, "both ends clear");
            Assert.That(ItemRestPose.Lift(0.1f, 0.01f, 0f, 0.01f), Is.EqualTo(0.1f + ItemRestPose.Clearance - 0.01f).Within(1e-6));
            Assert.That(ItemRestPose.Lift(0.05f, 0.01f, 0.2f, 0.01f), Is.EqualTo(0.2f + ItemRestPose.Clearance - 0.01f).Within(1e-6), "the higher end wins");
            Assert.That(ItemRestPose.Lift(float.NegativeInfinity, 0f, float.NegativeInfinity, 0f), Is.Zero, "no ground found: no lift");
        }
    }
}
