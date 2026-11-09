using System;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-243 (owner: picking a small iron-bound chest, the hero knelt inside it): the hero kneels on the chest actor's
    /// placement dummy, 1.41 m in front of its origin along its forward, facing the chest (CP 1.75 data in
    /// <c>G3Tests.Formats.ExactUsePointTests</c>).
    /// </summary>
    public sealed class ChestUsePoseTests
    {
        const float ChestHalfDepth = 0.44f;  // m, the deepest chest mesh (G3_Chest_Body_Chest Z -44.2..42.8 cm)
        const float KneelLean = 0.33f;       // m, the kneeling pelvis ahead of the hero's origin (Hero_Kneel_*_Lockpick_Loop)

        [Test]
        public void KneelsOnTheDummyInFrontFacingTheChest()
        {
            var (x, y, z, yaw) = ChestUsePose.Spot(new float[] { 10f, 2f, 20f }, new float[] { 0f, 0f, 1f }, 1f);
            Assert.That(x, Is.EqualTo(10f).Within(1e-4f));
            Assert.That(y, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(z, Is.EqualTo(20f + 1.40882f).Within(1e-4f));
            Assert.That(yaw, Is.EqualTo(180f).Within(1e-3f), "facing the chest along its -Z");
        }

        [TestCase(90f)]
        [TestCase(-135f)]
        [TestCase(33f)]
        public void FollowsTheChestsTurn(float chestYaw)
        {
            double a = chestYaw * Math.PI / 180.0;
            var forward = new float[] { (float)Math.Sin(a), 0f, (float)Math.Cos(a) };
            var (x, _, z, yaw) = ChestUsePose.Spot(new float[] { 0f, 0f, 0f }, forward, 1f);
            Assert.That(Math.Sqrt(x * x + z * z), Is.EqualTo(ChestUsePose.KneelDistance).Within(1e-4));
            Assert.That(Mathf360(yaw - (chestYaw + 180f)), Is.EqualTo(0f).Within(1e-3f), "faces back at the chest");
        }

        [Test]
        public void ScaleAndTiltedForward()
        {
            // a chest on a slope: its forward has a vertical part; the kneel point stays horizontal at 1.41 m x scale
            var (x, y, z, _) = ChestUsePose.Spot(new float[] { 0f, 5f, 0f }, new float[] { 0f, 0.3f, 0.95f }, 1.2f);
            Assert.That(x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(y, Is.EqualTo(5f));
            Assert.That(z, Is.EqualTo(ChestUsePose.KneelDistance * 1.2f).Within(1e-4f));
            // a degenerate forward falls back to +Z
            var (_, _, z2, _) = ChestUsePose.Spot(new float[] { 0f, 0f, 0f }, new float[] { 0f, 1f, 0f }, 0f);
            Assert.That(z2, Is.EqualTo(ChestUsePose.KneelDistance).Within(1e-4f));
        }

        [Test]
        public void KneelingBodyStaysOutOfTheChest()
        {
            // the kneel's pelvis ends ahead of the hero's origin; with the hero's core (UseSpotClearance) it must stay in
            // front of the chest's face. The old 0.85 m from the mesh centre left it inside (the owner's bug).
            float gap = ChestUsePose.KneelDistance - ChestHalfDepth;
            Assert.That(gap, Is.GreaterThan(KneelLean + UseSpotClearance.CoreRadius));
            Assert.That(0.85f - ChestHalfDepth, Is.LessThan(KneelLean + UseSpotClearance.CoreRadius), "the old kneel distance");
        }

        [Test]
        public void FaceYawLooksAtTheTarget()
        {
            Assert.That(ChestUsePose.FaceYaw(0f, 0f, 1f, 0f, 7f), Is.EqualTo(90f).Within(1e-3f));
            Assert.That(ChestUsePose.FaceYaw(0f, 0f, 0f, -1f, 7f), Is.EqualTo(180f).Within(1e-3f));
            Assert.That(ChestUsePose.FaceYaw(0f, 0f, -1f, 0f, 7f), Is.EqualTo(270f).Within(1e-3f));
            Assert.That(ChestUsePose.FaceYaw(3f, 4f, 3f, 4f, 7f), Is.EqualTo(7f), "same point: the fallback");
        }

        static float Mathf360(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            if (a < -180f) a += 360f;
            return a;
        }
    }
}
