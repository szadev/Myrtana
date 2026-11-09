using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Myrtana.Ui.Dialog
{
    /// <summary>
    /// The mouth shapes of one voice line (Docs/dialog-presentation.md §4): a list of keys "from <c>Time</c> seconds on,
    /// the mouth forms viseme <c>Viseme</c>" sorted by time. G3 tracks use visemes 1…13 and always end on
    /// <see cref="RestViseme"/> (closed mouth); 0 never occurs. Imported from the G3 <c>.xlip</c> of the
    /// recording and stored next to the clip in a small text form (<see cref="Write"/>/<see cref="Read"/>):
    /// a header line <c>lip 1</c>, then one <c>time viseme</c> pair per line (invariant culture).
    /// </summary>
    public sealed class LipSyncTrack
    {
        public const string Header = "lip 1";
        public const string FileExtension = ".lip";
        /// <summary>The closed mouth: before the first key, and the last key of every G3 track.</summary>
        public const int RestViseme = 1;

        public readonly struct Key
        {
            public readonly float Time;
            public readonly int Viseme;
            public Key(float time, int viseme) { Time = time; Viseme = viseme; }
        }

        readonly Key[] _keys;

        public LipSyncTrack(IEnumerable<Key> keys)
        {
            var list = new List<Key>(keys ?? Array.Empty<Key>());
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
            _keys = list.ToArray();
        }

        public IReadOnlyList<Key> Keys => _keys;

        /// <summary>Time of the last key (the mouth rests after it), 0 when empty.</summary>
        public float Duration => _keys.Length == 0 ? 0 : _keys[_keys.Length - 1].Time;

        /// <summary>Time the mouth first leaves <see cref="RestViseme"/> (the speech starts), 0 when it never does.</summary>
        public float SpeechStart
        {
            get
            {
                foreach (var k in _keys)
                    if (k.Viseme != RestViseme) return k.Time;
                return 0;
            }
        }

        /// <summary>
        /// The track stretched so its speech span [<see cref="SpeechStart"/>, <see cref="Duration"/>] covers
        /// [<paramref name="start"/>, <paramref name="end"/>] (times before the span move along; nothing goes below 0).
        /// </summary>
        public LipSyncTrack Retimed(float start, float end)
        {
            float s0 = SpeechStart, s1 = Duration;
            if (_keys.Length == 0 || s1 - s0 <= 1e-4f || end <= start) return this;
            float scale = (end - start) / (s1 - s0);
            var keys = new Key[_keys.Length];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = new Key(Math.Max(0f, start + (_keys[i].Time - s0) * scale), _keys[i].Viseme);
            return new LipSyncTrack(keys);
        }

        /// <summary>Default for <see cref="FitToVoice"/>: tracks whose speech span is this close to the voice's are kept.</summary>
        public const float FitTolerance = 0.1f;

        /// <summary>
        /// The track laid over the voice actually heard (Docs/dialog-presentation.md §4, "lip timing"): G3's Russian
        /// tracks were made for another recording (they correlate better with the English clip than with the Russian one)
        /// and end up to ±1 s off the Russian speech. When the track's speech start or end is more than
        /// <paramref name="tolerance"/> s away from the voiced span of the clip, the track is stretched onto that span;
        /// matching tracks (English) are returned unchanged.
        /// </summary>
        public LipSyncTrack FitToVoice(float voiceStart, float voiceEnd, float tolerance = FitTolerance)
        {
            if (_keys.Length == 0 || voiceEnd - voiceStart <= 1e-3f) return this;
            if (Math.Abs(SpeechStart - voiceStart) <= tolerance && Math.Abs(Duration - voiceEnd) <= tolerance) return this;
            return Retimed(voiceStart, voiceEnd);
        }

        /// <summary>Index of the key in effect at <paramref name="time"/>, -1 before the first key.</summary>
        public int IndexAt(float time)
        {
            int lo = 0, hi = _keys.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid].Time <= time) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        /// <summary>The viseme in effect at <paramref name="time"/> (<see cref="RestViseme"/> before the first key).</summary>
        public int VisemeAt(float time)
        {
            int i = IndexAt(time);
            return i < 0 ? RestViseme : _keys[i].Viseme;
        }

        /// <summary>
        /// The mouth at <paramref name="time"/> as a crossfade: the previous viseme fades into the current one over
        /// <paramref name="blend"/> seconds after each key, so shapes never snap.
        /// </summary>
        public void Sample(float time, float blend, out int from, out int to, out float weight)
        {
            int i = IndexAt(time);
            to = i < 0 ? RestViseme : _keys[i].Viseme;
            from = i <= 0 ? RestViseme : _keys[i - 1].Viseme;
            if (i < 0 || blend <= 0) { from = to; weight = 1; return; }
            weight = Math.Min(1f, Math.Max(0f, (time - _keys[i].Time) / blend));
        }

        public string Write()
        {
            var sb = new StringBuilder(Header).Append('\n');
            foreach (var k in _keys)
                sb.Append(k.Time.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ').Append(k.Viseme).Append('\n');
            return sb.ToString();
        }

        /// <summary>Parses <see cref="Write"/> output; throws <see cref="InvalidDataException"/> on anything else.</summary>
        public static LipSyncTrack Read(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || lines[0].Trim() != Header) throw new InvalidDataException("lip: missing header");
            var keys = new List<Key>(lines.Length - 1);
            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Trim().Split(' ');
                if (parts.Length != 2 ||
                    !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float t) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    throw new InvalidDataException($"lip: bad line {i + 1}");
                keys.Add(new Key(t, v));
            }
            return new LipSyncTrack(keys);
        }
    }
}
