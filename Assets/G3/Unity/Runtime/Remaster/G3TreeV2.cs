using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// World remaster v2 tree replacement (Docs/remaster-world.md 10.15). Since trees-v3 (10.17) only for the runner's A/B: the
    /// world v2 layer with <c>"v2TreeReplace": true</c> in G3WorldLook.json (<see cref="RemasterOptions.TreesWanted"/>,
    /// default false). <see cref="Option"/> still stores the Options → Remaster row "Trees" (PlayerPrefs
    /// <c>G3.Remaster.Trees</c>, default off; F7 flips it with the other parts), which now switches G3's own trees between
    /// their original and their remastered (v3) materials in <see cref="G3WorldRemaster"/>. Every G3 SpeedTree instance in the loaded cells (a LODGroup root named after its .spt, e.g.
    /// <c>G3_Tree_M_DouglasFir_02</c>) whose species has a rule in <see cref="TreeV2Plan"/> gets a v2 model from
    /// <see cref="G3TreeV2Set"/> as a child: G3's placement, yaw and scale stay (the child is fitted to the G3 tree's
    /// generated size, <see cref="TreeV2Plan.Fit"/>), the variant is a hash of the position, the G3 root keeps its trunk
    /// capsule (collider parity) while its renderers and LODGroup are switched off, not destroyed. Off restores them.
    /// Trees without a rule (or without a built model) keep their G3 SpeedTree.
    /// </summary>
    public static class G3TreeV2
    {
        sealed class Swap
        {
            public GameObject replacement;
            public List<Renderer> disabled;
            public LODGroup group;
            public string species;
        }

        static readonly Dictionary<GameObject, Swap> _swaps = new Dictionary<GameObject, Swap>();
        static readonly Dictionary<string, int> _perSpecies = new Dictionary<string, int>();
        static bool _enabled;
        static int _kept, _noModel;
        static bool? _option;

        /// <summary>The stored "Trees" world part (env chain <c>G3_REMASTER_TREES</c> / <c>G3_REMASTER_WORLD</c> / ... win,
        /// <see cref="RemasterOptions.EnvChain"/>; the old <c>G3.Remaster.World</c> migrated once, <see cref="G3WorldRemaster.Stored"/>).</summary>
        public static bool Option
        {
            get
            {
                if (_option == null) _option = G3WorldRemaster.Stored(RemasterFeature.Trees);
                return _option.Value;
            }
        }

        /// <summary>True while v2 trees replace G3's in the loaded cells.</summary>
        public static bool Enabled => _enabled;

        /// <summary>True when the build has v2 tree prefabs (G3/Remaster/World/V2/Build Trees ran before the build).</summary>
        public static bool Available
        {
            get
            {
                var set = G3WorldRemaster.Set != null ? G3WorldRemaster.Set.treeV2 : null;
                if (set == null) return false;
                foreach (var m in set.models) if (m.prefab != null) return true;
                return false;
            }
        }

        /// <summary>
        /// Sets the "Trees" option (live unless <paramref name="refresh"/> is false), optionally stores it; returns the number
        /// of trees replaced. The page and F7 go through <see cref="G3WorldRemaster.SetPart"/> / <see cref="G3WorldRemaster.SetAll"/>,
        /// which also swap the SpeedTree materials and the foliage/leaf-rock globals of the trees part.
        /// </summary>
        public static int SetOption(bool on, bool persist = true, bool refresh = true)
        {
            if (persist)
            {
                PlayerPrefs.SetInt(RemasterOptions.PrefKey(RemasterFeature.Trees), on ? 1 : 0);
                PlayerPrefs.Save();
            }
            _option = on;
            if (!refresh) return _swaps.Count;
            Refresh();
            LogState(on);
            return _swaps.Count;
        }

        /// <summary>Logs the <c>[Remaster] trees v2 on|off: N replaced</c> line for the current state (no work; play mode only).
        /// The page row and F7 call it after <see cref="G3WorldRemaster"/> applied the parts.</summary>
        public static void LogState(bool on)
        {
            // trees-v3: the option no longer brings the v2 models; log them only while the JSON A/B has them on
            if (Application.isPlaying && (_enabled || _swaps.Count > 0)) Debug.Log($"[Remaster] trees v2 models (json v2TreeReplace): {Describe()}");
        }

        /// <summary>Applies the wanted state: only the world v2 layer with the JSON switch (trees-v3: not the Trees option).</summary>
        public static void Refresh()
        {
            bool want = Application.isPlaying && RemasterOptions.TreesWanted(G3WorldV2.Active, WorldRemasterLook.Current.v2TreeReplace);
            if (want && !Available)
            {
                if (!_warned) Debug.LogWarning("[G3TreeV2] no v2 tree set in the remaster set (run G3/Remaster/World/V2/Build Trees): G3's trees stay");
                _warned = true;
                want = false;
            }
            SetEnabled(want);
        }
        static bool _warned;

        public static string Describe()
        {
            var parts = new List<string>();
            foreach (var kv in _perSpecies) parts.Add($"{kv.Key} {kv.Value}");
            return $"{_swaps.Count} replaced ({string.Join(", ", parts)}), {_kept} G3 kept" +
                   (_noModel > 0 ? $", {_noModel} without a built model" : "");
        }

        /// <summary>
        /// Low-level switch (use <see cref="Refresh"/> / <see cref="SetOption"/>). The live switch is paced per tree through
        /// <see cref="G3RendererChanges"/> (each tree instantiates a model and switches G3's renderers off: thousands of GPU
        /// Resident Drawer changes in one frame otherwise, Docs/performance.md); a cell that loads is converted at once.
        /// </summary>
        public static void SetEnabled(bool on)
        {
            if (on == _enabled) return;
            _enabled = on;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            PurgeDestroyed();
            _kept = _noModel = 0;
            if (on)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                if (!HasSet()) return;
                var groups = new List<LODGroup>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.IsValid() || !scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        root.GetComponentsInChildren(true, groups);
                        foreach (var g in groups)
                            if (IsTree(g.gameObject) && !_swaps.ContainsKey(g.gameObject)) QueueTree(g.gameObject);
                    }
                }
            }
            else
            {
                foreach (var go in new List<GameObject>(_swaps.Keys)) QueueTree(go);
            }
        }

        static readonly global::Unity.Profiling.ProfilerMarker SceneMarker = new global::Unity.Profiling.ProfilerMarker("G3TreeV2.SceneLoaded");

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            using var _ = SceneMarker.Auto();
            if (_enabled) Apply(scene);
        }

        static bool IsTree(GameObject go) =>
            go.name.StartsWith("G3_Tree", System.StringComparison.OrdinalIgnoreCase) ||
            go.name.StartsWith("G3_Speedtree", System.StringComparison.OrdinalIgnoreCase);

        static G3TreeV2Set TreeSet => G3WorldRemaster.Set != null ? G3WorldRemaster.Set.treeV2 : null;

        static bool HasSet()
        {
            if (TreeSet != null) return true;
            Debug.LogWarning("[G3TreeV2] no v2 tree set in the remaster set (run G3/Remaster/World/V2/Build Trees)");
            return false;
        }

        static void QueueTree(GameObject go) => G3RendererChanges.Enqueue(go, () => Converge(go), "trees v2");

        /// <summary>One tree towards the current state (a paced item); returns the renderers it switched.</summary>
        static int Converge(GameObject go)
        {
            if (go == null) return 0;
            bool swapped = _swaps.ContainsKey(go);
            if (_enabled && !swapped && go.TryGetComponent(out LODGroup g)) return Replace(g, TreeSet);
            if (!_enabled && swapped) return Restore(go);
            return 0;
        }

        /// <summary>A loaded cell at once (its renderers are new to the drawer anyway).</summary>
        static void Apply(Scene scene)
        {
            if (!Application.isPlaying || !scene.IsValid() || !scene.isLoaded) return;
            if (!HasSet()) return;
            var set = TreeSet;
            var groups = new List<LODGroup>();
            foreach (var root in scene.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, groups);
                foreach (var g in groups) Replace(g, set);
            }
        }

        static readonly List<Renderer> _renderers = new List<Renderer>();

        /// <summary>Replaces one G3 tree by its v2 model; returns the renderers switched (0 = kept or not a tree).</summary>
        static int Replace(LODGroup g, G3TreeV2Set set)
        {
            if (g == null || set == null) return 0;
            var go = g.gameObject;
            if (!IsTree(go) || _swaps.ContainsKey(go)) return 0;
            var rule = TreeV2Plan.Resolve(go.name);
            if (rule == null) { _kept++; return 0; }
            var p = go.transform.position;
            int variant = TreeV2Plan.PickVariant(rule, set.VariantCount(rule.Species), p.x, p.z);
            if (variant < 0 || !set.TryGet(rule.Species, variant, out var model)) { _noModel++; return 0; }

            // G3's generated size at scale 1 (the root carries the placement scale); fallback: the LODGroup size
            float refH = g.size, refW = 0;
            if (TreeV2Plan.G3Size.TryGetValue(go.name, out var size)) { refH = size.height; refW = size.width; }
            var (xz, y) = TreeV2Plan.Fit(refH, refW, model.height, model.halfWidth);

            var inst = Object.Instantiate(model.prefab, go.transform, false);
            inst.name = "v2 " + model.prefab.name;
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = new Vector3(xz, y, xz);

            go.GetComponentsInChildren(false, _renderers);
            var off = new List<Renderer>();
            int added = 0;
            foreach (var r in _renderers)
            {
                if (r.transform.IsChildOf(inst.transform)) { added++; continue; }
                if (!r.enabled) continue;
                r.enabled = false;
                off.Add(r);
            }
            _renderers.Clear();
            g.enabled = false;
            _swaps[go] = new Swap { replacement = inst, disabled = off, group = g, species = rule.Species };
            _perSpecies.TryGetValue(rule.Species, out int n);
            _perSpecies[rule.Species] = n + 1;
            return off.Count + added;
        }

        /// <summary>Puts one G3 tree back; returns the renderers switched.</summary>
        static int Restore(GameObject go)
        {
            if (!_swaps.TryGetValue(go, out var s)) return 0;
            _swaps.Remove(go);
            int n = 0;
            if (s.replacement != null)
            {
                s.replacement.GetComponentsInChildren(false, _renderers);
                n += _renderers.Count;
                _renderers.Clear();
                Object.Destroy(s.replacement);
            }
            foreach (var r in s.disabled)
                if (r != null) { r.enabled = true; n++; }
            if (s.group != null) s.group.enabled = true;
            Uncount(s.species);
            return n;
        }

        /// <summary>Drops the entries of trees whose cell was unloaded.</summary>
        static void PurgeDestroyed()
        {
            List<GameObject> dead = null;
            foreach (var kv in _swaps)
                if (kv.Key == null) (dead ??= new List<GameObject>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var go in dead)
            {
                Uncount(_swaps[go].species);
                _swaps.Remove(go);
            }
        }

        static void Uncount(string species)
        {
            if (species == null || !_perSpecies.TryGetValue(species, out int c)) return;
            if (c <= 1) _perSpecies.Remove(species);
            else _perSpecies[species] = c - 1;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _swaps.Clear();
            _perSpecies.Clear();
            _enabled = false;
            _option = null;
            _kept = _noModel = 0;
        }

        /// <summary>The stored option at start (the world remaster's own start re-applies through G3WorldV2.Apply).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            if (Option) Refresh();
        }
    }
}
