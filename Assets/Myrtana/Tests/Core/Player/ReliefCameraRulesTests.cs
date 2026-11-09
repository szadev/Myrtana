using System;
using System.Numerics;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-240: the third-person camera against the drawn terrain relief (<see cref="ReliefCameraRules"/>).</summary>
    public class ReliefCameraRulesTests
    {
        const float Dt = 1f / 60f;

        /// <summary>A ground of collision height <paramref name="col"/> and drawn height col + <paramref name="relief"/>.</summary>
        static ReliefCameraRules.Ground Field(Func<float, float, float> col, Func<float, float, float> relief) =>
            (float x, float y, float z, out float c, out float v) => { c = col(x, z); v = c + relief(x, z); return true; };

        static readonly ReliefCameraRules.Ground None = (float x, float y, float z, out float c, out float v) => { c = v = y; return false; };

        static float Drawn(ReliefCameraRules.Ground g, Vector3 p) { g(p.X, p.Y, p.Z, out _, out float v); return v; }

        [Test]
        public void EndInsideRaisedFlatGround_IsLiftedToTheClearance()
        {
            var g = Field((x, z) => 0f, (x, z) => 0.6f);
            var s = new ReliefCameraRules.State();
            var end = ReliefCameraRules.Follow(g, new Vector3(0, 1.7f, 0), new Vector3(0, 0.18f, -2.5f), ref s, Dt);
            Assert.That(end.Y, Is.EqualTo(0.6f + ReliefCameraRules.Clearance).Within(1e-4f));
            Assert.That(end.Z, Is.EqualTo(-2.5f).Within(1e-4f), "a lift, not a pull-in");
        }

        [Test]
        public void NoRelief_DrawnBelowCollision_OrUnderTheCollision_LeaveTheCameraAlone()
        {
            var from = new Vector3(0, 1.7f, 0);
            var at = new Vector3(0, 0.18f, -2.5f);
            var s = new ReliefCameraRules.State();
            Assert.That(ReliefCameraRules.Follow(None, from, at, ref s, Dt), Is.EqualTo(at));
            s = default;
            Assert.That(ReliefCameraRules.Follow(Field((x, z) => 0f, (x, z) => -0.3f), from, at, ref s, Dt), Is.EqualTo(at));
            // a cave roof: the collision ground found at this XZ is 3 m above the camera
            s = default;
            var roof = Field((x, z) => 3f, (x, z) => 0.5f);
            Assert.That(ReliefCameraRules.Follow(roof, from, at, ref s, Dt), Is.EqualTo(at));
        }

        [Test]
        public void OnASlope_TheClearanceIsAlongTheNormal()
        {
            const float slope = 1f;   // 45 deg rising behind the hero (towards -z)
            var g = Field((x, z) => -z * slope, (x, z) => 0.5f);
            Assert.That(ReliefCameraRules.Floor(g, new Vector3(0, 2f, -2f), ReliefCameraRules.Clearance, out float floor), Is.True);
            float ny = 1f / (float)Math.Sqrt(1 + slope * slope);
            Assert.That(floor, Is.EqualTo(2f + 0.5f + ReliefCameraRules.Clearance / ny).Within(1e-3f));
        }

        [Test]
        public void SwellBetweenHeroAndCamera_PullsTheCameraInFrontOfIt()
        {
            // a 1.5 m swell centred 2 m behind the hero, flat collision; the camera 4 m back, low
            var g = Field((x, z) => 0f, (x, z) => 1.5f * (float)Math.Exp(-(z + 2f) * (z + 2f) / 0.3f));
            var from = new Vector3(0, 1.0f, 0);
            var at = new Vector3(0, 0.3f, -4f);
            var s = new ReliefCameraRules.State();
            var end = ReliefCameraRules.Follow(g, from, at, ref s, Dt);
            Assert.That(end.Z, Is.GreaterThan(-2f), "in front of the swell");
            Assert.That(end.Y, Is.GreaterThanOrEqualTo(Drawn(g, end) + ReliefCameraRules.Clearance - 1e-3f));
            Assert.That(ReliefCameraRules.Reach(g, from, end), Is.EqualTo(1f), "the hero is in view");
        }

        [Test]
        public void TooDeepToRideUp_PullsInInstead()
        {
            // a 4 m wall of drawn relief from 1 m behind the hero
            var g = Field((x, z) => 0f, (x, z) => z < -1f ? 4f : 0f);
            var from = new Vector3(0, 1.7f, 0);
            var s = new ReliefCameraRules.State();
            var end = ReliefCameraRules.Follow(g, from, new Vector3(0, 0.5f, -3f), ref s, Dt);
            Assert.That(end.Z, Is.GreaterThan(-1f));
            Assert.That(end.Y, Is.LessThan(2f), "not lifted over the wall");
        }

        [Test]
        public void Lift_ComesInAtOnce_AndEasesOut()
        {
            float h = 0.8f;
            var g = Field((x, z) => 0f, (x, z) => h);
            var from = new Vector3(0, 1.7f, 0);
            var at = new Vector3(0, 0.2f, -2.5f);
            var s = new ReliefCameraRules.State();
            float y0 = ReliefCameraRules.Follow(g, from, at, ref s, Dt).Y;
            Assert.That(y0, Is.EqualTo(h + ReliefCameraRules.Clearance).Within(1e-4f));
            h = 0.1f;   // the relief under the camera drops (it moved past the swell)
            float y1 = ReliefCameraRules.Follow(g, from, at, ref s, Dt).Y;
            Assert.That(y1, Is.LessThan(y0).And.GreaterThan(0.1f + ReliefCameraRules.Clearance + 0.3f), "eased, no jump");
            float y = y1;
            for (int i = 0; i < 120; i++) y = ReliefCameraRules.Follow(g, from, at, ref s, Dt).Y;
            Assert.That(y, Is.EqualTo(0.1f + ReliefCameraRules.Clearance).Within(0.01f));
        }

        [Test]
        public void SweepOverHillyRelief_NeverEndsInsideTheDrawnGround()
        {
            // rolling collision with up to 1.2 m swells on top; the camera end as the physics casts would leave it: 0.18 m
            // above the collision ground, the hero walking along x
            Func<float, float, float> col = (x, z) => 0.4f * (float)Math.Sin(x * 0.3f) + 0.2f * (float)Math.Cos(z * 0.5f);
            Func<float, float, float> relief = (x, z) => 0.6f + 0.6f * (float)Math.Sin(x * 0.9f + z * 0.7f);
            var g = Field(col, relief);
            var s = new ReliefCameraRules.State();
            for (int i = 0; i < 600; i++)
            {
                float hx = i * 0.05f;
                var from = new Vector3(hx, col(hx, 0) + relief(hx, 0) + 1.7f, 0);
                float cz = -3f;
                var at = new Vector3(hx, col(hx, cz) + 0.18f, cz);
                var end = ReliefCameraRules.Follow(g, from, at, ref s, Dt);
                Assert.That(end.Y, Is.GreaterThanOrEqualTo(Drawn(g, end) + ReliefCameraRules.Clearance * ReliefCameraRules.MinNormalY - 1e-3f),
                            $"frame {i} at x {hx:0.00}");
            }
        }
    }
}
