using System;
using System.Numerics;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-253 (owner: "often after the bow is drawn the aim kicks upward" — a small mouse movement up while drawing or
    /// holding leaps the crosshair up by far more). The third-person view looked at the lift point (TargetOffsetY) from the
    /// collided boom end: a look up drops the end to the ground, the drawn relief lifts it (the view stops following the
    /// mouse) and then pulls it in (the view leaps up). With a ranged weapon drawn the view now follows the look only
    /// (<see cref="ThirdPersonAim.SteadyForward"/>).
    /// </summary>
    public class SteadyAimTests
    {
        const float Dt = 1f / 60f, PivotHeight = 1.7f, Step = 0.1f;
        static readonly CameraModeParams Mode = CameraModes.Ranged;

        /// <summary>Collision ground at 0 with a 0.9 m terrace from 1.5 m behind the hero (a ledge, a rock, a terrain lip).</summary>
        static float Collision(float z) => z < -1.5f ? 0.9f : 0f;

        /// <summary>The terrace, with the relief drawn 0.5 m above the collision behind the hero (z &lt; -0.5).</summary>
        static readonly ReliefCameraRules.Ground TerraceBehind = (float x, float y, float z, out float c, out float v) =>
        {
            c = Collision(z);
            v = c + (z < -0.5f ? 0.5f : 0f);
            return true;
        };

        /// <summary>One look-up sweep (Step deg per frame): the old look-at view's and the steady view's elevations.</summary>
        static void Sweep(out float[] look, out float[] oldView, out float[] steadyView)
        {
            const int n = 400; // 0 … 40 deg up
            look = new float[n];
            oldView = new float[n];
            steadyView = new float[n];
            var shoulder = new Vector3(0f, PivotHeight, 0f);
            var state = default(ReliefCameraRules.State);
            float d = Mode.BestRange;
            for (int i = 0; i < n; i++)
            {
                float up = i * Step, pitch = -up;
                var f = ThirdPersonAim.Forward(0f, pitch);
                // the physics cast along the boom against the collision ground (stops 0.1 m off it, at once)
                float free = d;
                for (float t = 0.05f; t <= d; t += 0.01f)
                {
                    var q = shoulder - f * t;
                    if (q.Y < Collision(q.Z) + 0.1f) { free = t - 0.01f; break; }
                }
                var at = shoulder - f * free;
                var end = ReliefCameraRules.Follow(TerraceBehind, shoulder, at, ref state, Dt);
                float cur = (end - shoulder).Length();
                float lift = cur > 0.6f ? Mode.TargetOffsetY(cur) : 0f;
                look[i] = up;
                oldView[i] = ThirdPersonAim.Elevation(ThirdPersonAim.LookAtForward(shoulder, end, lift, f));
                steadyView[i] = ThirdPersonAim.Elevation(ThirdPersonAim.SteadyForward(0f, pitch, d, Mode.TargetOffsetY(d)));
            }
        }

        static float MaxStep(float[] v)
        {
            float m = 0f;
            for (int i = 1; i < v.Length; i++) m = Math.Max(m, Math.Abs(v[i] - v[i - 1]));
            return m;
        }

        [Test]
        public void LookAtFromTheCollidedEnd_KicksUp_OnASmallLookUp()
        {
            Sweep(out _, out var oldView, out _);
            // the cause: once the boom end, lowered by the look up, catches the terrace, a 0.1 deg mouse step turns the
            // old view by degrees per frame (pull-in at once, the relief lift easing out under it)
            Assert.That(MaxStep(oldView), Is.GreaterThan(1.5f));
        }

        [Test]
        public void SteadyView_FollowsTheLookOneToOne_ThroughLiftAndPullIn()
        {
            Sweep(out var look, out _, out var steady);
            for (int i = 1; i < look.Length; i++)
            {
                float dv = steady[i] - steady[i - 1];
                Assert.That(dv, Is.InRange(Step * 0.8f, Step * 1.2f), $"step {i} at {look[i]:F1} deg up");
            }
        }

        [Test]
        public void SteadyView_EqualsLookAt_WithoutObstacles()
        {
            // no change of aim when the bow is drawn in the open (the steady weight eases in invisibly)
            foreach (float d in new[] { 2f, 3f, 6f, 15f })
                foreach (float pitch in new[] { -50f, -20f, 0f, 15f, 60f })
                {
                    float lift = Mode.TargetOffsetY(d);
                    ThirdPersonAim.Pose(new Vector3(0f, PivotHeight, 0f), 30f, pitch, 0f, d, lift, out _, out var a);
                    var b = ThirdPersonAim.SteadyForward(30f, pitch, d, lift);
                    Assert.That(Vector3.Distance(a, b), Is.LessThan(1e-4f), $"d {d} pitch {pitch}");
                }
        }

        [Test]
        public void SteadyView_PitchGainIsAboutOne_AtEveryZoom()
        {
            foreach (float d in new[] { 2f, 3f, 8f, 15f })
                for (float pitch = -45f; pitch <= 60f; pitch += 5f)
                {
                    float lift = Mode.TargetOffsetY(d);
                    float e0 = ThirdPersonAim.Elevation(ThirdPersonAim.SteadyForward(0f, pitch, d, lift));
                    float e1 = ThirdPersonAim.Elevation(ThirdPersonAim.SteadyForward(0f, pitch - 0.5f, d, lift));
                    Assert.That((e1 - e0) / 0.5f, Is.InRange(0.75f, 1.25f), $"d {d} pitch {pitch}");
                }
        }

        [Test]
        public void MouseLook_IsProportional_NoAcceleration()
        {
            var options = new ControlOptions();
            foreach (float dt in new[] { 1f / 30f, 1f / 60f, 1f / 144f })
            {
                float gain = float.NaN;
                foreach (float pixels in new[] { 1f, 3f, 10f, 100f })
                {
                    var filter = new MouseLookFilter();
                    float sum = 0f;
                    sum += filter.Apply(0f, pixels, dt, options).y;      // one frame of movement up …
                    for (int i = 0; i < 400; i++) sum += filter.Apply(0f, 0f, dt, options).y; // … then still
                    float g = sum / pixels;
                    if (float.IsNaN(gain)) gain = g;
                    Assert.That(g, Is.EqualTo(gain).Within(1e-3f), $"dt {dt} pixels {pixels}");
                    Assert.That(g, Is.EqualTo(ControlOptions.SensitivityFactor(options.SensitivityY)).Within(1e-3f));
                }
            }
        }
    }
}
