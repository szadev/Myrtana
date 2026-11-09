using System;
using System.Collections.Generic;

namespace G3.Weather
{
    /// <summary>One weather zone at the player: its environment, its blend factor (0..1) and its horizontal size (m).</summary>
    public readonly struct ZoneWeight
    {
        public readonly string Environment;
        public readonly float Factor, Size;
        public ZoneWeight(string environment, float factor, float size) { Environment = environment; Factor = factor; Size = size; }
    }

    /// <summary>
    /// fix-215 (owner: the weather changes at a line in Nordmar between the Fire clan / monastery and the Hammer clan):
    /// how the weather zones at a point weight the environments. The old rule summed every zone's blend factor per
    /// environment and normalized; with the world-wide Myrtana_WeatherZone_01 (circle r 8/10 km, factor 1 everywhere) Myrtana
    /// kept 17–25 % of Nordmar's weather and the Nordmar share followed how many Nordmar zones overlapped — the Nordmar_High
    /// spheres round the Hammer clan have 5–10 m ramps, so the share (and with it the snowfall above 50 % and which state was
    /// the second of the two rendered ones) jumped at their edge. [interp] Zones are layered like nested volumes instead: the
    /// smallest zone takes its factor of what is left, the next bigger one its factor of the rest, and so on; whatever no
    /// zone covers goes to the default environment. Inside a region its own zones cover everything; at a region's edge the
    /// weather blends over G3's own inner→outer ramp (100–200 m for the Nordmar/Varant weather circles).
    /// </summary>
    public static class WeatherZoneBlend
    {
        static readonly Comparison<ZoneWeight> BySize = (a, b) => a.Size.CompareTo(b.Size);

        /// <summary>
        /// Environment weights (summing to 1) into <paramref name="into"/> (cleared first). <paramref name="zones"/> is
        /// sorted in place (smallest first; equal sizes keep a stable order). Returns the share left to the default.
        /// </summary>
        public static float Layer(List<ZoneWeight> zones, IDictionary<string, float> into, string defaultEnvironment)
        {
            into.Clear();
            StableSort(zones);
            float left = 1f;
            foreach (var z in zones)
            {
                if (left <= 1e-5f) break;
                float f = Math.Max(0f, Math.Min(1f, z.Factor));
                if (f <= 0f || string.IsNullOrEmpty(z.Environment)) continue;
                float w = f * left;
                into[z.Environment] = (into.TryGetValue(z.Environment, out var o) ? o : 0f) + w;
                left -= w;
            }
            if (left > 1e-5f && !string.IsNullOrEmpty(defaultEnvironment))
                into[defaultEnvironment] = (into.TryGetValue(defaultEnvironment, out var d) ? d : 0f) + left;
            return left;
        }

        // insertion sort: stable, no allocation, a handful of zones per point
        static void StableSort(List<ZoneWeight> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                var x = list[i];
                int j = i - 1;
                while (j >= 0 && BySize(list[j], x) > 0) { list[j + 1] = list[j]; j--; }
                list[j + 1] = x;
            }
        }
    }

    /// <summary>
    /// fix-215: a time cross-fade of a weight map (weather state → weight). Every step moves all weights towards the target
    /// by the same fraction of their remaining difference, so the sum stays the target's (1) and no weight changes faster
    /// than 1 / <see cref="FadeSeconds"/> per second: a full swap of one state for another takes FadeSeconds, linear; the
    /// slow schedule fades (hours) pass through unchanged. States that faded out are dropped. <see cref="Snap"/> jumps
    /// (first frame, teleport, a loaded save).
    /// </summary>
    public sealed class WeightCrossfade
    {
        readonly Dictionary<string, float> _w = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _keys = new List<string>();
        bool _has;

        public float FadeSeconds;

        public WeightCrossfade(float fadeSeconds) { FadeSeconds = fadeSeconds; }

        /// <summary>The faded weights (live; valid until the next <see cref="Step"/> / <see cref="Snap"/>).</summary>
        public IReadOnlyDictionary<string, float> Weights => _w;

        public bool HasValue => _has;

        public void Clear() { _w.Clear(); _has = false; }

        /// <summary>Takes the target as is.</summary>
        public IReadOnlyDictionary<string, float> Snap(IReadOnlyDictionary<string, float> target)
        {
            _w.Clear();
            foreach (var kv in target) if (kv.Value > 0f) _w[kv.Key] = kv.Value;
            _has = true;
            return _w;
        }

        /// <summary>Moves towards <paramref name="target"/> over <paramref name="dt"/> seconds.</summary>
        public IReadOnlyDictionary<string, float> Step(IReadOnlyDictionary<string, float> target, float dt)
        {
            if (!_has || FadeSeconds <= 0f) return Snap(target);
            float maxDelta = 0f;
            foreach (var kv in target)
                maxDelta = Math.Max(maxDelta, Math.Abs(kv.Value - (_w.TryGetValue(kv.Key, out var c) ? c : 0f)));
            foreach (var kv in _w)
                if (!target.ContainsKey(kv.Key)) maxDelta = Math.Max(maxDelta, Math.Abs(kv.Value));
            if (maxDelta <= 1e-6f) return Snap(target);
            float step = Math.Max(0f, dt) / FadeSeconds;
            if (step >= maxDelta) return Snap(target);
            float k = step / maxDelta;
            _keys.Clear();
            foreach (var key in _w.Keys) _keys.Add(key);
            foreach (var kv in target) if (!_w.ContainsKey(kv.Key)) _keys.Add(kv.Key);
            foreach (var key in _keys)
            {
                float cur = _w.TryGetValue(key, out var c) ? c : 0f;
                float to = target.TryGetValue(key, out var t) ? t : 0f;
                float v = cur + (to - cur) * k;
                if (v <= 1e-4f && to <= 0f) _w.Remove(key);
                else _w[key] = v;
            }
            return _w;
        }
    }

    /// <summary>fix-215: values of an N-state weather mix (every state with its weight), continuous in the weights.</summary>
    public static class WeatherMix
    {
        /// <summary>Weighted mean of <paramref name="value"/> over the mix (weights normalized; empty → <paramref name="empty"/>).</summary>
        public static float Mean(IReadOnlyList<KeyValuePair<string, float>> mix, Func<string, float> value, float empty)
        {
            if (mix == null || mix.Count == 0) return empty;
            double sum = 0, wsum = 0;
            for (int i = 0; i < mix.Count; i++)
            {
                float w = mix[i].Value;
                if (w <= 0f) continue;
                sum += w * value(mix[i].Key);
                wsum += w;
            }
            return wsum > 0 ? (float)(sum / wsum) : empty;
        }

        /// <summary>
        /// G3's precipitation rule over a mix: the one state above 50 % counts, scaled by (w − 0.5) · 2
        /// (<see cref="WeatherRules.Dominant"/>); <paramref name="value"/> of that state times the intensity.
        /// </summary>
        public static float Dominant(IReadOnlyList<KeyValuePair<string, float>> mix, Func<string, float> value)
        {
            if (mix == null) return 0f;
            for (int i = 0; i < mix.Count; i++)
                if (mix[i].Value > 0.5f) return value(mix[i].Key) * Math.Min(1f, (mix[i].Value - 0.5f) * 2f);
            return 0f;
        }

        /// <summary>The weights as a list, heaviest first (ties by name), into <paramref name="into"/> (cleared first).</summary>
        public static void ToList(IReadOnlyDictionary<string, float> weights, List<KeyValuePair<string, float>> into)
        {
            into.Clear();
            foreach (var kv in weights) if (kv.Value > 0f) into.Add(kv);
            into.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
        }
    }
}
