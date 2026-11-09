using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>Varant architecture remaster (Docs/remaster-world.md section 25, arch-1): classes, layers, material vector.</summary>
    [TestFixture]
    public class WorldRemasterArchTests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        [Test]
        public void Classes_VarantStems()
        {
            Assert.AreEqual("sandstone", WorldV2.ArchClassFor("G3_Architecture_SandStone_05_Diffuse_01"));
            Assert.AreEqual("masonry", WorldV2.ArchClassFor("G3_Architecture_SandStone_06_Diffuse_02"));
            Assert.AreEqual("masonry", WorldV2.ArchClassFor("G3_Architecture_SandStoneTiles_02_Diffuse_02"));
            Assert.AreEqual("floor", WorldV2.ArchClassFor("G3_Architecture_GroundPlates_01_Diffuse_01"));
            Assert.AreEqual("masonry", WorldV2.ArchClassFor("G3_Desert_Ruins_Stone_01_Diffuse_01"));
            Assert.AreEqual("cloth", WorldV2.ArchClassFor("G3_Desert_Tent_04_Diffuse_01"));
            Assert.AreEqual("plaster", WorldV2.ArchClassFor("G3_Architecture_RoomWalls_01_Diffuse_01"));
            Assert.AreEqual("wood", WorldV2.ArchClassFor("G3_Objects_Orient_Wood_V_01_Diffuse_01"));
            Assert.AreEqual("metal", WorldV2.ArchClassFor("G3_Architecture_Gold_02_Diffuse_01"));
        }

        [Test]
        public void Classes_LeaveMyrtanasSharedStemsAlone()
        {
            // these keep their v2 CC0 classes (Faring keep, houses): the arch set must not take them over
            foreach (var s in new[] { "G3_Architecture_Bricks_03_Diffuse_01", "G3_Architecture_GroundStone_02_Diffuse_01", "G3_Architecture_Wood_04_Diffuse_01",
                                      "G3_Architecture_WallHouse_01_Diffuse_01", "G3_Architecture_Iron_01_Diffuse_01", "G3_Architecture_SandGround_01_Diffuse_01" })
                Assert.IsNull(WorldV2.ArchClassFor(s), s);
            Assert.AreEqual("polyhaven_castle_brick_07", WorldV2.LayerFor("G3_Architecture_Bricks_03_Diffuse_01").Cc0);
            Assert.AreEqual("polyhaven_coast_sand_01", WorldV2.LayerFor("G3_Architecture_SandGround_01_Diffuse_01").Cc0);
            Assert.IsFalse(WorldV2.Cc0Folders().Any(f => f.StartsWith("arch_detail_")), "detail tiles are packed by archtex.py, not worldv2_layers.py");
        }

        [Test]
        public void Classes_Arch2_WrongCc0ClassesFixed()
        {
            // WallHouse_02 is dark rubble masonry (the CC0 plaster replaced it), GroundStone_03 pebbled ground (was a wall scan)
            Assert.AreEqual("masonry", WorldV2.ArchClassFor("G3_Architecture_WallHouse_02_Diffuse_01"));
            Assert.AreEqual("floor", WorldV2.ArchClassFor("G3_Architecture_GroundStone_03_Diffuse_01"));
            Assert.AreEqual(0f, WorldV2.LayerFor("G3_Architecture_WallHouse_02_Diffuse_01").Replace);
            Assert.AreEqual("polyhaven_worn_plaster_wall", WorldV2.LayerFor("G3_Architecture_WallHouse_01_Diffuse_01").Cc0);
            Assert.AreEqual("polyhaven_stacked_stone_wall", WorldV2.LayerFor("G3_Architecture_GroundStone_02_Diffuse_01").Cc0);
        }

        [Test]
        public void Cutout_ClothOnly_ShaderClipsEveryPass()
        {
            Assert.IsTrue(WorldV2.ArchCutoutAllowed("G3_Object_Varant_Sheet_01_Diffuse_01"));
            Assert.IsTrue(WorldV2.ArchCutoutAllowed("G3_Architecture_Drapery_03_Diffuse_04"));
            Assert.IsFalse(WorldV2.ArchCutoutAllowed("G3_Architecture_SandStone_06_Diffuse_01"), "parallax classes stay opaque-only");
            Assert.IsFalse(WorldV2.ArchCutoutAllowed("G3_Architecture_Netting_01_Diffuse_01"));
            string sh = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader"));
            foreach (var pass in new[] { "\"ForwardLit\"", "\"ShadowCaster\"", "\"DepthOnly\"", "\"DepthNormals\"" })
            {
                int a = sh.IndexOf("Name " + pass, System.StringComparison.Ordinal);
                int b = sh.IndexOf("ENDHLSL", a, System.StringComparison.Ordinal);
                Assert.Greater(a, 0, pass);
                StringAssert.Contains("_ALPHATEST_ON", sh.Substring(a, b - a), pass);
            }
            string bld = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Editor/Remaster/G3WorldV2Builder.cs"));
            StringAssert.Contains("EnableKeyword(\"_ALPHATEST_ON\")", bld);
            StringAssert.Contains("RenderQueue.AlphaTest", bld);
        }

        [Test]
        public void Layer_KeepsG3sImage_DetailTilePerClass()
        {
            var wall = WorldV2.LayerFor("G3_Architecture_SandStone_06_Diffuse_01");
            Assert.AreEqual("arch", wall.Kind);
            Assert.AreEqual(0f, wall.Replace, "G3's image stays the surface");
            Assert.AreEqual("arch_detail_stone", wall.Cc0);
            Assert.IsTrue(wall.Parallax);
            Assert.IsFalse(wall.Rock, "not a rock layer (part Buildings, no moss)");
            var tent = WorldV2.LayerFor("G3_Desert_Tent_01_Diffuse_01");
            Assert.AreEqual("arch_detail_cloth", tent.Cc0);
            Assert.IsFalse(tent.Parallax);
            Assert.AreEqual("arch_detail_plaster", WorldV2.LayerFor("G3_Architecture_RoomWalls_01_Diffuse_01").Cc0);
            Assert.AreEqual("arch_detail_wood", WorldV2.LayerFor("G3_Objects_Orient_Wood_V_01_Diffuse_01").Cc0);
        }

        [Test]
        public void Vector_DepthDustClassTranslucency()
        {
            var m = WorldV2.ArchVector("masonry");
            var c = WorldV2.ArchVector("cloth");
            var f = WorldV2.ArchVector("floor");
            Assert.Greater(m[0], f[0]);
            Assert.AreEqual(0f, c[0], "no parallax on cloth");
            Assert.Greater(c[3], 0f, "cloth lets light through");
            Assert.AreEqual(0f, m[3]);
            Assert.AreEqual(1f, m[2]);
            Assert.AreEqual(6f, c[2]);
            CollectionAssert.AreEqual(new float[4], WorldV2.ArchVector(null));
            foreach (var cls in WorldV2.ArchClasses)
            {
                var v = WorldV2.ArchVector(cls);
                Assert.That(v[0], Is.InRange(0f, 0.05f), cls);
                Assert.That(v[1], Is.InRange(0f, 1f), cls);
            }
        }

        [Test]
        public void Global_OffWithV2OrArch()
        {
            CollectionAssert.AreEqual(new[] { 1f, 1f, 1f, 1f }, WorldV2.ArchGlobal);
            WorldRemasterLook.Current = new WorldLookSettings { arch = false };
            CollectionAssert.AreEqual(new float[4], WorldV2.ArchGlobal);
            WorldRemasterLook.Current = new WorldLookSettings { v2 = false };
            CollectionAssert.AreEqual(new float[4], WorldV2.ArchGlobal);
        }

        [Test]
        public void Classes_MyrtanaNordmar_Arch3()
        {
            Assert.AreEqual("wood", WorldV2.ArchClassFor("G3_Architecture_Snowwood_02_Diffuse_01"));
            Assert.AreEqual("wood", WorldV2.ArchClassFor("G3_Architecture_Ornamente_01_Diffuse_01"));
            Assert.AreEqual("floor", WorldV2.ArchClassFor("G3_Architecture_stairHouse_01_Diffuse_01"));
            Assert.AreEqual("ice", WorldV2.ArchClassFor("G3_Object_IceWall_01_Diffuse_01"));
            Assert.AreEqual("thatch", WorldV2.ArchClassFor("G3_Architecture_Prop_strawbale_07_Diffuse_01"));
            Assert.AreEqual("wood", WorldV2.ArchClassFor("G3_Architecture_Prop_Torch_01_Diffuse_01"));
            Assert.AreEqual("cloth", WorldV2.ArchClassFor("G3_Objects_Pelt_03_Diffuse_01"));
            Assert.AreEqual("metal", WorldV2.ArchClassFor("G3_Objects_Barrelmetal_01_Diffuse_01"));
            Assert.AreEqual("arch_detail_stone", WorldV2.LayerFor("G3_Object_IceWall_01_Diffuse_01").Cc0);
            Assert.AreEqual("arch_detail_wood", WorldV2.LayerFor("G3_Architecture_Prop_strawbale_07_Diffuse_01").Cc0);
            Assert.AreEqual(8f, WorldV2.ArchVector("ice")[2]);
            Assert.AreEqual(9f, WorldV2.ArchVector("thatch")[2]);
        }

        [Test]
        public void Weathering_OnlyForSharedCc0Stems()
        {
            Assert.AreEqual(1f, WorldV2.ArchWeatherFor("G3_Architecture_Bricks_04_Diffuse_01"));
            Assert.AreEqual(0.6f, WorldV2.ArchWeatherFor("G3_Architecture_Wood_04_Diffuse_01"));
            Assert.AreEqual(0.8f, WorldV2.ArchWeatherFor("G3_Architecture_Roofshingle_01_Diffuse_01"));
            Assert.AreEqual(0f, WorldV2.ArchWeatherFor("G3_Architecture_Snowwood_02_Diffuse_01"), "arch stems carry it in ArchVector");
            Assert.AreEqual(0f, WorldV2.ArchWeatherFor("G3_Nature_Ground_Grass_01_Diffuse_01"));
            Assert.AreEqual(0f, WorldV2.ArchWeatherFor("G3_Nature_Wall_Stone_07_Diffuse_02"));
            Assert.AreEqual(0f, WorldV2.ArchWeatherFor(null));
            CollectionAssert.AreEqual(new[] { 1f, 1f, 0f, 0f }, WorldV2.ArchGlobal2);
            WorldRemasterLook.Current = new WorldLookSettings { arch = false };
            CollectionAssert.AreEqual(new float[4], WorldV2.ArchGlobal2);
            string sh = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader"));
            StringAssert.Contains("_G3V2Arch2.x", sh, "Myrtana moss/dirt term");
            StringAssert.Contains("_G3V2Arch2.y", sh, "Nordmar frost term");
            StringAssert.Contains("(1 - snowW)", sh.Substring(sh.IndexOf("_G3V2Arch2.y", System.StringComparison.Ordinal)), "frost fades under G3SnowV2's snow");
            StringAssert.Contains("ArchWeatherFor", File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Editor/Remaster/G3WorldV2Builder.cs")));
        }

        [Test]
        public void PythonPipeline_SamePrefixesAndClasses()
        {
            // archtex.py bakes per class (height, de-light, smoothness); the C# table picks the runtime class: keep them in step
            string py = File.ReadAllText(Path.Combine(Repo(), "Tools/Remaster/archtex.py"));
            int a = py.IndexOf("PREFIXES = [", System.StringComparison.Ordinal), b = py.IndexOf("\n]", a, System.StringComparison.Ordinal);
            var rows = Regex.Matches(py.Substring(a, b - a), @"\('([^']+)', '([^']+)'\)").Select(x => (x.Groups[1].Value, x.Groups[2].Value)).ToList();
            Assert.Greater(rows.Count, 20);
            foreach (var (prefix, cls) in rows)
                Assert.AreEqual(cls, WorldV2.ArchClassFor(prefix + "_01_Diffuse_01"), prefix);
        }

        [Test]
        public void Shader_And_Builder_CarryTheVector()
        {
            string sh = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.shader"));
            StringAssert.Contains("_V2Arch(\"", sh);
            StringAssert.Contains("float4 _V2Arch;", sh);
            string b = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Editor/Remaster/G3WorldV2Builder.cs"));
            StringAssert.Contains("SetVector(\"_V2Arch\"", b);
        }
    }
}
