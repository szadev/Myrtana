using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class HeroUseInputTests
    {
        static HeroInputRoute Route(HeroInput input, bool stance = false, bool locked = true, bool quickLoot = true) =>
            HeroUseInput.Route(input, stance, locked, quickLoot);

        [Test]
        public void LeftButtonUsesTheFocusWithEmptyHands() =>
            Assert.That(Route(HeroInput.Primary), Is.EqualTo(HeroInputRoute.Use)); // G3 PS_Normal: Use1 queues Use

        [Test]
        public void LeftButtonFightsInADrawnStance() =>
            Assert.That(Route(HeroInput.Primary, stance: true), Is.EqualTo(HeroInputRoute.Combat)); // PS_Melee: attack, no use

        [Test]
        public void RightButtonQuickLootsWithEmptyHandsWhenTheOptionIsOn()
        {
            Assert.That(Route(HeroInput.Secondary), Is.EqualTo(HeroInputRoute.QuickLoot));
            Assert.That(Route(HeroInput.Secondary, quickLoot: false), Is.EqualTo(HeroInputRoute.None));
            Assert.That(Route(HeroInput.Secondary, stance: true), Is.EqualTo(HeroInputRoute.Combat)); // parade
        }

        [Test]
        public void MouseClicksOnAPageDoNothingInTheWorld()
        {
            Assert.That(Route(HeroInput.Primary, locked: false), Is.EqualTo(HeroInputRoute.None));
            Assert.That(Route(HeroInput.Secondary, locked: false), Is.EqualTo(HeroInputRoute.None));
        }

        [Test]
        public void TheUseKeyUsesTheFocusInEveryStance()
        {
            Assert.That(Route(HeroInput.UseKey), Is.EqualTo(HeroInputRoute.Use));
            Assert.That(Route(HeroInput.UseKey, stance: true), Is.EqualTo(HeroInputRoute.Use));
        }
    }

    public class StackTransferRepeatTests
    {
        [Test]
        public void PressMovesOnePieceAndNothingMoreBeforeTheFirstDelay()
        {
            var r = new StackTransferRepeat();
            Assert.That(r.Press(10f), Is.EqualTo(1));
            Assert.That(r.Poll(10.32f), Is.EqualTo(0));
            Assert.That(r.Poll(10.331f), Is.EqualTo(1));
            Assert.That(r.Poll(10.36f), Is.EqualTo(0));
            Assert.That(r.Poll(10.381f), Is.EqualTo(1)); // then every 0.05 s
        }

        [Test]
        public void StepGrowsTenfoldAfterEveryTenRepeats()
        {
            var r = new StackTransferRepeat();
            r.Press(0f);
            float t = StackTransferRepeat.FirstDelay + 0.001f;
            int[] steps = new int[25];
            for (int i = 0; i < steps.Length; i++, t += StackTransferRepeat.Interval) steps[i] = r.Poll(t);
            Assert.That(steps[0], Is.EqualTo(1));
            Assert.That(steps[9], Is.EqualTo(1));
            Assert.That(steps[10], Is.EqualTo(10));
            Assert.That(steps[19], Is.EqualTo(10));
            Assert.That(steps[20], Is.EqualTo(100));
        }

        [Test]
        public void ALongFrameSumsItsRepeatsAndReleaseStops()
        {
            var r = new StackTransferRepeat();
            r.Press(0f);
            Assert.That(r.Poll(0.331f + 0.05f * 2), Is.EqualTo(3));
            r.Release();
            Assert.That(r.Held, Is.False);
            Assert.That(r.Poll(5f), Is.EqualTo(0));
        }
    }
}
