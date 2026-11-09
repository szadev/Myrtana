using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The port's own option rows (player feedback items 5, 12, 15): defaults are the original, ranges, texts.</summary>
    public class PortOptionsTests
    {
        [Test]
        public void Defaults_AreTheOriginal()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.FovOffset, Is.EqualTo(0));
            Assert.That(port.Upscaling, Is.EqualTo(UpscalingMode.Off));
            Assert.That(port.CombatMusicThreat, Is.False);
            Assert.That(port.SmoothMusic, Is.False);
            Assert.That(port.CompanionsFollowTeleport, Is.False);
            Assert.That(port.CompanionMarkers, Is.False);
            Assert.That(port.TurnKeysOrbit, Is.False, "Q / E turn the hero as in G3");
            Assert.That(port.SkipDecorativeFocus, Is.False, "every interactive object is focused as in G3 (fix-79)");
            Assert.That(PortOptions.RenderScale(UpscalingMode.Off), Is.EqualTo(1f));
        }

        [Test]
        public void Changes_RaisePlayerOptionsChanged_OnceEach()
        {
            var options = new PlayerOptions();
            int changed = 0;
            options.Changed += () => changed++;
            options.Port.CompanionsFollowTeleport = true;
            options.Port.CompanionsFollowTeleport = true;
            options.Port.FovOffset = 10;
            options.Port.Upscaling = UpscalingMode.StpBalanced;
            Assert.That(changed, Is.EqualTo(3));
        }

        [TestCase(-50, -10)]
        [TestCase(12, 12)]
        [TestCase(90, 30)]
        public void FovOffset_IsClamped(int set, int stored)
        {
            var port = new PortOptions { FovOffset = set };
            Assert.That(port.FovOffset, Is.EqualTo(stored));
        }

        [Test]
        public void FovSlider_RoundTrips_OriginalAtZero()
        {
            for (int step = 0; step <= PortOptions.FovSteps; step++)
                Assert.That(PortOptions.FovToStep(PortOptions.FovFromStep(step)), Is.EqualTo(step));
            Assert.That(PortOptions.FovFromStep(PortOptions.FovToStep(0)), Is.EqualTo(0));
            Assert.That(PortOptions.FovText(0, "Russian"), Is.EqualTo("Оригинал"));
            Assert.That(PortOptions.FovText(15, "English"), Is.EqualTo("+15°"));
            Assert.That(PortOptions.FovText(-5, "English"), Is.EqualTo("-5°"));
        }

        [Test]
        public void Upscaling_ScalesAndFilters()
        {
            Assert.That(PortOptions.UpscalingSteps, Is.EqualTo(6));
            Assert.That(PortOptions.RenderScale(UpscalingMode.FsrPerformance), Is.EqualTo(0.5f));
            Assert.That(PortOptions.RenderScale(UpscalingMode.StpQuality), Is.EqualTo(1f / 1.5f).Within(1e-6));
            Assert.That(PortOptions.IsStp(UpscalingMode.StpBalanced), Is.True);
            Assert.That(PortOptions.IsStp(UpscalingMode.FsrBalanced), Is.False);
            var port = new PortOptions { Upscaling = (UpscalingMode)42 };
            Assert.That(port.Upscaling, Is.EqualTo(UpscalingMode.Off));
            Assert.That(PortOptions.UpscalingText(UpscalingMode.Off, "Russian"), Is.EqualTo("Выкл"));
        }

        [Test]
        public void RowTexts_RussianAndEnglish_OtherLanguagesEnglish()
        {
            foreach (PortOptions.Row row in System.Enum.GetValues(typeof(PortOptions.Row)))
            {
                if (row == PortOptions.Row.TurnKeys) continue; // key names ("Q / E") are not translated
                Assert.That(PortOptions.RowText(row, "Russian"), Is.Not.EqualTo(PortOptions.RowText(row, "English")));
                Assert.That(PortOptions.RowText(row, "German"), Is.EqualTo(PortOptions.RowText(row, "English")));
            }
            Assert.That(PortOptions.ValueText(PortOptions.Row.CombatMusic, false, "Russian"), Is.EqualTo("Оригинал"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.CompanionsTeleport, false, "English"), Is.EqualTo("Wait (original)"));
            Assert.That(PortOptions.RowText(PortOptions.Row.DecorativeFocus, "Russian"), Is.EqualTo("Подсвечивать бесполезные объекты"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.DecorativeFocus, false, "Russian"), Is.EqualTo("Да (оригинал)"), "position 0 = original");
            Assert.That(PortOptions.ValueText(PortOptions.Row.DecorativeFocus, true, "English"), Is.EqualTo("No"));
        }

        [Test]
        public void TurnKeysRow_NamesTheKeys_OriginalAtZero()
        {
            Assert.That(PortOptions.RowText(PortOptions.Row.TurnKeys, "Russian"), Is.EqualTo("Q / E"));
            Assert.That(PortOptions.TurnKeysText("A / D"), Is.EqualTo("A / D"));
            Assert.That(PortOptions.TurnKeysText(null), Is.EqualTo("Q / E"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.TurnKeys, false, "Russian"), Is.EqualTo("Поворот героя (оригинал)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.TurnKeys, true, "Russian"), Is.EqualTo("Обзор камерой"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.TurnKeys, false, "English"), Is.EqualTo("Turn hero (original)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.TurnKeys, true, "German"), Is.EqualTo("Orbit camera"));
            var options = new PlayerOptions();
            int changed = 0;
            options.Changed += () => changed++;
            options.Port.TurnKeysOrbit = true;
            options.Port.TurnKeysOrbit = true;
            Assert.That(changed, Is.EqualTo(1), "stored with the other options (OptionsPreferences saves on Changed)");
        }

        [Test]
        public void ControlsPage_PortRows_FitBetweenRadiosAndBack_WithoutOverlap()
        {
            // fix-107: the Quick saves row (fix-82) is gone; fix-205: Q / E, Focus decorative objects and Take all share the band, centred
            float[] tops = { ControlsPortRowsLayout.TurnKeysTop, ControlsPortRowsLayout.DecorativeFocusTop, ControlsPortRowsLayout.QuickLootChordTop };
            Assert.That(tops[0], Is.GreaterThanOrEqualTo(ControlsPortRowsLayout.CustomRadioBottom), "below the Custom radio");
            for (int i = 1; i < tops.Length; i++)
                Assert.That(tops[i], Is.GreaterThanOrEqualTo(ControlsPortRowsLayout.RowBottom(tops[i - 1]) - ControlsPortRowsLayout.SliderOffset),
                            "a row's label/slider starts where the previous row's slider band (25 px) ends");
            Assert.That(ControlsPortRowsLayout.RowBottom(tops[tops.Length - 1]), Is.LessThanOrEqualTo(ControlsPortRowsLayout.BackTop), "above Back");
            float above = tops[0] - ControlsPortRowsLayout.CustomRadioBottom;
            float below = ControlsPortRowsLayout.BackTop - ControlsPortRowsLayout.RowBottom(tops[tops.Length - 1]);
            Assert.That(System.Math.Abs(above - below), Is.LessThanOrEqualTo(4f), "the rows sit in the middle of the band");
            Assert.That(System.Enum.GetNames(typeof(PortOptions.Row)), Has.No.Member("QuickSaves"), "no quicksave rotation row (fix-107)");
        }

        /// <summary>fix-109 [owner]: "Shield protects from beasts", default No (original), one Video row above Upscaling (fix-116).</summary>
        [Test]
        public void ShieldBeastsRow_DefaultsToTheOriginal_AndSitsAboveUpscaling()
        {
            Assert.That(new PlayerOptions().Port.ShieldBlocksCreatures, Is.False);
            Assert.That(PortOptions.RowText(PortOptions.Row.ShieldCreatures, "Russian"), Is.EqualTo("Щит защищает от зверей"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ShieldCreatures, false, "Russian"), Is.EqualTo("Нет (оригинал)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ShieldCreatures, true, "Russian"), Is.EqualTo("Да"));
            Assert.That(VideoPortRowsLayout.ShieldCreaturesTop, Is.EqualTo(336f), "fix-116: into the removed Locked chests row");
            Assert.That(VideoPortRowsLayout.RowBottom(VideoPortRowsLayout.ShieldCreaturesTop),
                        Is.LessThanOrEqualTo(VideoPortRowsLayout.UpscalingTop + VideoPortRowsLayout.SliderOffset), "no overlap with Upscaling");
            Assert.That(VideoPortRowsLayout.ShieldCreaturesTop + 25f, Is.LessThanOrEqualTo(VideoPortRowsLayout.UpscalingTop), "label boxes");
            Assert.That(VideoPortRowsLayout.FovTop + 25f, Is.LessThanOrEqualTo(VideoPortRowsLayout.ResolutionTop));
            Assert.That((VideoPortRowsLayout.UpscalingTop, VideoPortRowsLayout.FovTop), Is.EqualTo((364f, 392f)), "fix-70 rows unchanged");
        }

        /// <summary>fix-122 [owner]: "Camera in windows", default As in play (no zoom); the Video row above Shield, no overlap.</summary>
        [Test]
        public void WindowCameraRow_DefaultsToNoZoom_AndSitsAboveShield()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.StillWindowCamera, Is.True, "[owner] default: pages and uses keep the play camera");
            int changed = 0;
            port.Changed += () => changed++;
            port.StillWindowCamera = false;
            port.StillWindowCamera = false;
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PortOptions.RowText(PortOptions.Row.WindowCamera, "Russian"), Is.EqualTo("Камера в окнах"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.WindowCamera, true, "Russian"), Is.EqualTo("Как в игре (без приближения)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.WindowCamera, false, "Russian"), Is.EqualTo("Оригинал"));
            Assert.That(VideoPortRowsLayout.WindowCameraTop, Is.EqualTo(308f));
            Assert.That(VideoPortRowsLayout.RowBottom(VideoPortRowsLayout.WindowCameraTop),
                        Is.LessThanOrEqualTo(VideoPortRowsLayout.ShieldCreaturesTop + VideoPortRowsLayout.SliderOffset), "no overlap with Shield");
            Assert.That(VideoPortRowsLayout.WindowCameraTop + 25f, Is.LessThanOrEqualTo(VideoPortRowsLayout.ShieldCreaturesTop), "label boxes");
        }

        /// <summary>
        /// fix-156 [owner]: "NPC shout subtitles", default Yes; an NPC line shows only its own voice set's text (off = G3:
        /// voice only); the hero's lines unchanged; G3's Subtitles option still rules both. The Audio row sits in G3's empty
        /// row between Ambient Volume and Text Language.
        /// </summary>
        [Test]
        public void NpcShoutSubtitlesRow_DefaultsToYes_OwnTextOnly_AndFitsTheAudioPage()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.NpcShoutSubtitles, Is.True, "[owner] default");
            int changed = 0;
            port.Changed += () => changed++;
            port.NpcShoutSubtitles = false;
            port.NpcShoutSubtitles = false;
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PortOptions.RowText(PortOptions.Row.NpcShoutSubtitles, "Russian"), Is.EqualTo("Субтитры реплик NPC"));
            Assert.That(PortOptions.RowText(PortOptions.Row.NpcShoutSubtitles, "English"), Is.EqualTo("NPC shout subtitles"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.NpcShoutSubtitles, false, "Russian"), Is.EqualTo("Нет (оригинал)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.NpcShoutSubtitles, true, "Russian"), Is.EqualTo("Да"));

            Assert.That(PortOptions.SharedLineText(false, true, true), Is.EqualTo(PortOptions.LineText.OwnVoiceSet), "never another set's text");
            Assert.That(PortOptions.SharedLineText(false, true, false), Is.EqualTo(PortOptions.LineText.None), "off = G3");
            Assert.That(PortOptions.SharedLineText(false, false, true), Is.EqualTo(PortOptions.LineText.None), "G3's Subtitles option off");
            Assert.That(PortOptions.SharedLineText(true, true, false), Is.EqualTo(PortOptions.LineText.OwnOrAnotherVoiceSet), "hero unchanged");
            Assert.That(PortOptions.SharedLineText(true, false, true), Is.EqualTo(PortOptions.LineText.None));

            Assert.That(AudioPortRowsLayout.NpcShoutSubtitlesTop, Is.EqualTo(536f));
            Assert.That(AudioPortRowsLayout.NpcShoutSubtitlesTop, Is.GreaterThanOrEqualTo(AudioPortRowsLayout.AmbientTop + 25f), "below Ambient's label");
            Assert.That(AudioPortRowsLayout.NpcShoutSubtitlesTop + AudioPortRowsLayout.SliderOffset,
                        Is.GreaterThanOrEqualTo(AudioPortRowsLayout.AmbientSliderBottom), "below Ambient's slider");
            Assert.That(AudioPortRowsLayout.RowBottom(AudioPortRowsLayout.NpcShoutSubtitlesTop),
                        Is.LessThanOrEqualTo(AudioPortRowsLayout.TextLanguageTop + AudioPortRowsLayout.SliderOffset), "no overlap with Text Language");
            Assert.That(AudioPortRowsLayout.NpcShoutSubtitlesTop + 25f, Is.LessThanOrEqualTo(AudioPortRowsLayout.TextLanguageTop), "label boxes");
        }

        /// <summary>fix-241 [owner]: "Arrows finish downed foes" default Yes; No = the original; the Video row 56 above Show helmet.</summary>
        [Test]
        public void RangedFinishRow_DefaultsToYes_NoIsTheOriginal_AboveShowHelmet()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.RangedFinishesDowned, Is.True, "[owner] default");
            int changed = 0;
            port.Changed += () => changed++;
            port.RangedFinishesDowned = false;
            port.RangedFinishesDowned = false;
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(new Myrtana.Rules.CombatPortOptions().RangedFinishesDowned, Is.False, "the rules' own default is CP 1.75");
            Assert.That(PortOptions.RowText(PortOptions.Row.RangedFinishesDowned, "Russian"), Is.EqualTo("Стрелы добивают лежачих"));
            Assert.That(PortOptions.RowText(PortOptions.Row.RangedFinishesDowned, "English"), Is.EqualTo("Arrows finish downed foes"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.RangedFinishesDowned, false, "Russian"), Is.EqualTo("Нет (оригинал)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.RangedFinishesDowned, true, "English"), Is.EqualTo("Yes"));
            Assert.That(VideoPortRowsLayout.RangedFinishTop, Is.EqualTo(56f));
            Assert.That(VideoPortRowsLayout.RangedFinishTop, Is.GreaterThan(25f), "inside the page's frame");
            Assert.That(VideoPortRowsLayout.RowBottom(VideoPortRowsLayout.RangedFinishTop),
                        Is.LessThanOrEqualTo(VideoPortRowsLayout.ShowHelmetTop + VideoPortRowsLayout.SliderOffset), "no overlap with Show helmet");
        }

        /// <summary>fix-232 [owner]: "Show helmet" default Yes (G3); the Video row 84 above Torch glow.</summary>
        [Test]
        public void ShowHelmetRow_DefaultsToYes_TheOriginal_AboveTorchGlow()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.ShowHelmet, Is.True, "[owner] default");
            int changed = 0;
            port.Changed += () => changed++;
            port.ShowHelmet = false;
            port.ShowHelmet = false;
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PortOptions.RowText(PortOptions.Row.ShowHelmet, "Russian"), Is.EqualTo("Показывать шлем"));
            Assert.That(PortOptions.RowText(PortOptions.Row.ShowHelmet, "English"), Is.EqualTo("Show helmet"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ShowHelmet, true, "Russian"), Is.EqualTo("Да (оригинал)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ShowHelmet, false, "English"), Is.EqualTo("No"));
            Assert.That(VideoPortRowsLayout.ShowHelmetTop, Is.EqualTo(84f));
            Assert.That(VideoPortRowsLayout.ShowHelmetTop, Is.GreaterThan(25f), "inside the page's frame");
            Assert.That(VideoPortRowsLayout.RowBottom(VideoPortRowsLayout.ShowHelmetTop),
                        Is.LessThanOrEqualTo(VideoPortRowsLayout.TorchGlowTop + VideoPortRowsLayout.SliderOffset), "no overlap with Torch glow");
        }

        /// <summary>fix-182 [owner]: "Torch glow" default No (no halo around the torch flame); Yes = the original; the Video row 112.</summary>
        [Test]
        public void TorchGlowRow_DefaultsToNo_YesIsTheOriginal_AboveKeepWounds()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.TorchGlow, Is.False, "[owner] default");
            int changed = 0;
            port.Changed += () => changed++;
            port.TorchGlow = true;
            port.TorchGlow = true;
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PortOptions.RowText(PortOptions.Row.TorchGlow, "Russian"), Is.EqualTo("Ореол факела"));
            Assert.That(PortOptions.RowText(PortOptions.Row.TorchGlow, "English"), Is.EqualTo("Torch glow"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.TorchGlow, true, "Russian"), Is.EqualTo("Да (оригинал)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.TorchGlow, false, "English"), Is.EqualTo("No"));
            Assert.That(VideoPortRowsLayout.TorchGlowTop, Is.EqualTo(112f));
            Assert.That(VideoPortRowsLayout.RowBottom(VideoPortRowsLayout.TorchGlowTop),
                        Is.LessThanOrEqualTo(VideoPortRowsLayout.KeepWoundsTop + VideoPortRowsLayout.SliderOffset), "no overlap with Keep wounds");
        }
    }
}
