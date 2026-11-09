using System;
using System.Collections.Generic;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Prefabs of the animated interactive objects (chests, doors, grindstones … — G3 <c>.FXA</c> actors, which the
    /// static world import leaves out), keyed by the actor name without extension. Built by the editor menu
    /// Myrtana/Content/Build interactive visuals into Resources (generated, git-ignored).
    /// </summary>
    public sealed class InteractiveVisualCatalog : ScriptableObject
    {
        public const string ResourcePath = "Myrtana/InteractiveVisuals";

        [Serializable]
        public sealed class Entry
        {
            public string key;
            public GameObject prefab;
        }

        public List<Entry> entries = new List<Entry>();

        Dictionary<string, GameObject> _byKey;

        public static InteractiveVisualCatalog Load() => Resources.Load<InteractiveVisualCatalog>(ResourcePath);

        /// <summary>Key of a visual resource name: the file name without extension.</summary>
        public static string KeyOf(string visual) => string.IsNullOrEmpty(visual) ? null : System.IO.Path.GetFileNameWithoutExtension(visual);

        public bool TryGet(string visual, out GameObject prefab)
        {
            if (_byKey == null || _byKey.Count != entries.Count)
            {
                _byKey = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries) if (e?.prefab != null) _byKey[e.key] = e.prefab;
            }
            prefab = null;
            string key = KeyOf(visual);
            return key != null && _byKey.TryGetValue(key, out prefab);
        }
    }
}
