#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// A condition of 'wait until=' and 'expect': <c>kind[:arg][op value]</c>, op one of = != &lt; &gt; &lt;= &gt;= ~ (contains).
    /// Without op the value must be true ("swimming", "dialog"); a leading '!' negates that ("!dialog").
    /// The Unity side reads the actual value of kind/arg as a string; <see cref="Test"/> compares (numbers numerically).
    /// </summary>
    public sealed class ScenarioCondition
    {
        static readonly string[] Ops = { "!=", "<=", ">=", "=", "<", ">", "~" };

        public string Kind { get; private set; }
        public string Arg { get; private set; }
        public string Op { get; private set; }
        public string Value { get; private set; }
        public string Source { get; private set; }

        public static bool TryParse(string text, out ScenarioCondition condition, out string error)
        {
            condition = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "empty condition"; return false; }
            string s = text.Trim();
            bool negate = s.StartsWith("!") && !s.StartsWith("!=");
            if (negate) s = s.Substring(1);
            int at = -1;
            string op = null;
            for (int i = 0; i < s.Length && at < 0; i++)
                foreach (var o in Ops)
                    if (string.CompareOrdinal(s, i, o, 0, o.Length) == 0 && !IsDevMark(s, i)) { at = i; op = o; break; }
            string left = at >= 0 ? s.Substring(0, at) : s;
            string value = at >= 0 ? s.Substring(at + op.Length).Trim() : "true";
            if (at < 0) op = "=";
            if (negate)
            {
                if (at >= 0) { error = $"'{text}': '!' only before a condition without operator"; return false; }
                value = "false";
            }
            int colon = left.IndexOf(':');
            string kind = (colon >= 0 ? left.Substring(0, colon) : left).Trim();
            string arg = colon >= 0 ? left.Substring(colon + 1).Trim() : null;
            if (kind.Length == 0) { error = $"'{text}': no condition kind"; return false; }
            condition = new ScenarioCondition { Kind = kind.ToLowerInvariant(), Arg = arg, Op = op, Value = value, Source = text.Trim() };
            return true;
        }

        /// <summary>
        /// "Kent~dev01": the '~' of a dev-spawn id (<see cref="DevNpcs.Mark"/> + digits) is part of the actor name, not
        /// the contains operator [fix-38: "state:Kent~dev01=Down" was read as state:Kent ~ "dev01=Down"].
        /// </summary>
        static bool IsDevMark(string s, int i)
        {
            string mark = DevNpcs.Mark;
            if (string.CompareOrdinal(s, i, mark, 0, mark.Length) != 0) return false;
            int j = i + mark.Length;
            return j < s.Length && char.IsDigit(s[j]);
        }

        public static ScenarioCondition Parse(string text) =>
            TryParse(text, out var c, out var e) ? c : throw new FormatException(e);

        /// <summary>True when <paramref name="actual"/> satisfies the condition.</summary>
        public bool Test(string actual)
        {
            if (actual == null) return Op == "!=";
            bool numbers = TryNumber(actual, out double a) & TryNumber(Value, out double b);
            switch (Op)
            {
                case "=": return numbers ? Math.Abs(a - b) < 1e-6 : Same(actual, Value);
                case "!=": return numbers ? Math.Abs(a - b) >= 1e-6 : !Same(actual, Value);
                case "<": return numbers && a < b;
                case ">": return numbers && a > b;
                case "<=": return numbers && a <= b;
                case ">=": return numbers && a >= b;
                case "~": return actual.IndexOf(Value, StringComparison.OrdinalIgnoreCase) >= 0;
                default: return false;
            }
        }

        static bool Same(string a, string b)
        {
            if (ScenarioStep.TryBool(a, out var x) && ScenarioStep.TryBool(b, out var y)) return x == y;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        static bool TryNumber(string s, out double d) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);

        public override string ToString() => Source;
    }

    /// <summary>"$name" / "$name.field" references in step values (set by spawn as=, bring as=, tp spot=, set).</summary>
    public static class ScenarioVars
    {
        static readonly Regex Ref = new Regex(@"\$([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)?)", RegexOptions.Compiled);

        /// <summary>Replaces known references; unknown ones are added to <paramref name="missing"/> and left as written.</summary>
        public static string Resolve(string value, IReadOnlyDictionary<string, string> vars, ICollection<string> missing = null)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('$') < 0) return value;
            return Ref.Replace(value, m =>
            {
                if (vars != null && vars.TryGetValue(m.Groups[1].Value, out var v)) return v;
                missing?.Add(m.Value);
                return m.Value;
            });
        }
    }
}
#endif
