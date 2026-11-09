using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using G3.Formats.Remaster;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>World remaster r2 (Docs/remaster-world.md section 8): the JSON look knobs, the gentler aerial perspective and W1 (bark).</summary>
    [TestFixture]
    public class WorldRemasterR2Tests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static string JsonPath([CallerFilePath] string self = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../../Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));

        [Test]
        public void ShippedJson_EqualsCodeDefaults_AndKnowsEveryField()
        {
            string text = File.ReadAllText(JsonPath());
            var parsed = JsonSerializer.Deserialize<WorldLookSettings>(text, new JsonSerializerOptions { IncludeFields = true })!;
            var keys = new HashSet<string>();
            using (var doc = JsonDocument.Parse(text))
                foreach (var p in doc.RootElement.EnumerateObject()) keys.Add(p.Name);
            var def = WorldLookSettings.Default;
            foreach (var f in typeof(WorldLookSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.IsTrue(keys.Contains(f.Name), $"G3WorldLook.json lacks '{f.Name}'");
                Assert.AreEqual(f.GetValue(def), f.GetValue(parsed), $"'{f.Name}': JSON and code default differ");
                keys.Remove(f.Name);
            }
            Assert.IsEmpty(keys, "unknown keys in G3WorldLook.json: " + string.Join(", ", keys));
        }

        [Test]
        public void Defaults_DoNotLiftBlacksOrGreyTheDistance()
        {
            var d = WorldLookSettings.Default;
            Assert.LessOrEqual(d.gradeShadows, 0f, "grading must not lift the blacks");
            Assert.LessOrEqual(d.gradeGain, 0.02f);
            Assert.AreEqual(0f, d.gradeSaturation, "grade-1: neutral (the global saturation / vibrance knobs replace the +10)");
            Assert.LessOrEqual(d.aerialMax, 0.08f, "r1's 0.15 cap washed the vista out");
            Assert.LessOrEqual(d.aerialLift, 0.5f, "most of the haze is a tint, not added light");
            // the black level a haze of colour f lifts at 300 m: amount * lift * f; keep it under ~1% of white
            Assert.Less(WorldRemasterLook.AerialAmount(300, 15, 15) * d.aerialLift * 0.6f, 0.01f, "black lift at 300 m (fog ~0.6) under 1% of white");
        }

        [Test]
        public void Aerial_FromHighGround_NoDenserBelowReference()
        {
            // r1: a camera 100 m up looking down had h1 = -95 -> exp(+1.1): maximum haze over the whole valley
            float down = WorldRemasterLook.AerialAmount(300, 15, -95);
            float level = WorldRemasterLook.AerialAmount(300, 15, 15);
            Assert.LessOrEqual(down, level * 1.25f + 1e-6f);
            WorldRemasterLook.Current = new WorldLookSettings { aerial = false };
            Assert.AreEqual(0f, WorldRemasterLook.AerialAmount(300, 15, 15));
        }

        [Test]
        public void Clarity_RampsWithDistance()
        {
            Assert.AreEqual(0f, WorldRemasterLook.ClarityWeight(5));
            Assert.Greater(WorldRemasterLook.ClarityWeight(80), 0f);
            Assert.AreEqual(1f, WorldRemasterLook.ClarityWeight(1000), 1e-6);
            WorldRemasterLook.Current = new WorldLookSettings { clarity = false };
            Assert.AreEqual(0f, WorldRemasterLook.ClarityWeight(1000));
        }

        [Test]
        public void TuneVectors_AreNeutralAtOne_AndClamped()
        {
            WorldRemasterLook.Current = new WorldLookSettings
            {
                macro = 1, antiTile = 1, meso = 1, triplanar = 1, normal = 1, occlusion = 1, cavity = 0, antiTileContrast = 0,
                mesoFadeStart = 20, mesoFadeEnd = 60, bark = 1, translucency = 1, translucencyWrap = 0, foliageSheen = 0,
            };
            foreach (var v in new[] { WorldRemasterLook.DetailTune, WorldRemasterLook.SurfaceTune, WorldRemasterLook.FadeTune, WorldRemasterLook.FoliageTune })
                CollectionAssert.AreEqual(new float[4], v, "unset shader globals (0) must mean 'as baked'");
            WorldRemasterLook.Current = new WorldLookSettings { aerialMax = 9, normal = -3, clarityStart = 200, clarityFull = 50, mesoFadeEnd = 1 };
            var c = WorldRemasterLook.Current;
            Assert.AreEqual(1f, c.aerialMax);
            Assert.AreEqual(0f, c.normal);
            Assert.Greater(c.clarityFull, c.clarityStart);
            Assert.Greater(c.mesoFadeEnd, c.mesoFadeStart);
            WorldRemasterLook.Current = new WorldLookSettings { clouds = false };
            Assert.AreEqual(0f, WorldRemasterLook.Sky[0], "clouds off = amount 0");
        }

        [Test]
        public void W1_SpeedTreeBark_GetsDetail_LeavesAndFrondsDoNot()
        {
            static WorldMaterialInfo Tree(string name, int mode, bool clip) => new WorldMaterialInfo
            {
                Name = name, Shader = "G3/SpeedTree", BaseMap = name + "_Diffuse_01", Opaque = false, AlphaClip = clip, TreeMode = mode,
            };
            Assert.IsTrue(WorldRemasterPlan.IsBark(Tree("Bark_G3_SpT_Bark_DouglasFirBark_01", 0, true)), "runner-29: logged 'macro 0 ...'");
            Assert.IsTrue(WorldRemasterPlan.IsBark(Tree("Pine_Trunk", 0, false)), "unclipped mesh = bark");
            Assert.IsFalse(WorldRemasterPlan.IsBark(Tree("Pine_Frond", 0, true)));
            Assert.IsFalse(WorldRemasterPlan.IsBark(Tree("Pine_Leaf", 1, true)));
            Assert.IsFalse(WorldRemasterPlan.IsBark(Tree("Pine_Bark_Billboard", 2, true)));
            Assert.IsFalse(WorldRemasterPlan.IsBark(new WorldMaterialInfo { Name = "Bark_wall", Shader = "G3/LitBlend" }));
        }

        [Test]
        public void TreesWithoutMaps_GetTermsOnlyCopy_OthersKeep()
        {
            Dictionary<string, string> none(string _) => null;
            var bark = new WorldMaterialInfo { Name = "Oak_Bark", Shader = "G3/SpeedTree", BaseMap = "x", Opaque = false, AlphaClip = true };
            var plan = WorldRemasterPlan.For(bark, none);
            Assert.AreEqual(WorldRemasterMode.Textures, plan.Mode);
            Assert.IsEmpty(plan.Maps);
            Assert.IsTrue(WorldRemasterPlan.Detail(bark, plan).BarkDetail);
            Assert.AreEqual(WorldRemasterMode.Textures, WorldRemasterPlan.For(new WorldMaterialInfo { Shader = "G3/Vegetation", BaseMap = "g", Opaque = false }, none).Mode);
            Assert.AreEqual(WorldRemasterMode.Keep, WorldRemasterPlan.For(new WorldMaterialInfo { Shader = "G3/LitBlend", BaseMap = "g" }, none).Mode);
        }
    }
}
