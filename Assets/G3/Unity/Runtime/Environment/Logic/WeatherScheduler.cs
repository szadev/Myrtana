using System;
using System.Collections.Generic;

namespace G3.Weather
{
    /// <summary>One eSProbability of a weather environment: state, probability, fade duration range (fractions of a day).</summary>
    public readonly struct WeatherChance
    {
        public readonly string State;
        public readonly float Probability, MinDuration, MaxDuration;
        public WeatherChance(string state, float probability, float minDuration, float maxDuration)
        { State = state; Probability = probability; MinDuration = minDuration; MaxDuration = maxDuration; }
    }

    /// <summary>weather-opt: the Remaster page's "Weather" row. Original = G3's rule; Fair = <see cref="WeatherFair"/>.</summary>
    public enum WeatherMode { Original = 0, Fair = 1 }

    /// <summary>
    /// weather-opt (owner-approved remaster option): the "Fair" weather. Every non-sunny state's probability is halved, and a
    /// fade from a non-sunny state back to a sunny one is capped at <see cref="FadeCapDays"/> (6 h) so the grey tail is
    /// shorter; the sunny period itself still lasts the drawn duration (the fade ends early and the sunny state holds until
    /// the drawn time is up, then the next draw happens as in G3). Applies to draws only: a running fade is never changed.
    /// </summary>
    public static class WeatherFair
    {
        /// <summary>The Rainy/Snowy/Sandstormy → Sunny fade cap: 6 h (fractions of a day).</summary>
        public const double FadeCapDays = 6.0 / 24;

        /// <summary>"Sunny", "Sunny(Varant)", "Sunny(Nordmar)" …</summary>
        public static bool IsSunny(string state) =>
            state != null && state.StartsWith("Sunny", StringComparison.OrdinalIgnoreCase);

        /// <summary>The probability a draw uses: Fair halves every non-sunny state's.</summary>
        public static float Probability(WeatherMode mode, string state, float probability) =>
            mode == WeatherMode.Fair && !IsSunny(state) ? probability * 0.5f : probability;

        /// <summary>The fade time of a drawn transition of <paramref name="duration"/> days.</summary>
        public static double Fade(WeatherMode mode, string from, string to, double duration) =>
            mode == WeatherMode.Fair && IsSunny(to) && !IsSunny(from) ? Math.Min(duration, FadeCapDays) : duration;
    }

    /// <summary>
    /// The state machine of one G3 weather environment (eCWeatherEnvironment::Process, Engine.dll CP 1.75 0x1028f780;
    /// Docs/formats-weather.md §7). Weather is a chain of linear cross-fades: the environment blends from Current to Next
    /// over Duration days (Next weight = Progress). When a fade is complete (or the clock went backwards) Next becomes Current
    /// and a new Next is drawn: every other state with probability p &gt; 0 draws r ∈ [0,1]; it qualifies when r ≤ p and its
    /// score is r / p; the best score wins and the fade takes min + rand·(max − min) of that state. When nothing qualifies,
    /// the same state is kept for another fade of the old duration. The very first state is the most probable one, faded
    /// over its maximum duration. weather-opt: <see cref="Mode"/> Fair changes the draws (<see cref="WeatherFair"/>); the fade
    /// may then end before the period (<see cref="Fade"/> &lt; <see cref="Duration"/>) and Next holds until the period is over.
    /// </summary>
    public sealed class WeatherScheduler
    {
        readonly WeatherChance[] _chances;
        readonly Func<float> _rand01;

        public string Name { get; }
        public int Current { get; private set; } = -1;
        public int Next { get; private set; } = -1;
        /// <summary>Day time (days, continuous) when the current fade started.</summary>
        public double Start { get; private set; }
        /// <summary>Period until the next draw, days (G3: the fade duration).</summary>
        public double Duration { get; private set; } = 1.0 / 24;
        /// <summary>weather-opt: the fade time, days (= <see cref="Duration"/> except a capped Fair fade back to sunny).</summary>
        public double Fade { get; private set; } = 1.0 / 24;
        /// <summary>weather-opt: the rule of the next draws (a running fade keeps its times).</summary>
        public WeatherMode Mode { get; set; }
        bool _due = true;
        /// <summary>Fade progress 0..1 (weight of Next).</summary>
        public float Progress { get; private set; } = 1f;

        public IReadOnlyList<WeatherChance> Chances => _chances;
        public string CurrentState => Current >= 0 ? _chances[Current].State : null;
        public string NextState => Next >= 0 ? _chances[Next].State : null;

