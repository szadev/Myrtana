using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    public enum TreeCategory { Conifer, Broadleaf, Palm, Desert, Bush }

    /// <summary>
    /// Maps original SpeedTree file names (e.g. "G3_Tree_M_DouglasFir_02.spt") to replacement prefabs.
    /// Lookup order: exact file name → species ("DouglasFir", with "_winter" variant) → category default.
    /// Replacement prefabs are scaled so their height matches the original tree's world bounds.
    /// </summary>
    [CreateAssetMenu(menuName = "G3/Tree Catalog", fileName = "TreeCatalog")]
    public sealed class TreeCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("Exact .spt file name or species name (e.g. DouglasFir). Add _winter for snowy variants.")]
            public string key;
            public GameObject[] prefabs;   // random pick per instance (stable by position)
        }

        [Serializable]
        public sealed class CategoryDefault
        {
            public TreeCategory category;
            public GameObject[] prefabs;
            public GameObject[] winterPrefabs;
        }

        public List<Entry> entries = new List<Entry>();
        public List<CategoryDefault> categoryDefaults = new List<CategoryDefault>();

        static readonly string[] Conifers = { "DouglasFir", "FraserFir", "LongleafPine", "ItalianCypress", "MontereyCypress" };
        static readonly string[] Palms = { "CoconutPalm", "FanPalm", "CurlyPalm", "DatePalm" };
        static readonly string[] Desert = { "UmbrellaThorn", "JoshuaTree", "Gimlet", "GhostGum", "RedGum", "Tamarind" };

        /// <summary>"G3_Tree_M_DouglasFir_02_winter.spt" → species "DouglasFir", winter true.</summary>
        public static string Species(string spt, out bool winter)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(spt ?? "");
            winter = name.EndsWith("_winter", StringComparison.OrdinalIgnoreCase);
            if (name.StartsWith("G3_Tree_bush", StringComparison.OrdinalIgnoreCase)) return "Bush";
            var parts = name.Split('_');
            // G3 _ Tree _ <size> _ <Species> _ <nn> [_winter]  |  G3 _ Speedtree _ Broadleaf _ 05 ...
            if (parts.Length >= 4 && parts[1].Equals("Tree", StringComparison.OrdinalIgnoreCase)) return parts[3];
            if (parts.Length >= 3) return parts[2];
            return name;
        }

        public static TreeCategory Category(string species)
        {
            if (species == "Bush") return TreeCategory.Bush;
            if (Array.IndexOf(Conifers, species) >= 0) return TreeCategory.Conifer;
            if (Array.IndexOf(Palms, species) >= 0) return TreeCategory.Palm;
            if (Array.IndexOf(Desert, species) >= 0) return TreeCategory.Desert;
            return TreeCategory.Broadleaf;
        }

        public GameObject Resolve(string spt, int seed)
        {
            var file = System.IO.Path.GetFileName(spt ?? "");
            var species = Species(file, out bool winter);
            GameObject[] pick = Find(file) ?? (winter ? Find(species + "_winter") : null) ?? Find(species);
            if (pick == null)
            {
                var cat = Category(species);
                var d = categoryDefaults.Find(c => c.category == cat);
                if (d != null) pick = winter && d.winterPrefabs != null && d.winterPrefabs.Length > 0 ? d.winterPrefabs : d.prefabs;
            }
            if (pick == null || pick.Length == 0) return null;
            return pick[(int)((uint)seed % (uint)pick.Length)];
        }

        GameObject[] Find(string key)
        {
            foreach (var e in entries)
                if (e.prefabs != null && e.prefabs.Length > 0 && string.Equals(e.key, key, StringComparison.OrdinalIgnoreCase))
                    return e.prefabs;
            return null;
        }
    }
}
