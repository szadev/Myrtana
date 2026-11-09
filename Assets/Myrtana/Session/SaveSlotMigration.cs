using System;
using System.Collections.Generic;
using System.IO;

namespace Myrtana.Session
{
    /// <summary>
    /// fix-107: one quicksave and one autosave file again ([owner]: the autosave is the backup of the quicksave), as G3's
    /// single "QuickSave" file. fix-82 had kept two files of each kind, written in turn (<c>quick</c> / <c>quick2</c>,
    /// <c>auto</c> / <c>auto2</c>). When the second file is the newer one (or the only one), the two files trade names
    /// (with their pictures), so the one slot holds the newer save; the older stays on disk as <c>quick2</c> / <c>auto2</c>,
    /// unchanged and not listed. Nothing is deleted. Runs once when the save controller starts.
    /// </summary>
    public static class SaveSlotMigration
    {
        /// <summary>The slots fix-82 had paired with a second file.</summary>
        public static readonly IReadOnlyList<string> Slots = new[] { SaveGameList.QuickSlot, SaveGameList.AutoSlot };

        /// <summary>fix-82's second file of <paramref name="slot"/> ("quick" → "quick2").</summary>
        public static string Legacy(string slot) => slot + "2";

        /// <summary>The second file takes the slot's name: it is readable and newer than the slot (or the slot is unreadable or missing).</summary>
        public static bool LegacyWins(DateTime? slot, DateTime? legacy) => legacy != null && (slot == null || legacy.Value > slot.Value);

        /// <summary>
        /// Swaps each slot with its second file where <see cref="LegacyWins"/> (files <c>&lt;slot&gt;&lt;extension&gt;</c> in
        /// <paramref name="directory"/>). <paramref name="savedAt"/> gives a slot's save time, null when missing or
        /// unreadable. Returns the slots that changed.
        /// </summary>
        public static List<string> Run(string directory, Func<string, DateTime?> savedAt, params string[] extensions)
        {
            var changed = new List<string>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return changed;
            foreach (var slot in Slots)
            {
                string legacy = Legacy(slot);
                if (!LegacyWins(savedAt(slot), savedAt(legacy))) continue;
                foreach (var ext in extensions)
                    Swap(Path.Combine(directory, slot + ext), Path.Combine(directory, legacy + ext));
                changed.Add(slot);
            }
            return changed;
        }

        /// <summary>Trades the names of two files; a missing one just lets the other take its name.</summary>
        static void Swap(string a, string b)
        {
            bool hasA = File.Exists(a), hasB = File.Exists(b);
            if (hasA && hasB)
            {
                string temp = a + ".swap";
                for (int i = 2; File.Exists(temp); i++) temp = a + ".swap" + i; // never over an older leftover
                File.Move(a, temp);
                File.Move(b, a);
                File.Move(temp, b);
            }
            else if (hasB) File.Move(b, a);
            else if (hasA) File.Move(a, b);
        }
    }
}
