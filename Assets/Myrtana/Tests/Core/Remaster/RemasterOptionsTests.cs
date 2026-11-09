using System.Collections.Generic;
using System.Linq;
using G3.Unity;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Options → Remaster (Docs/build.md §3): the world parts + characters, keys, migration, F7, texts, the page and its button.</summary>
    public class RemasterOptionsTests
    {
        sealed class Prefs : IRemasterPrefs
        {
            public readonly Dictionary<string, int> D = new Dictionary<string, int>();
            public bool Has(string key) => D.ContainsKey(key);
            public int Get(string key) => D.TryGetValue(key, out int v) ? v : 0;
            public void Set(string key, int value) => D[key] = value;
            public void Delete(string key) => D.Remove(key);
        }

        [Test]
        public void DemoPreset_TurnsEveryWorldPartOnAndModernOff_Once()
        {
            var p = new Prefs();
            p.Set(RemasterOptions.PrefKey(RemasterFeature.Grass), 0);
            p.Set("G3.Remaster.ModernLighting", 1);
            p.Set(RemasterOptions.PrefKey(RemasterFeature.Characters), 1);
            Assert.That(RemasterOptions.ApplyDemoPreset(p, "G3.Remaster.ModernLighting"), Is.EqualTo(7));
            foreach (var f in RemasterOptions.WorldFeatures) Assert.That(p.Get(RemasterOptions.PrefKey(f)), Is.EqualTo(1), f.ToString());
            Assert.That(p.Get("G3.Remaster.ModernLighting"), Is.EqualTo(0));
            Assert.That(p.Get(RemasterOptions.PrefKey(RemasterFeature.Characters)), Is.EqualTo(1), "characters keep their choice");
            p.Set(RemasterOptions.PrefKey(RemasterFeature.Trees), 0);   // the player's own choice afterwards
            Assert.That(RemasterOptions.ApplyDemoPreset(p, "G3.Remaster.ModernLighting"), Is.EqualTo(0));
            Assert.That(p.Get(RemasterOptions.PrefKey(RemasterFeature.Trees)), Is.EqualTo(0));
        }

        [Test]
        public void Switches_AreIndependent_AndOffByDefault()
        {
            Assert.That(RemasterOptions.Rows, Is.EqualTo(new[]
            {
                RemasterFeature.Lighting, RemasterFeature.Terrain, RemasterFeature.Grass, RemasterFeature.Rocks,
                RemasterFeature.Buildings, RemasterFeature.Trees, RemasterFeature.Characters,
            }));
            Assert.That(RemasterOptions.Rows.Length, Is.LessThanOrEqualTo(7), "the video layout has seven row places");
            var keys = RemasterOptions.Rows.Select(RemasterOptions.PrefKey).ToList();
            Assert.That(keys.Distinct().Count(), Is.EqualTo(7), "each switch is stored on its own");
            Assert.That(keys, Is.EqualTo(new[]
            {
                "G3.Remaster.Lighting", "G3.Remaster.Terrain", "G3.Remaster.Grass", "G3.Remaster.Rocks", "G3.Remaster.Buildings",
                "G3.Remaster.Trees", "G3.Remaster.Characters",
            }), "Trees and Characters keep the keys of fix-69");
            Assert.That(keys, Has.None.EqualTo(RemasterOptions.LegacyWorldKey), "the old single world key is only migrated");
            Assert.That(RemasterOptions.EnvVar(RemasterFeature.Trees), Is.EqualTo("G3_REMASTER_TREES"));
            Assert.That(RemasterOptions.EnvVar(RemasterFeature.Characters), Is.EqualTo("G3_REMASTER_CHARACTERS"));
            Assert.That(RemasterOptions.Rows.Select(RemasterOptions.EnvVar).Distinct().Count(), Is.EqualTo(7));
            Assert.That(RemasterOptions.Default, Is.False, "the faithful port is the default");
            Assert.That(keys, Has.None.EqualTo("G3.Remaster.Enabled"), "the editor's import switch (EditorPrefs) is a different thing");
        }

        [Test]
        public void WorldParts_AreSixFlags_CharactersAreNot()
        {
            Assert.That(RemasterOptions.WorldFeatures, Is.EqualTo(RemasterOptions.Rows.Where(RemasterOptions.IsWorld).ToArray()));
            Assert.That(RemasterOptions.WorldFeatures.Length, Is.EqualTo(6));
            var all = WorldParts.None;
            foreach (var f in RemasterOptions.WorldFeatures)
            {
                var p = RemasterOptions.Part(f);
                Assert.That(WorldRemasterParts.Stored((int)p), Is.EqualTo(p), f + " is exactly one flag");
                Assert.That(all & p, Is.EqualTo(WorldParts.None), f + " has its own flag");
                all |= p;
            }
            Assert.That(all, Is.EqualTo(WorldParts.All));
            Assert.That(RemasterOptions.IsWorld(RemasterFeature.Characters), Is.False);
            Assert.That(RemasterOptions.Part(RemasterFeature.Characters), Is.EqualTo(WorldParts.None));
            Assert.That(WorldRemasterParts.Stored(0), Is.Null, "0 = a set built before the split");
            Assert.That(WorldRemasterParts.Stored((int)(WorldParts.Terrain | WorldParts.Rocks)), Is.Null);
            Assert.That(WorldRemasterParts.Stored(64), Is.Null);
            Assert.That(WorldRemasterParts.Describe(WorldParts.Lighting | WorldParts.Trees), Is.EqualTo("lighting, trees"));
            Assert.That(WorldRemasterParts.Describe(WorldParts.None), Is.EqualTo("none"));
        }

        [TestCase(null, null, false, false)]
        [TestCase(null, null, true, true)]
        [TestCase("0", null, true, false)]
        [TestCase("on", null, false, true)]
        [TestCase(null, "1", false, true)]
        [TestCase("off", "1", true, false)]
        [TestCase("garbage", "false", true, false)]
        [TestCase("", "", true, true)]
        public void Resolve_OwnEnvThenSharedThenStored(string own, string shared, bool stored, bool expected) =>
            Assert.That(RemasterOptions.Resolve(own, shared, stored), Is.EqualTo(expected));

        [Test]
        public void EnvChain_OwnThenWorldThenShared()
        {
            Assert.That(RemasterOptions.EnvChain(RemasterFeature.Rocks), Is.EqualTo(new[]
                { "G3_REMASTER_ROCKS", WorldRemasterSwitch.EnvVar, RemasterOptions.SharedEnvVar, WorldRemasterSwitch.SharedEnvVar }));
            Assert.That(RemasterOptions.EnvChain(RemasterFeature.Characters), Is.EqualTo(new[] { "G3_REMASTER_CHARACTERS", RemasterOptions.SharedEnvVar }),
                        "G3_REMASTER_WORLD does not touch the characters");
            Assert.That(RemasterOptions.Resolve(new string[] { null, "1", null, null }, false), Is.True, "G3_REMASTER_WORLD=1 forces every world part");
            Assert.That(RemasterOptions.Resolve(new[] { "0", "1", null, null }, false), Is.False, "the part's own variable wins");
            Assert.That(RemasterOptions.Resolve(new[] { "x", "", null, "on" }, false), Is.True);
            Assert.That(RemasterOptions.Resolve(new string[4], true), Is.True, "nothing set: the stored choice");
            Assert.That(RemasterOptions.Resolve(null, true), Is.True);
        }

        [Test]
        public void Migration_OldWorldOn_TurnsAllWorldPartsOn_Once()
        {
            var p = new Prefs();
            p.Set(RemasterOptions.LegacyWorldKey, 1);
            p.Set(RemasterOptions.PrefKey(RemasterFeature.Characters), 0);
            Assert.That(RemasterOptions.Migrate(p), Is.EqualTo(6));
            foreach (var f in RemasterOptions.WorldFeatures) Assert.That(p.Get(RemasterOptions.PrefKey(f)), Is.EqualTo(1), f.ToString());
            Assert.That(p.Get(RemasterOptions.PrefKey(RemasterFeature.Characters)), Is.EqualTo(0), "the characters are not a world part");
            Assert.That(p.Has(RemasterOptions.LegacyWorldKey), Is.False, "the old key is gone: the migration runs once");
            p.Set(RemasterOptions.PrefKey(RemasterFeature.Lighting), 0);   // the player turns a part off afterwards ...
            Assert.That(RemasterOptions.Migrate(p), Is.EqualTo(0));
            Assert.That(p.Get(RemasterOptions.PrefKey(RemasterFeature.Lighting)), Is.EqualTo(0), "... and it stays off");
        }

        [Test]
        public void Migration_KeepsExplicitParts_AndOldWorldOffWritesNothing()
        {
            var p = new Prefs();
            p.Set(RemasterOptions.LegacyWorldKey, 1);
            p.Set(RemasterOptions.PrefKey(RemasterFeature.Trees), 0);   // chosen on the page of fix-69
            Assert.That(RemasterOptions.Migrate(p), Is.EqualTo(5));
            Assert.That(p.Get(RemasterOptions.PrefKey(RemasterFeature.Trees)), Is.EqualTo(0));
            Assert.That(p.Get(RemasterOptions.PrefKey(RemasterFeature.Terrain)), Is.EqualTo(1));

            var off = new Prefs();
            off.Set(RemasterOptions.LegacyWorldKey, 0);
            Assert.That(RemasterOptions.Migrate(off), Is.EqualTo(0));
            Assert.That(off.D, Is.Empty, "old world off: nothing on, the old key removed");

            Assert.That(RemasterOptions.Migrate(new Prefs()), Is.EqualTo(0));
            Assert.That(RemasterOptions.Migrate(null), Is.EqualTo(0));
        }

        [Test]
        public void MasterSwitch_F7_AllOrNothing()
        {
            Assert.That(RemasterOptions.MasterTarget(WorldParts.None), Is.True, "everything off: all on");
            Assert.That(RemasterOptions.MasterTarget(WorldParts.Lighting | WorldParts.Trees), Is.True, "some on: all on");
            Assert.That(RemasterOptions.MasterTarget(WorldParts.All), Is.False, "all on: all off");
            Assert.That(RemasterOptions.MasterOn(WorldParts.None), Is.False);
            Assert.That(RemasterOptions.MasterOn(WorldParts.Grass), Is.True);

            // F7 twice from a partial state: all on, then all off (the page then shows six "Off" rows)
            var parts = WorldParts.Terrain;
            parts = RemasterOptions.MasterTarget(parts) ? WorldParts.All : WorldParts.None;
            Assert.That(parts, Is.EqualTo(WorldParts.All));
            parts = RemasterOptions.MasterTarget(parts) ? WorldParts.All : WorldParts.None;
            Assert.That(parts, Is.EqualTo(WorldParts.None));
        }

        [TestCase(null, "G3/SpeedTree", "Fir_Bark", WorldParts.Trees)]
        [TestCase(null, "G3/Vegetation", "G3_Nature_Grass_01", WorldParts.Grass)]
        [TestCase("terrain", "G3/LitBlend", "G3_Landscape_01", WorldParts.Terrain)]
        [TestCase("terrain,rock", "G3/LitBlend", "G3_Nature_Rock_01_A", WorldParts.Terrain)]   // landscape cells: steep rock blends stay ground
        [TestCase("rock", "Universal Render Pipeline/Simple Lit", "G3_Nature_Wall_Stone_01_A", WorldParts.Rocks)]
        [TestCase("architecture", "G3/LitBlend", "G3_Architecture_Wood_01_A", WorldParts.Buildings)]
        [TestCase("prop,architecture", null, "G3_Objects_Barrel_01_A", WorldParts.Buildings)]
        [TestCase("plant", null, "G3_Nature_Decal_Farn_01_A", WorldParts.Grass)]
        [TestCase("plant", null, "G3_Nature_Tree_Bark_05_A", WorldParts.Trees)]
        [TestCase("water", null, "G3_Water_01", WorldParts.Terrain)]
        public void Classify_ShaderThenInventoryCategory(string category, string shader, string name, WorldParts expected) =>
            Assert.That(WorldRemasterParts.Classify(category, shader, name), Is.EqualTo(expected));

        [Test]
        public void Classify_WithoutInventory_UsesV2FlagsThenNames()
        {
            Assert.That(WorldRemasterParts.Classify(null, "G3/Remaster/WorldBlend", "G3_X", v2Kind: 1), Is.EqualTo(WorldParts.Terrain));
            Assert.That(WorldRemasterParts.Classify(null, "G3/Remaster/WorldBlend", "G3_X", 2, v2Arch: false, v2Rock: true), Is.EqualTo(WorldParts.Rocks));
            Assert.That(WorldRemasterParts.Classify(null, "G3/Remaster/WorldBlend", "G3_X", 2, v2Arch: true, v2Rock: false), Is.EqualTo(WorldParts.Buildings));
            Assert.That(WorldRemasterParts.Classify("", null, "G3_Nature_Rock_Big_01_A"), Is.EqualTo(WorldParts.Rocks));
            Assert.That(WorldRemasterParts.Classify(null, null, "G3_Architecture_Planks_01_A"), Is.EqualTo(WorldParts.Buildings));
            Assert.That(WorldRemasterParts.Classify(null, null, "G3_Objects_Books_01_A"), Is.EqualTo(WorldParts.Buildings));
            Assert.That(WorldRemasterParts.Classify(null, null, "G3_Nature_Ground_Forest_01_A"), Is.EqualTo(WorldParts.Terrain));
            Assert.That(WorldRemasterParts.Classify(null, "G3/LitBlend", "G3_Unknown"), Is.EqualTo(WorldParts.Terrain));
            Assert.That(WorldRemasterParts.Classify(null, null, "G3_Nature_OldTree_01_A"), Is.EqualTo(WorldParts.Trees));
            Assert.That(WorldRemasterParts.Classify(null, null, "G3_Nature_Plant_Mushroom_03_A"), Is.EqualTo(WorldParts.Grass));
            Assert.That(WorldRemasterParts.Classify(null, null, "Something"), Is.EqualTo(WorldParts.Buildings), "unknown meshes count as props");
        }

        [Test]
        public void SwapMap_PerPart_ConvertsOnlyItsOwnPairs()
        {
            // original, r3, v2, v2 kind, part
            var t = new List<(string, string, string, int, int)>
            {
                ("G", "Gr", "Gv", 1, (int)WorldParts.Terrain),
                ("R", "Rr", "Rv", 2, (int)WorldParts.Rocks),
                ("B", "Br", null, 0, (int)WorldParts.Buildings),
                ("T", "Tr", null, 0, (int)WorldParts.Trees),
            };
            var on = WorldParts.Terrain | WorldParts.Trees;
            var map = WorldRemasterSwap.Target(t, p => (on & (WorldParts)p) != 0, k => true);
            Assert.That(map["G"], Is.EqualTo("Gv"), "ground on, v2 wanted");
            Assert.That(map["Gr"], Is.EqualTo("Gv"));
            Assert.That(map["T"], Is.EqualTo("Tr"), "trees on, r3 only");
            Assert.That(map.ContainsKey("R"), Is.False, "rocks off: originals stay");
            Assert.That(map["Rv"], Is.EqualTo("R"), "rocks off: a v2 copy goes back");
            Assert.That(map["Br"], Is.EqualTo("B"));
            Assert.That(map.ContainsKey("B"), Is.False);

            var mats = new[] { "Gv", "R", "Br", "T", "X" };
            Assert.That(WorldRemasterSwap.Apply(mats, map), Is.EqualTo(2));
            Assert.That(mats, Is.EqualTo(new[] { "Gv", "R", "B", "Tr", "X" }));

            var legacy = WorldRemasterSwap.Target(new List<(string, string, string, int)> { ("G", "Gr", "Gv", 1) }, true, k => false);
            Assert.That(legacy["G"], Is.EqualTo("Gr"), "the old all-or-nothing overload still works");
        }

        [Test]
        public void Wind_AndFoliage_SplitBetweenGrassAndTrees()
        {
            var full = WorldRemasterLook.Wind;
            Assert.That(WorldRemasterLook.WindFor(true, true), Is.EqualTo(full));
            var grass = WorldRemasterLook.WindFor(true, false);
            Assert.That(grass[0], Is.EqualTo(full[0]));
            Assert.That(grass[1], Is.EqualTo(0f), "no leaf rock without the trees part");
            var trees = WorldRemasterLook.WindFor(false, true);
            Assert.That(trees[0], Is.EqualTo(0f), "no grass waves without the grass part");
            Assert.That(trees[1], Is.EqualTo(full[1]));
            Assert.That(WorldRemasterLook.WindFor(false, false).Take(2), Is.All.EqualTo(0f));

            var old = WorldRemasterLook.Current;
            try
            {
                var s = WorldLookSettings.Default;
                s.v2 = true;
                s.v2Foliage = true;
                WorldRemasterLook.Current = s;
                var f = WorldV2.FoliageGlobal;
                Assert.That(WorldV2.FoliageGlobalFor(true, true), Is.EqualTo(f));
                Assert.That(WorldV2.FoliageGlobalFor(false, true), Is.EqualTo(new[] { 0f, 0f, 0f, f[3] }), "grass only: translucency, no SpeedTree terms");
                Assert.That(WorldV2.FoliageGlobalFor(true, false), Is.EqualTo(new[] { f[0], f[1], f[2], 0f }));
            }
            finally { WorldRemasterLook.Current = old; }
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]   // world v2 on: the Trees option no longer brings the v2 models (trees-v3) ...
        [TestCase(true, true, true)]     // ... only the runner's JSON A/B does
        [TestCase(false, true, false)]   // JSON alone does nothing while the world remaster is off
        public void Trees_V2ModelsOnlyByWorldV2WithJson(bool v2, bool json, bool expected) =>
            Assert.That(RemasterOptions.TreesWanted(v2, json), Is.EqualTo(expected));

        [TestCase("Russian")]
        [TestCase("English")]
        public void Texts_InBothLanguages(string language)
        {
            Assert.That(RemasterOptions.PageName(language), Is.EqualTo(language == "Russian" ? "Ремастер" : "Remaster"));
            Assert.That(RemasterOptions.Hint(language), Is.Not.Empty);
            foreach (var f in RemasterOptions.Rows)
            {
                Assert.That(RemasterOptions.RowName(f, language), Is.Not.Empty);
                string on = RemasterOptions.RowValue(f, true, true, language), off = RemasterOptions.RowValue(f, false, true, language);
                Assert.That(on, Is.Not.EqualTo(off), f.ToString());
                Assert.That(RemasterOptions.RowValue(f, false, false, language), Is.EqualTo(off), "off never says 'not built'");
                Assert.That(RemasterOptions.RowValue(f, true, false, language), Does.StartWith(on).And.Length.GreaterThan(on.Length));
            }
            Assert.That(RemasterOptions.RowName(RemasterFeature.Characters, "Russian"), Is.EqualTo("Персонажи"));   // fix-74: no "v2"; fix-168: no "(герой)"
            Assert.That(RemasterOptions.RowName(RemasterFeature.Characters, "English"), Is.EqualTo("Characters"));
            Assert.That(RemasterOptions.RowName(RemasterFeature.Lighting, "Russian"), Is.EqualTo("Освещение и атмосфера"));
            Assert.That(RemasterOptions.RowName(RemasterFeature.Rocks, "Russian"), Is.EqualTo("Скалы и горы"));
            Assert.That(RemasterOptions.RowValue(RemasterFeature.Terrain, false, true, "Russian"), Is.EqualTo("Выкл."));
            Assert.That(RemasterOptions.RowValue(RemasterFeature.Terrain, true, false, "English"), Is.EqualTo("On (not built)"));
            Assert.That(RemasterOptions.Rows.Select(f => RemasterOptions.RowName(f, language)).Distinct().Count(), Is.EqualTo(7));
            Assert.That(RemasterOptions.Hint(language), Does.Contain("F7"));
            Assert.That(RemasterOptions.Rows.Select(f => RemasterOptions.RowName(f, language)).Max(n => n.Length), Is.LessThanOrEqualTo(24),
                        "fits the 364 px label column");
        }

        [Test]
        public void Page_IsAnOptionsSubPage()
        {
            Assert.That(GameMenuState.Parent(MenuPage.Remaster), Is.EqualTo(MenuPage.Options));
            var s = new GameMenuState();
            s.Show(MenuPage.Options);
            s.Show(MenuPage.Remaster);
            Assert.That(s.Back(), Is.True);
            Assert.That(s.Page, Is.EqualTo(MenuPage.Options));
        }

        /// <summary>G3's options column (g3.gui.res IDD_MENU_OPTIONS_2): 32 px buttons, 37 px apart, Back at 682.</summary>
        static List<MenuButtonColumn.Slot> OptionsColumn() => new List<MenuButtonColumn.Slot>
        {
            new MenuButtonColumn.Slot("DIFFICULTY", 592, 32), new MenuButtonColumn.Slot("CONTROLS", 555, 32),
            new MenuButtonColumn.Slot("AUDIO", 518, 32), new MenuButtonColumn.Slot("VIDEO", 481, 32),
            new MenuButtonColumn.Slot("PERFORMANCE", 444, 32),
        };

        [Test]
        public void Button_GoesBelowVideo_AndTheColumnStaysInsideTheFrame()
        {
            // fix-72 (r35_options_page): six buttons at the old 37 px step ended at 661, over the bottom edge of the button
            // frame painted into G3_HUD_View_Menu_Options_Back.tga (the five G3 buttons end at 624).
            var fitted = MenuButtonColumn.Insert(OptionsColumn(), "VIDEO", "REMASTER");
            Assert.That(fitted, Is.Not.Null);
            var tops = fitted.Tops;
            Assert.That(fitted.Height, Is.EqualTo(28));
            Assert.That(tops["PERFORMANCE"], Is.EqualTo(444), "the column starts where G3's does");
            Assert.That(tops["DIFFICULTY"] + fitted.Height, Is.LessThanOrEqualTo(624), "and ends where G3's does");
            var order = new[] { "PERFORMANCE", "VIDEO", "REMASTER", "AUDIO", "CONTROLS", "DIFFICULTY" };
            for (int i = 1; i < order.Length; i++)
                Assert.That(tops[order[i]] - tops[order[i - 1]], Is.GreaterThanOrEqualTo(fitted.Height + 2), order[i] + ": no overlap, a gap stays");
        }

        [Test]
        public void Button_WithoutRoom_IsLeftOut()
        {
            var appended = MenuButtonColumn.Insert(OptionsColumn(), "MISSING", "REMASTER");
            Assert.That(appended.Tops["DIFFICULTY"], Is.LessThan(appended.Tops["REMASTER"]), "an unknown anchor appends below the last button");
            Assert.That(appended.Tops["REMASTER"] + appended.Height, Is.LessThanOrEqualTo(624));
            var two = new List<MenuButtonColumn.Slot> { new MenuButtonColumn.Slot("A", 0, 32), new MenuButtonColumn.Slot("B", 37, 32) };
            Assert.That(MenuButtonColumn.Insert(two, "A", "C"), Is.Null, "three would be flatter than the minimum");
            Assert.That(MenuButtonColumn.Insert(new List<MenuButtonColumn.Slot>(), "VIDEO", "REMASTER"), Is.Null);
        }
    }
}
