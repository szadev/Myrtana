using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using G3.Formats.Trees;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>World remaster v2 trees (Docs/remaster-world.md 10.15): model format, species plan, fitting, LODs, shader hooks.</summary>
    [TestFixture]
    public class TreeV2Tests
    {
        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        static TreeV2Model Sample()
        {
            var m = new TreeV2Model { Name = "buckthorn_0", Height = 8, HalfWidth = 4, TrunkRadius = 0.12f };
            var lod = new TreeV2Lod
            {
                Positions = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 },
                Normals = new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1 },
                Colors = Enumerable.Repeat((byte)200, 16).ToArray(),
                Uv0 = new float[] { 0, 0, 1, 0, 1, 1, 0, 1 },
                Uv1 = new float[] { 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0.1f, 2, 0, 0, 0.1f, 3 },
            };
            lod.Submeshes.Add(new TreeV2Submesh { Material = TreeV2Model.MaterialBark, Indices = new[] { 0, 2, 1 } });
            lod.Submeshes.Add(new TreeV2Submesh { Material = TreeV2Model.MaterialCards, Indices = new[] { 0, 3, 2 } });
            m.Lods.Add(lod);
            return m;
        }

        [Test]
        public void Model_RoundTrips()
        {
            var ms = new MemoryStream();
            Sample().Write(ms);
            ms.Position = 0;
            var r = TreeV2Model.Read(ms);
            Assert.AreEqual("buckthorn_0", r.Name);
            Assert.AreEqual(8f, r.Height);
            Assert.AreEqual(1, r.Lods.Count);
            Assert.AreEqual(4, r.Lods[0].VertexCount);
            Assert.AreEqual(2, r.Lods[0].Triangles);
            CollectionAssert.AreEqual(new[] { 0, 3, 2 }, r.Lods[0].Submeshes[1].Indices);
            Assert.AreEqual(3f, r.Lods[0].Uv1[15]);
            Assert.IsFalse(r.Lods[0].IsImpostor);
        }

        [Test]
        public void Model_RejectsBadIndicesAndMagic()
        {
            var m = Sample();
            m.Lods[0].Submeshes[0].Indices = new[] { 0, 1, 9 };
            var ms = new MemoryStream();
            m.Write(ms);
            ms.Position = 0;
            Assert.Throws<InvalidDataException>(() => TreeV2Model.Read(ms));
            Assert.Throws<InvalidDataException>(() => TreeV2Model.Read(new MemoryStream(new byte[32])));
        }

        [Test]
        public void Manifest_Parses()
        {
            string tsv = "model\tspecies\tvariant\tregion\theight\thalf_width\ttrunk_radius\ttris0\ttris1\ttris2\ttris3\tleaf\tbark\tsnow\tflex\n" +
                         "firwinter_1\tfirwinter\t1\tNordmar\t22.2\t5.5\t0.4\t13142\t2878\t752\t2\tLeafSet019\tBark014\t0.65\t0.045\n";
            var e = TreeV2ManifestEntry.Parse(tsv).Single();
            Assert.AreEqual("firwinter", e.Species);
            Assert.AreEqual(1, e.Variant);
            Assert.AreEqual(0.65f, e.Snow, 1e-6);
            Assert.AreEqual(13142, e.Triangles[0]);
            // the Python writer has the same columns
            string py = File.ReadAllText(Path.Combine(Repo(), "Tools/Remaster/trees/treev2_build.py"));
            foreach (var c in tsv.Split('\n')[0].Split('\t')) StringAssert.Contains($"'{c}'", py);
        }

        [Test]
        public void Plan_MapsTheCommonSpecies()
        {
            Assert.AreEqual("firwinter", TreeV2Plan.Resolve("G3_Tree_L_DouglasFir_01_winter").Species);
            Assert.AreEqual("fir", TreeV2Plan.Resolve("G3_Tree_L_DouglasFir_01.spt").Species);
            Assert.AreEqual("fir", TreeV2Plan.Resolve("G3_Tree_S_DouglasFir_02").Species);
            CollectionAssert.AreEqual(new[] { 0 }, TreeV2Plan.Resolve("G3_Tree_S_DouglasFir_02").Variants, "young fir: the full cone");
            Assert.AreEqual("buckthorn", TreeV2Plan.Resolve("G3_Tree_S_CarolinaBuckthorn_02").Species);
            Assert.AreEqual("bushwinter", TreeV2Plan.Resolve("G3_Tree_bush_05_winter").Species);
            Assert.AreEqual("palm", TreeV2Plan.Resolve("G3_Tree_M_CoconutPalm_02").Species);
            Assert.AreEqual("fanpalm", TreeV2Plan.Resolve("G3_Tree_M_FanPalm_01").Species);
            Assert.AreEqual("deadwood", TreeV2Plan.Resolve("G3_Tree_M_UmbrellaThorn_02").Species);
            Assert.AreEqual("holly", TreeV2Plan.Resolve("g3_tree_xs_bulfordholly_01").Species);
            Assert.IsNull(TreeV2Plan.Resolve("G3_Tree_XXL_UmbrellaThorn_01"), "the 40 m giant stays G3");
            Assert.IsNull(TreeV2Plan.Resolve("G3_Tree_S_Linden_01"));
            Assert.IsNull(TreeV2Plan.Resolve(""));
        }

        [Test]
        public void Plan_CoversAtLeast80PercentOfThePlacements()
        {
            var (per, replaced) = TreeV2Plan.Coverage();
            Assert.GreaterOrEqual(replaced / (double)TreeV2Plan.G3TotalPlacements, 0.8);
            Assert.GreaterOrEqual(per.Count, 6);
            Assert.LessOrEqual(per.Count, 10);
            // every replaced tree has its generated G3 size for the fit
            foreach (var k in TreeV2Plan.G3Placements.Keys.Where(k => TreeV2Plan.Resolve(k) != null))
                Assert.IsTrue(TreeV2Plan.G3Size.ContainsKey(k), k);
        }

        [Test]
        public void Plan_VariantIsDeterministicAndAllowed()
        {
            var rule = TreeV2Plan.Resolve("G3_Tree_XS_CarolinaBuckthorn_02");
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < 200; i++)
            {
                int v = TreeV2Plan.PickVariant(rule, 3, i * 7.3f, i * -3.1f);
                Assert.AreEqual(v, TreeV2Plan.PickVariant(rule, 3, i * 7.3f, i * -3.1f));
                CollectionAssert.Contains(rule.Variants, v);
                seen.Add(v);
            }
            Assert.AreEqual(2, seen.Count, "both allowed variants are used");
            Assert.AreEqual(-1, TreeV2Plan.PickVariant(rule, 0, 0, 0));
            var any = TreeV2Plan.Resolve("G3_Tree_bush_01_winter");
            for (int i = 0; i < 50; i++) Assert.That(TreeV2Plan.PickVariant(any, 3, i, i), Is.InRange(0, 2));
        }

        [Test]
        public void Plan_FitMatchesHeightAndClampsWidth()
        {
            var (xz, y) = TreeV2Plan.Fit(14.26f, 6.94f, 15f, 4.5f);
            Assert.AreEqual(14.26f / 15f, y, 1e-5);
            Assert.That(xz, Is.InRange(0.8f * y, 1.25f * y));
            var (xz2, y2) = TreeV2Plan.Fit(8, 30, 8, 2);     // far wider G3 tree: clamped
            Assert.AreEqual(1.25f * y2, xz2, 1e-5);
            var (xz3, y3) = TreeV2Plan.Fit(8, 0, 4, 2);      // unknown width: uniform
            Assert.AreEqual(y3, xz3);
            Assert.AreEqual(2f, y3, 1e-6);
        }

        [Test]
        public void Plan_LodsAndImpostorFrames()
        {
            for (int k = 1; k < TreeV2Plan.LodHeights.Length; k++) Assert.Less(TreeV2Plan.LodHeights[k], TreeV2Plan.LodHeights[k - 1]);
            Assert.Less(TreeV2Plan.SwitchDistance(15, 0), 60, "LOD0 only close by");
            Assert.Greater(TreeV2Plan.SwitchDistance(15, 2), 150, "geometry up to the mid distance");
            Assert.AreEqual(0, TreeV2Plan.ImpostorFrame(0, 1, 8));
            Assert.AreEqual(2, TreeV2Plan.ImpostorFrame(1, 0, 8));
            Assert.AreEqual(4, TreeV2Plan.ImpostorFrame(0, -1, 8));
            Assert.AreEqual(6, TreeV2Plan.ImpostorFrame(-1, 0, 8));
            Assert.AreEqual(0, TreeV2Plan.ImpostorFrame(-0.1f, 1, 8), "rounds to the nearest frame, wraps");
            Assert.AreEqual(0, TreeV2Plan.ImpostorFrame(1, 0, 1));
        }

        [Test]
        public void Shader_And_Builder_AgreeOnTheV2Properties()
        {
            string root = Repo();
            string shader = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Shaders/Trees/G3SpeedTree.shader"));
            string hlsl = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Shaders/Trees/G3SpeedTree.hlsl"));
            string builder = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Editor/Remaster/G3TreeV2Builder.cs"));
            foreach (var p in new[] { "_TreeV2", "_ImpostorFrames", "_Snow", "_UseMaskMap", "_MaskMap" })
            {
                StringAssert.Contains(p + "(", shader);
                StringAssert.Contains(p, hlsl);
                StringAssert.Contains($"\"{p}\"", builder);
            }
            StringAssert.Contains("G3TreeImpostorFrame", hlsl);
            // the frame convention of the bake (yaw 0 = +z, +x at 90°) is the one of TreeV2Plan.ImpostorFrame
            StringAssert.Contains("atan2(d.x, d.z)", hlsl);
            string bake = File.ReadAllText(Path.Combine(root, "Tools/Remaster/trees/treev2_bake.py"));
            StringAssert.Contains("IMPOSTOR_FRAMES", bake);
        }

        [Test]
        public void GeneratedModels_AreUnderTheBudgets()
        {
            string dir = Path.Combine(Environment.GetEnvironmentVariable("G3_PROJECT") ?? ".", "GameData/Remaster/World/Trees/Models");
            string manifest = Path.Combine(dir, "manifest.tsv");
            if (!File.Exists(manifest)) Assert.Ignore("no generated models (python -I Tools/Remaster/trees/treev2_build.py)");
            int[] budget = { 25000, 8000, 2000 };
            foreach (var e in TreeV2ManifestEntry.Parse(File.ReadAllText(manifest)))
            {
                TreeV2Model m;
                using (var fs = File.OpenRead(Path.Combine(dir, e.Model + ".g3tv2"))) m = TreeV2Model.Read(fs);
                Assert.AreEqual(4, m.Lods.Count, e.Model);
                for (int k = 0; k < 3; k++) Assert.LessOrEqual(m.Lods[k].Triangles, budget[k], $"{e.Model} LOD{k}");
                Assert.IsTrue(m.Lods[3].IsImpostor, e.Model);
                Assert.AreEqual(e.Triangles[0], m.Lods[0].Triangles, e.Model);
                Assert.IsNotNull(TreeV2Plan.Rules.FirstOrDefault(r => r.Species == e.Species), "every built species is used");
            }
        }
    }
}
