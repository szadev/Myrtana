namespace G3.Audio
{
    /// <summary>
    /// One scheduled playback of a music sample on the DSP clock: starts at <see cref="Start"/> with the sample
    /// position <see cref="Offset"/>, stops at <see cref="End"/>, volume = <see cref="Volume"/> × <see cref="Gain"/>(t).
    /// The Unity voice pool mirrors the sequencer's cue list onto AudioSources.
    /// </summary>
    public sealed class MusicCue
    {
        public readonly int Id;
        public readonly string Clip;
        public readonly float Volume;
        public readonly double Start, Offset, Length;
        public readonly MusicFragmentData Fragment;      // null for a transition
        public readonly MusicTransitionData Transition;  // null for a fragment
        public readonly GainEnvelope Gain = new GainEnvelope();
        /// <summary>Scheduled stop (DSP time). Never later than the end of the sample.</summary>
        public double End;
        /// <summary>Set once a fade-out to silence has been scheduled.</summary>
        public bool FadingOut;

        public MusicCue(int id, MusicFragmentData f, double start, double offset, double length)
            : this(id, f.clip, f.volume, start, offset, length) { Fragment = f; }

        public MusicCue(int id, MusicTransitionData t, double start, double length)
            : this(id, t.clip, t.volume, start, 0, length) { Transition = t; }

        MusicCue(int id, string clip, float volume, double start, double offset, double length)
        {
            Id = id; Clip = clip; Volume = volume; Start = start; Offset = offset; Length = length;
            End = SampleEnd;
        }

        /// <summary>DSP time at which sample position 0 would play.</summary>
        public double Origin => Start - Offset;
        /// <summary>DSP time at which the sample runs out.</summary>
        public double SampleEnd => Origin + Length;

        public bool IsTransition => Transition != null;

        public override string ToString() => $"#{Id} {Clip} @{Start:F2}+{Offset:F2}..{End:F2}";
    }
}
