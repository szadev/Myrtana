using System;
using System.Collections.Generic;
using G3.Unity.Characters;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Remaster snow (Docs/remaster-world.md section 12): the "Snow" row of Options → Remaster (default on; it refines the
    /// v2 surfaces, so it shows only while Ground, Rocks or Buildings are on). Sets the globals of the snow terms in
    /// G3/Remaster/WorldV2 (snow BRDF on G3's snow layers, lasting + fresh snow on top faces in Nordmar, glints, blue shade,
    /// sastrugi) and keeps the footprints: every character near the camera leaves a print when a foot or paw is planted
    /// (<see cref="SnowV2.FootPlant"/>; an oval trail for rigs without foot bones), oriented along its walk; the prints are
    /// a ring of analytic shapes plus a toroidal 64 m grid of cells round the camera (<see cref="SnowPrintGrid"/>, uploaded
    /// as two structured buffers; the shader evaluates the shapes: G3RemasterSnowPrints.hlsl). Prints fill in over minutes,
    /// faster while it snows (refill clock). Maths in <see cref="SnowV2"/> (tested). Off = every global zero.
    /// </summary>
    public static class G3SnowV2
    {
        public const string PrefKey = "G3.Remaster.Snow";
        public const string EnvVar = "G3_REMASTER_SNOW";
        /// <summary>fix-178 "Persistent tracks" row (default on).</summary>
        public const string TracksPrefKey = "G3.Remaster.SnowTracks";
        public const string TracksEnvVar = "G3_REMASTER_SNOWTRACKS";

        static readonly int SnowId = Shader.PropertyToID("_G3Snow");
        static readonly int Snow2Id = Shader.PropertyToID("_G3Snow2");
        static readonly int Snow3Id = Shader.PropertyToID("_G3Snow3");
        static readonly int Snow4Id = Shader.PropertyToID("_G3Snow4");
        static readonly int TrailId = Shader.PropertyToID("_G3SnowTrail");
        static readonly int PrintsId = Shader.PropertyToID("_G3SnowPrints");
        static readonly int CellsId = Shader.PropertyToID("_G3SnowCells");
        static readonly int RegionTexId = Shader.PropertyToID("_G3SnowRegionTex");
        static readonly int RegionId = Shader.PropertyToID("_G3SnowRegion");
        static readonly int AlbedoId = Shader.PropertyToID("_G3SnowAlbedo");
        static readonly int ScanId = Shader.PropertyToID("_G3SnowScan");
        static readonly int ScanNormalId = Shader.PropertyToID("_G3SnowScanNormal");
        static readonly int TexParamsId = Shader.PropertyToID("_G3SnowTex");

        static bool? _option;
        static bool _active;    // the snow look (Snow row on, v2 surfaces)
        static bool _running;   // remaster-sand: the driver and the print grid (snow look or sand on, G3SandV2)
        static float _fresh, _snowfall;
        static double _clock;
        static Driver _driver;
        static string _state = "", _blendState = "";
        static float _blend;
        static IReadOnlyList<KeyValuePair<string, float>> _mix;   // fix-215: G3Environment.StateMix (live list)

        // prints (SnowPrintGrid on the CPU, two structured buffers on the GPU)
        static SnowPrintGrid _grid;
        static ComputeBuffer _printBuf, _cellBuf, _dummyPrints, _dummyCells;
        static readonly List<int> _dirtyCells = new List<int>(), _dirtyPrints = new List<int>();
        struct PrintGpu { public Vector4 a, b, c; }                        // G3SnowPrintData (48 bytes)
        struct CellGpu { public uint s0, s1, s2, s3, s4, s5, s6, s7; }   // G3SnowCell (32 bytes)
        static PrintGpu[] _printStage = new PrintGpu[64];
        static CellGpu[] _cellStage = new CellGpu[64];
        static Texture2D _region;
        static Vector4 _regionParams;
        static byte[] _regionPx, _desertPx, _desertCorePx;   // R snow (Nordmar), G desert (Varant; remaster-sand); grade-1: G undilated
        static int _regionW, _regionH;

        // fix-178 persistent tracks: every print in a snow region, world-wide (SnowTrackStore), fed into the GPU window
        static bool? _tracksOption;
        static SnowTrackStore _store;
        static int[] _ringOwner;                     // GPU window ring index -> store index (-1 none)
        static readonly List<int> _near = new List<int>();
        static readonly List<(int index, int window)> _expired = new List<(int, int)>();
        static int _feedAt;                          // next entry of _near to feed
        static Vector3 _feedCentre = new Vector3(float.MaxValue, 0, float.MaxValue);
        static float _feedTime = -1;
        static readonly List<(float x, float z, float dx, float dz, float side)> _steps = new List<(float, float, float, float, float)>();
        static bool _tracksMode;                     // the mode the current window was built in

        /// <summary>The stored Snow row (default on: a refinement of the v2 surfaces); env G3_REMASTER_SNOW, then the world variables.</summary>
        public static bool Option
        {
            get
            {
                if (_option == null)
                {
                    bool stored = true;
                    try { stored = PlayerPrefs.GetInt(PrefKey, 1) == 1; } catch (Exception) { }
                    var chain = new[] { EnvVar, WorldRemasterSwitch.EnvVar, RemasterOptions.SharedEnvVar, WorldRemasterSwitch.SharedEnvVar };
                    var values = new string[chain.Length];
                    for (int i = 0; i < chain.Length; i++) values[i] = Environment.GetEnvironmentVariable(chain[i]);
                    _option = RemasterOptions.Resolve(values, stored);
                }
                return _option.Value;
            }
        }

        /// <summary>True while the snow terms are applied (option on, world v2 on, a v2 surface part on, play mode).</summary>
        public static bool Active => _active;

        /// <summary>The stored "Persistent tracks" row (fix-178, default on): prints stay until snowfall covers them (or
        /// <c>snowTrackDays</c>), world-wide and saved; off = the old prints that fill in within minutes. Env G3_REMASTER_SNOWTRACKS.</summary>
        public static bool TracksOption
        {
            get
            {
                if (_tracksOption == null)
                {
                    bool stored = true;
                    try { stored = PlayerPrefs.GetInt(TracksPrefKey, 1) == 1; } catch (Exception) { }
                    _tracksOption = RemasterOptions.Resolve(new[] { Environment.GetEnvironmentVariable(TracksEnvVar) }, stored);
                }
                return _tracksOption.Value;
            }
        }

        /// <summary>The row: stores the choice; the window is rebuilt in the new mode (the stored tracks are kept).</summary>
        public static void SetTracksOption(bool on, bool persist = true)
        {
            _tracksOption = on;
            if (persist) { PlayerPrefs.SetInt(TracksPrefKey, on ? 1 : 0); PlayerPrefs.Save(); }
            Debug.Log($"[Remaster] snow tracks {(on ? "persistent" : "fading")}: {Describe()}");
        }

        /// <summary>Persistent tracks are being kept (snow on, prints on, the row on).</summary>
        public static bool TracksActive => _active && TracksOption && WorldRemasterLook.Current.snowTrails;

        /// <summary>The persistent tracks (null until the snow ran with the row on): tools, tests, the save.</summary>
        public static SnowTrackStore Tracks => _store;

        /// <summary>Game time (s) for the track age cap; set by the session (Myrtana), else the play time.</summary>
        public static Func<double> GameSeconds { get; set; }

        static double Now => GameSeconds != null ? GameSeconds() : Time.timeAsDouble;

        /// <summary>A new game / a load without tracks: no prints.</summary>
        public static void ResetTracks()
        {
            _store?.Clear();
            if (_grid != null) ReleaseTrail();
            _walkers.Clear();
        }

        /// <summary>The tracks for a save (deflated, <see cref="SnowTrackStore.Write"/>); null = none.</summary>
        public static byte[] SaveTracks() => _store != null && _store.Count > 0 ? _store.Write(Now) : null;

        /// <summary>perf-2: the tracks copied for a save encoded later on a worker (<see cref="SnowTrackStore.Encode"/>); null = none.</summary>
        public static SnowTrackStore.Snapshot CaptureTracks(SnowTrackStore.Snapshot into) =>
            _store != null && _store.Count > 0 ? _store.Capture(Now, into) : null;

        /// <summary>Tracks from a save (null/empty = none); the GPU window is refilled from them.</summary>
        public static void LoadTracks(byte[] data)
        {
            ResetTracks();
            if (data == null || data.Length == 0) return;
            EnsureStore(WorldRemasterLook.Current);
            try { _store.Read(data, Now); }
            catch (Exception e) { Debug.LogWarning($"[G3SnowV2] saved tracks unreadable, dropped: {e.Message}"); _store.Clear(); }
        }

        /// <summary>The prints (shared interface, Docs/remaster-world.md 12.6; null while off): CPU copy of what the shader
        /// reads from _G3SnowPrints / _G3SnowCells (G3RemasterSnowPrints.hlsl: G3SnowPrintsAt, G3SnowDisplacement).</summary>
        public static SnowPrintGrid Prints => _grid;

        /// <summary>
        /// A dense near-camera ground mesh to displace the prints into (remaster-relief's focus patch, Docs/remaster-world.md
        /// 12.6): set by its owner; snow asks it for a dense patch round the hero while prints are on. Null = parallax only.
        /// </summary>
        public static IDenseGroundPatch DenseGround { get; set; }

        /// <summary>Focus id snow uses with <see cref="DenseGround"/>.</summary>
        public const int DenseFocusId = 0x536E6F77;   // "Snow"

        /// <summary>Fresh snow 0..1 (snowfall builds it up).</summary>
        public static float Fresh => _fresh;

        /// <summary>The row: stores and re-applies with the current world parts.</summary>
        public static void SetOption(bool on, bool persist = true)
        {
            _option = on;
            if (persist) { PlayerPrefs.SetInt(PrefKey, on ? 1 : 0); PlayerPrefs.Save(); }
            Apply(G3WorldRemaster.Parts);
            Debug.Log($"[Remaster] snow {(on ? "on" : "off")}: {Describe()}");
        }

        /// <summary>Called by <see cref="G3WorldV2.Apply(WorldParts)"/> with the parts that are on.</summary>
        public static void Apply(WorldParts parts)
        {
            var c = WorldRemasterLook.Current;
            bool on = Option && c.v2 && (parts & WorldParts.Surfaces) != 0 && Application.isPlaying;
            // remaster-sand (section 22): the driver and the print grid also run for the desert sand alone (G3SandV2.Apply first)
            bool run = on || G3SandV2.Active;
            if (on == _active && run == _running) { if (run) SetGlobals(); return; }
            bool lookChanged = on != _active;
            _active = on;
            _running = run;
            G3Environment.Applied -= OnEnvironment;
            // the rain-shadow grid also keeps the snow out from under roofs; arch-4: and the settled sand / dust and the wind
            // ripples of the desert (G3RemasterWorldV2: G3V2Covered)
            G3WeatherDirector.ShelterWanted = on || G3SandV2.Active;
            if (!on) DenseGround?.ClearFocus(DenseFocusId);   // the dense patch serves the snow prints only
            if (run)
            {
                G3Environment.Applied += OnEnvironment;
                if (on && lookChanged) FindTextures();
                if (_driver == null)
                {
                    // not DontSave (remaster-relief's finding): destroyed when Play stops, OnDestroy frees the GPU data
                    var go = new GameObject("G3SnowV2") { hideFlags = HideFlags.HideInHierarchy };
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _driver = go.AddComponent<Driver>();
                }
                _driver.enabled = true;
            }
            else
            {
                if (_driver != null) _driver.enabled = false;
                ReleaseTrail();
            }
            SetGlobals();
        }

        public static string Describe()
        {
            if (!_active) return Option ? "snow off (needs world v2 Ground/Rocks/Buildings)" : "snow off (option)";
            return $"snow on: fresh {_fresh:0.00}, snowfall {_snowfall:0.0}, clock {_clock:0.000}, prints {(_grid != null ? $"{_grid.Made} (grid {_grid.Cells}x{_grid.Cells} {_grid.CellSize * 100:0} cm, ring {_grid.Capacity})" : "off")}, characters {_tracked.Count}, dense patch {(DenseGround != null ? "yes" : "no")}" +
                   $", tracks {(TracksOption ? "persistent" : "fading")} {(_store != null ? $"{_store.Count}/{_store.Capacity} (cover clock {_store.Clock:0.000}, walkers {_walkers.Count})" : "none")}";
        }

        static Vector4 V(float[] a) => new Vector4(a[0], a[1], a[2], a[3]);

        static void SetGlobals()
        {
            Shader.SetGlobalVector(SnowId, V(SnowV2.SnowGlobal(_active, _fresh)));
            Shader.SetGlobalVector(Snow2Id, V(SnowV2.Snow2Global));
            Shader.SetGlobalVector(Snow3Id, V(SnowV2.Snow3Global));
            Shader.SetGlobalVector(Snow4Id, V(SnowV2.Snow4Global));
            if (!_running || _grid == null) Shader.SetGlobalVector(TrailId, Vector4.zero);
            BindDummies();
        }

        static void OnEnvironment(G3Environment env, G3WeatherSample s, float overcast)
        {
            _state = env.weatherState;
            _blendState = env.blendState;
            _blend = env.blend;
            _mix = env.StateMix;
        }

        // ------------------------------------------------------------------ textures: G3's own snow + the CC0 snow scan

        static void FindTextures()
        {
            var set = G3WorldRemaster.Set;
            Texture g3 = null, scan = null, scanN = null;
            if (set != null && set.v2pairs != null)
                foreach (var p in set.v2pairs)
                {
                    if (p.original == null || p.v2 == null) continue;
                    if (p.original.name.IndexOf("Ground_Snow", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (p.v2.HasProperty("_BaseMap")) g3 = p.v2.GetTexture("_BaseMap");
                    if (p.v2.HasProperty("_V2AlbedoA")) scan = p.v2.GetTexture("_V2AlbedoA");
                    if (p.v2.HasProperty("_V2NormalA")) scanN = p.v2.GetTexture("_V2NormalA");
                    if (g3 != null && scan != null) break;
                }
            Shader.SetGlobalTexture(AlbedoId, g3 != null ? g3 : Texture2D.whiteTexture);
            Shader.SetGlobalTexture(ScanId, scan != null ? scan : Texture2D.grayTexture);
            Shader.SetGlobalTexture(ScanNormalId, scanN != null ? scanN : Texture2D.normalTexture);
            // x G3 snow repeat (1/m: G3's ground repeat ~7 m), y scan repeat (1/m: 2 m scan), z print wall (m), w print depth (m)
            Shader.SetGlobalVector(TexParamsId, new Vector4(1f / 7.1f, 0.5f, SnowV2.Wall, 0));
            if (g3 == null) Debug.LogWarning("[G3SnowV2] no v2 copy of G3_Nature_Ground_Snow in the world set: top-face snow uses white");
        }

        /// <summary>The snow-region map (1 texel per manifest cell, Nordmar 1; bilinear: a 250 m soft border).</summary>
        static void EnsureRegion()
        {
            if (_region != null) return;
            var streamer = UnityEngine.Object.FindAnyObjectByType<G3WorldStreamer>();
            var m = streamer != null ? streamer.manifest : null;
            if (m == null) return;
            // remaster-sand: two channels, R snow region (Nordmar), G desert (Varant) - the sand terms read G
            _region = new Texture2D(m.countX, m.countZ, TextureFormat.RG16, false, true)
            {
                name = "G3 Snow/Desert Regions", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
            };
            var px = new byte[m.countX * m.countZ];
            var dx = new byte[m.countX * m.countZ];
            var rg = new byte[m.countX * m.countZ * 2];
            for (int z = 0; z < m.countZ; z++)
                for (int x = 0; x < m.countX; x++)
                {
                    string region = m.Get(x, z)?.region;
                    int i = z * m.countX + x;
                    px[i] = (byte)(SnowV2.RegionWeight(region) * 255);
                    dx[i] = (byte)(SandV2.RegionWeight(region) * 255);
                    rg[i * 2] = px[i];
                    rg[i * 2 + 1] = dx[i];
                }
            // the desert grown by one cell (SandV2.Dilate): G3's desert sand reaches across the region border (Braga's outskirts
            // and Lago sit in border cells: the bilinear ramp alone gave them 0.26-0.6); Myrtana's coast sand is far from Varant
            _desertCorePx = (byte[])dx.Clone();   // grade-1: the region itself (no dilation) for the camera-level desert look
            SandV2.Dilate(dx, m.countX, m.countZ);
            for (int i = 0; i < dx.Length; i++) rg[i * 2 + 1] = dx[i];
            _regionPx = px; _desertPx = dx; _regionW = m.countX; _regionH = m.countZ;
            _region.SetPixelData(rg, 0);
            _region.Apply(false, true);
            // uv = (p.xz - origin) / (cell * count)
            _regionParams = new Vector4(1f / (m.cellSize * m.countX), 1f / (m.cellSize * m.countZ), -m.origin.x / (m.cellSize * m.countX), -m.origin.y / (m.cellSize * m.countZ));
            Shader.SetGlobalTexture(RegionTexId, _region);
            Shader.SetGlobalVector(RegionId, _regionParams);
        }

        /// <summary>Snow-region weight at a world point (the region map, bilinear as the shader reads it); 0 before the map exists.</summary>
        public static float RegionAt(float x, float z) => MapAt(_regionPx, x, z);

        /// <summary>atmo-1: the region map is built (<see cref="DesertAt"/> is geography, not 0 for lack of a map).</summary>
        public static bool RegionMapReady => _desertPx != null;

        /// <summary>remaster-sand: desert-region weight (Varant) at a world point, as the shader reads the map's G; 0 before the map exists.</summary>
        public static float DesertAt(float x, float z) => MapAt(_desertPx, x, z);

        /// <summary>
        /// grade-1: the desert region without the one-cell dilation (bilinear: a 250 m ramp across the region edge) - the
        /// camera-level desert look (grade, light, sky) of G3DesertAtmo: Lago, Braga's outskirts and the green border cells
        /// sit on the ramp instead of at full desert.
        /// </summary>
        public static float DesertCoreAt(float x, float z) => MapAt(_desertCorePx ?? _desertPx, x, z);

        static float MapAt(byte[] map, float x, float z)
        {
            if (map == null) return 0;
            float u = Mathf.Clamp01(x * _regionParams.x + _regionParams.z) * _regionW - 0.5f, v = Mathf.Clamp01(z * _regionParams.y + _regionParams.w) * _regionH - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, _regionW - 1), z0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, _regionH - 1);
            int x1 = Mathf.Min(x0 + 1, _regionW - 1), z1 = Mathf.Min(z0 + 1, _regionH - 1);
            float fx = Mathf.Clamp01(u - x0), fz = Mathf.Clamp01(v - z0);
            float a = Mathf.Lerp(map[z0 * _regionW + x0], map[z0 * _regionW + x1], fx), b = Mathf.Lerp(map[z1 * _regionW + x0], map[z1 * _regionW + x1], fx);
            return Mathf.Lerp(a, b, fz) / 255f;
        }

        // ------------------------------------------------------------------ per frame

        static readonly global::Unity.Profiling.ProfilerMarker PmTick = new global::Unity.Profiling.ProfilerMarker("G3SnowV2.Tick");
        static void Tick()
        {
            using var _pm = PmTick.Auto();   // snow3 perf
            if (!_running) return;
            float dt = Time.deltaTime;
            if (_active)
            {
                _snowfall = SnowV2.Snowfall(_mix) ?? SnowV2.Snowfall(_state, _blendState, _blend);
                _fresh = SnowV2.FreshStep(_fresh, _snowfall, dt);
                _clock = SnowV2.ClockStep(_clock, _snowfall, dt);
            }
            if (_region == null) EnsureRegion();
            var cam = Camera.main;
            G3SandV2.Tick(dt, cam);   // remaster-sand: wind clock of the sand prints, grain tile, wisps
            SetGlobals();   // every frame: live JSON tuning (G3WorldRemasterLook.Reload) and the fresh snow
            var c = WorldRemasterLook.Current;
            bool snowPrints = _active && c.snowTrails, sandPrints = G3SandV2.PrintsActive;
            if (!(snowPrints || sandPrints) || cam == null || !SystemInfo.supportsComputeShaders) { ReleaseTrail(); Shader.SetGlobalVector(TrailId, Vector4.zero); return; }
            EnsureTrail(c);
            bool tracks = _active && TracksOption;   // persistent tracks are snow tracks
            if (tracks != _tracksMode) { ReleaseTrail(); EnsureTrail(c); _tracksMode = tracks; }   // rebuilt in the other mode
            if (tracks)
            {
                EnsureStore(c);
                _store.Step(_snowfall, dt, c.snowTrackRefillSnowing);
            }
            float now = SnowV2.Wrapped(tracks ? _store.Clock : _clock);
            _grid.SnowNow = now;
            _grid.SandNow = SnowV2.Wrapped(G3SandV2.Clock);
            CollectStamps(cam.transform.position, c.snowTrailRange, now);
            if (tracks) TickTracks(cam.transform.position, c);
            Upload();
            // x on, y cell size (m), z cells per side, w clock now (wrapped)
            Shader.SetGlobalVector(TrailId, new Vector4(1, _grid.CellSize, _grid.Cells, now));
            Shader.SetGlobalVector(TexParamsId, new Vector4(1f / 7.1f, 0.5f, SnowV2.Wall, c.snowTrailDepth));
            if (_active) FocusDensePatch();   // displaced prints are snow prints (sand: parallax only, no relief re-tessellation)
        }

        static void EnsureTrail(WorldLookSettings c)
        {
            if (_grid != null && _grid.Cells == c.snowTrailCells && _grid.Capacity == c.snowTrailPrints &&
                Mathf.Approximately(_grid.CellSize, SnowV2.CellSize(c.snowTrailExtent, c.snowTrailCells))) return;
            ReleaseTrail();
            _grid = new SnowPrintGrid(c.snowTrailExtent, c.snowTrailCells, c.snowTrailPrints);
            _ringOwner = new int[_grid.Capacity];
            for (int i = 0; i < _ringOwner.Length; i++) _ringOwner[i] = -1;
            _feedCentre = new Vector3(float.MaxValue, 0, float.MaxValue); _feedTime = -1; _near.Clear(); _feedAt = 0;
            _printBuf = new ComputeBuffer(_grid.Capacity, 48, ComputeBufferType.Structured) { name = "G3 Snow Prints" };
            _cellBuf = new ComputeBuffer(_grid.Cells * _grid.Cells, 32, ComputeBufferType.Structured) { name = "G3 Snow Print Cells" };
            _printBuf.SetData(new PrintGpu[_grid.Capacity]);
            var empty = new CellGpu[_grid.Cells * _grid.Cells];
            for (int i = 0; i < empty.Length; i++) empty[i] = Cell(i);
            _cellBuf.SetData(empty);
            Shader.SetGlobalBuffer(PrintsId, _printBuf);
            Shader.SetGlobalBuffer(CellsId, _cellBuf);
        }

        /// <summary>New prints and the cells they went into (a few per frame: small sub-range uploads).</summary>
        static void Upload()
        {
            _grid.TakeDirty(_dirtyCells, _dirtyPrints);
            // fix-178: runs of neighbouring entries go up in one SetData each (the persistent window feeds hundreds of prints in
            // a frame, in ring order; one call per print and per cell cost ~0.2 ms in the editor)
            _dirtyPrints.Sort();
            var f = _grid.Prints;
            for (int k = 0; k < _dirtyPrints.Count;)
            {
                int start = _dirtyPrints[k], end = start;
                int n = 0;
                while (k < _dirtyPrints.Count && _dirtyPrints[k] <= end + 1)
                {
                    int id = _dirtyPrints[k++];
                    if (id < end || (n > 0 && id == end)) continue;   // duplicate
                    end = id;
                    if (n == _printStage.Length) Array.Resize(ref _printStage, n * 2);
                    int o = id * SnowPrintGrid.Stride;
                    _printStage[n++] = new PrintGpu
                    {
                        a = new Vector4(f[o], f[o + 1], f[o + 2], f[o + 3]),
                        b = new Vector4(f[o + 4], f[o + 5], f[o + 6], f[o + 7]),
                        c = new Vector4(f[o + 8], f[o + 9], f[o + 10], f[o + 11]),
                    };
                }
                _printBuf.SetData(_printStage, 0, start, n);
            }
            _dirtyCells.Sort();
            for (int k = 0; k < _dirtyCells.Count;)
            {
                int start = _dirtyCells[k], end = start;
                int n = 0;
                while (k < _dirtyCells.Count && _dirtyCells[k] <= end + 1)
                {
                    int cell = _dirtyCells[k++];
                    if (cell < end || (n > 0 && cell == end)) continue;
                    end = cell;
                    if (n == _cellStage.Length) Array.Resize(ref _cellStage, n * 2);
                    _cellStage[n++] = Cell(cell);
                }
                _cellBuf.SetData(_cellStage, 0, start, n);
            }
        }

        static CellGpu Cell(int cell)
        {
            var s = _grid.Slots;
            int b = cell * SnowV2.Slots;
            return new CellGpu { s0 = s[b], s1 = s[b + 1], s2 = s[b + 2], s3 = s[b + 3], s4 = s[b + 4], s5 = s[b + 5], s6 = s[b + 6], s7 = s[b + 7] };
        }

        /// <summary>One-element buffers bound while the prints are off (a draw must never read an unbound buffer).</summary>
        static void BindDummies()
        {
            if (_grid != null) return;
            if (_dummyPrints == null)
            {
                _dummyPrints = new ComputeBuffer(1, 48, ComputeBufferType.Structured) { name = "G3 Snow Prints (none)" };
                _dummyPrints.SetData(new PrintGpu[1]);
                _dummyCells = new ComputeBuffer(1, 32, ComputeBufferType.Structured) { name = "G3 Snow Print Cells (none)" };
                const uint E = SnowV2.Empty;
                _dummyCells.SetData(new[] { new CellGpu { s0 = E, s1 = E, s2 = E, s3 = E, s4 = E, s5 = E, s6 = E, s7 = E } });
            }
            Shader.SetGlobalBuffer(PrintsId, _dummyPrints);
            Shader.SetGlobalBuffer(CellsId, _dummyCells);
        }

        /// <summary>Asks the dense ground patch (if any) to follow the player: real displaced prints round him (12.6).</summary>
        static void FocusDensePatch()
        {
            var d = DenseGround;
            if (d == null) return;
            var hero = G3Character.Live.Count > 0 ? FindHero() : null;
            if (hero == null) { d.ClearFocus(DenseFocusId); return; }
            // 4 cm round the feet (a boot sole is ~11 cm wide), +6 cm per m: dense enough for prints within ~2 m
            d.SetFocus(DenseFocusId, hero.transform.position, 0.04f, 0.06f, 0.5f);
        }

        static G3Character _hero;
        static G3Character FindHero()
        {
            if (_hero != null && _hero.isActiveAndEnabled) return _hero;
            var p = UnityEngine.Object.FindAnyObjectByType<G3Player>();
            _hero = p != null ? p.GetComponentInChildren<G3Character>() : null;
            return _hero;
        }

        static void ReleaseTrail()
        {
            _printBuf?.Release(); _printBuf = null;
            _cellBuf?.Release(); _cellBuf = null;
            bool had = _grid != null;
            _grid = null;
            _ringOwner = null;
            if (_store != null) for (int i = 0; i < _store.Capacity; i++) _store.Window[i] = -1;   // the window is gone with the grid
            _tracked.Clear();
            if (had) { BindDummies(); DenseGround?.ClearFocus(DenseFocusId); }
        }

        // ------------------------------------------------------------------ characters → stamps

        sealed class Tracked
        {
            public Transform[] feet;
            public bool[] paw;
            public float[] side;   // -1 left, +1 right foot (the boot sole is mirrored)
            public SnowV2.FootPlant[] plant;
            public float height = 1.8f;
            public Vector3 lastRoot, walk;
            public bool hasLast, quadruped;
            public float trailAcc;
            public int seenFrame;
            public bool skip;   // doors, chests, items: never print
            public int enables;  // G3Character.Enables when looked at
        }

        static readonly Dictionary<G3Character, Tracked> _tracked = new Dictionary<G3Character, Tracked>();
        static readonly List<G3Character> _drop = new List<G3Character>();
        static readonly List<Transform> _bones = new List<Transform>();
        static readonly RaycastHit[] _hits = new RaycastHit[8];

        static Tracked Track(G3Character ch)
        {
            // fix-193: a character switched off and on again (the hero's body hidden for an animal form and shown on the
            // way back) is looked at afresh: its rig and bones may have changed meanwhile
            if (_tracked.TryGetValue(ch, out var t) && t.enables == ch.Enables) return t;
            t = new Tracked();
            string nm = ch.gameObject.name;
            t.skip = nm.StartsWith("Interactive", StringComparison.Ordinal) || nm.StartsWith("Item", StringComparison.Ordinal) ||
                     nm.StartsWith("G3_Object", StringComparison.Ordinal);
            _bones.Clear();
            ch.GetComponentsInChildren(true, _bones);
            var feet = new List<Transform>();
            var paw = new List<bool>();
            foreach (var b in _bones)
            {
                if (SnowV2.IsFootBone(b.name)) { feet.Add(b); paw.Add(false); }
                else if (SnowV2.IsPawBone(b.name)) { feet.Add(b); paw.Add(true); }
            }
            t.feet = feet.ToArray();
            t.paw = paw.ToArray();
            t.side = new float[t.feet.Length];
            for (int i = 0; i < t.feet.Length; i++) t.side[i] = t.feet[i].name.IndexOf("_Left_", StringComparison.Ordinal) >= 0 ? -1f : 1f;
            t.plant = new SnowV2.FootPlant[t.feet.Length];
            // size from the skeleton (the skinned bounds are loose: 3 m for a human, 4.4 m for a pig): humans their head's
            // height, quadrupeds the shoulder-to-hip length is about their height
            float head = 0, top = 0;
            foreach (var b in _bones)
            {
                float y = b.position.y - ch.transform.position.y;
                if (y > top) top = y;
                if (b.name.IndexOf("_Head_", StringComparison.Ordinal) >= 0 && y > head) head = y;
            }
            float h = head > 0 ? head : top;
            if (h > 0) t.height = Mathf.Clamp(h, 0.3f, 8f);
            // a rig is a quadruped when its front paws hang about as low as its feet in the bind pose
            float footY = float.MaxValue, pawY = float.MaxValue;
            for (int i = 0; i < t.feet.Length; i++)
            {
                float y = t.feet[i].position.y - ch.transform.position.y;
                if (t.paw[i]) pawY = Mathf.Min(pawY, y); else footY = Mathf.Min(footY, y);
            }
            string rig = !string.IsNullOrEmpty(ch.Rig) ? ch.Rig : t.feet.Length > 0 ? SnowV2.RigOfBone(t.feet[0].name) : null;
            t.quadruped = SnowV2.IsQuadruped(rig, footY, pawY, t.height);   // fix-193: by the rig, the pose only for unknown rigs
            t.enables = ch.Enables;
            _tracked[ch] = t;
            return t;
        }

        static void CollectStamps(Vector3 eye, float range, float clock)
        {
            // remaster-sand: with only the sand on, nobody prints outside the desert (no foot tracking, no rays)
            if (!_active && DesertAt(eye.x, eye.z) < 0.25f) return;
            float dt = Time.deltaTime, time = Time.time;
            int frame = Time.frameCount;
            float r2 = range * range;
            foreach (var ch in G3Character.Live)
            {
                if (ch == null || !ch.isActiveAndEnabled) continue;
                var root = ch.transform;
                var pos = root.position;
                if ((pos - eye).sqrMagnitude > r2) continue;
                var t = Track(ch);   // cached per character: no per-frame name string (perf: 0.1 ms per 20 actors in the editor)
                t.seenFrame = frame;
                if (t.skip) continue;
                float body = 0;
                var fwd = root.forward; fwd.y = 0;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                fwd.Normalize();
                if (t.hasLast && dt > 0)
                {
                    var step = new Vector3(pos.x - t.lastRoot.x, 0, pos.z - t.lastRoot.z);
                    body = step.magnitude / dt;
                    // prints point along the walk (smoothed; strafing and turning on the spot too), facing while standing
                    if (body > 0.3f && body < 20f) t.walk = Vector3.Slerp(t.walk.sqrMagnitude > 0.5f ? t.walk : step.normalized, step.normalized, Mathf.Clamp01(dt * 8f));
                    else if (body <= 0.3f) t.walk = Vector3.Slerp(t.walk.sqrMagnitude > 0.5f ? t.walk : fwd, fwd, Mathf.Clamp01(dt * 2f));
                }
                if (t.walk.sqrMagnitude > 0.5f) fwd = t.walk.normalized;
                if (t.feet.Length > 0)
                {
                    for (int i = 0; i < t.feet.Length; i++)
                    {
                        var f = t.feet[i];
                        if (f == null) continue;
                        var fp = f.position;
                        if (t.paw[i] && !t.quadruped) continue;   // human hands: no prints
                        if (t.plant[i].Update(fp.x, fp.y - pos.y, fp.z, body, dt, time))
                            Print(ch, fp, fwd, t.height, t.quadruped ? SnowV2.Paw : SnowV2.Boot, t.side[i], clock, body);   // quadrupeds: paws front and back
                    }
                }
                else if (t.hasLast && body > 0.5f && body < 20f)
                {
                    // no foot bones: a round trail every 0.3 m while it moves
                    t.trailAcc += body * dt;
                    if (t.trailAcc > 0.3f) { t.trailAcc = 0; Print(ch, pos, fwd, t.height * 2.2f, SnowV2.Round, 1, clock, body); }
                }
                t.lastRoot = pos;
                t.hasLast = true;
            }
            if (frame % 120 == 0)
            {
                _drop.Clear();
                foreach (var kv in _tracked) if (kv.Key == null || frame - kv.Value.seenFrame > 300) _drop.Add(kv.Key);
                foreach (var k in _drop) _tracked.Remove(k);
            }
        }

        static void Print(G3Character ch, Vector3 foot, Vector3 fwd, float height, int kind, float side, float clock, float speed)
        {
            float ground = foot.y - 0.08f;
            int hits = Physics.RaycastNonAlloc(foot + Vector3.up * 0.4f, Vector3.down, _hits, 1.6f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < hits; i++)
            {
                var h = _hits[i];
                var ct = h.collider.transform;
                // not the character itself: its bones' colliders, nor the controller capsule on a parent (the hero's
                // G3Player: hit 0.6 m above the ground, the prints then failed the shader's height test)
                if (ct.IsChildOf(ch.transform) || ch.transform.IsChildOf(ct) || h.collider is CharacterController) continue;
                if (h.point.y > foot.y + 0.1f) continue;   // the ground is below the ankle
                if (h.distance < best) { best = h.distance; ground = h.point.y; }
            }
            if (best == float.MaxValue) return;   // nothing under the foot: in the air
            // remaster-relief pass 2 (Docs/remaster-world.md section 14): on the terrain the print lies on the drawn relief (up to
            // ~1 m off G3's collision on open ground), where the character's feet are shown (G3ReliefFooting) and where the
            // shader compares it (G3SnowHeightMatch against the drawn vertex height)
            float hitGround = ground;   // fx-1: the surface the foot stands on (carpet, floor or terrain collision) for the dust gate
            if (G3TerrainRelief.TryGround(new Vector3(foot.x, ground, foot.z), out float col, out float vis) && Mathf.Abs(col - ground) < 0.1f) ground = vis;
            bool paw = kind != SnowV2.Boot;
            var (hl, hw) = SnowV2.PrintSize(height, paw);
            if (kind == SnowV2.Round) hw = hl * 0.8f;
            // the ankle bone sits above the heel: the sole's centre is ~35 % of its length ahead
            var c = foot + fwd * (kind == SnowV2.Boot ? hl * 0.35f : 0f);
            float depth = kind == SnowV2.Boot ? 1f : 0.8f;
            // remaster-sand (22.5): in the desert a sand print - shallower, on the wind clock, never persistent; shows on loose and
            // wet sand only (the shader's layer classes: none on paths or rock)
            if (G3SandV2.PrintsActive && DesertAt(c.x, c.z) >= 0.5f) { SandPrint(c, fwd, hl, hw, ground, kind, side, speed, hitGround); return; }
            if (!_active || !WorldRemasterLook.Current.snowTrails) return;   // sand only: no snow prints
            if (TracksOption && _store != null)
            {
                if (RegionAt(c.x, c.z) < 0.5f) return;   // no snow here: nothing to keep (the shader shows prints on snow only)
                Keep(c.x, c.z, fwd.x, fwd.z, hl, hw, ground, depth, kind, side, 0.6f);
                return;
            }
            _grid.Add(c.x, c.z, fwd.x, fwd.z, hl, hw, ground, clock, depth, kind, side, 0.6f);
        }

        /// <summary>remaster-sand: a sand print into the GPU window directly (fading; kind + <see cref="SandV2.SandKind"/>, sand clock).</summary>
        static void SandPrint(Vector3 c, Vector3 fwd, float hl, float hw, float ground, int kind, float side, float speed, float hitGround)
        {
            int id = _grid.Add(c.x, c.z, fwd.x, fwd.z, hl, hw, ground, SnowV2.Wrapped(G3SandV2.Clock), SandV2.PrintDepthRel(kind == SnowV2.Boot),
                               kind + SandV2.SandKind, side, 0.8f);
            // sand-2: a little dust kicked up by the step; fx-1: only walking / running on loose open sand (the relief's sand mask
            // of the terrain the foot stands on: never on carpets, floors, interiors, stone or wood; never dancing in place)
            if (speed >= SandV2.DustMinSpeed && G3TerrainRelief.LooseSandAt(new Vector3(c.x, hitGround, c.z)) >= 0.5f && G3CoveredMap.At(c.x, c.z) < 0.5f)
                G3SandDust.Puff(new Vector3(c.x, ground, c.z), hl / 0.14f, speed);
            // a ring entry the persistent window fed from the store is gone now: the store feeds it again later
            if (_ringOwner != null && id >= 0 && id < _ringOwner.Length && _ringOwner[id] >= 0)
            {
                int prev = _ringOwner[id];
                if (_store != null && prev < _store.Capacity && _store.Window[prev] == id) _store.Window[prev] = -1;
                _ringOwner[id] = -1;
            }
        }

        // ------------------------------------------------------------------ persistent tracks (fix-178)

        static void EnsureStore(WorldLookSettings c)
        {
            if (_store != null && _store.Capacity == c.snowTrackPrints) return;
            var old = _store;
            _store = new SnowTrackStore(c.snowTrackPrints);
            if (old != null && old.Count > 0) _store.Read(old.Write(Now), Now);   // budget changed (json): keep the newest
            if (_ringOwner != null) for (int i = 0; i < _ringOwner.Length; i++) _ringOwner[i] = -1;
        }

        /// <summary>A print into the store and, being made round the camera, straight into the GPU window.</summary>
        static void Keep(float x, float z, float dx, float dz, float hl, float hw, float ground, float depth, int kind, float side, float rim)
        {
            int i = _store.Add(x, z, dx, dz, hl, hw, ground, Now, depth, kind, side, rim, out int evictedWindow);
            if (evictedWindow >= 0 && _grid != null && evictedWindow < _ringOwner.Length && _ringOwner[evictedWindow] == i)
            {
                _grid.Remove(evictedWindow);
                _ringOwner[evictedWindow] = -1;
            }
            if (_grid != null && new Vector2(x - _feedCentre.x, z - _feedCentre.z).sqrMagnitude < WindowRadius * WindowRadius) ToWindow(i);
        }

        /// <summary>Prints within this distance of the camera are kept in the 64 m GPU window (its half side minus a margin).</summary>
        static float WindowRadius => _grid != null ? _grid.CellSize * _grid.Cells * 0.5f - 2f : 0;

        static void ToWindow(int i)
        {
            var st = _store;
            int id = _grid.Add(st.X[i], st.Z[i], st.DirX[i], st.DirZ[i], st.HalfLength[i], st.HalfWidth[i], st.Y[i], SnowV2.Wrapped(st.Stamp[i]),
                               st.Depth[i], st.Kind[i], st.Side[i], st.Rim[i]);
            int prev = _ringOwner[id];
            if (prev >= 0 && prev != i && st.Window[prev] == id) st.Window[prev] = -1;
            _ringOwner[id] = i;
            st.Window[i] = id;
        }

        static readonly global::Unity.Profiling.ProfilerMarker PmTracks = new global::Unity.Profiling.ProfilerMarker("G3SnowV2.Tracks");

        /// <summary>
        /// Per frame: drop covered / old prints (a slice of the ring), and every 0.5 s or 2 m of camera travel refill the GPU
        /// window from the store (prints that left the window are marked out, those round the camera fed in, a budget per frame).
        /// </summary>
        static void TickTracks(Vector3 eye, WorldLookSettings c)
        {
            using var _pm = PmTracks.Auto();
            double now = Now;
            _expired.Clear();
            _store.Expire(now, c.snowTrackDays * 86400.0, 1024, _expired);
            foreach (var (index, window) in _expired)
                if (window >= 0 && window < _ringOwner.Length && _ringOwner[window] == index) { _grid.Remove(window); _ringOwner[window] = -1; }
            float r = WindowRadius;
            float moved = new Vector2(eye.x - _feedCentre.x, eye.z - _feedCentre.z).sqrMagnitude;
            if (moved > 4f || Time.unscaledTime - _feedTime > 0.5f)
            {
                _feedCentre = eye; _feedTime = Time.unscaledTime;
                // prints the window no longer covers: out (their cells may be taken by prints 64 m away)
                for (int w = 0; w < _ringOwner.Length; w++)
                {
                    int i = _ringOwner[w];
                    if (i < 0) continue;
                    float dx = _store.X[i] - eye.x, dz = _store.Z[i] - eye.z;
                    if (dx * dx + dz * dz <= (r + 1f) * (r + 1f)) continue;
                    if (_store.Window[i] == w) _store.Window[i] = -1;
                    _ringOwner[w] = -1;
                }
                _store.Near(eye.x, eye.z, r, _near);
                _feedAt = 0;
            }
            int budget = 768;
            while (_feedAt < _near.Count && budget > 0)
            {
                int i = _near[_feedAt++];
                if (!_store.Alive[i] || _store.Window[i] >= 0) continue;
                ToWindow(i);
                budget--;
            }
        }

        // coarse trails of walkers the per-step prints do not see (far / unseen NPCs, fed by the session: Myrtana SnowTrackSampler)
        sealed class Walker { public Vector3 last; public float carry, foot; public bool has; public int seen; }
        static readonly Dictionary<long, Walker> _walkers = new Dictionary<long, Walker>();
        static readonly List<long> _walkerDrop = new List<long>();

        /// <summary>
        /// fix-178: an NPC the per-step prints do not cover (beyond <c>snowTrailRange</c> of the camera, within
        /// <c>snowTrackRange</c>; unseen or simulated) is at <paramref name="position"/> now: lays coarse prints along its path
        /// since the last call (one every <c>snowTrackStride</c> m, alternating feet; jumps over 30 m print nothing) so its trail
        /// can be followed later. <paramref name="key"/> identifies the walker. Nothing outside snow regions.
        /// </summary>
        public static void Walk(long key, Vector3 position, bool biped, float height)
        {
            if (!TracksActive || _store == null || _grid == null) return;
            if (!_walkers.TryGetValue(key, out var w)) _walkers[key] = w = new Walker();
            w.seen = Time.frameCount;
            var c = WorldRemasterLook.Current;
            var cam = Camera.main;
            float d2 = cam != null ? (position - cam.transform.position).sqrMagnitude : float.MaxValue;
            bool far = d2 > c.snowTrailRange * c.snowTrailRange, inRange = d2 < c.snowTrackRange * c.snowTrackRange;
            if (!w.has || !far || !inRange || RegionAt(position.x, position.z) < 0.5f) { w.last = position; w.has = true; w.carry = 0; return; }
            var (hl, hw) = SnowV2.PrintSize(height, !biped);
            SnowTrackStore.StrideSteps(w.last.x, w.last.z, position.x, position.z, biped ? c.snowTrackStride : c.snowTrackStride * 0.8f,
                                       biped ? 0.11f : 0.09f, 30f, ref w.carry, ref w.foot, _steps);
            float y0 = w.last.y, y1 = position.y;
            float len = Mathf.Max(new Vector2(position.x - w.last.x, position.z - w.last.z).magnitude, 1e-3f);
            foreach (var st in _steps)
            {
                float t = Mathf.Clamp01(new Vector2(st.x - w.last.x, st.z - w.last.z).magnitude / len);
                float y = Mathf.Lerp(y0, y1, t);
                if (G3TerrainRelief.TryGround(new Vector3(st.x, y, st.z), out float col, out float vis) && Mathf.Abs(col - y) < 0.3f) y = vis;
                float ahead = biped ? hl * 0.35f : 0f;
                Keep(st.x + st.dx * ahead, st.z + st.dz * ahead, st.dx, st.dz, hl, hw, y, biped ? 1f : 0.8f, biped ? SnowV2.Boot : SnowV2.Paw, st.side, 0.6f);
            }
            w.last = position;
        }

        /// <summary>Forgets walkers not reported for a while (call now and then).</summary>
        public static void ForgetIdleWalkers()
        {
            _walkerDrop.Clear();
            foreach (var kv in _walkers) if (Time.frameCount - kv.Value.seen > 600) _walkerDrop.Add(kv.Key);
            foreach (var k in _walkerDrop) _walkers.Remove(k);
        }

        sealed class Driver : MonoBehaviour
        {
            void LateUpdate() => Tick();   // after the animation: the feet are where they are drawn

            void OnDestroy()
            {
                if (_driver != this) return;
                _driver = null;
                // end of Play: buffers, the region map and the dense-patch focus go with the driver (no leak across Play sessions)
                ReleaseTrail();
                _dummyPrints?.Release(); _dummyPrints = null;
                _dummyCells?.Release(); _dummyCells = null;
                if (_region != null) { Destroy(_region); _region = null; }
                _regionPx = _desertPx = null;
                _active = false;
                _running = false;
                G3SandV2.Shutdown();
                G3Environment.Applied -= OnEnvironment;
                Shader.SetGlobalVector(SnowId, Vector4.zero);
                Shader.SetGlobalVector(TrailId, Vector4.zero);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _option = null; _active = false; _running = false; _desertPx = null; _desertCorePx = null; _fresh = 0; _clock = 0; _driver = null; _region = null; _grid = null;
            _tracksOption = null; _store = null; _ringOwner = null; _regionPx = null; _walkers.Clear(); _tracksMode = false; GameSeconds = null;
            SnowV2.LegacyLook = false;
            _printBuf = _cellBuf = _dummyPrints = _dummyCells = null; _tracked.Clear(); _hero = null; DenseGround = null;
            G3Environment.Applied -= OnEnvironment;
        }
    }
}
