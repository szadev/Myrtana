using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Body and hit shapes from the skeleton's extent (CombatBodyShape): long animals lie, upright bodies stand.</summary>
    public class CombatBodyShapeTests
    {
        // Joint extents incl. flesh, m: a wolf (0.6 wide, back 0.9, 1.6 long) and a human (0.8 wide, 1.95 high, 0.5 deep).
        const float WolfW = 0.6f, WolfH = 0.9f, WolfL = 1.6f, HumanW = 0.8f, HumanH = 1.95f, HumanL = 0.5f;

        [Test]
        public void Wolf_LiesAlongTheBody_TopIsItsBackNotItsLength()
        {
            var body = CombatBodyShape.Body(WolfW, WolfH, WolfL, 0.1f);
            Assert.That(body.Axis, Is.EqualTo(2));
            Assert.That(body.Length, Is.EqualTo(1.44f).Within(1e-3));
            Assert.That(body.Top, Is.EqualTo(0.75f).Within(1e-3), "a bite at 0.6 × top, not 0.6 × the 1.44 m length");
            Assert.That(body.CenterZ, Is.EqualTo(0.1f));
        }

        [Test]
        public void Human_IsUpright()
        {
            var body = CombatBodyShape.Body(HumanW, HumanH, HumanL, 0f);
            Assert.That(body.Axis, Is.EqualTo(1));
            Assert.That(body.Radius, Is.EqualTo(0.24f).Within(1e-3));
            Assert.That(body.Top, Is.EqualTo(HumanH).Within(1e-3));
        }
    }
}
