using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-122 [owner]: pages and object uses keep the play camera unless "Camera in windows: Original".</summary>
    public class WindowCameraTests
    {
        [Test]
        public void Default_PagesAndUses_KeepThePlayMode()
        {
            Assert.That(CameraModes.OfPlayerState(true, originalWindowCamera: false), Is.EqualTo(CameraMode.Ranged), "no Inventory zoom");
            Assert.That(CameraModes.OfPlayerState(false, originalWindowCamera: false), Is.EqualTo(CameraMode.Ranged));
            Assert.That(CameraModes.HoldsPlayCamera(true, false), Is.True);
            Assert.That(CameraModes.HoldsPlayCamera(false, false), Is.False, "play: the camera follows as always");
        }

        [Test]
        public void Original_UsesG3sInventoryMode()
        {
            Assert.That(CameraModes.OfPlayerState(true, originalWindowCamera: true), Is.EqualTo(CameraMode.Inventory));
            Assert.That(CameraModes.OfPlayerState(false, originalWindowCamera: true), Is.EqualTo(CameraMode.Ranged));
            Assert.That(CameraModes.HoldsPlayCamera(true, true), Is.False);
        }

        [Test]
        public void Hold_KeepsTheValueFromBeforeThePage_AndFollowsAgainAfter()
        {
            float kept = float.NaN;
            Assert.That(CameraModes.Hold(false, 3f, ref kept), Is.EqualTo(3f));
            Assert.That(CameraModes.Hold(true, 1.5f, ref kept), Is.EqualTo(3f), "a page / kneel: no zoom in");
            Assert.That(CameraModes.Hold(true, 4.5f, ref kept), Is.EqualTo(3f), "the approach walk: no zoom out");
            Assert.That(CameraModes.Hold(false, 3f, ref kept), Is.EqualTo(3f), "closing the page: the same value, no jump");
            float fresh = float.NaN;
            Assert.That(CameraModes.Hold(true, 2f, ref fresh), Is.EqualTo(2f), "nothing kept yet: the live value");
        }

        [Test]
        public void HeldDistance_KeepsTheFieldOfView()
        {
            var m = CameraModes.Params(CameraModes.OfPlayerState(true, false));
            float kept = float.NaN;
            float before = m.Fov(CameraModes.Hold(false, 3f, ref kept));
            float during = m.Fov(CameraModes.Hold(true, CameraModes.Inventory.BestRange, ref kept));
            Assert.That(during, Is.EqualTo(before), "FOV unchanged (no 45° inventory view)");
        }
    }
}
