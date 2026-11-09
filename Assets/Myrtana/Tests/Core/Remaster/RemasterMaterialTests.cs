using System;
using System.IO;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>R1 material kinds and texture-set lookup (Docs/remaster-pipeline.md §9), with the pilot's real names.</summary>
    public class RemasterMaterialTests
    {
        [TestCase("Hero_player", "G3_Human_Hero_Body_Diffuse_S1", "G3_Human_Hero_Body_Diffuse_S1", RemasterMaterialKind.Armour)]
        [TestCase("G3_Animation_Body_Paladin_01_A", "G3_Human_Paladin_Body_Diffuse_S1", "G3_Human_Paladin_Body_Specular_S1", RemasterMaterialKind.Armour)]
        [TestCase("G3_Human_HERO_Face_Diffuse_01_A", "G3_Animation_Head_Hero_01_S1", "G3_Animation_Head_Hero_01_S1", RemasterMaterialKind.Skin)]
        [TestCase("G3_Human_HERO_Face_Diffuse_01_B", "G3_Animation_Head_Hero_01_S1", "G3_Animation_Head_Eyes_01_Specular_01", RemasterMaterialKind.Eye)]
        [TestCase("G3_Animation_Head_Main_01", "G3_Animation_Head_Main_01_S29", null, RemasterMaterialKind.Skin)]
        [TestCase("G3_Animation_Head_Mouth_01", "G3_Animation_Head_Mouth_01_S1", null, RemasterMaterialKind.Skin)]
        [TestCase("G3_Animation_Head_Eyes_01", "G3_Animation_Head_Eyes_01_S5", null, RemasterMaterialKind.Eye)]
        [TestCase("G3_Animation_Head_Hair_Myrtana_01_A", "G3_Animation_Head_Hair_Myrtana_01_S5", null, RemasterMaterialKind.Hair)]
        [TestCase("G3_Animation_Head_Beard_01", null, null, RemasterMaterialKind.Hair)]
        [TestCase("G3_Animation_Head_Eyebrows_01", null, null, RemasterMaterialKind.Hair)]
        [TestCase("G3_Animation_Head_Eyelashes_01", null, null, RemasterMaterialKind.Lash)]
        public void ClassifiesPilotMaterials(string material, string diffuse, string specular, RemasterMaterialKind expected) =>
            Assert.That(RemasterMaterialKinds.Classify(material, diffuse, specular), Is.EqualTo(expected));

        [Test]
        public void KindNamesRoundTrip()
        {
            foreach (RemasterMaterialKind k in Enum.GetValues(typeof(RemasterMaterialKind)))
            {
                Assert.That(RemasterMaterialKinds.TryParse(RemasterMaterialKinds.Name(k), out var back), Is.True);
                Assert.That(back, Is.EqualTo(k));
            }
        }

        [Test]
        public void StemDropsImporterNormalSuffixOnly()
        {
            Assert.That(RemasterTextures.StemOf("G3_Human_Paladin_Body_Normal_S1_n"), Is.EqualTo("G3_Human_Paladin_Body_Normal_S1"));
            Assert.That(RemasterTextures.StemOf("G3_Animation_Head_Main_01_S29.asset"), Is.EqualTo("G3_Animation_Head_Main_01_S29"));
            Assert.That(RemasterTextures.StemOf(null), Is.Null);
        }

        [Test]
        public void FindListsOnlyExistingMaps()
        {
            string project = Path.Combine(Path.GetTempPath(), "remaster_r1_" + Guid.NewGuid().ToString("N"));
            string old = Environment.GetEnvironmentVariable(RemasterPaths.GameDataVariable);
            try
            {
                Environment.SetEnvironmentVariable(RemasterPaths.GameDataVariable, null);
                string dir = RemasterTextures.SetDir(project, "G3_Test_Diffuse_S1");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.BaseMap), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(dir, RemasterTextures.MaskMap), new byte[] { 1 });
                var maps = RemasterTextures.Find(project, "G3_Test_Diffuse_S1");
                Assert.That(maps.Keys, Is.EquivalentTo(new[] { RemasterTextures.BaseMap, RemasterTextures.MaskMap }));
                Assert.That(RemasterTextures.Find(project, "G3_Missing").Count, Is.Zero);
                Assert.That(dir.Replace('\\', '/'), Does.EndWith("GameData/Remaster/Textures/G3_Test_Diffuse_S1"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(RemasterPaths.GameDataVariable, old);
                if (Directory.Exists(project)) Directory.Delete(project, true);
            }
        }
    }
}
