using System;
using System.Collections.Generic;
using G3.Unity.Characters;
using UnityEngine;
using Object = UnityEngine.Object;

namespace G3.Unity
{
    /// <summary>
    /// The character remaster switched at runtime (Options → Remaster → Characters; Docs/remaster-characters.md §5.6).
    /// The import-time switch (G3RemasterSettings) bakes the remaster into the prefabs; this one leaves the prefabs original
    /// and swaps mesh, materials and bones of the registered characters' renderers through <see cref="G3CharacterRemasterSet"/>,
    /// adds the remaster's extra renderers (v2 hair) and restores everything when switched off. Off by default (PlayerPrefs
    /// <c>G3.Remaster.Characters</c>; env <c>G3_REMASTER_CHARACTERS</c> / <c>G3_REMASTER_RUNTIME</c> win).
    /// Characters register themselves (the game registers the player hero, NpcViewPool every NPC view); code that changes a
    /// registered character's renderers (HeroArmorView) wraps the change in <see cref="Suspend"/> / <see cref="Resume"/>.
    /// Renderers without a mesh swap (NPCs, orcs, creatures, items in hand) get the set's material table
    /// (<see cref="MaterialSwapMap{T}"/>, §6.3): mapped slot by slot on, mapped back off; code that assigns item materials
    /// to a registered character's renderer passes them through <see cref="Map"/>.
    /// </summary>
    public static class G3CharacterRemaster
    {
        sealed class Memento
        {
            public Renderer renderer;
            public Mesh mesh;
            public Material[] materials;
            public Transform[] bones;
            public Transform rootBone;
            public bool enabled;
            public Mesh applied;   // the mesh this swap put in (a later change by someone else is not undone)
        }

        sealed class State
        {
            public readonly List<Memento> swapped = new List<Memento>();
            public readonly List<GameObject> created = new List<GameObject>();
        }

        static readonly List<GameObject> _characters = new List<GameObject>();
        static readonly Dictionary<GameObject, State> _states = new Dictionary<GameObject, State>();
        static G3CharacterRemasterSet _set;
        static bool _setLoaded;
        static bool? _enabled;
        static CharacterSwapIndex _index;
        static LazyAssetCache<Mesh> _meshes;            // swap/extra meshes, loaded on first use (§6.6)
        static LazyAssetCache<Material> _swapMaterials; // their materials
        static LazyAssetCache<G3FaceKit> _kits;
        static MaterialSwapMap<Material> _materials;
        static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>The stored choice (or the environment override).</summary>
        public static bool Enabled
        {
            get
            {
                if (_enabled == null)
                {
                    bool stored = RemasterOptions.Default;
                    try { stored = PlayerPrefs.GetInt(RemasterOptions.PrefKey(RemasterFeature.Characters), 0) == 1; } catch (Exception) { }
                    _enabled = RemasterOptions.Resolve(Environment.GetEnvironmentVariable(RemasterOptions.EnvVar(RemasterFeature.Characters)),
                                                       Environment.GetEnvironmentVariable(RemasterOptions.SharedEnvVar), stored);
                }
                return _enabled.Value;
            }
        }

        /// <summary>The runtime set (null when the builder has not run before the build).</summary>
        public static G3CharacterRemasterSet Set
        {
            get
            {
                if (!_setLoaded)
                {
                    _setLoaded = true;
                    _set = Resources.Load<G3CharacterRemasterSet>(G3CharacterRemasterSet.ResourceName);
                }
                return _set;
            }
        }

        /// <summary>True when the build carries remaster data for characters.</summary>
        public static bool Available => Set != null && (Set.swaps.Count > 0 || Set.materials.Count > 0);

