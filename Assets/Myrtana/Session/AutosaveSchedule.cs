namespace Myrtana.Session
{
    /// <summary>
    /// When the port's autosave writes (G3 / CP 1.75 have no autosave: no string, no ini option, only the F5/F9
    /// quicksave). Every <see cref="IntervalSeconds"/> of real time (0 = off) on a fixed cadence that starts when a game is
    /// started or loaded. An autosave that falls due while saving is refused (conversation and its cutscenes, fight, dead
    /// or knocked out, falling, transformed, using an object) or while the game menu is open or an area is still loading
    /// waits and is written at the first moment saving is allowed again; the next one is due one interval after that.
    /// fix-112 [owner]: the player's own saves (quick, menu) do NOT move the autosave - fix-82 restarted the interval on
    /// every save, so a player who saves every few minutes never got an autosave. One slot, overwritten each time.
    /// </summary>
    public sealed class AutosaveSchedule
    {
        public const float DefaultMinutes = 10f;

        double _due;

        public AutosaveSchedule(float intervalSeconds = DefaultMinutes * 60f) => IntervalSeconds = intervalSeconds;

        public float IntervalSeconds { get; set; }

        public bool Enabled => IntervalSeconds > 0;

        /// <summary>Real time (seconds) from which the next autosave is due.</summary>
        public double Due => _due;

        /// <summary>A game was started or loaded at <paramref name="now"/> (or the runner set a new interval): the next autosave is one interval later.</summary>
        public void Restart(double now) => _due = now + IntervalSeconds;

        /// <summary>
        /// A save was written at <paramref name="now"/>. Only the autosave itself moves the schedule; a quicksave or a
        /// menu save leaves it as it is.
        /// </summary>
        public void Saved(double now, bool autosave)
        {
            if (autosave) Restart(now);
        }

        /// <summary>
        /// True when the autosave should be written now. <paramref name="blockReason"/> is the session's save blocker (null
        /// = saving allowed); <paramref name="paused"/> covers the open game menu and loading.
        /// </summary>
        public bool ShouldSave(double now, string blockReason, bool paused) =>
            Enabled && !paused && blockReason == null && now >= _due;
    }
}
