using System;
using System.Collections.Generic;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Canonical spelling of item and skill ids for the dialog importer (G3 scripts write names case-insensitively,
    /// the content compares ordinally).
    /// </summary>
    public sealed class ItemNames
    {
        readonly Dictionary<string, string> _items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _skills = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ItemNames(IEnumerable<string> items, IEnumerable<string> skills)
        {
            foreach (var i in items) _items[i] = i;
            foreach (var s in skills) _skills[s] = s;
        }

        public static readonly ItemNames None = new ItemNames(Array.Empty<string>(), Array.Empty<string>());

        /// <summary>Null if no such item.</summary>
        public string Item(string name) => !string.IsNullOrEmpty(name) && _items.TryGetValue(name, out var id) ? id : null;

        /// <summary>Null if no such skill.</summary>
        public string Skill(string name) => !string.IsNullOrEmpty(name) && _skills.TryGetValue(name, out var id) ? id : null;
    }
}
