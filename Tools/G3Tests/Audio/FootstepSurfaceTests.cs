using G3.Audio;
using G3.Formats.Audio;
using NUnit.Framework;

namespace G3Tests.Audio
{
    /// <summary>The runtime's eEShapeMaterial → footstep token table matches the importer's (Footsteps.MaterialToken).</summary>
    [TestFixture]
    public class FootstepSurfaceTests
    {
        [Test]
        public void ShapeMaterialTokensMatchTheEffectMap()
        {
            for (int m = 0; m <= 21; m++)
                Assert.AreEqual(Footsteps.MaterialToken(m), FootstepRules.SurfaceFromShapeMaterial(m), $"material {m}");
            Assert.AreEqual("stone", FootstepRules.SurfaceFromShapeMaterial(4));
            Assert.AreEqual("grass", FootstepRules.SurfaceFromShapeMaterial(15));
        }
    }
}
