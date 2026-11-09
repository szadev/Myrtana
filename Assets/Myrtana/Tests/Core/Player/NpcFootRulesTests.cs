using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-60: NPC feet stay on collision (G3 gravity), never at a path corner's or route point's height in the air.</summary>
    public class NpcFootRulesTests
    {
        [Test]
        public void NoFloorUnderTheStepRefusesItOnNavMeshAndRouteWalks()
        {
            // Faring: a guard on the round tower's top (99.6) walks towards "Stand" 117.6 (Faring_Tower_Front): the step off
            // the battlements has no floor within the probe -> it stays, it does not rise to 117.6 or keep 99.6 over the street
            Assert.That(NpcFootRules.TryStep(99.6f, false, 0f, direct: false, overWater: false, maxDown: 1.5f, carriedY: 117.6f, out float y), Is.False);
            Assert.That(y, Is.EqualTo(99.6f));
            Assert.That(NpcFootRules.TryStep(99.6f, false, 0f, direct: true, overWater: false, maxDown: 1.5f, carriedY: 99.0f, out y), Is.False);
            Assert.That(y, Is.EqualTo(99.6f));
        }

        [Test]
        public void FloorsAreFollowedUpAndDownWithinTheRules()
        {
            Assert.That(NpcFootRules.TryStep(80f, true, 80.4f, false, false, 1.5f, 90f, out float y), Is.True);
            Assert.That(y, Is.EqualTo(80.4f), "a stair step up");
            Assert.That(NpcFootRules.TryStep(80f, true, 79f, false, false, 1.5f, 70f, out y), Is.True);
            Assert.That(y, Is.EqualTo(79f), "down a slope");
            Assert.That(NpcFootRules.TryStep(80f, true, 77f, false, false, 1.5f, 70f, out y), Is.False, "a ledge on a NavMesh walk");
            Assert.That(NpcFootRules.TryStep(80f, true, 77f, true, false, 1.5f, 70f, out y), Is.True, "the designer's route goes down it");
            Assert.That(y, Is.EqualTo(77f));
        }

        [Test]
        public void ABodyOverWaterKeepsItsHeightForTheSurfaceRule()
        {
            Assert.That(NpcFootRules.TryStep(5f, false, 0f, false, true, 1.5f, 4f, out float y), Is.True);
            Assert.That(y, Is.EqualTo(4f));
        }
    }
}
