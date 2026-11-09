using System.Linq;
using G3.Formats.World;
using G3.Player;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-99: the NPC NavMesh is baked from one-sided copies of the two-sided collision meshes, oriented by the render
    /// mesh: a cave's ceiling keeps only its downward face (no NavMesh on its back inside the rock), its floor only the
    /// upward one; walls keep one copy; a surface without render triangles stays two-sided.
    /// </summary>
    [TestFixture]
    public class NavFacingRulesTests
    {
        // quad corners (x, z) 0..4 at height y; a b c d counter-clockwise seen from above
        static float[] Quad(float y) => new float[] { 0, y, 0, 4, y, 0, 4, y, 4, 0, y, 4 };

        static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

        /// <summary>Unity front face up for a quad seen from above: clockwise from above (a, d, c / a, c, b).</summary>
        static int[] Up(int b0) => new[] { b0, b0 + 3, b0 + 2, b0, b0 + 2, b0 + 1 };
        static int[] Down(int b0) => new[] { b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 };

        static float FrontY(float[] p, int a, int b, int c)
        {
            float ux = p[b * 3] - p[a * 3], uz = p[b * 3 + 2] - p[a * 3 + 2];
            float vx = p[c * 3] - p[a * 3], vz = p[c * 3 + 2] - p[a * 3 + 2];
            return uz * vx - ux * vz;
        }

        [Test]
        public void CaveCeiling_KeepsItsUnderside_FloorItsTop_UnrenderedStaysTwoSided()
        {
            // collision: floor (y 0), ceiling (y 3), a slab at y 6 without a render surface; two-sided as built
            var cp = Concat(Quad(0), Quad(3), Quad(6));
            var tris = Up(0).Concat(Up(4)).Concat(Up(8)).ToArray();
            var two = CollisionRules.TwoSided(cp, tris);
            // render (Unity winding): the floor seen from above, the ceiling seen from below
            var rp = Concat(Quad(0.02f), Quad(3.01f));
            var rt = Up(0).Concat(Down(4)).ToArray();

            var r = NavFacingRules.OneSided(cp, two, rp, rt);

            Assert.That(r.Pairs, Is.EqualTo(6));
            Assert.That((r.KeptUp, r.KeptDown, r.Unmatched), Is.EqualTo((2, 2, 2)));
            Assert.That(r.Triangles.Length, Is.EqualTo((2 + 2 + 4) * 3));
            for (int t = 0; t < r.Triangles.Length; t += 3)
            {
                int a = r.Triangles[t], b = r.Triangles[t + 1], c = r.Triangles[t + 2];
                float y = cp[a * 3 + 1];
                if (y == 0) Assert.That(FrontY(cp, a, b, c), Is.GreaterThan(0), "floor faces up");
                if (y == 3) Assert.That(FrontY(cp, a, b, c), Is.LessThan(0), "ceiling faces down: no NavMesh on its back");
            }
        }

        [Test]
        public void Walls_KeepOneCopy()
        {
            var cp = new float[] { 0, 0, 0, 4, 0, 0, 4, 3, 0, 0, 3, 0 };
            var two = CollisionRules.TwoSided(cp, new[] { 0, 1, 2, 0, 2, 3 });
            var r = NavFacingRules.OneSided(cp, two, new float[0], new int[0]);
            Assert.That(r.Steep, Is.EqualTo(2));
            Assert.That(r.Triangles.Length, Is.EqualTo(6));
        }
    }
}
