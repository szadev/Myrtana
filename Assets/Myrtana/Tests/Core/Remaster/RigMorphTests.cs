using System.IO;
using System.Linq;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Morph targets in the remaster .glb (MetaHuman hero head, Docs/remaster-metahuman.md §5).</summary>
    public class RigMorphTests
    {
        static RigModel WithMorphs()
        {
            var a = TestRig.Build();
            var m = a.Meshes[0];
            int n = m.VertexCount * 3;
            var jaw = new float[n]; var nrm = new float[n];
            for (int i = 0; i < n; i += 3) { jaw[i] = 0.01f; jaw[i + 1] = -0.02f * (i % 2); nrm[i + 2] = 0.1f; }
            m.Morphs.Add(new RigMorph { Name = "jawOpen", Positions = jaw, Normals = nrm });
            m.Morphs.Add(new RigMorph { Name = "eyeBlinkLeft", Positions = Enumerable.Repeat(0.003f, n).ToArray() });
            return a;
        }

        [Test]
        public void MorphsRoundTripWithNamesAndNormals()
        {
            var a = WithMorphs();
            var b = Glb.Read(Glb.Write(a));
            var body = b.Meshes.First(x => x.IsSkinned);
            Assert.That(body.Morphs.Select(x => x.Name), Is.EqualTo(new[] { "jawOpen", "eyeBlinkLeft" }));
            Assert.That(body.Morphs[0].Positions, Is.EqualTo(a.Meshes[0].Morphs[0].Positions).Within(1e-7));
            Assert.That(body.Morphs[0].Normals, Is.EqualTo(a.Meshes[0].Morphs[0].Normals).Within(1e-7));
            Assert.That(body.Morphs[1].Normals, Is.Null);
            Assert.That(b.Meshes.First(x => !x.IsSkinned).Morphs, Is.Empty);
            Assert.That(RigCheck.Compare(a, b).Passed);
        }

        [Test]
        public void ShippedMetaHumanHeadHasArkitMorphs()
        {
            // Blender writes morph targets as sparse accessors: the reader must expand them
            var f = Path.Combine("GameData/Remaster/Characters", "G3_Head_Hero_Hero_01.glb");
            if (!File.Exists(f)) Assert.Ignore("no hero head");
            var head = Glb.Read(f).Meshes.FirstOrDefault(m => m.Morphs.Count > 0);
            if (head == null) Assert.Ignore("published hero head has no morphs (not the MetaHuman one)");
            Assert.That(head.Morphs.Select(m => m.Name), Does.Contain("jawOpen").And.Contain("eyeBlinkLeft"));
            var jaw = head.Morphs.First(m => m.Name == "jawOpen").Positions;
            Assert.That(jaw.Length, Is.EqualTo(head.Positions.Length));
            Assert.That(jaw.Max(System.Math.Abs), Is.GreaterThan(0.005f));
        }

        [Test]
        public void MirrorFlipsMorphXAndRestDeltasFollowTheBindPose()
        {
            var a = WithMorphs();
            a.MirrorX();
            Assert.That(a.Meshes[0].Morphs[0].Positions[0], Is.EqualTo(-0.01f));
            Assert.That(a.Meshes[0].Morphs[0].Normals[2], Is.EqualTo(0.1f));
            // in the bind pose world(joint) * inverseBind = identity: rest deltas equal the stored ones
            var d = a.RestWorldDeltas(a.Meshes[0], a.Meshes[0].Morphs[0].Positions);
            Assert.That(d, Is.EqualTo(a.Meshes[0].Morphs[0].Positions).Within(1e-5));
        }
    }
}
