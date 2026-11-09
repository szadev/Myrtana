using System;
using System.Linq;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>Remaster GI (Docs/remaster-world.md section 16): toroidal volumes, screen injection, probe irradiance, ratio, cloud shadows.</summary>
    [TestFixture]
    public class WorldGiTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        [Test]
        public void Mod_IsEuclidean()
        {
            Assert.AreEqual(3, WorldGi.Mod(-1, 4));
            Assert.AreEqual(0, WorldGi.Mod(-128, 128));
            Assert.AreEqual(5, WorldGi.Mod(133, 128));
        }

        [Test]
        public void Origin_CentresCameraAndSnapsToFourVoxels()
        {
            var o = WorldGi.Origin(100f, 50f, -30f, 1.25f);
            Assert.AreEqual(0, o.x % 4); Assert.AreEqual(0, o.y % 4); Assert.AreEqual(0, o.z % 4);
            float cx = (100f / 1.25f - o.x), cy = (50f / 1.25f - o.y), cz = (-30f / 1.25f - o.z);
            Assert.That(cx, Is.InRange(WorldGi.VoxX / 2 - 4, WorldGi.VoxX / 2 + 4));
            Assert.That(cy, Is.InRange(WorldGi.VoxY * 0.4f - 4, WorldGi.VoxY * 0.4f + 4), "camera at 40 % of the height");
            Assert.That(cz, Is.InRange(WorldGi.VoxZ / 2 - 4, WorldGi.VoxZ / 2 + 4));
            // a small move keeps the origin (no scroll every frame)
            Assert.AreEqual(o, WorldGi.Origin(100.5f, 50.2f, -29.5f, 1.25f));
        }

        [Test]
        public void Toroidal_EveryWorldIndexOnce_AndScrollClearsOnlyEnteredSlabs()
        {
            const int dim = 16;
            int origin = -5;
            var w = Enumerable.Range(0, dim).Select(s => WorldGi.WorldIndex(s, origin, dim)).OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(origin, dim).ToArray(), w);
            // window moves +4: exactly 4 storage cells hold new world indices, and they hold old - entered = the 4 that left
            int entered = Enumerable.Range(0, dim).Count(s => WorldGi.Entered(s, origin, origin + 4, dim));
            Assert.AreEqual(4, entered);
            for (int s = 0; s < dim; s++)
                if (!WorldGi.Entered(s, origin, origin + 4, dim))
                    Assert.AreEqual(WorldGi.WorldIndex(s, origin, dim), WorldGi.WorldIndex(s, origin + 4, dim), "kept cells keep their world index");
            Assert.AreEqual(0, Enumerable.Range(0, dim).Count(s => WorldGi.Entered(s, origin, origin, dim)));
        }

        [Test]
        public void Classify_SurfaceEmptyHidden()
        {
            Assert.AreEqual(WorldGi.Seen.Surface, WorldGi.Classify(10.3f, 10f, 1.25f, true));
            Assert.AreEqual(WorldGi.Seen.Empty, WorldGi.Classify(30f, 10f, 1.25f, true));
            Assert.AreEqual(WorldGi.Seen.Empty, WorldGi.Classify(float.PositiveInfinity, 10f, 1.25f, true), "sky behind: empty");
            Assert.AreEqual(WorldGi.Seen.Hidden, WorldGi.Classify(4f, 10f, 1.25f, true));
            Assert.AreEqual(WorldGi.Seen.Outside, WorldGi.Classify(10f, 10f, 1.25f, false));
            Assert.AreEqual(WorldGi.Seen.Outside, WorldGi.Classify(10f, 0.1f, 1.25f, true), "behind / at the camera");
        }

        [Test]
        public void Inject_FirstObservationTaken_ThenBlended_HiddenKept()
        {
            var unknown = (0f, 0f, 0f, -1f);
            var a = WorldGi.Inject(unknown, WorldGi.Seen.Surface, 0.8f, 0.4f, 0.2f, 0.35f);
            Assert.AreEqual((0.8f, 0.4f, 0.2f, 1f), a);
            var b = WorldGi.Inject(a, WorldGi.Seen.Surface, 0f, 0f, 0f, 0.5f);
            Assert.AreEqual(0.4f, b.r, 1e-6); Assert.AreEqual(1f, b.a, 1e-6);
            var c = WorldGi.Inject(b, WorldGi.Seen.Empty, 0, 0, 0, 0.5f);
            Assert.AreEqual(0.5f, c.a, 1e-6, "seen through: occupancy decays");
            Assert.AreEqual(c, WorldGi.Inject(c, WorldGi.Seen.Hidden, 9, 9, 9, 0.5f));
            Assert.AreEqual((0f, 0f, 0f, 0f), WorldGi.Inject(unknown, WorldGi.Seen.Empty, 1, 1, 1, 0.3f));
        }

        [Test]
        public void FaceIrradiance_ConstantRadianceGivesItself()
        {
            const int n = 256;
            var dirs = Enumerable.Range(0, n).Select(i => WorldGi.Fibonacci(i, n)).ToArray();
            var rad = Enumerable.Repeat(0.7f, n).ToArray();
            foreach (var f in WorldGi.Faces)
                Assert.AreEqual(0.7f, WorldGi.FaceIrradiance(f, dirs, rad), 0.02f);
        }

        [Test]
        public void FaceIrradiance_SkyAboveGroundBelow_SeparatesLobes()
        {
            const int n = 256;
            var dirs = Enumerable.Range(0, n).Select(i => WorldGi.Fibonacci(i, n)).ToArray();
            var rad = dirs.Select(d => d.y > 0 ? 1f : 0.2f).ToArray();
            float up = WorldGi.FaceIrradiance(WorldGi.Faces[2], dirs, rad), down = WorldGi.FaceIrradiance(WorldGi.Faces[3], dirs, rad);
            float side = WorldGi.FaceIrradiance(WorldGi.Faces[0], dirs, rad);
            Assert.AreEqual(1f, up, 0.03f); Assert.AreEqual(0.2f, down, 0.03f); Assert.AreEqual(0.6f, side, 0.05f);
        }

        [Test]
        public void Fibonacci_UnitAndCoversBothHemispheres()
        {
            for (int i = 0; i < 16; i++)
            {
                var d = WorldGi.Fibonacci(i, 16);
                Assert.AreEqual(1f, Math.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z), 1e-4);
            }
            Assert.AreEqual(8, Enumerable.Range(0, 16).Count(i => WorldGi.Fibonacci(i, 16).y > 0));
        }

        [Test]
        public void EvalCube_WeightsBySquaredNormal()
        {
            var cube = new[] { 1f, 2f, 3f, 4f, 5f, 6f };
            Assert.AreEqual(3f, WorldGi.EvalCube(cube, 0, 1, 0), 1e-6);
            Assert.AreEqual(4f, WorldGi.EvalCube(cube, 0, -2, 0), 1e-6);
            Assert.AreEqual(0.5f * 1 + 0.5f * 5, WorldGi.EvalCube(cube, 1, 0, 1), 1e-5);
        }

        [Test]
        public void Inside_MostRaysBlockedAtOnce()
        {
            Assert.IsTrue(WorldGi.Inside(10, 16));
            Assert.IsFalse(WorldGi.Inside(6, 16), "a probe next to a wall (half the rays blocked) stays valid");
        }

        [Test]
        public void Hysteresis_FirstTakesNew()
        {
            Assert.AreEqual(2f, WorldGi.Hysteresis(0, 2, 0.8f, true));
            Assert.AreEqual(0.4f, WorldGi.Hysteresis(0, 2, 0.8f, false), 1e-6);
        }

        [Test]
        public void Weight_FadesAtWindowEdge()
        {
            var o = (0, 0, 0);
            float sp = 2.5f;
            Assert.AreEqual(1f, WorldGi.Weight(32 * sp, 16 * sp, 32 * sp, o, 1.25f, 1), 1e-5);
            Assert.AreEqual(0f, WorldGi.Weight(0.5f * sp, 16 * sp, 32 * sp, o, 1.25f, 1), 1e-5);
            Assert.That(WorldGi.Weight(2f * sp, 16 * sp, 32 * sp, o, 1.25f, 1), Is.InRange(0.3f, 0.7f));
            Assert.AreEqual(0.5f, WorldGi.Weight(32 * sp, 16 * sp, 32 * sp, o, 1.25f, 0.5f), 1e-5);
        }

        [Test]
        public void Ratio_GiReplacesAmbient_ClampedToMax()
        {
            // no GI weight = the old ratio
            Assert.AreEqual(WorldV2Light.Ratio(0.3f, 1.2f, 0f, 0.4f, 1f, 0.05f, 0.1f),
                WorldGi.Ratio(0.3f, 1.2f, 0f, 0.4f, 1f, 0.1f, 0f, 5f, 0f, 0.75f, 0.05f, 1f), 1e-6);
            // open sky, no bounce, lit: identity
            Assert.AreEqual(1f, WorldGi.Ratio(0.3f, 1.2f, 0, 1, 1, 0, 1f, 0f, 1, 0.75f, 0.05f, 1.15f), 1e-6);
            // a cave: no sky seen -> 1 - strength of G3's fill + the torch bounce
            Assert.AreEqual((0.3f * 0.25f + 0.03f) / 0.3f, WorldGi.Ratio(0.3f, 0, 0, 0.2f, 1, 0, 0f, 0.03f, 1, 0.75f, 0.05f, 1.15f), 1e-5);
            // a sunlit valley's bounce lifts above G3's frame, but at most to max
            Assert.AreEqual(1.15f, WorldGi.Ratio(0.3f, 0, 0, 1, 1, 0, 1f, 2f, 1, 0.75f, 0.05f, 1.15f), 1e-6);
            Assert.AreEqual(1f, WorldGi.Ratio(0, 0, 0, 0, 0, 0, 0, 0, 1, 0.75f, 0.05f, 1.15f), "black pixel untouched");
            // keep: traced darker than the map estimate (open forest) -> at most down to keep x the old ambient
            float old = WorldGi.Ratio(0.3f, 0, 0, 0.6f, 1, 0, 0, 0, 0, 0.75f, 0.05f, 1.15f);
            float kept = WorldGi.Ratio(0.3f, 0, 0, 0.6f, 1, 0, 0.1f, 0f, 1, 0.75f, 0.05f, 1.15f, 0.95f);
            Assert.AreEqual(old * 0.95f, kept, 1e-5);
            Assert.Greater(WorldGi.Ratio(0.3f, 0, 0, 0.6f, 1, 0, 0.6f, 0.2f, 1, 0.75f, 0.05f, 1.15f, 0.95f), old, "bounce still adds");
        }

        [Test]
        public void Face_HitsAndEscapesSplit()
        {
            const int n = 256;
            var dirs = Enumerable.Range(0, n).Select(i => WorldGi.Fibonacci(i, n)).ToArray();
            // a cave ceiling over the upper hemisphere: rays up hit (radiance 0.1), rays down escape (open below)
            var hit = dirs.Select(d => d.y > 0 ? 0.1f : 0f).ToArray();
            var esc = dirs.Select(d => d.y > 0 ? 0f : 1f).ToArray();
            var up = WorldGi.Face(WorldGi.Faces[2], dirs, hit, esc);
            var down = WorldGi.Face(WorldGi.Faces[3], dirs, hit, esc);
            Assert.AreEqual(0.1f, up.hit, 0.01f); Assert.AreEqual(0f, up.vis, 1e-6);
            Assert.AreEqual(0f, down.hit, 1e-6); Assert.AreEqual(1f, down.vis, 1e-6);
        }

        [Test]
        public void SunOffset_FollowsSunAndCapsLowSun()
        {
            var o = WorldGi.SunOffset(0.6f, 0.8f, 0f, 2000f);
            Assert.AreEqual(1500f, o.dx, 1e-3); Assert.AreEqual(0f, o.dz, 1e-6);
            var low = WorldGi.SunOffset(1f, 0.01f, 0f, 2000f);
            Assert.That(low.dx, Is.LessThanOrEqualTo(8000f));
        }

        [Test]
        public void CloudShadow_StrengthBlends()
        {
            Assert.AreEqual(1f, WorldGi.CloudShadow(0, 0.8f), 1e-6);
            Assert.AreEqual(1f, WorldGi.CloudShadow(50, 0f), 1e-6);
            Assert.AreEqual(0.2f, WorldGi.CloudShadow(50, 0.8f), 1e-4);
        }

        [Test]
        public void Settings_DefaultsOnAndClamped_GlobalZeroWhenOff()
        {
            var s = WorldLookSettings.Default;
            Assert.IsFalse(s.v2Gi, "off by default (doc 16.10)"); Assert.IsTrue(s.v2CloudShadows);
            Assert.IsFalse(WorldGi.Wanted);
            s.v2Gi = true;
            WorldRemasterLook.Current = s;
            Assert.IsTrue(WorldGi.Wanted);
            Assert.That(WorldGi.CostMs(), Is.InRange(0.05f, 1.5f), "within the owner's +1.5 ms");
            s.v2Gi = false;
            WorldRemasterLook.Current = s;
            Assert.IsFalse(WorldGi.Wanted);
            CollectionAssert.AreEqual(new float[4], WorldGi.GiGlobal(3));
        }
    }
}
