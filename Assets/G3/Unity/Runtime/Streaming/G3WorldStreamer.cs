using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Streams the world cells (additive scenes listed in a <see cref="G3WorldManifest"/>) around the player/camera:
    /// cells whose content is within <see cref="loadDistance"/> are loaded asynchronously (nearest first), cells
    /// beyond <see cref="unloadDistance"/> are unloaded (hysteresis). Also hides the low-poly backdrop where full detail
    /// is loaded and switches the G3 weather by region (Myrtana / Varant / Nordmar).
    /// In Edit mode it only keeps the backdrop in sync with the cells opened via the G3/World menu.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 World Streamer")]
    public sealed class G3WorldStreamer : MonoBehaviour
    {
        public G3WorldManifest manifest;
        [Tooltip("Streaming centre. Empty: Camera.main.")]
        public Transform target;
        [Tooltip("Player object whose Behaviours/Colliders (CharacterController, controller script) stay disabled until the cells under it are loaded, so it does not fall through the world at start or after a teleport.")]
        public GameObject holdUntilReady;

        [Header("Distances (metres, to the cell's content bounds)")]
        [Tooltip("250 m cells: 400 m ≈ 1.5–2 cells around the player (5×5…6×6 cells).")]
        public float loadDistance = 400f;
        [Tooltip("Must be > loadDistance (hysteresis).")]
        public float unloadDistance = 600f;
        [Tooltip("Ocean-only cells (sea surface / sea floor) are cheap but rarely needed close up.")]
        public float oceanLoadDistance = 300f;
        [Min(1)] public int maxConcurrentLoads = 2;
        [Tooltip("Seconds between streaming updates.")]
        public float updateInterval = 0.2f;
        [Tooltip("Call Resources.UnloadUnusedAssets after this many cell unloads (it blocks the main thread for 120-175 ms in the " +
                 "owner's player: it marks every loaded object, ~470k). perf-5: only at a quiet moment (a loading hold, the game " +
                 "paused by a menu: timeScale 0), or during play once the process uses more than unloadAssetsAboveMB.")]
        public int unloadAssetsEvery = 8;
        [Tooltip("Used memory (MB) above which the cleanup also runs during play; the larger of this and " +
                 "unloadAssetsSystemShare of the machine's RAM. 0 = always during play (the old behaviour).")]
        public int unloadAssetsAboveMB = 3072;
        [Range(0f, 1f)] public float unloadAssetsSystemShare = 0.6f;
        [Tooltip("Application.backgroundLoadingPriority while the player can move: Low keeps the per-frame time spent " +
                 "integrating loaded assets small (smoother, cells arrive a bit later). High is used while the area under " +
                 "the player is not loaded yet (start, teleport) to get there fast.")]
        public ThreadPriority streamingPriority = ThreadPriority.Low;

        [Header("Runtime optimisation (Docs/performance.md)")]
        [Tooltip("Adds G3LightShadowBudget (only the nearest few torch lights cast shadows) and G3CharacterLod " +
                 "(distance-frozen animation / hidden far NPCs) at Play start if missing.")]
        public bool addRuntimeOptimizers = true;
        [Tooltip("Keep the player behind a loading screen at Play start until the area around it is loaded, awake and " +
                 "rendering smoothly (G3StartupLoader + G3LoadingOverlay). Player argument -g3noloading turns it off.")]
        public bool loadingScreenAtStart = true;
        [Tooltip("What the loading screen waits for. View: the player's cell and the neighbour cells the camera sees " +
                 "(the rest streams in afterwards). Area: every cell within loadDistance. Player argument -g3gatearea forces Area.")]
        public G3StartupLoader.Gate loadingGate = G3StartupLoader.Gate.View;
        [Tooltip("Gate in the editor's Play mode. Area until the cells are rebuilt with unpacked trees: a cell streamed " +
                 "in after the screen costs 0.5–1.4 s there (MergePrefabs, LockPersistentManager), so View was a 20 s slide show.")]
        public G3StartupLoader.Gate editorLoadingGate = G3StartupLoader.Gate.Area;
        [Tooltip("Load the cells the camera looks at before the ones behind it (same distance order otherwise).")]
        public bool viewFirst = true;

        [Header("Backdrop / camera")]
        public G3LowpolyBackdrop backdrop;
        [Tooltip("Raise the camera far plane to at least this so the low-poly horizon is visible (0 = leave).")]
        public float minFarClip = 6000f;
        [Tooltip("Editor only: keep Play mode running while the editor is unfocused (CLI-driven tests otherwise freeze at frame 1).")]
        public bool runInBackgroundInEditor = true;

        [Header("Weather by region (play mode)")]
        public bool switchWeatherByRegion = true;
        public G3Environment environment;
        [Tooltip("region → G3 weather state or environment name (G3Environment.weatherState).")]
        public List<RegionWeather> regionWeather = new List<RegionWeather>
        {
            new RegionWeather { region = "Myrtana", weatherState = "Myrtana" },
            new RegionWeather { region = "Varant", weatherState = "Varant" },
            new RegionWeather { region = "Nordmar", weatherState = "Nordmar" },
        };
        public float weatherBlendSeconds = 10f;

        [Serializable]
        public sealed class RegionWeather
        {
            public string region;
            public string weatherState;
        }

        enum State { Loading, Loaded, Unloading }

        sealed class Slot
        {
            public State state;
            public bool unloadWhenLoaded;
            public float distance;
            /// <summary>The cell root's time-sliced bulk content (null if none).</summary>
            public G3DeferredActivation deferred;
        }

        readonly G3PlayerHold _hold = new G3PlayerHold();
        readonly Dictionary<int, Slot> _slots = new Dictionary<int, Slot>();
        readonly List<(int index, float d)> _candidates = new List<(int, float)>();
        int[][] _coverSets;
        readonly HashSet<int> _wanted = new HashSet<int>();
        /// <summary>perf-22d: oversized cells whose huge objects (the world border) were moved to the master scene.</summary>
        readonly HashSet<int> _resident = new HashSet<int>();
        readonly Plane[] _frustum = new Plane[6];
        float _nextUpdate;
        int _unloadsSinceCleanup;
        bool _backdropDirty = true;
        string _weatherTarget;
        float _weatherT;

        /// <summary>Raised on the main thread when a cell scene finished loading / was unloaded.</summary>
        public event Action<G3WorldManifest.Cell, Scene> CellLoaded;
        public event Action<G3WorldManifest.Cell> CellUnloaded;

        public int LoadedCount { get; private set; }
        public int LoadingCount { get; private set; }
        public string CurrentRegion { get; private set; }
        /// <summary>True while <see cref="holdUntilReady"/> is frozen (waiting for the cells under it).</summary>
        public bool IsHolding => _hold.IsHeld;
        /// <summary>True while <paramref name="owner"/> (e.g. the streamer itself, waiting for cells) freezes the player.</summary>
        public bool IsHeldBy(object owner) => _hold.IsHeldBy(owner);
        /// <summary>While set (start-up loading screen), only these cells are loaded, so the ones the player sees first
        /// are not slowed down by the rest. Null: every wanted cell.</summary>
        public ICollection<int> LoadOnly { get; set; }

        /// <summary>Streaming centre: <see cref="target"/>, else the main camera.</summary>
        public Vector3 Centre
        {
            get
            {
                if (target != null) return target.position;
                var cam = Camera.main;
                return cam != null ? cam.transform.position : transform.position;
            }
        }

        void OnEnable()
        {
            _slots.Clear();
            _coverSets = null;
            _backdropDirty = true;
            // Edit mode schedules on realtimeSinceStartup, Play mode on unscaledTime; with domain reload disabled the
            // Edit-mode value survives into Play and would stall streaming for minutes.
            _nextUpdate = 0f;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        void OnSceneLoaded(Scene s, LoadSceneMode m) => _backdropDirty = true;
        void OnSceneUnloaded(Scene s) => _backdropDirty = true;

        void Start()
        {
            if (!Application.isPlaying) return;
#if UNITY_EDITOR
            if (runInBackgroundInEditor) Application.runInBackground = true;
#endif
            var cam = target != null ? target.GetComponentInChildren<Camera>() : Camera.main;
            if (cam == null) cam = Camera.main;
            if (cam != null && minFarClip > 0 && cam.farClipPlane < minFarClip) cam.farClipPlane = minFarClip;
            if (environment == null) environment = FindAnyObjectByType<G3Environment>();
            // Player command line "-g3noopt": run without them (A/B performance comparison).
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-g3noopt") >= 0) addRuntimeOptimizers = false;
            if (addRuntimeOptimizers)
            {
                if (GetComponent<G3LightShadowBudget>() == null) gameObject.AddComponent<G3LightShadowBudget>();
                if (GetComponent<G3CharacterLod>() == null) gameObject.AddComponent<G3CharacterLod>();
            }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-g3noloading") >= 0) loadingScreenAtStart = false;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-g3gatearea") >= 0) loadingGate = G3StartupLoader.Gate.Area;
            if (loadingScreenAtStart && manifest != null && GetComponent<G3StartupLoader>() == null)
            {
                var loader = gameObject.AddComponent<G3StartupLoader>();
                loader.streamer = this;
                loader.gate = Application.isEditor ? editorLoadingGate : loadingGate;
                gameObject.AddComponent<G3LoadingOverlay>();
            }
            if (holdUntilReady != null && manifest != null && !IsReady(holdUntilReady.transform.position)) Hold(true);
            if (switchWeatherByRegion && environment != null && manifest != null)
            {
                // Start in the right weather without blending.
                var w = WeatherFor(manifest.RegionAt(Centre));
                if (w != null) { environment.weatherState = w; environment.blend = 0; }
            }
        }

        void Update()
        {
            if (manifest == null) return;
            if (_coverSets == null) BuildCoverSets();

            if (!Application.isPlaying)
            {
                // Edit mode: cells are opened by the editor menu; just sync the backdrop with what is open.
                if (Time.realtimeSinceStartup >= _nextUpdate || _backdropDirty)
                {
                    _nextUpdate = Time.realtimeSinceStartup + 0.5f;
                    RefreshBackdrop(i => manifest.cells[i].HasScene && SceneManager.GetSceneByPath(manifest.cells[i].scenePath).isLoaded);
                }
                return;
            }

            UpdateWeather();
            CleanUpAssetsIfQuiet();
            if (_hold.IsHeldBy(this) && holdUntilReady != null && IsReady(holdUntilReady.transform.position)) Hold(false);
            if (Time.unscaledTime < _nextUpdate) { if (_backdropDirty) RefreshBackdropPlay(); return; }
            _nextUpdate = Time.unscaledTime + updateInterval;
            // Load fast while the player waits for ground (start / teleport), gently while walking around.
            bool urgent = IsHolding || !IsReady(Centre);
            var prio = urgent ? ThreadPriority.High : streamingPriority;
            if (Application.backgroundLoadingPriority != prio) Application.backgroundLoadingPriority = prio;
            StreamStep();
            if (_backdropDirty) RefreshBackdropPlay();
        }

        // ------------------------------------------------------------------ streaming

        void StreamStep()
        {
            var p = Centre;
            _candidates.Clear();
            int loading = 0;
            foreach (var s in _slots.Values) if (s.state == State.Loading) loading++;

            // Wanted = cells within their load distance + every cell whose terrain reaches into their squares (coverers).
            // Without the coverers a loaded square has holes and its low-poly backdrop piece can never be hidden, so it
            // is drawn over the full-detail cell (e.g. Faring: (21,17) at 403 m kept LP_20_17 / LP_20_18 visible).
            // perf-22d: an oversized cell (Cell_14_16 holds the 4 km world border, content box 4124 x 4102 m) is "near" from
            // almost everywhere; it pulled itself and its 8 terrain coverers (13..15 x 15..17, ~25k renderers 1-1.7 km away)
            // into every load set. Its coverers now go by its square; it goes by its content box only until its huge objects
            // live in the master scene (MakeResident), then by its square like any cell.
            _wanted.Clear();
            for (int i = 0; i < manifest.cells.Count; i++)
            {
                var c = manifest.cells[i];
                if (!c.HasScene) continue;
                float near = LoadDistanceOf(c);
                if (CoverDistance(c, p) <= near) foreach (int k in _coverSets[i]) _wanted.Add(k);
                else if (StreamDistance(i, c, p) <= near) _wanted.Add(i);
            }

            for (int i = 0; i < manifest.cells.Count; i++)
            {
                var c = manifest.cells[i];
                if (!c.HasScene) continue;
                float d = StreamDistance(i, c, p);
                float near = LoadDistanceOf(c);
                float far = Mathf.Max(unloadDistance, near + 50f);
                _slots.TryGetValue(i, out var slot);

                if (slot == null)
                {
                    // Opened in the editor before Play, or by someone else.
                    var existing = SceneManager.GetSceneByPath(c.scenePath);
                    if (existing.isLoaded)
                    {
                        slot = _slots[i] = new Slot { state = State.Loaded };
                        _backdropDirty = true;
                    }
                }

                if (_wanted.Contains(i))
                {
                    if (slot == null) { if (LoadOnly == null || LoadOnly.Contains(i)) _candidates.Add((i, d)); }
                    else if (slot.state == State.Loading) slot.unloadWhenLoaded = false;
                }
                else if (d > far && slot != null)   // not wanted and beyond the hysteresis band
                {
                    if (slot.state == State.Loaded) BeginUnload(i, c);
                    else if (slot.state == State.Loading) slot.unloadWhenLoaded = true;
                }
            }

            // In view first: the cells in the camera frustum before those behind it (each group nearest first).
            var cam = viewFirst ? ViewCamera : null;
            if (cam != null)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
                for (int k = 0; k < _candidates.Count; k++)
                {
                    var (i, d) = _candidates[k];
                    if (!GeometryUtility.TestPlanesAABB(_frustum, CellBounds(manifest.cells[i]))) _candidates[k] = (i, d + 1e5f);
                }
            }
            _candidates.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var (i, d) in _candidates)
            {
                if (loading >= maxConcurrentLoads) break;
                if (BeginLoad(i, manifest.cells[i])) loading++;
            }

            int loaded = 0;
            foreach (var s in _slots.Values) if (s.state == State.Loaded) loaded++;
            LoadedCount = loaded;
            LoadingCount = loading;
        }

        float LoadDistanceOf(G3WorldManifest.Cell c) =>
            c.kind == G3WorldManifest.CellKind.Ocean ? Mathf.Min(oceanLoadDistance, loadDistance) : loadDistance;

        /// <summary>perf-22d: a content box wider than this many cells marks an oversized cell (one huge object).</summary>
        public const float OversizedCells = 3f;

        bool Oversized(G3WorldManifest.Cell c) =>
            c.contentBounds.size.x > OversizedCells * manifest.cellSize || c.contentBounds.size.z > OversizedCells * manifest.cellSize;

        float SquareDistance(G3WorldManifest.Cell c, Vector3 p)
        {
            var r = manifest.CellRect(c.ix, c.iz);
            float dx = Mathf.Max(0, Mathf.Max(r.xMin - p.x, p.x - r.xMax));
            float dz = Mathf.Max(0, Mathf.Max(r.yMin - p.z, p.z - r.yMax));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Distance that wants / keeps the cell itself loaded.</summary>
        float StreamDistance(int i, G3WorldManifest.Cell c, Vector3 p) =>
            _resident.Contains(i) && Oversized(c) ? SquareDistance(c, p) : manifest.Distance(c, p);

        /// <summary>Distance that wants the cell together with its terrain coverers.</summary>
        float CoverDistance(G3WorldManifest.Cell c, Vector3 p) => Oversized(c) ? SquareDistance(c, p) : manifest.Distance(c, p);

        /// <summary>
        /// perf-22d: objects of an oversized cell wider than <see cref="OversizedCells"/> cells (the world border wall with
        /// its collider) move to the streamer's scene on the cell's first load and stay there; later loads of the cell drop
        /// their copy. The cell itself then streams by its square.
        /// </summary>
        void MakeResident(int index, G3WorldManifest.Cell c, Scene scene)
        {
            if (!Oversized(c) || !scene.isLoaded) return;
            float limit = OversizedCells * manifest.cellSize;
            bool first = !_resident.Contains(index);
            var moved = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var b = r.bounds;
                    if (b.size.x <= limit && b.size.z <= limit) continue;
                    var go = r.gameObject;
                    if (go.transform.childCount > 0 || moved.Contains(go)) continue;   // a leaf mesh object only
                    moved.Add(go);
                }
            foreach (var go in moved)
            {
                if (!first) { Destroy(go); continue; }
                go.transform.SetParent(null, true);
                SceneManager.MoveGameObjectToScene(go, gameObject.scene);
                Debug.Log($"[G3WorldStreamer] {go.name} ({c.ix},{c.iz}) stays loaded; the cell now streams by its square");
            }
            if (moved.Count > 0) _resident.Add(index);
        }

        /// <summary>Cells the streamer wants around <paramref name="pos"/> (within their load distance) and how many of
        /// them are loaded — the start-up loading screen waits for all of them.</summary>
        public (int loaded, int total) AreaProgress(Vector3 pos)
        {
            int loaded = 0, total = 0;
            if (manifest == null) return (0, 0);
            for (int i = 0; i < manifest.cells.Count; i++)
            {
                var c = manifest.cells[i];
                if (!c.HasScene || manifest.Distance(c, pos) > LoadDistanceOf(c)) continue;
                total++;
                if (IsLoaded(i)) loaded++;
            }
            return (loaded, total);
        }

        Camera ViewCamera
        {
            get
            {
                var cam = target != null ? target.GetComponentInChildren<Camera>() : null;
                return cam != null ? cam : Camera.main;
            }
        }

        /// <summary>The cell's content box (its square at the content's height range if it has none).</summary>
        Bounds CellBounds(G3WorldManifest.Cell c)
        {
            var r = manifest.CellRect(c.ix, c.iz);
            var b = c.contentBounds;
            float y0 = b.size.sqrMagnitude > 0 ? b.min.y : -1000f, y1 = b.size.sqrMagnitude > 0 ? b.max.y : 1000f;
            var box = new Bounds();
            box.SetMinMax(new Vector3(r.xMin, y0, r.yMin), new Vector3(r.xMax, y1, r.yMax));
            if (b.size.sqrMagnitude > 0) box.Encapsulate(b);
            return box;
        }

        /// <summary>
        /// Cells the player sees first from <paramref name="pos"/>: the cell under it and the eight neighbours that are
        /// within their load distance and touched by the camera frustum, each with the cells whose terrain covers it
        /// (otherwise the visible square has holes). The start-up loading screen waits only for these (owner idea:
        /// load what is visible first, stream the rest in the background).
        /// </summary>
        public void VisibleCells(Vector3 pos, ICollection<int> result)
        {
            result.Clear();
            if (manifest == null) return;
            if (_coverSets == null) BuildCoverSets();
            var cam = ViewCamera;
            if (cam != null) GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
            var at = manifest.CellOf(pos);
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int i = manifest.Index(at.x + dx, at.y + dz);
                if (i < 0 || i >= _coverSets.Length) continue;
                var c = manifest.cells[i];
                bool own = dx == 0 && dz == 0;
                // Neighbours whose content reaches over the hero are gated too (IsReady waits for them anyway).
                if (!own && !ContentCovers(c, pos) && (!c.HasScene || manifest.Distance(c, pos) > LoadDistanceOf(c) ||
                             (cam != null && !GeometryUtility.TestPlanesAABB(_frustum, CellBounds(c))))) continue;
                foreach (int k in _coverSets[i]) if (!result.Contains(k)) result.Add(k);
            }
        }

        /// <summary>True when the cell is loaded and its deferred bulk content (NPCs, trees) is awake.</summary>
        public bool IsAwake(int cellIndex) =>
            _slots.TryGetValue(cellIndex, out var s) && s.state == State.Loaded && (s.deferred == null || s.deferred.Done);

        bool BeginLoad(int index, G3WorldManifest.Cell c)
        {
            AsyncOperation op = null;
            if (SceneUtility.GetBuildIndexByScenePath(c.scenePath) >= 0)
                op = SceneManager.LoadSceneAsync(c.scenePath, LoadSceneMode.Additive);
#if UNITY_EDITOR
            if (op == null)
                op = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(c.scenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#endif
            if (op == null)
            {
                Debug.LogWarning($"G3WorldStreamer: cannot load {c.scenePath} (not in Build Settings?)");
                _slots[index] = new Slot { state = State.Loaded };   // don't retry every frame
                return false;
            }
            var slot = new Slot { state = State.Loading };
            _slots[index] = slot;
            op.completed += _ =>
            {
                if (this == null) return;
                slot.state = State.Loaded;
                _backdropDirty = true;
                var scene = SceneManager.GetSceneByPath(c.scenePath);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        if (root.TryGetComponent(out slot.deferred)) break;
                if (Application.isPlaying) MakeResident(index, c, scene);
                try { CellLoaded?.Invoke(c, scene); } catch (Exception ex) { Debug.LogException(ex); }
                if (slot.unloadWhenLoaded) BeginUnload(index, c);
            };
            return true;
        }

        void BeginUnload(int index, G3WorldManifest.Cell c)
        {
            var scene = SceneManager.GetSceneByPath(c.scenePath);
            if (!scene.IsValid() || !scene.isLoaded) { _slots.Remove(index); _backdropDirty = true; return; }
            var slot = _slots[index];
            slot.state = State.Unloading;
            // Show the low-poly piece before the detail disappears.
            _backdropDirty = true;
            RefreshBackdropPlay();
            var op = SceneManager.UnloadSceneAsync(scene);
            if (op == null) { _slots.Remove(index); return; }
            op.completed += _ =>
            {
                if (this == null) return;
                _slots.Remove(index);
                try { CellUnloaded?.Invoke(c); } catch (Exception ex) { Debug.LogException(ex); }
                if (unloadAssetsEvery > 0 && ++_unloadsSinceCleanup >= unloadAssetsEvery) CleanUpAssetsIfQuiet();
            };
        }

        /// <summary>
        /// perf-5: Resources.UnloadUnusedAssets after <see cref="unloadAssetsEvery"/> cell unloads, but only when nobody sees
        /// the hitch (the loading screen, a teleport waiting for ground, the game paused under a menu or a loading hold) or once memory is really
        /// short (<see cref="UnloadAssetsLimitBytes"/>). It frees little (~200-260 assets per call in the owner's log), the cost
        /// is the mark pass over all loaded objects.
        /// </summary>
        void CleanUpAssetsIfQuiet()
        {
            if (unloadAssetsEvery <= 0 || _unloadsSinceCleanup < unloadAssetsEvery) return;
            // the streamer's own hold (teleport: waiting for ground), the start-up loading screen, or the game paused (menus,
            // Myrtana's loading holds); not other holders such as the perf probes riding the player
            bool quiet = Time.timeScale == 0f || _hold.IsHeldBy(this) ||
                         (TryGetComponent(out G3StartupLoader loader) && _hold.IsHeldBy(loader));
            if (!quiet && UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() <= UnloadAssetsLimitBytes) return;
            _unloadsSinceCleanup = 0;
            G3GpuDiag.Event("UnloadUnusedAssets (streamer)");
            G3HitchLog.Mark(quiet ? "UnloadUnusedAssets (quiet)" : "UnloadUnusedAssets (memory limit, in play)");   // fix-251
            Resources.UnloadUnusedAssets();
        }

        /// <summary>Used memory above which the asset cleanup runs during play too.</summary>
        public long UnloadAssetsLimitBytes =>
            unloadAssetsAboveMB <= 0 ? 0 : System.Math.Max(unloadAssetsAboveMB, (long)(SystemInfo.systemMemorySize * unloadAssetsSystemShare)) * 1048576L;

        /// <summary>True if the cell under <paramref name="worldPos"/>, every cell whose terrain covers it and every
        /// neighbour whose content reaches over it are loaded (e.g. for the player to wait before enabling gravity after a
        /// teleport).</summary>
        public bool IsReady(Vector3 worldPos)
        {
            if (manifest == null) return true;
            if (_coverSets == null) BuildCoverSets();
            var c = manifest.CellOf(worldPos);
            int i = manifest.Index(c.x, c.y);
            if (i < 0 || i >= _coverSets.Length) return true;
            foreach (int k in _coverSets[i])
                if (!IsLoaded(k)) return false;
            // A building belongs to the cell of its AABB centre, so a house standing over this square can come from a
            // neighbour that is no terrain coverer. Releasing the hero before it is loaded dropped him onto the terrain
            // under the house floor, and the floor then streamed in around his legs.
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int k = manifest.Index(c.x + dx, c.y + dz);
                if (k < 0 || k >= manifest.cells.Count) continue;
                if (ContentCovers(manifest.cells[k], worldPos) && !IsLoaded(k)) return false;
            }
            return true;
        }

        /// <summary>True if the cell has a scene and its content AABB (metres) contains the position horizontally.</summary>
        static bool ContentCovers(G3WorldManifest.Cell c, Vector3 p)
        {
            if (!c.HasScene || c.contentBounds.size.sqrMagnitude <= 0) return false;
            var b = c.contentBounds;
            return p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;
        }

        public bool IsLoaded(int cellIndex)
        {
            if (Application.isPlaying)
                return _slots.TryGetValue(cellIndex, out var s) && s.state == State.Loaded;
            var c = manifest.cells[cellIndex];
            return c.HasScene && SceneManager.GetSceneByPath(c.scenePath).isLoaded;
        }

        /// <summary>Freezes / releases <see cref="holdUntilReady"/> until the cells under it are loaded (released
        /// automatically then).</summary>
        public void Hold(bool hold) => Hold(this, hold);

        /// <summary>Freezes <see cref="holdUntilReady"/> on behalf of <paramref name="owner"/> (e.g. the loading screen);
        /// the player moves again once every owner has released it.</summary>
        public void Hold(object owner, bool hold) => _hold.Set(holdUntilReady, owner, hold, this);

        // ------------------------------------------------------------------ backdrop

        void BuildCoverSets()
        {
            _coverSets = new int[manifest.cells.Count][];
            for (int i = 0; i < manifest.cells.Count; i++)
            {
                var c = manifest.cells[i];
                var set = new List<int>();
                if (c.HasScene) set.Add(i);
                if (c.coverers != null)
                    foreach (int k in c.coverers)
                        if (k != i && k >= 0 && k < manifest.cells.Count && manifest.cells[k].HasScene && !set.Contains(k)) set.Add(k);
                _coverSets[i] = set.ToArray();
            }
        }

        void RefreshBackdropPlay() => RefreshBackdrop(i => _slots.TryGetValue(i, out var s) && s.state == State.Loaded);

        void RefreshBackdrop(Func<int, bool> loaded)
        {
            _backdropDirty = false;
            if (backdrop == null) return;
            backdrop.Refresh(i =>
            {
                if (i < 0 || i >= _coverSets.Length) return false;
                var set = _coverSets[i];
                if (set.Length == 0) return false;
                foreach (int k in set) if (!loaded(k)) return false;
                return true;
            });
        }

        // ------------------------------------------------------------------ weather

        public string WeatherFor(string region)
        {
            if (string.IsNullOrEmpty(region)) return null;
            foreach (var r in regionWeather)
                if (string.Equals(r.region, region, StringComparison.OrdinalIgnoreCase)) return r.weatherState;
            return null;
        }

        void UpdateWeather()
        {
            if (!switchWeatherByRegion) return;
            if (environment == null) { environment = FindAnyObjectByType<G3Environment>(); if (environment == null) return; }
            var region = manifest.RegionAt(Centre);
            if (region != null) CurrentRegion = region;
            var want = WeatherFor(CurrentRegion);
            if (want == null) return;

            if (_weatherTarget == null)
            {
                if (string.Equals(environment.weatherState, want, StringComparison.OrdinalIgnoreCase)) return;
                _weatherTarget = want;
                _weatherT = 0;
                environment.blendState = want;
            }
            else if (!string.Equals(_weatherTarget, want, StringComparison.OrdinalIgnoreCase))
            {
                // Region changed again mid-blend: snap the current blend and start towards the new target.
                if (_weatherT >= 0.5f) environment.weatherState = _weatherTarget;
                _weatherTarget = want;
                _weatherT = 0;
                environment.blendState = want;
            }

            _weatherT += Time.deltaTime / Mathf.Max(0.01f, weatherBlendSeconds);
            if (_weatherT >= 1f)
            {
                environment.weatherState = _weatherTarget;
                environment.blendState = "";
                environment.blend = 0;
                _weatherTarget = null;
            }
            else environment.blend = Mathf.SmoothStep(0, 1, _weatherT);
        }
    }
}
