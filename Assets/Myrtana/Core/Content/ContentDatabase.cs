using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    public sealed class ContentDatabase : IContentDatabase
    {
        readonly Dictionary<Type, Dictionary<string, IDefinition>> _tables = new Dictionary<Type, Dictionary<string, IDefinition>>();
        readonly List<ContentPackManifest> _packs = new List<ContentPackManifest>();

        public IReadOnlyList<ContentPackManifest> Packs => _packs;

        public bool TryGet<T>(string key, out T definition) where T : class, IDefinition
        {
            definition = null;
            if (!_tables.TryGetValue(typeof(T), out var table) || !table.TryGetValue(key, out var d)) return false;
            definition = (T)d;
            return true;
        }

        public IReadOnlyCollection<T> All<T>() where T : class, IDefinition
        {
            if (!_tables.TryGetValue(typeof(T), out var table)) return Array.Empty<T>();
            var list = new List<T>(table.Count);
            foreach (var d in table.Values) list.Add((T)d);
            return list;
        }

        public void AddPack(ContentPackManifest manifest) => _packs.Add(manifest);

        /// <summary>Adds or replaces a definition. Returns true if one with the same key was replaced.</summary>
        public bool Put(Type kind, IDefinition definition)
        {
            var table = Table(kind);
            bool replaced = table.ContainsKey(definition.Key);
            table[definition.Key] = definition;
            return replaced;
        }

        public void Put<T>(T definition) where T : class, IDefinition => Put(typeof(T), definition);

        public bool Remove(Type kind, string key) => Table(kind).Remove(key);

        Dictionary<string, IDefinition> Table(Type kind)
        {
            if (!_tables.TryGetValue(kind, out var table)) _tables[kind] = table = new Dictionary<string, IDefinition>(StringComparer.Ordinal);
            return table;
        }
    }
}
