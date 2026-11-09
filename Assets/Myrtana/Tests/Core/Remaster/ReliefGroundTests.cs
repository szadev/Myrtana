using System;
using System.Collections.Generic;
using System.Numerics;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// Standing on the drawn relief (Docs/remaster-world.md section 14): the ground map matches the mesh, the analytic query
    /// matches the map, swells grow away from placed things, the body/leg rules.
    /// </summary>
    public class ReliefGroundTests
    {
        static float Hills(float x, float z) => 4f * (float)Math.Sin(x * 0.05f) * (float)Math.Cos(z * 0.04f) + 3f * (float)Math.Sin(x * 0.013f + z * 0.02f);

        /// <summary>One grass-material grid cell 0..size with analytic normals, Unity winding (front = +y).</summary>
        static ReliefMesh Grid(float size, int cells, Func<float, float, float> h)
        {
            int n = cells + 1;
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = size * i / cells, z = size * j / cells;
                const float e = 0.01f;
                nrm.Add(Vector3.Normalize(new Vector3(-(h(x + e, z) - h(x - e, z)) / (2 * e), 1, -(h(x, z + e) - h(x, z - e)) / (2 * e))));
                pos.Add(new Vector3(x, h(x, z), z)); uv.Add(new Vector2(x, z));
            }
            for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                int v = j * n + i;
                tri.AddRange(new[] { v, v + n, v + 1, v + 1, v + n, v + n + 1 });
            }
            var mask = ReliefMaterials.Mask("G3_Nature_Ground_Grass_01_A");
            return new ReliefMesh { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv0 = uv.ToArray(), Sub = new[] { tri.ToArray() }, SubAmp = new[] { mask.amp }, SubPhong = new[] { mask.phong } };
        }

        /// <summary>Facts of the mesh with every position <paramref name="open"/> m from gameplay.</summary>
        static ReliefShared Facts(ReliefMesh m, ReliefSettings s, float open)
        {
            TerrainRelief.Weld(m);
            var f = new ReliefFacts(s.walkNy);
            f.Add(m);
            byte b = (byte)Math.Round(255 * Math.Min(1, open / ReliefFacts.OpenRange));
            return f.For(m, _ => b);
        }

        [Test]
        public void SwellsGrowAwayFromPlacedThings()
        {
            var s = ReliefSettings.Default;
            float near = 0, open = 0;
            foreach (var (dist, set) in new[] { (0f, 0), (40f, 1) })
            {
                var m = Grid(60, 12, Hills);
                TerrainRelief.Prepare(m, s, Facts(m, s, dist));
                var o = new ReliefOutput(); TerrainRelief.BuildFar(m, s, o);
                float max = 0;
                for (int i = 0; i < o.VertexCount; i++) max = Math.Max(max, Math.Abs(o.Vertices[i].Uv3.X));
                if (set == 0) near = max; else open = max;
            }
            Assert.That(near, Is.LessThan(0.12f), "next to a path / building / rock the drawn ground stays on the collision");
            Assert.That(open, Is.GreaterThan(0.25f), "open ground gets swells");
            Assert.That(open, Is.LessThan(1.6f));
        }

        [Test]
        public void GroundMapMatchesTheMeshAndTheQuery()
        {
            var s = ReliefSettings.Default;
            var m = Grid(120, 24, Hills);
            TerrainRelief.Prepare(m, s, Facts(m, s, 60));
            var cam = new[] { new ReliefPoint(new Vector3(60, Hills(60, 60) + 2, 60), s.nearSpacing, s.nearSlope) };
            var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
            var near = new ReliefOutput(); TerrainRelief.BuildNear(m, cam, 1, s, near);
            var map = new ReliefGroundMap(256, 0.25f);
            map.Begin(60, 60);
            ReliefMapRaster.Collision(map, m);
            ReliefMapRaster.Visual(map, m, far, near);
            map.Ready = true;

            var rnd = new Random(3);
            int n = 0; float worstCol = 0, worstQuery = 0, maxDelta = 0;
            for (int k = 0; k < 400; k++)
            {
                float x = 35 + (float)rnd.NextDouble() * 50, z = 35 + (float)rnd.NextDouble() * 50;
                Assert.That(map.Sample(x, z, out float col, out float vis), Is.True);
                // the collision is G3's triangle: the map's bilinear value is close to the plane (0.25 m grid on 5 m triangles)
                Assert.That(ReliefGround.CollisionAt(m, x, z, col, 0.5f, out _, out _, out float exact), Is.True);
                worstCol = Math.Max(worstCol, Math.Abs(exact - col));
                Assert.That(ReliefGround.SurfaceAt(m, x, z, col, 0.5f, cam, 1, near.Near, s, out _, out float av), Is.True);
                worstQuery = Math.Max(worstQuery, Math.Abs(av - vis));
                maxDelta = Math.Max(maxDelta, Math.Abs(vis - col));
                n++;
            }
            Assert.That(worstCol, Is.LessThan(0.02f));
            Assert.That(worstQuery, Is.LessThan(0.08f), "the analytic surface vs the drawn mesh");
            Assert.That(maxDelta, Is.GreaterThan(0.1f), "the open ground is moved");
        }

        [Test]
        public void MapKeepsTheVisualNearestTheCollisionAndHalfRoundTrips()
        {
            var map = new ReliefGroundMap(9, 1f);
            map.Begin(4, 4);
            // collision plane y = 0 over the whole map, a drawn layer at +0.3 and a far one at +5 (an overhang): the near one wins
            map.Collision(new Vector3(0, 0, 0), new Vector3(0, 0, 8), new Vector3(8, 0, 0));
            map.Collision(new Vector3(8, 0, 0), new Vector3(0, 0, 8), new Vector3(8, 0, 8));
            map.Visual(new Vector3(0, 5, 0), new Vector3(0, 5, 8), new Vector3(8, 5, 0));
            map.Visual(new Vector3(0, 0.3f, 0), new Vector3(0, 0.3f, 8), new Vector3(8, 0.3f, 0));
            map.Visual(new Vector3(8, 0.3f, 0), new Vector3(0, 0.3f, 8), new Vector3(8, 0.3f, 8));
            map.Ready = true;
            Assert.That(map.Sample(2.5f, 3.5f, out float c, out float v), Is.True);
            Assert.That(c, Is.EqualTo(0).Within(1e-5f));
            Assert.That(v, Is.EqualTo(0.3f).Within(1e-5f));
            var h = new ushort[162]; map.DeltaHalf(h, map.BaseHeight());
            Assert.That(h[80], Is.EqualTo(0x34CD).Or.EqualTo(0x34CC));   // 0.3 as an IEEE half
            Assert.That(h[81], Is.EqualTo(0));                           // collision at the base height
            Assert.That(ReliefGroundMap.Half(-1.5f), Is.EqualTo(0xBE00));
        }

        [Test]
        public void MapFindsTheGroundUnderAnOverhang()
        {
            // snow3 (build 20c "ik False"): a valley floor at y 0 with a terrain lip at y 6 over its x < 4 half; things on the
            // floor under the lip must get the floor, things on the lip the lip
            var map = new ReliefGroundMap(9, 1f);
            map.Begin(4, 4);
            map.Collision(new Vector3(0, 0, 0), new Vector3(0, 0, 8), new Vector3(8, 0, 0));
            map.Collision(new Vector3(8, 0, 0), new Vector3(0, 0, 8), new Vector3(8, 0, 8));
            map.Collision(new Vector3(-1, 6, -1), new Vector3(-1, 6, 9), new Vector3(4.2f, 6, -1));
            map.Collision(new Vector3(4.2f, 6, -1), new Vector3(-1, 6, 9), new Vector3(4.2f, 6, 9));
            map.Visual(new Vector3(0, 0.4f, 0), new Vector3(0, 0.4f, 8), new Vector3(8, 0.4f, 0));
            map.Visual(new Vector3(8, 0.4f, 0), new Vector3(0, 0.4f, 8), new Vector3(8, 0.4f, 8));
            map.Visual(new Vector3(-1, 6.2f, -1), new Vector3(-1, 6.2f, 9), new Vector3(4.2f, 6.2f, -1));
            map.Visual(new Vector3(4.2f, 6.2f, -1), new Vector3(-1, 6.2f, 9), new Vector3(4.2f, 6.2f, 9));
            map.Ready = true;
            Assert.That(map.Sample(2.5f, 0.05f, 3.5f, out float c, out float v), Is.True);
            Assert.That(c, Is.EqualTo(0).Within(1e-5f), "floor under the lip");
            Assert.That(v, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(map.Sample(2.5f, 6.1f, 3.5f, out c, out v), Is.True);
            Assert.That(c, Is.EqualTo(6).Within(1e-5f), "on the lip");
            Assert.That(v, Is.EqualTo(6.2f).Within(1e-5f));
            Assert.That(map.Sample(6.5f, 0.05f, 3.5f, out c, out v), Is.True);
            Assert.That(c, Is.EqualTo(0).Within(1e-5f), "open floor");
            Assert.That(map.Sample(2.5f, 3.5f, out c, out _), Is.True);
            Assert.That(c, Is.EqualTo(6).Within(1e-5f), "no height: the top layer as before");
        }

        [Test]
        public void BodyGoesToTheLowerFootWithinLimits()
        {
            Assert.That(ReliefFootingRules.BodyOffset(0.2f, 0.1f, 0.3f), Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(ReliefFootingRules.BodyOffset(0.2f, -0.5f, 0.3f), Is.EqualTo(0.2f - ReliefFootingRules.MaxCrouch).Within(1e-6f));
            Assert.That(ReliefFootingRules.BodyOffset(0f, 0.4f, 0.5f), Is.EqualTo(ReliefFootingRules.MaxLift).Within(1e-6f));
            Assert.That(ReliefFootingRules.BodyOffset(-0.4f, float.NaN, float.NaN), Is.EqualTo(-0.4f).Within(1e-6f));
            Assert.That(ReliefFootingRules.FootRaise(0.3f, 0.1f), Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(ReliefFootingRules.FootRaise(float.NaN, 0.1f), Is.EqualTo(0f));
        }

        [Test]
        public void LegIkOnlyForSlowGaitsAndPlantedFeet()
        {
            // fix-173: run / sprint keep the animation's legs, walking reaches the ground
            Assert.That(ReliefFootingRules.LegWeight(0f), Is.EqualTo(1f));
            Assert.That(ReliefFootingRules.LegWeight(1.6f), Is.EqualTo(1f), "walk");
            Assert.That(ReliefFootingRules.LegWeight(4f), Is.EqualTo(0f), "run");
            Assert.That(ReliefFootingRules.LegWeight(6f), Is.EqualTo(0f), "sprint");
            float mid = ReliefFootingRules.LegWeight(2.45f);
            Assert.That(mid, Is.GreaterThan(0.3f).And.LessThan(0.7f), "a smooth fade, no switch");
            Assert.That(ReliefFootingRules.Contact(0.10f, 0.10f), Is.EqualTo(1f), "at rest: down");
            Assert.That(ReliefFootingRules.Contact(0.35f, 0.10f), Is.EqualTo(0f), "lifted: swinging");
            Assert.That(ReliefFootingRules.Contact(float.NaN, 0.1f), Is.EqualTo(0f));
            // a swinging foot is never raised over the animation unless it would go into the ground
            Assert.That(ReliefFootingRules.FootRaise(0.3f, 0f, 0f, 0.4f), Is.EqualTo(0f), "swing foot clear of the hill");
            Assert.That(ReliefFootingRules.FootRaise(0.3f, 0f, 0f, 0.1f), Is.EqualTo(0.2f).Within(1e-6f), "only out of the ground");
            Assert.That(ReliefFootingRules.FootRaise(-0.3f, 0f, 0f, 0.1f), Is.EqualTo(0f), "a swinging foot is not pulled into a hollow");
            Assert.That(ReliefFootingRules.FootRaise(0.3f, 0.1f, 1f, 0f), Is.EqualTo(0.2f).Within(1e-6f), "planted: as before");
            // body: running follows the root's ground only; a swinging foot does not drag the walking body down
            Assert.That(ReliefFootingRules.BodyOffset(0.2f, -0.3f, 0.5f, 1f, 1f, 0f), Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(ReliefFootingRules.BodyOffset(0.2f, -0.3f, 0.5f, 1f, 1f, 1f), Is.EqualTo(ReliefFootingRules.BodyOffset(0.2f, -0.3f, 0.5f)).Within(1e-6f));
            Assert.That(ReliefFootingRules.BodyOffset(0.2f, -0.3f, 0.5f, 0f, 1f, 1f), Is.EqualTo(0.2f).Within(1e-6f), "left foot in the air");
        }

        [Test]
        public void TwoBoneIkReachesTheTargetAndKeepsTheBendSide()
        {
            // a leg: hip 0.9 m up, knee bent forward (+z), ankle under the hip
            var a = new Vector3(0, 0.9f, 0); var b = new Vector3(0, 0.48f, 0.08f); var c = new Vector3(0, 0.08f, 0);
            foreach (var t in new[] { new Vector3(0, 0.25f, 0), new Vector3(0.05f, 0.15f, 0.1f), new Vector3(0, 0.1f, -0.05f) })
            {
                Assert.That(ReliefFootingRules.TwoBone(a, b, c, t, Vector3.UnitX, out var hip, out var knee), Is.True);
                var b1 = a + Vector3.Transform(b - a, hip);
                var c1 = b1 + Vector3.Transform(c - b, knee);
                Assert.That(Vector3.Distance(c1, t), Is.LessThan(0.002f), $"target {t}");
                Assert.That(Vector3.Distance(a, b1), Is.EqualTo(Vector3.Distance(a, b)).Within(1e-4f));
                Assert.That(b1.Z, Is.GreaterThan(t.Z * 0.5f), "the knee still points forward");
            }
            // out of reach: stretched towards it, no NaN
            Assert.That(ReliefFootingRules.TwoBone(a, b, c, new Vector3(0, -2, 0), Vector3.UnitX, out var h2, out var k2), Is.True);
            Assert.That(float.IsNaN(h2.X) || float.IsNaN(k2.W), Is.False);
        }

        [Test]
        public void ReusedChunkOutputsAndMergeTarget_GiveTheSameNearBuild()
        {
            // perf-5: the near batches reuse their chunk outputs and merge into the part the last apply replaced
            var s = ReliefSettings.Default;
            var m = Grid(120, 24, Hills);
            TerrainRelief.Prepare(m, s, Facts(m, s, 60));
            var parts = new List<ReliefOutput> { new ReliefOutput(), new ReliefOutput(), new ReliefOutput() };
            var target = new ReliefOutput();
            foreach (var at in new[] { 40f, 70f, 55f })
            {
                var cam = new[] { new ReliefPoint(new Vector3(at, Hills(at, at) + 2, at), s.nearSpacing, s.nearSlope) };
                var whole = new ReliefOutput(); TerrainRelief.BuildNear(m, cam, 1, s, whole);
                int step = (m.TriCount + 2) / 3;
                for (int k = 0; k < 3; k++) TerrainRelief.BuildNear(m, cam, 1, s, parts[k], k * step, Math.Min(m.TriCount, (k + 1) * step));
                var merged = TerrainRelief.Merge(m, parts, target);
                Assert.That(merged, Is.SameAs(target));
                Assert.That(merged.VertexCount, Is.EqualTo(whole.VertexCount), $"vertices at {at}");
                Assert.That(merged.NearTriangles, Is.EqualTo(whole.NearTriangles));
                for (int i = 0; i < whole.VertexCount; i++)
                    Assert.That(merged.Vertices[i].Position, Is.EqualTo(whole.Vertices[i].Position));
                Assert.That(merged.Indices[0], Is.EqualTo(whole.Indices[0]));
                Assert.That(merged.Near, Is.EqualTo(whole.Near));
            }
            var cleared = target.Cleared(m);
            Assert.That(cleared.VertexCount, Is.Zero);
            Assert.That(cleared.NearTriangles, Is.Zero);
            Assert.That(Array.IndexOf(cleared.Near, true), Is.EqualTo(-1));
        }
    }
}
