using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Session
{
    public enum SaveSlotKind { New, Numbered, Quick, Auto, Recovered }

    /// <summary>
    /// The rows of the save and load lists (<c>IDD_MENU_FILES_2</c>). G3 (CP 1.75, gCHUDFileManager) sorts the save
    /// files by name descending, so its "QuickSave" file always comes before the numbered saves; the save list leaves the
    /// quicksave out. Here: saving offers "New Savegame" first and then the numbered slots to overwrite; loading lists
    /// the one quicksave first, then the port's one autosave (G3 has none; fix-107: one file each again,
    /// <see cref="SaveSlotMigration"/>), then an accepted crash recovery (fix-155, <see cref="CrashRecovery.RecoveredSlot"/>), then the numbered saves, newest first (fix-107 [owner]; G3: highest number first).
    /// Texts are G3's strings: "Savegame No. 3", "Quick save game", the game time as "3. day, 08:15" (G3's
    /// <c>HUD_File_SaveGame_Format</c> on one line, from the language-neutral header title), the place the hero saved at
    /// (a world map label, <see cref="SaveTitle"/>) and the real save date; the autosave's name is the port's own
    /// (<see cref="AutosaveName"/>).
    /// </summary>
    public static class SaveGameList
    {
        public const string QuickSlot = "quick";
        public const string AutoSlot = "auto";
        const string NumberedPrefix = "save";
        /// <summary>Between the game time and the place on a row's second line.</summary>
        public const string PlaceSeparator = " · ";

        static readonly TextId EmptyText = new TextId("HUD_File_SaveGame_Empty");
        static readonly TextId TimeFormat = new TextId("HUD_File_SaveGame_Format");
        static readonly TextId NumberText = new TextId("HUD_File_SaveGame_Format2");
        static readonly TextId QuickText = new TextId("HUD_QuickSave_Game");

        public sealed class Row
        {
            public string Slot;
            public SaveSlotKind Kind;
            /// <summary>Slot number of a numbered save (from 1), 0 otherwise.</summary>
            public int Number;
            /// <summary>"Savegame No. 3", "Quick save game", "New Savegame", "Autosave".</summary>
            public string Name;
            /// <summary>Game day and time in G3's format on one line ("3. day, 08:15"), or the raw header title.</summary>
            public string GameTime;
            /// <summary>Where the hero saved ("Faring"); empty when unknown (older saves).</summary>
            public string Place;
            /// <summary>Real local save date "dd.MM.yyyy HH:mm"; empty for a new slot.</summary>
            public string Date;
            public DateTime SavedAtUtc;

            /// <summary>The row's second line: game time and place ("3. day, 08:15 · Faring").</summary>
            public string Detail => string.IsNullOrEmpty(Place) ? GameTime ?? "" :
                                    string.IsNullOrEmpty(GameTime) ? Place : GameTime + PlaceSeparator + Place;

            /// <summary>Del deletes only numbered saves (fix-107 [owner]); the Delete button any listed save.</summary>
            public bool DeletableByKey => Kind == SaveSlotKind.Numbered;
        }

        /// <summary>File name of numbered slot <paramref name="number"/> ("save003").</summary>
        public static string NumberedSlot(int number) => NumberedPrefix + number.ToString("000", CultureInfo.InvariantCulture);

        public static bool TryNumber(string slot, out int number)
        {
            number = 0;
            return slot != null && slot.StartsWith(NumberedPrefix, StringComparison.Ordinal) &&
                   int.TryParse(slot.Substring(NumberedPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
                   number > 0;
        }

        public static SaveSlotKind KindOf(string slot)
        {
            if (slot == QuickSlot) return SaveSlotKind.Quick;
            if (slot == AutoSlot) return SaveSlotKind.Auto;
            if (slot == CrashRecovery.RecoveredSlot) return SaveSlotKind.Recovered;
            return SaveSlotKind.Numbered;
        }

        /// <summary>The slot a new save goes to: one above the highest numbered slot.</summary>
        public static string NextFreeSlot(IEnumerable<string> slots)
        {
            int max = 0;
            foreach (var s in slots)
                if (TryNumber(s, out int n)) max = Math.Max(max, n);
            return NumberedSlot(max + 1);
        }

        /// <summary>
        /// Rows for the save list (<paramref name="saving"/>) or the load list. <paramref name="slots"/> are the readable
        /// slots with their headers; other file names than quick/auto/saveNNN are ignored (runner checkpoints, fix-82's
        /// older <c>quick2</c> / <c>auto2</c>).
        /// </summary>
        /// <param name="language">Text language ("Russian", "English" …) for the autosave's name, which G3 has no string for.</param>
        public static List<Row> For(bool saving, IEnumerable<KeyValuePair<string, SaveHeader>> slots, ILocalizer text, string language = null)
        {
            var all = slots.ToList();
            var rows = new List<Row>();
            if (saving)
            {
                string slot = NextFreeSlot(all.Select(s => s.Key));
                TryNumber(slot, out int number);
                rows.Add(new Row { Slot = slot, Kind = SaveSlotKind.New, Number = number, Name = text.Text(EmptyText), GameTime = "", Place = "", Date = "" });
            }
            // the quicksave, the autosave, then numbered saves newest first (same time: higher number first)
            foreach (var s in all.Select(s => (slot: s, group: Group(s.Key))).Where(s => s.group.HasValue)
                                 .OrderBy(s => s.group.Value).ThenByDescending(s => s.slot.Value.SavedAtUtc)
                                 .ThenByDescending(s => TryNumber(s.slot.Key, out int n) ? n : 0).Select(s => s.slot))
            {
                var kind = KindOf(s.Key);
                int number = 0;
                if (kind == SaveSlotKind.Numbered) TryNumber(s.Key, out number);
                if (saving && kind != SaveSlotKind.Numbered) continue;
                rows.Add(new Row
                {
                    Slot = s.Key,
                    Kind = kind,
                    Number = number,
                    Name = Name(kind, number, text, language),
                    GameTime = GameTime(s.Value.Title, text),
                    Place = Place(s.Value.Title, text),
                    Date = s.Value.SavedAtUtc == default ? "" : s.Value.SavedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                    SavedAtUtc = s.Value.SavedAtUtc,
                });
            }
            return rows;
        }

        /// <summary>
        /// List group: 0 the quicksave, 1 the autosave, 2 an accepted crash recovery (fix-155), 3 numbered slots; null = not
        /// a menu save (the hidden <see cref="CrashRecovery.Slot"/>, runner checkpoints).
        /// </summary>
        static int? Group(string slot)
        {
            switch (KindOf(slot))
            {
                case SaveSlotKind.Quick: return 0;
                case SaveSlotKind.Auto: return 1;
                case SaveSlotKind.Recovered: return 2;
                default: return TryNumber(slot, out _) ? 3 : (int?)null;
            }
        }

        /// <summary>The autosave's name. G3 has no autosave and no string for it, so the port names it itself.</summary>
        public static string AutosaveName(string language) => language == "Russian" ? "Автосохранение" : "Autosave";

        static string Name(SaveSlotKind kind, int number, ILocalizer text, string language)
        {
            switch (kind)
            {
                case SaveSlotKind.Numbered: return text.Text(NumberText).TrimEnd() + " " + number.ToString(CultureInfo.InvariantCulture);
                case SaveSlotKind.Quick: return text.Text(QuickText);
                case SaveSlotKind.Auto: return AutosaveName(language);
                case SaveSlotKind.Recovered: return CrashRecovery.RecoveredName(language);
                default: return "";
            }
        }

        /// <summary>
        /// The header title "Day 3, 08:15" in G3's format on one line ("3. day, 08:15": the format's line break becomes a
        /// comma); an unknown title as it is.
        /// </summary>
        public static string GameTime(string title, ILocalizer text)
        {
            if (!SaveTitle.TryParse(title, out int day, out int hour, out int minute)) return title ?? "";
            string time = TextTemplate.Fill(text.Text(TimeFormat),
                                            ("day", day.ToString(CultureInfo.InvariantCulture)),
                                            ("hour", hour.ToString("00", CultureInfo.InvariantCulture)),
                                            ("minute", minute.ToString("00", CultureInfo.InvariantCulture)));
            return OneLine(time);
        }

        /// <summary>The place in the title in the current language; empty when the title has none or its text is missing.</summary>
        public static string Place(string title, ILocalizer text)
        {
            if (!SaveTitle.TryParse(title, out _, out _, out _, out var place) || place.IsEmpty) return "";
            return text.TryText(place, out var name) && !string.IsNullOrWhiteSpace(name) ? OneLine(name) : "";
        }

        static string OneLine(string s)
        {
            var lines = s.Replace("\r", "").Replace("\\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            return string.Join(", ", lines);
        }
    }
}
