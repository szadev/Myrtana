namespace Myrtana.Core
{
    /// <summary>
    /// Save section "core.time": game clock, a reserved word (the random generator state before fix-119) and real play time.
    /// fix-119: G3's <c>rand()</c> is not saved, so the generator is no longer saved or restored: a loaded game rolls with the
    /// seed its session got (<see cref="SessionSeed"/>) and a reload does not replay the rolls made after the save. The format
    /// stays version 1: old saves load (their generator state is skipped), the word is written as a constant.
    /// </summary>
    public sealed class TimeSaveSection : ISaveParticipant
    {
        readonly GameClock _clock;
        /// <summary>What the reserved word holds: a valid generator state, so an older build reading the save still rolls.</summary>
        internal static readonly ulong ReservedRandomWord = SeededRandom.StateFor(0).Value;

        readonly PlayTime _playTime;

        public TimeSaveSection(GameClock clock, PlayTime playTime)
        {
            _clock = clock;
            _playTime = playTime;
        }

        public string Section => "core.time";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            w.WriteDouble(_clock.GameSeconds);
            w.WriteUInt64(ReservedRandomWord);
            w.WriteDouble(_playTime.Seconds);
        }

        public void Read(ISaveReader r, int version)
        {
            _clock.Restore(r.ReadDouble());
            r.ReadUInt64(); // the saved generator state: ignored (fix-119)
            _playTime.Seconds = r.ReadDouble();
        }
    }

    /// <summary>Real seconds spent in a game (shown in save slots).</summary>
    public sealed class PlayTime
    {
        public double Seconds { get; set; }

        public void Advance(float realSeconds)
        {
            if (realSeconds > 0) Seconds += realSeconds;
        }
    }
}
