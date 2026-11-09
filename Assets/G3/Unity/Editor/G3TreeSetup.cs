using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>Creates the tree catalog and simple placeholder trees (1 m tall, pivot at the base) per category.</summary>
    public static class G3TreeSetup
    {
        public const string Folder = "Assets/G3/Trees";
        public const string CatalogPath = Folder + "/TreeCatalog.asset";

        public static TreeCatalog GetOrCreateCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<TreeCatalog>(CatalogPath);
            if (catalog != null) return catalog;
            Directory.CreateDirectory(Folder + "/Placeholders");
            catalog = ScriptableObject.CreateInstance<TreeCatalog>();
            foreach (TreeCategory cat in System.Enum.GetValues(typeof(TreeCategory)))
                catalog.categoryDefaults.Add(new TreeCatalog.CategoryDefault
                {
                    category = cat,
                    prefabs = new[] { Placeholder(cat, false) },
                    winterPrefabs = new[] { Placeholder(cat, true) },
                });
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            AssetDatabase.SaveAssets();
            return catalog;
        }

        static GameObject Placeholder(TreeCategory cat, bool winter)
        {
            string path = $"{Folder}/Placeholders/Placeholder_{cat}{(winter ? "_Winter" : "")}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Material Mat(string name, Color c)
            {
                string mp = $"{Folder}/Placeholders/{name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (m != null) return m;
                m = new Material(lit) { color = c };
                m.SetFloat("_Smoothness", 0.05f);
                AssetDatabase.CreateAsset(m, mp);
                return m;
            }
            var bark = Mat("Bark", new Color(0.30f, 0.22f, 0.15f));
            var leaves = Mat(winter ? "LeavesWinter" : "Leaves" + cat, winter ? new Color(0.85f, 0.88f, 0.9f) :
                cat == TreeCategory.Conifer ? new Color(0.12f, 0.25f, 0.13f) :
                cat == TreeCategory.Desert ? new Color(0.45f, 0.45f, 0.25f) :
                cat == TreeCategory.Palm ? new Color(0.25f, 0.4f, 0.15f) : new Color(0.2f, 0.35f, 0.15f));

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            void Part(PrimitiveType type, Material m, Vector3 pos, Vector3 scale)
            {
                var p = GameObject.CreatePrimitive(type);
                Object.DestroyImmediate(p.GetComponent<Collider>());
                p.transform.SetParent(root.transform, false);
                p.transform.localPosition = pos;
                p.transform.localScale = scale;
                p.GetComponent<MeshRenderer>().sharedMaterial = m;
            }
            switch (cat)
            {
                case TreeCategory.Conifer:
                    Part(PrimitiveType.Cylinder, bark, new Vector3(0, 0.15f, 0), new Vector3(0.05f, 0.15f, 0.05f));
                    Part(PrimitiveType.Capsule, leaves, new Vector3(0, 0.6f, 0), new Vector3(0.3f, 0.4f, 0.3f));
                    break;
                case TreeCategory.Palm:
                    Part(PrimitiveType.Cylinder, bark, new Vector3(0, 0.42f, 0), new Vector3(0.04f, 0.42f, 0.04f));
                    Part(PrimitiveType.Sphere, leaves, new Vector3(0, 0.9f, 0), new Vector3(0.45f, 0.15f, 0.45f));
                    break;
                case TreeCategory.Bush:
                    Part(PrimitiveType.Sphere, leaves, new Vector3(0, 0.5f, 0), new Vector3(1f, 1f, 1f));
                    break;
                default: // broadleaf / desert: trunk + wide crown
                    Part(PrimitiveType.Cylinder, bark, new Vector3(0, 0.25f, 0), new Vector3(0.07f, 0.25f, 0.07f));
                    Part(PrimitiveType.Sphere, leaves, new Vector3(0, 0.7f, 0),
                        cat == TreeCategory.Desert ? new Vector3(0.9f, 0.3f, 0.9f) : new Vector3(0.6f, 0.6f, 0.6f));
                    break;
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static readonly Dictionary<GameObject, float> HeightCache = new Dictionary<GameObject, float>();

        /// <summary>Height of a prefab's renderers in metres (used to match the original tree's height).</summary>
        public static float PrefabHeight(GameObject prefab)
        {
            if (HeightCache.TryGetValue(prefab, out var h)) return h;
            var b = new Bounds();
            bool first = true;
            foreach (var r in prefab.GetComponentsInChildren<Renderer>())
            {
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            h = first ? 1f : Mathf.Max(0.01f, b.max.y - Mathf.Min(0, b.min.y));
            HeightCache[prefab] = h;
            return h;
        }
    }
}
