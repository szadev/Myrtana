#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Myrtana.Core;

namespace Myrtana.Session.Dev
{
    /// <summary>A scenario file (Tools/Scenarios/&lt;name&gt;.json) after parsing.</summary>
    public sealed class ScenarioSpec
    {
        public string Name { get; set; }
        /// <summary>What the scenario verifies (handoff item), shown in the result line.</summary>
        public string Verifies { get; set; }
        /// <summary>The runner wrapper starts a fresh Play first.</summary>
        public bool Fresh { get; set; }
        /// <summary>Game seconds after which the run stops (default 120).</summary>
        public float TimeoutSeconds { get; set; } = 120f;
        public string Notes { get; set; }
        /// <summary>fix-54: minutes between checkpoint saves inside the run (0 = none); a passed run also saves (<see cref="ScenarioCheckpoints"/>).</summary>
        public float CheckpointMinutes { get; set; }
        /// <summary>fix-54: answer dialogs the scenario did not open (null = <see cref="DialogAutoAnswer.DefaultOn"/>).</summary>
        public bool? AutoDialog { get; set; }
        /// <summary>fix-54: dialog entries the scenario expects an NPC-initiated talk to take ("answers": [...]).</summary>
        public List<string> Answers { get; } = new List<string>();
        /// <summary>fix-54: the loadout the run starts with ("loadout": "bow"; <see cref="ScenarioLoadout"/>).</summary>
        public string Loadout { get; set; }
        /// <summary>fix-54: names in give/equip/spell must match uniquely (scenario files); the dev console takes the first match.</summary>
        public bool LooseNames { get; set; }
        /// <summary>
        /// fix-119: the seed the runner pins the session random to when the run starts (null = the session's own fresh seed).
        /// Scenario files default to <see cref="ScenarioParser.DefaultSeed"/> ("seed": n | "live"); inline runs and the dev
        /// console leave the game's random alone. Loads inside a run roll anew, as in the game.
        /// </summary>
        public ulong? Seed { get; set; }
        public List<ScenarioStep> Steps { get; } = new List<ScenarioStep>();
        public List<string> Errors { get; } = new List<string>();
        public bool Ok => Errors.Count == 0;
    }

    /// <summary>
    /// Parses scenarios. Inline text: statements separated by ';' or new lines, tokens by spaces, "quoted" or 'quoted'
    /// values may hold spaces, key=value names an argument, '#' or '//' starts a comment line. JSON files hold
    /// {name, verifies, fresh, timeout, notes, steps:[ "inline statement" | {"do":"op", "arg":value, ...} ]}.
    /// </summary>
    public static class ScenarioParser
    {
        public static ScenarioSpec ParseInline(string text, string name = "inline")
        {
            var spec = new ScenarioSpec { Name = name };
            foreach (var statement in SplitStatements(text))
                AddStatement(spec, statement);
            return spec;
        }

        /// <summary>fix-119: the seed a scenario file pins unless it says "seed" (1 = the seed every new game had before fix-119).</summary>
        public const ulong DefaultSeed = 1;

        public static ScenarioSpec ParseJson(string json, string fallbackName = null)
        {
            var spec = new ScenarioSpec { Name = fallbackName, Seed = DefaultSeed };
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException e)
            {
                spec.Errors.Add("bad JSON: " + e.Message);
                return spec;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    spec.Errors.Add("scenario JSON must be an object");
                    return spec;
                }
                foreach (var p in root.EnumerateObject())
                {
                    switch (p.Name.ToLowerInvariant())
                    {
                        case "name": spec.Name = Text(p.Value); break;
                        case "verifies": spec.Verifies = Text(p.Value); break;
                        case "notes": spec.Notes = Text(p.Value); break;
                        case "seed": // a number pins it, "live" / "random" / null = the session's fresh seed
                            spec.Seed = SessionSeed.Parse(Text(p.Value));
                            if (spec.Seed == null && !IsLiveSeed(Text(p.Value))) spec.Errors.Add($"bad seed '{Text(p.Value)}' (a number or \"live\")");
                            break;
                        case "fresh": spec.Fresh = p.Value.ValueKind == JsonValueKind.True || ScenarioStep.TryBool(Text(p.Value), out var f) && f; break;
                        case "timeout":
                            if (float.TryParse(Text(p.Value), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) spec.TimeoutSeconds = t;
                            break;
                        case "checkpoint": // true = every ScenarioCheckpoints.DefaultMinutes, a number = minutes, false/0 = off
                            if (p.Value.ValueKind == JsonValueKind.True) spec.CheckpointMinutes = ScenarioCheckpoints.DefaultMinutes;
                            else if (float.TryParse(Text(p.Value), NumberStyles.Float, CultureInfo.InvariantCulture, out var cm)) spec.CheckpointMinutes = Math.Max(0f, cm);
                            break;
                        case "autodialog":
                            if (p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.False) spec.AutoDialog = p.Value.ValueKind == JsonValueKind.True;
                            else if (ScenarioStep.TryBool(Text(p.Value), out var ad)) spec.AutoDialog = ad;
                            break;
                        case "answers":
                            foreach (var a in (Text(p.Value) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) spec.Answers.Add(a.Trim());
                            break;
                        case "loadout":
                            spec.Loadout = Text(p.Value);
                            if (!ScenarioLoadout.ValidFileValue(spec.Loadout))
                                spec.Errors.Add($"unknown loadout '{spec.Loadout}' ({ScenarioLoadout.Names}|{ScenarioLoadout.Own})");
                            break;
                        case "steps":
                            if (p.Value.ValueKind != JsonValueKind.Array) { spec.Errors.Add("'steps' must be an array"); break; }
                            foreach (var s in p.Value.EnumerateArray())
                            {
                                if (s.ValueKind == JsonValueKind.String)
                                    foreach (var statement in SplitStatements(s.GetString())) AddStatement(spec, statement);
                                else if (s.ValueKind == JsonValueKind.Object) AddObjectStep(spec, s);
                                else spec.Errors.Add("a step must be a string or an object: " + s.GetRawText());
                            }
                            break;
                    }
                }
            }
            if (string.IsNullOrEmpty(spec.Name)) spec.Errors.Add("scenario has no name");
            if (spec.Steps.Count == 0 && spec.Errors.Count == 0) spec.Errors.Add("scenario has no steps");
            return spec;
        }

