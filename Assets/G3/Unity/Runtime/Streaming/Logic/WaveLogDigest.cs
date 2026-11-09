using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace G3.Unity
{
    /// <summary>
    /// fix-186: which finished <see cref="G3RendererChanges"/> waves get their own log line. A big wave (a remaster switch, a
    /// liberation sector switch) is logged at once; the small ones (the terrain relief swaps one or two finished cells per
    /// frame, ~400 waves after every start with the relief on: regress-1021b counted 2830 lines in 30 min) are only counted
    /// per label and logged as one digest line at most every <see cref="DigestSeconds"/>.
    /// </summary>
    public sealed class WaveLogDigest
    {
        /// <summary>Waves touching at least this many renderers are logged on their own.</summary>
        public const int BigWave = 64;
        /// <summary>The digest of the small waves comes at most this often (s).</summary>
        public const float DigestSeconds = 30f;

        readonly Dictionary<string, (int waves, int items, int renderers)> _small = new Dictionary<string, (int, int, int)>();
        float _since = -1;

        /// <summary>Small waves counted since the last digest.</summary>
        public int PendingWaves { get; private set; }

        /// <summary>
        /// A wave finished at <paramref name="now"/> (s). Returns the line to log now (the wave itself when big, the digest of
        /// the small ones when it is due), or null.
        /// </summary>
        public string Finished(string labels, int items, int renderers, string waveLine, float now)
        {
            if (renderers >= BigWave) return waveLine;
            string key = string.IsNullOrEmpty(labels) ? "-" : labels;
            _small.TryGetValue(key, out var had);
            _small[key] = (had.waves + 1, had.items + items, had.renderers + renderers);
            PendingWaves++;
            if (_since < 0) _since = now;
            return now - _since >= DigestSeconds ? Digest(now) : null;
        }

        /// <summary>The digest of the small waves so far (null when there are none), and a new count from <paramref name="now"/>.</summary>
        public string Digest(float now)
        {
            if (PendingWaves == 0) return null;
            var sb = new StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture, "small waves over {0:0} s: ", now - _since));
            bool first = true;
            foreach (var kv in _small)
            {
                if (!first) sb.Append("; ");
                first = false;
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0} {1} waves, {2} items, {3} renderers", kv.Key, kv.Value.waves, kv.Value.items, kv.Value.renderers));
            }
            _small.Clear();
            PendingWaves = 0;
            _since = now;
            return sb.ToString();
        }

        public void Clear()
        {
            _small.Clear();
            PendingWaves = 0;
            _since = -1;
        }
    }
}
