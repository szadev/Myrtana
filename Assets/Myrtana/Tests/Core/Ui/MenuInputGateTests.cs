using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-233: the input that skipped a video never reaches the title menu.</summary>
    public class MenuInputGateTests
    {
        [Test]
        public void Fresh_Gate_IsOpen()
        {
            var gate = new MenuInputGate();
            Assert.That(gate.Blocks, Is.False);
            Assert.That(gate.PointerDown(0), Is.True);
            Assert.That(gate.PointerUp(0), Is.True);
        }

        [Test]
        public void Shut_ForTheGrace_EvenWithNothingHeld()
        {
            var gate = new MenuInputGate();
            gate.Close(10);
            gate.Tick(10.1, false);
            Assert.That(gate.Blocks, Is.True);
            gate.Tick(10 + MenuInputGate.GraceSeconds, false);
            Assert.That(gate.Blocks, Is.False);
        }

        [Test]
        public void Shut_UntilTheSkipInputIsReleased()
        {
            var gate = new MenuInputGate();
            gate.Close(10);
            gate.Tick(11, true);
            gate.Tick(15, true);
            Assert.That(gate.Blocks, Is.True, "the key or button that skipped is still held");
            gate.Tick(15.01, false);
            Assert.That(gate.Blocks, Is.False);
        }

        [Test]
        public void ClickOfTheSkip_NeverActivates()
        {
            var gate = new MenuInputGate();
            gate.Close(10);
            Assert.That(gate.PointerDown(0), Is.False, "the press lands while shut");
            gate.Tick(11, false);
            Assert.That(gate.PointerUp(0), Is.False, "its release after the gate opened completes no click");
        }

        [Test]
        public void SecondClickOfADoubleClick_InTheGrace_IsSwallowed()
        {
            var gate = new MenuInputGate();
            gate.Close(10);
            gate.Tick(10.05, false);
            Assert.That(gate.PointerDown(0), Is.False);
            gate.Tick(10.1, true);
            Assert.That(gate.PointerUp(0), Is.False);
        }

        [Test]
        public void ReleaseWithoutPress_IsNoClick()
        {
            var gate = new MenuInputGate();
            Assert.That(gate.PointerUp(0), Is.False, "the press happened before the menu (or elsewhere)");
        }

        [Test]
        public void PressBeforeShut_IsForgotten()
        {
            var gate = new MenuInputGate();
            Assert.That(gate.PointerDown(0), Is.True);
            gate.Close(10);
            gate.Tick(11, false);
            Assert.That(gate.PointerUp(0), Is.False);
        }

        [Test]
        public void AfterTheGate_AFullClickActivates()
        {
            var gate = new MenuInputGate();
            gate.Close(10);
            gate.Tick(11, false);
            Assert.That(gate.PointerDown(0), Is.True);
            Assert.That(gate.PointerUp(0), Is.True);
            Assert.That(gate.PointerUp(0), Is.False, "one press, one click");
        }

        [Test]
        public void CloseAgain_ExtendsNeverShortens()
        {
            var gate = new MenuInputGate();
            gate.Close(10);
            gate.Close(9);
            gate.Tick(10.2, false);
            Assert.That(gate.Blocks, Is.True);
            gate.Tick(10.31, false);
            Assert.That(gate.Blocks, Is.False);
        }
    }
}
