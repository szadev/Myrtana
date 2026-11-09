using System;
using System.Collections.Generic;
using G3.Audio;
using G3.Weather;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// G3's weather (eCWeatherAdmin, Docs/formats-weather.md §7): every weather environment (Myrtana, Varant, Nordmar, Cave …)
    /// runs its own <see cref="WeatherScheduler"/> on the game clock; the weather zones (eCWeatherZone_PS, from the baked
    /// audio world) around the player weight the environments; the resulting state weights drive <see cref="G3Environment"/>
    /// (light, fog, sky; the two heaviest states), the precipitation (rain, snow: the state above 50 %, scaled by
    /// (w − 0.5)·2, as RainAndSnow.tga particles in a 10 m cube around the camera) and the CP 1.75 loop sounds (rain,
    /// snow-storm and sand-storm wind). <see cref="Force"/> pins a state for testing.
    /// Drops fall only where G3's traced spawn columns reach (<see cref="PrecipitationColumns"/>: one downward ray per
    /// column from high above, cached): under a roof they end on the roof, so there is no rain indoors, and a drop that
    /// lands in view leaves a splash (SplashDuration, SplashTextureTile, SplashSizeScale). The schedules are saved
    /// (<see cref="CaptureSchedule"/> / <see cref="RestoreSchedule"/>).
    /// fix-130 (owner: rain and wet walls inside a mill): the columns are traced again every few seconds (a building
    /// that streamed in after its columns were traced let rain through its roof for good), each trace takes the nearest
    /// hit from above (a full hit buffer dropped hits), streaks and splashes are kept above the surface they end on, a
    /// top-down rain shadow (<see cref="RainShadowGrid"/>, shader globals _G3RainShadow / _G3RainShadowParams) keeps
    /// surfaces under roofs dry, and the rain loops are muffled while the camera is under a roof [interp].
    /// fix-215 (owner: in Nordmar west of the Hammer clan the weather switched at a visible line): the zones are layered
    /// (<see cref="WeatherZoneBlend"/>: the smaller zone over the bigger one, the rest to the default environment) instead of
    /// summed, so a region's own zones cover it fully and region borders blend over G3's inner→outer ramp; the resulting
    /// state weights are cross-faded in time (<see cref="WeightCrossfade"/>, <see cref="crossfadeSeconds"/>; a teleport or a
    /// loaded save snaps), and every state goes to <see cref="G3Environment.SetStateMix"/> instead of only the two heaviest
    /// (light, fog, sky, clouds, wind, wetness and the remaster snow follow the whole mix).
    /// fix-224 (owner: thick, bright, blobby rain): drops, flakes and splashes are drawn by G3Precipitation.shader
    /// (<see cref="PrecipitationLook"/>): rain as thin ribbons along the velocity (length from the fall speed), never
    /// thinner than a pixel (alpha = coverage), faded out within ~2 m of the camera, lit by the ambient and the scene lights.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class G3WeatherDirector : MonoBehaviour
    {
        public G3Environment environment;
        public G3AudioContext audioContext;
        [Tooltip("Environment used where no weather zone applies")] public string defaultEnvironment = "Myrtana";
        public float zoneInterval = 0.5f;
        [Tooltip("fix-215: seconds for a full cross-fade of one weather for another (zone edges, cave entrances, a forced state)")]
        public float crossfadeSeconds = 6f;
        [Tooltip("fix-215: the weather snaps instead of fading when the player moved this far between two zone updates (teleport)")]
        public float snapDistance = 150f;
        [Tooltip("Precipitation particles at most (eCPrecipitation_PS MaxParticles 3000)")] public int maxParticles = 3000;
        [Tooltip("Side of the precipitation cube around the camera, m (PrecipitationCubeSize 1000 cm)")] public float cubeSize = 10f;
        [Range(0, 1)] public float loopVolume = 0.8f;
        [Tooltip("Width of a traced rain column (m) [interp: G3 MaxSpawnPoints over the cube]")] public float columnSize = 0.5f;
        [Tooltip("Columns traced per frame at most")] public int tracesPerFrame = 48;
        [Tooltip("Rays start this high above the camera (G3: 20 cubes = 200 m)")] public float traceHeight = 80f;
        [Tooltip("A traced rain column is traced again after this many seconds (fix-130: buildings that stream in later)")] public float columnMaxAge = 3f;
        [Header("Rain shadow (fix-130)")]
        [Tooltip("Cells of the top-down rain shadow around the camera (per side)")] public int shadowSize = 96;
        [Tooltip("Cell of the rain shadow, m")] public float shadowCell = 1f;
        [Tooltip("Rain-shadow cells traced per frame while it rains or surfaces are wet")] public int shadowTracesPerFrame = 96;
        [Tooltip("Rain loops muffled while the camera is under a roof [interp]")] public bool muffleIndoors = true;
        [Range(0, 1)] public float indoorVolume = 0.55f;
        public float indoorCutoff = 1800f;
        public bool log;

        [Header("Current (read only)")]
        [SerializeField] string weights;
        [SerializeField] string precipitation;

        /// <summary>Days since the game start (continuous); null = advanced from the environment's hour.</summary>
        public Func<double> DayTimeSource;

        readonly Dictionary<string, WeatherScheduler> _schedulers = new Dictionary<string, WeatherScheduler>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, float> _envWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, float> _stateWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, AudioSource> _loops = new Dictionary<string, AudioSource>(StringComparer.OrdinalIgnoreCase);
        readonly System.Random _rng = new System.Random();
        double _dayTime = 1;
        float _lastHour = -1, _nextZones;
        string _forced;
        readonly WeightCrossfade _fade = new WeightCrossfade(6f);
        readonly List<ZoneWeight> _zoneList = new List<ZoneWeight>();
        readonly List<KeyValuePair<string, float>> _mixList = new List<KeyValuePair<string, float>>();
        bool _snap = true, _hadFocus;
        Vector3 _lastFocus;
        ParticleSystem _rain, _splash;
        Material _rainMat, _splashMat;   // fix-224: G3Precipitation instances (null: the shared G3/Particle material)
        PrecipitationLook _look;
        string _rainState;
        G3WeatherState _rainDef;
        PrecipitationColumns _columns;
        readonly SplashQueue _splashes = new SplashQueue(1024);
        readonly List<(float x, float y, float z)> _dueSplashes = new List<(float, float, float)>();
        readonly RaycastHit[] _traceHits = new RaycastHit[6];
        float _emitCarry, _nextPrune, _dropHalf, _splashHalf, _indoors;
        RainShadowGrid _shadow;
        Texture2D _shadowTex;
        bool _shadowOn;
        AudioLowPassFilter _muffle;
        static readonly int RainShadowId = Shader.PropertyToID("_G3RainShadow");
        static readonly int RainShadowParamsId = Shader.PropertyToID("_G3RainShadowParams");
        List<WeatherScheduleSnapshot> _pendingRestore;
        IReadOnlyDictionary<string, float> _current = new Dictionary<string, float>();
        readonly Dictionary<string, float> _normalized = new Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
        string _precipName;
        int _precipRate = -1;

        public static G3WeatherDirector Instance { get; private set; }
        public IReadOnlyDictionary<string, float> StateWeights => _current;
        /// <summary>Precipitation and splash particles alive (diagnostics).</summary>
        public int PrecipitationParticles => (_rain != null ? _rain.particleCount : 0) + (_splash != null ? _splash.particleCount : 0);
        /// <summary>The top-down rain shadow (null before the first rain; dev probes).</summary>
        public RainShadowGrid RainShadow => _shadow;
        /// <summary>0 open .. 1 under a roof at a world point (null before the first rain; dev probes).</summary>
        public float? RainShelterAt(Vector3 p) => _shadow != null ? _shadow.ShelterAt(p.x, p.y, p.z) : (float?)null;
        /// <summary>Rain-shadow cells traced since it was placed (dev probes).</summary>
        public int RainShadowTraced => _shadow != null ? _shadow.Traced : 0;
        /// <summary>Whether the rain shadow is applied to the materials now.</summary>
        public bool RainShadowOn => _shadowOn;

        /// <summary>Keeps the rain shadow traced in dry weather too (remaster snow: no snow on floors under roofs, G3SnowV2).</summary>
        public static bool ShelterWanted;
        /// <summary>0..1: the camera is under a roof (rain loops muffled).</summary>
        public float Indoors => _indoors;
        /// <summary>Rain columns cached (dev probes).</summary>
        public int RainColumns => _columns != null ? _columns.Cached : 0;

        /// <summary>Highest static surface over (x, z) as the rain sees it (null: none; dev probes and tests).</summary>
        public float? RainSurface(float x, float z) => Trace(x, z);

        /// <summary>The drops alive inside the box (dev probes: rain under a roof).</summary>
        public int DropsInside(Bounds box)
        {
            if (_rain == null) return 0;
            if (_dropBuf == null || _dropBuf.Length < _rain.main.maxParticles) _dropBuf = new ParticleSystem.Particle[_rain.main.maxParticles];
            int n = _rain.GetParticles(_dropBuf), k = 0;
            for (int i = 0; i < n; i++) if (box.Contains(_dropBuf[i].position)) k++;
            return k;
        }

        ParticleSystem.Particle[] _dropBuf;

        /// <summary>Every environment's schedule (save games).</summary>
        public List<WeatherScheduleSnapshot> CaptureSchedule()
        {
            if (_schedulers.Count == 0 && _pendingRestore != null) return new List<WeatherScheduleSnapshot>(_pendingRestore);
            var list = new List<WeatherScheduleSnapshot>();
            foreach (var s in _schedulers.Values) if (s.Current >= 0) list.Add(s.Capture());
            return list;
        }

        /// <summary>The schedules as one save string (<see cref="WeatherScheduleSnapshot.Encode"/>).</summary>
        public string SaveSchedule() => WeatherScheduleSnapshot.Encode(CaptureSchedule());

        /// <summary>Restores <see cref="SaveSchedule"/>'s string.</summary>
        public void LoadSchedule(string saved) => RestoreSchedule(WeatherScheduleSnapshot.Decode(saved));

        /// <summary>Restores saved schedules (applied once the schedulers exist); environments not listed pick anew.</summary>
        public void RestoreSchedule(IEnumerable<WeatherScheduleSnapshot> saved)
        {
            _pendingRestore = saved != null ? new List<WeatherScheduleSnapshot>(saved) : null;
            ApplyPendingRestore();
        }

        void ApplyPendingRestore()
        {
            if (_pendingRestore == null || _schedulers.Count == 0) return;
            foreach (var snap in _pendingRestore)
                if (snap.Environment != null && _schedulers.TryGetValue(snap.Environment, out var s) && !s.Restore(snap) && log)
                    Debug.Log($"[G3Weather] saved weather of {snap.Environment} not restored ({snap.Current} unknown)");
            _pendingRestore = null;
            _snap = true;
            _splashes.Clear();
            if (_rain != null) _rain.Clear();
        }

        /// <summary>
        /// Pins the weather to one state (e.g. "Rainy", "Foggy", "Snowy(Nordmar)"); null or "" = back to the schedule.
        /// Instant by default (dev scenarios look right away); <paramref name="fade"/> cross-fades it like a zone change.
        /// </summary>
        public static void Force(string state, bool fade = false)
        {
            if (Instance == null) return;
            Instance._forced = string.IsNullOrEmpty(state) ? null : state;
            if (!fade) Instance._snap = true;
            Debug.Log($"[G3Weather] forced: {(Instance._forced ?? "off")}");
        }

        static WeatherMode? _mode;

        /// <summary>
        /// weather-opt: the Remaster page's Weather row (<see cref="RemasterOptions.WeatherPrefKey"/>, env
        /// <see cref="RemasterOptions.WeatherEnvVar"/>; default Original = G3). Fair: <see cref="WeatherFair"/>.
        /// </summary>
        public static WeatherMode Mode
        {
            get
            {
                if (_mode == null)
                {
                    int stored = 0;
                    try { stored = PlayerPrefs.GetInt(RemasterOptions.WeatherPrefKey, 0); } catch (Exception) { }
                    _mode = (WeatherMode)RemasterOptions.WeatherMode(System.Environment.GetEnvironmentVariable(RemasterOptions.WeatherEnvVar), stored);
                }
                return _mode.Value;
            }
        }

        /// <summary>Sets (and stores) the Weather mode; the running schedulers use it from their next draw on (a running fade
        /// is not changed).</summary>
        public static void SetMode(WeatherMode mode, bool persist = true)
        {
            _mode = mode;
            if (persist) { PlayerPrefs.SetInt(RemasterOptions.WeatherPrefKey, (int)mode); PlayerPrefs.Save(); }
            if (Instance != null) foreach (var s in Instance._schedulers.Values) s.Mode = mode;
            Debug.Log($"[G3Weather] weather mode: {mode}");
        }

        /// <summary><see cref="Mode"/> as 0 Original / 1 Fair (the menu page, which does not reference G3.Weather.Logic).</summary>
        public static int ModeOption => (int)Mode;

        /// <summary><see cref="SetMode"/> from the menu row (0 Original / 1 Fair).</summary>
        public static void SetModeOption(int option) => SetMode(option == 1 ? WeatherMode.Fair : WeatherMode.Original);

        /// <summary>Adds the director next to the scene's G3Environment (GameBootstrap / tools).</summary>
        public static G3WeatherDirector Ensure()
        {
            if (Instance != null) return Instance;
            var env = FindAnyObjectByType<G3Environment>();
            if (env == null || env.weather == null) return null;
            var d = env.gameObject.GetComponent<G3WeatherDirector>() ?? env.gameObject.AddComponent<G3WeatherDirector>();
            d.environment = env;
            return d;
        }

        void OnEnable() => Instance = this;
        void OnDisable()
        {
            if (Instance == this) Instance = null;
            if (environment != null) environment.SetStateMix(null);
            _snap = true;
        }

        void Start()
        {
            if (environment == null) environment = FindAnyObjectByType<G3Environment>();
            if (audioContext == null) audioContext = FindAnyObjectByType<G3AudioContext>();
            if (environment == null || environment.weather == null) { enabled = false; return; }
            foreach (var e in environment.weather.environments)
            {
                var chances = new List<WeatherChance>();
                foreach (var p in e.probabilities) chances.Add(new WeatherChance(p.state, p.probability, p.minDuration, p.maxDuration));
                _schedulers[e.name] = new WeatherScheduler(e.name, chances, () => (float)_rng.NextDouble()) { Mode = Mode };
            }
            // The region switch of the streamer would fight over G3Environment.weatherState.
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            if (streamer != null) streamer.switchWeatherByRegion = false;
            _lastHour = environment.timeOfDay;
            ApplyPendingRestore();
        }

        void Update()
        {
            if (environment == null) return;
            if (DayTimeSource != null) _dayTime = DayTimeSource();
            else
            {
                _dayTime = WeatherRules.AdvanceDayTime(_dayTime, _lastHour < 0 ? environment.timeOfDay : _lastHour, environment.timeOfDay);
                _lastHour = environment.timeOfDay;
            }
            if (Time.unscaledTime >= _nextZones) { _nextZones = Time.unscaledTime + zoneInterval; UpdateZones(); }

            // fix-236: every environment runs on the clock, as eCWeatherAdmin::Process does (CP 1.75 Engine.dll 0x1028d330:
            // all environments, every frame), not only those with a zone at the player — a region's chain no longer freezes
            // while the hero is elsewhere and does not resume from its frozen state when he comes back.
            foreach (var sch in _schedulers.Values) sch.Update(_dayTime);
            _stateWeights.Clear();
            if (_forced != null) _stateWeights[_forced] = 1f;
            else
                foreach (var kv in _envWeights)
                    if (_schedulers.TryGetValue(kv.Key, out var s))
                        s.AddWeights(kv.Value, _stateWeights);
            var target = WeatherRules.Normalize(_stateWeights, _normalized); // reused buffer: Update runs every frame (perf-7)
            if (target.Count == 0) return;
            // fix-215: cross-faded in time; the first frame, a teleport, a loaded save or an instant Force snap
            _fade.FadeSeconds = crossfadeSeconds;
            var w = _snap ? _fade.Snap(target) : _fade.Step(target, Mathf.Min(Time.unscaledDeltaTime, 0.25f));
            _snap = false;
            _current = w;
            WeatherRules.TopTwo(w, out var a, out var b, out var blend);
            environment.weatherState = a;
            environment.blendState = b ?? "";
            environment.blend = b != null ? blend : 0f;
            WeatherMix.ToList(w, _mixList);
            environment.SetStateMix(_mixList);
            UpdatePrecipitation(w);
            UpdateRainShadow();
            UpdateLoops(w);
        }

        void UpdateZones()
        {
            _envWeights.Clear();
            _zoneList.Clear();
            var zones = audioContext != null && audioContext.world != null ? audioContext.world.zones : null;
            var cam = Camera.main;
            var p = audioContext != null && audioContext.FocusPosition != Vector3.zero ? audioContext.FocusPosition : cam != null ? cam.transform.position : Vector3.zero;
            // a jump (teleport, map travel, a loaded save) snaps the weather instead of fading it in over seconds
            if (_hadFocus && (p - _lastFocus).sqrMagnitude > snapDistance * snapDistance) _snap = true;
            _lastFocus = p;
            _hadFocus = true;
            if (zones != null)
            {
                var v = new V3(p.x, p.y, p.z);
                foreach (var z in zones)
                {
                    if (z == null || !z.enabled || string.IsNullOrEmpty(z.weatherEnvironment) || !_schedulers.ContainsKey(z.weatherEnvironment)) continue;
                    float f = z.BlendFactor(v);
                    if (f <= 0) continue;
                    _zoneList.Add(new ZoneWeight(z.weatherEnvironment, f, z.Size));
                }
            }
            // fix-215: layered (smallest zone first), not summed: see WeatherZoneBlend
            WeatherZoneBlend.Layer(_zoneList, _envWeights, _schedulers.ContainsKey(defaultEnvironment) ? defaultEnvironment : null);
            if (log || Application.isEditor)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _envWeights)
                {
                    var s = _schedulers[kv.Key];
                    sb.Append($"{kv.Key} {kv.Value:0.##}: {s.CurrentState}→{s.NextState} {s.Progress:0.00} ({s.Duration * 24:0.#} h); ");
                }
                weights = sb.ToString();
                if (log) Debug.Log("[G3Weather] " + weights);
            }
        }

        // ------------------------------------------------------------------ precipitation

        void UpdatePrecipitation(IReadOnlyDictionary<string, float> w)
        {
            float rate = 0;
            G3WeatherState state = null;
            if (WeatherRules.Dominant(w, out var name, out var k))
            {
                state = environment.weather.GetState(name);
                if (state != null && state.particlesPerSecond > 0) rate = state.particlesPerSecond * k;
            }
            // Inspector readout; formatted only when it changes instead of every frame (perf-7).
            int shown = rate > 0 ? Mathf.RoundToInt(rate) : -1;
            if (shown != _precipRate || (shown >= 0 && name != _precipName))
            {
                _precipRate = shown;
                _precipName = name;
                precipitation = shown >= 0 ? $"{name} {rate:0}/s" : "-";
            }
            if (rate <= 0 && _rain == null) return;
            var ps = Rain();
            if (ps == null) return;
            if (state != null && rate > 0 && _rainState != name) Configure(ps, state);
            var cam = Camera.main;
            if (cam == null) return;
            Emit(ps, cam.transform.position, rate);
            EmitSplashes();
        }

        /// <summary>
        /// G3-style emission: drops start at the top of the cube over a traced column and live until they reach the column's
        /// hit (roof, ground: a splash there) or the cube's floor; columns under a ceiling get none.
        /// </summary>
        void Emit(ParticleSystem ps, Vector3 cam, float rate)
        {
            if (_columns == null || _columns.CellSize != columnSize) _columns = new PrecipitationColumns(columnSize, Trace, tracesPerFrame, columnMaxAge);
            _columns.BeginFrame(Time.unscaledTimeAsDouble);
            if (Time.unscaledTime >= _nextPrune)
            {
                _nextPrune = Time.unscaledTime + 1f;
                _columns.Prune(cam.x, cam.z, cubeSize);
            }
            if (rate <= 0 || _rainDef == null) { _emitCarry = 0; return; }
            float want = rate * Time.deltaTime + _emitCarry;
            int n = Mathf.Min((int)want, Mathf.Max(1, maxParticles / 8));
            _emitCarry = Mathf.Min(want - n, 50f);
            float half = cubeSize * 0.5f, top = cam.y + half, bottom = cam.y - half;
            float vMin = Mathf.Max(0.3f, _rainDef.speedMinMax.x * 0.01f), vMax = Mathf.Max(vMin, _rainDef.speedMinMax.y * 0.01f);
            bool splashes = _rainDef.splashDuration > 0 && _splash != null;
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < n; i++)
            {
                float x = cam.x + UnityEngine.Random.Range(-half, half), z = cam.z + UnityEngine.Random.Range(-half, half);
                if (!_columns.TryGround(x, z, out float hitY)) continue;
                float speed = UnityEngine.Random.Range(vMin, vMax);
                float life = PrecipitationColumns.FallSeconds(top, hitY, bottom, speed, _dropHalf, out bool splash);
                if (life <= 0.02f) continue;
                ep.position = new Vector3(x, top, z);
                ep.velocity = new Vector3(0, -speed, 0);
                ep.startLifetime = life;
                ps.Emit(ep, 1);
                // the splash billboard is centred on its position: lifted by half its height, it stays above the roof
                if (splash && splashes) _splashes.Add(Time.timeAsDouble + life, x, hitY + 0.02f + _splashHalf, z);
            }
        }

        void EmitSplashes()
        {
            if (_splash == null || !_splash.gameObject.activeSelf) return;
            _dueSplashes.Clear();
            if (_splashes.TakeDue(Time.timeAsDouble, _dueSplashes) == 0) return;
            var ep = new ParticleSystem.EmitParams();
            foreach (var p in _dueSplashes)
            {
                ep.position = new Vector3(p.x, p.y, p.z);
                _splash.Emit(ep, 1);
            }
        }

        /// <summary>
        /// The highest static surface under (x, z) from high above (characters and moving bodies are ignored): the nearest
        /// hit from above, stepping past skipped colliders. fix-130: a multi-hit query with a 6-hit buffer kept an arbitrary
        /// subset of the hits in a column through a building (two-sided roofs, floors, crates) and could miss the roof.
        /// </summary>
        float? Trace(float x, float z)
        {
            var cam = Camera.main;
            float from = (cam != null ? cam.transform.position.y : 0) + traceHeight;
            var origin = new Vector3(x, from, z);
            float left = traceHeight * 2.5f;
            for (int i = 0; i < 8 && left > 0; i++)
            {
                if (!Physics.Raycast(origin, Vector3.down, out var hit, left, ~0, QueryTriggerInteraction.Ignore)) return null;
                var c = hit.collider;
                if (c != null && !(c is CharacterController) && c.attachedRigidbody == null) return hit.point.y;
                float d = hit.distance + 0.02f;
                origin.y -= d;
                left -= d;
            }
            return null;
        }

        /// <summary>
        /// The top-down rain shadow (fix-130) while it rains or surfaces are still wet: traced round the camera from a
        /// per-frame budget, uploaded as a small float texture the v2 materials read (no wetness under a roof).
        /// </summary>
        static readonly global::Unity.Profiling.ProfilerMarker PmRainShadow = new global::Unity.Profiling.ProfilerMarker("G3WeatherDirector.RainShadow");
        void UpdateRainShadow()
        {
            using var _pm = PmRainShadow.Auto();   // snow3 perf
            bool want = _precipRate > 0 || G3WorldV2.Wetness > 0.001f || ShelterWanted;
            var cam = Camera.main;
            if (!want || cam == null)
            {
                if (_shadowOn) Shader.SetGlobalVector(RainShadowParamsId, Vector4.zero);
                _shadowOn = false;
                _indoors = Mathf.MoveTowards(_indoors, 0f, Time.unscaledDeltaTime * 2f);
                return;
            }
            if (_shadow == null || _shadow.Size != shadowSize || _shadow.CellSize != shadowCell)
            {
                _shadow = new RainShadowGrid(shadowSize, shadowCell);
                if (_shadowTex != null) G3GpuSafe.Destroy(_shadowTex);   // dx12-3: bound globally until replaced below
                _shadowTex = new Texture2D(_shadow.Size, _shadow.Size, TextureFormat.RFloat, false, true)
                {
                    name = "G3 Rain Shadow", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                };
            }
            var p = cam.transform.position;
            _shadow.Recenter(p.x, p.z);
            int budget = Mathf.Max(1, shadowTracesPerFrame);
            if (_precipRate > 0 || G3WorldV2.Wetness > 0.001f) _shadow.Trace(Trace, budget);
            // dry, only the remaster snow wants it (no snow under roofs): new cells once, the rest refreshed 16x slower
            // (remaster-snow pass 2: the full budget every frame was ~0.75 ms of raycasts on open snow fields)
            else if (_shadow.Pending > 0) _shadow.TracePending(Trace, budget);
            else _shadow.Trace(Trace, Mathf.Max(1, budget / 16));
            if (_shadow.TakeDirty())
            {
                _shadowTex.SetPixelData(_shadow.Heights, 0);
                _shadowTex.Apply(false);
                G3GpuDiag.Count("rainshadow.apply");
            }
            Shader.SetGlobalTexture(RainShadowId, _shadowTex);
            // x, z: the grid's lower corner (m), cell (m), cells per side (0 = off)
            Shader.SetGlobalVector(RainShadowParamsId, new Vector4(_shadow.MinX, _shadow.MinZ, _shadow.CellSize, _shadow.Size));
            _shadowOn = true;
            float under = _shadow.HeightAt(p.x, p.z) > p.y + 0.3f ? 1f : 0f;
            _indoors = Mathf.MoveTowards(_indoors, under, Time.unscaledDeltaTime * 2f);
        }

        void OnDestroy()
        {
            if (_shadowOn) Shader.SetGlobalVector(RainShadowParamsId, Vector4.zero);
            if (_shadowTex != null) Destroy(_shadowTex);
            if (_rainMat != null) Destroy(_rainMat);
            if (_splashMat != null) Destroy(_splashMat);
        }

        ParticleSystem Rain()
        {
            if (_rain != null) return _rain;
            var mat = environment.weather.precipitationMaterial;
            if (mat == null) return null;
            var sh = Resources.Load<Shader>("G3Precipitation");
            if (sh != null)
            {
                _rainMat = LookMaterial(sh, mat, "G3 Precipitation (drops)");
                _splashMat = LookMaterial(sh, mat, "G3 Precipitation (splashes)");
            }
            _rain = NewSystem("G3 Precipitation", _rainMat != null ? _rainMat : mat);
            _splash = NewSystem("G3 Precipitation Splashes", _splashMat != null ? _splashMat : mat);
            _rain.Play();
            _splash.Play();
            return _rain;
        }

        static Material LookMaterial(Shader sh, Material src, string name)
        {
            var m = new Material(sh) { name = name, renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent };
            m.mainTexture = src.mainTexture;
            m.mainTextureScale = src.mainTextureScale;   // the DDS flip of G3_Precipitation.mat
            m.mainTextureOffset = src.mainTextureOffset;
            return m;
        }

        static readonly List<ParticleSystemVertexStream> LookStreams = new List<ParticleSystemVertexStream>
        {
            // packed as G3Precipitation.shader reads them: UV + Center.xy | Center.z + Velocity | SizeXYZ
            ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
            ParticleSystemVertexStream.Center, ParticleSystemVertexStream.Velocity, ParticleSystemVertexStream.SizeXYZ,
        };

        static void SetLook(Material m, PrecipitationLook look, int tile)
        {
            m.SetFloat("_Tile", Mathf.Clamp(tile, 0, 3));
            m.SetFloat("_Streak", look.Streak ? 1 : 0);
            m.SetFloat("_Width", look.Width);
            m.SetFloat("_StreakSeconds", look.StreakSeconds);
            m.SetFloat("_MinPixels", look.MinPixels);
            m.SetVector("_NearFade", new Vector4(look.NearFadeStart, look.NearFadeEnd, 0, 0));
            m.SetFloat("_Opacity", look.Opacity);
        }

        ParticleSystem NewSystem(string name, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.playOnAwake = false;
            main.loop = true;
            main.startColor = Color.white;
            main.startSpeed = 0;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var em = ps.emission;
            em.enabled = false; // emitted by Emit / EmitSplashes over traced columns
            var shape = ps.shape;
            shape.enabled = false;
            bool look = mat == _rainMat || mat == _splashMat;
            var sheet = ps.textureSheetAnimation;
            sheet.enabled = !look;   // G3Precipitation picks the tile itself from corner UVs
            sheet.numTilesX = 2;
            sheet.numTilesY = 2;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            if (look)
            {
                r.renderMode = ParticleSystemRenderMode.Billboard;   // the shader rebuilds the quad (ribbon or billboard)
                r.SetActiveVertexStreams(LookStreams);
                r.maxParticleSize = 10f;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        /// <summary>The dominant state's eCWeatherStates precipitation values (cm, cm/s; tile of the 2 × 2 RainAndSnow.tga).</summary>
        void Configure(ParticleSystem ps, G3WeatherState s)
        {
            _rainState = s.name;
            var main = ps.main;
            float vMin = s.speedMinMax.x * 0.01f, vMax = Mathf.Max(vMin, s.speedMinMax.y * 0.01f);
            _rainDef = s;
            main.startSpeed = 0;
            main.startLifetime = cubeSize / Mathf.Max(0.3f, vMin);
            main.startSize = new ParticleSystem.MinMaxCurve(s.size.x * 0.01f, Mathf.Max(s.size.x, s.size.y) * 0.01f);
            // fix-130: half of the drawn drop (a streak of size x DirectionScale is centred on the particle) plus a margin
            float drop = Mathf.Max(s.size.x, s.size.y) * 0.01f;
            _dropHalf = 0.5f * drop * Mathf.Max(1f, s.directionScale) + 0.05f;
            _look = PrecipitationLook.For(Mathf.Max(s.size.x, s.size.y), s.directionScale, s.speedMinMax.x, s.speedMinMax.y);
            if (_rainMat != null)
            {
                SetLook(_rainMat, _look, s.textureTile);
                _dropHalf = Mathf.Max(_look.DropHalf(vMax), 0.5f * _look.Width + 0.05f);
            }
            var vel = ps.velocityOverLifetime;
            vel.enabled = false; // the fall speed is given per drop (Emit)
            ConfigureSplash(s);
            var noise = ps.noise;
            noise.enabled = s.jitterPower > 0;
            noise.strength = s.jitterPower * 0.01f;
            noise.frequency = Mathf.Max(0.05f, s.jitterSpeed * 10f);
            var sheet = ps.textureSheetAnimation;
            // frameOverTime is normalised over the 4 tiles; Unity's tile 0 is top-left like G3's.
            float f = (Mathf.Clamp(s.textureTile, 0, 3) + 0.5f) / 4f;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(f);
            var r = ps.GetComponent<ParticleSystemRenderer>();
            // fix-224: with G3Precipitation the shader builds the ribbon / billboard (renderer stays Billboard)
            if (_rainMat == null && s.directionScale > 0)
            {
                // [inferred] DirectionScale stretches the drop along its fall (rain streaks); snow (0) stays a sprite.
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.lengthScale = s.directionScale;
                r.velocityScale = 0;
            }
            else r.renderMode = ParticleSystemRenderMode.Billboard;
            if (log) Debug.Log($"[G3Weather] look {(_look.Streak ? "streak" : "flake")} width {_look.Width * 1000:0.#} mm length {_look.Length(vMax) * 100:0.#} cm");
            if (log) Debug.Log($"[G3Weather] precipitation {s.name}: {s.particlesPerSecond}/s speed {vMin:0.#}-{vMax:0.#} m/s size {s.size.x} cm tile {s.textureTile}");
        }

        /// <summary>
        /// Splash of a landed drop: tile SplashTextureTile of the sheet, SplashDuration seconds, the drop size x SplashSizeScale
        /// (x width, y height [interp]), growing and fading out [interp].
        /// </summary>
        void ConfigureSplash(G3WeatherState s)
        {
            if (_splash == null) return;
            var main = _splash.main;
            main.startLifetime = Mathf.Max(0.05f, s.splashDuration);
            float size = Mathf.Max(s.size.x, s.size.y) * 0.01f;
            main.startSize3D = true;
            main.startSizeX = size * Mathf.Max(0.1f, s.splashSizeScale.x);
            main.startSizeY = size * Mathf.Max(0.1f, s.splashSizeScale.y);
            _splashHalf = 0.5f * size * Mathf.Max(0.1f, s.splashSizeScale.y);
            main.startSizeZ = 1f;
            if (_splashMat != null) SetLook(_splashMat, PrecipitationLook.Splash(_look), s.splashTextureTile);
            var sheet = _splash.textureSheetAnimation;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve((Mathf.Clamp(s.splashTextureTile, 0, 3) + 0.5f) / 4f);
            var sol = _splash.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.5f, 1, 1f));
            var col = _splash.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
            col.color = g;
            _splash.gameObject.SetActive(s.splashDuration > 0);
            if (_splash.gameObject.activeSelf && !_splash.isPlaying) _splash.Play();
        }

        // ------------------------------------------------------------------ loop sounds (CP 1.75 LoopSample)

        void UpdateLoops(IReadOnlyDictionary<string, float> w)
        {
            // A low-pass filter needs an AudioSource on the same object; before the first loop starts there is none and
            // AddComponent fails (returns null) every frame.
            if ((muffleIndoors || _muffle != null) && (_muffle != null || TryGetComponent<AudioSource>(out _)))
            {
                if (_muffle == null && !TryGetComponent(out _muffle)) _muffle = gameObject.AddComponent<AudioLowPassFilter>();
                if (_muffle != null)
                {
                    float f = muffleIndoors ? _indoors : 0f;
                    _muffle.enabled = f > 0.01f;
                    _muffle.cutoffFrequency = Mathf.Lerp(22000f, indoorCutoff, f);
                }
            }
            foreach (var s in environment.weather.states)
            {
                if (s.loopClip == null) continue;
                w.TryGetValue(s.name, out var weight);
                _loops.TryGetValue(s.name, out var src);
                if (weight <= 0.01f)
                {
                    if (src != null && src.isPlaying) src.Stop();
                    continue;
                }
                if (src == null)
                {
                    src = gameObject.AddComponent<AudioSource>();
                    G3AudioMixer.Route(src, G3AudioBus.Effects);
                    src.clip = s.loopClip;
                    src.loop = true;
                    src.spatialBlend = 0;
                    src.playOnAwake = false;
                    _loops[s.name] = src;
                }
                // [inferred] volume follows the state's blend weight (the CP engine side is not in the analysed DLLs).
                // fix-130 [interp]: quieter and duller under a roof.
                float room = muffleIndoors ? Mathf.Lerp(1f, indoorVolume, _indoors) : 1f;
                src.volume = Mathf.Clamp01(s.loopVolume * weight * loopVolume * G3SoundPool.EffectsVolume * room);
                if (!src.isPlaying) src.Play();
            }
        }
    }
}
