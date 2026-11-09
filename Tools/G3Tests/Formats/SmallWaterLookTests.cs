using G3.Weather;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>fix-249: fountain basins and water barrels keep visible water (small-body variant of G3/Water).</summary>
    [TestFixture]
    public class SmallWaterLookTests
    {
        [Test]
        public void SmallBodies_AreFountainsAndBarrels_NotLandscapeWater()
        {
            Assert.That(SmallWaterLook.IsSmallBody(1.54f, 1.12f), Is.True, "G3_Object_Interact_Fountain_01 water");
            Assert.That(SmallWaterLook.IsSmallBody(0.79f, 0.79f), Is.True, "G3_Object_Interact_Barrel_01 water");
            Assert.That(SmallWaterLook.IsSmallBody(9.83f, 10.64f), Is.False, "G3_Varant_Ishtar_Rivermesh_02, smallest landscape water");
            Assert.That(SmallWaterLook.IsSmallBody(493f, 967f), Is.False, "Myrtana lake mesh sharing the material");
            Assert.That(SmallWaterLook.IsSmallBody(0f, 0f), Is.False);
            Assert.That(SmallWaterLook.IsSmallBody(float.NaN, 1f), Is.False);
        }

        [Test]
        public void ShallowBasin_ReadsAsWater()
        {
            // a few cm of water: the surface is opaque and tints the floor (the river material gave alpha 0.1, ~97 % floor)
            Assert.That(SmallWaterLook.Alpha(0.05f), Is.EqualTo(1f));
            Assert.That(SmallWaterLook.Alpha(0.0f), Is.EqualTo(0f), "soft only at the contact line");
            for (int c = 0; c < 3; c++)
                Assert.That(SmallWaterLook.Transmittance(c, 0.05f), Is.LessThan(0.7f), "channel " + c);
            Assert.That(SmallWaterLook.Transmittance(0, 0.05f), Is.LessThan(SmallWaterLook.Transmittance(1, 0.05f)), "red absorbed most");
        }
    }
}
