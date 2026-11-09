using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>
    /// fix-155 [owner]: progress lost to a crash (game crash, PC reboot) since the last save is offered back. G3 has nothing
    /// of the kind; the port option <see cref="PortOptions.CrashRecovery"/> (default on) turns it off.
    /// <list type="bullet">
    /// <item>A hidden save, slot <see cref="Slot"/>, is written every <see cref="RecoverySchedule.DefaultIntervalSeconds"/>
    /// of play and soon after key moments (quest status change, a quest item taken, a teleport), only when a normal save
    /// would be allowed (the session's save blockers, no menu, no loading) — <see cref="RecoverySchedule"/>. It is never
    /// listed (<see cref="SaveGameList"/> and <see cref="ContinueGame"/> skip it) and is overwritten each time.</item>
    /// <item>A marker file (<see cref="MarkerFile"/>) exists while the game runs and is deleted on a normal quit, together
    /// with the hidden save (a player who quits without saving chose so). Found at start-up, the last run ended
    /// unclean.</item>
    /// <item>Then, when the hidden save is newer than the newest normal save, the title asks
    /// (<see cref="PromptText"/>): Yes copies it to the visible slot <see cref="RecoveredSlot"/> ("Восстановление" in the
    /// load list, Continue considers it from then on) and loads it; No discards it. Continue never loads the hidden save
    /// silently.</item>
    /// </list>
    /// </summary>
    public static class CrashRecovery
    {
        /// <summary>The hidden rolling save (not a menu slot).</summary>
        public const string Slot = "recovery";
        /// <summary>The visible slot an accepted recovery becomes (<see cref="SaveSlotKind.Recovered"/>).</summary>
        public const string RecoveredSlot = "recovered";
        /// <summary>The marker of a running game, next to the saves.</summary>
        public const string MarkerFile = "session.running";

        /// <summary>
        /// The title offers the hidden save: the option is on, the last run ended unclean (<paramref name="uncleanExit"/>),
        /// the hidden save is readable (<paramref name="recoveryAtUtc"/>) and newer than the newest normal save (null: none).
        /// </summary>
        public static bool ShouldOffer(bool enabled, bool uncleanExit, DateTime? recoveryAtUtc, DateTime? newestNormalAtUtc) =>
            enabled && uncleanExit && recoveryAtUtc.HasValue &&
            (!newestNormalAtUtc.HasValue || recoveryAtUtc.Value > newestNormalAtUtc.Value);

        /// <summary>The newest save time of the normal saves (every readable slot but the hidden one), or null.</summary>
        public static DateTime? NewestNormal(IEnumerable<KeyValuePair<string, SaveHeader>> saves)
        {
            DateTime? newest = null;
            if (saves == null) return null;
            foreach (var s in saves)
            {
                if (s.Key == Slot || s.Value == null) continue;
                if (!newest.HasValue || s.Value.SavedAtUtc > newest.Value) newest = s.Value.SavedAtUtc;
            }
            return newest;
        }

        /// <summary>The title's question with the hidden save's local time ("dd.MM.yyyy HH:mm", as the load list).</summary>
        public static string PromptText(string language, DateTime savedAtLocal)
        {
            string when = savedAtLocal.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            return IsRussian(language)
                ? $"Игра завершилась некорректно. Восстановить прогресс от {when}?"
                : $"The game did not exit properly. Restore the progress from {when}?";
        }

        /// <summary>The load list's name of <see cref="RecoveredSlot"/> (G3 has no string for it).</summary>
        public static string RecoveredName(string language) => IsRussian(language) ? "Восстановление" : "Recovered game";

        static bool IsRussian(string language) => string.Equals(language, "Russian", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// fix-155: the marker of a running game (<see cref="CrashRecovery.MarkerFile"/>). <see cref="Begin"/> at start-up
    /// tells whether the previous run left its marker (it crashed or was killed) and writes this run's; <see cref="End"/>
    /// on a normal quit removes it.
    /// </summary>
    public sealed class SessionMarker
    {
        public SessionMarker(string path) => Path = path;

        public string Path { get; }

        /// <summary>The previous run's marker was found by <see cref="Begin"/>.</summary>
        public bool UncleanExit { get; private set; }

        /// <summary>
        /// Checks for the previous run's marker and writes this run's (start time and process id). Returns
        /// <see cref="UncleanExit"/>. A marker of the same process (<paramref name="processId"/>, not 0) is no crash: the
        /// editor's play sessions share one process, and one that ended without its quit event left it.
        /// </summary>
        public bool Begin(DateTime nowUtc, int processId = 0)
        {
            UncleanExit = File.Exists(Path) && (processId == 0 || ProcessOf(Read()) != processId);
            string directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(Path, Content(nowUtc, processId));
            return UncleanExit;
        }

        /// <summary>The marker's text: "&lt;start time ISO 8601&gt; pid &lt;id&gt;".</summary>
        public static string Content(DateTime startUtc, int processId) =>
            startUtc.ToString("o", CultureInfo.InvariantCulture) + " pid " + processId.ToString(CultureInfo.InvariantCulture);

        /// <summary>The process id in a marker's text, 0 when there is none.</summary>
        public static int ProcessOf(string content)
        {
            if (string.IsNullOrEmpty(content)) return 0;
            int at = content.LastIndexOf(" pid ", StringComparison.Ordinal);
            return at >= 0 && int.TryParse(content.Substring(at + 5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) ? pid : 0;
        }

        string Read()
        {
            try
            {
                return File.ReadAllText(Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>A normal quit: no marker remains.</summary>
        public void End()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }

    /// <summary>
    /// fix-155: when the hidden recovery save is written. Every <see cref="IntervalSeconds"/> of real play time on a cadence
    /// that starts with each game (new or loaded), and <see cref="SettleSeconds"/> after a key moment (quest status, quest
    /// item, teleport), but never less than <see cref="MinSpacingSeconds"/> after the previous snapshot (a burst of quest
    /// changes gives one). A snapshot due while saving is refused (fight, conversation, cutscene, falling …), the menu is
    /// open or an area loads waits for the first moment saving is allowed; the next is due one interval after it.
    /// </summary>
    public sealed class RecoverySchedule
    {
        public const float DefaultIntervalSeconds = 120f;
        public const float DefaultMinSpacingSeconds = 20f;
        public const float DefaultSettleSeconds = 1f;

        double _due, _last = double.NegativeInfinity;

        public float IntervalSeconds { get; set; } = DefaultIntervalSeconds;
        public float MinSpacingSeconds { get; set; } = DefaultMinSpacingSeconds;
        public float SettleSeconds { get; set; } = DefaultSettleSeconds;

        /// <summary>Real time from which the next snapshot is due.</summary>
        public double Due => _due;

        /// <summary>A game was started or loaded at <paramref name="now"/>: the first snapshot one interval later.</summary>
        public void Restart(double now)
        {
            _last = now;
            _due = now + IntervalSeconds;
        }

        /// <summary>A key moment at <paramref name="now"/>: a snapshot soon (settled, spaced from the last one), unless one is due sooner anyway.</summary>
        public void KeyMoment(double now)
        {
            double soon = Math.Max(now + SettleSeconds, _last + MinSpacingSeconds);
            if (soon < _due) _due = soon;
        }

        /// <summary>A snapshot should be written now (<paramref name="blockReason"/>: the session's save blocker, null = allowed).</summary>
        public bool ShouldSave(double now, string blockReason, bool paused) => !paused && blockReason == null && now >= _due;

        /// <summary>A snapshot was written at <paramref name="now"/>.</summary>
        public void Saved(double now)
        {
            _last = now;
            _due = now + IntervalSeconds;
        }
    }
}