        /// <summary>Switches the remaster for every registered character; returns the renderers changed.</summary>
        public static int SetEnabled(bool on, bool persist = true)
        {
            if (persist)
            {
                PlayerPrefs.SetInt(RemasterOptions.PrefKey(RemasterFeature.Characters), on ? 1 : 0);
                PlayerPrefs.Save();
            }
            _enabled = on;
            int n = 0;
            Prune();
            foreach (var c in _characters) n += Apply(c, on);
            int loaded = _materials != null ? _materials.Loaded : 0;
            int swapLoaded = (_meshes?.Loaded ?? 0) + (_swapMaterials?.Loaded ?? 0) + (_kits?.Loaded ?? 0);
            if (!on && (loaded > 0 || swapLoaded > 0))
            {
                // every registered renderer is back on its original: let the remaster meshes and textures go
                _materials?.ReleaseLoaded();
                _meshes?.Clear();
                _swapMaterials?.Clear();
                _kits?.Clear();
                _prefetch.Clear();
                _requests.Clear();
                Resources.UnloadUnusedAssets();
            }
            Debug.Log($"[Remaster] characters {(on ? "on" : "off")}: {n} renderers changed on {_characters.Count} characters" +
                      (on ? $", {loaded} table materials, {_meshes?.Loaded ?? 0} swap meshes, {_swapMaterials?.Loaded ?? 0} swap materials loaded" : "") +
                      (on && !Available ? " (no G3CharacterRemasterSet in this build: run G3/Remaster/Build Runtime Character Set)" : ""));
            return n;
        }

        /// <summary>Tracks a character (the player hero) and applies the current state to it.</summary>
        public static void Register(GameObject character)
        {
            if (character == null) return;
            Prune();
            if (!_characters.Contains(character)) _characters.Add(character);
            if (Enabled) Apply(character, true);
        }

        public static void Unregister(GameObject character)
        {
            if (character == null) return;
            Apply(character, false);
            _characters.Remove(character);
        }

        /// <summary>Puts the original renderers back before someone else changes them (armour change).</summary>
        public static void Suspend(GameObject character)
        {
            if (character != null && _states.ContainsKey(character)) Apply(character, false);
        }

        /// <summary>Re-applies the current state after <see cref="Suspend"/>.</summary>
        public static void Resume(GameObject character)
        {
            if (character != null && Enabled && _characters.Contains(character)) Apply(character, true);
        }

        /// <summary>The material table of the set (empty without a set).</summary>
        public static MaterialSwapMap<Material> Materials
        {
            get
            {
                if (_materials == null)
                {
                    _materials = new MaterialSwapMap<Material>(m => m.name, path => Resources.Load<Material>(path));
                    if (Set != null)
                        foreach (var p in Set.materials) _materials.Add(p.original, p.remaster);
                }
                return _materials;
            }
        }

        /// <summary>Item/prop materials as a registered character should show them now (remastered while on).</summary>
        public static Material[] Map(Material[] materials) =>
            materials == null || !Enabled || Set == null ? materials : Materials.Map(materials, true);

        /// <summary>True for a mesh the remaster put in (a remastered head wears its remastered talking face). Only swaps
        /// that were applied loaded their meshes, so this never loads anything.</summary>
        public static bool IsRemasterMesh(Mesh mesh) => mesh != null && _meshes != null && _meshes.Contains(mesh);

        /// <summary>The remastered talking head for a static head mesh name (loaded on first use), or null.</summary>
        public static G3FaceKit FaceKit(string head)
        {
            if (Set == null || string.IsNullOrEmpty(head)) return null;
            foreach (var k in Set.faceKits)
                if (string.Equals(k.head, head, StringComparison.OrdinalIgnoreCase))
                    return (_kits ??= new LazyAssetCache<G3FaceKit>(p => Resources.Load<G3FaceKit>(p))).Get(k.kit);
            return null;
        }

        // perf-5: the Resources loads Apply would make for a character, started asynchronously ahead of its first spawn (a new
        // NPC type loaded its remaster meshes / materials / textures synchronously in the spawn frame: 20-500+ ms)
        static readonly Dictionary<GameObject, List<ResourceRequest>> _prefetch = new Dictionary<GameObject, List<ResourceRequest>>();
        static readonly Dictionary<string, ResourceRequest> _requests = new Dictionary<string, ResourceRequest>(StringComparer.Ordinal);
        static readonly List<Renderer> _prefetchRenderers = new List<Renderer>();

