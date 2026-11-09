using System;
using System.Numerics;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-228: the drawn bow's aim pose (<see cref="BowAimPose"/>): what it points at, how far the body pitches.</summary>
    public class BowAimPoseTests
    {
        static Vector3 Dir(float pitchDeg)
        {
            double p = pitchDeg * Math.PI / 180.0;
            return new Vector3(0f, (float)Math.Sin(p), (float)Math.Cos(p));
        }

        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(-45f)]
        [TestCase(89f)]
        public void PitchOf_ReadsTheDirectionsElevation(float pitch) =>
            Assert.That(BowAimPose.PitchOf(Dir(pitch) * 7f), Is.EqualTo(pitch).Within(0.01f));

        [Test]
        public void PitchOf_ZeroDirectionIsLevel() => Assert.That(BowAimPose.PitchOf(Vector3.Zero), Is.EqualTo(0f));

        [Test]
        public void TargetPitch_StaysInTheNaturalRange()
        {
            var bow = new Vector3(0, 1.5f, 0);
            Assert.That(BowAimPose.TargetPitch(bow, bow + new Vector3(0, -10f, 1f)), Is.EqualTo(BowAimPose.MinPitch));
            Assert.That(BowAimPose.TargetPitch(bow, bow + new Vector3(0, 10f, 1f)), Is.EqualTo(BowAimPose.MaxPitch));
            Assert.That(BowAimPose.TargetPitch(bow, bow + new Vector3(0, 1f, 1f)), Is.EqualTo(45f).Within(0.01f));
        }

        /// <summary>
        /// The owner's shot: the hero on a slope, the camera high behind him looking down the hill, a hornet hovering at the
        /// crosshair. The screen-centre ray slips just past the small flyer and hits the slope far behind and below it: the
        /// bow pitched down at that ground point. With the focus under the crosshair it points at the hornet's body.
        /// </summary>
        [Test]
        public void FlyingFocusUnderTheCrosshair_IsAimedAtNotTheSlopeBehindIt()
        {
            var camera = new Vector3(0f, 3.2f, -3.5f);
            var bow = new Vector3(0.1f, 1.45f, 0.3f);
            var hornet = new Vector3(0.3f, 0.9f, 12f);          // hovering, a little below the hero (downhill)
            var forward = Vector3.Normalize(hornet + new Vector3(0f, -0.35f, 0f) - camera); // the ray passes just under it
            var slopeHit = camera + forward * 40f;              // where the ray meets the hillside
            var aim = BowAimPose.AimPoint(camera, forward, slopeHit, pickHit: true, focus: hornet);
            Assert.That(aim, Is.EqualTo(hornet));
            float toHornet = BowAimPose.TargetPitch(bow, aim);
            float toSlope = BowAimPose.TargetPitch(bow, slopeHit);
            Assert.That(toHornet, Is.GreaterThan(-6f).And.LessThan(0f), "a slight dip towards the hovering hornet");
            Assert.That(toSlope, Is.LessThan(toHornet - 5f), "the slope point behind it is much lower");
        }

        [Test]
        public void FocusOffTheCrosshair_LeavesThePickPoint()
        {
            var camera = Vector3.Zero;
            var forward = Vector3.UnitZ;
            var pick = new Vector3(0, 0, 30f);
            var focus = new Vector3(8f, 0, 20f); // ~22° aside
            Assert.That(BowAimPose.AimPoint(camera, forward, pick, true, focus), Is.EqualTo(pick));
            Assert.That(BowAimPose.AimPoint(camera, forward, pick, true, null), Is.EqualTo(pick));
        }

        [Test]
        public void SomethingNearerUnderTheCrosshair_BlocksTheFocus()
        {
            var camera = Vector3.Zero;
            var forward = Vector3.UnitZ;
            var focus = new Vector3(0.5f, 0, 20f);
            var tree = new Vector3(0, 0, 8f);
            Assert.That(BowAimPose.AimPoint(camera, forward, tree, true, focus), Is.EqualTo(tree));
            // the ray hit the focus's own body surface (nearer than its centre by less than the margin): the centre
            var surface = new Vector3(0.1f, 0, 19.6f);
            Assert.That(BowAimPose.AimPoint(camera, forward, surface, true, focus), Is.EqualTo(focus));
            // the ray hit nothing: the far point is no block
            Assert.That(BowAimPose.AimPoint(camera, forward, forward * 300f, false, focus), Is.EqualTo(focus));
        }

        /// <summary>The Down aim motion (much steeper than the target just below the ±25° split) is raised onto the target.</summary>
        [Test]
        public void Correction_TurnsTheAnimatedArrowOntoTheTarget()
        {
            Assert.That(BowAimPose.Correction(-27f, -50f), Is.EqualTo(23f).Within(0.01f));
            Assert.That(BowAimPose.Correction(10f, 2f), Is.EqualTo(8f).Within(0.01f));
            Assert.That(BowAimPose.Correction(0f, 0f), Is.EqualTo(0f));
        }

        [Test]
        public void Correction_IsLimited()
        {
            Assert.That(BowAimPose.Correction(-60f, 30f), Is.EqualTo(-BowAimPose.MaxCorrection));
            Assert.That(BowAimPose.Correction(80f, 0f), Is.EqualTo(BowAimPose.MaxCorrection));
            // the target itself is limited first: a camera looking straight down never bends the hero past -60°
            Assert.That(BowAimPose.Correction(-89f, -40f), Is.EqualTo(BowAimPose.MinPitch + 40f).Within(0.01f));
        }

        [Test]
        public void Ease_ApproachesWithoutOvershootAndIndependentOfFrameRate()
        {
            float a = 0f, b = 0f;
            for (int i = 0; i < 30; i++) a = BowAimPose.Ease(a, 20f, BowAimPose.CorrectionRate, 1f / 30f);
            for (int i = 0; i < 120; i++) b = BowAimPose.Ease(b, 20f, BowAimPose.CorrectionRate, 1f / 120f);
            Assert.That(a, Is.EqualTo(b).Within(0.01f));
            Assert.That(a, Is.LessThanOrEqualTo(20f).And.GreaterThan(19.9f));
            Assert.That(BowAimPose.Ease(5f, 20f, BowAimPose.CorrectionRate, 0f), Is.EqualTo(5f));
            float one = BowAimPose.Ease(0f, 20f, BowAimPose.CorrectionRate, 1f / 60f);
            Assert.That(one, Is.GreaterThan(0f).And.LessThan(5f), "smooth: one frame moves a fraction of the way");
        }

        // fix-257: the point under the crosshair seen from the hero's shoulder (owner: a low camera in a Varant dune hollow,
        // the bow pointing steeply up while the crosshair was on a foe ahead).

        static readonly Vector3 Shoulder = new Vector3(0f, 1.45f, 0f);

        /// <summary>A flat collision ground at 0 with a drawn dune: rising 0.5 m per m from z = 4 up to 4 m, then flat.</summary>
        static bool Dune(float x, float y, float z, out float col, out float vis)
        {
            col = 0f;
            vis = Math.Clamp((z - 4f) * 0.5f, 0f, 4f);
            return true;
        }

        [Test]
        public void LowCameraLookingUp_FocusAtShoulderHeight_AimsNearlyLevel()
        {
            var camera = new Vector3(0.4f, 0.45f, -2f);   // pulled in low behind the hero
            var forward = Dir(9f);                          // the crosshair a little above the foe
            var foe = new Vector3(0f, 1.5f, 16f);          // on level ground, at the hero's height
            var pick = BowAimPose.CrosshairPoint(camera, forward, null, null, 300f, out bool hit);
            var aim = BowAimPose.AimPoint(camera, forward, pick, hit, foe);
            Assert.That(aim, Is.EqualTo(foe));
            float pitch = BowAimPose.TargetPitch(Shoulder, aim);
            Assert.That(Math.Abs(pitch), Is.LessThan(2f), "level foe: level bow, not the camera's 9 deg look");
        }

        [Test]
        public void LowCamera_CrosshairOnAFoeAtShoulderHeight_AimsNearlyLevel()
        {
            var camera = new Vector3(0.4f, 0.45f, -2f);
            var foe = new Vector3(0f, 1.5f, 14f);
            var forward = foe - camera;
            var pick = BowAimPose.CrosshairPoint(camera, forward, forward.Length(), null, 300f, out bool hit);
            Assert.That(hit, Is.True);
            Assert.That(Math.Abs(BowAimPose.TargetPitch(Shoulder, pick)), Is.LessThan(1f));
        }

        [TestCase(2f)]
        [TestCase(5f)]
        [TestCase(10f)]
        public void TargetAbove_PitchIsTheElevationFromTheShoulder(float height)
        {
            var target = new Vector3(0f, Shoulder.Y + height, 15f);
            float expected = (float)(Math.Atan2(height, 15f) * 180.0 / Math.PI);
            Assert.That(BowAimPose.TargetPitch(Shoulder, target), Is.EqualTo(expected).Within(0.1f));
        }

        [Test]
        public void CrosshairOnTheSky_AimsAlongTheRay()
        {
            var camera = new Vector3(0.4f, 0.6f, -2.5f);
            var pick = BowAimPose.CrosshairPoint(camera, Dir(35f), null, null, 300f, out bool hit);
            Assert.That(hit, Is.False);
            Assert.That(BowAimPose.TargetPitch(Shoulder, BowAimPose.AimPoint(camera, Dir(35f), pick, hit, null)),
                        Is.EqualTo(35f).Within(1f));
            var steep = BowAimPose.CrosshairPoint(camera, Dir(80f), null, null, 300f, out _);
            Assert.That(BowAimPose.TargetPitch(Shoulder, steep), Is.EqualTo(BowAimPose.MaxPitch));
        }

        [Test]
        public void ReliefHit_FindsTheDrawnDuneUnderTheCrosshair()
        {
            var camera = new Vector3(0f, 0.5f, -2f);
            var forward = Dir(10f);
            Assert.That(BowAimPose.ReliefHit(Dune, camera, forward, 0.5f, 150f, out float d), Is.True);
            var p = camera + forward * d;
            Dune(p.X, p.Y, p.Z, out _, out float vis);
            Assert.That(p.Y, Is.EqualTo(vis).Within(0.02f), "on the drawn surface");
            Assert.That(p.Z, Is.InRange(4f, 12f));
            // the dune, not 300 m along the ray: the bow points at the slope in front, much lower than the far point
            var pick = BowAimPose.CrosshairPoint(camera, forward, null, d, 300f, out bool hit);
            Assert.That(hit, Is.True);
            Assert.That(Vector3.Distance(pick, p), Is.LessThan(1e-3f));
        }

        [Test]
        public void ReliefHit_RayOverTheDuneMisses() =>
            Assert.That(BowAimPose.ReliefHit(Dune, new Vector3(0f, 0.5f, -2f), Dir(40f), 0.5f, 150f, out _), Is.False);

        [Test]
        public void ReliefHit_RayStartingInsideCountsOnlyAfterLeavingIt()
        {
            // a near plane in the drawn ground of the hollow (z < 1: 0.3 m drawn) then out, then the dune
            static bool Hollow(float x, float y, float z, out float col, out float vis)
            {
                col = 0f;
                vis = z < 1f ? 0.3f : Math.Clamp((z - 4f) * 0.5f, 0f, 4f);
                return true;
            }
            var camera = new Vector3(0f, 0.2f, -1f);
            Assert.That(BowAimPose.ReliefHit(Hollow, camera, Dir(5f), 0f, 150f, out float d), Is.True);
            Assert.That((camera + Dir(5f) * d).Z, Is.GreaterThan(4f));
        }

        [Test]
        public void ReliefHit_IgnoresPointsUnderTheCollisionGround()
        {
            static bool Cave(float x, float y, float z, out float col, out float vis) { col = 5f; vis = 6f; return true; }
            Assert.That(BowAimPose.ReliefHit(Cave, new Vector3(0f, 1f, 0f), Dir(0f), 0f, 50f, out _), Is.False);
        }

        [Test]
        public void CrosshairPoint_TakesTheNearerOfColliderAndRelief()
        {
            var camera = Vector3.Zero;
            var f = Vector3.UnitZ;
            Assert.That(BowAimPose.CrosshairPoint(camera, f, 20f, 8f, 300f, out _).Z, Is.EqualTo(8f).Within(1e-4f));
            Assert.That(BowAimPose.CrosshairPoint(camera, f, 6f, 8f, 300f, out _).Z, Is.EqualTo(6f).Within(1e-4f));
            Assert.That(BowAimPose.CrosshairPoint(camera, f * 3f, null, null, 300f, out bool hit).Z, Is.EqualTo(300f).Within(1e-3f));
            Assert.That(hit, Is.False);
        }

        [Test]
        public void FocusBehindADuneUnderTheCrosshair_TheDuneBlocksIt()
        {
            var camera = new Vector3(0f, 0.5f, -2f);
            var forward = Dir(10f);
            BowAimPose.ReliefHit(Dune, camera, forward, 0.5f, 150f, out float d);
            var pick = BowAimPose.CrosshairPoint(camera, forward, null, d, 300f, out bool hit);
            var foe = camera + forward * (d + 10f) - new Vector3(0f, 0.5f, 0f);
            Assert.That(BowAimPose.AimPoint(camera, forward, pick, hit, foe), Is.EqualTo(pick));
        }
    }
}
