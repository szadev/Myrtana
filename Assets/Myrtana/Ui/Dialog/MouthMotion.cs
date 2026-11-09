using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Dialog
{
    /// <summary>
    /// The mouth of a speaker for one frame: a crossfade from viseme <see cref="From"/> to <see cref="To"/>
    /// (<see cref="Weight"/> 0…1 towards <see cref="To"/>), scaled by <see cref="Amount"/> (0 = the face as animated by the
    /// body motion, 1 = the full viseme pose).
    /// </summary>
    public readonly struct MouthShape
    {
        public MouthShape(int from, int to, float weight, float amount)
        {
            From = from; To = to; Weight = weight; Amount = amount;
        }

        public int From { get; }
        public int To { get; }
        public float Weight { get; }
        public float Amount { get; }

        public static readonly MouthShape Rest = new MouthShape(LipSyncTrack.RestViseme, LipSyncTrack.RestViseme, 1, 0);
    }

    /// <summary>
    /// Drives a speaker's mouth over a voice line (Docs/dialog-presentation.md §4): from the line's G3 viseme track
    /// when there is one, otherwise from the loudness of the playing voice (one "open" viseme, faded by an envelope with
    /// quick attack and slower release). The mouth eases in at the start and out at the end of the line.
    /// </summary>
    public sealed class MouthMotion
    {
        /// <summary>Crossfade between two visemes, s (G3 visemes change every 40–200 ms).</summary>
        public const float VisemeBlend = 0.06f;
        /// <summary>Loudness below which the mouth is closed, and the loudness that opens it fully (RMS, 0…1).</summary>
        public const float SilentLevel = 0.01f, FullLevel = 0.12f;
        public const float Attack = 0.04f, Release = 0.12f, EaseOut = 0.15f;

        /// <summary>A rest in a fitted track longer than this while the voice is heard is filled from the loudness, s.</summary>
        public const float MaxRestInSpeech = 0.15f;

        LipSyncTrack _track;
        readonly int _openViseme;
        float _envelope, _amount;
        float _heldTime; // line time while the voice was last heard: the mouth eases out from there, not from time 0
        VoiceSegment[] _gate; // voiced stretches of the clip; set when the track was fitted (it belongs to another recording)

        /// <param name="track">The line's visemes, or null to follow the loudness.</param>
        /// <param name="openViseme">Viseme shown for loud sound when there is no track.</param>
        public MouthMotion(LipSyncTrack track, int openViseme)
        {
            _track = track;
            _openViseme = openViseme;
        }

        public bool HasTrack => _track != null && _track.Keys.Count > 0;

        /// <summary>The track in use (after <see cref="FitToVoice"/>), null without one.</summary>
        public LipSyncTrack Track => _track;

        /// <summary>Line time of the last update that showed an open mouth (a viseme other than rest), -1 before any (diagnostics).</summary>
        public float LastOpenTime { get; private set; } = -1;

        /// <summary>
        /// Lays the track over the voiced span of the clip once it is known (<see cref="LipSyncTrack.FitToVoice"/>).
        /// Returns true when the track was retimed.
        /// </summary>
        public bool FitToVoice(float voiceStart, float voiceEnd)
        {
            if (!HasTrack) return false;
            var fitted = _track.FitToVoice(voiceStart, voiceEnd);
            bool changed = !ReferenceEquals(fitted, _track);
            _track = fitted;
            return changed;
        }

        /// <summary>
        /// <see cref="FitToVoice(float, float)"/> over the clip's voiced stretches (<see cref="VoiceSpan.FindSegments"/>).
        /// A fitted track was made for another recording (Russian), so its pauses do not fall on the voice's: the mouth
        /// then also follows the voice — closed in the voice's pauses, and opened by the loudness where the voice is heard
        /// but the track rests longer than <see cref="MaxRestInSpeech"/>. Matching tracks (English) play as they are.
        /// </summary>
        public bool FitToVoice(IReadOnlyList<VoiceSegment> voiced)
        {
            if (!HasTrack || voiced == null || voiced.Count == 0) return false;
            bool changed = FitToVoice(voiced[0].Start, voiced[voiced.Count - 1].End);
            if (changed)
            {
                _gate = new VoiceSegment[voiced.Count];
                for (int i = 0; i < voiced.Count; i++) _gate[i] = voiced[i];
            }
            return changed;
        }

        /// <summary>The track is laid over the voice and gated by its pauses (diagnostics).</summary>
        public bool Gated => _gate != null;

        /// <summary>
        /// The mouth at <paramref name="time"/> s into the line. <paramref name="level"/>: current loudness (RMS) of the
        /// voice; <paramref name="speaking"/>: the voice still plays. <paramref name="dt"/>: frame time for the envelopes.
        /// </summary>
        public MouthShape Update(float time, float level, bool speaking, float dt)
        {
            dt = Math.Max(0, dt);
            // The voice player reports time 0 once the clip is over; easing out from there would show the track's first
            // viseme (an open mouth for tracks that start speaking at 0) and overwrite LastOpenTime with 0.
            if (speaking) _heldTime = time;
            else time = _heldTime;
            float targetAmount = speaking ? 1 : 0;
            _amount = Approach(_amount, targetAmount, dt / (speaking ? VisemeBlend : EaseOut));
            if (HasTrack)
            {
                _track.Sample(time, VisemeBlend, out int from, out int to, out float w);
                if (_gate != null) Gate(time, level, speaking, dt, ref from, ref to, ref w);
                if (_amount > 0 && (to != LipSyncTrack.RestViseme || (from != LipSyncTrack.RestViseme && w < 1)))
                    LastOpenTime = time;
                return new MouthShape(from, to, w, _amount);
            }
            FollowLoudness(level, speaking, dt);
            return new MouthShape(LipSyncTrack.RestViseme, _openViseme, _envelope, _amount);
        }

        void FollowLoudness(float level, bool speaking, float dt)
        {
            float open = speaking ? Clamp01((level - SilentLevel) / (FullLevel - SilentLevel)) : 0;
            float rate = open > _envelope ? Attack : Release;
            _envelope += (open - _envelope) * Clamp01(dt / rate);
        }

        /// <summary>A fitted track against the voice: closed in its pauses, loudness where the track rests too long.</summary>
        void Gate(float time, float level, bool speaking, float dt, ref int from, ref int to, ref float w)
        {
            int seg = SegmentAt(time, out float pauseStart);
            if (seg < 0)
            {
                // A pause of the voice (or before/after it): close from what the track showed when the pause began.
                _envelope = 0;
                from = _track.VisemeAt(pauseStart);
                to = LipSyncTrack.RestViseme;
                w = Clamp01((time - pauseStart) / VisemeBlend);
                return;
            }
            int i = _track.IndexAt(time);
            bool restingLong = to == LipSyncTrack.RestViseme && i >= 0 && time - RestStart(i) > MaxRestInSpeech;
            if (!restingLong)
            {
                _envelope = 0;
                return;
            }
            FollowLoudness(level, speaking, dt);
            from = LipSyncTrack.RestViseme;
            to = _openViseme;
            w = _envelope;
        }

        /// <summary>Index of the voiced stretch at <paramref name="time"/>, or -1 with the time the pause began.</summary>
        int SegmentAt(float time, out float pauseStart)
        {
            pauseStart = 0;
            for (int i = 0; i < _gate.Length; i++)
            {
                if (time < _gate[i].Start) return -1;
                if (time < _gate[i].End) return i;
                pauseStart = _gate[i].End;
            }
            return -1;
        }

        /// <summary>When the rest that key <paramref name="i"/> belongs to began (consecutive rest keys form one rest).</summary>
        float RestStart(int i)
        {
            var keys = _track.Keys;
            while (i > 0 && keys[i - 1].Viseme == LipSyncTrack.RestViseme) i--;
            return keys[i].Time;
        }

        static float Approach(float value, float target, float step) =>
            value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
