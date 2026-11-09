using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-105 (owner, Demo_2026-10-18c: the torch could no longer be taken into the hand): a take or put-away overlay that
    /// never ends must not hold the torch keys for good (<see cref="HoldMotionRules.OverlayStuck"/>).
    /// </summary>
    public class TorchOverlayTests
    {
        [Test]
        public void ARunningPair_IsNotStuck()
        {
            // G3 hold pairs run 0.56 (spell) to 0.76 s (1H, 2H, bow); a slow frame on top stays far below the limit
            Assert.That(HoldMotionRules.OverlayStuck(10f, 10.76f), Is.False);
            Assert.That(HoldMotionRules.OverlayStuck(10f, 12.9f), Is.False);
        }

        [Test]
        public void AnOverlayThatNeverEnded_IsGivenUp()
        {
            // r40 owner log: PlayOverlay threw inside HeroTorchView.BeginOverlay; the overlay flag stayed set for minutes
            Assert.That(HoldMotionRules.OverlayStuck(10f, 13.1f), Is.True);
            Assert.That(HoldMotionRules.OverlayStuck(10f, 600f), Is.True);
        }

        [Test]
        public void TheLimitIsAboveEveryHoldPair()
        {
            Assert.That(HoldMotionRules.OverlayGiveUpSeconds, Is.GreaterThan(2f * 0.76f));
        }
    }
}
