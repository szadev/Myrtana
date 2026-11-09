using System;
using System.IO;
using System.Linq;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>R2 LOD layout, LOD file paths and the R2 normal map lookup (Docs/remaster-pipeline.md §10).</summary>
    public class RemasterLodTests
    {
        [TestCase("G3_Hero_Body_Player", 0, "G3_Hero_Body_Player")]
        [TestCase("G3_Hero_Body_Player_LOD1", 1, "G3_Hero_Body_Player")]
        [TestCase("G3_Head_Hero_Hero_Animated_01_LOD2", 2, "G3_Head_Hero_Hero_Animated_01")]
        [TestCase("G3_Hero_Body_Player_2_LOD1", 1, "G3_Hero_Body_Player_2")]
        [TestCase("Slot_LOD", 0, "Slot_LOD")]
        public void ParsesRendererNames(string name, int level, string part)
        {
            Assert.That(RemasterLods.LevelOf(name), Is.EqualTo(level));
            Assert.That(RemasterLods.PartOf(name), Is.EqualTo(part));
        }

        [Test]
        public void PlanShowsEachPartOncePerLevel()
        {
            var plan = RemasterLods.Plan(new[]
            {
                "Body", "Body_LOD1", "Body_LOD2", "Head", "Head_LOD1", "G3_Animation_Head_Hair_01",
            });
            Assert.That(plan.Count, Is.EqualTo(3));
            Assert.That(plan[0], Is.EquivalentTo(new[] { "Body", "Head", "G3_Animation_Head_Hair_01" }));
            Assert.That(plan[1], Is.EquivalentTo(new[] { "Body_LOD1", "Head_LOD1", "G3_Animation_Head_Hair_01" }));
            Assert.That(plan[2], Is.EquivalentTo(new[] { "Body_LOD2", "Head_LOD1", "G3_Animation_Head_Hair_01" })); // head has no LOD2
        }

        [Test]
        public void NoLodRenderersNoPlan() => Assert.That(RemasterLods.Plan(new[] { "Body", "Head" }), Is.Empty);

        [Test]
        public void TransitionsDecrease()
        {
            var h = RemasterLods.ScreenHeights;
            Assert.That(h.Length, Is.EqualTo(RemasterPaths.LodLevels + 1));
            for (int i = 1; i < h.Length; i++) Assert.That(h[i], Is.LessThan(h[i - 1]));
            Assert.That(h[h.Length - 1], Is.GreaterThan(0f));
        }

        [Test]
        public void LodFilesLiveBesideTheOverrides()
        {
            string p = RemasterPaths.LodFile("X", "G3_Hero_Body_Player.xact", 2).Replace(Path.DirectorySeparatorChar, '/');
            Assert.That(p, Does.EndWith("Remaster/Characters/LOD/G3_Hero_Body_Player_LOD2.glb"));
        }

        [Test]
        public void R2NormalMapReplacesR1()
        {
            string project = Path.Combine(Path.GetTempPath(), "remaster_r2_" + Guid.NewGuid().ToString("N"));
            string old = Environment.GetEnvironmentVariable(RemasterPaths.GameDataVariable);
            try
            {
                Environment.SetEnvironmentVariable(RemasterPaths.GameDataVariable, null);
                string dir = RemasterTextures.SetDir(project, "G3_Test_Diffuse_S1");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.BaseMap), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.BumpMap), new byte[] { 1 });
                Assert.That(RemasterTextures.Find(project, "G3_Test_Diffuse_S1")[RemasterTextures.BumpMap], Does.EndWith(RemasterTextures.BumpMap));
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.BumpMapR2), new byte[] { 1 });
                var maps = RemasterTextures.Find(project, "G3_Test_Diffuse_S1");
                Assert.That(maps.Keys, Is.EquivalentTo(new[] { RemasterTextures.BaseMap, RemasterTextures.BumpMap }));
                Assert.That(maps[RemasterTextures.BumpMap], Does.EndWith(RemasterTextures.BumpMapR2));
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.MaskMap), new byte[] { 1 });
                Assert.That(RemasterTextures.Find(project, "G3_Test_Diffuse_S1")[RemasterTextures.MaskMap], Does.EndWith(RemasterTextures.MaskMap));
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.MaskMapR2), new byte[] { 1 });
                maps = RemasterTextures.Find(project, "G3_Test_Diffuse_S1");
                Assert.That(maps.Keys, Is.EquivalentTo(new[] { RemasterTextures.BaseMap, RemasterTextures.BumpMap, RemasterTextures.MaskMap }));
                Assert.That(maps[RemasterTextures.MaskMap], Does.EndWith(RemasterTextures.MaskMapR2));
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.BaseMapR2c), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.BumpMapR2c), new byte[] { 1 });
                maps = RemasterTextures.Find(project, "G3_Test_Diffuse_S1");
                Assert.That(maps.Keys, Is.EquivalentTo(new[] { RemasterTextures.BaseMap, RemasterTextures.BumpMap, RemasterTextures.MaskMap }));
                Assert.That(maps[RemasterTextures.BaseMap], Does.EndWith(RemasterTextures.BaseMapR2c));
                Assert.That(maps[RemasterTextures.BumpMap], Does.EndWith(RemasterTextures.BumpMapR2c));
                Assert.That(maps[RemasterTextures.MaskMap], Does.EndWith(RemasterTextures.MaskMapR2));
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.MaskMapR2c), new byte[] { 1 });
                Assert.That(RemasterTextures.Find(project, "G3_Test_Diffuse_S1")[RemasterTextures.MaskMap], Does.EndWith(RemasterTextures.MaskMapR2c));

                // the local-AI pass wins over R1/R2/R2c, per key; G3_REMASTER_AI=0 ignores it
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.BaseMapAI), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.MaskMapAI), new byte[] { 1 });
                Environment.SetEnvironmentVariable(RemasterTextures.AiVariable, null);
                maps = RemasterTextures.Find(project, "G3_Test_Diffuse_S1");
                Assert.That(maps.Keys, Is.EquivalentTo(new[] { RemasterTextures.BaseMap, RemasterTextures.BumpMap, RemasterTextures.MaskMap }));
                Assert.That(maps[RemasterTextures.BaseMap], Does.EndWith(RemasterTextures.BaseMapAI));
                Assert.That(maps[RemasterTextures.BumpMap], Does.EndWith(RemasterTextures.BumpMapR2c));
                Assert.That(maps[RemasterTextures.MaskMap], Does.EndWith(RemasterTextures.MaskMapAI));
                Environment.SetEnvironmentVariable(RemasterTextures.AiVariable, "0");
                Assert.That(RemasterTextures.Find(project, "G3_Test_Diffuse_S1")[RemasterTextures.BaseMap], Does.EndWith(RemasterTextures.BaseMapR2c));
            }
            finally
            {
                Environment.SetEnvironmentVariable(RemasterTextures.AiVariable, null);
                Environment.SetEnvironmentVariable(RemasterPaths.GameDataVariable, old);
                if (Directory.Exists(project)) Directory.Delete(project, true);
            }
        }
    }
}