        /// <summary>
        /// perf-5: starts loading (asynchronously) what <see cref="Register"/> will load for an instance of
        /// <paramref name="prefab"/> while the remaster is on; true when nothing of it is still loading (then Register only
        /// finds loaded assets). Cheap to call again every pass until it says true.
        /// </summary>
        public static bool Prefetch(GameObject prefab)
        {
            if (prefab == null || !Enabled || Set == null || (Set.swaps.Count == 0 && Set.materials.Count == 0)) return true;
            if (!_prefetch.TryGetValue(prefab, out var list))
            {
                _prefetch[prefab] = list = new List<ResourceRequest>();
                prefab.GetComponentsInChildren(true, _prefetchRenderers);
                foreach (var r in _prefetchRenderers)
                {
                    var smr = r as SkinnedMeshRenderer;
                    var filter = smr == null ? r.GetComponent<MeshFilter>() : null;
                    Mesh mesh = smr != null ? smr.sharedMesh : filter != null ? filter.sharedMesh : null;
                    if (mesh == null) continue;
                    var mats = r.sharedMaterials;
                    var swap = Find(mesh, mats, smr != null, out _);
                    if (swap != null && !swap.hide)
                    {
                        Request<G3CharacterMeshRef>(swap.mesh, list);
                        foreach (var p in swap.materials) Request<Material>(p, list);
                        foreach (var e in swap.extras)
                        {
                            Request<G3CharacterMeshRef>(e.mesh, list);
                            foreach (var p in e.materials) Request<Material>(p, list);
                        }
                    }
                    foreach (var m in mats)
                        if (m != null && Materials.TryPendingKey(m.name, out var key)) Request<Material>(key, list);
                }
                _prefetchRenderers.Clear();
            }
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].isDone) list.RemoveAt(i);
            return list.Count == 0;
        }

        static void Request<T>(string path, List<ResourceRequest> into) where T : Object
        {
            if (string.IsNullOrEmpty(path)) return;
            string key = typeof(T).Name + ":" + path;
            if (!_requests.TryGetValue(key, out var req)) _requests[key] = req = Resources.LoadAsync<T>(path);
            if (req != null && !req.isDone) into.Add(req);
        }

        static Mesh LoadMesh(string path) =>
            (_meshes ??= new LazyAssetCache<Mesh>(p => { var r = Resources.Load<G3CharacterMeshRef>(p); return r != null ? r.mesh : null; })).Get(path);

        static Material[] LoadMaterials(string[] paths) =>
            (_swapMaterials ??= new LazyAssetCache<Material>(p => Resources.Load<Material>(p))).GetAll(paths, all: false);

        /// <summary>The swaps by original mesh name / vertex count / skinning (built once per set).</summary>
        static CharacterSwapIndex Index
        {
            get
            {
                if (_index == null)
                {
                    _index = new CharacterSwapIndex();
                    if (Set != null)
                        for (int i = 0; i < Set.swaps.Count; i++)
                        {
                            var s = Set.swaps[i];
                            _index.Add(i, s.original, s.originalVertices, s.skinned, s.originalMaterials);
                        }
                }
                return _index;
            }
        }

        /// <summary>Converts one character towards <paramref name="on"/>; returns the renderers changed.</summary>
        public static int Apply(GameObject character, bool on)
        {
            if (character == null) return 0;
            if (!on) return Restore(character);
            if (Set == null || (Set.swaps.Count == 0 && Set.materials.Count == 0)) return 0;
            if (!_states.TryGetValue(character, out var state)) _states[character] = state = new State();

            Dictionary<string, Transform> bones = null;
            var done = new HashSet<Renderer>();
            foreach (var m in state.swapped) done.Add(m.renderer);
            foreach (var go in state.created) foreach (var r in go.GetComponentsInChildren<Renderer>(true)) done.Add(r);

            int n = 0;
            var headExtras = new List<Renderer>();
            foreach (var r in character.GetComponentsInChildren<Renderer>(true))
            {
                if (done.Contains(r)) continue;
                var smr = r as SkinnedMeshRenderer;
                var filter = smr == null ? r.GetComponent<MeshFilter>() : null;
                Mesh mesh = smr != null ? smr.sharedMesh : filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                var swap = Find(mesh, r.sharedMaterials, smr != null, out bool exact);
                Mesh swapTo = null;
                if (swap != null && !swap.hide && !string.IsNullOrEmpty(swap.mesh))
                {
                    swapTo = LoadMesh(swap.mesh);   // first use loads it (and nothing else of the set)
                    if (swapTo == null)
                    {
                        if (_warned.Add("mesh/" + swap.mesh)) Debug.LogWarning($"[Remaster] character part {swap.label}: {swap.mesh} not in Resources");
                        swap = null;
                    }
                }
                if (swap == null)
                {
                    // no mesh swap: the material table (NPCs, orcs, creatures, props)
                    var mapped = Materials.Map(r.sharedMaterials, true);
                    if (!ReferenceEquals(mapped, r.sharedMaterials)) { r.sharedMaterials = mapped; n++; }
                    continue;
                }
                if (bones == null)
                {
                    bones = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in character.GetComponentsInChildren<Transform>(true))
                        if (!bones.ContainsKey(t.name)) bones[t.name] = t;
                }

                var memento = new Memento
                {
                    renderer = r, mesh = mesh, materials = r.sharedMaterials, enabled = r.enabled,
                    bones = smr != null ? smr.bones : null, rootBone = smr != null ? smr.rootBone : null,
                };
                if (swap.hide) r.enabled = false;
                else
                {
                    if (smr != null)
                    {
                        if (swapTo != null && swapTo != mesh)
                        {
                            smr.sharedMesh = swapTo;
                            smr.bones = Bones(swap.bones, bones, smr.rootBone, swap.label);
                            smr.rootBone = Bone(swap.rootBone, bones, smr.rootBone);
                            G3FaceBlink.Ensure(smr); // a head with ARKit blink shapes (MetaHuman hero) blinks
                        }
                    }
                    else if (swapTo != null) filter.sharedMesh = swapTo;
                    var current = memento.materials;
                    var swapMesh = smr != null ? smr.sharedMesh : filter.sharedMesh;
                    if (!exact && swapMesh != null && swapMesh.subMeshCount == current.Length)
                        r.sharedMaterials = Materials.Map(current, true);   // another MaterialSwitch: its own maps, same UVs
                    else if (swap.materials != null && swap.materials.Length > 0)
                    {
                        var mats = LoadMaterials(swap.materials);
                        for (int i = 0; i < mats.Length; i++) if (mats[i] == null && i < current.Length) mats[i] = current[i];
                        r.sharedMaterials = mats;
                    }
                    else r.sharedMaterials = Materials.Map(current, true);   // part kept (it only gains extras): the table
                }
                memento.applied = smr != null ? smr.sharedMesh : filter.sharedMesh;
                state.swapped.Add(memento);
                n++;
                foreach (var e in swap.extras)
                {
                    var go = CreateExtra(e, r, bones, smr != null ? smr.rootBone : null, swap.label);
                    if (go == null) continue;
                    state.created.Add(go);
                    n++;
                    if (e.head) headExtras.Add(go.GetComponent<Renderer>());
                }
            }
            if (headExtras.Count > 0) UpdateHeadView(character, headExtras, add: true);
            return n;
        }

        static int Restore(GameObject character)
        {
            int n = 0;
            if (!_states.TryGetValue(character, out var state)) return MapBack(character);
            _states.Remove(character);
            var gone = new List<Renderer>();
            foreach (var go in state.created)
            {
                if (go == null) continue;
                gone.AddRange(go.GetComponentsInChildren<Renderer>(true));
                go.SetActive(false);
                Object.Destroy(go);
                n++;
            }
            if (gone.Count > 0) UpdateHeadView(character, gone, add: false);
            for (int i = state.swapped.Count - 1; i >= 0; i--)
            {
                var m = state.swapped[i];
                if (m.renderer == null) continue;
                var smr = m.renderer as SkinnedMeshRenderer;
                var mf = smr == null ? m.renderer.GetComponent<MeshFilter>() : null;
                Mesh now = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                if (now != m.applied) continue;   // changed by someone else since: theirs stays
                if (smr != null)
                {
                    G3FaceBlink.Remove(smr);
                    smr.sharedMesh = m.mesh;
                    smr.bones = m.bones;
                    smr.rootBone = m.rootBone;
                }
                else if (mf != null) mf.sharedMesh = m.mesh;
                m.renderer.sharedMaterials = m.materials;
                m.renderer.enabled = m.enabled;
                n++;
            }
            return n + MapBack(character);
        }

        /// <summary>Every renderer of the character back to the original materials of the table (stateless).</summary>
        static int MapBack(GameObject character)
        {
            if (Set == null || Set.materials.Count == 0) return 0;
            int n = 0;
            foreach (var r in character.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                var back = Materials.Map(mats, false);
                if (ReferenceEquals(back, mats)) continue;
                r.sharedMaterials = back;
                n++;
            }
            return n;
        }

        /// <summary>The entry for a renderer: same mesh and materials (<paramref name="exact"/>), else same mesh (another
        /// MaterialSwitch: the caller maps that renderer's own materials).</summary>
        static G3CharacterRemasterSet.Swap Find(Mesh mesh, Material[] materials, bool skinned, out bool exact)
        {
            exact = false;
            if (Set.swaps.Count == 0) return null;
            var names = new string[materials?.Length ?? 0];
            for (int i = 0; i < names.Length; i++) names[i] = materials[i] != null ? materials[i].name : null;
            int k = Index.Find(mesh.name, mesh.vertexCount, skinned, names, out exact);
            return k >= 0 ? Set.swaps[k] : null;
        }

        static Transform[] Bones(string[] names, Dictionary<string, Transform> bones, Transform fallback, string label)
        {
            var result = new Transform[names?.Length ?? 0];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Bone(names[i], bones, fallback);
                if (names[i] != null && !bones.ContainsKey(names[i]) && _warned.Add(label + "/" + names[i]))
                    Debug.LogWarning($"[Remaster] character part {label}: bone {names[i]} not on the character, bound to its root bone");
            }
            return result;
        }

        static Transform Bone(string name, Dictionary<string, Transform> bones, Transform fallback) =>
            name != null && bones.TryGetValue(name, out var t) ? t : fallback;

        static GameObject CreateExtra(G3CharacterRemasterSet.Extra e, Renderer owner, Dictionary<string, Transform> bones, Transform rootBone, string label)
        {
            var mesh = LoadMesh(e.mesh);
            if (mesh == null) return null;
            Transform parent = !string.IsNullOrEmpty(e.parent) && bones.TryGetValue(e.parent, out var p) ? p : owner.transform.parent;
            var go = new GameObject(e.name) { layer = owner.gameObject.layer, hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(e.position, e.rotation);
            go.transform.localScale = e.scale;
            Renderer r;
            if (e.skinned)
            {
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = Bones(e.bones, bones, rootBone, label);
                smr.rootBone = Bone(e.rootBone, bones, rootBone);
                if (owner is SkinnedMeshRenderer o)
                {
                    smr.localBounds = o.localBounds;
                    smr.updateWhenOffscreen = o.updateWhenOffscreen;
                }
                r = smr;
            }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                r = go.AddComponent<MeshRenderer>();
            }
            r.sharedMaterials = LoadMaterials(e.materials);
            r.shadowCastingMode = owner.shadowCastingMode;
            r.receiveShadows = owner.receiveShadows;
            return go;
        }

        /// <summary>First-person view: head extras only cast shadows while the camera sits in the head, like the head itself.</summary>
        static void UpdateHeadView(GameObject character, List<Renderer> renderers, bool add)
        {
            var view = character.GetComponentInChildren<G3HeroView>(true);
            if (view == null) return;
            var list = new List<Renderer>(view.headRenderers ?? Array.Empty<Renderer>());
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (add) { if (!list.Contains(r)) list.Add(r); }
                else list.Remove(r);
            }
            list.RemoveAll(r => r == null);
            view.headRenderers = list.ToArray();
            if (add) view.SetFirstPerson(view.FirstPerson);
        }

        static void Prune()
        {
            _characters.RemoveAll(c => c == null);
            var dead = new List<GameObject>();
            foreach (var k in _states.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) _states.Remove(k);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _characters.Clear();
            _states.Clear();
            _set = null;
            _setLoaded = false;
            _enabled = null;
            _index = null;
            _meshes = null;
            _swapMaterials = null;
            _kits = null;
            _materials = null;
            _prefetch.Clear();
            _requests.Clear();
            _warned.Clear();
        }
    }
}
