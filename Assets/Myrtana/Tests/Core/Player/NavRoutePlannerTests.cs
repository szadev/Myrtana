using System;
using System.Collections.Generic;
using System.Linq;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class NavRoutePlannerTests
    {
        static RoutePoint P(float x, float z, float y = 0) => new RoutePoint(x, y, z);

        /// <summary>Edges between nodes closer than the neighbour radius cost their flat distance, except the blocked pairs.</summary>
        static Func<int, int, float?> Open(List<RoutePoint> nodes, params (int, int)[] blocked) =>
            (a, b) => blocked.Contains((a, b)) || blocked.Contains((b, a)) ? (float?)null : nodes[a].FlatDistance(nodes[b]);

        [Test]
        public void CorridorRunsAlongTheWayWithSideLanesAndStopsAtTheGoal()
        {
            var c = NavRoutePlanner.Corridor(0, 0, 50, 0, spacing: 20, halfWidth: 40, maxAhead: 180);
            // rows at 20, 40 and the goal (50); 5 lanes each
            Assert.That(c.Count, Is.EqualTo(15));
            Assert.That(c[0].x, Is.EqualTo(20).Within(1e-3));
            Assert.That(c.Any(p => Math.Abs(p.x - 50) < 1e-3 && Math.Abs(p.z) < 1e-3), "the goal itself");
            Assert.That(c.Any(p => Math.Abs(p.z - 40) < 1e-3) && c.Any(p => Math.Abs(p.z + 40) < 1e-3), "side lanes");
        }

        [Test]
        public void CorridorStopsAtMaxAheadForFarGoals()
        {
            var c = NavRoutePlanner.Corridor(0, 0, 0, 500, spacing: 20, halfWidth: 0, maxAhead: 100);
            Assert.That(c.Count, Is.EqualTo(5));
            Assert.That(c.Max(p => p.z), Is.EqualTo(100).Within(1e-3));
        }

        [Test]
        public void StraightWayReachesTheGoal()
        {
            var nodes = new List<RoutePoint> { P(0, 0), P(20, 0), P(40, 0), P(60, 0) };
            var r = NavRoutePlanner.Plan(nodes, 0, P(60, 0), Open(nodes), neighbourRadius: 25);
            Assert.That(r.ReachesGoal);
            Assert.That(r.Nodes, Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(r.Cost, Is.EqualTo(60).Within(1e-3));
        }

        [Test]
        public void GoesAroundABlockedStretch()
        {
            // a wall between (20,0) and (40,0): the way goes over the side lane (30, 20)
            var nodes = new List<RoutePoint> { P(0, 0), P(20, 0), P(40, 0), P(60, 0), P(30, 20) };
            var r = NavRoutePlanner.Plan(nodes, 0, P(60, 0), Open(nodes, (1, 2), (0, 2)), neighbourRadius: 25);
            Assert.That(r.ReachesGoal);
            Assert.That(r.Nodes, Does.Contain(4));
            Assert.That(r.Nodes.Last(), Is.EqualTo(3));
        }

        [Test]
        public void OneWayDropLinkIsUsedDownButNotUp()
        {
            // plateau 0-1 (y 245), below 2-3 (y 180): only 1 -> 2 exists (a drop link), never 2 -> 1
            var nodes = new List<RoutePoint> { P(0, 0, 245), P(20, 0, 245), P(40, 0, 180), P(60, 0, 180) };
            float? Edge(int a, int b)
            {
                bool upper(int i) => i < 2;
                if (upper(a) == upper(b)) return nodes[a].FlatDistance(nodes[b]);
                return a == 1 && b == 2 ? 25f : (float?)null;
            }
            var down = NavRoutePlanner.Plan(nodes, 0, P(60, 0), Edge, neighbourRadius: 25);
            Assert.That(down.ReachesGoal);
            Assert.That(down.Nodes, Is.EqualTo(new[] { 0, 1, 2, 3 }));
            var up = NavRoutePlanner.Plan(nodes, 3, P(0, 0), Edge, neighbourRadius: 25);
            Assert.That(up.ReachesGoal, Is.False);
            Assert.That(up.Nodes.Last(), Is.EqualTo(2), "as far as the lower floor goes");
        }

        [Test]
        public void PartialRouteEndsAtTheReachedNodeClosestToTheGoal()
        {
            // the goal lies beyond the loaded NavMesh (no node there): walk to the frontier (node 3), not to the island node 4
            var nodes = new List<RoutePoint> { P(0, 0), P(20, 0), P(40, 0), P(60, 0), P(80, 0) };
            var r = NavRoutePlanner.Plan(nodes, 0, P(300, 0), Open(nodes, (3, 4), (2, 4)), neighbourRadius: 25);
            Assert.That(r.ReachesGoal, Is.False);
            Assert.That(r.Moves);
            Assert.That(r.Nodes.Last(), Is.EqualTo(3));
            Assert.That(r.Left, Is.EqualTo(240).Within(1e-3));
        }

        [Test]
        public void NoWayStaysAtTheStart()
        {
            var nodes = new List<RoutePoint> { P(0, 0), P(20, 0) };
            var r = NavRoutePlanner.Plan(nodes, 0, P(20, 0), (a, b) => null);
            Assert.That(r.Moves, Is.False);
            Assert.That(r.Nodes, Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void QueryBudgetIsKept()
        {
            var nodes = new List<RoutePoint>();
            for (int i = 0; i < 40; i++) nodes.Add(P(i * 5, (i % 4) * 5));
            int asked = 0;
            var r = NavRoutePlanner.Plan(nodes, 0, P(500, 0), (a, b) => { asked++; return nodes[a].FlatDistance(nodes[b]); }, maxQueries: 30);
            Assert.That(asked, Is.LessThanOrEqualTo(30));
            Assert.That(r.Queries, Is.EqualTo(asked));
            Assert.That(r.Moves);
        }

        [Test]
        public void StuckPeriodsGrowWithTheWay()
        {
            Assert.That(NavRoutePlanner.StuckPeriods(0), Is.EqualTo(2));
            Assert.That(NavRoutePlanner.StuckPeriods(30), Is.EqualTo(2));
            Assert.That(NavRoutePlanner.StuckPeriods(200), Is.EqualTo(6));
            Assert.That(NavRoutePlanner.StuckPeriods(1950), Is.EqualTo(12));
        }
    
        [Test]
        public void TheCurrentLegIsKeptUnlessTheNewPlanIsClearlyShorter()
        {
            // editor-runner-24: Hauke's legs flipped between floor levels (route 298 / 105 / 146 / 471 m) on every replan
            Assert.That(NavRoutePlanner.KeepLeg(300f, 290f), Is.True, "a little shorter: keep");
            Assert.That(NavRoutePlanner.KeepLeg(300f, 471f), Is.True, "longer: keep");
            Assert.That(NavRoutePlanner.KeepLeg(300f, float.PositiveInfinity), Is.True, "the new plan has no way on: keep");
            Assert.That(NavRoutePlanner.KeepLeg(300f, 105f), Is.False, "much shorter: switch");
            Assert.That(NavRoutePlanner.KeepLeg(float.PositiveInfinity, 400f), Is.False, "the leg is no longer reachable");
            Assert.That(NavRoutePlanner.KeepLeg(float.NaN, 400f), Is.False);
            Assert.That(NavRoutePlanner.KeepLeg(20f, 1f), Is.False, "short ways: the metres margin");
            Assert.That(NavRoutePlanner.KeepLeg(20f, 3f), Is.True);
        }

        [Test]
        public void IslandExitsLookAllAroundTowardsTheGoalFirst()
        {
            var d = NavRoutePlanner.IslandExitDegrees(24);
            Assert.That(d.Length, Is.EqualTo(24));
            Assert.That(d[0], Is.EqualTo(0f));
            Assert.That(d.Distinct().Count(), Is.EqualTo(24), "no direction twice");
            Assert.That(d.Any(x => Math.Abs(Math.Abs(x) - 180f) < 1e-3), "straight away from the goal too");
            Assert.That(Math.Abs(d[1]), Is.EqualTo(15f).Within(1e-3), "the next ones beside the way to the goal");
            Assert.That(NavRoutePlanner.IslandExitDegrees(1), Is.EqualTo(new[] { 0f }));
        }

        [Test]
        public void TheIslandExitClosestToTheGoalWins()
        {
            Assert.That(NavRoutePlanner.BetterExit(true, 0f, 50f, false, 5f, 3f), Is.True, "a complete way on wins");
            Assert.That(NavRoutePlanner.BetterExit(false, 5f, 3f, true, 0f, 50f), Is.False);
            Assert.That(NavRoutePlanner.BetterExit(false, 100f, 3f, false, 200f, 3f), Is.True, "closer to the goal");
            Assert.That(NavRoutePlanner.BetterExit(false, 100.5f, 3f, false, 100f, 10f), Is.True, "about as close: the nearer edge");
        }
}
}
