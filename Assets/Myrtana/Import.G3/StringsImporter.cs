using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Story;
using Myrtana.Core;
using StringTable = G3.Formats.Story.StringTable;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Splits the multi-language G3 string table into one <see cref="StringTableFile"/> per language and resolves G3's
    /// case-insensitive ids to the canonical spelling, which content then uses with ordinal comparison.
    /// </summary>
    public sealed class StringsImporter
    {
        readonly StringTable _table;

        public StringsImporter(StringTable table) => _table = table;

        public IEnumerable<string> Languages => _table.Languages;

        /// <summary>The canonical text id, or an empty id if the table does not know it.</summary>
        public TextId Resolve(string id) =>
            !string.IsNullOrEmpty(id) && _table.Entries.TryGetValue(id, out var e) ? new TextId(e.Id) : default;

        public bool Exists(string id) => !string.IsNullOrEmpty(id) && _table.Entries.ContainsKey(id);

        /// <summary>The table of one language (only non-empty texts), or null if the language has no text at all.</summary>
        public StringTableFile Export(string language)
        {
            int column = _table.LanguageIndex(language);
            if (column < 0) return null;
            var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var stages = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in _table.Entries.Values)
            {
                if (column < e.Text.Length && !string.IsNullOrEmpty(e.Text[column])) texts[e.Id] = e.Text[column];
                if (column < e.Stage.Length && !string.IsNullOrEmpty(e.Stage[column])) stages[e.Id] = e.Stage[column];
            }
            return texts.Count == 0 ? null : new StringTableFile(texts, stages);
        }

        public int TextCount(string language)
        {
            int column = _table.LanguageIndex(language);
            return column < 0 ? 0 : _table.Entries.Values.Count(e => column < e.Text.Length && !string.IsNullOrEmpty(e.Text[column]));
        }
    }
}
