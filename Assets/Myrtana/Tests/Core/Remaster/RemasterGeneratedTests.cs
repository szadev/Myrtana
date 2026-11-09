using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>FACES generated materials (eyes, cornea, hair cards/cap) and the hair-card file of a head.</summary>
    public class RemasterGeneratedTests
    {
        [TestCase("G3_Remaster_Eye__G3_Animation_Head_Hero_01_S1_eye", RemasterGeneratedKind.Eye)]
        [TestCase("G3_Remaster_Eye__G3_Animation_Head_Main_01_S29_eye.001", RemasterGeneratedKind.Eye)]
        [TestCase("G3_Remaster_Cornea", RemasterGeneratedKind.Cornea)]
        [TestCase("G3_Remaster_Cornea.002", RemasterGeneratedKind.Cornea)]
        [TestCase("G3_Remaster_Hero_HairCards", RemasterGeneratedKind.HairCards)]
        [TestCase("G3_Remaster_Hero_HairCap", RemasterGeneratedKind.HairCap)]
        [TestCase("G3_Animation_Head_Eyes_01", RemasterGeneratedKind.None)]
        [TestCase("G3_Human_HERO_Face_Diffuse_01_A", RemasterGeneratedKind.None)]
        [TestCase("G3_Remaster_Something", RemasterGeneratedKind.None)]
        [TestCase(null, RemasterGeneratedKind.None)]
        public void ClassifiesGeneratedMaterials(string name, RemasterGeneratedKind expected)
        {
            Assert.That(RemasterGenerated.Classify(name), Is.EqualTo(expected));
            Assert.That(RemasterGenerated.IsGenerated(name), Is.EqualTo(expected != RemasterGeneratedKind.None));
        }

        [Test]
        public void TextureSetIsTheMaterialNameExceptCornea()
        {
            Assert.That(RemasterGenerated.TextureSet("G3_Remaster_Eye__G3_Animation_Head_Hero_01_S1_eye.001"),
                        Is.EqualTo("G3_Remaster_Eye__G3_Animation_Head_Hero_01_S1_eye"));
            Assert.That(RemasterGenerated.TextureSet("G3_Remaster_Hero_HairCards"), Is.EqualTo("G3_Remaster_Hero_HairCards"));
            Assert.That(RemasterGenerated.TextureSet("G3_Remaster_Hero_HairCap"), Is.EqualTo("G3_Remaster_Hero_HairCap"));
            Assert.That(RemasterGenerated.TextureSet("G3_Remaster_Cornea"), Is.Null);
            Assert.That(RemasterGenerated.TextureSet("G3_Animation_Head_Eyes_01"), Is.Null);
        }

        [Test]
        public void HairSetFollowsTheMaterialSwitch()
        {
            var have = new HashSet<string> { "G3_Remaster_Hair_Myrtana_01_S1_HairCards", "G3_Remaster_Hair_Myrtana_01_S4_HairCards",
                                             "G3_Remaster_Hair_Myrtana_01_S4_HairCap" };
            Func<string, bool> exists = have.Contains;
            const string cards = "G3_Remaster_Hair_Myrtana_01_S5_HairCards.001";
            Assert.That(RemasterGenerated.SwitchTextureSet(cards, 4, exists), Is.EqualTo("G3_Remaster_Hair_Myrtana_01_S4_HairCards"));
            Assert.That(RemasterGenerated.SwitchTextureSet(cards, 0, exists), Is.EqualTo("G3_Remaster_Hair_Myrtana_01_S1_HairCards"));
            Assert.That(RemasterGenerated.SwitchTextureSet(cards, 7, exists), Is.EqualTo("G3_Remaster_Hair_Myrtana_01_S5_HairCards"));
            Assert.That(RemasterGenerated.SwitchTextureSet("G3_Remaster_Hair_Myrtana_01_S5_HairCap", 4, exists), Is.EqualTo("G3_Remaster_Hair_Myrtana_01_S4_HairCap"));
            Assert.That(RemasterGenerated.SwitchTextureSet("G3_Remaster_Hero_HairCards", 4, exists), Is.EqualTo("G3_Remaster_Hero_HairCards"));
            Assert.That(RemasterGenerated.SwitchTextureSet("G3_Remaster_Eye__G3_Animation_Head_Hero_01_S1_eye", 4, exists),
                        Is.EqualTo("G3_Remaster_Eye__G3_Animation_Head_Hero_01_S1_eye"));
            Assert.That(RemasterGenerated.SwitchTextureSet("G3_Remaster_Cornea", 4, exists), Is.Null);
        }

        [Test]
        public void ShadersPerKind()
        {
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.Cornea), Is.EqualTo("G3/Remaster/Cornea"));
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.Eye), Is.EqualTo("G3/Remaster/Eye"));
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.HairCards), Is.EqualTo("G3/Remaster/Hair"));
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.HairCap), Is.EqualTo("G3/Remaster/Hair"));
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.None), Is.Null);
            Assert.That(RemasterGenerated.ShaderKind(RemasterGeneratedKind.Eye), Is.EqualTo(RemasterMaterialKind.Eye));
            Assert.That(RemasterGenerated.ShaderKind(RemasterGeneratedKind.Cornea), Is.Null);
            Assert.That(RemasterGenerated.IsCutout(RemasterGeneratedKind.HairCards), Is.True);
            Assert.That(RemasterGenerated.IsCutout(RemasterGeneratedKind.HairCap), Is.False);
        }

        [TestCase("G3_Head_Hero_Hero_01", "G3_Head_Hero_Hero_01")]
        [TestCase("G3_Head_Hero_Hero_Animated_01", "G3_Head_Hero_Hero_01")]
        [TestCase("G3_Head_Hero_Myrtana_Animated_05.xact", "G3_Head_Hero_Myrtana_05")]
        [TestCase("G3_Head_Hero_Hero_Animated_01__talk", "G3_Head_Hero_Hero_01")]
        [TestCase("G3_Hero_Body_Player", "G3_Hero_Body_Player")]
        public void HairBelongsToTheStaticHead(string part, string head) =>
            Assert.That(RemasterHair.HeadStem(part), Is.EqualTo(head));

        [Test]
        public void HairFileUnderCharactersHair()
        {
            string f = RemasterHair.File("P", "G3_Head_Hero_Hero_Animated_01");
            Assert.That(Path.GetFileName(f), Is.EqualTo("G3_Head_Hero_Hero_01__hair.glb"));
            Assert.That(Path.GetFileName(Path.GetDirectoryName(f)), Is.EqualTo("Hair"));
            Assert.That(Path.GetDirectoryName(Path.GetDirectoryName(f)), Is.EqualTo(RemasterPaths.OverrideDir("P")));
            Assert.That(RemasterHair.MeshStem("G3_Head_Hero_Hero_Animated_01"), Is.EqualTo("G3_Head_Hero_Hero_01__hair"));
        }

        [Test]
        public void FirstPartWithHairWins()
        {
            var parts = new[] { "G3_Hero_Body_Player", "G3_Head_Hero_Hero_Animated_01" };
            Assert.That(RemasterHair.FirstWithHair(parts, h => h == "G3_Head_Hero_Hero_01"), Is.EqualTo("G3_Head_Hero_Hero_Animated_01"));
            Assert.That(RemasterHair.FirstWithHair(parts, h => false), Is.Null);
        }
    }
}
