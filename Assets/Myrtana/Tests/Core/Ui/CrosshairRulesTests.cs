using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class CrosshairRulesTests
    {
        [TestCase(0f, 31)]    // clamped to 1 m: t = 0.975 → 31.4
        [TestCase(1f, 31)]
        [TestCase(10f, 26)]   // t = 0.75 → 26
        [TestCase(20f, 20)]   // t = 0.5
        [TestCase(40f, 8)]
        [TestCase(300f, 8)]   // nothing under the centre: far → smallest
        public void Size_ShrinksWithTheAimDistance(float metres, int pixels) =>
            Assert.That(CrosshairRules.SizePixels(metres), Is.EqualTo(pixels));

        [Test]
        public void SizeFactor_IsTheOriginalFormula() =>
            Assert.That(CrosshairRules.SizeFactor(30f), Is.EqualTo(1f - 3000f / 4000f).Within(1e-5));

        [Test]
        public void Visible_OnlyWhileAimingARangedWeapon()
        {
            Assert.That(CrosshairRules.Visible(true, true, inAim: true, false, false, false), Is.True);
            Assert.That(CrosshairRules.Visible(true, true, false, inShot: true, false, false), Is.True, "through the shot");
            Assert.That(CrosshairRules.Visible(true, true, false, false, aimHeld: true, reloading: true), Is.True, "Aim_Reload");
            Assert.That(CrosshairRules.Visible(true, true, false, false, aimHeld: false, reloading: true), Is.False, "back to walk");
            Assert.That(CrosshairRules.Visible(true, true, false, false, false, false), Is.False, "bow drawn, not aimed");
            Assert.That(CrosshairRules.Visible(false, true, true, false, false, false), Is.False, "spells and melee have none");
            Assert.That(CrosshairRules.Visible(true, false, true, false, false, false), Is.False, "hit/knock-out resets");
        }

        [Test]
        public void Fade_Takes200Ms()
        {
            Assert.That(CrosshairRules.Fade(0f, true, 0.1f), Is.EqualTo(0.5f).Within(1e-5));
            Assert.That(CrosshairRules.Fade(0.9f, true, 0.1f), Is.EqualTo(1f));
            Assert.That(CrosshairRules.Fade(0.3f, false, 0.1f), Is.EqualTo(0f));
        }
    }
}
