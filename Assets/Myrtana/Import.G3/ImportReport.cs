using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// What the import could not translate or found broken: unsupported commands and conditions (counted per name),
    /// missing texts and voices, dangling references. Nothing is dropped silently.
    /// </summary>
    public sealed class ImportReport
    {
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> _examples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        readonly List<string> _notes = new List<string>();

        /// <summary>Counts one occurrence of an issue category, remembering a few examples.</summary>
        public void Count(string category, string example = null)
        {
            _counts[category] = _counts.TryGetValue(category, out var n) ? n + 1 : 1;
            if (example == null) return;
            if (!_examples.TryGetValue(category, out var list)) _examples[category] = list = new List<string>();
            if (list.Count < 3) list.Add(example);
        }

        public void Note(string line) => _notes.Add(line);

        public int CountOf(string category) => _counts.TryGetValue(category, out var n) ? n : 0;

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var n in _notes) sb.AppendLine(n);
            foreach (var kv in _counts.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append($"{kv.Value,7}  {kv.Key}");
                if (_examples.TryGetValue(kv.Key, out var ex)) sb.Append("   e.g. ").Append(string.Join(", ", ex));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
