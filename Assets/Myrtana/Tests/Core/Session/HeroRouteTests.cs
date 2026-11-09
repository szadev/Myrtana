using System.Collections.Generic;
using Myrtana.Session.Dev;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-34: the scenario hero's G3 route progress and anti-loop rules (<see cref="HeroRoute"/>).</summary>
    public class HeroRouteTests
    {
        static HeroRoutePoint P(float x, float z) => new HeroRoutePoint(x, 0, z);

        // a straight route along +x: points every 10 m from 0 to 50
        static List<HeroRoutePoint> Line() => new List<HeroRoutePoint> { P(0, 0), P(10, 0), P(20, 0), P(30, 0), P(40, 0), P(50, 0) };

        [Test]
        public void SetRoute_StartsAfterTheNearestSegment()
        {
            var r = new HeroRoute();
            r.SetRoute(Line(), P(23, 1));
            Assert.That(r.Next, Is.EqualTo(3));
            Assert.That(r.WayLeft(P(23, 0)), Is.EqualTo(27f).Within(0.01f));
            Assert.That(r.Length, Is.EqualTo(50f).Within(0.01f));
        }

        [Test]
        public void Leg_MovesOnWhenReachedOrPassed()
        {
            var r = new HeroRoute();
            r.SetRoute(Line(), P(0, 0));
            Assert.That(r.Next, Is.EqualTo(1));
            Assert.That(r.Leg(P(5, 0)).X, Is.EqualTo(10f));
            Assert.That(r.Leg(P(8.5f, 0)).X, Is.EqualTo(20f)); // within reach
            Assert.That(r.Leg(P(22, 0.5f)).X, Is.EqualTo(30f)); // passed point 2
            Assert.That(r.Leg(P(50, 0)).X, Is.EqualTo(50f));   // the goal stays the leg
        }

        [Test]
        public void StuckAt_TeleportsForwardBeyondTheObstacle_NeverBack()
        {
            var r = new HeroRoute();
            r.SetRoute(Line(), P(0, 0));
            r.Leg(P(9, 0)); // on to point 2
            // stuck at a rock 2 m before point 2: point 2 is too close, the target is point 3
            Assert.That(r.StuckAt(P(18, 0), out int k), Is.True);
            Assert.That(k, Is.EqualTo(3));
            Assert.That(r.Next, Is.EqualTo(3));
            Assert.That(r.IsBlocked(P(18, 0)), Is.True);
        }

        [Test]
        public void Replan_SkipsPointsAndLegsThroughABlockedSpot()
        {
            var r = new HeroRoute();
            r.SetRoute(Line(), P(0, 0));
            r.StuckAt(P(19, 0), out _); // the rock at point 2
            // a new plan from 5 m back: point 1 is free, so it is the leg
            r.SetRoute(Line(), P(5, 0));
            Assert.That(r.Next, Is.EqualTo(1));
            // past point 1: point 2 is at the rock, the leg goes on to point 3 beyond it
            r.SetRoute(Line(), P(10.5f, 0));
            Assert.That(r.Next, Is.EqualTo(3));
        }

        [Test]
        public void StuckAt_WithoutAPointAhead_IsFalse()
        {
            var r = new HeroRoute();
            r.SetRoute(new List<HeroRoutePoint> { P(0, 0), P(10, 0) }, P(0, 0));
            Assert.That(r.StuckAt(P(8, 0), out int k), Is.False);
            Assert.That(k, Is.EqualTo(-1));
            var none = new HeroRoute();
            Assert.That(none.StuckAt(P(1, 1), out _), Is.False);
            Assert.That(none.Blocked.Count, Is.EqualTo(1));
        }

        [Test]
        public void ThreeTeleportsWithinSixtySeconds_Loop()
        {
            var r = new HeroRoute();
            Assert.That(r.RecordTeleport(0), Is.False);
            Assert.That(r.RecordTeleport(30), Is.False);
            Assert.That(r.RecordTeleport(59), Is.True);

            var spread = new HeroRoute();
            Assert.That(spread.RecordTeleport(0), Is.False);
            Assert.That(spread.RecordTeleport(40), Is.False);
            Assert.That(spread.RecordTeleport(80), Is.False); // the first is older than 60 s
            Assert.That(spread.RecordTeleport(95), Is.True);
        }

        [Test]
        public void MoveGoal_FollowsTheGuide()
        {
            var r = new HeroRoute();
            r.SetRoute(new List<HeroRoutePoint> { P(0, 0), P(10, 0), P(20, 0) }, P(0, 0));
            r.MoveGoal(P(25, 3));
            Assert.That(r.Points[2].X, Is.EqualTo(25f));
            Assert.That(r.Leg(P(19, 0)).Z, Is.EqualTo(3f));
        }
    
        [Test]
        public void TeleportsThatBringTheHeroOn_AreNoLoop()
        {
            // fix-46 r28 hammerclan_04: three stuck teleports up a slope, each further on, failed as "the hero loops".
            var on = new HeroRoute();
            Assert.That(on.RecordTeleport(0, 60f), Is.False);
            Assert.That(on.RecordTeleport(20, 52f), Is.False);
            Assert.That(on.RecordTeleport(40, 44f), Is.False, "16 m nearer the target since the first: progress");
            Assert.That(on.RecordTeleport(50, 42f), Is.False);
            Assert.That(on.RecordTeleport(55, 41f), Is.True, "three within 60 s without 5 m progress");

            // r28 fireclan_01: teleports #2/#3 moved the hero 0.1 m next to a guide stuck off the NavMesh
            var stuck = new HeroRoute();
            Assert.That(stuck.RecordTeleport(0, 26.9f), Is.False);
            Assert.That(stuck.RecordTeleport(21, 26.9f), Is.False);
            Assert.That(stuck.RecordTeleport(42, 26.8f), Is.True);
        }
    }
}
