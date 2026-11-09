using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-138 [owner]: the main menu's Continue — newest save of any kind, fallbacks, menu place, option.</summary>
    public class ContinueGameTests
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        static KeyValuePair<string, SaveHeader> Save(string slot, double minutes) =>
            new KeyValuePair<string, SaveHeader>(slot, new SaveHeader(null, 60, "1|08:00|Faring", T0.AddMinutes(minutes)));

        [Test]
        public void Newest_IsTheLatestSaveTime_OfAnyKind()
        {
            var saves = new[] { Save("save001", 5), Save(SaveGameList.QuickSlot, 3), Save(SaveGameList.AutoSlot, 9), Save("save002", 1) };
            Assert.That(ContinueGame.Newest(saves), Is.EqualTo(SaveGameList.AutoSlot), "the autosave counts");
            saves[1] = Save(SaveGameList.QuickSlot, 12);
            Assert.That(ContinueGame.Newest(saves), Is.EqualTo(SaveGameList.QuickSlot), "the quicksave counts");
            saves[3] = Save("save002", 20);
            Assert.That(ContinueGame.Newest(saves), Is.EqualTo("save002"), "a manual save counts");
        }

        [Test]
        public void Candidates_AreNewestFirst_ForTheFallback()
        {
            var saves = new[] { Save("save001", 5), Save(SaveGameList.QuickSlot, 3), Save(SaveGameList.AutoSlot, 9), Save("save002", 1) };
            Assert.That(ContinueGame.Candidates(saves), Is.EqualTo(new[] { SaveGameList.AutoSlot, "save001", SaveGameList.QuickSlot, "save002" }));
        }

        [Test]
        public void SameSaveTime_PrefersThePlayersOwnSave()
        {
            var saves = new[] { Save(SaveGameList.AutoSlot, 4), Save(SaveGameList.QuickSlot, 4), Save("save003", 4), Save("save007", 4) };
            Assert.That(ContinueGame.Candidates(saves), Is.EqualTo(new[] { "save007", "save003", SaveGameList.QuickSlot, SaveGameList.AutoSlot }));
        }

        [Test]
        public void NoSave_NothingToContinue()
        {
            Assert.That(ContinueGame.Newest(Array.Empty<KeyValuePair<string, SaveHeader>>()), Is.Null);
            Assert.That(ContinueGame.Candidates(null), Is.Empty);
            var broken = new[] { new KeyValuePair<string, SaveHeader>("save001", null), new KeyValuePair<string, SaveHeader>("", new SaveHeader(null, 0)) };
            Assert.That(ContinueGame.Candidates(broken), Is.Empty, "an unreadable header or a nameless slot is no candidate");
        }

        [Test]
        public void SaveWithoutTime_ComesLast()
        {
            var saves = new[] { new KeyValuePair<string, SaveHeader>("save001", new SaveHeader(null, 0)), Save(SaveGameList.AutoSlot, -600) };
            Assert.That(ContinueGame.Newest(saves), Is.EqualTo(SaveGameList.AutoSlot));
        }

        [Test]
        public void ButtonText_RussianEnglish_OthersFallBackToEnglish()
        {
            Assert.That(ContinueGame.ButtonText("Russian"), Is.EqualTo("Продолжить"));
            Assert.That(ContinueGame.ButtonText("English"), Is.EqualTo("Continue"));
            Assert.That(ContinueGame.ButtonText("German"), Is.EqualTo("Fortsetzen"));
            Assert.That(ContinueGame.ButtonText("Klingon"), Is.EqualTo("Continue"));
            Assert.That(ContinueGame.ButtonText(null), Is.EqualTo("Continue"));
        }

        [Test]
        public void MenuButtons_ContinueAtTheTitleAndAfterDeath_NeverInPlay()
        {
            Assert.That(MainMenuButtons.For(atTitle: true, gameOver: false, continueOffered: true).Continue, Is.True);
            Assert.That(MainMenuButtons.For(atTitle: false, gameOver: true, continueOffered: true).Continue, Is.True);
            Assert.That(MainMenuButtons.For(atTitle: false, gameOver: false, continueOffered: true).Continue, Is.False, "Resume is there");
            Assert.That(MainMenuButtons.For(atTitle: true, gameOver: false, continueOffered: false).Continue, Is.False, "option off: G3's menu");
            Assert.That(MainMenuButtons.For(atTitle: true, gameOver: false).Continue, Is.False);
        }

        /// <summary>G3's main column (g3.gui.res IDD_MENU_MAIN_2): 32 px buttons, no gaps, Quit 4 px lower.</summary>
        static List<MenuButtonColumn.Slot> MainColumn() => new List<MenuButtonColumn.Slot>
        {
            new MenuButtonColumn.Slot("NEW_GAME", 368, 32), new MenuButtonColumn.Slot("LOAD", 400, 32),
            new MenuButtonColumn.Slot("SAVE", 432, 32), new MenuButtonColumn.Slot("OPTIONS", 464, 32),
            new MenuButtonColumn.Slot("CREDITS", 496, 32), new MenuButtonColumn.Slot("QUIT", 532, 32),
        };

        [Test]
        public void Column_ContinueOnTop_StaysInG3sSpan_ClearOfLogoAndBalancing()
        {
            var fitted = MenuButtonColumn.InsertBefore(MainColumn(), "NEW_GAME", "CONTINUE", minGap: 0f);
            Assert.That(fitted, Is.Not.Null);
            var tops = fitted.Tops;
            Assert.That(fitted.Height, Is.EqualTo(28), "seven touching buttons in 196 px");
            Assert.That(fitted.Height, Is.GreaterThanOrEqualTo(MenuButtonColumn.MinHeight));
            Assert.That(tops["CONTINUE"], Is.EqualTo(368), "starts where G3's column does: the 'Gothic' logo ends above it");
            Assert.That(tops["QUIT"] + fitted.Height, Is.LessThanOrEqualTo(564), "ends where G3's does");
            Assert.That(tops["QUIT"] + fitted.Height, Is.LessThan(588), "clear of the Alternative Balancing heading");
            var order = new[] { "CONTINUE", "NEW_GAME", "LOAD", "SAVE", "OPTIONS", "CREDITS", "QUIT" };
            for (int i = 1; i < order.Length; i++)
                Assert.That(tops[order[i]] - tops[order[i - 1]], Is.GreaterThanOrEqualTo(fitted.Height), order[i] + ": no overlap");
        }

        [Test]
        public void InsertBefore_UnknownAnchor_GoesFirst_AndInsertStaysUnchanged()
        {
            var first = MenuButtonColumn.InsertBefore(MainColumn(), "MISSING", "X", minGap: 0f);
            Assert.That(first.Tops["X"], Is.LessThan(first.Tops["NEW_GAME"]));
            var after = MenuButtonColumn.InsertBefore(MainColumn(), "LOAD", "X", minGap: 0f);
            Assert.That(after.Tops["NEW_GAME"], Is.LessThan(after.Tops["X"]));
            Assert.That(after.Tops["X"], Is.LessThan(after.Tops["LOAD"]));
            var below = MenuButtonColumn.Insert(MainColumn(), "NEW_GAME", "X", minGap: 0f);
            Assert.That(below.Tops["X"], Is.EqualTo(after.Tops["X"]), "Insert after New Game = InsertBefore Load");
        }

        [Test]
        public void Option_DefaultsOn_RowAboveCameraInWindows()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.ContinueButton, Is.True, "[owner] default: Continue on the main menu");
            int changed = 0;
            port.Changed += () => changed++;
            port.ContinueButton = false;
            port.ContinueButton = false;
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(PortOptions.RowText(PortOptions.Row.ContinueButton, "Russian"), Is.EqualTo("Кнопка «Продолжить»"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ContinueButton, false, "English"), Is.EqualTo("No (original)"), "position 0 = original");
            Assert.That(PortOptions.ValueText(PortOptions.Row.ContinueButton, true, "Russian"), Is.EqualTo("Да"));
            Assert.That(VideoPortRowsLayout.ContinueButtonTop, Is.EqualTo(280f));
            Assert.That(VideoPortRowsLayout.RowBottom(VideoPortRowsLayout.ContinueButtonTop),
                        Is.LessThanOrEqualTo(VideoPortRowsLayout.WindowCameraTop + VideoPortRowsLayout.SliderOffset), "no overlap with Camera in windows");
            Assert.That(VideoPortRowsLayout.ContinueButtonTop + 25f, Is.LessThanOrEqualTo(VideoPortRowsLayout.WindowCameraTop), "label boxes");
        }
    }
}
