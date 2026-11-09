#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// One dev-scenario command (Docs/dev-scenarios.md): an op name and its arguments by name. Positional arguments of the
    /// source text are bound to names by <see cref="ScenarioOps"/>; values are plain strings (invariant culture), "$name"
    /// references are resolved when the step runs (<see cref="ScenarioVars"/>).
    /// </summary>
    public sealed class ScenarioStep
    {
        readonly Dictionary<string, string> _args;

        public ScenarioStep(string op, IDictionary<string, string> args, string source)
        {
            Op = op ?? throw new ArgumentNullException(nameof(op));
            _args = new Dictionary<string, string>(args ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            Source = source ?? op;
        }

        public string Op { get; }
        public IReadOnlyDictionary<string, string> Args => _args;
        /// <summary>The statement as written (for logs).</summary>
        public string Source { get; }

        public bool Has(string name) => _args.ContainsKey(name) && !string.IsNullOrEmpty(_args[name]);

        public string Get(string name, string fallback = null) => _args.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

        public float Float(string name, float fallback) =>
            _args.TryGetValue(name, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

        public int Int(string name, int fallback) =>
            _args.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        public bool Bool(string name, bool fallback) => _args.TryGetValue(name, out var v) && TryBool(v, out var b) ? b : fallback;

        /// <summary>A copy with every value passed through <paramref name="map"/> (variable resolution).</summary>
        public ScenarioStep Map(Func<string, string> map)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _args) copy[kv.Key] = map(kv.Value);
            return new ScenarioStep(Op, copy, Source);
        }

        public static bool TryBool(string value, out bool result)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "1": case "true": case "on": case "yes": case "y": result = true; return true;
                case "0": case "false": case "off": case "no": case "n": result = false; return true;
                default: result = false; return false;
            }
        }

        /// <summary>"x,y,z" or "x,z" (metres) → numbers; false otherwise.</summary>
        public static bool TryVector(string value, out float[] xyz)
        {
            xyz = null;
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split(',');
            if (parts.Length != 2 && parts.Length != 3) return false;
            var r = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return false;
            xyz = r;
            return true;
        }

        public override string ToString() => Source;
    }
}
#endif
