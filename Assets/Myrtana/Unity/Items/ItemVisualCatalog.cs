using System;
using System.Collections.Generic;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Unity assets of the items' visual keys (<see cref="Rules.ItemVisual"/>): static meshes for
    /// items lying in the world and skinned bodies for worn armour (inventory icons come from the G3 GUI skin). Built from the game files by
    /// the editor menu Myrtana/Content/Build item visuals into Resources (generated, git-ignored).
    /// </summary>
    public sealed class ItemVisualCatalog : ScriptableObject
    {
        public const string ResourcePath = "Myrtana/ItemVisuals";

        [Serializable]
        public sealed class MeshEntry
        {
            public string key;
            public Mesh mesh;
            public Material[] materials;
        }

        /// <summary>One skinned mesh of an armour body actor (e.g. GFG_Hero_Body_King: the body and its fur collar).</summary>
        [Serializable]
        public sealed class BodyPart
        {
            public Mesh mesh;
            public Material[] materials;
            /// <summary>Bone names in the order of the mesh's bind poses.</summary>
            public string[] bones;
            public string rootBone;
        }

        [Serializable]
        public sealed class BodyEntry
        {
            public string key;
            /// <summary>Every skinned mesh of the body actor; the first is the main body.</summary>
            public List<BodyPart> parts = new List<BodyPart>();
        }

        public List<MeshEntry> meshes = new List<MeshEntry>();
        public List<BodyEntry> bodies = new List<BodyEntry>();

        Dictionary<string, MeshEntry> _meshes;
        Dictionary<string, BodyEntry> _bodies;

        public static ItemVisualCatalog Load() => Resources.Load<ItemVisualCatalog>(ResourcePath);

        public static string BodyKey(string body, int materialSwitch) => materialSwitch != 0 ? body + "_S" + materialSwitch : body;

        public bool TryGetMesh(string key, out MeshEntry entry) => Index(ref _meshes, meshes, e => e.key).TryGetValue(key ?? "", out entry);

        public bool TryGetBody(string key, out BodyEntry entry) => Index(ref _bodies, bodies, e => e.key).TryGetValue(key ?? "", out entry);

        static Dictionary<string, T> Index<T>(ref Dictionary<string, T> cache, List<T> list, Func<T, string> key)
        {
            if (cache != null) return cache;
            cache = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list) cache[key(e)] = e;
            return cache;
        }
    }
}
