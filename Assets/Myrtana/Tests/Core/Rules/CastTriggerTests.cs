using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-114: a cast goes off during its motion, not on the button's release (G3 OnMagicHandleInput, _AI_Cast).</summary>
    public sealed class CastTriggerTests
    {
        [TestCase(CastAction.Cast)]
        [TestCase(CastAction.QuickCast)]
        [TestCase(CastAction.Summon)]
        [TestCase(CastAction.FlameSword)]
        public void PlainCasts_GoOffByThemselves_TheReleaseDoesNothing(CastAction action)
        {
            Assert.That(CastTrigger.GoesOffByItself(action), Is.True);
            Assert.That(CastTrigger.GoesOff(action, powered: false), Is.True);
            Assert.That(CastTrigger.OnButtonUp(action, released: false, powered: false, heldSeconds: 0.1, powerHoldSeconds: 0.7),
                        Is.EqualTo(CastButtonUp.Nothing));
        }

        [Test]
        public void PowerCast_WaitsForItsPowerUp()
        {
            Assert.That(CastTrigger.GoesOffByItself(CastAction.PowerCast), Is.False);
            Assert.That(CastTrigger.GoesOff(CastAction.PowerCast, powered: false), Is.False);
            Assert.That(CastTrigger.GoesOff(CastAction.PowerCast, powered: true), Is.True);
        }

        [Test]
        public void PowerCast_LetGoBefore07s_RestartsPlain_LaterReleasesAreIgnored()
        {
            Assert.That(CastTrigger.OnButtonUp(CastAction.PowerCast, false, false, 0.69, 0.7), Is.EqualTo(CastButtonUp.RecastPlain));
            Assert.That(CastTrigger.OnButtonUp(CastAction.PowerCast, false, false, 0.7, 0.7), Is.EqualTo(CastButtonUp.Nothing));
            Assert.That(CastTrigger.OnButtonUp(CastAction.PowerCast, false, true, 0.5, 0.7), Is.EqualTo(CastButtonUp.Nothing), "powered");
            Assert.That(CastTrigger.OnButtonUp(CastAction.PowerCast, true, false, 0.2, 0.7), Is.EqualTo(CastButtonUp.Nothing), "already gone");
        }
    }
}
