using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Terrain relief (Docs/remaster-world.md section 13): watertight, collision-true where masked, small gap on walkable ground.</summary>
    public class TerrainReliefTests
    {
        /// <summary>A grid "cell" x0..x0+size, z0..z0+size with height h(x, z) and analytic normals; two submeshes (left half 0, right half 1).</summary>
        static ReliefMesh Grid(float x0, float z0, float size, int cells, Func<float, float, float> h, string mat0 = "G3_Nature_Ground_Grass_01_A", string mat1 = "G3_Nature_Myrtana_Rock_02_A")
        {
            int n = cells + 1;
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>();
            // vertices duplicated per submesh like G3's elements (split vertices at the material seam)
            var sub = new[] { new List<int>(), new List<int>() };
            var index = new Dictionary<(int, int, int), int>();
            int V(int i, int j, int sm)
            {
                if (index.TryGetValue((i, j, sm), out int v)) return v;
                float x = x0 + size * i / cells, z = z0 + size * j / cells;
                const float e = 0.01f;
                var nn = Vector3.Normalize(new Vector3(-(h(x + e, z) - h(x - e, z)) / (2 * e), 1, -(h(x, z + e) - h(x, z - e)) / (2 * e)));
                pos.Add(new Vector3(x, h(x, z), z)); nrm.Add(nn); uv.Add(new Vector2(x, z));
                return index[(i, j, sm)] = pos.Count - 1;
            }
            for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                int sm = i < cells / 2 ? 0 : 1;
                // clockwise seen from +y (Unity front face)
                sub[sm].AddRange(new[] { V(i, j, sm), V(i, j + 1, sm), V(i + 1, j, sm) });
                sub[sm].AddRange(new[] { V(i + 1, j, sm), V(i, j + 1, sm), V(i + 1, j + 1, sm) });
            }
            var m0 = ReliefMaterials.Mask(mat0); var m1 = ReliefMaterials.Mask(mat1);
            return new ReliefMesh
            {
                Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv0 = uv.ToArray(),
                Sub = new[] { sub[0].ToArray(), sub[1].ToArray() },
                SubAmp = new[] { m0.amp, m1.amp }, SubPhong = new[] { m0.phong, m1.phong },
            };
        }

        static float Hills(float x, float z) => 6f * (float)Math.Sin(x * 0.05f) * (float)Math.Cos(z * 0.04f) + 12f * (float)Math.Sin(x * 0.013f + z * 0.02f);
        static float Cliff(float x, float z) => 20f / (1f + (float)Math.Exp(-(x - 30f) * 0.6f)) + Hills(x, z) * 0.3f;

        /// <summary>Directed edges of the output by exact positions: interior edges come in both directions, open edges once.</summary>
        static void AddEdges(Dictionary<(Vector3, Vector3), int> edges, ReliefVertex[] v, IEnumerable<List<int>> lists)
        {
            foreach (var l in lists)
                for (int i = 0; i + 2 < l.Count; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var key = (v[l[i + k]].Position, v[l[i + (k + 1) % 3]].Position);
                        edges.TryGetValue(key, out int c);
                        edges[key] = c + 1;
                    }
        }

        /// <summary>Open edges of the union (each edge without its reverse), minus the outer border of the union's rectangle.</summary>
        static int InnerCracks(Dictionary<(Vector3, Vector3), int> edges, float xmin, float xmax, float zmin, float zmax)
        {
            int cracks = 0;
            foreach (var kv in edges)
            {
                var (a, b) = kv.Key;
                if (edges.ContainsKey((b, a))) continue;
                // the outer border itself is displaced too (border vertices keep their slope strength): 1.5 m band
                bool Near(float v, float e) => Math.Abs(v - e) < 1.5f;
                bool outer = (Near(a.X, xmin) && Near(b.X, xmin)) || (Near(a.X, xmax) && Near(b.X, xmax)) || (Near(a.Z, zmin) && Near(b.Z, zmin)) || (Near(a.Z, zmax) && Near(b.Z, zmax));
                if (!outer) cracks++;
            }
            return cracks;
        }

        static (ReliefOutput far, ReliefOutput near, List<int>[] combined) Build(ReliefMesh m, ReliefSettings s, ReliefPoint[] pts)
        {
            TerrainRelief.Prepare(m, s);
            var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
            var near = new ReliefOutput(); TerrainRelief.BuildNear(m, pts, pts.Length, s, near);
            var comb = new[] { new List<int>(), new List<int>() };
            TerrainRelief.Combine(m, far, near, comb);
            return (far, near, comb);
        }

        static ReliefVertex[] Concat(ReliefOutput far, ReliefOutput near)
        {
            var v = new ReliefVertex[far.VertexCount + near.VertexCount];
            Array.Copy(far.Vertices, v, far.VertexCount);
            Array.Copy(near.Vertices, 0, v, far.VertexCount, near.VertexCount);
            return v;
        }

        [Test]
        public void FarAndNearAreWatertightInsideAndAcrossTwoCells()
        {
            var s = ReliefSettings.Default;
            // the refine point sits on the shared border x = 60, so both cells have near and far parts touching it
            var pts = new[] { new ReliefPoint(new Vector3(200, Cliff(200, 30), 30), s.nearSpacing, s.nearSlope), new ReliefPoint(new Vector3(198, Cliff(198, 31), 31), 0.12f, 0.06f) };
            var a = Grid(0, 0, 200, 30, Cliff);
            var b = Grid(200, 0, 200, 30, Cliff);
            var (fa, na, ca) = Build(a, s, pts);
            var (fb, nb, cb) = Build(b, s, pts);
            Assert.That(na.NearTriangles, Is.GreaterThan(0));
            Assert.That(nb.NearTriangles, Is.GreaterThan(0));
            Assert.That(na.NearTriangles, Is.LessThan(a.TriCount), "part of cell a stays far");
            var edges = new Dictionary<(Vector3, Vector3), int>();
            AddEdges(edges, Concat(fa, na), ca);
            AddEdges(edges, Concat(fb, nb), cb);
            Assert.That(InnerCracks(edges, 0, 400, 0, 200), Is.EqualTo(0));
            // and the far meshes alone (no refine point)
            var edges2 = new Dictionary<(Vector3, Vector3), int>();
            AddEdges(edges2, fa.Vertices, fa.Indices);
            AddEdges(edges2, fb.Vertices, fb.Indices);
            Assert.That(InnerCracks(edges2, 0, 400, 0, 200), Is.EqualTo(0));
        }

        [Test]
        public void NoTriangleIsFlipped()
        {
            var s = ReliefSettings.Default;
            var m = Grid(0, 0, 60, 12, Hills);
            var pts = new[] { new ReliefPoint(new Vector3(20, Hills(20, 25), 25), 0.12f, 0.06f) };
            var (far, near, comb) = Build(m, s, pts);
            var v = Concat(far, near);
            int flipped = 0, total = 0;
            foreach (var l in comb)
                for (int i = 0; i < l.Count; i += 3)
                {
                    var p0 = v[l[i]].Position; var p1 = v[l[i + 1]].Position; var p2 = v[l[i + 2]].Position;
                    var c = Vector3.Cross(p1 - p0, p2 - p0);
                    if (c.Length() < 1e-9f) continue;
                    total++;
                    // the source grid's triangles all have cross.y > 0 (see Grid)
                    if (c.Y < 0) flipped++;
                }
            Assert.That(total, Is.GreaterThan(m.TriCount));
            Assert.That(flipped, Is.EqualTo(0));
        }

        [Test]
        public void SourceVerticesStayOnTheSurfaceAndMaskedGroundOnTheCollision()
        {
            var s = ReliefSettings.Default;
            s.lumps = 0; s.gully = 0; s.strata = 0; s.swell = 0;   // Phong only: the source vertices themselves never move
            var m = Grid(0, 0, 60, 12, Hills);
            TerrainRelief.Prepare(m, s);
            var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
            var set = new HashSet<Vector3>(Enumerable.Range(0, far.VertexCount).Select(i => far.Vertices[i].Position));
            foreach (var p in m.Pos) Assert.That(set.Contains(p), Is.True);

            // a road material: no Phong, no detail -> every output point lies on its source triangle (offset 0)
            var road = Grid(0, 0, 60, 12, Hills, "G3_Nature_Ground_Path_01_A", "G3_Architecture_Ground_City_04_A");
            var def = ReliefSettings.Default;
            TerrainRelief.Prepare(road, def);
            var o = new ReliefOutput(); TerrainRelief.BuildNear(road, new[] { new ReliefPoint(new Vector3(30, Hills(30, 30), 30), 0.3f, 0.03f) }, 1, def, o);
            Assert.That(o.NearTriangles, Is.GreaterThan(0));
            // (the cell border keeps the slope-only strength: the neighbour cell may have other materials)
            for (int i = 0; i < o.VertexCount; i++)
            {
                var q = o.Vertices[i].Position;
                if (q.X < 6 || q.X > 54 || q.Z < 6 || q.Z > 54) continue;
                Assert.That(Math.Abs(o.Vertices[i].Uv3.X), Is.LessThan(1e-3f));
            }
        }

        [Test]
        public void WalkableGapIsSmallAndSteepGetsRelief()
        {
            var s = ReliefSettings.Default;
            var gentle = Grid(0, 0, 120, 24, Hills);
            var (_, ng, _) = Build(gentle, s, new[] { new ReliefPoint(new Vector3(60, Hills(60, 60), 60), s.nearSpacing, s.nearSlope) });
            Assert.That(ng.CountWalk, Is.GreaterThan(1000));
            Assert.That(ng.MaxOffsetWalk, Is.LessThan(0.12f), "visual vs collision on walkable ground");
            Assert.That(ng.SumOffsetWalk / ng.CountWalk, Is.LessThan(0.03f));

            var cliff = Grid(0, 0, 60, 12, Cliff);
            var (_, nc, _) = Build(cliff, s, new[] { new ReliefPoint(new Vector3(30, Cliff(30, 30), 30), s.nearSpacing, s.nearSlope) });
            Assert.That(nc.MaxOffsetAll, Is.GreaterThan(0.15f), "the cliff gets visible relief");
            Assert.That(nc.MaxOffsetAll, Is.LessThan(6f));
        }

        [Test]
        public void NearRegionIsDenseAndFarIsCamerafree()
        {
            var s = ReliefSettings.Default;
            var m = Grid(0, 0, 240, 40, Hills);
            var p = new Vector3(120, Hills(120, 120), 120);
            var (far, near, _) = Build(m, s, new[] { new ReliefPoint(p, s.nearSpacing, s.nearSlope) });
            // spacing near the point: the closest output vertices are about nearSpacing apart
            var close = Enumerable.Range(0, near.VertexCount).Select(i => near.Vertices[i].Position)
                .Where(v => Vector2.Distance(new Vector2(v.X, v.Z), new Vector2(p.X, p.Z)) < 2).ToList();
            Assert.That(close.Count, Is.GreaterThan(40));
            // the far build does not depend on any point: built twice, same vertex count
            var far2 = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far2);
            Assert.That(far2.VertexCount, Is.EqualTo(far.VertexCount));
            // a point beyond the near radius changes nothing
            var o = new ReliefOutput();
            Assert.That(TerrainRelief.BuildNear(m, new[] { new ReliefPoint(new Vector3(5000, 0, 5000), s.nearSpacing, s.nearSlope) }, 1, s, o), Is.EqualTo(0));
        }

        [Test]
        public void BandLimitAndMasks()
        {
            Assert.That(ReliefNoise.Band(0.5f, 0.33f), Is.EqualTo(0));
            Assert.That(ReliefNoise.Band(4f, 0.33f), Is.EqualTo(1));
            Assert.That(ReliefMaterials.Mask("G3_Nature_Ground_Path_01_A").amp, Is.EqualTo(0));
            Assert.That(ReliefMaterials.Mask("G3_Varant_Dungeon_Wall_01").phong, Is.EqualTo(0));
            Assert.That(ReliefMaterials.Mask("G3_Nature_Myrtana_Rock_02_A").amp, Is.EqualTo(1));
            // gullies are about zero-mean
            double sum = 0; int n = 0;
            for (int i = 0; i < 400; i++)
            for (int j = 0; j < 40; j++)
            {
                sum += ReliefNoise.Gullies(new Vector2(i * 0.7f, j * 1.3f), new Vector2(0.6f, 0.3f), 16, 6, 0.3f); n++;
            }
            Assert.That(Math.Abs(sum / n), Is.LessThan(0.15));
        }
    }
}
