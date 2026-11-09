using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class G3FontMetricsTests
    {
        [Test]
        public void ResSize_GivesTimesCapHeight()
        {
            // an 18 px res label: Times caps 11.9 px; Gothic3 caps are 1.074 em
            float em = 18 * G3FontMetrics.ResFaceScale;
            Assert.That(em * G3FontMetrics.Gothic3CapHeightEm, Is.EqualTo(18 * G3FontMetrics.TimesCapHeightEm).Within(0.01f));
            Assert.That(G3FontMetrics.LineHeight(18), Is.LessThanOrEqualTo(25f), "fits the 25 px option rows");
        }

        [Test]
        public void CellHeight_IsTheWholeLine()
        {
            // focus names (+24) and messages (+20) are cell heights: one Gothic3 line of that height
            Assert.That(G3FontMetrics.LineHeight(G3FontMetrics.FromCellHeight(24)), Is.EqualTo(24f).Within(0.01f));
            Assert.That(G3FontMetrics.LineHeight(G3FontMetrics.FromCellHeight(20)), Is.EqualTo(20f).Within(0.01f));
            Assert.That(G3FontMetrics.FromCellHeight(24), Is.EqualTo(18f).Within(0.1f));
        }

        [Test]
        public void TrustedGlyphs_AreAsciiAndCheckedLatin1()
        {
            foreach (char c in "09AZaz()+-.,!?") Assert.That(G3FontMetrics.IsTrustedGlyph(c), Is.True, c.ToString());
            foreach (char c in "äöüßÄÖÜéèñ") Assert.That(G3FontMetrics.IsTrustedGlyph(c), Is.True, c.ToString());
            foreach (char c in "¤²ÀÆ×æÿ’–—…€№Жж") Assert.That(G3FontMetrics.IsTrustedGlyph(c), Is.False, c.ToString());
        }

        [Test]
        public void SynthesizedCyrillic_IsTrustedOnlyInTheGeneratedFont()
        {
            foreach (char c in "АЯаяЁёЖжЩщ№«»–—‘’“”„…")
            {
                Assert.That(G3FontMetrics.IsTrustedGlyph(c, withCyrillic: true), Is.True, c.ToString());
                Assert.That(G3FontMetrics.IsTrustedGlyph(c), Is.False, c.ToString());
            }
            foreach (char c in "ЀЂЄІЇЎѐђєіїў‚‹›€¤") Assert.That(G3FontMetrics.IsTrustedGlyph(c, withCyrillic: true), Is.False, c.ToString());
            Assert.That(G3FontMetrics.IsTrustedGlyph('A', withCyrillic: true) && G3FontMetrics.IsTrustedGlyph('ä', withCyrillic: true), Is.True);
        }

        [Test]
        public void TrackedCyrillicFont_MapsExactlyTheTrustedCodePoints()
        {
            string path = null;
            for (var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); d != null && path == null; d = d.Parent)
            {
                string p = System.IO.Path.Combine(d.FullName, "Assets", "G3", "Fonts", "Gothic3_Cyr.ttf");
                if (System.IO.File.Exists(p)) path = p;
            }
            if (path == null && System.IO.File.Exists("Assets/G3/Fonts/Gothic3_Cyr.ttf")) path = "Assets/G3/Fonts/Gothic3_Cyr.ttf";
            if (path == null) Assert.Ignore("Assets/G3/Fonts/Gothic3_Cyr.ttf not found");

            byte[] ttf = System.IO.File.ReadAllBytes(path);
            int tables = ttf[4] << 8 | ttf[5], cmap = -1;
            for (int i = 0; i < tables; i++)
            {
                int rec = 12 + i * 16;
                if (System.Text.Encoding.ASCII.GetString(ttf, rec, 4) == "cmap")
                    cmap = ttf[rec + 8] << 24 | ttf[rec + 9] << 16 | ttf[rec + 10] << 8 | ttf[rec + 11];
            }
            var map = G3.Formats.Gui.TrueTypeCmapFilter.ReadUnicodeMap(ttf, cmap);
            foreach (int cp in map.Keys)
                Assert.That(G3FontMetrics.IsTrustedGlyph(cp, withCyrillic: true), Is.True, $"U+{cp:X4} is mapped but not trusted");
            for (int cp = 0; cp <= 0xFFFF; cp++)
                if (G3FontMetrics.IsSynthesizedGlyph(cp)) Assert.That(map.ContainsKey(cp), Is.True, $"U+{cp:X4} missing");
            foreach (char c in "AZaz09äßÄé") Assert.That(map.ContainsKey(c), Is.True, c.ToString());
        }

        [Test]
        public void PanelScale_100PercentIsFitToHeight_SizeMultipliesAroundIt()
        {
            float fit = 1080f / 768f;
            Assert.That(UiScaleRules.PanelScale(GuiPanelKind.Hud, 100, 1920, 1080), Is.EqualTo(fit));
            Assert.That(UiScaleRules.PanelScale(GuiPanelKind.Hud, 70, 1920, 1080), Is.EqualTo(fit * 0.7f).Within(1e-5f));
            Assert.That(UiScaleRules.PanelScale(GuiPanelKind.Hud, 130, 1920, 1080), Is.EqualTo(fit * 1.3f).Within(1e-5f));
            Assert.That(UiScaleRules.PanelScale(GuiPanelKind.Page, 70, 1920, 1080), Is.EqualTo(fit * 0.7f).Within(1e-5f));
            Assert.That(UiScaleRules.PanelScale(GuiPanelKind.Page, 130, 1920, 1080), Is.EqualTo(fit), "pages never leave the screen");
            Assert.That(UiScaleRules.PanelScale(GuiPanelKind.Screen, 50, 1920, 1080), Is.EqualTo(fit), "full-screen 4:3 ignores the size");
            Assert.That(UiScaleRules.PanelScale(GuiPanelKind.Page, 100, 800, 600), Is.EqualTo(600f / 768f), "small windows shrink");
        }

        [Test]
        public void FitScale_FillsHeightAndKeeps4By3InsideTheScreen()
        {
            Assert.That(UiScaleRules.FitScale(1920, 1080), Is.EqualTo(1080f / 768f), "1440x1080 at 1080p");
            Assert.That(UiScaleRules.FitScale(2560, 1440), Is.EqualTo(1440f / 768f));
            Assert.That(UiScaleRules.FitScale(1024, 768), Is.EqualTo(1f));
            Assert.That(UiScaleRules.FitScale(1280, 1024), Is.EqualTo(1.25f), "narrower than 4:3: fits the width");
            Assert.That(UiScaleRules.FitScale(0, 0), Is.EqualTo(1f));
        }

        [Test]
        public void UiScaleOption_ClampsAndSteps()
        {
            var o = new PlayerOptions();
            Assert.That(o.UiScale, Is.EqualTo(100));
            int changes = 0;
            o.Changed += () => changes++;
            o.UiScale = 500;
            Assert.That(o.UiScale, Is.EqualTo(PlayerOptions.UiScaleMax));
            o.UiScale = 10;
            Assert.That(o.UiScale, Is.EqualTo(PlayerOptions.UiScaleMin));
            Assert.That(PlayerOptions.UiScaleMin, Is.LessThan(100));
            Assert.That(PlayerOptions.UiScaleFromStep(PlayerOptions.UiScaleToStep(100)), Is.EqualTo(100), "100 % is a slider step");
            o.UiScale = PlayerOptions.UiScaleFromStep(PlayerOptions.UiScaleToStep(130));
            Assert.That(o.UiScale, Is.EqualTo(130));
            Assert.That(changes, Is.EqualTo(3));
            Assert.That(GameMenuState.Parent(MenuPage.Video), Is.EqualTo(MenuPage.Options));
        }
    }
}
