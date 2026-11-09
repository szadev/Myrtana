using System.Linq;
using System.Text.Json;
using Myrtana.Core;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class NavGraphTests
    {
        /// <summary>An axis-aligned square zone at height y (band y-1 .. y+1.5).</summary>
        static NavZoneDef Square(string id, float x0, float z0, float x1, float z1, float y = 0, NavLinkFlags link = NavLinkFlags.None)
        {
            var pts = new[] { x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1 };
            float cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
            float r = (float)System.Math.Sqrt((x1 - cx) * (x1 - cx) + (z1 - cz) * (z1 - cz)) + 0.01f;
            return new NavZoneDef(id, id, pts, new[] { cx, cz }, r, y + 1.5f, y - 1f, link);
        }

        static NavPathDef Path(string id, float width, params float[] xyz) =>
            new NavPathDef(id, xyz, Enumerable.Repeat(width, xyz.Length / 3).ToArray(), "dc92a6e0-e73e-4e4e-8074-16fc57d7f52d", null,
                           xyz.Where((v, i) => i % 3 == 1).Max() + 1.5f, xyz.Where((v, i) => i % 3 == 1).Min() - 1f, false, NavLinkFlags.None);

        /// <summary>Zones A (0..10) and B (40..50) on the x axis, joined by a path 9 → 41; zone C (100..110) has no path.</summary>
        static NavSectorDef World(string sector = "S") => new NavSectorDef(sector,
            new[] { Square("A", 0, 0, 10, 10), Square("B", 40, 0, 50, 10), Square("C", 100, 0, 110, 10) },
            new[] { Path("AB", 1f, 9, 0, 5, 25, 0, 5, 41, 0, 5) });

        [Test]
        public void PathEndsAreLinkedToTheZonesAroundThemNotToTheStoredIds()
        {
            var g = new NavGraph(new[] { World() });
            Assert.That(g.DanglingPathEnds, Is.EqualTo(0));
            Assert.That(g.RelinkedPathEnds, Is.EqualTo(2), "the placeholder id and the empty one are replaced");
            var a = g.Locate(2, 0, 2);
            var b = g.Locate(48, 0, 8);
            Assert.That(g.NetOf(a), Is.EqualTo(g.NetOf(b)));
            Assert.That(g.NetOf(g.Locate(105, 0, 5)), Is.EqualTo(-1), "C has no paths");
        }

        [Test]
        public void RouteRunsFromZoneOverThePathRibbonIntoTheNextZone()
        {
            var g = new NavGraph(new[] { World() });
            var r = g.FindRoute(2, 0, 5, 48, 0, 5);
            Assert.That(r.Found, Is.True, r.Note);
            Assert.That(r.Length, Is.EqualTo(46f).Within(0.1f));
            Assert.That(r.Points.First().X, Is.EqualTo(2f));
            Assert.That(r.Points.Last().X, Is.EqualTo(48f));
            Assert.That(r.Points.Any(p => p.On == NavAreaKind.Path && p.X == 25f && p.Width == 1f), "the path's middle point with its width");
        }

        [Test]
        public void RepeatedAndInterleavedRoutes_AreTheSame_WithoutAllocatingTheSearch()
        {
            // perf-9: the A* state and the snap sets are the graph's reusable scratch; a route must not see the last one's.
            var g = new NavGraph(new[] { World() });
            string Describe(NavRoute r) => r.Found + " " + string.Join(";", r.Points.Select(p => $"{p.X:0.00},{p.Z:0.00}"));
            string there = Describe(g.FindRoute(2, 0, 5, 48, 0, 5));
            string back = Describe(g.FindRoute(48, 0, 8, 2, 0, 2));
            string none = Describe(g.FindRoute(2, 0, 5, 105, 0, 5));
            for (int i = 0; i < 3; i++)
            {
                Assert.That(Describe(g.FindRoute(2, 0, 5, 48, 0, 5)), Is.EqualTo(there));
                Assert.That(Describe(g.FindRoute(2, 0, 5, 105, 0, 5)), Is.EqualTo(none));
                Assert.That(Describe(g.FindRoute(48, 0, 8, 2, 0, 2)), Is.EqualTo(back));
            }
            Assert.That(there, Does.StartWith("True"));
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) g.FindRoute(2, 0, 5, 48, 0, 5);
            long perRoute = (System.GC.GetAllocatedBytesForCurrentThread() - before) / 50;
            Assert.That(perRoute, Is.LessThan(2048), "only the route itself (points, note)");
        }

        [Test]
        public void ZoneWithoutPathsAndUnknownPlacesHaveNoRoute()
        {
            var far = new NavGraph(new[] { new NavSectorDef("S",
                new[] { Square("A", 0, 0, 10, 10), Square("B", 40, 0, 50, 10), Square("C", 200, 0, 210, 10) },
                new[] { Path("AB", 1f, 9, 0, 5, 25, 0, 5, 41, 0, 5) }) });
            var r = far.FindRoute(2, 0, 5, 205, 0, 5);
            Assert.That(r.Found, Is.False, "C has no paths and no net within ConnectSnap");
            Assert.That(r.Note, Does.Contain("goal zone has no paths"));
            var g = new NavGraph(new[] { World() });
            var off = g.FindRoute(2, 0, 5, 75, 0, 5, snap: 5f);
            Assert.That(off.Found, Is.False);
            Assert.That(off.Note, Does.Contain("goal"));
        }

        /// <summary>fix-37 (hammerclan_02: Mort in Hammerclan_Ore, "start zone has no paths"): an end in a zone without
        /// paths steps onto the other end's net, the nearest zone within ConnectSnap.</summary>
        [Test]
        public void EndInAZoneWithoutPathsStepsOntoTheNearestConnectedZone()
        {
            var g = new NavGraph(new[] { World() });
            var r = g.FindRoute(105, 0, 5, 2, 0, 5);
            Assert.That(r.Found, Is.True, r.Note);
            Assert.That(r.Note, Does.Contain("start zone without paths"));
            Assert.That(g.Describe(r.Start), Does.Contain("B"));
            Assert.That(r.Points.First().X, Is.EqualTo(105f), "the route starts where the walker stands");
            Assert.That(r.Points.Last().X, Is.EqualTo(2f));
            var back = g.FindRoute(2, 0, 5, 105, 0, 5);
            Assert.That(back.Found, Is.True, back.Note);
            Assert.That(back.Note, Does.Contain("goal zone without paths"));
        }

        /// <summary>A small zone without paths inside a big zone of the net (an ore field in a camp): the big zone takes the start.</summary>
        [Test]
        public void SmallZoneWithoutPathsInsideAConnectedZoneUsesTheContainingZone()
        {
            var g = new NavGraph(new[] { new NavSectorDef("S",
                new[] { Square("Camp", 0, 0, 30, 30), Square("Ore", 12, 12, 16, 16), Square("B", 60, 0, 70, 10) },
                new[] { Path("CB", 1f, 29, 0, 5, 45, 0, 5, 61, 0, 5) }) });
            Assert.That(g.NetOf(g.Locate(14, 0, 14)), Is.EqualTo(-1), "the ore zone is the smallest and has no paths");
            var r = g.FindRoute(14, 0, 14, 65, 0, 5);
            Assert.That(r.Found, Is.True, r.Note);
            Assert.That(r.Start.Snapped, Is.EqualTo(0f), "contained, not snapped");
            Assert.That(g.Describe(r.Start), Does.Contain("Camp"));
        }

        [Test]
        public void PositionsNearAZoneAreSnappedOntoIt()
        {
            var g = new NavGraph(new[] { World() });
            var a = g.Locate(-3, 0, 5, snap: 5f);
            Assert.That(a.Kind, Is.EqualTo(NavAreaKind.Zone));
            Assert.That(a.Snapped, Is.EqualTo(3f).Within(0.01f));
            Assert.That(g.Locate(-3, 0, 5).IsValid, Is.False, "no snap by default");
        }

        [Test]
        public void HeightBandSeparatesFloorsAndTheSmallestZoneWins()
        {
            var g = new NavGraph(new[]
            {
                new NavSectorDef("S", new[] { Square("Ground", 0, 0, 100, 100), Square("Hut", 10, 10, 20, 20), Square("Roof", 10, 10, 20, 20, y: 8) },
                                 new NavPathDef[0]),
            });
            Assert.That(g.ZoneName(g.Locate(15, 0, 15).Index), Is.EqualTo("Hut"), "smallest radius");
            Assert.That(g.ZoneName(g.Locate(15, 8, 15).Index), Is.EqualTo("Roof"), "the height band");
            Assert.That(g.ZoneName(g.Locate(50, 0, 50).Index), Is.EqualTo("Ground"));
            Assert.That(g.Locate(50, 5, 50).IsValid, Is.False, "above every band");
        }

        [Test]
        public void LinkInnerPriorityBeatsASmallerZone()
        {
            var g = new NavGraph(new[]
            {
                new NavSectorDef("S", new[] { Square("Big", 0, 0, 100, 100, link: NavLinkFlags.Inner), Square("Small", 10, 10, 20, 20) }, new NavPathDef[0]),
            });
            Assert.That(g.ZoneName(g.Locate(15, 0, 15).Index), Is.EqualTo("Big"));
        }

        [Test]
        public void PositionOnAPathRibbonIsOnThePath()
        {
            var g = new NavGraph(new[] { World() });
            var a = g.Locate(25, 0, 5.8f);
            Assert.That(a.Kind, Is.EqualTo(NavAreaKind.Path));
            Assert.That(g.Locate(25, 0, 7f).IsValid, Is.False, "beyond the half-width");
            var r = g.FindRoute(25, 0, 5, 48, 0, 5);
            Assert.That(r.Found, Is.True, r.Note);
            Assert.That(r.Length, Is.EqualTo(23f).Within(0.1f));
        }

        [Test]
        public void ShorterOfTwoWaysIsChosen()
        {
            // A to B directly (length 32) or over a detour through zone D (much longer)
            var g = new NavGraph(new[]
            {
                new NavSectorDef("S", new[] { Square("A", 0, 0, 10, 10), Square("B", 40, 0, 50, 10), Square("D", 20, 60, 30, 70) },
                    new[]
                    {
                        Path("AB", 1, 9, 0, 5, 41, 0, 5),
                        Path("AD", 1, 5, 0, 9, 22, 0, 61),
                        Path("DB", 1, 28, 0, 61, 45, 0, 9),
                    }),
            });
            var r = g.FindRoute(5, 0, 5, 45, 0, 5);
            Assert.That(r.Found, Is.True, r.Note);
            Assert.That(r.Points.Any(p => p.Z > 50), Is.False, "not over D");
            Assert.That(r.Length, Is.EqualTo(40f).Within(0.5f));
        }

        [Test]
        public void SwitchedOffSectorsAreNotUsed()
        {
            bool on = true;
            var g = new NavGraph(new[]
            {
                new NavSectorDef("Base", new[] { Square("A", 0, 0, 10, 10), Square("B", 40, 0, 50, 10) }, new NavPathDef[0]),
                new NavSectorDef("Bridge", new NavZoneDef[0], new[] { Path("AB", 1, 9, 0, 5, 41, 0, 5) }),
            }, s => s != "Bridge" || on);
            Assert.That(g.FindRoute(5, 0, 5, 45, 0, 5).Found, Is.True);
            on = false;
            g.RefreshSectors();
            Assert.That(g.FindRoute(5, 0, 5, 45, 0, 5).Found, Is.False);
        }

        [Test]
        public void RouteRunsAPathBackwardsWhenNeeded()
        {
            var g = new NavGraph(new[] { World() });
            var r = g.FindRoute(48, 0, 5, 2, 0, 5);
            Assert.That(r.Found, Is.True, r.Note);
            var xs = r.Points.Select(p => p.X).ToList();
            for (int i = 1; i < xs.Count; i++) Assert.That(xs[i], Is.LessThanOrEqualTo(xs[i - 1] + 1e-3f), "monotone back to A");
        }

        [Test]
        public void NavSectorSurvivesTheContentJson()
        {
            var schema = new ContentSchema();
            SimulationSchema.Register(schema);
            var options = ContentJson.CreateOptions(schema);
            var back = JsonSerializer.Deserialize<NavSectorDef>(JsonSerializer.Serialize(World(), options), options);
            Assert.That(back.Id, Is.EqualTo("S"));
            Assert.That(back.Zones.Length, Is.EqualTo(3));
            Assert.That(back.Paths[0].Radii, Is.EqualTo(new[] { 1f, 1f, 1f }));
            Assert.That(back.Paths[0].ZoneA, Is.EqualTo("dc92a6e0-e73e-4e4e-8074-16fc57d7f52d"));
            Assert.That(new NavGraph(new[] { back }).FindRoute(2, 0, 5, 48, 0, 5).Found, Is.True);
        }

        [Test]
        public void FollowerHandsOutTheRoutePointsInTurn()
        {
            var g = new NavGraph(new[] { World() });
            var f = new NavRouteFollower();
            Assert.That(f.Update(g, 0, 2, 0, 5, 48, 0, 5, out var leg, out bool planned), Is.True, f.Note);
            Assert.That(planned, Is.True);
            Assert.That(leg.X, Is.EqualTo(9f), "the path's first point");
            Assert.That(f.Update(g, 0.1, 8.5f, 0, 5, 48, 0, 5, out leg, out planned), Is.True);
            Assert.That(planned, Is.False);
            Assert.That(leg.X, Is.EqualTo(25f), "reached within the reach: the middle point");
            f.Update(g, 0.2, 30, 0, 5.5f, 48, 0, 5, out leg, out _);
            Assert.That(leg.X, Is.EqualTo(41f), "passed the middle point on the ribbon");
            f.LegReached();
            f.Update(g, 0.3, 41, 0, 5, 48, 0, 5, out leg, out _);
            Assert.That(leg.X, Is.EqualTo(48f));
            Assert.That(f.AtLastPoint, Is.True);
        }

        [Test]
        public void FollowerReplansWhenTheGoalMovesAndGivesUpAfterStuckPeriods()
        {
            var g = new NavGraph(new[] { World() });
            var f = new NavRouteFollower();
            f.Update(g, 0, 2, 0, 5, 48, 0, 5, out _, out _);
            f.Update(g, 0.5, 2, 0, 5, 42, 0, 5, out _, out bool planned);
            Assert.That(planned, Is.False, "moved less than GoalMoved");
            f.Update(g, 2, 2, 0, 5, 2, 0, 9, out _, out planned);
            Assert.That(planned, Is.True);
            Assert.That(f.Route.Note, Is.EqualTo("same area"));
            f.Update(g, 4, 2, 0, 5, 48, 0, 5, out var leg, out _);
            Assert.That(f.Stuck(4), Is.False);
            Assert.That(f.Ribbon, Is.True, "first stuck period: along the ribbon");
            Assert.That(f.Stuck(8), Is.False);
            Assert.That(f.Next, Is.EqualTo(2), "second: the leg is skipped");
            Assert.That(f.Stuck(12), Is.False);
            Assert.That(f.Stuck(16), Is.True, "then the follower gives up");
            Assert.That(f.Update(g, 20, 2, 0, 5, 48, 0, 5, out _, out _), Is.False, "off the graph for a while");
            Assert.That(f.Update(g, 37, 2, 0, 5, 48, 0, 5, out _, out _), Is.True, "then routed again");
        }

        [Test]
        public void FollowerWithoutRouteRetriesOnlyNowAndThen()
        {
            var g = new NavGraph(new[] { World() });
            var f = new NavRouteFollower();
            Assert.That(f.Update(g, 0, 2, 0, 5, 300, 0, 5, out _, out bool planned), Is.False);
            Assert.That(planned, Is.True);
            f.Update(g, 2, 2, 0, 5, 300, 0, 5, out _, out planned);
            Assert.That(planned, Is.False);
            f.Update(g, 6, 2, 0, 5, 300, 0, 5, out _, out planned);
            Assert.That(planned, Is.True);
            Assert.That(f.Update(null, 7, 2, 0, 5, 48, 0, 5, out _, out _), Is.False, "no graph");
        }
    }
}