        public WeatherScheduler(string name, IEnumerable<WeatherChance> chances, Func<float> rand01)
        {
            Name = name;
            _chances = new List<WeatherChance>(chances).ToArray();
            _rand01 = rand01 ?? throw new ArgumentNullException(nameof(rand01));
        }

        /// <summary>Advances to <paramref name="dayTime"/> (days, continuous: day + hour / 24).</summary>
        public void Update(double dayTime)
        {
            if (_chances.Length == 0) return;
            if (_due || Start > dayTime || Current < 0) Reselect(dayTime);
            double f = Fade > 0 ? (dayTime - Start) / Fade : 1.0;
            Progress = f < 0 ? 0f : f > 1 ? 1f : (float)f;
            double g = Duration > 0 ? (dayTime - Start) / Duration : 1.0;
            _due = g >= 1 || (float)g >= 1f;   // the old "Progress >= 1f" when Fade == Duration
        }

        void Reselect(double dayTime)
        {
            Start = dayTime;
            Current = Next;
            if (Current < 0)
            {
                int best = 0;
                for (int i = 1; i < _chances.Length; i++)
                    if (_chances[i].Probability > _chances[best].Probability) best = i;
                Current = best;
                Duration = _chances[best].MaxDuration;
            }
            float bestScore = 0f;
            for (int i = 0; i < _chances.Length; i++)
            {
                var c = _chances[i];
                float p = WeatherFair.Probability(Mode, c.State, c.Probability);
                if (p <= 0 || i == Current) continue;
                float r = _rand01();
                if (p < r) continue;
                float score = r / p;
                if (score <= bestScore) continue;
                bestScore = score;
                Next = i;
                Duration = c.MinDuration + _rand01() * (c.MaxDuration - c.MinDuration);
            }
            if (bestScore <= 0f) Next = Current;
            if (Duration <= 0) Duration = 1.0 / 24;
            Fade = Next != Current ? WeatherFair.Fade(Mode, _chances[Current].State, _chances[Next].State, Duration) : Duration;
        }

        /// <summary>Adds this environment's state weights (Current 1 − p, Next p) times <paramref name="weight"/> (AddWeatherStateBlendFactor).</summary>
        public void AddWeights(float weight, IDictionary<string, float> into)
        {
            if (Current < 0 || weight <= 0) return;
            Add(into, _chances[Current].State, weight * (1f - Progress));
            Add(into, _chances[Next >= 0 ? Next : Current].State, weight * Progress);
        }

        /// <summary>Restores a saved schedule (weather survives save/load).</summary>
        /// <remarks><paramref name="fade"/> ≤ 0 or ≥ the duration: the fade lasts the whole period (G3; saves before weather-opt).</remarks>
        public void Restore(int current, int next, double start, double duration, double fade = 0)
        {
            Current = current < _chances.Length ? current : -1;
            Next = next < _chances.Length ? next : -1;
            Start = start;
            Duration = duration > 0 ? duration : 1.0 / 24;
            Fade = fade > 0 && fade < Duration ? fade : Duration;
            Progress = 0f;
            _due = false;
        }

        /// <summary>The schedule by state names (save games; robust to a reordered weather file).</summary>
        public WeatherScheduleSnapshot Capture() => new WeatherScheduleSnapshot(Name, CurrentState, NextState, Start, Duration, Fade);

        /// <summary>Restores a <see cref="Capture"/>; unknown state names leave the scheduler to pick anew. False when nothing applied.</summary>
        public bool Restore(WeatherScheduleSnapshot s)
        {
            int c = IndexOf(s.Current), n = IndexOf(s.Next);
            if (c < 0) return false;
            Restore(c, n < 0 ? c : n, s.Start, s.Duration, s.Fade);
            return true;
        }

