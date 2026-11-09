using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The hero who fell out of the world is put back (<see cref="WorldCatch"/>, interp).</summary>
    public class WorldCatchTests
    {
        [Test]
        public void FallingIntoTheVoid_IsCaughtAfterASecond()
        {
            Assert.That(WorldCatch.ShouldCatch(0.5f, groundBelow: false, feetY: 20f), Is.False, "a jump");
            Assert.That(WorldCatch.ShouldCatch(1.2f, groundBelow: false, feetY: 20f), Is.True, "under the terrain");
        }

        [Test]
        public void RealFalls_AreNeverCaught_ButTheWorldFloorAlwaysIs()
        {
            Assert.That(WorldCatch.ShouldCatch(5f, groundBelow: true, feetY: 100f), Is.False, "a long fall off a cliff");
            Assert.That(WorldCatch.ShouldCatch(0.1f, groundBelow: true, feetY: -1800f), Is.True);
        }

        [Test]
        public void CaughtHero_GoesOnTheSurfaceHeFellThrough_NotTheTopmost()
        {
            // fix-38 (r27 trelis_01): fell to -4.5 under ground at 5, a rock top at 60 above -> 5, not 60
            float[] surfaces = { 60f, 5f, -20f };
            float? Cast(float from)
            {
                foreach (var y in surfaces) if (y <= from) return y; // the first surface below 'from'
                return null;
            }
            Assert.That(WorldCatch.LowestSurfaceAbove(-4.5f, 2000f, Cast), Is.EqualTo(5f));
            Assert.That(WorldCatch.LowestSurfaceAbove(70f, 2070f, Cast), Is.Null, "nothing above");
            Assert.That(WorldCatch.LowestSurfaceAbove(-30f, 2000f, f => null), Is.Null);
        }
    }
}
