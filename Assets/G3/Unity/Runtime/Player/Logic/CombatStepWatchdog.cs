namespace G3.Player
{
    /// <summary>
    /// Safety end of a combat-animator step that waits for its motion to finish (audit S6). G3 has no equivalent: its
    /// sequencer steps are one-shot motions; a looping motion in our library (the loop flag of the imported data) never
    /// reports the end, so the body would stay Busy for good. Such a step counts as finished after one full cycle (its phase
    /// wrapped), and any waiting step after <see cref="MaxStepSeconds"/> [interp]. Both are logged by the animator.
    /// </summary>
    public static class CombatStepWatchdog
    {
        /// <summary>No single attack, reaction, draw or get-up motion lasts this long (real seconds) [interp].</summary>
        public const float MaxStepSeconds = 10f;

        public enum Verdict { Running, LoopWrapped, TimedOut }

        /// <param name="hold">The step holds on purpose (parade, aim, sitting loop): never cut.</param>
        /// <param name="loops">The step's motion loops.</param>
        /// <param name="lastPhase">The motion's phase (0–1) on the previous frame.</param>
        /// <param name="phase">The motion's phase now.</param>
        /// <param name="seconds">Real seconds since the step started.</param>
        public static Verdict Check(bool hold, bool loops, float lastPhase, float phase, float seconds)
        {
            if (hold) return Verdict.Running;
            if (loops && phase + 1e-4f < lastPhase) return Verdict.LoopWrapped;
            return seconds > MaxStepSeconds ? Verdict.TimedOut : Verdict.Running;
        }
    }
}
