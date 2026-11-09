using System.Collections.Generic;
using G3.Weather;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// fix-249: small still water (fountain basins, water barrels) gets the <see cref="SmallWaterLook"/> variant of its
    /// G3/Water material when a scene (a streamed cell) loads; see <see cref="SmallWaterLook"/> for why the shared river
    /// material is invisible over a few cm of water. Runs for the original look and the remaster alike (the remaster's
    /// material map never holds water, so its swaps and these do not meet). One variant per source material, made on
    /// first use; renderers that already carry a variant are skipped. Cost: one pass over the scene's mesh renderers'
    /// shared-material lists (reused lists, no allocation) per loaded scene.
    /// </summary>
    public static class G3SmallWater
    {
        public const string WaterShaderName = "G3/Water";
        const string VariantSuffix = " (small body)";

        static Shader _water;
        static readonly Dictionary<Material, Material> Variants = new Dictionary<Material, Material>();
        static readonly HashSet<Material> Made = new HashSet<Material>();
        static readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
        static readonly List<Material> Mats = new List<Material>();
        static int _swapped;
        static bool _logged;

        static readonly global::Unity.Profiling.ProfilerMarker Marker = new global::Unity.Profiling.ProfilerMarker("G3SmallWater.SceneLoaded");

        /// <summary>Slots swapped since start (runner / log).</summary>
        public static int Swapped => _swapped;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Variants.Clear();
            Made.Clear();
            _water = null;
            _swapped = 0;
            _logged = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) Apply(SceneManager.GetSceneAt(i));
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            using var _ = Marker.Auto();
            Apply(scene);
        }

        /// <summary>Swaps the water slots of the small water renderers in <paramref name="scene"/>; returns the count.</summary>
        public static int Apply(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return 0;
            if (_water == null) _water = Shader.Find(WaterShaderName);
            if (_water == null) return 0;
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, Renderers);
                foreach (var r in Renderers)
                {
                    r.GetSharedMaterials(Mats);
                    int k = 0;
                    for (int i = 0; i < Mats.Count; i++)
                    {
                        var m = Mats[i];
                        if (m == null || m.shader != _water || Made.Contains(m)) continue;
                        if (k == 0)
                        {
                            var size = r.bounds.size;
                            if (!SmallWaterLook.IsSmallBody(size.x, size.z)) break;
                        }
                        Mats[i] = VariantOf(m);
                        k++;
                    }
                    if (k == 0) continue;
                    r.SetSharedMaterials(Mats);
                    n += k;
                }
            }
            Renderers.Clear();
            Mats.Clear();
            _swapped += n;
            if (n > 0 && !_logged)
            {
                _logged = true;
                Debug.Log($"[G3SmallWater] fix-249: small water bodies (≤ {SmallWaterLook.MaxExtent} m) use the shallow-water variant ({n} slot(s) in {scene.name}; further scenes quietly)");
            }
            return n;
        }

        static Material VariantOf(Material source)
        {
            if (Variants.TryGetValue(source, out var v) && v != null) return v;
            v = new Material(source) { name = source.name + VariantSuffix, hideFlags = HideFlags.DontSave };
            v.SetFloat("_ShoreFade", SmallWaterLook.ShoreFade);
            var a = SmallWaterLook.Absorption;
            v.SetVector("_Absorption", new Vector4(a[0], a[1], a[2], 0f));
            var s = SmallWaterLook.ScatterColor;
            v.SetColor("_ScatterColor", new Color(s[0], s[1], s[2], 1f));
            v.SetFloat("_WorldUV", 1f);
            v.SetFloat("_TilingA", SmallWaterLook.TilingA);
            v.SetFloat("_TilingB", SmallWaterLook.TilingB);
            v.SetFloat("_NormalStrength", SmallWaterLook.NormalStrength);
            var fa = SmallWaterLook.FlowA; var fb = SmallWaterLook.FlowB;
            v.SetVector("_FlowA", new Vector4(fa[0], fa[1], fa[2], fa[3]));
            v.SetVector("_FlowB", new Vector4(fb[0], fb[1], fb[2], fb[3]));
            Variants[source] = v;
            Made.Add(v);
            return v;
        }
    }
}
