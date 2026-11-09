using G3.Effects;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>fix-196: Spl_Light's orb (LightOrbRules): small, bright but bounded core, above the head, light brighter than import.</summary>
    public class LightOrbRulesTests
    {
        [Test]
        public void Sphere_StackedCore_IsBoundedNearCorePeak()
        {
            var look = LightOrbRules.For(LightOrbRules.SphereTemplate);
            Assert.That(look.Local, Is.True, "sprites move with the orb (no trail behind a running hero)");
            Assert.That(look.Size, Is.LessThan(0.6f));
            // 50 sprites × mean alpha 0.25 × Brighten gain 1.5 ≈ 19× white before; the look brings it to CorePeak
            float core = 50 * 0.25f * EmitterRules.ColorGain(6) * look.Alpha;
            Assert.That(core, Is.EqualTo(LightOrbRules.CorePeak).Within(0.01f));
            Assert.That(look.Alpha, Is.InRange(0.1f, 0.35f));
        }

        [Test]
        public void Glow_IsSmallAndFainter_LightIsBrighter_OthersUntouched()
        {
            var glow = LightOrbRules.For("vfx_magic_light_glow_01");
            Assert.That(glow.Size * 8f, Is.LessThanOrEqualTo(2f), "halo at most 2 m (G3 2-8 m)");
            Assert.That(glow.Alpha, Is.LessThan(1f));
            var light = LightOrbRules.For(LightOrbRules.LightTemplate);
            Assert.That(light.LightGain, Is.GreaterThan(1f));
            Assert.That(light.Shadows, Is.False);
            Assert.That(LightOrbRules.For(LightOrbRules.LightTemplate, shadows: true).Shadows, Is.True);
            Assert.That(LightOrbRules.For("VFX_Magic_Fireball_01").IsDefault, Is.True);
            Assert.That(EffectPartLook.Default.IsDefault, Is.True);
        }

        [Test]
        public void Orb_FloatsAboveTheHead()
        {
            Assert.That(LightOrbRules.OffsetUp, Is.GreaterThan(2f), "above a ~1.8 m hero, not at the chest / camera line");
            Assert.That(LightOrbRules.OffsetRight, Is.InRange(0f, 0.6f));
        }

        [Test]
        public void StackAlphaScale_NeverAboveOne()
        {
            Assert.That(LightOrbRules.StackAlphaScale(2, 0.1f, 1.5f, 3f), Is.EqualTo(1f));
            Assert.That(LightOrbRules.StackAlphaScale(0, 0.5f, 1.5f, 3f), Is.EqualTo(1f));
            Assert.That(LightOrbRules.StackAlphaScale(20, 0.5f, 1f, 2f), Is.EqualTo(0.2f).Within(1e-5f));
        }
    }
}
