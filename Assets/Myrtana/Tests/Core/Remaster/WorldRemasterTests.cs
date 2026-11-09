using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Remaster;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>World remaster override: categories, texel density, plan, recipe, switch, swap (Docs/remaster-world.md).</summary>
    public class WorldRemasterTests
    {
        [TestCase("G3_Myrtana_Landscape_Cell_244.xcmsh", "terrain")]
        [TestCase("G3_World_Rivermesh_01_Depth_271.xcmsh", "hidden")]
        [TestCase("G3_World_Oceanmesh_004.xcmsh", "water")]
        [TestCase("G3_Object_Stone_01.xcmsh", "rock")]
        [TestCase("G3_Object_Plant_Fern_01.xcmsh", "plant")]
        [TestCase("G3_Object_Barrel_01.xcmsh", "prop")]
        [TestCase("G3_Myrtana_Faring_Castle_Donjon_04.xcmsh", "architecture")]
        [TestCase("G3_Object_Wood_Plank_03.xcmsh", "architecture")]
        public void ClassifiesMeshes(string mesh, string category) =>
            Assert.That(WorldRemasterCategories.Classify(mesh), Is.EqualTo(category));

        [Test]
        public void WaterOnlyWhenTheMeshIsWater()
        {
            Assert.That(WorldRemasterCategories.Classify("G3_Object_Interact_Barrel_01.xcmsh", new[] { "G3_Nature_Water_River_01_A" }, isWater: false),
                Is.EqualTo("prop"));
            Assert.That(WorldRemasterCategories.Classify("G3_Myrtana_Lake_01.xcmsh", new[] { "G3_Nature_Water_River_01_A" }, isWater: true),
                Is.EqualTo("water"));
        }

        [TestCase("G3_Nature_Ground_Grass_01_A", "terrain", "terrain")]
        [TestCase("G3_Architecture_Wood_08_A", "architecture", "architecture")]
        [TestCase("G3_Nature_Wall_Stone_02_C", "rock", "rock")]
        [TestCase("G3_Nature_Decal_Farn_01_A", "plant", "plant")]
        [TestCase("G3_Objects_Barrelmetal_01_A", "prop", "prop")]
        [TestCase("G3_Nature_Ground_Gravel_05_D", "architecture", "terrain")]
        [TestCase("G3_Myrtana_Water_River_01_A", "water", "water")]
        [TestCase("G3_Architecture_Bricks_03_B", "terrain", "terrain")]
        public void ClassifiesMaterials(string material, string meshCategory, string expected) =>
            Assert.That(WorldRemasterCategories.ClassifyMaterial(material, meshCategory), Is.EqualTo(expected));

        [Test]
        public void PrimaryCategoryFollowsPrecedence() =>
            Assert.That(WorldRemasterCategories.PrimaryOf(new[] { "prop", "architecture", "terrain" }), Is.EqualTo("terrain"));

        [Test]
        public void AreasGiveMetresPerUv()
        {
            // a 200 x 100 cm quad mapped to UV 2 x 1: 100 cm per UV unit
            var pos = new float[] { 0, 0, 0, 200, 0, 0, 200, 0, 100, 0, 0, 100 };
            var uv = new float[] { 0, 0, 2, 0, 2, 1, 0, 1 };
            var (w, u) = WorldRemasterCategories.Areas(pos, uv, new uint[] { 0, 1, 2, 0, 2, 3 });
            Assert.That(w, Is.EqualTo(20000).Within(1e-6));
            Assert.That(Math.Sqrt(w / u), Is.EqualTo(100).Within(1e-9));
        }

        static Func<string, Dictionary<string, string>> Sets(params string[] stems) => stem =>
        {
            var d = new Dictionary<string, string>();
            if (stems.Contains(stem))
                foreach (var f in new[] { RemasterTextures.BaseMap, RemasterTextures.BumpMap, RemasterTextures.MaskMap })
                    d[f] = $"/w/{stem}/{f}";
            return d;
        };

        [Test]
        public void PlanKeepsMaterialsWithoutMaps()
        {
            var plan = WorldRemasterPlan.For(new WorldMaterialInfo { Shader = "G3/LitBlend", BaseMap = "A", BaseMap2 = "B" }, Sets("X"));
            Assert.That(plan.Mode, Is.EqualTo(WorldRemasterMode.Keep));
            Assert.That(plan.Maps, Is.Empty);
        }

        [Test]
        public void PlanBlendsBothLayers()
        {
            var plan = WorldRemasterPlan.For(new WorldMaterialInfo { Shader = "G3/LitBlend", BaseMap = "A", BaseMap2 = "B" }, Sets("A", "B"));
            Assert.That(plan.Mode, Is.EqualTo(WorldRemasterMode.Blend));
            Assert.That(plan.HasLayerB, Is.True);
            Assert.That(plan.Maps.Keys, Is.EquivalentTo(new[] { "_BaseMap", "_BumpMap", "_MaskMap", "_BaseMap2", "_BumpMap2", "_MaskMap2" }));
            Assert.That(plan.Maps["_MaskMap2"], Is.EqualTo("/w/B/MaskMap.png"));
        }

        [Test]
        public void PlanUsesOneLayerWhenOnlyOneHasMaps()
        {
            var plan = WorldRemasterPlan.For(new WorldMaterialInfo { Shader = "G3/LitBlend", BaseMap = "A", BaseMap2 = "B" }, Sets("B"));
            Assert.That(plan.Mode, Is.EqualTo(WorldRemasterMode.Blend));
            Assert.That(plan.Maps.Keys, Is.EquivalentTo(new[] { "_BaseMap2", "_BumpMap2", "_MaskMap2" }));
        }

        [Test]
        public void PlanIgnoresLayerBOfSingleLayerAndSimpleLit()
        {
            var single = WorldRemasterPlan.For(new WorldMaterialInfo { Shader = "G3/LitBlend", BaseMap = "A", BaseMap2 = "B", SingleLayer = true }, Sets("A", "B"));
            Assert.That(single.HasLayerB, Is.False);
            Assert.That(single.Maps.ContainsKey("_BaseMap2"), Is.False);
            var simple = WorldRemasterPlan.For(new WorldMaterialInfo { Shader = "Universal Render Pipeline/Simple Lit", BaseMap = "A" }, Sets("A"));
            Assert.That(simple.Mode, Is.EqualTo(WorldRemasterMode.Blend));
        }

        [TestCase("Universal Render Pipeline/Simple Lit", false)]
        [TestCase("G3/Vegetation", true)]
        [TestCase("G3/SpeedTree", true)]
        public void PlanSwapsTexturesOnFoliageAndCutouts(string shader, bool opaque)
        {
            var plan = WorldRemasterPlan.For(new WorldMaterialInfo { Shader = shader, BaseMap = "A", Opaque = opaque }, Sets("A"));
            Assert.That(plan.Mode, Is.EqualTo(WorldRemasterMode.Textures));
            Assert.That(plan.Maps.ContainsKey("_BaseMap"), Is.True);
        }

        [Test]
        public void RecipeParsesKeyValues()
        {
            var r = WorldMaterialRecipe.Parse("# comment\nheight_blend = 0.5\n height_depth=0.1 # inline\nfoo = bar\nbroken line\n");
            Assert.That(r.HeightBlend, Is.EqualTo(0.5f));
            Assert.That(r.HeightDepth, Is.EqualTo(0.1f));
            Assert.That(r.NormalStrength, Is.EqualTo(1f));
            Assert.That(r.Values["foo"], Is.EqualTo("bar"));
            Assert.That(WorldMaterialRecipe.Parse(r.ToString()).Values.Count, Is.EqualTo(3));
        }

        [TestCase(null, null, false, false)]
        [TestCase(null, null, true, true)]
        [TestCase("1", "0", false, true)]
        [TestCase("off", "1", true, false)]
        [TestCase("", "true", false, true)]
        [TestCase("garbage", null, true, true)]
        public void SwitchPrecedence(string world, string shared, bool pref, bool expected) =>
            Assert.That(WorldRemasterSwitch.Resolve(world, shared, pref), Is.EqualTo(expected));

        [Test]
        public void SwapAppliesAndReverts()
        {
            string o1 = "o1", o2 = "o2", r1 = "r1";
            var pairs = new List<(string, string)> { (o1, r1) };
            var mats = new[] { o1, o2, null, o1 };
            Assert.That(WorldRemasterSwap.Apply(mats, WorldRemasterSwap.Direction(pairs, true)), Is.EqualTo(2));
            Assert.That(mats, Is.EqualTo(new[] { r1, o2, null, r1 }));
            Assert.That(WorldRemasterSwap.Apply(mats, WorldRemasterSwap.Direction(pairs, true)), Is.EqualTo(0));
            Assert.That(WorldRemasterSwap.Apply(mats, WorldRemasterSwap.Direction(pairs, false)), Is.EqualTo(2));
            Assert.That(mats, Is.EqualTo(new[] { o1, o2, null, o1 }));
        }

        [Test]
        public void FindsWorldTextureSets()
        {
            string root = Path.Combine(Path.GetTempPath(), "g3wr_" + Guid.NewGuid().ToString("N"));
            string prev = Environment.GetEnvironmentVariable(RemasterPaths.GameDataVariable);
            try
            {
                Environment.SetEnvironmentVariable(RemasterPaths.GameDataVariable, root);
                string dir = Path.Combine(WorldRemasterPaths.TextureDir("ignored"), "G3_Nature_Ground_Grass_01_Normal_01");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, RemasterTextures.BaseMap), "x");
                var found = WorldRemasterPaths.FindTextures("ignored", "G3_Nature_Ground_Grass_01_Normal_01_n");
                Assert.That(found.Keys, Is.EquivalentTo(new[] { RemasterTextures.BaseMap }));
                Assert.That(WorldRemasterPaths.FindTextures("ignored", "Missing"), Is.Empty);
                Assert.That(WorldRemasterPaths.RecipeFile("ignored", "Mat.xshmat"), Does.EndWith(Path.Combine("World", "Materials", "Mat.txt")));
            }
            finally
            {
                Environment.SetEnvironmentVariable(RemasterPaths.GameDataVariable, prev);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
