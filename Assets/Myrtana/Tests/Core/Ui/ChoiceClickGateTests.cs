using Myrtana.Ui.Dialog;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-84 item 1: only a click on a choice row runs it (G3 one-click-activate list).</summary>
    public class ChoiceClickGateTests
    {
        [Test]
        public void ClickOnRow_Activates()
        {
            var gate = new ChoiceClickGate();
            gate.Shown(10);
            gate.Press(2, 12);
            Assert.That(gate.Release(2), Is.True);
            Assert.That(gate.Release(2), Is.False, "one press, one activation");
        }

        [Test]
        public void ClickBesideRows_DoesNotActivateHighlight()
        {
            var gate = new ChoiceClickGate();
            gate.Shown(10);
            gate.Press(-1, 12);
            Assert.That(gate.Release(0), Is.False, "pressed beside the list, released over the highlighted row");
            gate.Press(1, 13);
            Assert.That(gate.Release(-1), Is.False, "released beside the list");
            gate.Press(1, 14);
            Assert.That(gate.Release(0), Is.False, "pressed on one row, released on another");
        }

        [Test]
        public void LineSkipClick_LandingOnFreshRows_DoesNotActivate()
        {
            var gate = new ChoiceClickGate();
            gate.Shown(20);                // the click skipped the line and the choices appeared in this frame
            gate.Press(0, 20);
            Assert.That(gate.Release(0), Is.False);
            gate.Press(0, 21);
            gate.Shown(21);                // the list is rebuilt: an earlier press is forgotten
            Assert.That(gate.Release(0), Is.False);
        }
    }
}
