using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class CameraBoomTests
    {
        const float MinWidth = 0.6f;

        [Test]
        public void Wall_Blocks() => Assert.That(CameraBoom.Blocks(1.5f, false, 8f, MinWidth), Is.True);

        [Test]
        public void ThinPost_DoesNotBlock() => Assert.That(CameraBoom.Blocks(1.5f, false, 0.3f, MinWidth), Is.False);

        [Test]
        public void OwnBody_DoesNotBlock() => Assert.That(CameraBoom.Blocks(0.4f, true, 0.7f, MinWidth), Is.False);

        [Test]
        public void StartedInside_DoesNotBlock() => Assert.That(CameraBoom.Blocks(0f, false, 50f, MinWidth), Is.False);

        [Test]
        public void PlanWidth_IsTheLargerHorizontalSide() => Assert.That(CameraBoom.PlanWidth(0.2f, 3f), Is.EqualTo(3f));

        [Test]
        public void Follow_PullsInAtOnce()
        {
            float v = 1f;
            Assert.That(CameraBoom.Follow(3f, 1.2f, ref v, 0.3f, 1 / 60f), Is.EqualTo(1.2f));
            Assert.That(v, Is.EqualTo(0f));
        }

        [Test]
        public void Follow_FirstPlacementSnaps()
        {
            float v = 0;
            Assert.That(CameraBoom.Follow(-1f, 3f, ref v, 0.3f, 1 / 60f), Is.EqualTo(3f));
        }

        [Test]
        public void Follow_EasesOutWithoutJumpsOrOvershoot()
        {
            float d = 1f, v = 0f, maxStep = 0f;
            for (int i = 0; i < 120; i++)
            {
                float next = CameraBoom.Follow(d, 3f, ref v, 0.3f, 1 / 60f);
                Assert.That(next, Is.LessThanOrEqualTo(3f));
                Assert.That(next, Is.GreaterThanOrEqualTo(d));
                maxStep = System.Math.Max(maxStep, next - d);
                d = next;
            }
            Assert.That(d, Is.EqualTo(3f).Within(0.01f));
            Assert.That(maxStep, Is.LessThan(0.3f)); // never reads as a jump at 60 fps
        }

        [Test]
        public void Follow_IsFrameRateIndependent()
        {
            float a = 1f, va = 0f, b = 1f, vb = 0f;
            for (int i = 0; i < 30; i++) a = CameraBoom.Follow(a, 3f, ref va, 0.3f, 1 / 120f);
            for (int i = 0; i < 5; i++) b = CameraBoom.Follow(b, 3f, ref vb, 0.3f, 1 / 20f);
            Assert.That(b, Is.EqualTo(a).Within(0.05f));
        }

        [Test]
        public void Follow_HitchFrameDoesNotOvershoot()
        {
            float v = 0f;
            Assert.That(CameraBoom.Follow(1f, 3f, ref v, 0.3f, 0.5f), Is.LessThanOrEqualTo(3f));
        }

        [TestCase(3f, 2.6f, true)]
        [TestCase(3f, 2.8f, false)]
        [TestCase(-1f, 3f, false)]
        public void IsJump(float prev, float cur, bool jump) => Assert.That(CameraBoom.IsJump(prev, cur, 0.3f), Is.EqualTo(jump));
    
        [Test]
        public void UseOrbit_KeepsTheBack_UntilCramped_ThenTheFreestSide()
        {
            // fix-46 r28 par_misc: the water pipe in a hut, a wall right behind the sitting hero.
            float wanted = 1.5f;
            var free = new[] { 1.5f, 1.5f, 1.5f, 1.5f, 1.5f, 1.5f, 1.5f, 1.5f };
            Assert.That(CameraBoom.UseOrbit(free, wanted, 0), Is.EqualTo(0), "free behind: stays behind");
            free = new[] { 0.3f, 0.4f, 0.3f, 1.5f, 0.6f, 1.2f, 0.4f, 0.8f };
            Assert.That(CameraBoom.UseOrbit(free, wanted, 0), Is.EqualTo(3), "the freest side (90 deg)");
            free[3] = 1.1f;
            Assert.That(CameraBoom.UseOrbit(free, wanted, 3), Is.EqualTo(3), "still enough room: no hop");
            free[3] = 0.5f;
            Assert.That(CameraBoom.UseOrbit(free, wanted, 3), Is.EqualTo(5), "cramped again: the next freest");
            free[0] = 1.2f;
            Assert.That(CameraBoom.UseOrbit(free, wanted, 5), Is.EqualTo(0), "room behind again: back");
        }
    }
}
