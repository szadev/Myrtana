using System.Collections.Generic;
using G3.Formats.Remaster;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>World remaster r1 (Docs/remaster-world.md section 7): gameplay-distance terms per material and the look layer.</summary>
    [TestFixture]
    public class WorldRemasterR1Tests
    {
        static Dictionary<string, string> Set(string stem) => new Dictionary<string, string>
        {
            [RemasterTextures.BaseMap] = stem + "/BaseMap.png",
            [RemasterTextures.MaskMap] = stem + "/MaskMap.png",
        };

        static WorldDetailPlan DetailOf(WorldMaterialInfo info, string recipe = null) =>
            WorldRemasterPlan.Detail(info, WorldRemasterPlan.For(info, Set, recipe));

        [Test]
        public void TerrainRockGrassBlend_GetsMacroMesoAndTriplanar()
        {
            var d = DetailOf(new WorldMaterialInfo
            {
                Name = "G3_Nature_Wall_Stone_02_F", Shader = "G3/LitBlend",
                BaseMap = "G3_Nature_Wall_Stone_02_Diffuse_01", BaseMap2 = "G3_Nature_Ground_Grass_01_Diffuse_01",
            });
            Assert.AreEqual(0.08f, d.MacroStrength, 1e-6);
            Assert.AreEqual(0.5f, d.AntiTile, 1e-6);
            Assert.AreEqual(0.6f, d.MesoStrength, 1e-6);
            Assert.AreEqual(1f, d.Triplanar, 1e-6);
            Assert.IsFalse(d.BarkDetail);
        }

        [Test]
        public void ObjectStone_SimpleLit_NoTriplanar_ArchitectureLightMacroOnly()
        {
            var stone = DetailOf(new WorldMaterialInfo
            {
                Name = "G3_Nature_Wall_Stone_02_C", Shader = "Universal Render Pipeline/Simple Lit", BaseMap = "G3_Nature_Wall_Stone_12_Diffuse_01",
            });
            Assert.AreEqual(0f, stone.Triplanar);
            Assert.AreEqual(0f, stone.MesoStrength);
            var wall = DetailOf(new WorldMaterialInfo
            {
                Name = "G3_Architecture_WallHouse_01_A", Shader = "Universal Render Pipeline/Simple Lit", BaseMap = "G3_Architecture_WallHouse_01_Diffuse_01",
            });
            Assert.AreEqual(0.04f, wall.MacroStrength, 1e-6);
            Assert.AreEqual(0f, wall.AntiTile);
            Assert.AreEqual(0f, wall.Triplanar);
        }

        [Test]
        public void Recipe_OverridesAndBarkDetail()
        {
            var d = DetailOf(new WorldMaterialInfo
            {
                Name = "G3_Nature_Ground_Grass_01_A", Shader = "G3/LitBlend", BaseMap = "G3_Nature_Ground_Grass_01_Diffuse_01", SingleLayer = true,
            }, "macro_strength = 0.02\nmeso_strength = 0 # off");
            Assert.AreEqual(0.02f, d.MacroStrength, 1e-6);
            Assert.AreEqual(0f, d.MesoStrength);
            var bark = DetailOf(new WorldMaterialInfo
            {
                // as the builder describes it in-engine (W1): SpeedTree is never Opaque and _AlphaClip defaults to 1
                Name = "DouglasFir_Bark", Shader = "G3/SpeedTree", BaseMap = "G3_SpT_Bark_DouglasFirBark_01_Diffuse_01",
                Opaque = false, AlphaClip = true,
            });
            Assert.IsTrue(bark.BarkDetail);
            var leaf = DetailOf(new WorldMaterialInfo
            {
                Name = "DouglasFir_Leaf", Shader = "G3/SpeedTree", BaseMap = "G3_Speedtree_Misc_Composite_02_Diffuse_01", Opaque = false,
                AlphaClip = true, TreeMode = 1,
            });
            Assert.IsFalse(leaf.BarkDetail);
        }

        [Test]
        public void AerialAmount_MonotonicCappedAndThinnerAbove()
        {
            Assert.AreEqual(0f, WorldRemasterLook.AerialAmount(0, 15, 15));
            float a10 = WorldRemasterLook.AerialAmount(10, 15, 15), a100 = WorldRemasterLook.AerialAmount(100, 15, 15);
            float a2000 = WorldRemasterLook.AerialAmount(2000, 15, 15);
            Assert.Less(a10, 0.02f, "near haze stays negligible (5-30 m keeps G3's colours)");
            Assert.Greater(a100, a10);
            Assert.LessOrEqual(a2000, WorldRemasterLook.AerialMax + 1e-6f);
            Assert.Less(WorldRemasterLook.AerialAmount(200, 15, 80), WorldRemasterLook.AerialAmount(200, 15, 15), "less haze towards high ground");
        }

        [Test]
        public void CloudCoverage_FollowsOvercast()
        {
            Assert.AreEqual(WorldRemasterLook.Sky[1], WorldRemasterLook.CloudCoverage(0), 1e-6);
            Assert.AreEqual(0.98f, WorldRemasterLook.CloudCoverage(1), 1e-5);
            Assert.Greater(WorldRemasterLook.CloudCoverage(0.5f), WorldRemasterLook.CloudCoverage(0.2f));
        }
    }
}
