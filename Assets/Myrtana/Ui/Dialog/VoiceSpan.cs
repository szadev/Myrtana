using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Dialog
{
    /// <summary>A stretch of a voice clip, s (<see cref="VoiceSpan.FindSegments"/>).</summary>
    public readonly struct VoiceSegment
    {
        public VoiceSegment(float start, float end) { Start = start; End = end; }
        public float Start { get; }
        public float End { get; }
        public override string ToString() => $"{Start:0.00}-{End:0.00}";
    }

    /// <summary>
    /// Where the speech in a voice clip starts and ends (Docs/dialog-presentation.md §4, "lip timing"): the clip is cut
    /// into <see cref="Window"/> s windows; the voiced span runs from the first to the end of the last window whose RMS
    /// reaches <see cref="RelativeThreshold"/> of the loudest window (and at least <see cref="FloorThreshold"/>).
    /// Measured on 146 English clips, its end matches the end of the G3 viseme track (median +6 ms, 91 % within 0.1 s).
    /// </summary>
    public static class VoiceSpan
    {
        public const float Window = 0.02f, RelativeThreshold = 0.05f, FloorThreshold = 0.003f;

        /// <param name="samples">Interleaved PCM, -1…1 (AudioClip.GetData).</param>
        /// <returns>False for an empty or silent clip.</returns>
        public static bool TryFind(float[] samples, int channels, int frequency, out float start, out float end)
        {
            start = end = 0;
            var segments = FindSegments(samples, channels, frequency, float.MaxValue);
            if (segments.Count == 0) return false;
            start = segments[0].Start;
            end = segments[segments.Count - 1].End;
            return true;
        }

        /// <summary>Pauses shorter than this do not split the speech (<see cref="FindSegments"/>), s.</summary>
        public const float MinPause = 0.15f;

        /// <summary>
        /// The voiced stretches of the clip in order (same windows and threshold as <see cref="TryFind"/>); silences
        /// shorter than <paramref name="minPause"/> s are bridged. Empty for an empty or silent clip.
        /// </summary>
        public static List<VoiceSegment> FindSegments(float[] samples, int channels, int frequency, float minPause = MinPause)
        {
            var result = new List<VoiceSegment>();
            if (samples == null || channels <= 0 || frequency <= 0) return result;
            int frames = samples.Length / channels;
            int windowFrames = Math.Max(1, (int)(frequency * Window));
            int windows = frames / windowFrames;
            if (windows == 0) return result;
            var rms = new float[windows];
            float max = 0;
            for (int w = 0; w < windows; w++)
            {
                double sum = 0;
                int from = w * windowFrames * channels, to = from + windowFrames * channels;
                for (int i = from; i < to; i++) sum += samples[i] * samples[i];
                rms[w] = (float)Math.Sqrt(sum / (windowFrames * channels));
                if (rms[w] > max) max = rms[w];
            }
            float threshold = Math.Max(max * RelativeThreshold, FloorThreshold);
            float seconds = (float)windowFrames / frequency;
            int runStart = -1;
            for (int w = 0; w <= windows; w++)
            {
                bool voiced = w < windows && rms[w] >= threshold;
                if (voiced && runStart < 0) runStart = w;
                else if (!voiced && runStart >= 0)
                {
                    float s = runStart * seconds, e = w * seconds;
                    if (result.Count > 0 && s - result[result.Count - 1].End < minPause)
                        result[result.Count - 1] = new VoiceSegment(result[result.Count - 1].Start, e);
                    else result.Add(new VoiceSegment(s, e));
                    runStart = -1;
                }
            }
            return result;
        }
    }
}
