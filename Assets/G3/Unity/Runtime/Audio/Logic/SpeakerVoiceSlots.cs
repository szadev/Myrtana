using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>
    /// fix-199: which of a few 3D speech voices says a speaker's line. Each voice stays with its speaker for the line and
    /// follows him (NpcReactionView), so a voice is never moved to another speaker while it still loads or sounds — before,
    /// one shared voice was moved to every new speaker and a second line during the clip's load took the loading voice,
    /// so a line could sound where another NPC stood.
    /// </summary>
    public static class SpeakerVoiceSlots
    {
        /// <summary>
        /// The voice for <paramref name="speaker"/>'s new line: the one already speaking for him (his new line replaces his
        /// own), else a free one, else — when <paramref name="mayInterrupt"/> — the one that started first; −1 = none (the
        /// line is not voiced).
        /// </summary>
        /// <param name="speakers">Speaker of each voice's current or last line (null = none yet).</param>
        /// <param name="busy">Whether each voice is loading or sounding a clip.</param>
        /// <param name="started">When each voice's line was asked for (any clock).</param>
        public static int Choose(IReadOnlyList<string> speakers, IReadOnlyList<bool> busy, IReadOnlyList<float> started,
                                 string speaker, bool mayInterrupt)
        {
            int n = speakers.Count;
            for (int i = 0; i < n; i++)
                if (busy[i] && speakers[i] == speaker) return i;
            for (int i = 0; i < n; i++)
                if (!busy[i]) return i;
            if (!mayInterrupt || n == 0) return -1;
            int oldest = 0;
            for (int i = 1; i < n; i++)
                if (started[i] < started[oldest]) oldest = i;
            return oldest;
        }
    }
}
