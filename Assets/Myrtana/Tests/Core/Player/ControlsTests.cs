using System.Linq;
using G3.Player;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>par-controls: G3 / CP 1.75 default keys, rebinding, the Controls page values, potion keys (Docs/controls-options.md).</summary>
    public class ControlsTests
    {
        static KeyInput K(int dik) => KeyInput.Key(dik);
        static KeyInput M(int offset) => KeyInput.Mouse(offset);

        [Test]
        public void DefaultsAreCreateDefaultKeysOfCp175()
        {
            var m = KeyMap.Default();
            void Bound(SessionKey key, KeyInput a, KeyInput b)
            {
                Assert.That(m.Get(key, 0), Is.EqualTo(a), key + " Key1");
                Assert.That(m.Get(key, 1), Is.EqualTo(b), key + " Key2");
            }
            Bound(SessionKey.Forward, K(0xC8), K(0x11));                 // Up arrow, W
            Bound(SessionKey.Backward, K(0xD0), K(0x1F));
            Bound(SessionKey.StrafeLeft, K(0xCB), K(0x1E));
            Bound(SessionKey.StrafeRight, K(0xCD), K(0x20));
            Bound(SessionKey.RotateCamRight, M(7), K(0x12));             // Mouse X+, E
            Bound(SessionKey.RotateCamLeft, M(8), K(0x10));              // Mouse X-, Q
            Bound(SessionKey.TurnUp, M(10), KeyInput.None);
            Bound(SessionKey.TurnDown, M(9), KeyInput.None);
            Bound(SessionKey.Walk, K(0x36), K(0x2A));                    // shifts: walk / sprint
            Bound(SessionKey.WalkToggle, K(0x3A), KeyInput.None);        // Caps Lock
            Bound(SessionKey.Up, K(0x52), K(0x38));                      // Num 0, Left Alt: jump
            Bound(SessionKey.Down, K(0x9D), K(0x1D));                    // Ctrl: sneak
            Bound(SessionKey.Use1, M(3), KeyInput.None);
            Bound(SessionKey.Use2, M(4), KeyInput.None);
            Bound(SessionKey.Plus, K(0x4E), M(11));
            Bound(SessionKey.Minus, K(0x4A), M(12));
            Bound(SessionKey.ResetCamera, K(0x4B), KeyInput.None);
            Bound(SessionKey.FirstPerson, K(0x53), K(0x14));             // Num Del, T
            Bound(SessionKey.Look, K(0x50), KeyInput.None);
            Bound(SessionKey.QuickUse0, K(0x0B), KeyInput.None);
            Bound(SessionKey.QuickUse1, K(0x02), KeyInput.None);
            Bound(SessionKey.QuickUse9, K(0x0A), KeyInput.None);
            Bound(SessionKey.InventoryMode, K(0x17), K(0x0E));           // I, Backspace
            Bound(SessionKey.WeaponMode, K(0x39), M(5));                 // Space, middle button
            Bound(SessionKey.JournalModeLog, K(0x26), KeyInput.None);
            Bound(SessionKey.JournalModeCharScreen, K(0x2E), KeyInput.None);
            Bound(SessionKey.JournalModeMagBook, K(0x30), KeyInput.None);
            Bound(SessionKey.JournalModeMap, K(0x32), KeyInput.None);
            Bound(SessionKey.Lock, K(0x13), K(0xCF));
            Bound(SessionKey.ToggleHUD, K(0x46), KeyInput.None);
            Bound(SessionKey.TakeAll, K(0x16), KeyInput.None);
            Bound(SessionKey.UsePotionHP, K(0x47), KeyInput.None);       // Num 7 / 8 / 9
            Bound(SessionKey.UsePotionMP, K(0x48), KeyInput.None);
            Bound(SessionKey.UsePotionSP, K(0x49), KeyInput.None);
            Bound(SessionKey.Confirm, K(0x1C), KeyInput.None);
            Bound(SessionKey.QuickSave, K(0x3F), KeyInput.None);         // F5
            Bound(SessionKey.QuickLoad, K(0x43), KeyInput.None);         // F9
        }

        [Test]
        public void EveryKeyHasADefaultRowAndNoInputIsBoundTwice()
        {
            Assert.That(KeyMap.DefaultTable.Select(r => r.key), Is.EquivalentTo(KeyMap.All), "44 rows of the custom list + the port's modifier");
            Assert.That(KeyMap.All.Length, Is.EqualTo(45));
            Assert.That(KeyMap.All.Count(k => !KeyMap.IsPortKey(k)), Is.EqualTo(44), "G3's rows");
            var m = KeyMap.Default();
            // fix-205: a modifier may share Ctrl with Sneak; within each class no input is bound twice
            var inputs = KeyMap.All.Where(k => !KeyMap.IsModifier(k)).SelectMany(k => m.Inputs(k)).ToList();
            Assert.That(inputs.Distinct().Count(), Is.EqualTo(inputs.Count));
            Assert.That(m.IsDefault, Is.True);
        }

        [Test]
        public void ListOrderIsG3sCustomList()
        {
            Assert.That(KeyMap.All.First(), Is.EqualTo(SessionKey.Forward));
            Assert.That(KeyMap.All[4], Is.EqualTo(SessionKey.TurnUp));
            Assert.That(KeyMap.All.Last(k => !KeyMap.IsPortKey(k)), Is.EqualTo(SessionKey.UsePotionSP), "G3's last row; the port key follows");
            Assert.That(KeyMap.TextId(SessionKey.JournalModeMagBook), Is.EqualTo("HUD_SessionKey_JournalModeMagBook"));
        }

        [Test]
        public void AssigningATakenInputMovesItAndReportsTheOldKey()
        {
            var m = KeyMap.Default();
            int changes = 0;
            m.Changed += () => changes++;
            var from = m.Assign(SessionKey.Use1, 1, K(0x12)); // E, the second Use1 key
            Assert.That(from, Is.EqualTo(SessionKey.RotateCamRight));
            Assert.That(m.Get(SessionKey.RotateCamRight, 1), Is.EqualTo(KeyInput.None));
            Assert.That(m.Owner(K(0x12), out int slot), Is.EqualTo(SessionKey.Use1));
            Assert.That(slot, Is.EqualTo(1));
            Assert.That(m.Assign(SessionKey.Use1, 1, K(0x12)), Is.Null, "already there");
            m.Clear(SessionKey.Use1, 1);
            Assert.That(m.Owner(K(0x12), out _), Is.Null);
            Assert.That(changes, Is.EqualTo(3));
            Assert.That(m.IsDefault, Is.False);
            m.ResetToDefault();
            Assert.That(m.IsDefault, Is.True);
        }

        [Test]
        public void SerializeRoundTripsAndParseIsTolerant()
        {
            var m = KeyMap.Default();
            m.Assign(SessionKey.Up, 0, K(0x39));            // Space jumps (takes it from WeaponMode)
            m.Assign(SessionKey.WeaponMode, 0, K(0x13));    // R draws (takes it from Lock)
            var text = m.Serialize();
            var back = KeyMap.Parse(text);
            foreach (var key in KeyMap.All)
                for (int s = 0; s < KeyMap.Slots; s++)
                    Assert.That(back.Get(key, s), Is.EqualTo(m.Get(key, s)), key + " " + s);

            Assert.That(KeyMap.Parse(null).IsDefault, Is.True);
            Assert.That(KeyMap.Parse("garbage;Up=x;Nope=0:1,-").IsDefault, Is.True);
            // An older file without the potion keys: they get their defaults unless the file used the key elsewhere.
            var old = KeyMap.Parse("Up=0:71,-");   // Num 7 jumps
            Assert.That(old.Get(SessionKey.Up, 0), Is.EqualTo(K(0x47)));
            Assert.That(old.Get(SessionKey.UsePotionHP, 0), Is.EqualTo(KeyInput.None));
            Assert.That(old.Get(SessionKey.UsePotionMP, 0), Is.EqualTo(K(0x48)));
        }

        [Test]
        public void KeyNamesFollowG3()
        {
            Assert.That(K(0x38).Name, Is.EqualTo("LEFT ALT"));
            Assert.That(K(0x47).Name, Is.EqualTo("NUM 7"));
            Assert.That(K(0x14).Name, Is.EqualTo("T"));
            Assert.That(K(0x02).Name, Is.EqualTo("1"));
            Assert.That(K(0x43).Name, Is.EqualTo("F9"));
            Assert.That(M(3).Name, Is.EqualTo("Mouse 0"));
            Assert.That(M(7).Name, Is.EqualTo("Mouse X+"));
            Assert.That(KeyInput.TryParse("1:7", out var x) && x == M(7), Is.True);
            Assert.That(KeyInput.TryParse("1:99", out _), Is.False);
        }

        [Test]
        public void MouseSlidersMapLikeG3()
        {
            Assert.That(ControlOptions.FromSlider(0), Is.EqualTo(0.5f));
            Assert.That(ControlOptions.FromSlider(-100), Is.EqualTo(0f));
            Assert.That(ControlOptions.FromSlider(100), Is.EqualTo(1f));
            Assert.That(ControlOptions.ToSlider(0.75f), Is.EqualTo(50));
            Assert.That(ControlOptions.SensitivityFactor(0.5f), Is.EqualTo(1f).Within(1e-6), "the default is the port's base speed");
            Assert.That(ControlOptions.SensitivityFactor(1f), Is.EqualTo(2.5f / 1.5f).Within(1e-6), "2·f + 0.5");
            Assert.That(ControlOptions.SensitivityFactor(0f), Is.EqualTo(0.5f / 1.5f).Within(1e-6));
            Assert.That(ControlOptions.SmoothingValue(0.5f), Is.EqualTo(1f));
            Assert.That(ControlOptions.SmoothingValue(0f), Is.EqualTo(0.002f).Within(1e-6), "G3 clamps at 0.001");
        }

        [Test]
        public void MouseLookInvertsAndSmooths()
        {
            var o = new ControlOptions { SmoothingX = 0.001f, SmoothingY = 0.001f };
            var f = new MouseLookFilter();
            var (x, y) = f.Apply(10, 4, 0.016f, o);
            Assert.That(x, Is.EqualTo(10).Within(0.01f));
            Assert.That(y, Is.EqualTo(4).Within(0.01f));
            o.InvertX = true;
            o.InvertY = true;
            (x, y) = f.Apply(10, 4, 0.016f, o);
            Assert.That(x, Is.LessThan(0));
            Assert.That(y, Is.LessThan(0));
            o.InvertY = false;
            o.LookaroundInverse = true;
            (_, y) = f.Apply(0, 4, 0.016f, o, lookingBehind: true);
            Assert.That(y, Is.LessThan(0), "rear view inverts Y");

            var smooth = new MouseLookFilter();
            var s = new ControlOptions { SmoothingX = 1f };
            float first = smooth.Apply(10, 0, 0.016f, s).x;
            Assert.That(first, Is.GreaterThan(0).And.LessThan(10), "smoothing lags");
            float last = first;
            for (int i = 0; i < 200; i++) last = smooth.Apply(10, 0, 0.016f, s).x;
            Assert.That(last, Is.EqualTo(10).Within(0.01f), "a steady movement converges");
        }

        [Test]
        public void StandardRadioRestoresTheDefaultsAndRaisesChanged()
        {
            var o = new ControlOptions();
            int changes = 0;
            o.Changed += () => changes++;
            o.Layout = ControlOptions.Custom;
            o.Keys.Assign(SessionKey.Up, 0, K(0x39));
            o.UseStandardKeys();
            Assert.That(o.Layout, Is.EqualTo(ControlOptions.Standard));
            Assert.That(o.Keys.IsDefault, Is.True);
            Assert.That(changes, Is.EqualTo(3));
        }

        [Test]
        public void PotionKeysDrinkTheSmallPotionUnlessTransformed()
        {
            int Count(string item) => item == "It_Potion_Health" || item == "It_Potion_Stamina" ? 2 : 0;
            Assert.That(PotionQuickUse.ItemToDrink(QuickPotion.Health, false, Count), Is.EqualTo("It_Potion_Health"));
            Assert.That(PotionQuickUse.ItemToDrink(QuickPotion.Mana, false, Count), Is.Null, "none carried");
            Assert.That(PotionQuickUse.ItemToDrink(QuickPotion.Stamina, false, Count), Is.EqualTo("It_Potion_Stamina"));
            Assert.That(PotionQuickUse.ItemToDrink(QuickPotion.Health, true, Count), Is.Null, "animal form / ghost");
        }

        [Test]
        public void JumpCostsTenStaminaInEveryRuleSet()
        {
            var rules = TestRules.Combat(); // the G3 rules importer's table
            foreach (var ab in new[] { false, true })
            {
                int stamina = 25;
                var options = new RuleOptions(alternativeBalancing: ab);
                StaminaRules.PayForMove(ref stamina, CombatAction.Jump, rules, options);
                Assert.That(stamina, Is.EqualTo(15));
                stamina = 4;
                StaminaRules.PayForMove(ref stamina, CombatAction.Jump, rules, options);
                Assert.That(stamina, Is.EqualTo(0), "floored, no refusal");
            }
        }

        [Test]
        public void VideoSlidersUseTheGammaScaling()
        {
            var v = new VideoOptions();
            v.SetSlider(VideoColor.Brightness, 100);
            v.SetSlider(VideoColor.Contrast, -100);
            v.SetSlider(VideoColor.GammaRed, 50);
            v.SetSlider(VideoColor.GammaBlue, 300);
            Assert.That(v.Value(VideoColor.Brightness), Is.EqualTo(0.2f).Within(1e-6));
            Assert.That(v.Value(VideoColor.Contrast), Is.EqualTo(-0.075f).Within(1e-6));
            Assert.That(v.Value(VideoColor.GammaRed), Is.EqualTo(0.15f).Within(1e-6));
            Assert.That(v.Slider(VideoColor.GammaBlue), Is.EqualTo(100), "clamped to −100…100");
            Assert.That(v.Value(VideoColor.GammaGreen), Is.EqualTo(0f));
            Assert.That(VideoOptions.ResolutionText(1920, 1080), Is.EqualTo("1920 x 1080"));
            v.SetResolution(0, 600, 60);
            Assert.That(v.NativeResolution, Is.True);
        }

        [Test]
        public void DetailPresetsAreGe3Ini()
        {
            var p = new PerformanceOptions();
            Assert.That(p.Preset, Is.EqualTo(DetailPreset.High));
            p.SelectPreset(DetailPreset.VeryHigh);
            Assert.That(p.Value(DetailSetting.ObjectDetails), Is.EqualTo(4));
            Assert.That(p.Value(DetailSetting.TextureFilter), Is.EqualTo(4));
            Assert.That(p.Value(DetailSetting.Bloom), Is.EqualTo(2));
            Assert.That(p.Value(DetailSetting.Antialiasing), Is.EqualTo(1));
            p.SelectPreset(DetailPreset.Low);
            Assert.That(p.Value(DetailSetting.ShadowQuality), Is.EqualTo(0));
            Assert.That(p.Value(DetailSetting.VegetationViewRange), Is.EqualTo(1));
            Assert.That(p.Value(DetailSetting.ColorGrading), Is.EqualTo(1), "no preset sets colour grading");
            p.Set(DetailSetting.ShadowQuality, 9);
            Assert.That(p.Value(DetailSetting.ShadowQuality), Is.EqualTo(4), "clamped");
            Assert.That(p.Preset, Is.EqualTo(DetailPreset.Custom), "a slider makes it custom");

            var loaded = new PerformanceOptions();
            loaded.Load(DetailPreset.Medium, new System.Collections.Generic.Dictionary<DetailSetting, int>
                { [DetailSetting.ShadowQuality] = 4, [DetailSetting.ColorGrading] = 0 });
            Assert.That(loaded.Value(DetailSetting.ShadowQuality), Is.EqualTo(1), "a preset's own value wins over a stale one");
            Assert.That(loaded.Value(DetailSetting.ColorGrading), Is.EqualTo(0));
        }

        [Test]
        public void CommandLineSwitchesWinOverTheDetailsMenu()
        {
            var menu = new G3.Unity.RenderTuningOptions { Cascades = 2, ShadowSmallMesh = 1, SmallMesh = 2, PointShadows = 1, LightDistance = 50, NpcLod = false };
            var cmd = G3.Unity.RenderTuningOptions.Parse(new[] { "-g3cascades", "4", "-g3npclod", "1" });
            var o = G3.Unity.RenderTuningOptions.Merge(menu, cmd);
            Assert.That(o.Cascades, Is.EqualTo(4), "-g3cascades given");
            Assert.That(o.NpcLod, Is.True, "-g3npclod given");
            Assert.That(o.ShadowSmallMesh, Is.EqualTo(1f));
            Assert.That(o.SmallMesh, Is.EqualTo(2f));
            Assert.That(o.PointShadows, Is.EqualTo(1));
            Assert.That(o.LightDistance, Is.EqualTo(50f));
            Assert.That(G3.Unity.RenderTuningOptions.Merge(menu, new G3.Unity.RenderTuningOptions()).NpcLod, Is.False, "menu without -g3npclod");
            Assert.That(G3.Unity.RenderTuningOptions.Merge(null, cmd).Cascades, Is.EqualTo(4));
        }

        [Test]
        public void MenuPagesGoBackLikeG3()
        {
            Assert.That(GameMenuState.Parent(MenuPage.ControlsCustom), Is.EqualTo(MenuPage.Controls));
            Assert.That(GameMenuState.Parent(MenuPage.PerformanceCustom), Is.EqualTo(MenuPage.Performance));
            Assert.That(GameMenuState.Parent(MenuPage.Performance), Is.EqualTo(MenuPage.Options));
        }
    }
}
