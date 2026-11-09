using System.Collections.Generic;
using System.Linq;
using G3.Unity;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>opts-1: Options → Remaster → World details (Docs/controls-options.md §9): defaults, persistence, env, the json overlay, texts, page.</summary>
    public class DesertOptionsTests
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
        public void Defaults_AreTheCurrentLook()
        {
            var c = DesertOptions.Load(new Prefs());
            Assert.That(c.Sand, Is.EqualTo(SandLook.Scans));
            Assert.That(c.SandFx, Is.True);
            Assert.That(c.Atmo, Is.True);
            Assert.That(c.ArchSand, Is.True);
            Assert.That(c.Saturation, Is.Null, "the json's saturation stays until chosen");
        }

        [Test]
        public void Defaults_ChangeNothingInTheJson()
        {
            var json = WorldLookSettings.Default;
            var o = DesertOptions.Apply(json, new DesertChoices());
            foreach (var f in typeof(WorldLookSettings).GetFields())
                Assert.That(f.GetValue(o), Is.EqualTo(f.GetValue(json.Clamped())), f.Name);
        }

        [Test]
        public void Choices_PersistAndReload()
        {
            var p = new Prefs();
            var c = new DesertChoices { Sand = SandLook.Simple, SandFx = false, Atmo = false, ArchSand = false, Saturation = -3 };
            DesertOptions.Save(p, c);
            Assert.That(p.D.Keys, Is.SupersetOf(new[] { DesertOptions.SandKey, DesertOptions.SandFxKey, DesertOptions.AtmoKey, DesertOptions.ArchSandKey, DesertOptions.SaturationKey }));
            var r = DesertOptions.Load(p);
            Assert.That(r.Sand, Is.EqualTo(SandLook.Simple));
            Assert.That(r.SandFx, Is.False);
            Assert.That(r.Atmo, Is.False);
            Assert.That(r.ArchSand, Is.False);
            Assert.That(r.Saturation, Is.EqualTo(-3));
            DesertOptions.Save(p, new DesertChoices { Sand = SandLook.Original });
            Assert.That(DesertOptions.Load(p).Sand, Is.EqualTo(SandLook.Original));
            Assert.That(p.Has(DesertOptions.SaturationKey), Is.False, "unchosen saturation is not stored");
        }

        [Test]
        public void Keys_DoNotCollideWithOtherRemasterKeys()
        {
            var keys = new[] { DesertOptions.SandKey, DesertOptions.SandFxKey, DesertOptions.AtmoKey, DesertOptions.ArchSandKey, DesertOptions.SaturationKey };
            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Length));
            var others = RemasterOptions.Rows.Select(RemasterOptions.PrefKey).Concat(new[]
                { G3TerrainReliefKeys.Relief, G3TerrainReliefKeys.Dunes, "G3.Remaster.Snow", "G3.Remaster.SnowTracks", RemasterOptions.WeatherPrefKey, LightingQualityLevels.PrefKey });
            Assert.That(keys.Intersect(others), Is.Empty);
        }

        /// <summary>The dunes pref names (G3TerrainRelief is engine code; the names are pinned here).</summary>
        static class G3TerrainReliefKeys
        {
            public const string Relief = "G3.Remaster.Relief", Dunes = "G3.Remaster.Dunes";
        }

        [Test]
        public void Stored_OutOfRange_IsClamped()
        {
            var p = new Prefs();
            p.Set(DesertOptions.SandKey, 9);
            p.Set(DesertOptions.SaturationKey, 40);
            var c = DesertOptions.Load(p);
            Assert.That(c.Sand, Is.EqualTo(SandLook.Scans));
            Assert.That(c.Saturation, Is.EqualTo(DesertOptions.SaturationMax));
            p.Set(DesertOptions.SandKey, -2);
            Assert.That(DesertOptions.Load(p).Sand, Is.EqualTo(SandLook.Original));
        }

        [Test]
        public void Environment_WinsOverTheStore()
        {
            var p = new Prefs();
            DesertOptions.Save(p, new DesertChoices { Sand = SandLook.Simple, SandFx = true, Atmo = true, ArchSand = true });
            var env = new Dictionary<string, string>
            {
                [DesertOptions.SandEnvVar] = "0", [DesertOptions.SandFxEnvVar] = "off", [DesertOptions.AtmoEnvVar] = "false", [DesertOptions.ArchSandEnvVar] = "0",
            };
            var c = DesertOptions.Load(p, k => env.TryGetValue(k, out var v) ? v : null);
            Assert.That(c.Sand, Is.EqualTo(SandLook.Original));
            Assert.That(c.SandFx || c.Atmo || c.ArchSand, Is.False);
            DesertOptions.Save(p, new DesertChoices { Sand = SandLook.Original });
            env.Clear();
            env[DesertOptions.SandEnvVar] = "1";
            Assert.That(DesertOptions.Load(p, k => env.TryGetValue(k, out var v) ? v : null).Sand, Is.EqualTo(SandLook.Scans));
            DesertOptions.Save(p, new DesertChoices { Sand = SandLook.Simple });
            Assert.That(DesertOptions.Load(p, k => env.TryGetValue(k, out var v) ? v : null).Sand, Is.EqualTo(SandLook.Simple), "on keeps a stored look");
        }

        [Test]
        public void Off_FlipsTheSystemsOwnJsonSwitches()
        {
            var json = WorldLookSettings.Default;
            var o = DesertOptions.Apply(json, new DesertChoices { Sand = SandLook.Original, SandFx = false, Atmo = false, ArchSand = false });
            Assert.That(o.sand, Is.False);
            Assert.That(o.sandWisps, Is.EqualTo(0f));
            Assert.That(o.sandDust, Is.EqualTo(0f));
            Assert.That(o.archSandWisps, Is.EqualTo(0));
            Assert.That(o.atmo, Is.False);
            Assert.That(o.archSandDeposit, Is.EqualTo(json.Clamped().archSandRest), "storm sand off: the resting deposit");
            Assert.That(json.sand && json.atmo, Is.True, "the input is not changed");

            var simple = DesertOptions.Apply(json, new DesertChoices { Sand = SandLook.Simple });
            Assert.That(simple.sand, Is.True);
            Assert.That(simple.sandScan, Is.EqualTo(0f), "sand-2 look");
        }

        [Test]
        public void ArchSandOff_KeepsAPinnedDeposit()
        {
            var json = WorldLookSettings.Default;
            json.archSandDeposit = 0.8f;
            Assert.That(DesertOptions.Apply(json, new DesertChoices { ArchSand = false }).archSandDeposit, Is.EqualTo(0.8f).Within(1e-6));
        }

        [Test]
        public void Saturation_SetsGrade1sField_WhenThere()
        {
            var o = DesertOptions.Apply(WorldLookSettings.Default, new DesertChoices { Saturation = 2 });
            if (!DesertOptions.HasSaturation) { Assert.Pass("no grade-1 saturation field on this build: the row is hidden"); return; }
            Assert.That((float)DesertOptions.SaturationField.GetValue(o), Is.EqualTo(0.2f).Within(1e-5));
            Assert.That(DesertOptions.SaturationSteps(-0.31f), Is.EqualTo(-3));
            Assert.That(DesertOptions.SaturationSteps(3f), Is.EqualTo(DesertOptions.SaturationMax));
        }

        [TestCase("Russian")]
        [TestCase("English")]
        public void Texts_FitTheLabelColumn(string language)
        {
            var names = new[]
            {
                DesertOptions.SandName(language), DesertOptions.SandFxName(language), DesertOptions.DunesName(language),
                DesertOptions.AtmoName(language), DesertOptions.ArchSandName(language), DesertOptions.SaturationName(language),
            };
            Assert.That(names.Max(n => n.Length), Is.LessThanOrEqualTo(24), "fits the 364 px label column");
            Assert.That(DesertOptions.PageName(language).Length, Is.LessThanOrEqualTo(14), "fits the 200 px button");
        }

        [Test]
        public void Texts_Russian()
        {
            Assert.That(DesertOptions.SandName("Russian"), Is.EqualTo("Песок пустыни"));
            Assert.That(DesertOptions.SandValue(SandLook.Original, true, "Russian"), Is.EqualTo("Выкл. (оригинал)"));
            Assert.That(DesertOptions.SandValue(SandLook.Simple, true, "Russian"), Is.EqualTo("Простой"));
            Assert.That(DesertOptions.SandValue(SandLook.Scans, false, "Russian"), Is.EqualTo("Сканы (с Землёй)"));
            Assert.That(DesertOptions.DunesValue(true, false, "Russian"), Is.EqualTo("Вкл. (с Рельефом)"));
            Assert.That(DesertOptions.DunesValue(false, false, "Russian"), Is.EqualTo("Выкл."));
            Assert.That(DesertOptions.SaturationValue(2, true, "English"), Is.EqualTo("+20%"));
            Assert.That(DesertOptions.SaturationValue(0, true, "English"), Is.EqualTo("0%"));
        }

        [Test]
        public void Page_IsARemasterSubPage()
        {
            Assert.That(GameMenuState.Parent(MenuPage.RemasterDetails), Is.EqualTo(MenuPage.Remaster));
            var s = new GameMenuState();
            s.Show(MenuPage.Options);
            s.Show(MenuPage.Remaster);
            s.Show(MenuPage.RemasterDetails);
            Assert.That(s.Back(), Is.True);
            Assert.That(s.Page, Is.EqualTo(MenuPage.Remaster));
        }
    }
}
