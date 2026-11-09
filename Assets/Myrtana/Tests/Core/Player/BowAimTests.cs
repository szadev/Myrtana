using System;
using System.Numerics;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-56 (owner shot: the bow's crosshair sat above the sabretooth). The aim path of a bow shot, engine-free: the look
    /// that puts the screen centre on the target (<see cref="ThirdPersonAim"/>, as G3PlayerCamera hangs), the screen-centre
    /// ray's hit (PlayerCombat.AimPoint), the shot from the bow towards it at the drawn velocity, under gravity
    /// (Projectile). An arrow loosed with the crosshair on a foe's body centre must hit the body.
    /// </summary>
    public class BowAimTests
    {
        const float PivotHeight = 1.7f, Shoulder = 0.35f, MinPitch = -60f, MaxPitch = 70f;
        static readonly float Boom = CameraModes.Ranged.BestRange;
        static float Lift => CameraModes.Ranged.TargetOffsetY(Boom);

        /// <summary>A sabretooth-sized body: a sphere this big round its centre (0.6 m above its feet) is hit.</summary>
        const float BodyRadius = 0.4f, BodyCentreHeight = 0.6f;

        static Vector3 TargetFeet(float distance, float bearingDeg, float slopeDeg)
        {
            float b = bearingDeg * (float)Math.PI / 180f;
            float drop = distance * (float)Math.Tan(slopeDeg * Math.PI / 180.0);
            return new Vector3(distance * (float)Math.Sin(b), -drop, distance * (float)Math.Cos(b));
        }

        static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 centre, float radius, out Vector3 hit)
        {
            var oc = origin - centre;
            float b = Vector3.Dot(oc, dir), c = oc.LengthSquared() - radius * radius, disc = b * b - c;
            hit = default;
            if (disc < 0f) return false;
            float t = -b - (float)Math.Sqrt(disc);
            if (t < 0f) return false;
            hit = origin + dir * t;
            return true;
        }

        static float SegmentDistance(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            float t = ab.LengthSquared() > 0f ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            return Vector3.Distance(a + ab * t, p);
        }

        /// <summary>Flies the arrow (Projectile.Update: v += g·dt, swept per step); true when it passes through the body.</summary>
        static bool Flies(Vector3 from, Vector3 velocity, Vector3 centre, float radius, out float closest)
        {
            const float dt = 1f / 60f;
            var p = from;
            closest = float.MaxValue;
            for (int i = 0; i < 600; i++)
            {
                velocity += new Vector3(0, -RangedRules.Gravity, 0) * dt;
                var next = p + velocity * dt;
                closest = Math.Min(closest, SegmentDistance(p, next, centre));
                if (closest <= radius) return true;
                if (next.Y < centre.Y - 30f) return false;
                p = next;
            }
            return false;
        }

        /// <summary>PlayerCombat.ShotOrigin: the bow hand (left of the chest), 0.3 m ahead of the hero, who faces the look yaw.</summary>
        static Vector3 Origin(Vector3 feet, float yaw) =>
            feet + Vector3.UnitY * 1.45f - ThirdPersonAim.Right(yaw) * 0.15f + ThirdPersonAim.Forward(yaw, 0f) * 0.3f;

        /// <summary>
        /// Where the crosshair goes: the body centre, or with <paramref name="holdOver"/> a point far along the launch line
        /// that carries the arrow to it under gravity (the dev runner's ScenarioRunner.HoldOver; a G3 player aims higher at
        /// long range the same way).
        /// </summary>
        static Vector3 AimTarget(Vector3 origin, Vector3 centre, float speed, bool holdOver)
        {
            if (!holdOver) return centre;
            var d = centre - origin;
            var flat = new Vector3(d.X, 0, d.Z);
            float h = flat.Length();
            Assert.That(RangedRules.LaunchElevation(h, d.Y, speed, out float e), Is.True, "in reach");
            return origin + (flat / h * (float)Math.Cos(e) + Vector3.UnitY * (float)Math.Sin(e)) * 200f;
        }

        /// <summary>
        /// The whole shot: crosshair on the aim target (<see cref="ThirdPersonAim.Solve"/>), the screen-centre pick
        /// (PlayerCombat.AimPoint: the body, else the backdrop 300 m out), the arrow from the bow at the pick point at the
        /// drawn velocity, under gravity. True when it passes through the body.
        /// </summary>
        static bool Shoot(float distance, float bearing, float slope, float drawSeconds, bool holdOver, out float closest)
        {
            var feet = Vector3.Zero;
            var pivot = feet + Vector3.UnitY * PivotHeight;
            var centre = TargetFeet(distance, bearing, slope) + Vector3.UnitY * BodyCentreHeight;
            float speed = RangedRules.ShotVelocity(true, RangedRules.HitMultiplier(WeaponClass.Bow, drawSeconds));

            float yaw = 0f, pitch = 0f;
            var aim = centre;
            for (int i = 0; i < 3; i++) // the look turns the hero, which moves the bow: settle like the runner's per-frame aim
            {
                ThirdPersonAim.Solve(pivot, centre, Shoulder, Boom, Lift, MinPitch, MaxPitch, out yaw, out _);
                aim = AimTarget(Origin(feet, yaw), centre, speed, holdOver);
                Assert.That(ThirdPersonAim.Solve(pivot, aim, Shoulder, Boom, Lift, MinPitch, MaxPitch, out yaw, out pitch), Is.True);
            }
            ThirdPersonAim.Pose(pivot, yaw, pitch, Shoulder, Boom, Lift, out var cam, out var fwd);
            Assert.That(Vector3.Dot(Vector3.Normalize(aim - cam), fwd), Is.GreaterThan(0.99999f), "the screen centre is on the aim target");

            var pick = RaySphere(cam, fwd, centre, BodyRadius, out var onBody) ? onBody : cam + fwd * 300f;
            var origin = Origin(feet, yaw);
            var velocity = Vector3.Normalize(pick - origin) * speed;
            return Flies(origin, velocity, centre, BodyRadius, out closest);
        }

        static void Hits(float distance, float bearing, float slope, float drawSeconds, bool holdOver)
        {
            Assert.That(Shoot(distance, bearing, slope, drawSeconds, holdOver, out float closest), Is.True,
                        $"{distance} m, slope {slope}°, bearing {bearing}°, draw {drawSeconds} s, hold-over {holdOver}: " +
                        $"the arrow passes {closest:0.00} m from the body centre");
        }

        /// <summary>
        /// G3 speed (full draw 55.6 m/s) and drop: at 10 m the arrow falls ~0.16 m, inside the body — the crosshair on its
        /// centre hits. At 20 m (~0.6 m) and 40 m (~2.5 m) a G3 player holds the crosshair above the foe by the drop; so do
        /// these shots (hold-over), as the dev runner does.
        /// </summary>
        [TestCase(10f, 0f, false), TestCase(20f, 0f, true), TestCase(40f, 0f, true)]
        [TestCase(10f, 20f, false), TestCase(20f, 20f, true), TestCase(40f, 20f, true)]
        public void FullDraw_CrosshairOnTheBody_OrHeldOverItFarOut_Hits(float distance, float downhillDeg, bool holdOver)
        {
            foreach (float bearing in new[] { 0f, 35f, -50f })
                Hits(distance, bearing, downhillDeg, RangedRules.BowFullDrawSeconds, holdOver);
        }

        [TestCase(10f, 0f), TestCase(20f, 0f), TestCase(40f, 0f), TestCase(10f, 20f), TestCase(40f, 20f)]
        public void WeakDraw_HeldOver_StillHits(float distance, float downhillDeg)
        {
            Hits(distance, 25f, downhillDeg, 1.0f, holdOver: true); // 0.61 · 55.6 = 34 m/s
        }

        [Test]
        public void UphillTargetIsReachedToo()
        {
            Hits(20f, 10f, -15f, RangedRules.BowFullDrawSeconds, holdOver: true);
        }

        [Test]
        public void CrosshairOnTheBodyAt40m_FallsShort_TheG3Drop()
        {
            Assert.That(Shoot(40f, 0f, 0f, RangedRules.BowFullDrawSeconds, false, out float closest), Is.False);
            Assert.That(closest, Is.GreaterThan(1f), "~2.5 m of drop over 40 m at 55.6 m/s");
        }

        /// <summary>
        /// The old runner aim: the look pointed straight from the camera at the target. The camera looks at a point
        /// TargetOffsetY above the hero, so the screen centre ran ~10° high: metres above a foe 20 m away (the owner's shot).
        /// </summary>
        [Test]
        public void LookingStraightAtTheTarget_PutsTheCrosshairAboveIt()
        {
            var pivot = Vector3.UnitY * PivotHeight;
            var centre = TargetFeet(20f, 0f, 10f) + Vector3.UnitY * BodyCentreHeight;
            ThirdPersonAim.Pose(pivot, 0f, 0f, Shoulder, Boom, Lift, out var cam0, out _);
            float yaw = 0f, pitch = 0f;
            for (int i = 0; i < 5; i++) // the old AimAt, frame after frame
            {
                var d = centre - cam0;
                yaw = (float)(Math.Atan2(d.X, d.Z) * 180 / Math.PI);
                pitch = (float)(-Math.Atan2(d.Y, Math.Sqrt(d.X * d.X + d.Z * d.Z)) * 180 / Math.PI);
                ThirdPersonAim.Pose(pivot, yaw, pitch, Shoulder, Boom, Lift, out cam0, out _);
            }
            ThirdPersonAim.Pose(pivot, yaw, pitch, Shoulder, Boom, Lift, out var cam, out var fwd);
            var along = cam + fwd * Vector3.Distance(cam, centre);
            Assert.That(along.Y - centre.Y, Is.GreaterThan(2f), "the crosshair sits metres above the foe");
            Assert.That(RaySphere(cam, fwd, centre, BodyRadius, out _), Is.False);
        }

        [Test]
        public void PoseMatchesTheBoom_LiftLooksUp_ShoulderToTheRight()
        {
            var pivot = new Vector3(5, 10, -3);
            ThirdPersonAim.Pose(pivot, 90f, 0f, Shoulder, Boom, 0f, out var cam, out var fwd);
            Assert.That(Vector3.Distance(cam, pivot + new Vector3(-Boom, 0, -Shoulder)), Is.LessThan(1e-4f), "yaw 90 looks +x, right is −z");
            Assert.That(Vector3.Distance(fwd, Vector3.UnitX), Is.LessThan(1e-4f));
            ThirdPersonAim.Pose(pivot, 0f, 0f, Shoulder, Boom, Lift, out _, out var lifted);
            Assert.That(Math.Asin(lifted.Y) * 180 / Math.PI, Is.EqualTo(Math.Atan2(Lift, Boom) * 180 / Math.PI).Within(1e-3));
        }

        [Test]
        public void TargetInsideTheBoomIsRefused()
        {
            Assert.That(ThirdPersonAim.Solve(Vector3.UnitY, new Vector3(0.1f, 1, 0.1f), Shoulder, Boom, Lift, MinPitch, MaxPitch, out _, out _),
                        Is.False);
        }
    }
}
