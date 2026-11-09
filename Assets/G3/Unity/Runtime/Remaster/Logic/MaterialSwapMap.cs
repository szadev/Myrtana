using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Original → remaster material table of the runtime character remaster (Docs/remaster-characters.md §6.3): every
    /// character renderer the set has no mesh swap for gets its materials mapped slot by slot (NPCs, orcs, creatures,
    /// items in hand), and mapped back when the remaster is switched off.
    /// Pairs are names only (original material name → the remaster's Resources path) and a remaster is loaded the first
    /// time a renderer needs it: the set itself references no material or texture, so only the looks actually on screen
    /// are in memory (all ~130 texture sets at once would be gigabytes). The way back is remembered per loaded instance
    /// (remaster → the original instance it replaced), so a renderer whose materials are reassigned later (a hand prop
    /// showing another item) still maps both ways. Generic so the rules are testable without Unity (T = Material).
    /// </summary>
    public sealed class MaterialSwapMap<T> where T : class
    {
        readonly Func<T, string> _name;
        readonly Func<string, T> _load;
        readonly Dictionary<string, string> _pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly HashSet<string> _remasterKeys = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, T> _loaded = new Dictionary<string, T>(StringComparer.Ordinal);
        readonly Dictionary<T, T> _back = new Dictionary<T, T>();

        public MaterialSwapMap(Func<T, string> name, Func<string, T> load)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _load = load ?? throw new ArgumentNullException(nameof(load));
        }

        /// <summary>Pairs in the table.</summary>
        public int Count => _pairs.Count;

        /// <summary>Remasters loaded so far (the looks that were on screen).</summary>
        public int Loaded
        {
            get { int n = 0; foreach (var v in _loaded.Values) if (v != null) n++; return n; }
        }

        /// <summary>Adds a pair; false (ignored) for empty names, identity pairs, an original already mapped, or a remaster
        /// another original already uses (the way back must be unique).</summary>
        public bool Add(string original, string remaster)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(remaster) || original == remaster) return false;
            if (_pairs.ContainsKey(original) || _remasterKeys.Contains(remaster)) return false;
            _pairs[original] = remaster;
            _remasterKeys.Add(remaster);
            return true;
        }

        /// <summary>perf-5: the remaster key (load path) for an original material name, without loading it; false = not in the
        /// table or already loaded.</summary>
        public bool TryPendingKey(string originalName, out string key)
        {
            key = null;
            if (originalName == null || !_pairs.TryGetValue(originalName, out key)) return false;
            return !_loaded.ContainsKey(key);
        }

        public bool Covers(T m) => m != null && _pairs.ContainsKey(_name(m) ?? "");

        public bool IsRemaster(T m) => m != null && _back.ContainsKey(m);

        /// <summary>The remaster of <paramref name="m"/> (loaded on first use), or <paramref name="m"/> itself: not in the
        /// table, already a remaster, the remaster failed to load, or the remaster already stands for another instance of
        /// the same name (the way back would be ambiguous).</summary>
        public T Forward(T m)
        {
            if (m == null || _back.ContainsKey(m)) return m;
            if (!_pairs.TryGetValue(_name(m) ?? "", out var key)) return m;
            if (!_loaded.TryGetValue(key, out var r))
            {
                r = _load(key);
                _loaded[key] = r;
            }
            if (r == null || ReferenceEquals(r, m)) return m;
            if (_back.TryGetValue(r, out var prev)) return ReferenceEquals(prev, m) ? r : m;
            _back[r] = m;
            return r;
        }

        /// <summary>The original a loaded remaster replaced, else <paramref name="m"/>.</summary>
        public T Back(T m) => m != null && _back.TryGetValue(m, out var o) ? o : m;

        /// <summary>The array mapped towards <paramref name="on"/>; the same instance when nothing changes (no allocation
        /// for renderers the table does not cover).</summary>
        public T[] Map(T[] materials, bool on)
        {
            if (materials == null) return null;
            T[] result = null;
            for (int i = 0; i < materials.Length; i++)
            {
                var m = materials[i];
                var n = on ? Forward(m) : Back(m);
                if (ReferenceEquals(n, m)) continue;
                if (result == null) result = (T[])materials.Clone();
                result[i] = n;
            }
            return result ?? materials;
        }

        /// <summary>Forgets the loaded remasters (call after every renderer was mapped back, then unload unused assets).</summary>
        public void ReleaseLoaded()
        {
            _loaded.Clear();
            _back.Clear();
        }
    }
}
