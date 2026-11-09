using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-171 [owner, build 21a: "the mountain changes as I move"]: the relief's macro shape is the same in every mesh, the fine
    /// octaves and the strata are faded by the live distance in the shader (G3ReliefMorph.hlsl), and the GPU vertex rebuilds
    /// the CPU position exactly at the vertex's own spacing.
    /// </summary>
    public class TerrainReliefMorphTests
    {
        static ReliefMesh Grid(float x0, float z0, float size, int cells, Func<float, float, float> h)
        {
            int n = cells + 1;
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = x0 + size * i / cells, z = z0 + size * j / cells;
                const float e = 0.01f;
                pos.Add(new Vector3(x, h(x, z), z));
                nrm.Add(Vector3.Normalize(new Vector3(-(h(x + e, z) - h(x - e, z)) / (2 * e), 1, -(h(x, z + e) - h(x, z - e)) / (2 * e))));
                uv.Add(new Vector2(x, z));
            }
            for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                int a = j * n + i;
                tri.AddRange(new[] { a, a + n, a + 1, a + 1, a + n, a + n + 1 });
            }
            var mask = ReliefMaterials.Mask("G3_Nature_Myrtana_Rock_02_A");
            return new ReliefMesh { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv0 = uv.ToArray(), Sub = new[] { tri.ToArray() }, SubAmp = new[] { mask.amp }, SubPhong = new[] { mask.phong } };
        }

        static float Cliff(float x, float z) => 20f / (1f + (float)Math.Exp(-(x - 30f) * 0.6f)) + 3f * (float)Math.Sin(z * 0.07f);

        /// <summary>The shader position (G3ReliefMorph.hlsl) at spacing <paramref name="sigma"/>, in C#.</summary>
        static Vector3 Gpu(in ReliefVertex v, float sigma, ReliefSettings s)
        {
            var p0 = v.Position - v.FineOffset * v.Ng;
            if (v.Sigma >= s.farSpacing - 1e-3f) return p0;
            sigma = Math.Max(sigma, v.Sigma);
            float fine = 0;
            for (int k = 0; k < FineOctaves.Count; k++) fine += ReliefNoise.Band(TerrainRelief.FineLambda(k, s), sigma) * v.Fine[k];
            float strata = v.StrataAmp > 0 ? v.StrataAmp * ReliefNoise.Band(s.strataScale, sigma) * ReliefNoise.Strata(p0, s.strataScale, 1e-3f) : 0;
            return p0 + (fine + strata) * v.Ng;
        }

        static (ReliefMesh m, ReliefOutput far, ReliefOutput near) BuildCliff(ReliefSettings s)
        {
            var m = Grid(0, 0, 60, 12, Cliff);
            TerrainRelief.Prepare(m, s);
            var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
            var cam = new ReliefPoint(new Vector3(30, Cliff(30, 30), 30), s.nearSpacing, s.nearSlope, true, float.MaxValue, s.nearMargin);
            var near = new ReliefOutput(); TerrainRelief.BuildNear(m, new[] { cam }, 1, s, near);
            return (m, far, near);
        }

        [Test]
        public void GpuVertex_RebuildsTheCpuPosition_AtItsOwnSpacing()
        {
            var s = ReliefSettings.Default;
            var (_, far, near) = BuildCliff(s);
            Assert.That(near.NearTriangles, Is.GreaterThan(0));
            float worst = 0; int withFine = 0, withStrata = 0;
            foreach (var o in new[] { far, near })
                for (int i = 0; i < o.VertexCount; i++)
                {
                    var v = o.Vertices[i];
                    worst = Math.Max(worst, Vector3.Distance(Gpu(v, v.Sigma, s), v.Position));
                    if (v.FineOffset != 0) withFine++;
                    if (v.StrataAmp > 0 && v.Sigma < s.farSpacing) withStrata++;
                }
            Assert.That(worst, Is.LessThan(2e-3f), "GPU position at sigma = CPU position");
            Assert.That(withFine, Is.GreaterThan(100), "the near cliff has fine detail");
            Assert.That(withStrata, Is.GreaterThan(100), "and strata");
        }

        [Test]
        public void FarMesh_HasNoFineDetail_AndTheMacroShapeIsTheSameAtEverySpacing()
        {
            var s = ReliefSettings.Default;
            var (m, far, _) = BuildCliff(s);
            for (int i = 0; i < far.VertexCount; i++)
            {
                Assert.That(far.Vertices[i].FineOffset, Is.EqualTo(0f), "far vertices carry no fine octaves");
                Assert.That(far.Vertices[i].Sigma, Is.EqualTo(s.farSpacing));
            }
            // the coarse point of one surface point does not depend on the spacing it is sampled with
            for (int t = 0; t < m.TriCount; t += 7)
            {
                var a = TerrainRelief.Evaluate(m, t, 0.3f, 0.3f, 0.4f, 0.4f, s, out _, out _);
                var b = TerrainRelief.Evaluate(m, t, 0.3f, 0.3f, 0.4f, 1.7f, s, out _, out _);
                var c = TerrainRelief.Evaluate(m, t, 0.3f, 0.3f, 0.4f, s.farSpacing, s, out _, out _);
                var pa = a.Position - a.FineOffset * a.Ng; var pb = b.Position - b.FineOffset * b.Ng; var pc = c.Position - c.FineOffset * c.Ng;
                Assert.That(Vector3.Distance(pa, pc), Is.LessThan(1e-4f), $"tri {t}: coarse point at 0.4 m vs far");
                Assert.That(Vector3.Distance(pb, pc), Is.LessThan(1e-4f), $"tri {t}: coarse point at 1.7 m vs far");
            }
        }

        [Test]
        public void LiveFade_IsContinuous_AndReachesTheCoarseSurfaceAtTheFarSpacing()
        {
            var s = ReliefSettings.Default;
            var (_, _, near) = BuildCliff(s);
            float maxStep = 0;
            for (int i = 0; i < near.VertexCount; i += 3)
            {
                var v = near.Vertices[i];
                var prev = Gpu(v, v.Sigma, s);
                for (float sg = v.Sigma; sg <= s.farSpacing; sg += 0.05f)
                {
                    var q = Gpu(v, sg, s);
                    maxStep = Math.Max(maxStep, Vector3.Distance(q, prev));
                    prev = q;
                }
                Assert.That(Vector3.Distance(Gpu(v, s.farSpacing, s), v.Position - v.FineOffset * v.Ng), Is.LessThan(1e-4f), "at the far spacing only the coarse surface");
            }
            Assert.That(maxStep, Is.LessThan(0.2f), "no jump while the camera walks away (5 cm of spacing per step)");
        }

        [Test]
        public void ChunkedNearBuild_EqualsOneBuild()
        {
            var s = ReliefSettings.Default;
            var m = Grid(0, 0, 60, 12, Cliff);
            TerrainRelief.Prepare(m, s);
            var pts = new[] { new ReliefPoint(new Vector3(30, Cliff(30, 30), 30), s.nearSpacing, s.nearSlope, true, float.MaxValue, s.nearMargin) };
            var one = new ReliefOutput(); TerrainRelief.BuildNear(m, pts, 1, s, one);
            var parts = new List<ReliefOutput>();
            for (int f = 0; f < m.TriCount; f += 37) { var o = new ReliefOutput(); TerrainRelief.BuildNear(m, pts, 1, s, o, f, Math.Min(m.TriCount, f + 37)); parts.Add(o); }
            var merged = TerrainRelief.Merge(m, parts);
            Assert.That(merged.VertexCount, Is.EqualTo(one.VertexCount));
            Assert.That(merged.NearTriangles, Is.EqualTo(one.NearTriangles));
            for (int i = 0; i < one.VertexCount; i++) Assert.That(merged.Vertices[i].Position, Is.EqualTo(one.Vertices[i].Position));
            Assert.That(merged.Indices[0], Is.EqualTo(one.Indices[0]));
            for (int t = 0; t < m.TriCount; t++) Assert.That(merged.Near[t], Is.EqualTo(one.Near[t]));
        }

        [Test]
        public void FineSlots_DefaultKnobs()
        {
            var s = ReliefSettings.Default;
            var l = Enumerable.Range(0, FineOctaves.Count).Select(k => TerrainRelief.FineLambda(k, s)).ToArray();
            Assert.That(l, Is.EqualTo(new[] { 6f, 3f, 1.5f, 7.5f, 4f, 2f, 1f }), "gullies 6/3/1.5, swells 7.5, lumps 4/2/1");
            Assert.That(TerrainRelief.FineSlot(0, 1, s), Is.EqualTo(-1), "the 12 m gullies are macro");
            Assert.That(TerrainRelief.FineSlot(0, 5, s), Is.EqualTo(-1), "0.75 m is never sampled finely enough");
            Assert.That(TerrainRelief.FineSlot(2, 0, s), Is.EqualTo(4));
        }

        [Test]
        public void NearMargin_KeepsTheCameraSpacingInsideIt()
        {
            var p = new ReliefPoint(Vector3.Zero, 0.4f, 0.05f, true, float.MaxValue, 8f);
            Assert.That(p.SpacingAt(5), Is.EqualTo(0.4f));
            Assert.That(p.SpacingAt(18), Is.EqualTo(0.9f).Within(1e-5f));
            Assert.That(p.Radius(4f), Is.EqualTo(8f + 72f).Within(1e-3f));   // margin 8 given explicitly
        }

        [Test]
        public void CellBorder_GetsASkirt_TheInsideDoesNot()
        {
            var s = ReliefSettings.Default;
            var m = Grid(0, 0, 60, 12, Cliff);
            TerrainRelief.Prepare(m, s);
            int border = 0;
            for (int e = 0; e < m.EdgeCount; e++) if (m.EdgeBorder[e]) border++;
            Assert.That(border, Is.EqualTo(12 * 4), "the 48 outline edges of a 12x12 grid");
            var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
            float lowest = float.MaxValue;
            for (int i = 0; i < far.VertexCount; i++) lowest = Math.Min(lowest, far.Vertices[i].Position.Y - Cliff(far.Vertices[i].Position.X, far.Vertices[i].Position.Z));
            Assert.That(lowest, Is.LessThan(-s.skirt + 0.1f), "skirt vertices hang below the surface");
            s.skirt = 0;
            var far0 = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far0);
            Assert.That(far0.VertexCount, Is.LessThan(far.VertexCount));
        }
    }
}
