using System;

namespace Myrtana.Simulation
{
    public enum WarningStep { None, FirstWarning, SecondWarning, Attack }

    /// <summary>
    /// One NPC's warn – warn – attack sequence against someone who keeps doing what it dislikes: a drawn weapon, an
    /// intruder in its house or camp (ZS_ReactToWeapon / ZS_ReactToZone; perception.json warnings). The first warning
    /// comes at once, the second after 4 s, the attack after 8 s. Stopping the offence ends the sequence. Real seconds.
    /// </summary>
    public sealed class WarningSequence
    {
        readonly WarningTimings _timings;
        double _start;

        public WarningSequence(WarningTimings timings) => _timings = timings ?? throw new ArgumentNullException(nameof(timings));

        public WarningStep Step { get; private set; }
        public bool IsRunning => Step != WarningStep.None;

        /// <summary>
        /// Advances the sequence while the offence goes on (<paramref name="offending"/>) and returns the step that is due
        /// now, or None when nothing new happens. The offence stopping resets it.
        /// </summary>
        public WarningStep Update(bool offending, double now)
        {
            if (!offending)
            {
                Step = WarningStep.None;
                return WarningStep.None;
            }
            if (Step == WarningStep.None)
            {
                _start = now;
                return Step = WarningStep.FirstWarning;
            }
            double elapsed = now - _start;
            var due = elapsed >= _timings.Attack ? WarningStep.Attack
                    : elapsed >= _timings.SecondWarning ? WarningStep.SecondWarning : WarningStep.FirstWarning;
            if (due <= Step) return WarningStep.None;
            return Step = due;
        }

        public void Reset() => Step = WarningStep.None;
    }
}