        static void AddObjectStep(ScenarioSpec spec, JsonElement s)
        {
            string op = null;
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in s.EnumerateObject())
            {
                if (string.Equals(p.Name, "do", StringComparison.OrdinalIgnoreCase)) op = Text(p.Value);
                else named[p.Name] = Text(p.Value);
            }
            if (string.IsNullOrEmpty(op))
            {
                spec.Errors.Add("object step without \"do\": " + s.GetRawText());
                return;
            }
            var step = ScenarioOps.Bind(op, Array.Empty<string>(), named, s.GetRawText(), spec.Errors);
            if (step != null) spec.Steps.Add(step);
        }

        static void AddStatement(ScenarioSpec spec, string statement)
        {
            var tokens = Tokenize(statement);
            if (tokens.Count == 0) return;
            var positional = new List<string>();
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < tokens.Count; i++)
            {
                var (text, quoted) = tokens[i];
                int eq = quoted ? -1 : text.IndexOf('=');
                // "hp:Grok<10" style conditions contain '=' after other characters; only a leading identifier is a name
                // "expect partner=Kent" is a condition, not an argument named "partner"
                bool condition = ScenarioOps.TryGet(tokens[0].text, out var opSpec) && opSpec.Name == "expect";
                // ... except the unless= every op takes
                bool unless = eq > 0 && (text.Substring(0, eq).Equals(OpSpec.Unless, StringComparison.OrdinalIgnoreCase) ||
                                         text.Substring(0, eq).Equals(OpSpec.Answer, StringComparison.OrdinalIgnoreCase));
                if (eq > 0 && (!condition || unless) && IsIdentifier(text.Substring(0, eq))) named[text.Substring(0, eq)] = Unquote(text.Substring(eq + 1));
                else positional.Add(text);
            }
            var step = ScenarioOps.Bind(tokens[0].text, positional, named, statement.Trim(), spec.Errors);
            if (step != null) spec.Steps.Add(step);
        }

        static bool IsIdentifier(string s)
        {
            if (s.Length == 0 || !char.IsLetter(s[0])) return false;
            foreach (char c in s) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        static string Unquote(string v) =>
            v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0] ? v.Substring(1, v.Length - 2) : v;

        /// <summary>Statements of an inline text: split at ';' and new lines outside quotes, comments dropped.</summary>
        public static List<string> SplitStatements(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            var sb = new StringBuilder();
            char quote = '\0';
            void Flush()
            {
                var s = sb.ToString().Trim();
                sb.Clear();
                if (s.Length > 0 && !s.StartsWith("#") && !s.StartsWith("//")) result.Add(s);
            }
            foreach (char c in text)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; sb.Append(c); continue; }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); continue; }
                if (c == ';' || c == '\n' || c == '\r') { Flush(); continue; }
                sb.Append(c);
            }
            Flush();
            return result;
        }

        /// <summary>Tokens of one statement; quotes group spaces (removed from the value), quoted = the token was quoted.</summary>
        public static List<(string text, bool quoted)> Tokenize(string statement)
        {
            var tokens = new List<(string, bool)>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;
            char quote = '\0';
            foreach (char c in statement ?? "")
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else sb.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    // a quote at the token start makes the whole token a value; inside key="a b" it only groups
                    if (sb.Length == 0) quoted = true;
                    any = true;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    if (any || sb.Length > 0) tokens.Add((sb.ToString(), quoted));
                    sb.Clear(); quoted = false; any = false;
                    continue;
                }
                sb.Append(c);
                any = true;
            }
            if (any || sb.Length > 0) tokens.Add((sb.ToString(), quoted));
            return tokens;
        }

        static bool IsLiveSeed(string text) =>
            text == null || string.Equals(text, "live", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "random", StringComparison.OrdinalIgnoreCase);

        static string Text(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null: return null;
                case JsonValueKind.Number: return e.GetRawText();
                case JsonValueKind.Array:
                    var parts = new List<string>();
                    foreach (var x in e.EnumerateArray()) parts.Add(Text(x));
                    return string.Join(",", parts);
                default: return e.GetRawText();
            }
        }
    }
}
#endif
