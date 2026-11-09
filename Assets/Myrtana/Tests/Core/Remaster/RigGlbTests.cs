using System;
using System.Linq;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The remaster exchange format (.glb) and the contract check (Docs/remaster-pipeline.md).</summary>
    public class RigGlbTests
    {
        static RigModel RoundTrip(RigModel m) => Glb.Read(Glb.Write(m));

        [Test]
        public void TestRigRoundTripKeepsNodesSkinAndMaterials()
        {
            var a = TestRig.Build();
            var b = RoundTrip(a);
            Assert.That(b.Nodes.Select(n => n.Name).Take(a.Nodes.Count), Is.EqualTo(a.Nodes.Select(n => n.Name)));
            for (int i = 0; i < a.Nodes.Count; i++)
            {
                Assert.That(b.Nodes[i].Parent, Is.EqualTo(a.Nodes[i].Parent), a.Nodes[i].Name);
                Assert.That(M4.TranslationDelta(a.World(i), b.World(i)), Is.LessThan(1e-6), a.Nodes[i].Name);
            }
            Assert.That(b.Meshes.Count, Is.EqualTo(2));
            var body = b.Meshes.First(m => m.IsSkinned);
            Assert.That(body.VertexCount, Is.EqualTo(a.Meshes[0].VertexCount));
            Assert.That(body.SubMeshes.Select(s => s.Material), Is.EqualTo(new[] { TestRig.BodyMaterial, TestRig.SkinMaterial }));
            Assert.That(body.Uv0[1], Is.EqualTo(a.Meshes[0].Uv0[1]).Within(1e-6));
            var hair = b.Meshes.First(m => !m.IsSkinned);
            Assert.That(b.Nodes[hair.Node].Parent, Is.EqualTo(b.FindNode("Slot_Hair")), "static mesh hangs on its slot");
            var report = RigCheck.Compare(a, b);
            Assert.That(report.Passed, report.ToText());
        }

        [Test]
        public void MirrorTwiceIsIdentityAndKeepsCounterClockwiseFrontFaces()
        {
            var a = TestRig.Build();
            var b = TestRig.Build();
            b.MirrorX(); b.MirrorX();
            Assert.That(b.Meshes[0].Positions, Is.EqualTo(a.Meshes[0].Positions));
            Assert.That(b.Meshes[0].SubMeshes[0].Indices, Is.EqualTo(a.Meshes[0].SubMeshes[0].Indices));
            var winding = RigCheck.Compare(a, a).Lines.Where(l => l.Check.EndsWith("winding")).ToList();
            Assert.That(winding, Is.Not.Empty);
            Assert.That(winding.All(l => l.Level == RigCheck.Level.Pass), string.Join("\n", winding));
        }

        [Test]
        public void MovedBoneFailsBindPose()
        {
            var a = TestRig.Build();
            var b = RoundTrip(a);
            b.Nodes[b.FindNode("Hero_R_Arm_Forearm")].T[1] += 0.0005; // 0.5 mm
            var r = RigCheck.Compare(a, b);
            Assert.That(r.Passed, Is.False);
            Assert.That(r.Lines.Single(l => l.Check == "bind pose position").Level, Is.EqualTo(RigCheck.Level.Fail));
        }

        [Test]
        public void ReorientedBoneFailsAxes()
        {
            var a = TestRig.Build();
            var b = RoundTrip(a);
            var n = b.Nodes[b.FindNode("Slot_Hair")];
            double h = 0.5 * Math.PI / 180; // 1° twist
            var q = new[] { 0, 0, Math.Sin(h), Math.Cos(h) };
            var r0 = n.R;
            n.R = new[]
            {
                r0[3] * q[0] + r0[0] * q[3] + r0[1] * q[2] - r0[2] * q[1], r0[3] * q[1] - r0[0] * q[2] + r0[1] * q[3] + r0[2] * q[0],
                r0[3] * q[2] + r0[0] * q[1] - r0[1] * q[0] + r0[2] * q[3], r0[3] * q[3] - r0[0] * q[0] - r0[1] * q[1] - r0[2] * q[2],
            };
            Assert.That(RigCheck.Compare(a, b).Lines.Single(l => l.Check == "bind pose axes").Level, Is.EqualTo(RigCheck.Level.Fail));
        }

        [Test]
        public void RenamedBoneAndBadWeightsFail()
        {
            var a = TestRig.Build();
            var b = RoundTrip(a);
            b.Nodes[b.FindNode("Slot_Beard")].Name = "Slot_Beard.001";
            var body = b.Meshes.First(m => m.IsSkinned);
            body.Weights[0] = 0.7f; body.Weights[1] = 0.1f; // sum 0.8
            var r = RigCheck.Compare(a, b);
            Assert.That(r.Lines.Single(l => l.Check == "nodes").Level, Is.EqualTo(RigCheck.Level.Fail));
            Assert.That(r.Lines.Single(l => l.Check == "slots").Level, Is.EqualTo(RigCheck.Level.Fail));
            Assert.That(r.Lines.Single(l => l.Check.EndsWith("weight sums")).Level, Is.EqualTo(RigCheck.Level.Fail));
        }

        [Test]
        public void MoreThanFourInfluencesFail()
        {
            var a = TestRig.Build();
            var b = RoundTrip(a);
            var m = b.Meshes.First(x => x.IsSkinned);
            int nv = m.VertexCount;
            var ji = new int[nv * 8]; var wt = new float[nv * 8];
            for (int v = 0; v < nv; v++)
                for (int k = 0; k < 4; k++) { ji[v * 8 + k] = m.JointIndices[v * 4 + k]; wt[v * 8 + k] = m.Weights[v * 4 + k]; }
            for (int k = 0; k < 8; k++) { ji[k] = k % m.Joints.Length; wt[k] = 1 / 8f; }
            m.Stride = 8; m.JointIndices = ji; m.Weights = wt;
            Assert.That(RigCheck.Compare(a, b).Lines.Single(l => l.Check.EndsWith("influences")).Level, Is.EqualTo(RigCheck.Level.Fail));
        }

        /// <summary>
        /// Tools/Blender/remaster/fixtures/testrig_blender_roundtrip.glb: the test rig imported into and exported from
        /// Blender 5.1.2 with g3rig.py's settings (one primitive per material with its own vertices, Armature node,
        /// Slot_* empties). Proves the reader merges Blender's layout and the rig survives the round trip.
        /// </summary>
        [Test]
        public void BlenderRoundTripFixturePasses()
        {
            string fixture = null;
            for (var dir = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null && fixture == null; dir = dir.Parent)
            {
                string p = System.IO.Path.Combine(dir.FullName, "Tools", "Blender", "remaster", "fixtures", "testrig_blender_roundtrip.glb");
                if (System.IO.File.Exists(p)) fixture = p;
            }
            if (fixture == null) Assert.Ignore("fixture not found");
            var b = Glb.Read(fixture);
            var a = TestRig.Build();
            Assert.That(b.Generator, Does.Contain("Blender"));
            var r = RigCheck.Compare(a, b);
            Assert.That(r.Passed, r.ToText());
            var body = b.Meshes.Single(m => m.IsSkinned);
            Assert.That(body.VertexCount, Is.EqualTo(a.Meshes[0].VertexCount));
            Assert.That(body.SubMeshes.Count, Is.EqualTo(2));
        }

        [Test]
        public void MatrixDecomposeRoundTrip()
        {
            var q = new[] { 0.3, -0.2, 0.5, 0.0 };
            double l = Math.Sqrt(q.Sum(x => x * x));
            q = q.Select(x => x / l).ToArray();
            q[3] = Math.Sqrt(Math.Max(0, 1 - q[0] * q[0] - q[1] * q[1] - q[2] * q[2]));
            var m = M4.Trs(new[] { 1.0, 2, 3 }, q, null);
            M4.Decompose(m, out var t, out var r, out var s);
            var m2 = M4.Trs(t, r, s);
            for (int i = 0; i < 16; i++) Assert.That(m2[i], Is.EqualTo(m[i]).Within(1e-9));
            var inv = M4.Mul(M4.Inverse(m), m);
            for (int i = 0; i < 16; i++) Assert.That(inv[i], Is.EqualTo(M4.Identity()[i]).Within(1e-9));
        }
    }
}
