using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>par-misc: G3 camera details (gCCameraAI_PS UpdateDistance, TargetOffsetY, the Inventory mode).</summary>
    public class CameraSmoothingTests
    {
        [Test]
        public void SmoothDistance_MovesPartWayEachFrame_FasterForLargeGaps()
        {
            float dt = 1f / 60f;
            float near = CameraModes.SmoothDistance(3f, 3.1f, 15f, dt);
            float far = CameraModes.SmoothDistance(3f, 8f, 15f, dt);
            Assert.That(near, Is.GreaterThan(3f).And.LessThan(3.1f));
            Assert.That(far, Is.GreaterThan(3f).And.LessThan(8f));
            Assert.That((far - 3f) / 5f, Is.GreaterThan((near - 3f) / 0.1f), "(4q+1)/(q+1) speeds up large gaps");
            float d = 3f;
            for (int i = 0; i < 60; i++) d = CameraModes.SmoothDistance(d, 8f, 15f, dt);
            Assert.That(d, Is.EqualTo(8f).Within(0.01f), "settles within a second");
        }

        [Test]
        public void SmoothDistance_NeverExceedsTheFreeDistance()
        {
            Assert.That(CameraModes.SmoothDistance(5f, 5f, 1.2f, 0.016f), Is.EqualTo(1.2f), "an obstacle pulls in at once");
            Assert.That(CameraModes.SmoothDistance(-1f, 3f, 15f, 0.016f), Is.EqualTo(3f), "first frame snaps");
        }

        [Test]
        public void TargetOffsetY_EasesFrom50To120cmOverTheZoom()
        {
            var ranged = CameraModes.Ranged;
            Assert.That(ranged.TargetOffsetY(2f), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(ranged.TargetOffsetY(15f), Is.EqualTo(1.2f).Within(1e-4f));
            Assert.That(ranged.TargetOffsetY(8.5f), Is.EqualTo(0.85f).Within(1e-3f), "half cosine: the middle is the mean");
            Assert.That(ranged.TargetOffsetY(3f), Is.LessThan(0.5f + 0.7f * (1f / 13f)), "eased: slower than linear near MinRange");
            Assert.That(CameraModes.Inventory.TargetOffsetY(1.5f), Is.EqualTo(0f));
        }

        [Test]
        public void HudAndInteraction_UseTheInventoryMode()
        {
            Assert.That(CameraModes.OfPlayerState(true), Is.EqualTo(CameraMode.Inventory));
            Assert.That(CameraModes.Params(CameraMode.Inventory).BestRange, Is.EqualTo(1.5f));
            Assert.That(CameraModes.Params(CameraMode.Inventory).Fov(1.5f), Is.EqualTo(45f));
        }
    }
}