        int IndexOf(string state)
        {
            if (string.IsNullOrEmpty(state)) return -1;
            for (int i = 0; i < _chances.Length; i++)
                if (string.Equals(_chances[i].State, state, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static void Add(IDictionary<string, float> d, string k, float v)
        {
            if (v <= 0) return;
            d[k] = d.TryGetValue(k, out var o) ? o + v : v;
        }
    }

    /// <summary>A saved weather schedule of one environment (effects-2: weather in saves).</summary>
    public readonly struct WeatherScheduleSnapshot
    {
        public readonly string Environment, Current, Next;
        public readonly double Start, Duration;
        /// <summary>weather-opt: the fade time (= Duration unless a capped Fair fade).</summary>
        public readonly double Fade;
        public WeatherScheduleSnapshot(string environment, string current, string next, double start, double duration, double fade = 0)
        {
            Environment = environment; Current = current; Next = next; Start = start; Duration = duration;
            Fade = fade > 0 && fade < duration ? fade : duration;
        }

        /// <summary>Snapshots as one line ("env|current|next|start|duration" joined by ';', invariant numbers); a capped
        /// fade adds "|fade" (weather-opt; older saves have five fields and fade over the whole duration).</summary>
        public static string Encode(IEnumerable<WeatherScheduleSnapshot> list)
        {
            var parts = new List<string>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var s in list)
            {
                string e = string.Join("|", Clean(s.Environment), Clean(s.Current), Clean(s.Next),
                                       s.Start.ToString("R", inv), s.Duration.ToString("R", inv));
                if (s.Fade < s.Duration) e += "|" + s.Fade.ToString("R", inv);
                parts.Add(e);
            }
            return string.Join(";", parts);
        }

        /// <summary>The snapshots of <see cref="Encode"/>; malformed entries are skipped.</summary>
        public static List<WeatherScheduleSnapshot> Decode(string text)
        {
            var list = new List<WeatherScheduleSnapshot>();
            if (string.IsNullOrEmpty(text)) return list;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var e in text.Split(';'))
            {
                var f = e.Split('|');
                if ((f.Length != 5 && f.Length != 6) || !double.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out var start) ||
                    !double.TryParse(f[4], System.Globalization.NumberStyles.Float, inv, out var duration)) continue;
                double fade = 0;
                if (f.Length == 6 && !double.TryParse(f[5], System.Globalization.NumberStyles.Float, inv, out fade)) fade = 0;
                list.Add(new WeatherScheduleSnapshot(f[0], f[1].Length > 0 ? f[1] : null, f[2].Length > 0 ? f[2] : null, start, duration, fade));
            }
            return list;
        }

        static string Clean(string s) => (s ?? "").Replace("|", "").Replace(";", "");
    }

    /// <summary>Precipitation and loop sound of the blended weather (eCWeatherAdmin::Process, Engine.dll CP 1.75 ~0x1028e41a).</summary>
    public static class WeatherRules
    {
        /// <summary>
        /// The precipitation comes from the one state whose blend weight exceeds 0.5; its particle rate is scaled by
        /// (weight − 0.5) · 2 (none at 50 %, full at 100 %). No such state → no precipitation.
        /// </summary>
        public static bool Dominant(IReadOnlyDictionary<string, float> normalizedWeights, out string state, out float intensity)
        {
            foreach (var kv in normalizedWeights)
                if (kv.Value > 0.5f)
                {
                    state = kv.Key;
                    intensity = Math.Min(1f, (kv.Value - 0.5f) * 2f);
                    return true;
                }
            state = null;
            intensity = 0f;
            return false;
        }

        /// <summary>Weights divided by their sum (empty when the sum is 0).</summary>
        public static Dictionary<string, float> Normalize(IDictionary<string, float> weights) =>
            Normalize(weights, new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// <see cref="Normalize(IDictionary{string, float})"/> into <paramref name="into"/> (cleared first, returned), so a
        /// per-frame caller does not allocate.
        /// </summary>
        public static Dictionary<string, float> Normalize(IDictionary<string, float> weights, Dictionary<string, float> into)
        {
            into.Clear();
            float sum = 0;
            foreach (var v in weights.Values) sum += v;
            if (sum <= 0) return into;
            foreach (var kv in weights) into[kv.Key] = kv.Value / sum;
            return into;
        }

        /// <summary>The two heaviest states and the blend of the second (for a two-state environment renderer).</summary>
        public static void TopTwo(IReadOnlyDictionary<string, float> weights, out string a, out string b, out float blend)
        {
            a = null; b = null;
            float wa = 0, wb = 0;
            foreach (var kv in weights)
            {
                if (kv.Value > wa) { b = a; wb = wa; a = kv.Key; wa = kv.Value; }
                else if (kv.Value > wb) { b = kv.Key; wb = kv.Value; }
            }
            blend = b != null && wa + wb > 0 ? wb / (wa + wb) : 0f;
        }

        /// <summary>Continuous game day time: whole days elapsed plus hour / 24, advanced from successive hours (wraps at 24).</summary>
        public static double AdvanceDayTime(double dayTime, float previousHour, float hour)
        {
            float d = hour - previousHour;
            if (d < -12f) d += 24f;          // passed midnight
            else if (d > 12f) d -= 24f;      // set back across midnight
            return dayTime + d / 24.0;
        }
    }
}
