using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The way across a non-convex nav zone bends round its reflex corners (fix-35: Xardas' valley).</summary>
    public class NavZoneWayTests
    {
        /// <summary>A U open to +z: arms x 0..10 and 20..30, the notch x 10..20 above z 10.</summary>
        static readonly float[] U = Poly(0, 0, 30, 0, 30, 30, 20, 30, 20, 10, 10, 10, 10, 30, 0, 30);

        /// <summary>
        /// Nav_Zone df679bb8 of G3_Nordmar_01_Landscape_NavZones_01 (the valley below Xardas' tower), rounded to the metre
        /// as `G3Cli` prints it; the route Xardas -> Adanos_Gate enters it at (940, 1152) and leaves at (864, 1212).
        /// </summary>
        static readonly float[] Valley = Poly(852, 1197, 868, 1183, 874, 1184, 890, 1172, 893, 1167, 891, 1162, 894, 1156, 900, 1154, 907, 1143,
                                              903, 1133, 895, 1136, 890, 1144, 891, 1131, 910, 1129, 930, 1141, 943, 1148, 940, 1156, 936, 1153,
                                              930, 1152, 921, 1151, 918, 1152, 914, 1153, 893, 1185, 885, 1199, 882, 1212, 868, 1218, 849, 1212);

        static float[] Poly(params float[] xz)
        {
            var p = new float[xz.Length / 2 * 3];
            for (int i = 0; i < xz.Length / 2; i++) { p[3 * i] = xz[2 * i]; p[3 * i + 1] = 165f; p[3 * i + 2] = xz[2 * i + 1]; }
            return p;
        }

        static float[] Reversed(float[] p)
        {
            int n = p.Length / 3;
            var r = new float[p.Length];
            for (int i = 0; i < n; i++) Array.Copy(p, 3 * (n - 1 - i), r, 3 * i, 3);
            return r;
        }

        static bool Inside(float[] p, float x, float z)
        {
            bool inside = false;
            int n = p.Length / 3;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = p[3 * i], zi = p[3 * i + 2], xj = p[3 * j], zj = p[3 * j + 2];
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>The way's polyline a, bends.., b.</summary>
        static List<(float x, float z)> Polyline(NavZoneWay way, List<int> bends, float ax, float az, float bx, float bz)
        {
            var l = new List<(float, float)> { (ax, az) };
            foreach (int i in bends)
            {
                way.Corner(i, out float x, out _, out float z);
                l.Add((x, z));
            }
            l.Add((bx, bz));
            return l;
        }

        /// <summary>Share of samples (every 0.25 m) along the polyline that lie outside the polygon.</summary>
        static int OutsideSamples(float[] poly, List<(float x, float z)> line)
        {
            int outside = 0;
            for (int k = 1; k < line.Count; k++)
            {
                var (ax, az) = line[k - 1];
                var (bx, bz) = line[k];
                float len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                int steps = Math.Max(1, (int)(len / 0.25f));
                for (int s = 1; s < steps; s++)
                {
                    float t = s / (float)steps;
                    if (!Inside(poly, ax + (bx - ax) * t, az + (bz - az) * t)) outside++;
                }
            }
            return outside;
        }

        [Test]
        public void ConvexZoneIsCrossedStraight()
        {
            var way = new NavZoneWay(Poly(0, 0, 10, 0, 10, 10, 0, 10));
            Assert.That(way.Convex, Is.True);
            Assert.That(way.Way(1, 1, 9, 9, out float len), Is.Empty);
            Assert.That(len, Is.EqualTo((float)Math.Sqrt(128)).Within(1e-3f));
        }

        [Test]
        public void WayRoundTheNotchBendsAtTheTwoInnerCornersEitherWinding([Values(false, true)] bool reversed)
        {
            var poly = reversed ? Reversed(U) : U;
            var way = new NavZoneWay(poly);
            Assert.That(way.CornerCount, Is.EqualTo(2), "the notch's two corners");
            var bends = way.Way(5, 25, 25, 25, out float len);
            Assert.That(bends.Count, Is.EqualTo(2));
            foreach (int i in bends)
            {
                way.Corner(i, out float x, out _, out float z);
                Assert.That(z, Is.LessThan(10f), "below the notch");
                Assert.That(Inside(poly, x, z), Is.True);
            }
            Assert.That(len, Is.GreaterThan(40f), "down the arm, under the notch, up the other arm");
            Assert.That(OutsideSamples(poly, Polyline(way, bends, 5, 25, 25, 25)), Is.EqualTo(0));
        }

        [Test]
        public void StraightLineInsideANonConvexZoneStaysStraight()
        {
            var way = new NavZoneWay(U);
            Assert.That(way.Way(2, 25, 8, 3, out _), Is.Empty, "within one arm");
            Assert.That(way.Way(2, 5, 28, 5, out _), Is.Empty, "along the bottom");
        }

        [Test]
        public void EndsJustOutsideTheZoneDoNotBlockTheStraightLine()
        {
            // a path end 0.6 m outside the left arm's edge, walking into the arm
            var way = new NavZoneWay(U);
            Assert.That(way.Way(-0.6f, 20, 5, 15, out _), Is.Empty);
        }

        [Test]
        public void XardasValleyIsWalkedRoundItsCliffsNotThroughThem()
        {
            var way = new NavZoneWay(Valley);
            var straight = new List<(float, float)> { (940, 1152), (864, 1212) };
            Assert.That(OutsideSamples(Valley, straight), Is.GreaterThan(40), "the straight line leaves the valley (the bug)");
            var bends = way.Way(940, 1152, 864, 1212, out float len);
            Assert.That(bends, Is.Not.Empty);
            Assert.That(len, Is.GreaterThan(97f).And.LessThan(140f));
            Assert.That(OutsideSamples(Valley, Polyline(way, bends, 940, 1152, 864, 1212)), Is.EqualTo(0));
        }

        [Test]
        public void RouteThroughAWindingZoneCarriesTheBends()
        {
            // the U as a zone; a path comes into the top of the left arm, another leaves from the top of the right arm
            var u = new NavZoneDef("U", "U", U, new[] { 15f, 15f }, 22f, 166.5f, 164f, NavLinkFlags.None);
            NavPathDef P(string id, params float[] xyz) =>
                new NavPathDef(id, xyz, Enumerable.Repeat(1f, xyz.Length / 3).ToArray(), null, null, 167f, 160f, false, NavLinkFlags.None);
            var g = new NavGraph(new[]
            {
                new NavSectorDef("S", new[] { u }, new[] { P("In", 5, 165, 60, 5, 165, 28), P("Out", 25, 165, 28, 25, 165, 60) }),
            });
            var r = g.FindRoute(5, 165, 55, 25, 165, 55);
            Assert.That(r.Found, Is.True, r.Note);
            // zone points: the two bends, then the out path's end node (25, 28)
            var zone = r.Points.Where(p => p.On == NavAreaKind.Zone).ToList();
            Assert.That(zone.Count, Is.EqualTo(3), string.Join(" ", r.Points.Select(p => $"({p.X:0.0},{p.Z:0.0},{p.On})")));
            Assert.That(zone.Take(2).All(p => p.Z < 10f && Inside(U, p.X, p.Z)), "the bends below the notch");
            Assert.That(zone[2].X, Is.EqualTo(25f));
            Assert.That(r.Length, Is.GreaterThan(100f), "the bends count in the route's length");
        }
    }
}
