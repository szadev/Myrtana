using System;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-76: the third-person camera's stability rules (<see cref="CameraStability"/>) and its jitter metrics.</summary>
    public class CameraStabilityTests
    {
        const float Dt = 1f / 60f;

        /// <summary>The run's head: 168 cm mean, ±4.7 cm at 2.5 Hz (Hero_Stand_*_Move_Run_N_Fwd, 0.8 s, two steps).</summary>
        static float RunHead(float t) => CameraPivotRules.HeadOffset(1.68f + 0.047f * (float)Math.Sin(2 * Math.PI * 2.5 * t));

        [Test]
        public void HeadFilter_KeepsTheRunBobOut_ButFollowsAStanceChange()
        {
            var f = new CameraStability.HeadFilter();
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 600; i++)
            {
                float v = CameraStability.FollowHead(ref f, RunHead(i * Dt), false, Dt);
                if (i > 240) { min = Math.Min(min, v); max = Math.Max(max, v); }
            }
            Assert.That(max - min, Is.LessThan(0.012f), "9.4 cm of head bob reach the camera as about 1 cm at most");
            float mean = CameraPivotRules.HeadOffset(1.68f);
            Assert.That((max + min) / 2, Is.EqualTo(mean).Within(0.02f), "the run's mean head height is followed");

            // a stance change: the unarmed stand (head 177.9) to the 1H stand (168.5)
            var g = new CameraStability.HeadFilter();
            CameraStability.FollowHead(ref g, CameraPivotRules.HeadOffset(1.779f), false, Dt);
            float target = CameraPivotRules.HeadOffset(1.685f), value = 0, prevStep = 0, maxStepChange = 0;
            float last = g.Value;
            for (int i = 0; i < 120; i++)
            {
                value = CameraStability.FollowHead(ref g, target, false, Dt);
                float step = value - last;
                if (i > 0) maxStepChange = Math.Max(maxStepChange, Math.Abs(step - prevStep));
                prevStep = step;
                last = value;
            }
            Assert.That(value, Is.EqualTo(target).Within(CameraStability.HeadDeadband + 0.002f), "settled within 2 s");
            Assert.That(maxStepChange, Is.LessThan(0.0015f), "no velocity step: the spring starts and ends softly");
        }

        [Test]
        public void HeadFilter_Busy_DampsASwingDipMoreThanSteady()
        {
            // an attack: the head dips 15 cm for 0.4 s
            float Dip(float t) => t > 0.5f && t < 0.9f ? -0.15f : 0f;
            float Run(bool busy)
            {
                var f = new CameraStability.HeadFilter();
                float low = 0;
                for (int i = 0; i < 120; i++) low = Math.Min(low, CameraStability.FollowHead(ref f, Dip(i * Dt), busy && i * Dt > 0.5f && i * Dt < 0.9f, Dt));
                return -low;
            }
            Assert.That(Run(true), Is.LessThan(Run(false)));
            Assert.That(Run(true), Is.LessThan(0.04f), "a 15 cm swing dip moves the busy camera by less than 4 cm");
        }

        [Test]
        public void FollowRoot_EasesAStepUp_FollowsSlopesAndJumpsWithoutLag()
        {
            // a 60 cm step-up in one frame while walking 4 m/s on flat ground
            var f = new CameraStability.RootFilter();
            CameraStability.FollowRoot(ref f, 0f, 4f, 0f, Dt);
            float y = 0, maxStep = 0, feet = 0.6f;
            for (int i = 0; i < 60; i++)
            {
                float next = CameraStability.FollowRoot(ref f, feet, 4f, 0f, Dt);
                maxStep = Math.Max(maxStep, next - y);
                y = next;
            }
            Assert.That(y, Is.EqualTo(0.6f).Within(0.005f));
            Assert.That(maxStep, Is.LessThan(0.6f * 0.2f), "spread over several frames, not one jump");

            // a 30° slope at 4 m/s: followed exactly (no lag that would lower the hero in the picture uphill)
            var g = new CameraStability.RootFilter();
            float h = 0;
            for (int i = 0; i < 120; i++) h = CameraStability.FollowRoot(ref g, i * Dt * 4f * 0.577f, 4f, 0f, Dt);
            Assert.That(h, Is.EqualTo(119 * Dt * 4f * 0.577f).Within(1e-4f));

            // a jump (intended vertical speed): no lag either
            var j = new CameraStability.RootFilter();
            float vy = 6.6f, z = 0, jy = 0;
            for (int i = 0; i < 30; i++) { z += vy * Dt; jy = CameraStability.FollowRoot(ref j, z, 4f, vy, Dt); vy -= 20f * Dt; }
            Assert.That(jy, Is.EqualTo(z).Within(1e-4f));

            // stairs: 20 cm every 0.1 s — the pivot rises smoothly and never trails by more than RootMaxLag
            var st = new CameraStability.RootFilter();
            float stairFeet = 0, last = 0, maxLag = 0;
            for (int i = 0; i < 120; i++)
            {
                if (i % 6 == 0) stairFeet += 0.2f;
                float p = CameraStability.FollowRoot(ref st, stairFeet, 2f, 0f, Dt);
                maxLag = Math.Max(maxLag, stairFeet - p);
                Assert.That(p, Is.GreaterThanOrEqualTo(last - 1e-4f), "never goes down on stairs up");
                last = p;
            }
            Assert.That(maxLag, Is.LessThanOrEqualTo(CameraStability.RootMaxLag));
            Assert.That(CameraStability.FollowRoot(ref st, float.NaN, 0f, 0f, Dt), Is.NaN.Or.Not.NaN);
        }

        [Test]
        public void FollowFree_PullsInAtOnce_HoldsThenEasesOut_ZoomPassesStraight()
        {
            var s = new CameraStability.FreeLimit();
            Assert.That(CameraStability.FollowFree(ref s, 3f, false, Dt), Is.EqualTo(3f));
            Assert.That(CameraStability.FollowFree(ref s, 1.2f, true, Dt), Is.EqualTo(1.2f), "an obstacle pulls in at once");
            // the obstacle is gone after 3 frames (a pole while running): the limit holds, then eases out
            float v = 0;
            for (int i = 0; i < 6; i++) v = CameraStability.FollowFree(ref s, 3f, false, Dt);
            Assert.That(v, Is.EqualTo(1.2f), "held right after the obstacle");
            float t = 0;
            while (v < 2.99f && t < 3f) { v = CameraStability.FollowFree(ref s, 3f, false, Dt); t += Dt; }
            Assert.That(t, Is.GreaterThan(0.3f), "eases out, does not pop back");
            Assert.That(t, Is.LessThan(2.5f));
            // a pole that blinks in and out every other frame keeps the boom still instead of pumping it
            var p = new CameraStability.FreeLimit();
            CameraStability.FollowFree(ref p, 3f, false, Dt);
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 30; i++)
            {
                float x = i % 2 == 0 ? CameraStability.FollowFree(ref p, 1.5f, true, Dt) : CameraStability.FollowFree(ref p, 3f, false, Dt);
                if (i > 0) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
            }
            Assert.That(hi - lo, Is.LessThan(0.01f));
            // without an obstacle a zoom-out is not delayed (the G3 distance smoothing does the easing)
            var z = new CameraStability.FreeLimit();
            CameraStability.FollowFree(ref z, 3f, false, Dt);
            Assert.That(CameraStability.FollowFree(ref z, 5f, false, Dt), Is.EqualTo(5f));
        }

        [Test]
        public void SoftObstacles_BlockOnlyNearTheCamera()
        {
            Assert.That(CameraStability.Blocks(1.0f, false, 0.7f, 0.6f, soft: true, length: 3f), Is.False, "an NPC crossing mid-boom");
            Assert.That(CameraStability.Blocks(2.6f, false, 0.7f, 0.6f, soft: true, length: 3f), Is.True, "an NPC at the camera");
            Assert.That(CameraStability.Blocks(1.0f, false, 2f, 0.6f, soft: false, length: 3f), Is.True, "a wall");
            Assert.That(CameraStability.Blocks(1.0f, false, 0.3f, 0.6f, soft: false, length: 3f), Is.False, "a thin pole");
            Assert.That(CameraStability.Blocks(0f, false, 2f, 0.6f, soft: false, length: 3f), Is.False, "started inside (G3 S_FilterShape)");
            Assert.That(CameraStability.Blocks(1f, true, 2f, 0.6f, soft: false, length: 3f), Is.False, "own body");
        }

        [Test]
        public void MovingLatch_StandsOnlyAfterAPause()
        {
            float still = 0;
            Assert.That(CameraStability.MovingLatch(true, ref still, Dt), Is.True);
            Assert.That(CameraStability.MovingLatch(false, ref still, Dt), Is.True, "a one-frame stop keeps MinRangeOnMove");
            for (int i = 0; i < 30; i++) CameraStability.MovingLatch(false, ref still, Dt);
            Assert.That(CameraStability.MovingLatch(false, ref still, Dt), Is.False, "0.5 s standing");
        }

        [Test]
        public void Transitions_CutFarTeleports_BlendSmoothly()
        {
            Assert.That(CameraStability.Cuts(0.8f), Is.False, "an object use's spot blends");
            Assert.That(CameraStability.Cuts(50f), Is.True, "a teleport cuts (G3 EntityMovedBigDistance)");
            Assert.That(CameraStability.BlendWeight(0f, 0.4f), Is.EqualTo(0f));
            Assert.That(CameraStability.BlendWeight(0.2f, 0.4f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(CameraStability.BlendWeight(0.4f, 0.4f), Is.EqualTo(1f));
            Assert.That(CameraStability.BlendWeight(0.01f, 0.4f), Is.LessThan(0.01f), "smoothstep: starts without a jump");
        }

        [Test]
        public void LocomotionSetSwitch_KeepsThePhase()
        {
            Assert.That(MotionBlendRules.StartPhase(true, true, true, 0.37f), Is.EqualTo(0.37f).Within(1e-6f), "1H run after unarmed run");
            Assert.That(MotionBlendRules.StartPhase(true, true, true, 1.25f), Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(MotionBlendRules.StartPhase(false, true, true, 0.37f), Is.EqualTo(0f), "after the idle or a one-shot: from the start");
            Assert.That(MotionBlendRules.StartPhase(true, true, false, 0.37f), Is.EqualTo(0f), "into a one-shot");
        }

        // ------------------------------------------------------------------------------------------- jitter metrics

        static CameraJitterStats Path(Func<int, (float x, float y, float z, float wy, float yaw)> at, int frames, Func<int, float> dt = null)
        {
            var s = new CameraJitterStats();
            for (int i = 0; i < frames; i++)
            {
                var p = at(i);
                s.Add(dt?.Invoke(i) ?? Dt, p.x, p.y, p.z, p.wy, p.yaw, 10f);
            }
            return s;
        }

        [Test]
        public void JitterStats_SmoothPath_IsQuiet_EvenWithUnevenFrames()
        {
            // the camera eases out 2 m in 1 s with frame times alternating 8 / 25 ms
            float t = 0;
            var times = new float[200];
            for (int i = 0; i < times.Length; i++) { times[i] = t; t += i % 2 == 0 ? 0.008f : 0.025f; }
            var s = Path(i => (0, 1.5f, -1 - 2 * (float)Math.Sin(Math.Min(1, times[i]) * Math.PI / 2), 1.5f, 0), times.Length,
                         i => i % 2 == 0 ? 0.025f : 0.008f);
            Assert.That(s.Metric("jump"), Is.LessThan(0.01f));
            Assert.That(s.Metric("rotjump"), Is.LessThan(1e-4f));
        }

        [Test]
        public void JitterStats_FindAPop_AStep_AndACutIsNotCounted()
        {
            var pop = Path(i => (0, 1.5f, i == 50 ? -2.0f : -3.0f, 1.5f, 0), 100); // one frame pulled in by a pole
            Assert.That(pop.Metric("jump"), Is.GreaterThan(0.9f));
            Assert.That(pop.Metric("step"), Is.GreaterThan(0.9f));
            var step = Path(i => (0, 1.5f, -3f, i < 50 ? 1.5f : 2.0f, 0), 100); // a step-up taken in one frame
            Assert.That(step.Metric("yjerk"), Is.GreaterThan(0.45f));
            var cut = new CameraJitterStats();
            for (int i = 0; i < 100; i++) cut.Add(Dt, 0, 1.5f, -3f, i < 50 ? 1.5f : 80f, 0, 10f, cut: i == 50);
            Assert.That(cut.Metric("yjerk"), Is.LessThan(1e-4f), "a teleport cut is no jitter");
            Assert.That(float.IsNaN(cut.Metric("nonsense")), Is.True);
        }

        [Test]
        public void JitterStats_PelvisDip_FindsACycleRestartedOutOfPhase()
        {
            float Pelvis(float phase) => 1.0f + 0.04f * (float)Math.Cos(4 * Math.PI * phase); // two bobs per cycle
            CameraJitterStats Run(bool restart)
            {
                var s = new CameraJitterStats();
                float phase = 0;
                for (int i = 0; i < 300; i++)
                {
                    phase = (phase + Dt / 0.8f) % 1f;
                    float pelvis = Pelvis(phase);
                    if (restart && i >= 150 && i < 162)
                    {
                        // the old set out of phase, the new one from 0, cross-faded over 0.2 s
                        float w = (i - 150) / 12f;
                        float newPhase = (i - 150) * Dt / 0.8f;
                        pelvis = Pelvis(phase) * (1 - w) + Pelvis(newPhase + 0.25f) * w;
                        s.Add(Dt, 0, 1.5f, -3, 1.5f, 0, 10, pelvis: pelvis - 0.02f * (float)Math.Sin(Math.PI * w), phase: newPhase);
                        continue;
                    }
                    s.Add(Dt, 0, 1.5f, -3, 1.5f, 0, 10, pelvis: pelvis, phase: phase);
                }
                return s;
            }
            Assert.That(Run(false).Metric("pelvisdip"), Is.LessThan(0.01f), "the binning error of a steady run");
            Assert.That(Run(true).Metric("pelvisdip"), Is.GreaterThan(0.03f));
            Assert.That(Run(true).Metric("phasejump"), Is.GreaterThan(0.1f));
            Assert.That(Run(false).Metric("phasejump"), Is.LessThan(0.01f));
        }

        [Test]
        public void JitterStats_Drift_MeasuresAnUnwantedCameraMove()
        {
            var still = Path(i => (0.35f, 1.5f, -3f, 1.5f, 0f), 60);
            Assert.That(still.Metric("drift"), Is.LessThan(1e-4f));
            Assert.That(still.Metric("yawdrift"), Is.LessThan(1e-4f));
            var snap = Path(i => (0.35f, 1.5f, i < 30 ? -3f : -1f, 1.5f, i < 30 ? 0f : 40f), 60); // a talk cut to a dialog shot
            Assert.That(snap.Metric("drift"), Is.GreaterThan(1.9f));
            Assert.That(snap.Metric("yawdrift"), Is.GreaterThan(39f));
        }

        // ------------------------------------------------------------------- fix-94: camera pressed against a slope

        [Test]
        public void HeroAlpha_G3HalfCosine_From1mTo20cm()
        {
            Assert.That(CameraStability.HeroAlpha(1.5f), Is.EqualTo(1f));
            Assert.That(CameraStability.HeroAlpha(1.0f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(CameraStability.HeroAlpha(0.6f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(CameraStability.HeroAlpha(0.2f), Is.EqualTo(0f));
            Assert.That(CameraStability.HeroAlpha(0.05f), Is.EqualTo(0f));
        }

        [Test]
        public void HidesHero_WholeHeroAroundHalfAlpha_WithHysteresis()
        {
            Assert.That(CameraStability.HidesHero(0.7f, false), Is.False, "G3 alpha 0.69: the hero is drawn whole, head included");
            Assert.That(CameraStability.HidesHero(0.57f, false), Is.False, "alpha 0.43: not yet");
            Assert.That(CameraStability.HidesHero(0.45f, false), Is.True, "alpha 0.22: G3 has nearly faded him out");
            Assert.That(CameraStability.HidesHero(0.55f, true), Is.True, "stays hidden until alpha 0.6");
            Assert.That(CameraStability.HidesHero(0.7f, true), Is.False);
        }

        [Test]
        public void WallPush_KeepsG3s17cmOffTheSurface()
        {
            Assert.That(CameraStability.WallPush(0f), Is.EqualTo(0.175f).Within(1e-6f));
            Assert.That(CameraStability.WallPush(0.1f), Is.EqualTo(0.075f).Within(1e-6f));
            Assert.That(CameraStability.WallPush(0.3f), Is.EqualTo(0f));
            Assert.That(CameraStability.RayHitDistance(0.5f, 0.1f, -0.5f), Is.EqualTo(0.7f).Within(1e-6f));
            Assert.That(CameraStability.RayHitDistance(0.5f, 0.1f, 0f), Is.EqualTo(1.0f).Within(1e-6f), "grazing: capped at 5 radii");
        }

        /// <summary>
        /// The owner's case (fix-90 item D): the hero aims with his back to a steep slope, the ranged camera hangs 30 deg above
        /// him (BestElev) and its boom runs into the slope. Plane 60 deg steep, 0.35 m behind the pivot; boom up 30 deg (2D:
        /// x back, y up). The old 0.2 m sphere stopped 0.2 m short of the face, minus 0.05 m: 0.25 m boom, the head culled.
        /// Now: thin sweep -> the ray's hit, 17.5 cm out along the normal (G3 MaintainWallDistance): 0.63 m, hero drawn.
        /// </summary>
        [Test]
        public void SlopeBehindTheHero_CameraRidesOffTheSurface_HeroStaysDrawn()
        {
            double a = 60 * Math.PI / 180, e = 30 * Math.PI / 180;
            double nx = -Math.Sin(a), ny = Math.Cos(a);           // slope normal, toward the hero
            double dx = Math.Cos(e), dy = Math.Sin(e);            // boom direction
            double f0 = 0.35;                                     // pivot's distance to the slope plane
            double cos = dx * nx + dy * ny;                       // < 0: toward the surface
            float Sweep(double r) => (float)((f0 - r) / -cos);    // where a sphere of radius r touches the plane

            float old = Sweep(0.2) - 0.05f;
            Assert.That(old, Is.LessThan(0.6f), "before: a 0.25 m boom, the old 0.6 m first-person switch hid the head");

            float free = CameraStability.RayHitDistance(Sweep(0.1), 0.1f, (float)cos);
            Assert.That(free, Is.EqualTo((float)(f0 / -cos)).Within(1e-4f), "the ray's own hit on the slope");
            double px = dx * free, py = dy * free;
            float push = CameraStability.WallPush((float)(f0 + (px * nx + py * ny)));
            Assert.That(push, Is.EqualTo(CameraStability.WallDistance).Within(1e-4f));
            px += nx * push; py += ny * push;
            float dist = (float)Math.Sqrt(px * px + py * py);
            Assert.That(dist, Is.GreaterThan(0.6f), "G3: hit point + 17.5 cm along the normal");
            Assert.That(CameraStability.HidesHero(dist, false), Is.False, "the hero is drawn whole, not headless");
        }
    }
}
