using G3.Player;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-101 (owner: "scrolling the mouse wheel in the inventory also zooms the camera"): the camera's wheel and Num + / -
    /// zoom act only in the fix-87 Gameplay cursor role (<see cref="CameraZoomInput"/>, <see cref="CursorPolicy"/>).
    /// </summary>
    public class CameraZoomInputTests
    {
        static float Zoom(in CursorUi ui, float wheel = 1f, float keys = 0f) =>
            CameraZoomInput.Notches(CursorPolicy.Captured(CursorPolicy.Evaluate(ui)), wheel, keys);

        [Test]
        public void Play_TheWheelAndTheKeysZoom()
        {
            Assert.That(Zoom(default), Is.EqualTo(1f));
            Assert.That(Zoom(default, -2f, 0.1f), Is.EqualTo(-1.9f).Within(1e-6f));
        }

        [Test]
        public void PageMenuDialogTip_TheWheelScrollsTheUiOnly()
        {
            Assert.That(Zoom(new CursorUi { Page = true }), Is.EqualTo(0f), "inventory, trade, loot, map …");
            Assert.That(Zoom(new CursorUi { Menu = true }), Is.EqualTo(0f));
            Assert.That(Zoom(new CursorUi { DialogChoices = true, ControlsLocked = true }), Is.EqualTo(0f), "the dialog choice list");
            Assert.That(Zoom(new CursorUi { Tip = true }), Is.EqualTo(0f));
            Assert.That(Zoom(new CursorUi { Dev = true }), Is.EqualTo(0f));
            Assert.That(Zoom(new CursorUi { Page = true }, 0f, 1f), Is.EqualTo(0f), "Num + / - neither");
        }

        [Test]
        public void SpokenLineLoadingVideo_NoZoom()
        {
            Assert.That(Zoom(new CursorUi { ControlsLocked = true }), Is.EqualTo(0f), "a spoken line, a use");
            Assert.That(Zoom(new CursorUi { Loading = true }), Is.EqualTo(0f));
            Assert.That(Zoom(new CursorUi { Video = true }), Is.EqualTo(0f));
        }
    }
}
