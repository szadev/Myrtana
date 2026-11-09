using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-52: the eye contract of FACES heads (RemasterEyes): eyeball and cornea face outwards, no skin inside an eyeball.
    /// The synthetic head mirrors eyegen.py v6's defect (inside-out sphere + dome) and mhface's leftovers (triangles inside).
    /// </summary>
    public class RemasterEyesTests
    {
        const string EyeMat = "G3_Remaster_Eye__G3_Test_Head_S1_eye";
        const double R = 0.014;
        static readonly double[] C = { 0.03, 1.83, 0.10 };

        sealed class Builder
        {
            public readonly List<float> Pos = new List<float>(), Nrm = new List<float>();
            public int V(double x, double y, double z, double nx, double ny, double nz)
            {
                Pos.AddRange(new[] { (float)x, (float)y, (float)z });
                Nrm.AddRange(new[] { (float)nx, (float)ny, (float)nz });
                return Pos.Count / 3 - 1;
            }
        }

        /// <summary>UV sphere (or a cap up to <paramref name="maxTheta"/>) around +z, outward or inside out (winding and normals).</summary>
        static List<int> Sphere(Builder b, double[] c, double r, bool outward, double maxTheta = Math.PI, int rings = 12, int segs = 16)
        {
            var idx = new List<int>();
            var grid = new int[rings + 1, segs];
            for (int i = 0; i <= rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    double th = maxTheta * i / rings, ph = 2 * Math.PI * j / segs;
                    double dx = Math.Sin(th) * Math.Cos(ph), dy = Math.Sin(th) * Math.Sin(ph), dz = Math.Cos(th);
                    double s = outward ? 1 : -1;
                    grid[i, j] = b.V(c[0] + dx * r, c[1] + dy * r, c[2] + dz * r, s * dx, s * dy, s * dz);
                }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int a = grid[i, j], b1 = grid[i + 1, j], c1 = grid[i + 1, (j + 1) % segs], d = grid[i, (j + 1) % segs];
                    // outward (from +z, theta increasing, phi counter-clockwise): (a, b1, c1) has its normal outwards
                    if (outward) idx.AddRange(new[] { a, b1, c1, a, c1, d });
                    else idx.AddRange(new[] { a, c1, b1, a, d, c1 });
                }
            return idx;
        }

        static double[] Off(double[] c, double dx, double dy, double dz) => new[] { c[0] + dx, c[1] + dy, c[2] + dz };

        /// <summary>A head with one eye as eyegen v6 built it plus skin around/inside it.</summary>
        static RigMesh Head(bool insideOut, out int skinInside, out int skinOutside)
        {
            var b = new Builder();
            var eye = Sphere(b, C, R, !insideOut);
            var cornea = Sphere(b, Off(C, 0, 0, R * 0.3), R * 0.75, !insideOut, maxTheta: 0.7, rings: 4);
            var caruncle = Sphere(b, Off(C, -R * 0.8, 0, R * 0.45), 0.0018, true, rings: 4, segs: 6);
            var skin = new List<int>();
            // lid sheet in front of the eyeball (outside), facing +z
            int l0 = b.V(C[0] - 0.02, C[1] + 0.005, C[2] + R + 0.002, 0, 0, 1), l1 = b.V(C[0] + 0.02, C[1] + 0.005, C[2] + R + 0.002, 0, 0, 1),
                l2 = b.V(C[0], C[1] + 0.02, C[2] + R + 0.002, 0, 0, 1);
            skin.AddRange(new[] { l0, l1, l2 });
            skinOutside = 1;
            // G3 eye cap / MH pocket remains: small triangles deep inside the eyeball
            skinInside = 0;
            for (int k = 0; k < 5; k++)
            {
                double z = C[2] + R * (0.6 - 0.2 * k);
                int a = b.V(C[0] - 0.003, C[1], z, 0, 0, 1), c1 = b.V(C[0] + 0.003, C[1], z, 0, 0, 1), d = b.V(C[0], C[1] + 0.003, z, 0, 0, 1);
                skin.AddRange(new[] { a, c1, d });
                skinInside++;
            }
            var m = new RigMesh { Name = "G3_Test_Head", Positions = b.Pos.ToArray(), Normals = b.Nrm.ToArray() };
            m.SubMeshes.Add(new RigSubMesh { Material = "G3_Test_Head_S1", Indices = skin.ToArray() });
            m.SubMeshes.Add(new RigSubMesh { Material = EyeMat, Indices = eye.Concat(caruncle).ToArray() });
            m.SubMeshes.Add(new RigSubMesh { Material = RemasterGenerated.Cornea, Indices = cornea.ToArray() });
            return m;
        }

        [Test]
        public void FitsTheEyeballSphere()
        {
            var m = Head(true, out _, out _);
            var eyes = RemasterEyes.Eyeballs(m);
            Assert.That(eyes.Count, Is.EqualTo(1), "the caruncle is too small to be an eyeball");
            Assert.That(eyes[0].R, Is.EqualTo(R).Within(1e-5));
            Assert.That(eyes[0].Distance(C[0], C[1], C[2]), Is.LessThan(1e-5));
        }

        [Test]
        public void InsideOutEyesFailTheContract()
        {
            var m = Head(true, out int inside, out _);
            var r = RemasterEyes.Check(m);
            Assert.That(r.HasEyes, Is.True);
            Assert.That(r.Passed, Is.False);
            Assert.That(r.InsideOutParts, Is.EqualTo(2), "eyeball and cornea dome; the caruncle faces out");
            Assert.That(r.InwardNormals, Is.EqualTo(0), "eyegen's normals followed its winding");
            Assert.That(r.SkinInside, Is.EqualTo(inside));
        }

        [Test]
        public void RepairTurnsTheEyesAndDropsTheSkinInside()
        {
            var m = Head(true, out int inside, out int outside);
            var cornerBefore = m.SubMeshes[1].Indices.Take(3).ToArray();
            var fix = RemasterEyes.Repair(m);
            Assert.That(fix.InsideOutParts, Is.EqualTo(2));
            Assert.That(fix.DroppedTriangles, Is.EqualTo(inside));
            Assert.That(fix.FlippedNormals, Is.GreaterThan(0), "normals follow the turned faces");
            Assert.That(m.SubMeshes[0].Indices.Length, Is.EqualTo(outside * 3), "the lid in front of the eyeball stays");
            Assert.That(m.SubMeshes[1].Indices.Take(3), Is.EqualTo(new[] { cornerBefore[0], cornerBefore[2], cornerBefore[1] }));
            var after = RemasterEyes.Check(m);
            Assert.That(after.Passed, Is.True, after.ToString());
            Assert.That(RemasterEyes.Repair(m).Changed, Is.False, "idempotent");
        }

        [Test]
        public void InwardNormalsAloneAreFlipped()
        {
            var m = Head(false, out _, out _);
            // outward winding, normals of the eye material pointing in (E2 as measured in game: dot -0.96)
            foreach (int v in m.SubMeshes[1].Indices.Distinct())
                for (int k = 0; k < 3; k++) m.Normals[v * 3 + k] = -m.Normals[v * 3 + k];
            Assert.That(RemasterEyes.Check(m).InwardNormals, Is.GreaterThan(0));
            var fix = RemasterEyes.Repair(m);
            Assert.That(fix.InsideOutParts, Is.EqualTo(0));
            Assert.That(fix.FlippedNormals, Is.EqualTo(m.SubMeshes[1].Indices.Distinct().Count()));
            Assert.That(RemasterEyes.Check(m).Passed, Is.True);
        }

        [Test]
        public void MeshesWithoutGeneratedEyesAreLeftAlone()
        {
            var m = Head(true, out _, out _);
            m.SubMeshes[1].Material = "G3_Animation_Head_Eyes_01";
            m.SubMeshes[2].Material = "G3_Animation_Head_Eyes_01";
            var before = m.SubMeshes.Select(s => s.Indices.ToArray()).ToList();
            Assert.That(RemasterEyes.Check(m).HasEyes, Is.False);
            Assert.That(RemasterEyes.Repair(m).Changed, Is.False);
            Assert.That(m.SubMeshes.Select(s => s.Indices), Is.EqualTo(before), "G3 originals (LOD1/LOD2) untouched");
        }

        [Test]
        public void RigCheckReportsTheEyes()
        {
            var original = new RigModel();
            original.AddNode("Root", -1);
            var cand = new RigModel();
            cand.AddNode("Root", -1);
            cand.Meshes.Add(Head(true, out _, out _));
            var report = RigCheck.Compare(original, cand);
            var line = report.Lines.FirstOrDefault(l => l.Check.EndsWith(" eyes", StringComparison.Ordinal));
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Level, Is.EqualTo(RigCheck.Level.Fail));
        }

        /// <summary>The shipped FACES heads (editor-runner-30): every one is repaired to the contract (data needed).</summary>
        [Test]
        public void ShippedHeadsAreRepaired()
        {
            string dir = Environment.GetEnvironmentVariable(RemasterPaths.GameDataVariable);
            dir = string.IsNullOrEmpty(dir) ? "GameData" : dir;
            dir = Path.Combine(dir, "Remaster", "Characters");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "G3_Head_*.glb") : new string[0];
            if (files.Length == 0) Assert.Ignore("no FACES heads in " + dir);
            int heads = 0;
            foreach (var f in files)
            {
                var model = Glb.Read(f);
                model.MirrorX();
                foreach (var mesh in model.Meshes.Where(RemasterEyes.HasGeneratedEyes))
                {
                    heads++;
                    var fix = RemasterEyes.Repair(mesh);
                    var check = RemasterEyes.Check(mesh);
                    Assert.That(check.Passed, Is.True, $"{Path.GetFileName(f)}: {fix} -> {check}");
                    Assert.That(check.Eyeballs.Count, Is.EqualTo(2), Path.GetFileName(f));
                    TestContext.Out.WriteLine($"{Path.GetFileName(f)}: {fix}");
                }
            }
            Assert.That(heads, Is.GreaterThan(0));
        }
    }
}
