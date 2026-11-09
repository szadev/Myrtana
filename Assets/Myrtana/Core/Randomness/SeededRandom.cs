using System;

namespace Myrtana.Core
{
    /// <summary>xorshift64* generator: small state, fast, identical results on every platform.</summary>
    public sealed class SeededRandom : IRandom
    {
        ulong _state;

        public SeededRandom(ulong seed) => _state = StateFor(seed).Value;

        /// <summary>The state a generator made with <paramref name="seed"/> starts in (0 is not a valid xorshift state).</summary>
        public static RandomState StateFor(ulong seed) => new RandomState(seed == 0 ? 0x9E3779B97F4A7C15UL : seed);

        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            return (int)(NextUInt64() % (ulong)maxExclusive);
        }

        public float NextFloat() => (NextUInt64() >> 40) / (float)(1UL << 24);

        public RandomState Save() => new RandomState(_state);

        public void Load(RandomState state) => _state = state.Value;

        ulong NextUInt64()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return _state * 0x2545F4914F6CDD1DUL;
        }
    }

    /// <summary>
    /// fix-119: seeds of the game sessions. G3 rolls with <c>rand()</c>, which no save holds, so a new game and every load get
    /// a seed of their own (save-scumming a pick, a pickpocket or a fight rolls anew); tests and scenario runs pin one.
    /// </summary>
    public static class SessionSeed
    {
        static long _sessions;

        /// <summary>A seed from the clock and a per-process counter: two sessions made in the same tick still differ.</summary>
        public static ulong Fresh()
        {
            ulong seed = (ulong)System.Diagnostics.Stopwatch.GetTimestamp() * 0x9E3779B97F4A7C15UL
                         ^ (ulong)System.Threading.Interlocked.Increment(ref _sessions) * 0xD1B54A32D192ED03UL
                         ^ (ulong)Environment.TickCount * 0x94D049BB133111EBUL;
            return seed == 0 ? 1 : seed;
        }

        /// <summary>The pinned seed, else a fresh one.</summary>
        public static ulong Resolve(ulong? pinned) => pinned ?? Fresh();

        /// <summary>"7", "0x1F" → a pinned seed; empty, "live", "random" or junk → null (a fresh seed per session).</summary>
        public static ulong? Parse(string text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex))
                return hex;
            return ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (ulong?)null;
        }
    }
}
