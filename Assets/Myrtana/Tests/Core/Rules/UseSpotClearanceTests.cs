using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-103: a use spot inside the used object's collision is moved out (the hero got locked inside a Beliar shrine).</summary>
    public class UseSpotClearanceTests
    {
        const float Hero = 0.35f;

        // a shrine slab: 2.4 m wide, 1.6 m deep, centred at the origin, facing +z
        static bool FreeOfSlab(float x, float z) => !UseSpotClearance.CircleHitsBox(x, z, Hero, 0f, 0f, 1.2f, 0.8f, 0f);

        [Test]
        public void SpotInsideTheSlab_IsPushedOutAlongTheHerosSide()
        {
            // 0.9 m in front of the bounds centre: inside the 0.8 m half depth + the hero's radius
            Assert.That(FreeOfSlab(0f, 0.9f), Is.False, "the shrine's 0.9 m spot is embedded");
            Assert.That(UseSpotClearance.TryClear(0f, 0.9f, 0f, 0f, FreeOfSlab, null, out float x, out float z, out _), Is.True);
            Assert.That(x, Is.EqualTo(0f).Within(1e-3));
            Assert.That(z, Is.GreaterThanOrEqualTo(0.8f + Hero).And.LessThan(0.8f + Hero + UseSpotClearance.Step + 1e-3f), "just outside, same side");
            Assert.That(FreeOfSlab(x, z), Is.True);
        }

        [Test]
        public void FreeSpot_StaysWhereItIs()
        {
            Assert.That(UseSpotClearance.TryClear(0f, 2f, 0f, 0f, FreeOfSlab, null, out float x, out float z, out int tried), Is.True);
            Assert.That((x, z, tried), Is.EqualTo((0f, 2f, 1)));
        }

        [Test]
        public void BehindAWall_TurnsRoundTheObject()
        {
            // a thick wall right in front of the slab blocks the straight push: the spot turns round the object
            bool Free(float x, float z) => FreeOfSlab(x, z) && !UseSpotClearance.CircleHitsBox(x, z, Hero, 0f, 2.6f, 3f, 1.4f, 0f);
            Assert.That(UseSpotClearance.TryClear(0f, 0.9f, 0f, 0f, Free, null, out float rx, out float rz, out _), Is.True);
            Assert.That(Free(rx, rz), Is.True);
            Assert.That(System.Math.Abs(rx), Is.GreaterThan(1f), "beside the slab, not through the wall");
        }

        [Test]
        public void WalkableMeshIsPreferred_ThenOnlyRoom()
        {
            // NavMesh only beyond 1.5 m: the first pass finds it there
            Assert.That(UseSpotClearance.TryClear(0f, 0.9f, 0f, 0f, FreeOfSlab, (x, z) => z >= 1.5f, out _, out float z1, out _), Is.True);
            Assert.That(z1, Is.GreaterThanOrEqualTo(1.5f).And.LessThan(1.61f));
            // no NavMesh at all: room is enough
            Assert.That(UseSpotClearance.TryClear(0f, 0.9f, 0f, 0f, FreeOfSlab, (x, z) => false, out _, out float z2, out _), Is.True);
            Assert.That(z2, Is.LessThan(1.3f));
        }

        [Test]
        public void NothingFree_Fails_TheHeroStays()
        {
            Assert.That(UseSpotClearance.TryClear(0f, 0.9f, 0f, 0f, (x, z) => false, null, out float x0, out float z0, out _), Is.False);
            Assert.That((x0, z0), Is.EqualTo((0f, 0.9f)));
        }

        [Test]
        public void SpotAtTheCentre_UsesADefaultDirection()
        {
            Assert.That(UseSpotClearance.TryClear(0f, 0f, 0f, 0f, FreeOfSlab, null, out _, out float z, out _), Is.True);
            Assert.That(FreeOfSlab(0f, z), Is.True);
        }

        [Test]
        public void RotatedBox_Footprint()
        {
            // the slab turned 90 deg: 1.2 m half extent now along z
            Assert.That(UseSpotClearance.CircleHitsBox(0f, 1.4f, Hero, 0f, 0f, 1.2f, 0.8f, 90f), Is.True);
            Assert.That(UseSpotClearance.CircleHitsBox(1.2f, 0f, Hero, 0f, 0f, 1.2f, 0.8f, 90f), Is.False);
        }
    }
}
