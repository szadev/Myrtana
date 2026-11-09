using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Lookup of the runtime character set's mesh swaps by what a renderer shows (Docs/remaster-characters.md §6.6): the
    /// original mesh's name and vertex count (names alone are not unique over all imported files), skinned or not, and
    /// preferably the same material names (another MaterialSwitch of the same body still finds the entry: the caller then
    /// maps that renderer's own materials). The set holds names, not references, so matching never loads a mesh.
    /// </summary>
    public sealed class CharacterSwapIndex
    {
        sealed class Entry
        {
            public int index;
            public string[] materials;
        }

        readonly Dictionary<(string, int, bool), List<Entry>> _byMesh = new Dictionary<(string, int, bool), List<Entry>>();

        public int Count { get; private set; }

        /// <summary>Adds swap <paramref name="index"/>; <paramref name="materials"/> empty = any materials. Entries keep their order.</summary>
        public void Add(int index, string mesh, int vertices, bool skinned, IReadOnlyList<string> materials)
        {
            if (string.IsNullOrEmpty(mesh)) return;
            var key = (mesh, vertices, skinned);
            if (!_byMesh.TryGetValue(key, out var list)) _byMesh[key] = list = new List<Entry>();
            var m = new string[materials?.Count ?? 0];
            for (int i = 0; i < m.Length; i++) m[i] = materials[i];
            list.Add(new Entry { index = index, materials = m });
            Count++;
        }

        /// <summary>The swap for a renderer, or -1: the first entry with the same materials (<paramref name="exact"/>), else
        /// the first with the same mesh.</summary>
        public int Find(string mesh, int vertices, bool skinned, IReadOnlyList<string> materials, out bool exact)
        {
            exact = false;
            if (string.IsNullOrEmpty(mesh) || !_byMesh.TryGetValue((mesh, vertices, skinned), out var list)) return -1;
            foreach (var e in list)
                if (Same(e.materials, materials)) { exact = true; return e.index; }
            return list[0].index;
        }

        static bool Same(string[] a, IReadOnlyList<string> b)
        {
            if (a.Length == 0) return true;
            if (b == null || a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    /// <summary>
    /// Assets loaded on first use by path and kept until <see cref="Clear"/> (the character remaster's swap meshes and
    /// materials: only what a character on screen needs is in memory; switching the remaster off clears the cache and
    /// unloads them). A failed load is remembered too (no retry per renderer). Generic so it is testable without Unity.
    /// </summary>
    public sealed class LazyAssetCache<T> where T : class
    {
        readonly Func<string, T> _load;
        readonly Dictionary<string, T> _byPath = new Dictionary<string, T>(StringComparer.Ordinal);
        readonly HashSet<T> _loaded = new HashSet<T>();

        public LazyAssetCache(Func<string, T> load) { _load = load ?? throw new ArgumentNullException(nameof(load)); }

        /// <summary>Assets loaded so far (failed loads not counted).</summary>
        public int Loaded => _loaded.Count;

        /// <summary>The asset at <paramref name="path"/> (null for an empty path or a failed load).</summary>
        public T Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_byPath.TryGetValue(path, out var a)) return a;
            a = _load(path);
            _byPath[path] = a;
            if (a != null) _loaded.Add(a);
            return a;
        }

        /// <summary>All of <paramref name="paths"/> (null entries where a load failed); null when any is missing and
        /// <paramref name="all"/> is set.</summary>
        public T[] GetAll(IReadOnlyList<string> paths, bool all = true)
        {
            var r = new T[paths?.Count ?? 0];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = Get(paths[i]);
                if (r[i] == null && all) return null;
            }
            return r;
        }

        /// <summary>True for an asset this cache loaded.</summary>
        public bool Contains(T asset) => asset != null && _loaded.Contains(asset);

        /// <summary>Forgets everything (call after no renderer uses the assets any more, then unload unused assets).</summary>
        public void Clear()
        {
            _byPath.Clear();
            _loaded.Clear();
        }
    }
}
