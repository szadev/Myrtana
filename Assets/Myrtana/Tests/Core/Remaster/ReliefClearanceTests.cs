using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-174: the relief never moves G3's open terrain edges and stays within its share of the room to other geometry.</summary>
    public class ReliefClearanceTests
    {
        /// <summary>A flat cliff-steep sheet: grid in x/y at z = <paramref name="z"/> (facing -z), unit spacing <paramref name="step"/>; optional hole cells.</summary>
        static ReliefMesh Wall(int cells, float step, float z, HashSet<(int, int)> holes = null)
        {
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>();
            var idx = new Dictionary<(int, int), int>();
            int V(int i, int j)
            {
                if (idx.TryGetValue((i, j), out int v)) return v;
                pos.Add(new Vector3(i * step, j * step, z)); nrm.Add(new Vector3(0, 0, -1)); uv.Add(new Vector2(i, j));
                return idx[(i, j)] = pos.Count - 1;
            }
            var tris = new List<int>();
            for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                if (holes != null && holes.Contains((i, j))) continue;
                tris.AddRange(new[] { V(i, j), V(i, j + 1), V(i + 1, j) });
                tris.AddRange(new[] { V(i + 1, j), V(i, j + 1), V(i + 1, j + 1) });
            }
            return new ReliefMesh { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv0 = uv.ToArray(), Sub = new[] { tris.ToArray() }, SubAmp = new[] { 1f }, SubPhong = new[] { 1f } };
        }

        static ReliefClearance Grid(params ReliefMesh[] meshes)
        {
            var c = new ReliefClearance(2f);
            foreach (var m in meshes)
                for (int i = 0; i + 2 < m.Sub[0].Length; i += 3)
                    c.Add(m.Pos[m.Sub[0][i]], m.Pos[m.Sub[0][i + 1]], m.Pos[m.Sub[0][i + 2]], true);
            return c;
        }

        [Test]
        public void ThinWall_ClearanceIsTheThickness()
        {
            var front = Wall(10, 2, 0); var back = Wall(10, 2, 0.6f);
            var c = Grid(front, back);
            float d = c.AlongNormal(new Vector3(10, 10, 0), new Vector3(0, 0, -1), 8);
            Assert.That(d, Is.EqualTo(0.6f).Within(1e-3f), "the ray backwards meets the back sheet");
            Assert.That(Grid(front).AlongNormal(new Vector3(10, 10, 0), new Vector3(0, 0, -1), 8), Is.EqualTo(float.PositiveInfinity), "a lone sheet is free");
        }

        [Test]
        public void OpenEdges_AreTheHoleRimAndTheBorder_NotDuplicatesOrInterior()
        {
            var m = Wall(10, 2, 0, new HashSet<(int, int)> { (4, 4), (5, 4) });
            var dup = Wall(10, 2, 0);   // an overlapping cell with the same triangles (and none missing) closes nothing twice
            var c = Grid(m);
            var open = c.OpenPositions();
            Assert.That(open.Contains(new Vector3(8, 8, 0)), Is.True, "hole corner");
            Assert.That(open.Contains(new Vector3(12, 10, 0)), Is.True, "hole rim");
            Assert.That(open.Contains(new Vector3(0, 0, 0)), Is.True, "outer border");
            Assert.That(open.Contains(new Vector3(4, 4, 0)), Is.False, "interior");
            var c2 = Grid(dup, Wall(10, 2, 0));
            Assert.That(c2.OpenPositions().Contains(new Vector3(4, 4, 0)), Is.False, "identical overlapping triangles are one");
        }

        [Test]
        public void NearestMesh_SeesOnlyPlacedMeshes()
        {
            var c = new ReliefClearance(2f);
            c.Add(new Vector3(0, 0, 0), new Vector3(0, 0, 10), new Vector3(10, 0, 0), true);
            c.Add(new Vector3(3, 1, 3), new Vector3(3, 3, 3), new Vector3(5, 1, 3), false);
            Assert.That(c.NearestMesh(new Vector3(3, 0, 3), 8), Is.EqualTo(1).Within(1e-4f));
            Assert.That(c.NearestMesh(new Vector3(30, 0, 30), 8), Is.EqualTo(float.PositiveInfinity));
        }

        [Test]
        public void Encode_RoundsDown_AndSaturates()
        {
            Assert.That(ReliefClearance.Decode(ReliefClearance.Encode(1.0f)), Is.LessThanOrEqualTo(1.0f).And.GreaterThan(0.95f));
            Assert.That(ReliefClearance.Encode(100f), Is.EqualTo(ReliefClearance.NoLimit));
            Assert.That(ReliefClearance.Decode(ReliefClearance.NoLimit), Is.EqualTo(float.PositiveInfinity));
        }

        static float MaxBulge(ReliefMesh m, int w)
        {
            float b = 0;
            for (int e = 0; e < m.EdgeCount; e++)
                if (m.EdgeA[e] == w || m.EdgeB[e] == w) b = Math.Max(b, 0.25f * m.EdgeTerm[e].Length());
            return b;
        }

        static ReliefMesh Bumpy()
        {
            // a steep wall with bent normals (Phong bulges) round a hole
            var m = Wall(12, 3, 0, new HashSet<(int, int)> { (6, 6) });
            for (int i = 0; i < m.Nrm.Length; i++)
                m.Nrm[i] = Vector3.Normalize(new Vector3((float)Math.Sin(m.Pos[i].X * 0.4f) * 0.5f, (float)Math.Cos(m.Pos[i].Y * 0.3f) * 0.5f, -1));
            return m;
        }

        [Test]
        public void Prepare_PinnedPositionsDoNotMove_AndTheRoomCapsTheRest()
        {
            var s = ReliefSettings.Default;
            var m = Bumpy();
            TerrainRelief.Weld(m);
            var facts = ReliefFacts.Local(m, s);
            // the facts as G3Cli writes them: hole rim pinned, a thin wall behind the left half (0.8 m)
            var c = Grid(m);
            var open = c.OpenPositions();
            for (int w = 0; w < m.WeldCount; w++)
            {
                if (open.Contains(m.WPos[w]) && m.WPos[w].X > 1 && m.WPos[w].X < 35 && m.WPos[w].Y > 1 && m.WPos[w].Y < 35) facts.Flags[w] |= ReliefFacts.Pin;
                facts.Clear[w] = m.WPos[w].X < 15 ? ReliefClearance.Encode(0.8f) : ReliefClearance.NoLimit;
            }
            TerrainRelief.Prepare(m, s, facts);
            int pinned = 0, capped = 0;
            for (int w = 0; w < m.WeldCount; w++)
            {
                if ((facts.Flags[w] & ReliefFacts.Pin) != 0)
                {
                    pinned++;
                    Assert.That(m.WAmp[w], Is.EqualTo(0), "pinned: no detail");
                    Assert.That(m.WSwell[w], Is.EqualTo(0), "pinned: no swell");
                    Assert.That(MaxBulge(m, w), Is.EqualTo(0), "pinned: no Phong on its edges");
                }
                else if (m.WPos[w].X < 15)
                {
                    capped++;
                    float room = s.clearShare * ReliefClearance.Decode(facts.Clear[w]);
                    Assert.That(m.WAmp[w] + m.WSwell[w], Is.LessThanOrEqualTo(room + 1e-5f));
                    Assert.That(MaxBulge(m, w), Is.LessThanOrEqualTo(room + 1e-5f));
                }
            }
            Assert.That(pinned, Is.EqualTo(4), "the hole's four corners");
            Assert.That(capped, Is.GreaterThan(10));
            // the free right part keeps the full steep amplitude
            int far = Enumerable.Range(0, m.WeldCount).First(w => m.WPos[w].X > 25 && m.WPos[w].Y > 10 && m.WPos[w].Y < 25);
            Assert.That(m.WAmp[far], Is.GreaterThan(1f));
        }

        [Test]
        public void PinnedRim_RelievedMeshKeepsTheHoleExactlyWhereG3HasIt()
        {
            var s = ReliefSettings.Default;
            var m = Bumpy();
            TerrainRelief.Weld(m);
            var facts = ReliefFacts.Local(m, s);
            var open = Grid(m).OpenPositions();
            for (int w = 0; w < m.WeldCount; w++) if (open.Contains(m.WPos[w])) facts.Flags[w] |= ReliefFacts.Pin;
            TerrainRelief.Prepare(m, s, facts);
            var o = new ReliefOutput();
            TerrainRelief.BuildFar(m, s, o);
            // every output vertex on the hole's rim (x in 18..21, y in 18..21, on the rim lines) lies on the original plane z = 0
            int rim = 0;
            for (int i = 0; i < o.VertexCount; i++)
            {
                var p = o.Vertices[i].Position;
                bool onRim = (Math.Abs(p.X - 18) < 1e-3f || Math.Abs(p.X - 21) < 1e-3f) && p.Y >= 18 - 1e-3f && p.Y <= 21 + 1e-3f ||
                             (Math.Abs(p.Y - 18) < 1e-3f || Math.Abs(p.Y - 21) < 1e-3f) && p.X >= 18 - 1e-3f && p.X <= 21 + 1e-3f;
                if (!onRim) continue;
                rim++;
                Assert.That(Math.Abs(p.Z), Is.LessThan(1e-4f), $"rim point {p} moved off G3's plane");
            }
            Assert.That(rim, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void FactsTable_RoundTripsTheClearance()
        {
            var s = ReliefSettings.Default;
            var m = Wall(4, 2, 0);
            TerrainRelief.Weld(m);
            var f = ReliefFacts.Local(m, s);
            for (int w = 0; w < f.Count; w++) { f.Clear[w] = (byte)(w * 7 % 256); if (w % 3 == 0) f.Flags[w] |= ReliefFacts.Pin; }
            var ms = new MemoryStream();
            ReliefFacts.Write(ms, new[] { ("cell", f) });
            var bytes = ms.ToArray();
            var (o, n) = ReliefFacts.Index(bytes)["cell"];
            var d = ReliefFacts.Decode(bytes, o, n);
            Assert.That(d.Clear, Is.EqualTo(f.Clear));
            Assert.That(d.Flags, Is.EqualTo(f.Flags));
        }
    }
}
