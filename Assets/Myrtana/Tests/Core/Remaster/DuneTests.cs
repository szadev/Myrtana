using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// dune-1 (Docs/remaster-world.md section 23): the dune bake keeps the ground exactly where gameplay needs it, the field
    /// round-trips, the relief with dunes stays watertight, and what stands on the ground (the ground map) matches the drawn mesh.
    /// </summary>
    public class DuneTests
    {
        // transverse dunes across the wind (azimuth 60: wind towards +x+z), 50 m apart, 3 m high, rounded crests
        static float Dunes(float x, float z)
        {
            double az = 60 * Math.PI / 180;
            float u = x * (float)Math.Sin(az) + z * (float)Math.Cos(az);
            return 3f * (float)Math.Pow(0.5 + 0.5 * Math.Sin(u * 2 * Math.PI / 50), 1.5) + 0.01f * x;
        }

        static DuneShape.Grid HeightGrid(int size, Func<float, float, float> h)
        {
            int n = size + 1;
            var g = new DuneShape.Grid { Nx = n, Nz = n, X0 = 0, Z0 = 0, Res = 1, H = new float[n * n], S = new float[n * n] };
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++) { g.H[j * n + i] = h(i, j); g.S[j * n + i] = 1; }
            return g;
        }

        static bool[] Cells(int n, Func<int, int, bool> f)
        {
            var c = new bool[n * n];
            for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) c[j * n + i] = f(i, j);
            return c;
        }

        static (DuneField field, List<DuneProtect> protect, DuneShape.Grid g) Bake(int size = 160)
        {
            var g = HeightGrid(size, Dunes);
            int n = size + 1;
            var protect = new List<DuneProtect>
            {
                new DuneProtect("path", Cells(n, (i, j) => i >= 72 && i <= 88), 4, 14),
                new DuneProtect("building", Cells(n, (i, j) => i >= 30 && i <= 36 && j >= 110 && j <= 118), 3, 12),
                new DuneProtect("npc", Cells(n, (i, j) => i == 125 && j == 40), 2.5f, 8),
            };
            var f = DuneShape.Build(g, protect, new DuneBakeSettings(), out _);
            return (f, protect, g);
        }

        [Test]
        public void SandWeightFollowsG3Layers()
        {
            Assert.That(DuneShape.SandWeight("G3_Desert_Ground_01_A", 0.7f), Is.EqualTo(1));
            Assert.That(DuneShape.SandWeight("G3_Desert_PlainSand_To_Rock_01_A", 0.2f), Is.EqualTo(0.8f).Within(1e-6));
            Assert.That(DuneShape.SandWeight("G3_Desert_Plainsand_To_Wetsand_01_A", 1f), Is.EqualTo(0));
            Assert.That(DuneShape.SandWeight("G3_Desert_PlainSand_to_Path_01_A", 0f), Is.EqualTo(0), "paths never");
            Assert.That(DuneShape.SandWeight("G3_Nature_Ground_Grass_01_A", 0f), Is.EqualTo(0));
        }

        [Test]
        public void DisplacementIsExactlyZeroNearObjectsPathsAndNpcs()
        {
            var (f, protect, g) = Bake();
            int n = g.Nx, inside = 0, moved = 0;
            foreach (var p in protect)
            {
                var d = DuneShape.Distance(p.Cells, n, n);
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        // a world point is zero when the grid points round it are within the margin (bilinear footprint: -1 m)
                        if (d[j * n + i] > p.Zero - 1.5f) continue;
                        inside++;
                        for (int q = 0; q < 4; q++)
                        {
                            float x = i + 0.37f * (q & 1), z = j + 0.61f * (q >> 1);
                            if (f.Sample(x, z, out var s) && (s.W > 0 || s.C != 0 || s.B != 0)) moved++;
                        }
                    }
            }
            Assert.That(inside, Is.GreaterThan(100));
            Assert.That(moved, Is.EqualTo(0), "the ground under paths, buildings and NPC spots keeps G3's height");
            // and the dunes do change open sand, within the cap
            float maxUp = 0, maxAbs = 0; int weighted = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    if (f.Sample(i, j, out var s)) { weighted++; maxUp = Math.Max(maxUp, s.C + s.B); maxAbs = Math.Max(maxAbs, Math.Abs(s.C + s.B)); }
            Assert.That(weighted, Is.GreaterThan(n * n / 3));
            Assert.That(maxUp, Is.GreaterThan(0.15f), "crests are raised");
            Assert.That(maxAbs, Is.LessThanOrEqualTo(DuneShape.MaxChange + 1e-3f));
        }

        [Test]
        public void CrestsAreSharpenedTroughsKept()
        {
            var (f, _, g) = Bake();
            // along a wind line in open sand: the change at G3's crests vs its troughs
            double az = 60 * Math.PI / 180;
            float wx = (float)Math.Sin(az), wz = (float)Math.Cos(az);
            float crest = 0, trough = 0;
            for (float t = 0; t < 60; t += 0.5f)
            {
                float x = 100 + t * wx, z = 60 + t * wz;
                f.Sample(x, z, out var s);
                float h = Dunes(x, z);
                if (h > 2.5f) crest = Math.Max(crest, s.C + s.B);
                if (h < 0.3f + 0.01f * x) trough = Math.Max(trough, Math.Abs(s.C));
            }
            Assert.That(crest, Is.GreaterThan(0.1f));
            Assert.That(trough, Is.LessThan(crest));
        }

        [Test]
        public void FieldRoundTrips()
        {
            var (f, _, _) = Bake(96);
            var ms = new MemoryStream(); f.Write(ms);
            var b = DuneField.Read(ms.ToArray());
            Assert.That(b.TileCount, Is.EqualTo(f.TileCount));
            var rnd = new Random(4);
            for (int k = 0; k < 2000; k++)
            {
                float x = (float)rnd.NextDouble() * 96, z = (float)rnd.NextDouble() * 96;
                Assert.That(b.Sample(x, z, out var s2), Is.EqualTo(f.Sample(x, z, out var s1)));
                Assert.That(s2.C, Is.EqualTo(s1.C)); Assert.That(s2.B, Is.EqualTo(s1.B)); Assert.That(s2.W, Is.EqualTo(s1.W)); Assert.That(s2.Gx, Is.EqualTo(s1.Gx));
            }
        }

        // ------------------------------------------------------------------ the relief with dunes

        static ReliefMesh Mesh(float x0, float size, int cells, Func<float, float, float> h)
        {
            int n = cells + 1;
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x = x0 + size * i / cells, z = size * j / cells;
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
            var mask = ReliefMaterials.Mask("G3_Desert_Ground_01_A");
            return new ReliefMesh { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv0 = uv.ToArray(), Sub = new[] { tri.ToArray() }, SubAmp = new[] { mask.amp }, SubPhong = new[] { mask.phong } };
        }

        static ReliefShared Facts(IEnumerable<ReliefMesh> all, ReliefMesh m, ReliefSettings s)
        {
            var f = new ReliefFacts(s.walkNy);
            foreach (var x in all) { TerrainRelief.Weld(x); f.Add(x); }
            return f.For(m, _ => 255);
        }

        static ReliefSettings WithField(DuneField f)
        {
            var s = ReliefSettings.Default.Clamped();
            s.Dunes = f;
            return s;
        }

        [Test]
        public void ReliefWithDunesIsWatertightAcrossCells()
        {
            var (f, _, _) = Bake();
            var s = WithField(f);
            var a = Mesh(0, 80, 16, Dunes); var b = Mesh(80, 80, 16, Dunes);
            var fa = Facts(new[] { a, b }, a, s); var fb = Facts(new[] { a, b }, b, s);
            TerrainRelief.Prepare(a, s, fa); TerrainRelief.Prepare(b, s, fb);
            Assert.That(a.WDune, Is.Not.Null, "the cell gets dunes");
            var cam = new[] { new ReliefPoint(new Vector3(80, Dunes(80, 40) + 2, 40), s.nearSpacing, s.nearSlope) };
            var edges = new Dictionary<(Vector3, Vector3), int>();
            foreach (var m in new[] { a, b })
            {
                var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
                var near = new ReliefOutput(); TerrainRelief.BuildNear(m, cam, 1, s, near);
                var comb = new[] { new List<int>() };
                TerrainRelief.Combine(m, far, near, comb);
                var v = new ReliefVertex[far.VertexCount + near.VertexCount];
                Array.Copy(far.Vertices, v, far.VertexCount); Array.Copy(near.Vertices, 0, v, far.VertexCount, near.VertexCount);
                foreach (var l in comb)
                    for (int i = 0; i + 2 < l.Count; i += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            var key = (v[l[i + k]].Position, v[l[i + (k + 1) % 3]].Position);
                            edges.TryGetValue(key, out int c); edges[key] = c + 1;
                        }
            }
            int cracks = 0;
            foreach (var kv in edges)
            {
                var (p, q) = kv.Key;
                if (edges.ContainsKey((q, p))) continue;
                bool Near(float v, float e) => Math.Abs(v - e) < 1.6f;
                bool outer = (Near(p.X, 0) && Near(q.X, 0)) || (Near(p.X, 160) && Near(q.X, 160)) || (Near(p.Z, 0) && Near(q.Z, 0)) || (Near(p.Z, 80) && Near(q.Z, 80));
                if (!outer) cracks++;
            }
            Assert.That(cracks, Is.EqualTo(0));
        }

        [Test]
        public void GroundUnderObjectsStaysAndTheMapMatchesTheDrawnDunes()
        {
            var (f, protect, _) = Bake();
            var s = WithField(f);
            var off = ReliefSettings.Default.Clamped();
            var m = Mesh(0, 160, 32, Dunes); var m0 = Mesh(0, 160, 32, Dunes);
            TerrainRelief.Prepare(m, s, Facts(new[] { m }, m, s));
            TerrainRelief.Prepare(m0, off, Facts(new[] { m0 }, m0, off));
            // under the path / building / NPC zero margins the drawn ground is the relief without dunes, bit for bit
            int checkedPts = 0;
            var dPath = DuneShape.Distance(protect[0].Cells, 161, 161);
            for (int t = 0; t < m.TriCount; t++)
            {
                // a triangle whose three corners are inside the path's zero margin (4 m; -1.5 m for the bilinear footprint)
                bool inside = true;
                for (int c = 0; c < 3; c++)
                {
                    var p = m.Pos[m.TriV[t * 3 + c]];
                    int gi = (int)Math.Round(p.X), gj = (int)Math.Round(p.Z);
                    if (gi < 0 || gj < 0 || gi > 160 || gj > 160 || dPath[gj * 161 + gi] > 2.5f) inside = false;
                }
                if (!inside) continue;
                for (int q = 0; q < 3; q++)
                {
                    float b0 = q == 0 ? 1 : q == 1 ? 0.2f : 0.5f, b1 = q == 0 ? 0 : q == 1 ? 0.3f : 0.5f, b2 = q == 1 ? 0.5f : 0;
                    var pa = TerrainRelief.SurfacePoint(m, t, b0, b1, b2, 1f, s);
                    var pb = TerrainRelief.SurfacePoint(m0, t, b0, b1, b2, 1f, off);
                    Assert.That(Vector3.Distance(pa, pb), Is.LessThan(1e-4f), "dunes never move the path");
                    checkedPts++;
                }
            }
            Assert.That(checkedPts, Is.GreaterThan(10));

            // the ground map (what characters, items and corpses stand on) vs the drawn surface: feet within 3 cm
            var cam = new[] { new ReliefPoint(new Vector3(110, Dunes(110, 80) + 2, 80), s.nearSpacing, s.nearSlope) };
            var far = new ReliefOutput(); TerrainRelief.BuildFar(m, s, far);
            var near = new ReliefOutput(); TerrainRelief.BuildNear(m, cam, 1, s, near);
            var map = new ReliefGroundMap(256, 0.3125f);
            map.Begin(110, 80);
            ReliefMapRaster.Collision(map, m);
            ReliefMapRaster.Visual(map, m, far, near);
            map.Ready = true;
            var rnd = new Random(5);
            var err = new List<float>(); float maxDelta = 0;
            for (int k = 0; k < 600; k++)
            {
                float x = 95 + (float)rnd.NextDouble() * 30, z = 65 + (float)rnd.NextDouble() * 30;
                if (!map.Sample(x, z, out float col, out float vis)) continue;
                if (!ReliefGround.SurfaceAt(m, x, z, col, 2f, cam, 1, near.Near, s, out _, out float av)) continue;
                err.Add(Math.Abs(av - vis));
                maxDelta = Math.Max(maxDelta, Math.Abs(vis - col));
            }
            err.Sort();
            Assert.That(err.Count, Is.GreaterThan(400));
            Assert.That(err[(int)(err.Count * 0.99)], Is.LessThan(0.03f), "p99 feet vs drawn dunes");
            Assert.That(maxDelta, Is.GreaterThan(0.05f), "the dunes are in the map");
        }

        [Test]
        public void DunesOffIsTheReliefWithoutThem()
        {
            var (f, _, _) = Bake(96);
            var on = WithField(f); on.dunes = false;
            var none = ReliefSettings.Default.Clamped();
            var a = Mesh(0, 96, 12, Dunes); var b = Mesh(0, 96, 12, Dunes);
            TerrainRelief.Prepare(a, on, Facts(new[] { a }, a, on)); TerrainRelief.Prepare(b, none, Facts(new[] { b }, b, none));
            var oa = new ReliefOutput(); TerrainRelief.BuildFar(a, on, oa);
            var ob = new ReliefOutput(); TerrainRelief.BuildFar(b, none, ob);
            Assert.That(oa.VertexCount, Is.EqualTo(ob.VertexCount));
            for (int i = 0; i < oa.VertexCount; i++) Assert.That(oa.Vertices[i].Position, Is.EqualTo(ob.Vertices[i].Position));
        }
    }
}
