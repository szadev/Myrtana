using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Myrtana.Core
{
    /// <summary>Supplies the merged string table of a language, or null if no pack has one.</summary>
    public interface IStringTableSource
    {
        StringTable Load(string language);
    }

    /// <summary>
    /// Reads <c>strings/&lt;Language&gt;.json</c> from every pack (in load order) the first time a language is asked
    /// for, so only the selected language and the fallback are ever parsed.
    /// </summary>
    public sealed class PackStringTables : IStringTableSource
    {
        readonly IReadOnlyList<IContentSource> _packs;
        readonly JsonSerializerOptions _json;
        readonly Dictionary<string, StringTable> _cache = new Dictionary<string, StringTable>(StringComparer.OrdinalIgnoreCase);

        public PackStringTables(IReadOnlyList<IContentSource> packs, ContentSchema schema)
        {
            _packs = packs;
            _json = ContentJson.CreateOptions(schema);
        }

        public StringTable Load(string language)
        {
            if (string.IsNullOrEmpty(language)) return null;
            if (_cache.TryGetValue(language, out var cached)) return cached;
            StringTable table = null;
            string path = StringTableFile.PathFor(language);
            foreach (var pack in _packs)
            {
                if (!pack.Exists(path)) continue;
                using (var s = pack.Open(path))
                {
                    table = table ?? new StringTable(language);
                    table.Overlay(JsonSerializer.Deserialize<StringTableFile>(s, _json));
                }
            }
            _cache[language] = table;
            return table;
        }
    }
}
