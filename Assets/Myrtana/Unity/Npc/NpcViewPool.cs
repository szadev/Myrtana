using System.Collections.Generic;
using G3.Unity;
using G3.Unity.Characters;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// NPC views by visual key: prefabs are loaded asynchronously from Resources (<see cref="G3NpcPrefabs"/>), instances
    /// are reused. A new instance is turned from the imported static NPC (legacy idle Animation) into a live view: an
    /// Animator played by <see cref="G3MotionPlayer"/> with the rig's <see cref="G3MotionLibrary"/>, a
    /// <see cref="G3NpcAnimator"/>, the motion sounds (<see cref="G3MotionSounds"/>), the items in the hands
    /// (<see cref="NpcHandProps"/>) and an <see cref="NpcView"/>. Rigs without a library keep their idle loop.
    /// </summary>
    public sealed class NpcViewPool
    {
        readonly Transform _activeRoot, _idleRoot;
        readonly int _keepPerVisual;
        readonly Dictionary<string, Stack<NpcView>> _idle = new Dictionary<string, Stack<NpcView>>();
        readonly Dictionary<string, ResourceRequest> _loading = new Dictionary<string, ResourceRequest>();
        readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>();
        readonly HashSet<string> _missing = new HashSet<string>();
        ItemVisualCatalog _itemVisuals;
        bool _itemVisualsLoaded;

        public NpcViewPool(Transform parent, int keepPerVisual = 6)
        {
            _activeRoot = new GameObject("NPC Views").transform;
            _activeRoot.SetParent(parent, false);
            var idle = new GameObject("NPC View Pool");
            idle.transform.SetParent(parent, false);
            idle.SetActive(false); // pooled views and fresh instances stay inactive until they are set up
            _idleRoot = idle.transform;
            _keepPerVisual = keepPerVisual;
        }

        /// <summary>
        /// Mesh key of a content item by id (the session's item database), for the items NPCs take into their hands that
        /// are no hand-prop template (fix-153); null = none.
        /// </summary>
        public System.Func<string, string> ItemMeshOf { get; set; }

        /// <summary>Visual keys without a prefab (the NPCs stay invisible; build them with G3NpcImporter.BuildPrefabs).</summary>
        public IReadOnlyCollection<string> Missing => _missing;

        /// <summary>A view for <paramref name="visual"/> (inactive, under the active root), or null while its prefab is loading or missing.</summary>
        public NpcView Rent(string visual)
        {
            if (string.IsNullOrEmpty(visual) || _missing.Contains(visual)) return null;
            if (_idle.TryGetValue(visual, out var stack) && stack.Count > 0) return Activate(stack.Pop());
            var prefab = Prefab(visual);
            return prefab != null ? Activate(Create(prefab, visual)) : null;
        }

        public void Return(NpcView view)
        {
            view.Unbind();
            if (!_idle.TryGetValue(view.Visual, out var stack)) _idle[view.Visual] = stack = new Stack<NpcView>();
            if (stack.Count >= _keepPerVisual)
            {
                Object.Destroy(view.gameObject);
                return;
            }
            view.transform.SetParent(_idleRoot, false);
            stack.Push(view);
        }

        GameObject Prefab(string visual)
        {
            if (_prefabs.TryGetValue(visual, out var prefab)) return prefab;
            if (!_loading.TryGetValue(visual, out var request))
            {
                _loading[visual] = G3NpcPrefabs.LoadAsync(visual);
                return null;
            }
            if (!request.isDone) return null;
            prefab = request.asset as GameObject;
            if (prefab == null) { _loading.Remove(visual); _missing.Add(visual); return null; }
            // perf-5: what Create loads for this prefab is loaded asynchronously first (the rig's motion library, the item
            // visual catalog, the character remaster's meshes and materials): in the spawn frame they were synchronous loads
            // of 20 ms to over 1 s whenever the first NPC of a new type came into view
            var info = prefab.GetComponent<G3Character>();
            bool ready = G3MotionLibraries.Prefetch(info != null ? info.Rig : null);
            ready &= ItemVisualsReady();
            ready &= G3CharacterRemaster.Prefetch(prefab);
            if (!ready) return null;
            _loading.Remove(visual);
            _prefabs[visual] = prefab;
            return prefab;
        }

        ResourceRequest _itemVisualsRequest;

        bool ItemVisualsReady()
        {
            if (_itemVisualsLoaded) return true;
            _itemVisualsRequest ??= Resources.LoadAsync<ItemVisualCatalog>(ItemVisualCatalog.ResourcePath);
            if (!_itemVisualsRequest.isDone) return false;
            _itemVisuals = _itemVisualsRequest.asset as ItemVisualCatalog;
            _itemVisualsLoaded = true;
            _itemVisualsRequest = null;
            return true;
        }

        ItemVisualCatalog ItemVisuals()
        {
            if (!_itemVisualsLoaded) { _itemVisuals = ItemVisualCatalog.Load(); _itemVisualsLoaded = true; }
            return _itemVisuals;
        }

        NpcView Activate(NpcView view)
        {
            view.transform.SetParent(_activeRoot, false);
            return view;
        }

        static readonly global::Unity.Profiling.ProfilerMarker InstantiateMarker = new global::Unity.Profiling.ProfilerMarker("NpcViewPool.Instantiate");
        static readonly global::Unity.Profiling.ProfilerMarker SetupMarker = new global::Unity.Profiling.ProfilerMarker("NpcViewPool.Setup");
        static readonly global::Unity.Profiling.ProfilerMarker RemasterMarker = new global::Unity.Profiling.ProfilerMarker("NpcViewPool.Remaster");

        NpcView Create(GameObject prefab, string visual)
        {
            GameObject go;
            using (InstantiateMarker.Auto()) go = Object.Instantiate(prefab, _idleRoot);
            using var setup = SetupMarker.Auto();
            var info = go.GetComponent<G3Character>();
            var library = G3MotionLibraries.Load(info != null ? info.Rig : null);
            G3NpcAnimator animator = null;
            if (library != null)
            {
                var legacy = go.GetComponent<Animation>();
                if (legacy != null) Object.DestroyImmediate(legacy);
                if (info != null) info.AutoPlay = null;
                var mecanim = go.AddComponent<Animator>();
                mecanim.applyRootMotion = false;
                // perf-3: an NPC no camera or shadow view renders is not evaluated at all (was CullUpdateTransforms: evaluated,
                // bones not written). Nothing reads its bones then (they were not written before either); no root motion.
                mecanim.cullingMode = AnimatorCullingMode.CullCompletely;
                go.AddComponent<G3MotionPlayer>().library = library;
                animator = go.AddComponent<G3NpcAnimator>();
                go.AddComponent<G3MotionSounds>();
                go.AddComponent<NpcHandProps>().Init(animator, ItemVisuals(), item => ItemMeshOf?.Invoke(item));
                G3ReliefFooting.Attach(go);   // stands on the drawn relief (Docs/remaster-world.md section 14)
            }
            var view = go.AddComponent<NpcView>();
            view.Init(visual, animator);
            using (RemasterMarker.Auto()) G3CharacterRemaster.Register(go); // Options → Remaster → Characters: NPC materials (§6.3); pruned when destroyed
            return view;
        }
    }
}
