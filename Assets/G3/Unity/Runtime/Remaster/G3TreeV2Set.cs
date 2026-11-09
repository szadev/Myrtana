using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The world remaster v2 replacement trees (Docs/remaster-world.md 10.15): one prefab per species variant (LODGroup:
    /// LOD0-2 + impostor, G3/SpeedTree materials, no colliders: the G3 tree keeps its trunk capsule), written by the
    /// editor's <c>G3TreeV2Builder</c> (menu G3/Remaster/World/V2/Build Trees) and referenced from the remaster set
    /// (<see cref="G3WorldRemasterSet.treeV2"/>). Read by <see cref="G3TreeV2"/>.
    /// </summary>
    public sealed class G3TreeV2Set : ScriptableObject
    {
        [Serializable]
        public struct Model
        {
            public string species;
            public int variant;
            public GameObject prefab;
            public float height;      // authored model height (m)
            public float halfWidth;   // max horizontal extent from the trunk axis (m)
        }

        public List<Model> models = new List<Model>();

        public int VariantCount(string species)
        {
            int n = 0;
            foreach (var m in models) if (m.species == species && m.prefab != null) n = Math.Max(n, m.variant + 1);
            return n;
        }

        public bool TryGet(string species, int variant, out Model model)
        {
            foreach (var m in models)
                if (m.species == species && m.variant == variant && m.prefab != null) { model = m; return true; }
            model = default;
            return false;
        }
    }
}
