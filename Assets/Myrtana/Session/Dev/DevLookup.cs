#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// Name search for dev commands: "It_1H_Sword_01", "sword_01" or a localized name part ("меч") finds an item; the
    /// same for NPC templates, quests, skills. Ranking: exact id, id prefix, exact name, name prefix, id part, name
    /// part, then every word of the query somewhere in id or name; ties by id length, then ordinal.
    /// </summary>
    public static class DevLookup
    {
        /// <summary>
        /// Languages whose names are searched besides the current text language (editor-runner-16: "give меч" found
        /// nothing while the text language was English).
        /// </summary>
        public static readonly string[] SearchLanguages = { "Russian", "English" };

        /// <summary>Search entries of an id: its name in the current text language and in <see cref="SearchLanguages"/>.</summary>
        public static IEnumerable<(string id, string name)> Names(string id, TextId name, ILocalizer localizer)
        {
            if (localizer == null) { yield return (id, null); yield break; }
            yield return (id, localizer.Text(name));
            if (localizer is Localizer l)
                foreach (var language in SearchLanguages)
                    if (l.TryTextIn(language, name, out var text)) yield return (id, text);
        }

        /// <summary>
        /// Ranked ids; an id may come several times (one entry per language of its name): its best entry counts.
        /// </summary>
        public static List<string> Rank(IEnumerable<(string id, string name)> entries, string query, int max = 20)
        {
            if (string.IsNullOrWhiteSpace(query) || entries == null) return new List<string>();
            string q = query.Trim();
            var words = q.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
            var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var (id, name) in entries)
            {
                if (string.IsNullOrEmpty(id)) continue;
                int s = Score(id, name ?? "", q, words);
                if (s < 0) continue;
                if (!best.TryGetValue(id, out int had)) { best[id] = s; order.Add(id); }
                else if (s < had) best[id] = s;
            }
            return order.OrderBy(id => best[id]).ThenBy(id => id.Length).ThenBy(id => id, StringComparer.Ordinal)
                        .Take(max).ToList();
        }

        /// <summary>The best match or null; <paramref name="others"/> = how many more matched.</summary>
        public static string Best(IEnumerable<(string id, string name)> entries, string query, out int others)
        {
            var all = Rank(entries, query, 1000);
            others = Math.Max(0, all.Count - 1);
            return all.FirstOrDefault();
        }

        /// <summary>
        /// The match of a query with how it matched (fix-54: r30 "give It_Arrow" silently gave It_Book_Arrow, "It_Staff_01"
        /// became It_Staff_Mana_01): <see cref="LookupMatch.Exact"/> (the id, any case), a unique best match, or
        /// <see cref="LookupMatch.Ambiguous"/> (several share the best rank) / <see cref="LookupMatch.Loose"/> (only every
        /// word somewhere, not one part) — the runner refuses the last two in scenario files and names the candidates.
        /// </summary>
        public static LookupResult Resolve(IEnumerable<(string id, string name)> entries, string query)
        {
            var result = new LookupResult { Query = query };
            if (string.IsNullOrWhiteSpace(query) || entries == null) return result;
            string q = query.Trim();
            var words = q.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
            var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, name) in entries)
            {
                if (string.IsNullOrEmpty(id)) continue;
                int s = Score(id, name ?? "", q, words);
                if (s < 0) continue;
                if (!best.TryGetValue(id, out int had) || s < had) best[id] = s;
            }
            if (best.Count == 0) return result;
            var ranked = best.Keys.OrderBy(id => best[id]).ThenBy(id => id.Length).ThenBy(id => id, StringComparer.Ordinal).ToList();
            int top = best[ranked[0]];
            result.Id = ranked[0];
            result.Candidates = ranked;
            result.Tied = ranked.Where(id => best[id] == top).ToList();
            result.Match = top == 0 ? LookupMatch.Exact
                : result.Tied.Count > 1 ? LookupMatch.Ambiguous
                : top >= 6 ? LookupMatch.Loose
                : top == 1 || top == 3 ? LookupMatch.Prefix
                : top == 2 ? LookupMatch.Name
                : LookupMatch.Part;
            return result;
        }

        static int Score(string id, string name, string q, string[] words)
        {
            const StringComparison C = StringComparison.OrdinalIgnoreCase;
            if (id.Equals(q, C)) return 0;
            if (id.StartsWith(q, C)) return 1;
            if (name.Length > 0 && name.Equals(q, C)) return 2;
            if (name.Length > 0 && name.StartsWith(q, C)) return 3;
            if (id.IndexOf(q, C) >= 0) return 4;
            if (name.IndexOf(q, C) >= 0) return 5;
            if (words.Length > 1 && words.All(w => id.IndexOf(w, C) >= 0 || name.IndexOf(w, C) >= 0)) return 6;
            return -1;
        }

        /// <summary>Distinct template names of NPC ids: "Bloodfly#01ea064d" → "Bloodfly"; unique ids stay.</summary>
        public static IEnumerable<string> TemplateNames(IEnumerable<string> actorIds) =>
            actorIds.Select(a => { int h = a.IndexOf('#'); return h > 0 ? a.Substring(0, h) : a; }).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How a <see cref="DevLookup.Resolve"/> query matched.</summary>
    public enum LookupMatch { None, Exact, Prefix, Name, Part, Ambiguous, Loose }

    /// <summary>The result of <see cref="DevLookup.Resolve"/>.</summary>
    public sealed class LookupResult
    {
        public string Query { get; set; }
        /// <summary>The first ranked id (null = nothing matched).</summary>
        public string Id { get; set; }
        public LookupMatch Match { get; set; }
        /// <summary>The ids sharing the best rank.</summary>
        public IReadOnlyList<string> Tied { get; set; } = Array.Empty<string>();
        /// <summary>Every matching id, best first.</summary>
        public IReadOnlyList<string> Candidates { get; set; } = Array.Empty<string>();

        /// <summary>A unique match a scenario file may use (exact, prefix, name or one id/name part).</summary>
        public bool Safe => Match == LookupMatch.Exact || Match == LookupMatch.Prefix || Match == LookupMatch.Name || Match == LookupMatch.Part;

        /// <summary>"'q' is ambiguous: a, b, c (+4 more) ..." / "'q' is no id; it only loosely matches ..." / "".</summary>
        public string Problem
        {
            get
            {
                string List(IReadOnlyList<string> ids) => string.Join(", ", ids.Take(6)) + (ids.Count > 6 ? $" (+{ids.Count - 6} more)" : "");
                switch (Match)
                {
                    case LookupMatch.None: return $"nothing matches '{Query}'";
                    case LookupMatch.Ambiguous: return $"'{Query}' is ambiguous: {List(Tied)} - give an exact id";
                    case LookupMatch.Loose: return $"'{Query}' is no id; it only loosely matches {List(Candidates)} (every word somewhere) - give an exact id";
                    default: return "";
                }
            }
        }
    }

    /// <summary>
    /// Tab cycling of the dev console: the first Tab completes a single match (plus a space) or puts in the first of several
    /// full matches; each further Tab on the unchanged line puts in the next one (Shift+Tab the previous), wrapping around.
    /// Editing the line starts over. (editor-runner-16: Tab only completed the common prefix, so "give меч" with 30
    /// swords could not reach one.)
    /// </summary>
    public sealed class DevTabCycle
    {
        List<string> _matches;
        string _base, _produced;
        int _index;

        /// <summary>The matches of the current cycle (empty when none).</summary>
        public IReadOnlyList<string> Matches => (IReadOnlyList<string>)_matches ?? Array.Empty<string>();
        public int Index => _index;

        public void Reset() { _matches = null; _base = _produced = null; _index = 0; }

        /// <summary>The line after one Tab; <paramref name="complete"/> gives the matches of a line.</summary>
        public string Tab(string line, Func<string, List<string>> complete, bool backwards = false)
        {
            line = line ?? "";
            if (_matches != null && _matches.Count > 1 && line == _produced)
                _index = (_index + (backwards ? _matches.Count - 1 : 1)) % _matches.Count;
            else
            {
                var list = complete(line) ?? new List<string>();
                if (list.Count == 0) { Reset(); return line; }
                _matches = list;
                _base = line;
                _index = backwards ? list.Count - 1 : 0;
                if (list.Count == 1)
                {
                    string done = DevCompletion.Replace(line, list[0]) + " ";
                    _produced = null;
                    return done;
                }
            }
            _produced = DevCompletion.Replace(_base, _matches[_index]);
            return _produced;
        }
    }

    /// <summary>
    /// Tab completion for the dev console: the op name first, then a value list chosen by the op and argument position
    /// (items, NPCs, quests, skills, spells, scenarios, spots, weather states). <see cref="Values"/> supplies the lists.
    /// </summary>
    public sealed class DevCompletion
    {
        /// <summary>Candidates of a value kind ("item", "npc", "quest", "skill", "spell", "scenario", "spot", "weather", "op").</summary>
        public Func<string, IEnumerable<string>> Values { get; set; }

        /// <summary>
        /// Localized names of a value kind ("item", "spell", "npc"): (value, name) entries, Russian and English
        /// (<see cref="DevLookup.Names"/>), searched besides the values, so "give меч" cycles the swords (editor-runner-18).
        /// The completion is still the value (an id: names have spaces and depend on the language).
        /// </summary>
        public Func<string, IEnumerable<(string id, string name)>> Names { get; set; }

        readonly Dictionary<string, string> _labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The name each value of the last <see cref="Complete"/> matched by (or its first name), for the suggestion list.</summary>
        public IReadOnlyDictionary<string, string> Labels => _labels;

        /// <summary>"id (name)" when the last completion knows a name of <paramref name="value"/>, else the value.</summary>
        public string Label(string value)
        {
            int eq = value.IndexOf('=');
            return _labels.TryGetValue(eq > 0 ? value.Substring(eq + 1) : value, out var name) ? $"{value} ({name})" : value;
        }

        /// <summary>Console-only words besides the ops.</summary>
        public static readonly string[] ConsoleWords = { "help", "ops", "spots", "list", "status", "stop", "reset", "clear", "scenario" };

        public static string ArgKind(string op, int index)
        {
            if (!ScenarioOps.TryGet(op, out var spec)) return op == "scenario" ? "scenario" : op == "help" ? "op" : op == "spots" ? "spot" : null;
            switch (spec.Name)
            {
                case "give": case "equip": return index == 0 ? "item" : null;
                case "spell": case "cast": case "teach": return index == 0 ? "spell" : "npc";
                case "learn": return "skill";
                case "quest": return index == 0 ? "quest" : "queststatus";
                case "weather": return "weather";
                case "docs": return "dockind";
                case "tp": case "look": return "target";
                case "scenario": return "scenario";
                case "spawn": case "bring": case "talk": case "ko": case "kill": case "hp": case "probe": case "focus":
                case "engage": case "party": case "despawn": case "fight": case "finisher": case "restore": case "disengage": case "strayhit":
                    return index == 0 ? "npc" : null;
                default: return null;
            }
        }

        /// <summary>Completions for the last word of <paramref name="line"/> (whole replacement words, ranked).</summary>
        public List<string> Complete(string line, int max = 30)
        {
            line = line ?? "";
            int start = Math.Max(line.LastIndexOf(';'), -1) + 1;
            string statement = line.Substring(start).TrimStart();
            var words = statement.Split(' ');
            string last = words[words.Length - 1];
            if (words.Length == 1)
            {
                var ops = ScenarioOps.All.Select(o => o.Name).Concat(ConsoleWords).Concat(ScenarioOps.AliasNames);
                return ops.Where(o => o.StartsWith(last, StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(o => o, StringComparer.Ordinal).Take(max).ToList();
            }
            int eq = last.IndexOf('=');
            string prefix = "";
            if (eq > 0) { prefix = last.Substring(0, eq + 1); last = last.Substring(eq + 1); }
            string kind = eq > 0 ? NamedKind(prefix.TrimEnd('=')) : ArgKind(words[0], words.Length - 2);
            _labels.Clear();
            if (kind == null || Values == null) return new List<string>();
            if (kind == "dockind") return DocumentsPage.GiveKinds.Where(s => s.StartsWith(last, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kind == "queststatus") return new[] { "Running", "Success", "Failed", "Obsolete", "Open" }.Where(s => s.StartsWith(last, StringComparison.OrdinalIgnoreCase)).ToList();
            var list = Values(kind) ?? Enumerable.Empty<string>();
            if (last.Length == 0) return list.OrderBy(x => x, StringComparer.Ordinal).Take(max).ToList();
            var names = Names?.Invoke(kind)?.Where(n => !string.IsNullOrEmpty(n.name)).ToList() ?? new List<(string id, string name)>();
            var ranked = DevLookup.Rank(list.Select(x => (x, (string)null)).Concat(names), last, max);
            foreach (var (id, name) in names)
            {
                if (!ranked.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                bool hit = name.IndexOf(last, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!_labels.TryGetValue(id, out var had) || (hit && had.IndexOf(last, StringComparison.OrdinalIgnoreCase) < 0)) _labels[id] = name;
            }
            return ranked.Select(x => prefix + x).ToList();
        }

        static string NamedKind(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "npc": case "near": case "look": case "target": case "except": case "only": return "npc";
                case "spot": return "spot";
                default: return null;
            }
        }

        /// <summary>
        /// <paramref name="line"/> with its last word replaced by the completion <paramref name="match"/> (a named argument
        /// keeps its "name=" part).
        /// </summary>
        public static string Replace(string line, string match)
        {
            line = line ?? "";
            int cut = line.LastIndexOf(' ') + 1;
            string word = line.Substring(cut);
            int eq = word.IndexOf('=');
            if (eq > 0 && !match.StartsWith(word.Substring(0, eq + 1), StringComparison.OrdinalIgnoreCase)) match = word.Substring(0, eq + 1) + match;
            return line.Substring(0, cut) + match;
        }

        public static string CommonPrefix(IReadOnlyList<string> items)
        {
            if (items == null || items.Count == 0) return "";
            string p = items[0];
            foreach (var s in items)
            {
                int n = 0;
                while (n < p.Length && n < s.Length && char.ToLowerInvariant(p[n]) == char.ToLowerInvariant(s[n])) n++;
                p = p.Substring(0, n);
            }
            return p;
        }
    }
}
#endif
