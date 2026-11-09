using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Runtime world remaster override (Docs/remaster-world.md §4, parts 10.15.3). Off by default and split into independent
    /// parts (<see cref="RemasterOptions.WorldFeatures"/>, fix-71): Lighting (the look layers <see cref="G3WorldRemasterLook"/> +
    /// <see cref="G3WorldV2"/>), Terrain, Grass, Rocks, Buildings (the material pairs of each part) and Trees (SpeedTree
    /// materials + <see cref="G3TreeV2"/>, whose option is the stored Trees switch). Every part: env
    /// (<see cref="RemasterOptions.EnvChain"/>), then PlayerPrefs <c>G3.Remaster.&lt;Part&gt;</c> (the old
    /// <c>G3.Remaster.World</c> is migrated once). The renderers of every scene loaded in play mode (the world cells are
    /// additive scenes) get their shared materials swapped through <see cref="G3WorldRemasterSet"/>, per pair towards its
    /// part's state; <see cref="G3VegetationRenderer"/> resolves its material per draw via <see cref="Resolve"/>.
    /// <see cref="SetPart"/> / <see cref="SetAll"/> (F7) re-walk all loaded scenes in either direction (live A/B); the live
    /// swaps go through <see cref="G3RendererChanges"/> (nearest renderers first, a few hundred per frame, GPU Resident
    /// Drawer safe, Docs/performance.md), a loaded cell is swapped at once. The original materials and cell scenes are
    /// never modified on disk.
    /// </summary>
    public static class G3WorldRemaster
    {
        static G3WorldRemasterSet _set;
        static bool _setLoaded;
        static readonly bool?[] _parts = new bool?[(int)RemasterFeature.Trees];   // Lighting..Buildings (Trees: G3TreeV2.Option)
        static bool _migrated;

        /// <summary>Raised after <see cref="SetPart"/> / <see cref="SetAll"/> (page rows, F7) so open UI re-reads the parts.</summary>
        public static event Action PartsChanged;

        sealed class Prefs : IRemasterPrefs
        {
            public bool Has(string key) => PlayerPrefs.HasKey(key);
            public int Get(string key) => PlayerPrefs.GetInt(key, 0);
            public void Set(string key, int value) => PlayerPrefs.SetInt(key, value);
            public void Delete(string key) => PlayerPrefs.DeleteKey(key);
        }

        /// <summary>Migrates the old single <c>G3.Remaster.World</c> key once (<see cref="RemasterOptions.Migrate"/>).</summary>
        public static void EnsureMigrated()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                int n = RemasterOptions.Migrate(new Prefs());
                if (n > 0)
                {
                    PlayerPrefs.Save();
                    Debug.Log($"[Remaster] migrated {RemasterOptions.LegacyWorldKey}=1 to {n} world part switches");
                }
#if G3_DEMO
                // rebrand: the demo build starts remastered (all world parts on, Modern lighting off), once per machine
                int p = RemasterOptions.ApplyDemoPreset(new Prefs(), G3WorldGi.ModernPrefKey);
                if (p > 0)
                {
                    PlayerPrefs.Save();
                    Debug.Log($"[Remaster] demo preset v{RemasterOptions.DemoPresetVersion}: all world parts on, modern lighting off ({p} keys)");
                }
#endif
            }
            catch (Exception) { }
        }

        /// <summary>The effective state of a switch: env chain, then the stored choice (after the migration).</summary>
        public static bool Stored(RemasterFeature f)
        {
            EnsureMigrated();
            bool stored = RemasterOptions.Default;
            try { stored = PlayerPrefs.GetInt(RemasterOptions.PrefKey(f), 0) == 1; } catch (Exception) { }
            var chain = RemasterOptions.EnvChain(f);
            var values = new string[chain.Length];
            for (int i = 0; i < chain.Length; i++) values[i] = Environment.GetEnvironmentVariable(chain[i]);
            return RemasterOptions.Resolve(values, stored);
        }

        /// <summary>One world part's state (Trees = <see cref="G3TreeV2.Option"/>).</summary>
        public static bool IsOn(RemasterFeature f)
        {
            if (f == RemasterFeature.Trees) return G3TreeV2.Option;
            if (!RemasterOptions.IsWorld(f)) return false;
            int i = (int)f;
            if (_parts[i] == null) _parts[i] = Stored(f);
            return _parts[i].Value;
        }

        /// <summary>The world parts that are on.</summary>
        public static WorldParts Parts
        {
            get
            {
                var p = WorldParts.None;
                foreach (var f in RemasterOptions.WorldFeatures) if (IsOn(f)) p |= RemasterOptions.Part(f);
                return p;
            }
        }

        /// <summary>Any world part on (the F7 label's state).</summary>
        public static bool Enabled => RemasterOptions.MasterOn(Parts);

        /// <summary>The pair list (null when the builder has not run).</summary>
        public static G3WorldRemasterSet Set
        {
            get
            {
                if (!_setLoaded)
                {
                    _setLoaded = true;
                    _set = Resources.Load<G3WorldRemasterSet>(G3WorldRemasterSet.ResourceName);
                }
                return _set;
            }
        }

        static readonly int V2SurfaceId = Shader.PropertyToID("_V2Surface");
        static List<(Material original, Material r3, Material v2, int kind, int part)> _triples;

        /// <summary>
        /// Every original with its r3 copy, v2 copy, v2 kind and world part (the stored part, else
        /// <see cref="WorldRemasterParts.Classify"/> from the shader, the name and the v2 copy's _V2Surface: x building, y rock).
        /// </summary>
        static List<(Material original, Material r3, Material v2, int kind, int part)> Triples()
        {
            if (_triples != null) return _triples;
            _triples = new List<(Material, Material, Material, int, int)>();
            if (Set == null) return _triples;
            var byOriginal = new Dictionary<Material, (Material r3, Material v2, int kind, int part)>();
            foreach (var p in Set.pairs)
                if (p.original != null) byOriginal[p.original] = (p.remaster, null, 0, p.part);
            if (Set.v2pairs != null)
                foreach (var p in Set.v2pairs)
                {
                    if (p.original == null) continue;
                    byOriginal.TryGetValue(p.original, out var t);
                    byOriginal[p.original] = (t.r3, p.v2, p.kind, p.part != 0 ? p.part : t.part);
                }
            foreach (var kv in byOriginal)
            {
                var (r3, v2, kind, stored) = kv.Value;
                var part = WorldRemasterParts.Stored(stored);
                if (part == null)
                {
                    bool arch = false, rock = false;
                    if (v2 != null && v2.HasProperty(V2SurfaceId))
                    {
                        var s = v2.GetVector(V2SurfaceId);
                        arch = s.x > 0.5f;
                        rock = s.y > 0.5f;
                    }
                    var o = kv.Key;
                    part = WorldRemasterParts.Classify(null, o.shader != null ? o.shader.name : null, o.name, kind, arch, rock);
                }
                _triples.Add((kv.Key, r3, v2, kind, (int)part.Value));
            }
            return _triples;
        }

        /// <summary>Material pairs of one part in the built set (0 = nothing built for it).</summary>
        public static int PairCount(WorldParts part)
        {
            int n = 0;
            foreach (var t in Triples()) if (((WorldParts)t.part & part) != 0 && (t.r3 != null || t.v2 != null)) n++;
            return n;
        }

        /// <summary>
        /// True when the build has something to show for a world part: Lighting = the look shader is in the build; Grass =
        /// grass pairs or the v2 dense grass (json v2 + v2Grass); Trees = SpeedTree pairs or v2 tree models; the rest = pairs.
        /// </summary>
        public static bool Available(RemasterFeature f)
        {
            var c = WorldRemasterLook.Current;
            switch (f)
            {
                case RemasterFeature.Lighting: return Resources.Load<Shader>("G3RemasterAtmosphere") != null;
                case RemasterFeature.Grass: return PairCount(WorldParts.Grass) > 0 || (c.v2 && c.v2Grass);
                case RemasterFeature.Trees: return PairCount(WorldParts.Trees) > 0;   // trees-v3: the remastered G3 materials (v2 models: JSON A/B only)
                case RemasterFeature.Characters: return false;
                default: return PairCount(RemasterOptions.Part(f)) > 0;
            }
        }

        static int _mapKey = -1;
        static Dictionary<Material, Material> _map;

        /// <summary>
        /// The swap map towards the wanted state: a pair whose part is off → original; on → the v2 copy where v2 and its
        /// feature (v2Terrain, v2Surfaces) are on and a copy exists, else the r3 copy (section 10).
        /// </summary>
        static Dictionary<Material, Material> Map()
        {
            var c = WorldRemasterLook.Current;
            var parts = Parts;
            bool v2Terrain = c.v2 && c.v2Terrain, v2Surfaces = c.v2 && c.v2Surfaces;
            int key = (int)parts | (v2Terrain ? 1 << 8 : 0) | (v2Surfaces ? 1 << 9 : 0);
            if (_map != null && key == _mapKey) return _map;
            _mapKey = key;
            _map = WorldRemasterSwap.Target(Triples(), part => (parts & (WorldParts)part) != 0, kind => kind == 1 ? v2Terrain : kind == 2 && v2Surfaces);
            return _map;
        }

        /// <summary>Re-walks the loaded scenes after a v2 toggle changed (G3WorldRemasterLook.Reload), paced.</summary>
        public static int Refresh() => Application.isPlaying ? QueueLoadedScenes() : 0;

        static Dictionary<Material, Material> _originalOf;

        /// <summary>G3's material of a remaster copy (r3 or v2), else the material itself (remaster-veg: layer classes read
        /// from G3's own layer images).</summary>
        public static Material OriginalOf(Material m)
        {
            if (m == null) return null;
            if (_originalOf == null)
            {
                _originalOf = new Dictionary<Material, Material>();
                foreach (var t in Triples())
                {
                    if (t.r3 != null) _originalOf[t.r3] = t.original;
                    if (t.v2 != null) _originalOf[t.v2] = t.original;
                }
            }
            return _originalOf.TryGetValue(m, out var o) && o != null ? o : m;
        }

        /// <summary>The material to draw: the remaster copy while its part is on in play mode, else the original.</summary>
        public static Material Resolve(Material original)
        {
            if (original == null || !Application.isPlaying || !Enabled) return original;
            return Map().TryGetValue(original, out var r) && r != null ? r : original;
        }

        /// <summary>
        /// Switches one world part (live in play mode: look layers re-applied, loaded scenes re-walked); <paramref name="persist"/>
        /// stores it. Trees go through <see cref="G3TreeV2.SetOption"/>. Returns the swapped material slots.
        /// </summary>
        public static int SetPart(RemasterFeature f, bool on, bool persist = true)
        {
            if (!RemasterOptions.IsWorld(f)) return 0;
            Store(f, on, persist);
            if (persist) PlayerPrefs.Save();
            int n = Apply();
            Debug.Log($"[Remaster] {f.ToString().ToLowerInvariant()} {(on ? "on" : "off")}: {n} material slots queued (G3RendererChanges) " +
                      $"(parts on: {WorldRemasterParts.Describe(Parts)}; {PairCount(RemasterOptions.Part(f))} pairs" +
                      $"{(on && !Available(f) ? ", not built" : "")})");
            if (f == RemasterFeature.Trees) G3TreeV2.LogState(on);
            PartsChanged?.Invoke();
            return n;
        }

        /// <summary>The master switch (F7): every world part on or off at once. Returns the swapped material slots.</summary>
        public static int SetAll(bool on, bool persist = true)
        {
            foreach (var f in RemasterOptions.WorldFeatures) Store(f, on, persist);
            if (persist) PlayerPrefs.Save();
            int n = Apply();
            G3TreeV2.LogState(on);
            PartsChanged?.Invoke();
            return n;
        }

        /// <summary>Old single switch (editor menu, scripts): same as <see cref="SetAll"/>.</summary>
        public static int SetEnabled(bool on, bool persist = false) => SetAll(on, persist);

        static void Store(RemasterFeature f, bool on, bool persist)
        {
            EnsureMigrated();
            if (f == RemasterFeature.Trees) { G3TreeV2.SetOption(on, persist, refresh: false); return; }
            if (persist) PlayerPrefs.SetInt(RemasterOptions.PrefKey(f), on ? 1 : 0);
            _parts[(int)f] = on;
        }

        /// <summary>Applies the current parts in play mode: look layers, v2 trees, every loaded scene (the last two paced).</summary>
        static int Apply()
        {
            if (!Application.isPlaying) return 0;
            G3WorldRemasterLook.Apply(Parts);   // also G3WorldV2 (grass v2, tree refresh)
            G3TreeV2.Refresh();
            G3TerrainRelief.Apply(Parts);       // remaster-relief (section 13): the landscape cells' relief copies
            return QueueLoadedScenes();
        }

        const string WaveLabel = "world remaster";
        static readonly List<(float dist, Renderer renderer)> _plan = new List<(float, Renderer)>();
        static readonly Comparison<(float dist, Renderer renderer)> ByDistance = (a, b) => a.dist.CompareTo(b.dist);

        /// <summary>
        /// The live switch: finds every renderer of the loaded scenes whose materials differ from the wanted state and queues
        /// it on <see cref="G3RendererChanges"/>, nearest to the camera first. Each queued swap re-reads the wanted state when
        /// it runs (a switch flipped back meanwhile makes it a no-op). Returns the material slots that will change.
        /// </summary>
        public static int QueueLoadedScenes()
        {
            var map = Map();
            if (map.Count == 0) return 0;
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            int n = 0;
            _plan.Clear();
            var renderers = new List<Renderer>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    root.GetComponentsInChildren(true, renderers);
                    foreach (var r in renderers)
                    {
                        if (r is ParticleSystemRenderer) continue;
                        int k = WorldRemasterSwap.Apply(r.sharedMaterials, map);   // a copy: counts only
                        if (k == 0) continue;
                        n += k;
                        _plan.Add(((r.bounds.center - eye).sqrMagnitude, r));
                    }
                }
            }
            _plan.Sort(ByDistance);
            foreach (var (_, r) in _plan)
            {
                var target = r;
                G3RendererChanges.Enqueue(target, () => SwapNow(target), WaveLabel);
            }
            _plan.Clear();
            return n;
        }

        /// <summary>One renderer towards the current state; returns its weight for the pacing (1 when it is drawn, else 0).</summary>
        static int SwapNow(Renderer r)
        {
            if (r == null) return 0;
            var mats = r.sharedMaterials;
            if (WorldRemasterSwap.Apply(mats, Map()) == 0) return 0;
            r.sharedMaterials = mats;
            return r.enabled && r.gameObject.activeInHierarchy ? 1 : 0;
        }

        /// <summary>Converts every loaded scene to the current state at once (start); returns the swapped material slots.</summary>
        public static int ApplyLoadedScenes()
        {
            int n = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++) n += Apply(SceneManager.GetSceneAt(i));
            return n;
        }

        /// <summary>Swaps the shared materials of every renderer in <paramref name="scene"/> towards the current parts.</summary>
        static readonly List<Renderer> SceneRenderers = new List<Renderer>();
        static readonly List<Material> SceneMaterials = new List<Material>();

        public static int Apply(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return 0;
            var map = Map();
            if (map.Count == 0) return 0;
            int n = 0;
            var renderers = SceneRenderers;
            var mats = SceneMaterials;
            foreach (var root in scene.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, renderers);
                foreach (var r in renderers)
                {
                    if (r is ParticleSystemRenderer) continue;
                    // perf-5: read into a reused list (r.sharedMaterials allocates an array per renderer: ~20k per cell)
                    r.GetSharedMaterials(mats);
                    int k = 0;
                    for (int i = 0; i < mats.Count; i++)
                        if (mats[i] != null && map.TryGetValue(mats[i], out var to) && to != null && !ReferenceEquals(to, mats[i])) { mats[i] = to; k++; }
                    if (k == 0) continue;
                    r.SetSharedMaterials(mats);
                    n += k;
                }
            }
            renderers.Clear();
            mats.Clear();
            return n;
        }

        /// <summary>One line for the runner: the parts and the pairs per part.</summary>
        public static string Describe()
        {
            var l = new List<string>();
            foreach (var f in RemasterOptions.WorldFeatures)
                l.Add($"{f.ToString().ToLowerInvariant()} {(IsOn(f) ? "on" : "off")}/{PairCount(RemasterOptions.Part(f))}");
            return "world parts (state/pairs): " + string.Join(", ", l);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _set = null; _setLoaded = false; _map = null; _mapKey = -1; _triples = null; _originalOf = null; _migrated = false;
            for (int i = 0; i < _parts.Length; i++) _parts[i] = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (Enabled)
            {
                G3WorldRemasterLook.Apply(Parts);
                G3TerrainRelief.Apply(Parts);
                int n = ApplyLoadedScenes();
                Debug.Log($"[G3WorldRemaster] on ({WorldRemasterParts.Describe(Parts)}): {Map().Count} material swaps, {n} slots swapped in the loaded scenes; {Describe()} ({G3WorldV2.Describe()})");
            }
            else Debug.Log("[G3WorldRemaster] off at start; " + Describe());
            Debug.Log("[G3WorldRemaster] start switches (fix-168): " + Sources());
        }

        /// <summary>fix-168: where each switch's start state comes from: the stored pref (- = none) and an overriding variable.</summary>
        public static string Sources()
        {
            var l = new List<string>();
            foreach (var f in RemasterOptions.Rows)
            {
                string pref = "?";
                try { var key = RemasterOptions.PrefKey(f); pref = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key, 0).ToString() : "-"; } catch (Exception) { }
                string env = null;
                foreach (var v in RemasterOptions.EnvChain(f))
                {
                    var value = Environment.GetEnvironmentVariable(v);
                    if (WorldRemasterSwitch.ParseFlag(value) != null) { env = v + "=" + value; break; }
                }
                l.Add($"{f.ToString().ToLowerInvariant()} pref {pref}{(env != null ? ", env " + env : "")}");
            }
            // fix-171: the sub-switches too (relief, lighting quality)
            string relief = "?", quality = "?";
            try { relief = PlayerPrefs.HasKey(G3TerrainRelief.PrefKey) ? PlayerPrefs.GetInt(G3TerrainRelief.PrefKey, 1).ToString() : "-"; } catch (Exception) { }
            try { quality = PlayerPrefs.HasKey(LightingQualityLevels.PrefKey) ? ((LightingQuality)PlayerPrefs.GetInt(LightingQualityLevels.PrefKey, 2)).ToString() : "-"; } catch (Exception) { }
            string renv = Environment.GetEnvironmentVariable(G3TerrainRelief.EnvVar), qenv = Environment.GetEnvironmentVariable(LightingQualityLevels.EnvVar);
            l.Add($"relief pref {relief}{(!string.IsNullOrEmpty(renv) ? ", env " + G3TerrainRelief.EnvVar + "=" + renv : "")} (on {G3TerrainRelief.Option})");
            l.Add($"lighting quality pref {quality}{(!string.IsNullOrEmpty(qenv) ? ", env " + LightingQualityLevels.EnvVar + "=" + qenv : "")} (now {G3WorldRemasterLook.Quality}, {G3WorldRemasterLook.QualitySource})");
            return string.Join("; ", l);
        }

        static readonly global::Unity.Profiling.ProfilerMarker SceneMarker = new global::Unity.Profiling.ProfilerMarker("G3WorldRemaster.SceneLoaded");

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            using var _ = SceneMarker.Auto();
            if (Enabled) Apply(scene);
        }
    }
}
