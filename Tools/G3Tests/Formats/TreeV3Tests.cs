using System.IO;
using System.Runtime.CompilerServices;
using G3.Formats.Remaster;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// Trees v3 (Docs/remaster-world.md 10.17): G3's own SpeedTree materials get remastered copies with crown/variation terms;
    /// the procedural v2 models are no longer behind the Trees option.
    /// </summary>
    [TestFixture]
    public class TreeV3Tests
    {
        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        static WorldMaterialInfo Tree(string name, int mode, bool clip, string baseMap) => new WorldMaterialInfo
        {
            Name = name, Shader = TreeV3Look.Shader, TreeMode = mode, AlphaClip = clip, BaseMap = baseMap,
        };

        [Test]
        public void Foliage_CrownNormalAoMaskAndNormalMap()
        {
            foreach (var m in new[]
                     {
                         Tree("Leaf_G3_Speedtree_Misc_Composite_02_Diffuse_01", 1, true, "G3_Speedtree_Misc_Composite_02_Diffuse_01"),
                         Tree("Frond_G3_Speedtree_Misc_Composite_02_Diffuse_01", 0, true, "G3_Speedtree_Misc_Composite_02_Diffuse_01"),
                         Tree("Billboard_G3_Speedtree_Misc_Composite_02_Diffuse_01", 2, true, "G3_Speedtree_Misc_Composite_02_Diffuse_01"),
                     })
            {
                var l = TreeV3Look.For(m);
                Assert.That(l.Applies && l.Foliage, m.Name);
                Assert.That(l.CrownNormal, Is.EqualTo(TreeV3Look.FoliageCrownNormal));
                Assert.That(l.CrownAO, Is.EqualTo(TreeV3Look.FoliageCrownAO));
                Assert.That(l.TintVariation, Is.InRange(0.01f, 0.2f));
                bool frond = m.TreeMode == 0;   // no mesh tangents: the normal map read black (runner A|B)
                Assert.That(l.ForceNormalMap, Is.EqualTo(!frond), m.Name);
                Assert.That(l.PackedMask, Is.EqualTo(!frond), m.Name);
            }
        }

        [Test]
        public void Bark_OnlyVariation_KeepsItsMaskLayout()
        {
            var l = TreeV3Look.For(Tree("Bark_G3_SpT_Bark_DouglasFirBark_01_Diffuse_01", 0, false, "G3_SpT_Bark_DouglasFirBark_01_Diffuse_01"));
            Assert.That(l.Applies && !l.Foliage);
            Assert.That(l.CrownNormal, Is.Zero);
            Assert.That(l.CrownAO, Is.Zero);
            Assert.That(l.TintVariation, Is.EqualTo(TreeV3Look.BarkTint));
            Assert.That(l.PackedMask, Is.False, "worldtex bark masks are R metal / G AO / B height / A smoothness");
            Assert.That(l.ForceNormalMap, Is.False);
        }

        [Test]
        public void OtherShaders_NotTouched_RecipeOverrides()
        {
            Assert.That(TreeV3Look.For(new WorldMaterialInfo { Name = "x", Shader = "G3/Vegetation" }).Applies, Is.False);
            Assert.That(TreeV3Look.For(null).Applies, Is.False);
            var r = WorldMaterialRecipe.Parse("crown_normal = 0.3\ncrown_ao = 0.2\ntint_variation = 0\nnormal_strength = 0.5");
            var l = TreeV3Look.For(Tree("Leaf_x", 1, true, "G3_Speedtree_Misc_Composite_01_Diffuse_01"), r);
            Assert.That(new[] { l.CrownNormal, l.CrownAO, l.TintVariation, l.NormalStrength }, Is.EqualTo(new[] { 0.3f, 0.2f, 0f, 0.5f }));
        }

        [Test]
        public void TreesOption_NoLongerBringsV2Models()
        {
            Assert.That(RemasterOptions.TreesWanted(worldV2Active: true, jsonTreeReplace: false), Is.False);
            Assert.That(RemasterOptions.TreesWanted(worldV2Active: false, jsonTreeReplace: true), Is.False);
            Assert.That(RemasterOptions.TreesWanted(worldV2Active: true, jsonTreeReplace: true), Is.True);
            Assert.That(WorldLookSettings.Default.v2TreeReplace, Is.False, "v2 models off by default");
        }

        [Test]
        public void Shader_V3TermsInEveryPassSharingTheCBuffer()
        {
            string hlsl = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Trees/G3SpeedTree.hlsl"));
            string shader = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Trees/G3SpeedTree.shader"));
            foreach (var p in new[] { "_TreeV3", "_CrownNormal", "_CrownAO", "_TintVariation" })
            {
                StringAssert.Contains($"half {p};", hlsl, "UnityPerMaterial (SRP batcher: one layout for all passes)");
                StringAssert.Contains($"{p}(\"", shader, "material property");
            }
            StringAssert.Contains("unity_RendererBounds_Min", hlsl, "crown from the renderer bounds (works with the GPU Resident Drawer)");
            StringAssert.Contains("G3TreeCrownPos", shader);
        }
    }
}
