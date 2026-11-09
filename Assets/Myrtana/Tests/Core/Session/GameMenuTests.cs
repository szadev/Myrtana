using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class GameMenuTests
    {
        /// <summary>G3's English strings of the menu, as in the string table (line breaks written as \n).</summary>
        sealed class Strings : ILocalizer
        {
            static readonly Dictionary<string, string> Texts = new Dictionary<string, string>
            {
                ["HUD_File_SaveGame_Empty"] = "New Savegame",
                ["HUD_File_SaveGame_Format"] = "$(day). day\\n$(hour):$(minute)",
                ["HUD_File_SaveGame_Format2"] = "Savegame No. ",
                ["HUD_QuickSave_Game"] = "Quick save game",
                ["MAP_World_Myrtana_Faring_01"] = "Faring",
            };

            public string Text(TextId id) => TryText(id, out var t) ? t : "[" + id.Value + "]";
            public bool TryText(TextId id, out string text) => Texts.TryGetValue(id.Value, out text);
            public string Stage(TextId id) => null;
        }

        static KeyValuePair<string, SaveHeader> Save(string slot, string title, int minute) =>
            new KeyValuePair<string, SaveHeader>(slot, new SaveHeader(new PackStamp[0], 0, title,
                                                                      new DateTime(2026, 10, 11, 20, minute, 0, DateTimeKind.Utc)));

        [Test]
        public void Esc_GoesBackOnePageAndClosesFromTheMainPage()
        {
            var menu = new GameMenuState();
            int changes = 0;
            menu.Changed += () => changes++;
            menu.Open();
            menu.Show(MenuPage.Options);
            menu.Show(MenuPage.Audio);

            Assert.That(menu.Back(), Is.True);
            Assert.That(menu.Page, Is.EqualTo(MenuPage.Options));
            Assert.That(menu.Back(), Is.True);
            Assert.That(menu.Page, Is.EqualTo(MenuPage.Main));
            menu.Show(MenuPage.Load);
            Assert.That(menu.Back(), Is.True);
            Assert.That(menu.Back(), Is.False, "Esc on the main page resumes the game");
            Assert.That(menu.IsOpen, Is.False);
            Assert.That(changes, Is.EqualTo(8));
        }

        [Test]
        public void ConfirmBox_TakesEscFirstAndRunsItsActionOnlyOnYes()
        {
            var menu = new GameMenuState();
            menu.Open();
            menu.Show(MenuPage.Save);
            int confirmed = 0;
            menu.Ask(new MenuRequest(new TextId("HUD_Confirm_Overwrite"), () => confirmed++));

            Assert.That(menu.Back(), Is.True);
            Assert.That(menu.Request, Is.Null);
            Assert.That(menu.Page, Is.EqualTo(MenuPage.Save), "Esc answers No and stays on the page");
            Assert.That(confirmed, Is.Zero);

            menu.Ask(new MenuRequest(new TextId("HUD_Confirm_Overwrite"), () => { confirmed++; menu.Close(); }));
            menu.Answer(true);
            Assert.That(confirmed, Is.EqualTo(1));
            Assert.That(menu.IsOpen, Is.False, "the action may close the menu");
        }

        [Test]
        public void SaveList_OffersANewSlotAndOnlyNumberedSlotsNewestFirst()
        {
            var slots = new[]
            {
                Save("save001", "Day 1, 08:00", 1), Save("quick", "Day 2, 13:05", 5), Save("save003", "Day 3, 21:30", 3),
                Save("auto", "Day 2, 10:00", 4), Save("notes", "x", 9),
            };
            var rows = SaveGameList.For(true, slots, new Strings());

            Assert.That(rows.Select(r => r.Slot), Is.EqualTo(new[] { "save004", "save003", "save001" }));
            Assert.That(rows[0].Kind, Is.EqualTo(SaveSlotKind.New));
            Assert.That(rows[0].Name, Is.EqualTo("New Savegame"));
            Assert.That(rows[0].Detail, Is.Empty);
            Assert.That(rows[1].Name, Is.EqualTo("Savegame No. 3"));
            Assert.That(rows[1].GameTime, Is.EqualTo("3. day, 21:30"), "G3's two-line format on one line (compact rows)");
            Assert.That(rows[1].Date, Is.Not.Empty);
        }

        [Test]
        public void LoadList_PutsTheQuicksaveFirstThenTheAutosaveThenNumberedSlotsNewestFirst()
        {
            // fix-107 [owner]: numbered saves newest first (G3 sorted by name: highest number first). The newest save does
            // not move the quicksave/autosave rows down.
            var slots = new[]
            {
                Save("save001", "Day 1, 08:00", 9), Save("save010", "Day 5, 08:00", 2), Save("auto", "Day 2, 10:00", 4),
                Save("save002", "Day 3, 08:00", 8), Save("quick", "Day 2, 13:05", 1), Save("scn_ch04", "Day 4, 08:00", 7),
            };
            var rows = SaveGameList.For(false, slots, new Strings(), "English");

            Assert.That(rows.Select(r => r.Slot), Is.EqualTo(new[] { "quick", "auto", "save001", "save002", "save010" }),
                        "runner checkpoints (scn_*) and other files are not menu saves");
            Assert.That(rows.Select(r => r.Kind), Is.EqualTo(new[] { SaveSlotKind.Quick, SaveSlotKind.Auto, SaveSlotKind.Numbered, SaveSlotKind.Numbered, SaveSlotKind.Numbered }));
            Assert.That(rows[0].Name, Is.EqualTo("Quick save game"));
            Assert.That(rows[0].GameTime, Is.EqualTo("2. day, 13:05"));
            Assert.That(rows[0].Date, Is.Not.Empty);
            Assert.That(rows[1].Name, Is.EqualTo("Autosave"));
            Assert.That(rows[1].GameTime, Is.EqualTo("2. day, 10:00"));
            Assert.That(rows[1].Date, Is.Not.Empty);
        }

        [Test]
        public void LoadList_WithOnlyAnAutosaveStartsWithIt()
        {
            var rows = SaveGameList.For(false, new[] { Save("save003", "Day 3, 08:00", 9), Save("auto", "Day 1, 08:00", 1) }, new Strings(), "Russian");
            Assert.That(rows.Select(r => r.Slot), Is.EqualTo(new[] { "auto", "save003" }));
            Assert.That(rows[0].Name, Is.EqualTo("Автосохранение"));
        }

        [Test]
        public void SaveList_NeverOffersTheQuicksaveOrAutosave()
        {
            var slots = new[] { Save("quick", "Day 2, 13:05", 9), Save("auto", "Day 2, 10:00", 8), Save("save002", "Day 1, 08:00", 1), Save("save005", "Day 1, 09:00", 2) };
            var rows = SaveGameList.For(true, slots, new Strings(), "English");
            Assert.That(rows.Select(r => r.Slot), Is.EqualTo(new[] { "save006", "save005", "save002" }));
        }

        [Test]
        public void LoadList_HasOneQuicksaveAndOneAutosave_TheOldSecondFilesAreNotListed()
        {
            // fix-107: fix-82's rotation files quick2 / auto2 (after the migration: the older saves) are left on disk unlisted
            var slots = new[]
            {
                Save("save001", "Day 1, 08:00", 1), Save("quick", "Day 2, 13:05", 5), Save("quick2", "Day 2, 14:00", 7),
                Save("auto", "Day 2, 15:00", 9), Save("auto2", "Day 2, 12:00", 3),
            };
            var rows = SaveGameList.For(false, slots, new Strings(), "English");

            Assert.That(rows.Select(r => r.Slot), Is.EqualTo(new[] { "quick", "auto", "save001" }));
            Assert.That(rows.Select(r => r.Name), Is.EqualTo(new[] { "Quick save game", "Autosave", "Savegame No. 1" }), "no numbers, no (latest) mark");
            Assert.That(SaveGameList.For(false, slots, new Strings(), "Russian")[1].Name, Is.EqualTo("Автосохранение"));
            Assert.That(SaveGameList.For(true, slots, new Strings(), "English").Select(r => r.Slot), Is.EqualTo(new[] { "save002", "save001" }));
            Assert.That(SaveGameList.KindOf("quick2"), Is.EqualTo(SaveSlotKind.Numbered));
            Assert.That(SaveGameList.TryNumber("quick2", out _), Is.False);
        }

        [Test]
        public void Rows_ShowThePlaceAfterTheGameTime_WhenTheTitleHasOne()
        {
            var slots = new[]
            {
                Save("quick", SaveTitle.Format(5, 21, 52, new TextId("MAP_World_Myrtana_Faring_01")), 5),
                Save("save001", "Day 1, 08:00", 1), // an older save: no place
                Save("save002", SaveTitle.Format(2, 9, 0, new TextId("MAP_Unknown")), 2), // no text: no place
            };
            var rows = SaveGameList.For(false, slots, new Strings(), "English");

            Assert.That(rows[0].Place, Is.EqualTo("Faring"));
            Assert.That(rows[0].Detail, Is.EqualTo("5. day, 21:52 · Faring"));
            Assert.That(rows.Single(r => r.Slot == "save001").Detail, Is.EqualTo("1. day, 08:00"));
            Assert.That(rows.Single(r => r.Slot == "save002").Detail, Is.EqualTo("2. day, 09:00"));
        }

        [Test]
        public void DelKey_DeletesOnlyNumberedSaves()
        {
            var slots = new[] { Save("quick", "Day 2, 13:05", 5), Save("auto", "Day 2, 15:00", 9), Save("save001", "Day 1, 08:00", 1) };
            var rows = SaveGameList.For(false, slots, new Strings(), "English");
            Assert.That(rows.Select(r => r.DeletableByKey), Is.EqualTo(new[] { false, false, true }));
            Assert.That(SaveGameList.For(true, slots, new Strings())[0].DeletableByKey, Is.False, "not the new slot");
        }

        [Test]
        public void Migration_TheNewerOfTheTwoFilesKeepsTheSlotsName()
        {
            var t = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            Assert.That(SaveSlotMigration.LegacyWins(t, null), Is.False, "no second file: nothing to do");
            Assert.That(SaveSlotMigration.LegacyWins(t, t.AddMinutes(-1)), Is.False, "the slot is the newer one");
            Assert.That(SaveSlotMigration.LegacyWins(t, t), Is.False);
            Assert.That(SaveSlotMigration.LegacyWins(t, t.AddMinutes(1)), Is.True, "the second file is newer");
            Assert.That(SaveSlotMigration.LegacyWins(null, t), Is.True, "only the second file (or the slot is unreadable)");
            Assert.That(SaveSlotMigration.Legacy("quick"), Is.EqualTo("quick2"));
            Assert.That(SaveSlotMigration.Slots, Is.EqualTo(new[] { "quick", "auto" }));
        }

        [Test]
        public void Migration_SwapsTheFilesAndPictures_AndDeletesNothing()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "g3-fix107-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var t = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
                void Write(string name, string body) => System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name), body);
                string Read(string name) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir, name));
                Write("quick.sav", "old quick"); Write("quick.jpg", "old pic");
                Write("quick2.sav", "new quick"); Write("quick2.jpg", "new pic"); // the second quicksave is newer: they swap
                Write("auto2.sav", "only auto"); Write("auto2.jpg", "auto pic");  // only the second autosave: it becomes "auto"
                Write("save001.sav", "numbered");
                // the save time is read from the file each time (it moves with the file)
                DateTime? SavedAt(string slot)
                {
                    string path = System.IO.Path.Combine(dir, slot + ".sav");
                    if (!System.IO.File.Exists(path)) return null;
                    string body = System.IO.File.ReadAllText(path);
                    return body == "old quick" ? t : body == "new quick" ? t.AddMinutes(3) : body == "only auto" ? t : (DateTime?)null;
                }

                var changed = SaveSlotMigration.Run(dir, SavedAt, ".sav", ".jpg");

                Assert.That(changed, Is.EqualTo(new[] { "quick", "auto" }));
                Assert.That(Read("quick.sav"), Is.EqualTo("new quick"));
                Assert.That(Read("quick.jpg"), Is.EqualTo("new pic"), "the picture goes with its save");
                Assert.That(Read("quick2.sav"), Is.EqualTo("old quick"), "the older save stays on disk, untouched");
                Assert.That(Read("quick2.jpg"), Is.EqualTo("old pic"));
                Assert.That(Read("auto.sav"), Is.EqualTo("only auto"));
                Assert.That(Read("auto.jpg"), Is.EqualTo("auto pic"));
                Assert.That(System.IO.File.Exists(System.IO.Path.Combine(dir, "auto2.sav")), Is.False);
                Assert.That(Read("save001.sav"), Is.EqualTo("numbered"));
                Assert.That(System.IO.Directory.GetFiles(dir).Length, Is.EqualTo(7), "nothing deleted, no leftovers");
                Assert.That(SaveSlotMigration.Run(dir, SavedAt, ".sav", ".jpg"), Is.Empty, "a second start changes nothing");
                Assert.That(Read("quick.sav"), Is.EqualTo("new quick"));
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }

        [Test]
        public void SaveList_ShowsSevenOrEightBiggerRowsWhereG3ShowedFive()
        {
            int rows = Myrtana.Ui.Skin.SaveListLayout.VisibleRows(Myrtana.Ui.Skin.SaveListLayout.Height);
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.G3VisibleRows, Is.EqualTo(5), "G3: 106 px items at a 110 px pitch");
            Assert.That(rows, Is.InRange(7, 8), "fix-112 [owner]: a bit bigger than fix-107's ten rows");
            Assert.That(rows * Myrtana.Ui.Skin.SaveListLayout.Pitch - Myrtana.Ui.Skin.SaveListLayout.RowSpacing,
                        Is.LessThanOrEqualTo(Myrtana.Ui.Skin.SaveListLayout.Height), "whole rows inside the list control");
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.ThumbSize, Is.GreaterThan(46f), "a bigger picture than fix-107's");
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.NameSize, Is.GreaterThan(16));
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.DetailSize, Is.GreaterThan(14));
            float Line(float size) => Myrtana.Ui.Skin.G3FontMetrics.LineHeight(size);
            const int N = Myrtana.Ui.Skin.SaveListLayout.NameSize, D = Myrtana.Ui.Skin.SaveListLayout.DetailSize;
            // a whole row: name, then detail
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.NameTop + Line(N), Is.LessThanOrEqualTo(Myrtana.Ui.Skin.SaveListLayout.DetailTop));
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.DetailTop + Line(D), Is.LessThanOrEqualTo(Myrtana.Ui.Skin.SaveListLayout.RowHeight));
            // a half row (quicksave / autosave): name, date, detail
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.HalfNameTop + Line(N), Is.LessThanOrEqualTo(Myrtana.Ui.Skin.SaveListLayout.HalfDateTop + 0.5f));
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.HalfDateTop + Line(D), Is.LessThanOrEqualTo(Myrtana.Ui.Skin.SaveListLayout.HalfDetailTop + 0.5f));
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.HalfDetailTop + Line(D), Is.LessThanOrEqualTo(Myrtana.Ui.Skin.SaveListLayout.RowHeight));
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.ThumbTop + Myrtana.Ui.Skin.SaveListLayout.ThumbSize, Is.LessThanOrEqualTo(Myrtana.Ui.Skin.SaveListLayout.RowHeight));
            // the two halves and the divider fill the row width
            Assert.That(2 * Myrtana.Ui.Skin.SaveListLayout.HalfWidth + Myrtana.Ui.Skin.SaveListLayout.DividerWidth,
                        Is.EqualTo(Myrtana.Ui.Skin.SaveListLayout.RowWidth).Within(0.01f));
            Assert.That(Myrtana.Ui.Skin.SaveListLayout.CellLeft(1), Is.EqualTo(Myrtana.Ui.Skin.SaveListLayout.HalfWidth + Myrtana.Ui.Skin.SaveListLayout.DividerWidth));
        }

        [Test]
        public void LoadGrid_PutsQuicksaveAndAutosaveOnOneLineAndArrowsMoveBetweenTheHalves()
        {
            // quick, auto, three numbered saves
            var grid = new Myrtana.Ui.Skin.SaveListGrid(new[] { 0, 1, -1, -1, -1 });
            Assert.That(grid.LineCount, Is.EqualTo(4));
            Assert.That(grid[0].ToString(), Is.EqualTo("0:0"));
            Assert.That(grid[1].ToString(), Is.EqualTo("0:1"));
            Assert.That(grid[2].ToString(), Is.EqualTo("1:-1"));
            Assert.That(grid.MoveSide(0, 1), Is.EqualTo(1), "Right: quicksave -> autosave");
            Assert.That(grid.MoveSide(1, -1), Is.EqualTo(0), "Left: autosave -> quicksave");
            Assert.That(grid.MoveSide(1, 1), Is.EqualTo(1), "Right on the right half stays");
            Assert.That(grid.MoveSide(3, 1), Is.EqualTo(3), "a whole row has no halves");
            Assert.That(grid.MoveLines(1, 1, 1), Is.EqualTo(2), "Down from the autosave: the first numbered save");
            Assert.That(grid.MoveLines(2, -1, 1), Is.EqualTo(1), "Up goes back to the half last used");
            Assert.That(grid.MoveLines(2, -1, 0), Is.EqualTo(0));
            Assert.That(grid.MoveLines(4, -10, 0), Is.EqualTo(0), "Page Up clamps at the first line");
            Assert.That(grid.MoveLines(0, 10, 0), Is.EqualTo(4), "Page Down clamps at the last line");

            var autoOnly = new Myrtana.Ui.Skin.SaveListGrid(new[] { 1, -1 });
            Assert.That(autoOnly[0].ToString(), Is.EqualTo("0:1"), "an autosave without a quicksave keeps the right half");
            Assert.That(autoOnly.MoveLines(1, -1, 0), Is.EqualTo(0), "Up reaches it from the left side too");
            Assert.That(autoOnly.MoveSide(0, -1), Is.EqualTo(0), "the empty left half is not selectable");

            var saving = new Myrtana.Ui.Skin.SaveListGrid(new[] { -1, -1, -1 }); // Save page: "New Savegame", numbered saves
            Assert.That(saving.LineCount, Is.EqualTo(3));
            Assert.That(saving.MoveLines(0, 1, 0), Is.EqualTo(1));
        }

        [Test]
        public void ItemsOnLines_AreTheRowsWhosePicturesLoad()
        {
            // fix-222: quick+auto on line 0, then numbered saves one per line
            var grid = new Myrtana.Ui.Skin.SaveListGrid(new[] { 0, 1, -1, -1, -1, -1, -1 });
            Assert.That(grid.ItemsOnLines(0, 2), Is.EqualTo(new[] { 0, 1, 2 }), "both halves of the split line");
            Assert.That(grid.ItemsOnLines(3, 2, margin: 1), Is.EqualTo(new[] { 3, 4, 5, 6 }), "the margin, clamped at the end");
            Assert.That(grid.ItemsOnLines(-3, 1, margin: 1), Is.Empty, "nothing above the first line");
            Assert.That(new Myrtana.Ui.Skin.SaveListGrid(new int[0]).ItemsOnLines(0, 7, 2), Is.Empty);
        }

        [Test]
        public void PlaceNear_IsTheNearestNamedVisibleMarkerOfTheLargestMap()
        {
            var world = new Myrtana.Rules.MapDef("It_Map_World", "G3_Map_World", -2364f, 1711f, 1212f, -1865f, new[]
            {
                new Myrtana.Rules.MapMarkerDef(new TextId("MAP_World_Myrtana_Faring_01"), 610f, 561f, true),
                new Myrtana.Rules.MapMarkerDef(new TextId("MAP_World_Myrtana_Gotha_01"), 334f, 479f, true),
                new Myrtana.Rules.MapMarkerDef(new TextId("MAP_World_Myrtana_$_Pass_Faring_01"), 567f, 632f, false), // hidden
                new Myrtana.Rules.MapMarkerDef(new TextId("MAP_World_Myrtana_Okara_01"), 560f, 570f, true),          // no text
            });
            var graves = new Myrtana.Rules.MapDef("It_Map_Ancestorgraves", "G3_Map_Ancestorgraves", 158.8f, 1768f, 1112f, 814.3f, new[]
            {
                new Myrtana.Rules.MapMarkerDef(new TextId("MAP_AncestorGraves_Nordmar_Cave_01"), 631f, 1005f, true),
            });
            var maps = new Myrtana.Rules.WorldMaps(new[] { graves, world }, () => null);
            bool Named(TextId id) => id.Value != "MAP_World_Myrtana_Okara_01";

            Assert.That(maps.PlaceNear(568f, 576f, Named).Value, Is.EqualTo("MAP_World_Myrtana_Faring_01"), "Faring's lower town; the hero needs no map");
            Assert.That(maps.PlaceNear(350f, 470f, Named).Value, Is.EqualTo("MAP_World_Myrtana_Gotha_01"));
            Assert.That(maps.PlaceNear(630f, 1000f, Named).Value, Is.EqualTo("MAP_World_Myrtana_Faring_01"),
                        "the larger map wins where both contain the point");
            Assert.That(maps.PlaceNear(5000f, 5000f, Named).IsEmpty, Is.True, "outside every map: no place");
        }

        [Test]
        public void Autosave_IsDueAfterTheIntervalAndOnlyWhenSavingIsAllowed()
        {
            var timer = new AutosaveSchedule(600f);
            timer.Restart(100);
            Assert.That(timer.ShouldSave(699, null, false), Is.False, "not before the interval");
            Assert.That(timer.ShouldSave(700, null, false), Is.True);
            Assert.That(timer.ShouldSave(700, GameSession.BlockedByDialog, false), Is.False, "not during a conversation (or its cutscene)");
            Assert.That(timer.ShouldSave(700, CombatWiring.BlockedByCombat, false), Is.False, "not during a fight");
            Assert.That(timer.ShouldSave(700, CombatWiring.BlockedByDeath, false), Is.False, "not with the hero dead");
            Assert.That(timer.ShouldSave(700, null, true), Is.False, "not with the menu open or an area loading");
            Assert.That(timer.ShouldSave(950, null, false), Is.True, "a due autosave waits out the block and is written at once after it");
        }

        [Test]
        public void Autosave_PlayersOwnSavesDoNotMoveItOnlyTheAutosaveAndLoadsDo()
        {
            var timer = new AutosaveSchedule(600f);
            timer.Restart(0);
            timer.Saved(550, autosave: false); // the player quicksaved (or saved in the menu)
            Assert.That(timer.ShouldSave(600, null, false), Is.True, "fix-112: a quicksave does not push the autosave back");
            timer.Saved(610, autosave: true);
            Assert.That(timer.ShouldSave(1200, null, false), Is.False, "the next autosave is one interval after the last one");
            Assert.That(timer.ShouldSave(1210, null, false), Is.True);
            timer.Restart(1000); // a game was loaded
            Assert.That(timer.ShouldSave(1599, null, false), Is.False);
            timer.IntervalSeconds = 0;
            Assert.That(timer.ShouldSave(1e9, null, false), Is.False, "interval 0 = off");
            Assert.That(new AutosaveSchedule().IntervalSeconds, Is.EqualTo(AutosaveSchedule.DefaultMinutes * 60f));
        }

        /// <summary>
        /// fix-112 [owner]: a 10-minute session the way the owner plays (Player.log of Demo_2026-10-18c: a quicksave or a
        /// menu save every 1-4 minutes, conversations, fights, the menu) must end with an autosave, written as soon as
        /// the block around the due moment is over; a 22-minute session gets two.
        /// </summary>
        [Test]
        public void Autosave_TenMinutesOfTypicalPlayWithBlocksAndFrequentSavesWritesIt()
        {
            var timer = new AutosaveSchedule(600f);
            const double start = 37.5; // real time when the game was loaded (after the intro and the loading screen)
            timer.Restart(start);
            var autosaves = new List<double>();
            int ownSaves = 0;
            double nextOwnSave = start + 90;
            for (double t = start; t < start + 22 * 60; t += 1.0 / 30)
            {
                double s = t - start;
                bool menu = In(s, 300, 340) || In(s, 640, 660);            // Esc menu (menu save at 330), map/inventory
                string block = In(s, 60, 180) || In(s, 400, 470) ? GameSession.BlockedByDialog
                             : In(s, 500, 560) || In(s, 590, 640) || In(s, 1190, 1215) ? CombatWiring.BlockedByCombat
                             : null;
                if (!menu && block == null && t >= nextOwnSave) { timer.Saved(t, autosave: false); ownSaves++; nextOwnSave = t + 100; }
                if (menu && In(s, 330, 330.05)) { timer.Saved(t, autosave: false); ownSaves++; }
                if (timer.ShouldSave(t, block, menu)) { autosaves.Add(s); timer.Saved(t, autosave: true); }
            }
            Assert.That(ownSaves, Is.GreaterThan(8), "the player saved often");
            Assert.That(autosaves.Count, Is.EqualTo(2), "one autosave per 10 minutes");
            Assert.That(autosaves[0], Is.EqualTo(660).Within(0.1), "due at 600 s, waits out the fight (to 640) and the menu (to 660)");
            Assert.That(autosaves[1], Is.EqualTo(1260).Within(0.1), "the next one 10 minutes after the first");

            bool In(double x, double from, double to) => x >= from && x < to;
        }

        [Test]
        public void SlotNames_AreNumberedFromOne()
        {
            Assert.That(SaveGameList.NextFreeSlot(new string[0]), Is.EqualTo("save001"));
            Assert.That(SaveGameList.NextFreeSlot(new[] { "quick", "save002", "save010" }), Is.EqualTo("save011"));
            Assert.That(SaveGameList.TryNumber("save007", out int n) && n == 7, Is.True);
            Assert.That(SaveGameList.TryNumber("save", out _), Is.False);
            Assert.That(SaveGameList.TryNumber("save-1", out _), Is.False);
        }

        [Test]
        public void SaveTitle_RoundTripsAndUnknownTitlesStayAsTheyAre()
        {
            Assert.That(SaveTitle.TryParse(SaveTitle.Format(12, 7, 5), out int d, out int h, out int m), Is.True);
            Assert.That((d, h, m), Is.EqualTo((12, 7, 5)));
            Assert.That(SaveTitle.TryParse("Tag 1", out _, out _, out _), Is.False);
            // fix-107: the place's text id after " @ "; older titles have none
            string title = SaveTitle.Format(3, 8, 15, new TextId("MAP_World_Myrtana_Faring_01"));
            Assert.That(title, Is.EqualTo("Day 3, 08:15 @ MAP_World_Myrtana_Faring_01"));
            Assert.That(SaveTitle.TryParse(title, out d, out h, out m, out var place), Is.True);
            Assert.That((d, h, m, place.Value), Is.EqualTo((3, 8, 15, "MAP_World_Myrtana_Faring_01")));
            Assert.That(SaveTitle.TryParse("Day 3, 08:15", out _, out _, out _, out place) && place.IsEmpty, Is.True);
            Assert.That(SaveTitle.Format(3, 8, 15), Is.EqualTo("Day 3, 08:15"));
            Assert.That(SaveGameList.GameTime("Tag 1", new Strings()), Is.EqualTo("Tag 1"));
        }

        [Test]
        public void TextTemplate_FillsPlaceholdersAndLineBreaks()
        {
            Assert.That(TextTemplate.Fill("$(day). день\\n$(hour):$(minute)", ("day", "2"), ("hour", "08"), ("minute", "15")),
                        Is.EqualTo("2. день\n08:15"));
            Assert.That(TextTemplate.Fill("$(other) left", ("day", "1")), Is.EqualTo("$(other) left"));
        }

        [Test]
        public void Options_ClampVolumesAndReportChanges()
        {
            var options = new PlayerOptions();
            int changes = 0;
            options.Changed += () => changes++;

            options.SetVolume(VolumeChannel.Music, 140);
            Assert.That(changes, Is.Zero, "already 100");
            options.SetVolume(VolumeChannel.Music, -3);
            Assert.That(options.Volume(VolumeChannel.Music), Is.Zero);
            options.SetVolume(VolumeChannel.Voice, PlayerOptions.FromStep(PlayerOptions.ToStep(83)));
            Assert.That(options.Volume(VolumeChannel.Voice), Is.EqualTo(85));
            Assert.That(options.Gain(VolumeChannel.Voice), Is.EqualTo(0.85f).Within(1e-6));
            Assert.That(PlayerOptions.Percent(85), Is.EqualTo("85%"));
            options.FirstPerson = true;
            options.FirstPerson = true;
            Assert.That(changes, Is.EqualTo(3));
        }
    }
}
