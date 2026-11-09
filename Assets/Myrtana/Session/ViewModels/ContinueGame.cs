using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>
    /// fix-138 [owner]: the port's "Continue" button on the main menu (G3 has none; <see cref="PortOptions.ContinueButton"/>)
    /// loads the newest save of any kind — a numbered save, the quicksave or the autosave — by the real save time in its
    /// header. The candidates come newest first so the host can fall back to the next one when a save turns out to be
    /// corrupt or no longer loads. Ties (same second) prefer the player's own saves: numbered, then quick, then auto.
    /// </summary>
    public static class ContinueGame
    {
        /// <summary>
        /// The readable saves to try, newest first (<paramref name="saves"/>: slot and header, as the save list reads them;
        /// unreadable files never get there). Empty when there is nothing to continue.
        /// </summary>
        public static IReadOnlyList<string> Candidates(IEnumerable<KeyValuePair<string, SaveHeader>> saves)
        {
            if (saves == null) return Array.Empty<string>();
            // fix-155: the hidden crash-recovery save only through the title's question, never silently
            return saves.Where(s => !string.IsNullOrEmpty(s.Key) && s.Value != null && s.Key != CrashRecovery.Slot)
                        .OrderByDescending(s => s.Value.SavedAtUtc)
                        .ThenBy(s => KindRank(SaveGameList.KindOf(s.Key)))
                        .ThenByDescending(s => s.Key, StringComparer.Ordinal)
                        .Select(s => s.Key)
                        .ToList();
        }

        /// <summary>The save Continue loads first, or null when there is none.</summary>
        public static string Newest(IEnumerable<KeyValuePair<string, SaveHeader>> saves)
        {
            var candidates = Candidates(saves);
            return candidates.Count > 0 ? candidates[0] : null;
        }

        static int KindRank(SaveSlotKind kind)
        {
            switch (kind)
            {
                case SaveSlotKind.Numbered: return 0;
                case SaveSlotKind.Quick: return 1;
                case SaveSlotKind.Auto: return 2;
                default: return 3;
            }
        }

        /// <summary>The button's text (G3's string tables have none) in the text language; unknown languages: English.</summary>
        public static string ButtonText(string language)
        {
            switch (language)
            {
                case "Russian": return "Продолжить";
                case "German": return "Fortsetzen";
                case "French": return "Continuer";
                case "Italian": return "Continua";
                case "Spanish": return "Continuar";
                case "Polish": return "Kontynuuj";
                case "Czech": return "Pokračovat";
                case "Hungarian": return "Folytatás";
                default: return "Continue";
            }
        }
    }
}
