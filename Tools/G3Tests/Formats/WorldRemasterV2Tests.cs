using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>World remaster v2 (Docs/remaster-world.md section 10): the engine-free maths of the new look.</summary>
    [TestFixture]
    public class WorldRemasterV2Tests
    {
        [TearDown]
        public void Reset() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static string Repo([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../.."));

        static WorldLookSettings AllOff() => new WorldLookSettings { v2 = false };

        [Test]
        public void AiLayer_Kind3_TriplanarOnlyOnTerrainCells()
        {
            // 10.18: kind 3 must stay "rock" for the steep push and the part classifier (z > 1.5) and above every CC0 kind
            CollectionAssert.AreEqual(new[] { WorldV2.AiNormalScale, WorldV2.AiSaturation, 3f, 1f }, WorldV2.AiLayerVector(true));
            CollectionAssert.AreEqual(new[] { WorldV2.AiNormalScale, WorldV2.AiSaturation, 3f, 0f }, WorldV2.AiLayerVector(false));
            Assert.Greater(WorldV2.AiKind, 2.5f);
            Assert.AreEqual("ai_G3_Nature_Wall_Stone_02_Diffuse_01", WorldV2.AiFolder("G3_Nature_Wall_Stone_02_Diffuse_01"));
            Assert.IsNull(WorldV2.AiFolder(""));
            Assert.IsFalse(WorldV2.Cc0Folders().Any(f => f.StartsWith(WorldV2.AiPrefix)));
        }

        [Test]
        public void AiLayer_Pass4_MeshUvUnlessStretched_GroundColourInItsOwnGlobal()
        {
            // conformal G3 UV (square texels, any scale/rotation): mesh UV only
            Assert.AreEqual(0f, WorldV2.AiTriplanarWeight(new[] { 2f, 0, 0 }, new[] { 0, 0, 2f }));
            Assert.AreEqual(0f, WorldV2.AiTriplanarWeight(new[] { 0.7f, 0.7f, 0 }, new[] { -0.7f, 0.7f, 0 }));
            // planar XZ UV on a near-vertical cliff: v runs 5x longer than u -> triplanar
            Assert.AreEqual(1f, WorldV2.AiTriplanarWeight(new[] { 1f, 0, 0 }, new[] { 0, 5f, 0.2f }));
            // degenerate (UV constant along one direction) -> triplanar; in between a smooth blend
            Assert.AreEqual(1f, WorldV2.AiTriplanarWeight(new[] { 1f, 0, 0 }, new[] { 2f, 0, 0 }));
            float mid = WorldV2.AiTriplanarWeight(new[] { 1f, 0, 0 }, new[] { 0, 0, 2.75f });
            Assert.That(mid, Is.GreaterThan(0.3f).And.LessThan(0.7f));
            Assert.AreEqual(2.1f, WorldV2.AiSaturation);
            // ground hue lock / saturation moved to _G3V2Color2 (grass-v3 owns _G3V2Color.zw: G3 detail, G3 fine)
            WorldRemasterLook.Current = new WorldLookSettings();
            CollectionAssert.AreEqual(new[] { 1f, 1.5f, 1f, 3f }, WorldV2.Color2Global);
            Assert.AreEqual(1f, WorldV2.ColorGlobal[2]);
        }

        [Test]
        public void AiLayer_Pass6_SaturationPerRegion_Eased()
        {
            WorldRemasterLook.Current = new WorldLookSettings();
            Assert.AreEqual(1.4f, WorldV2.AiSaturationFor("Myrtana"));
            Assert.AreEqual(1.4f, WorldV2.AiSaturationFor(null));   // outside Play / before the first cell: Myrtana
            Assert.Less(WorldV2.AiSaturationFor("Nordmar"), WorldV2.AiSaturationFor("Myrtana"));   // grey rock, not yellow-olive
            Assert.AreEqual(WorldRemasterLook.Current.v2AiSatVarant, WorldV2.AiSaturationFor("varant"));
            Assert.AreEqual(1.3f, WorldV2.AiSaturationStep(0, 1.3f, 0.016f));          // first frame: the target at once
            float s = WorldV2.AiSaturationStep(2.1f, 1.3f, 0.5f);
            Assert.That(s, Is.LessThan(2.1f).And.GreaterThan(1.3f));                     // eased at the border
            CollectionAssert.AreEqual(new[] { 1.3f, 0f, 0f, 0f }, WorldV2.AiGlobal(1.3f));
            WorldRemasterLook.Current = AllOff();
            CollectionAssert.AreEqual(new float[4], WorldV2.AiGlobal(1.3f));
        }

        [Test]
        public void Blend_Pass6_SandSoftAndG3Own_OthersUnchanged()
        {
            var sand = WorldV2.LayerFor("G3_Desert_Sand_01_Diffuse_01");
            var rock = WorldV2.LayerFor("G3_Desert_Rock_V_05_Diffuse_01");
            var grass = WorldV2.LayerFor("G3_Nature_Ground_Grass_01_Diffuse_01");
            Assert.IsTrue(WorldV2.IsSand(sand));
            Assert.IsFalse(WorldV2.IsSand(rock));
            Assert.IsFalse(WorldV2.IsSand(grass));
            CollectionAssert.AreEqual(new[] { WorldV2.SandBlendBand, WorldV2.SandSteepPush, 1f, WorldV2.SandCc0Grain }, WorldV2.BlendVector(sand, rock));
            CollectionAssert.AreEqual(new[] { WorldV2.SandBlendBand, WorldV2.SandSteepPush, 1f, WorldV2.SandCc0Grain }, WorldV2.BlendVector(rock, sand));
            // the shader default (0, 1, 0, 1) = the global band, the full steep push, global G3 fine, full CC0 grain
            CollectionAssert.AreEqual(new[] { 0f, 1f, 0f, 1f }, WorldV2.BlendVector(grass, rock));
            CollectionAssert.AreEqual(new[] { 0f, 1f, 0f, 1f }, WorldV2.BlendVector(null, null));
            Assert.Greater(WorldV2.SandBlendBand, WorldRemasterLook.Current.v2HeightBlend);
        }

        [Test]
        public void V2Off_EveryGlobalIsNeutral()
        {
            WorldRemasterLook.Current = AllOff();
            CollectionAssert.AreEqual(new float[4], WorldV2.SurfaceGlobal);
            CollectionAssert.AreEqual(new float[4], WorldV2.Surface2Global);
            CollectionAssert.AreEqual(new float[4], WorldV2.WeatherGlobal(1));
            CollectionAssert.AreEqual(new float[4], WorldV2.CloudsGlobal);
            CollectionAssert.AreEqual(new float[4], WorldV2.FoliageGlobal);
            CollectionAssert.AreEqual(new float[4], WorldV2.ShoulderParams);
            CollectionAssert.AreEqual(new float[4], WorldV2.ColorGlobal);
            CollectionAssert.AreEqual(new float[4], WorldV2.Color2Global);
            Assert.AreEqual((1f, 1f, 0f), WorldV2.LightBalance(0));
            Assert.AreEqual(0f, WorldV2.FogDensity(300));
            Assert.AreEqual(0.6f, WorldV2.ShadowStrength(0.6f, 0));
            CollectionAssert.AreEqual(new[] { 0.5f, 0.4f, 0.3f }, WorldV2.SunColor(0.5f, 0.4f, 0.3f, 10));
            Assert.AreEqual(0, WorldV2.BudgetMs().Count);
            Assert.AreEqual(12.4f, WorldV2.EstimatedFrameMs(), 1e-4);
        }

        [Test]
        public void Defaults_V2IsOn_AndEveryFeatureHasAToggle()
        {
            var d = WorldLookSettings.Default;
            Assert.IsTrue(d.v2);
            var toggles = typeof(WorldLookSettings).GetFields().Where(f => f.Name.StartsWith("v2") && f.FieldType == typeof(bool)).Select(f => f.Name).ToList();
            foreach (var t in new[] { "v2Sun", "v2Shadows", "v2Ssao", "v2Fog", "v2Shafts", "v2Clouds", "v2Shoulder", "v2ProbeGI", "v2Terrain",
                                      "v2Surfaces", "v2Pbr", "v2Stochastic", "v2TerrainTriplanar", "v2RockTriplanar", "v2Parallax", "v2Moss",
                                      "v2BaseDirt", "v2EdgeWear", "v2Wetness", "v2Grass", "v2GrassShadows", "v2Foliage", "v2TreeReplace" })
                CollectionAssert.Contains(toggles, t);
            Assert.IsFalse(d.v2ProbeGI, "APV needs an editor bake: off by default");
            Assert.IsFalse(d.v2TreeReplace, "replacement trees are an option, G3's SpeedTrees stay the default");
        }

        [Test]
        public void LightBalance_ContrastInClearWeather_G3SoftLightWhenOvercast()
        {
            var clear = WorldV2.LightBalance(0);
            Assert.AreEqual(1.45f, clear.sun, 1e-5);
            Assert.AreEqual(1.1f, clear.ambient, 1e-5);   // fix-169: 0.9 -> 1.1 (forest under canopy too dark at noon)
            Assert.GreaterOrEqual(clear.fill, 0f);   // fix-169: no cool fill when the ambient is raised
            var over = WorldV2.LightBalance(1);
            Assert.AreEqual(1f, over.sun, 1e-5);
            Assert.AreEqual(1f, over.ambient, 1e-5);
            Assert.AreEqual(0f, over.fill, 1e-5);
            float prev = float.MaxValue;
            for (float o = 0; o <= 1.0001f; o += 0.1f)
            {
                var b = WorldV2.LightBalance(o);
                Assert.LessOrEqual(b.sun, prev + 1e-5f, "sun boost eases out monotonically with overcast");
                prev = b.sun;
            }
            Assert.AreEqual(0.75f, WorldV2.ShadowStrength(0.6f, 0), 1e-5);
            Assert.AreEqual(0.6f, WorldV2.ShadowStrength(0.6f, 1), 1e-5);
        }

        [Test]
        public void AirMass_AndSunColour()
        {
            Assert.AreEqual(1.0, WorldV2.AirMass(90), 1e-3);
            Assert.AreEqual(2.0, WorldV2.AirMass(30), 0.01);
            Assert.Greater(WorldV2.AirMass(0), 30);
            var noon = WorldV2.AirMassTransmittance(90);
            foreach (var v in noon) Assert.AreEqual(1f, v, 1e-5);
            var low = WorldV2.AirMassTransmittance(4);
            Assert.Greater(low[0], low[1]);
            Assert.Greater(low[1], low[2]);
            // hue towards warm at low sun, luminance kept
            var c = WorldV2.SunColor(0.8f, 0.8f, 0.8f, 4);
            float l0 = 0.2126f * 0.8f + 0.7152f * 0.8f + 0.0722f * 0.8f, l1 = 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];
            Assert.AreEqual(l0, l1, 1e-4);
            Assert.Greater(c[0], c[2]);
            var n = WorldV2.SunColor(0.8f, 0.8f, 0.8f, 60);
            Assert.AreEqual(n[0], n[2], 0.06f, "high sun stays near-white");
        }

        [Test]
        public void CascadeSplits_IncreasingAndPractical()
        {
            var uni = WorldV2.CascadeSplits(0.3f, 150, 4, 0);
            Assert.AreEqual(0.25f, uni[0], 0.01f);
            Assert.AreEqual(0.5f, uni[1], 0.01f);
            Assert.AreEqual(0.75f, uni[2], 0.01f);
            var s = WorldV2.CascadeSplits(0.3f, 150, 4, 0.78f);
            Assert.AreEqual(3, s.Length);
            Assert.That(s[0] > 0 && s[0] < s[1] && s[1] < s[2] && s[2] < 1);
            Assert.Less(s[0], 0.1f, "first cascade small: crisp near shadows");
            Assert.AreEqual(1, WorldV2.CascadeSplits(0.3f, 150, 2, 0.5f).Length);
            Assert.AreEqual(0, WorldV2.CascadeSplits(0.3f, 150, 1, 0.5f).Length);
        }

        [Test]
        public void Fog_HeightAndWeather()
        {
            Assert.AreEqual(0f, WorldV2.Fogginess(1500));
            Assert.AreEqual(1f, WorldV2.Fogginess(100));
            Assert.Greater(WorldV2.FogDensity(200), WorldV2.FogDensity(1500));
            float d = 0.001f;
            float near = WorldV2.FogOpticalDepth(50, 0, 0, d, 0.035f), far = WorldV2.FogOpticalDepth(500, 0, 0, d, 0.035f);
            Assert.Greater(far, near);
            Assert.AreEqual(0.5f, far, 1e-4, "level ray at the base: density x distance");
            Assert.Less(WorldV2.FogOpticalDepth(500, 100, 100, d, 0.035f), far * 0.05f, "100 m above the base: almost clear");
            Assert.AreEqual(far, WorldV2.FogOpticalDepth(500, -50, -50, d, 0.035f), 1e-5, "below the base the density is capped");
            Assert.LessOrEqual(WorldV2.FogAmount(100000, 0, 0, 0.01f), WorldLookSettings.Default.v2FogMax + 1e-6f);
            // HG phase integrates to 1 over the sphere
            double sum = 0; int n = 20000;
            for (int i = 0; i < n; i++) { double mu = -1 + 2.0 * (i + 0.5) / n; sum += WorldV2.PhaseHG((float)mu, 0.62f) * 2 * Math.PI * (2.0 / n); }
            Assert.AreEqual(1.0, sum, 0.01);
        }

        [Test]
        public void Shoulder_IdentityBelowKnee_ContinuousMonotonic_WhiteIsOne()
        {
            float knee = 0.72f, white = 3f;
            Assert.AreEqual(0.5f, WorldV2.Shoulder(0.5f, knee, white));
            Assert.AreEqual(knee, WorldV2.Shoulder(knee + 1e-4f, knee, white), 2e-4);
            Assert.AreEqual(1f, WorldV2.Shoulder(white, knee, white), 1e-4);
            Assert.AreEqual(1f, WorldV2.Shoulder(50, knee, white), 1e-6);
            float prev = 0;
            for (float x = 0; x < 4; x += 0.01f)
            {
                float y = WorldV2.Shoulder(x, knee, white);
                Assert.GreaterOrEqual(y, prev - 1e-6f);
                Assert.LessOrEqual(y, 1f);
                prev = y;
            }
            // slope at the knee is ~1 (no visible kink)
            float slope = (WorldV2.Shoulder(knee + 0.01f, knee, white) - knee) / 0.01f;
            Assert.AreEqual(1f, slope, 0.05f);
        }

        [Test]
        public void MaterialClasses_FaringImages()
        {
            Assert.AreEqual("polyhaven_leafy_grass", WorldV2.LayerFor("G3_Nature_Ground_Grass_01_Diffuse_01").Cc0);
            Assert.AreEqual("polyhaven_brown_mud_02", WorldV2.LayerFor("G3_Nature_Earthground_01_Diffuse_02").Cc0);
            var rock = WorldV2.LayerFor("G3_Nature_Wall_Stone_12_Diffuse_01");
            Assert.IsTrue(rock.Rock);
            Assert.AreEqual(1f, rock.Replace, "cliffs: full CC0 rock (owner: the speckle noise must go)");
            Assert.IsTrue(WorldV2.LayerFor("G3_Nature_Wall_Stone_02_Diffuse_01").Rock);
            var bricks = WorldV2.LayerFor("G3_Architecture_Bricks_03_Diffuse_01");
            Assert.AreEqual("wall", bricks.Kind);
            Assert.AreEqual(1f, bricks.Replace, "tower masonry: full CC0 bricks with parallax");
            Assert.IsTrue(bricks.Parallax);
            Assert.AreEqual("polyhaven_worn_plaster_wall", WorldV2.LayerFor("G3_Architecture_WallHouse_01_Diffuse_01").Cc0);
            var wood = WorldV2.LayerFor("G3_Architecture_Wood_04_Diffuse_01");
            Assert.Less(wood.Replace, 1f, "planks keep G3's layout");
            Assert.AreEqual("polyhaven_brown_planks_03", WorldV2.LayerFor("G3_Architecture_Wood_07_Diffuse_01").Cc0);
            Assert.AreEqual("polyhaven_thatch_roof_angled", WorldV2.LayerFor("G3_Architecture_Strawroof_01_Diffuse_01").Cc0);
            Assert.IsNull(WorldV2.LayerFor("G3_Object_Barrel_01_Diffuse_01"));
            Assert.IsNull(WorldV2.LayerFor("G3_Nature_Plant_Herbs_Composit_01_Diffuse_S1"));
            Assert.IsNull(WorldV2.LayerFor(null));
            Assert.AreEqual(1.2f, WorldV2.UvScale(2.4f, 2f), 1e-5);
        }

        [Test]
        public void RockColour_LockedToG3Hue_Runner30()
        {
            // orange sandstone scan (cliff_side mean) on G3's grey-green rock
            var cc0Mean = new[] { 0.213f, 0.092f, 0.034f };
            var g3Mean = new[] { 0.10f, 0.11f, 0.09f };
            var texel = new[] { 0.30f, 0.13f, 0.05f };   // a bright orange texel
            var locked = WorldV2.MatchColor(texel, cc0Mean, g3Mean, 0.85f, WorldLookSettings.Default.v2RockHueLock);
            Assert.Greater(locked[1], locked[0], "G3's green-grey hue, not orange");
            float k = locked[1] / g3Mean[1];
            Assert.AreEqual(k, locked[0] / g3Mean[0], 1e-4, "hue/sat of G3's mean exactly (lock 1)");
            Assert.Greater(k, 1f, "the texel's brightness detail survives");
            // the r0-v2 per-channel ratio alone (lock 0) keeps the orange (blue ratio clamped at 5)
            var ratio = WorldV2.MatchColor(texel, cc0Mean, g3Mean, 0.85f, 0);
            Assert.Greater(ratio[0], ratio[2]);
            Assert.AreEqual(1f, WorldLookSettings.Default.v2RockHueLock);
            Assert.AreEqual("polyhaven_rock_face", WorldV2.LayerFor("G3_Nature_Wall_Stone_02_Diffuse_01").Cc0, "cliff_side strata read as stripes");
        }

        [Test]
        public void Runner31_Lattice_DebugModes_FarFade()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(0, d.v2DebugMode);
            Assert.AreEqual(60f, d.v2SsaoFalloff, "160 put SSAO on the far cliffs; demo_nogrid 60");
            Assert.AreEqual(1f, WorldV2.DetailAt(10), 1e-6, "full scan detail near");
            Assert.AreEqual(d.v2DetailFar, WorldV2.DetailAt(500), 1e-5, "tiled detail mostly gone at range");
            Assert.Less(WorldV2.DetailAt(80), WorldV2.DetailAt(40));
            CollectionAssert.AreEqual(new float[4], WorldV2.DebugGlobal);
            WorldRemasterLook.Current = new WorldLookSettings { v2DebugMode = 5 };
            Assert.AreEqual(5f, WorldV2.DebugGlobal[0]);
            WorldRemasterLook.Current = new WorldLookSettings { v2Debug = true };
            Assert.AreEqual(1f, WorldV2.DebugGlobal[0], "old kinds flag still works");
            WorldRemasterLook.Current = new WorldLookSettings { v2DebugMode = 42 };
            Assert.AreEqual(9, WorldRemasterLook.Current.v2DebugMode, "clamped");
            // the demo fallback preset is a valid partial JSON of known keys
            string preset = File.ReadAllText(Path.Combine(Repo(), "Tools/Remaster/v2_presets/demo_nogrid.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(preset);
            foreach (var p in doc.RootElement.EnumerateObject()) Assert.IsNotNull(typeof(WorldLookSettings).GetField(p.Name), p.Name);
        }

        [Test]
        public void Runner31_ForestBudget_Defaults()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(90f, d.v2ShadowDistance);
            Assert.AreEqual(2048, d.v2ShadowResolution);
            Assert.AreEqual(1.3f, d.v2GrassDensity);
            Assert.AreEqual(1.0f, d.v2GrassDistance);
            Assert.LessOrEqual(d.v2FoliageAO, 0.25f, "runner-31: foliage near-black");
            Assert.GreaterOrEqual(d.v2AmbientScale, 0.85f, "runner-31: forest crushed at 0.62");
            Assert.Less(WorldV2.EstimatedFrameMs(), 13f);
        }

        [Test]
        public void GrassV3_GroundAndGrassDefaults_ReachTheGlobals()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(1f, d.v2G3Detail, "G3's detail maps back on v2 ground");
            Assert.AreEqual(0.6f, d.v2G3Fine);
            Assert.AreEqual(0.12f, d.v2GroundGloss);
            Assert.AreEqual(0.45f, d.v2GrassTranslucency);
            WorldRemasterLook.Current = new WorldLookSettings();
            var c = WorldV2.ColorGlobal;
            Assert.AreEqual(1f, c[2]); Assert.AreEqual(0.6f, c[3]);
            Assert.AreEqual(0.12f, WorldV2.FarGlobal[3]);
            var cl = new WorldLookSettings { v2G3Detail = 3, v2G3Fine = -1, v2GroundGloss = 5 }.Clamped();
            Assert.AreEqual((1f, 0f, 1f), (cl.v2G3Detail, cl.v2G3Fine, cl.v2GroundGloss));
            // the shipped JSON carries the new keys
            string json = File.ReadAllText(Path.Combine(Repo(), "Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json"));
            foreach (var k in new[] { "\"v2G3Detail\"", "\"v2G3Fine\"", "\"v2GroundGloss\"" }) StringAssert.Contains(k, json);
        }

        [Test]
        public void Runner30_Defaults()
        {
            var d = WorldLookSettings.Default;
            Assert.AreEqual(0.55f, d.clarityStrength, "ab_stats 8/8 with 0.55 (7/8 with 0.45)");
            Assert.AreEqual(4, d.v2VolumetricDownsample);
            Assert.IsTrue(d.v2VolumetricTemporal);
            var b = WorldV2.BudgetMs();
            Assert.Less(b["volumetrics"], 0.3f, "quarter resolution");
            WorldRemasterLook.Current = new WorldLookSettings { v2VolumetricDownsample = 3 };
            Assert.AreEqual(2, WorldRemasterLook.Current.v2VolumetricDownsample, "only 1, 2, 4");
        }

        [Test]
        public void Cc0Folders_ArePackedByTheLayerTool()
        {
            string py = File.ReadAllText(Path.Combine(Repo(), "Tools/Remaster/worldv2_layers.py"));
            foreach (var f in WorldV2.Cc0Folders()) StringAssert.Contains($"'{f}'", py, $"worldv2_layers.py FOLDERS lacks {f}");
            StringAssert.Contains("'polyhaven_leafy_grass'", py, "moss layer");
        }

        [Test]
        public void HexWeights_SumToOne_AndSharpen()
        {
            var w = WorldV2.HexWeights(0.5f, 0.3f, 0.2f);
            Assert.AreEqual(1f, w.Sum(), 1e-5);
            Assert.Greater(w[0], 0.9f, "exponent 7 makes the nearest vertex dominate");
            var eq = WorldV2.HexWeights(1 / 3f, 1 / 3f, 1 / 3f);
            Assert.AreEqual(1 / 3f, eq[1], 1e-5);
        }

        [Test]
        public void TreeSpecies()
        {
            Assert.AreEqual("fir", WorldV2.TreeSpecies("G3_Tree_M_DouglasFir_02"));
            Assert.AreEqual("pine", WorldV2.TreeSpecies("G3_Tree_L_LongleafPine_01"));
            Assert.AreEqual("deciduous", WorldV2.TreeSpecies("G3_Tree_M_RedOak_01"));
            Assert.AreEqual("bush", WorldV2.TreeSpecies("G3_Tree_S_CarolinaBuckthorn_02"));
            Assert.AreEqual("palm", WorldV2.TreeSpecies("G3_Tree_M_CoconutPalm_01"));
            Assert.AreEqual("other", WorldV2.TreeSpecies(""));
        }

        [Test]
        public void Budget_FaringUnder16ms_WithEverythingOn()
        {
            var b = WorldV2.BudgetMs();
            Assert.Greater(b.Count, 5);
            float est = WorldV2.EstimatedFrameMs();
            Assert.LessOrEqual(est, 16f, "task budget: Faring <= ~16 ms on the RTX 3080 at 1080p");
            Assert.Greater(est, 11.0f, "main thread stays the bound (grass path saves CPU)");
            WorldRemasterLook.Current = new WorldLookSettings { v2ShaftSteps = 96, v2CloudSteps = 64, v2GrassDensity = 8, v2GrassDistance = 4 };
            Assert.Greater(WorldV2.EstimatedFrameMs(), 16f, "the budget reacts to the knobs");
        }

        [Test]
        public void Wetness_FromG3RainStates()
        {
            Assert.IsTrue(WorldV2.IsRainy("Rainy"));
            Assert.IsTrue(WorldV2.IsRainy("Storm(Nordmar)"));
            Assert.IsFalse(WorldV2.IsRainy("Snowy"));
            Assert.IsFalse(WorldV2.IsRainy("Sunny"));
            Assert.IsFalse(WorldV2.IsRainy("Sandstormy(Varant)"), "fix-239: a sandstorm does not wet");
            // fix-239: only rain that falls (G3: the state above 50 %, at (w - 0.5) * 2) wets
            Assert.AreEqual(0f, WorldV2.WetTarget("Sunny", "Rainy", 0.25f), 1e-6, "a fading rain share does not wet");
            Assert.AreEqual(0.8f, WorldV2.WetTarget("Sunny", "Rainy", 0.6f), 1e-5, "light rain (0.2) wets 80 %");
            Assert.AreEqual(1f, WorldV2.WetTarget("Rainy", "", 0), 1e-6);
            float w = 0;
            for (int i = 0; i < 60; i++) w = WorldV2.WetStep(w, 1, 1);
            Assert.Greater(w, 0.9f, "wet within a minute");
            for (int i = 0; i < 60; i++) w = WorldV2.WetStep(w, 0, 1);
            Assert.Greater(w, 0.4f, "dries slower than it wets");
            Assert.AreEqual(0f, WorldV2.WeatherGlobal(1)[0] - 1f, 1e-6);
            WorldRemasterLook.Current = new WorldLookSettings { v2Wetness = false };
            Assert.AreEqual(0f, WorldV2.WeatherGlobal(1)[0]);
        }

        [Test]
        public void Swap_ThreeStates_ConvertFromAnyCurrentState()
        {
            string o = "O", r = "R", v = "V", o2 = "O2", r2 = "R2";
            var triples = new List<(string, string, string, int)> { (o, r, v, 2), (o2, r2, null, 0) };
            var off = WorldRemasterSwap.Target(triples, false, k => true);
            Assert.AreEqual(o, off[r]); Assert.AreEqual(o, off[v]); Assert.AreEqual(o2, off[r2]);
            Assert.IsFalse(off.ContainsKey(o));
            var v2 = WorldRemasterSwap.Target(triples, true, k => k == 2);
            Assert.AreEqual(v, v2[o]); Assert.AreEqual(v, v2[r]); Assert.AreEqual(r2, v2[o2]);
            var r3 = WorldRemasterSwap.Target(triples, true, k => false);
            Assert.AreEqual(r, r3[o]); Assert.AreEqual(r, r3[v]);
            // a v2-only material (no r3 copy)
            var only = WorldRemasterSwap.Target(new List<(string, string, string, int)> { ("X", null, "XV", 1) }, true, k => false);
            Assert.AreEqual("X", only["XV"]);
        }

        [Test]
        public void GrassDensify_FlatField_DenserOnTheGround()
        {
            // 30 x 30 plants, 1 m apart, on a tilted plane y = 0.2 x + 0.1 z + 5
            var pts = new List<float>();
            for (int i = 0; i < 30; i++)
                for (int j = 0; j < 30; j++) { float x = i, z = j; pts.AddRange(new[] { x, 0.2f * x + 0.1f * z + 5, z }); }
            var xyz = pts.ToArray();
            var o = new GrassDensify.Options { Density = 2.5f, ClumpContrast = 0 };
            var a = GrassDensify.Generate(xyz, 900, o);
            Assert.AreEqual(900 * 1.5, a.Count, 900 * 0.1, "about (density - 1) copies per plant");
            foreach (var e in a)
            {
                Assert.AreEqual(0.2f * e.X + 0.1f * e.Z + 5, e.Y, 0.02f, "copies sit on the ground plane");
                Assert.That(e.Scale >= 0.72f && e.Scale <= 1.32f, "size: stands field +-20 % x own +-10 %");
                Assert.That(e.Bright >= 0.94f && e.Bright <= 1.06f);
                Assert.AreEqual(e.Anchor, e.Source, "one species: the copy is its anchor's");
                float sx = xyz[e.Source * 3], sz = xyz[e.Source * 3 + 2];
                Assert.LessOrEqual(Math.Sqrt((e.X - sx) * (e.X - sx) + (e.Z - sz) * (e.Z - sz)), o.MaxJitter + 1e-4);
            }
            var b = GrassDensify.Generate(xyz, 900, o);
            Assert.AreEqual(a.Count, b.Count);
            Assert.AreEqual(a[17].X, b[17].X, "deterministic");
            Assert.AreEqual(0, GrassDensify.Generate(xyz, 900, new GrassDensify.Options { Density = 1 }).Count);
        }

        [Test]
        public void GrassDensify_KindOf_G3Meshes()
        {
            Assert.AreEqual(GrassDensify.Kind.Grass, GrassDensify.KindOf("G3_Brush_Plant_Grass_05"));
            Assert.AreEqual(GrassDensify.Kind.Dry, GrassDensify.KindOf("G3_Brush_Plant_DryGrass_01"));
            Assert.AreEqual(GrassDensify.Kind.Dry, GrassDensify.KindOf("G3_Brush_Plant_Crops_01"));
            Assert.AreEqual(GrassDensify.Kind.Herb, GrassDensify.KindOf("G3_Brush_Plant_Herb_08"));
            Assert.AreEqual(GrassDensify.Kind.Herb, GrassDensify.KindOf("G3_Nature_Varant_SmallPlants_01"));
            Assert.AreEqual(GrassDensify.Kind.Low, GrassDensify.KindOf("G3_Brush_Plant_Fern_01"));
            Assert.AreEqual(GrassDensify.Kind.Low, GrassDensify.KindOf("G3_Brush_Plant_Forest_Underbrush_02"));
            Assert.AreEqual(GrassDensify.Kind.Low, GrassDensify.KindOf("G3_Nature_Varant_Vegetation_Bushes_01"));
            foreach (var n in new[] { "G3_Brush_Object_Stone_Small_02", "G3_Brush_Object_Stone_Varant_Small_01", "G3_Object_Branch_Small_04",
                                      "G3_Brush_Misc_Blood_Splatter_04", "G3_Brush_Plant_Roots_01", "G3_Nature_Varant_Vegetation_Roots_02", "", null })
                Assert.AreEqual(GrassDensify.Kind.None, GrassDensify.KindOf(n), n);
        }

        /// <summary>A 60 x 60 m flat meadow, 1 plant per 1.5 m: west half grass (0) + 10 % herb (1) + stones (2), east half
        /// dry grass (3) only.</summary>
        static (float[] xyz, int[] sp, GrassDensify.Kind[] kinds) Meadow()
        {
            var pts = new List<float>(); var sp = new List<int>();
            int k = 0;
            for (float x = 0; x < 60; x += 1.5f)
                for (float z = 0; z < 60; z += 1.5f, k++)
                {
                    pts.AddRange(new[] { x, 3f, z });
                    sp.Add(x >= 30 ? 3 : k % 10 == 3 ? 1 : k % 10 == 7 ? 2 : 0);
                }
            return (pts.ToArray(), sp.ToArray(), new[] { GrassDensify.Kind.Grass, GrassDensify.Kind.Herb, GrassDensify.Kind.None, GrassDensify.Kind.Dry });
        }

        [Test]
        public void GrassDensify_CopiesTheLocalMix_NeverStones()
        {
            var (xyz, sp, kinds) = Meadow();
            int n = sp.Length;
            var o = new GrassDensify.Options { Density = 2f };
            var ex = GrassDensify.Generate(xyz, n, o, sp, kinds);
            Assert.Greater(ex.Count, n / 3);
            int herbs = 0, west = 0;
            foreach (var e in ex)
            {
                int s = sp[e.Source];
                Assert.AreNotEqual(2, sp[e.Anchor], "stones are not anchors");
                Assert.AreNotEqual(2, s, "stones are never copied");
                float dx = xyz[e.Source * 3] - xyz[e.Anchor * 3], dz = xyz[e.Source * 3 + 2] - xyz[e.Anchor * 3 + 2];
                Assert.LessOrEqual(dx * dx + dz * dz, o.MixRadius * o.MixRadius + 1e-3, "species from the anchor's neighbourhood");
                if (e.X < 22) { west++; Assert.AreNotEqual(3, s, "no dry grass deep in the green half"); if (s == 1) herbs++; }
                if (e.X > 38) Assert.AreEqual(3, s, "only dry grass deep in the dry half");
            }
            // G3: herbs are 1/9 of the west half's plants; sqrt weighting lifts them a little, the colonies keep it local
            float frac = herbs / (float)west;
            Assert.That(frac > 0.13f && frac < 0.45f, $"herb share of the copies {frac:F2}");
        }

        [Test]
        public void GrassDensify_ComesInClumps_MeanKept()
        {
            var (xyz, sp, kinds) = Meadow();
            int n = sp.Length;
            var o = new GrassDensify.Options { Density = 2f };
            var ex = GrassDensify.Generate(xyz, n, o, sp, kinds);
            int plants = 0; foreach (int s in sp) if (s != 2) plants++;
            Assert.AreEqual(plants * 1.0, ex.Count, plants * 0.3, "mean about (density - 1) per plant");
            // copies per 6 m cell: some cells keep G3's own density, others get clearly more than the mean
            var cells = new Dictionary<int, int>();
            foreach (var e in ex) { int c = (int)(e.X / 6) * 100 + (int)(e.Z / 6); cells.TryGetValue(c, out int v); cells[c] = v + 1; }
            float perCell = 36 / 2.25f * 0.9f;   // ~14 plants per cell, ~1 copy each on average
            int sparse = 0, dense = 0;
            for (int cx = 0; cx < 10; cx++)
                for (int cz = 0; cz < 10; cz++)
                {
                    cells.TryGetValue(cx * 100 + cz, out int v);
                    if (v < perCell * 0.35f) sparse++;
                    if (v > perCell * 1.5f) dense++;
                }
            Assert.GreaterOrEqual(sparse, 8, "patches without copies");
            Assert.GreaterOrEqual(dense, 8, "denser patches");
            var uni = GrassDensify.Generate(xyz, n, new GrassDensify.Options { Density = 2f, ClumpContrast = 0 }, sp, kinds);
            Assert.AreEqual(plants * 1.0, uni.Count, plants * 0.05);
        }

        [Test]
        public void GrassDensify_Noise_InRangeAndSmooth()
        {
            float min = 1, max = 0;
            for (int i = 0; i < 2000; i++)
            {
                float x = i * 0.37f - 300, z = i * 0.91f - 500;
                float v = GrassDensify.Noise(x, z, 7, 5);
                min = Math.Min(min, v); max = Math.Max(max, v);
                Assert.AreEqual(v, GrassDensify.Noise(x + 0.01f, z, 7, 5), 0.01f, "smooth");
            }
            Assert.That(min >= 0 && max <= 1 && max - min > 0.6f);
        }

        [Test]
        public void GrassDensify_SkipsRoughGround_AndIsolatedPlantsStayClose()
        {
            // rough: alternating heights +-1 m
            var pts = new List<float>();
            for (int i = 0; i < 10; i++)
                for (int j = 0; j < 10; j++) pts.AddRange(new[] { (float)i, ((i + j) % 2) * 2f, (float)j });
            Assert.AreEqual(0, GrassDensify.Generate(pts.ToArray(), 100, new GrassDensify.Options { Density = 3 }).Count);
            var lone = GrassDensify.Generate(new[] { 0f, 7f, 0f }, 1, new GrassDensify.Options { Density = 3 });
            foreach (var e in lone)
            {
                Assert.AreEqual(7f, e.Y, 1e-5);
                Assert.LessOrEqual(Math.Sqrt(e.X * e.X + e.Z * e.Z), 0.26);
            }
        }

        [Test]
        public void FitPlane_Exact()
        {
            var xyz = new[] { 0f, 1f, 0f, 1f, 3f, 0f, 0f, 0f, 1f, 1f, 2f, 1f };
            Assert.IsTrue(GrassDensify.FitPlane(xyz, new List<int> { 0, 1, 2, 3 }, out float a, out float b, out float c, out float res));
            Assert.AreEqual(2f, a, 1e-4); Assert.AreEqual(-1f, b, 1e-4); Assert.AreEqual(1f, c, 1e-4); Assert.Less(res, 1e-4);
            Assert.IsFalse(GrassDensify.FitPlane(new[] { 0f, 0f, 0f, 1f, 0f, 1f, 2f, 0f, 2f }, new List<int> { 0, 1, 2 }, out _, out _, out _, out _), "collinear");
        }

        [Test]
        public void Shaders_ReadTheGlobalsTheRuntimeSets()
        {
            string root = Repo();
            string rt = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Runtime/Remaster/G3WorldV2.cs"));
            string inc = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Shaders/Remaster/G3RemasterWorldV2.hlsl"));
            string vol = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterVolumetrics.shader"));
            string sky = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Runtime/Environment/G3Sky.shader"));
            string tree = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Shaders/Trees/G3SpeedTree.hlsl"));
            string grass = File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Shaders/Remaster/G3RemasterGrassV2.shader"));
            foreach (var g in new[] { "_G3V2Surface", "_G3V2Surface2", "_G3V2Weather", "_G3V2Debug" }) { StringAssert.Contains($"\"{g}\"", rt); StringAssert.Contains(g, inc); }
            foreach (var g in new[] { "_G3V2Fog", "_G3V2Shaft", "_G3V2FogAmb", "_G3V2VolTex" }) { StringAssert.Contains($"\"{g}\"", rt); StringAssert.Contains(g, vol); }
            StringAssert.Contains("_G3V2Clouds", sky);
            StringAssert.Contains("_G3V2Foliage", tree);
            StringAssert.Contains("_G3V2Foliage", grass);
            StringAssert.Contains("Hidden/G3/Remaster/Volumetrics", vol);
            // the grade shoulder mirrors WorldV2.Shoulder
            StringAssert.Contains("_G3GradeV2", File.ReadAllText(Path.Combine(root, "Assets/G3/Unity/Runtime/Remaster/Resources/G3RemasterAtmosphere.shader")));
        }

        /// <summary>
        /// dx12-2: a render-graph render function must not change a shared material (Material.Set* acts at once, the draws
        /// it records execute later: every draw of the frame saw the last value, and with split graphics jobs a draw in
        /// flight could see the next frame's ping-pong textures). Per-pass values go through G3PassBlit's property block.
        /// </summary>
        [Test]
        public void RenderFunctions_DoNotChangeSharedMaterials()
        {
            string root = Repo();
            var files = Directory.GetFiles(Path.Combine(root, "Assets/G3/Unity/Runtime"), "*.cs", SearchOption.AllDirectories);
            int funcs = 0;
            foreach (var f in files)
            {
                string s = File.ReadAllText(f);
                for (int i = s.IndexOf("SetRenderFunc(", StringComparison.Ordinal); i >= 0; i = s.IndexOf("SetRenderFunc(", i + 1, StringComparison.Ordinal))
                {
                    // the lambda: from the call to its closing parenthesis
                    int depth = 0, j = i + "SetRenderFunc".Length;
                    for (; j < s.Length; j++)
                    {
                        if (s[j] == '(') depth++;
                        else if (s[j] == ')' && --depth == 0) break;
                    }
                    string body = s.Substring(i, Math.Min(j, s.Length - 1) - i);
                    funcs++;
                    Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(body, @"\.material\.Set\w+\(|\b_mat\.Set\w+\("),
                        $"{Path.GetFileName(f)}: a render function sets a material property:\n{body}");
                }
            }
            Assert.Greater(funcs, 10, "render functions found");
        }
    }
}
