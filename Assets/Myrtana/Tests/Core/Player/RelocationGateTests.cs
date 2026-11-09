using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-191 (build 22c crash after several bodies were moved in one frame): one body relocation per frame.</summary>
    public class RelocationGateTests
    {
        [Test]
        public void OnlyTheFirstRelocationOfAFrameIsAllowed()
        {
            var g = new RelocationGate();
            Assert.That(g.TryTake(10), Is.True);
            Assert.That(g.TryTake(10), Is.False);
            Assert.That(g.TryTake(10), Is.False);
        }

        [Test]
        public void ARefusedBodyMovesInALaterFrame()
        {
            var g = new RelocationGate();
            int moved = 0;
            // three buried bodies found in frame 5; each asks again every frame until it moved
            var pending = new[] { true, true, true };
            for (long frame = 5; frame < 9; frame++)
                for (int b = 0; b < pending.Length; b++)
                    if (pending[b] && g.TryTake(frame)) { pending[b] = false; moved++; }
            Assert.That(moved, Is.EqualTo(3));
            Assert.That(pending, Is.All.False);
        }
    }
}
