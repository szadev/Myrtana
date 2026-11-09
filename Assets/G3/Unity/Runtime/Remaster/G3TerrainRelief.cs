using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using NV3 = System.Numerics.Vector3;
using NV2 = System.Numerics.Vector2;
using NV4 = System.Numerics.Vector4;

namespace G3.Unity
{
    /// <summary>
    /// Terrain relief (Docs/remaster-world.md section 13): with the Terrain remaster part on, every loaded landscape cell
    /// draws a re-tessellated copy of its mesh (Phong-smoothed silhouettes, erosion gullies and strata on steep ground,
    /// <see cref="TerrainRelief"/>), and the cells round the camera (and the focus points, e.g. the hero for the snow
    /// prints) get a dense near part, re-centred on a grid. Only <c>MeshFilter.sharedMesh</c> changes: the MeshCollider keeps
    /// G3's mesh, so collision, the NavMesh (built from the colliders) and every ray are untouched; the visual surface stays
    /// within a few cm of it on walkable faces (G3Cli <c>relief audit</c>). Mesh building runs on one background thread;
    /// the main thread only copies the source arrays and uploads. Off = every original mesh back, the copies destroyed.
    /// Switch: the Terrain row, plus <c>G3.Remaster.Relief</c> / env <c>G3_REMASTER_RELIEF</c> (default on) and json
    /// <c>relief</c> (Resources/G3TerrainRelief.json, live with <see cref="Reload"/>).
    /// </summary>
    public static class G3TerrainRelief
    {
        public const string PrefKey = "G3.Remaster.Relief";
        public const string EnvVar = "G3_REMASTER_RELIEF";
        const string SettingsResource = "G3TerrainRelief";
        const string FactsResource = "G3TerrainReliefFacts";
        // dune-1 (Docs/remaster-world.md section 23): Varant's dunes, a sub-switch of the relief (no free row on the remaster page:
        // they follow "Terrain relief"); pref / env / json `dunes` for A/B; off = the relief without them (relief off = G3's heights)
        public const string DunesPrefKey = "G3.Remaster.Dunes";
        public const string DunesEnvVar = "G3_REMASTER_DUNES";
        const string DuneResource = "G3DuneField";
        static DuneField _dunes;
        static bool _dunesLoaded;
        static bool? _dunesOption;

        static readonly ProfilerMarker ReadMarker = new ProfilerMarker("G3TerrainRelief.Read");
        static readonly ProfilerMarker UploadMarker = new ProfilerMarker("G3TerrainRelief.Upload");
        static readonly ProfilerMarker NearMarker = new ProfilerMarker("G3TerrainRelief.NearApply");
        static readonly int EyeId = Shader.PropertyToID("_G3ReliefEye");
        static readonly int BandId = Shader.PropertyToID("_G3ReliefBand");
        static readonly int LambdaAId = Shader.PropertyToID("_G3ReliefLambdaA");
        static readonly int LambdaBId = Shader.PropertyToID("_G3ReliefLambdaB");

        static bool? _option;
        static bool _active;
        static ReliefSettings _settings;
        static Driver _driver;
        static byte[] _facts;
        static Dictionary<string, (int offset, int count)> _factsIndex;
        static readonly Dictionary<Mesh, Mesh> _sourceOf = new Dictionary<Mesh, Mesh>();

        // ------------------------------------------------------------------ public API

        /// <summary>The stored switch (default on: a refinement of the Terrain part); env G3_REMASTER_RELIEF first.</summary>
        public static bool Option
        {
            get
            {
                if (_option == null)
                {
                    bool stored = true;
                    try { stored = PlayerPrefs.GetInt(PrefKey, 1) == 1; } catch (Exception) { }
                    _option = RemasterOptions.Resolve(new[] { Environment.GetEnvironmentVariable(EnvVar) }, stored);
                }
                return _option.Value;
            }
        }

        public static bool Active => _active;

        public static ReliefSettings Settings
        {
            get
            {
                if (_settings == null) _settings = LoadSettings();
                return _settings;
            }
        }

        /// <summary>A/B switch (scripts, the runner): stores and re-applies with the current world parts.</summary>
        public static void SetOption(bool on, bool persist = true)
        {
            _option = on;
            if (persist) { PlayerPrefs.SetInt(PrefKey, on ? 1 : 0); PlayerPrefs.Save(); }
            Apply(G3WorldRemaster.Parts);
            Debug.Log($"[Remaster] relief {(on ? "on" : "off")}: {Describe()}");
        }

        /// <summary>
        /// fix-171 runner A/B: the relief as in build 21a: every octave band-limited by the near batch (no live fade in the
        /// shader), the camera point on a 6 m grid, no near margin. Rebuilds every cell (like <see cref="Reload"/>).
        /// </summary>
        public static bool Legacy
        {
            get => _legacy;
            set { if (_legacy == value) return; _legacy = value; Reload(); Debug.Log($"[Relief] legacy (21a) {(value ? "on" : "off")}"); }
        }
        static bool _legacy;

        /// <summary>dune-1: the stored dune switch (default on); env G3_REMASTER_DUNES first.</summary>
        public static bool DunesOption
        {
            get
            {
                if (_dunesOption == null)
                {
                    bool stored = true;
                    try { stored = PlayerPrefs.GetInt(DunesPrefKey, 1) == 1; } catch (Exception) { }
                    _dunesOption = RemasterOptions.Resolve(new[] { Environment.GetEnvironmentVariable(DunesEnvVar) }, stored);
                }
                return _dunesOption.Value;
            }
        }

        /// <summary>dune-1 A/B (scripts, the runner): Varant's dunes on / off; rebuilds every relief cell (0.5-3 s of worker time).</summary>
        public static void SetDunes(bool on, bool persist = true)
        {
            _dunesOption = on;
            if (persist) { PlayerPrefs.SetInt(DunesPrefKey, on ? 1 : 0); PlayerPrefs.Save(); }
            Reload();
            Debug.Log($"[Remaster] dunes {(on ? "on" : "off")}: {DescribeDunes()}");
        }

        /// <summary>dune-1: one line: switch, field tiles and memory.</summary>
        public static string DescribeDunes()
        {
            var f = Settings.Dunes;
            return $"dunes option {DunesOption}, json {Settings.dunes}, field " +
                   (f == null ? "none" : $"{f.TileCount} tiles, {f.Bytes / 1048576f:F1} MB, wind {f.WindAzimuth:F0} deg");
        }

        /// <summary>Re-reads the json and rebuilds every cell (live tuning).</summary>
        public static void Reload()
        {
            _settings = LoadSettings();
            if (_active) { SetActive(false); Apply(G3WorldRemaster.Parts); }
        }

        /// <summary>Called by <see cref="G3WorldRemaster"/> with the parts that are on.</summary>
        public static void Apply(WorldParts parts)
        {
            bool on = Option && Settings.relief && (parts & WorldParts.Terrain) != 0 && Application.isPlaying;
            if (on != _active) SetActive(on);
        }

        /// <summary>
        /// A point that wants the terrain dense round it (shared with remaster-snow: the hero, for real deformed prints):
        /// <paramref name="spacing"/> m at the point, + <paramref name="slope"/> per metre; re-centred when it moves more than
        /// <paramref name="grid"/> m. The near parts are rebuilt in the background (latency ~0.1–0.5 s).
        /// </summary>
        public static void SetFocus(int id, Vector3 position, float spacing = 0.12f, float slope = 0.06f, float grid = 1f, float maxSpacing = 0.3f)
        {
            _focus[id] = new Focus { position = position, spacing = Mathf.Max(0.03f, spacing), slope = Mathf.Max(0.001f, slope), grid = Mathf.Max(0.25f, grid), max = maxSpacing };
        }

        /// <summary>The dense ground patch for remaster-snow (<see cref="IDenseGroundPatch"/>): the relief's focus points.</summary>
        public static readonly IDenseGroundPatch Patch = new DensePatch();

        sealed class DensePatch : IDenseGroundPatch
        {
            public void SetFocus(int id, Vector3 point, float spacing, float slope, float grid) => G3TerrainRelief.SetFocus(id, point, spacing, slope, grid);
            public void ClearFocus(int id) => G3TerrainRelief.ClearFocus(id);
        }

        /// <summary>
        /// Registers <see cref="Patch"/> as the snow's dense ground (G3SnowV2.DenseGround, remaster-snow) while the relief is on.
        /// By name, so this branch builds without the snow's; after both are merged it can become a direct assignment.
        /// </summary>
        static void RegisterPatch(bool on)
        {
            var prop = Type.GetType("G3.Unity.G3SnowV2")?.GetProperty("DenseGround", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop == null || !prop.CanWrite) return;
            var cur = prop.GetValue(null);
            if (on) prop.SetValue(null, Patch);
            else if (ReferenceEquals(cur, Patch)) prop.SetValue(null, null);
        }

        public static void ClearFocus(int id) => _focus.Remove(id);

        /// <summary>fix-171 runner: no new near batch is started while true (a pop measurement captures the frame before the swap).</summary>
        public static bool HoldNear;

        /// <summary>fix-171 runner: near batches applied so far (the swap frame of each).</summary>
        public static int NearSwaps { get; private set; }

        // ------------------------------------------------------------------ the ground under things (pass 2, Docs/remaster-world.md section 14)

        /// <summary>
        /// Bumped whenever the drawn ground changes under things (a new ground map after a near batch, the relief on or off):
        /// whatever is placed on the visual ground (<see cref="G3ReliefGrounded"/>, corpses) re-reads <see cref="GroundDelta"/>.
        /// </summary>
        public static int GroundVersion { get; private set; }

        /// <summary>
        /// The drawn ground under things changed as a whole: the relief went off, or the first ground maps after it went on.
        /// Things that measured the ground once (corpses at rest) measure again.
        /// </summary>
        public static event Action GroundReset;
        static bool _resetPending;

        /// <summary>Things this far above or below the collision ground at their spot are not on the terrain (a table, a bridge, a cave).</summary>
        public const float OnGroundTolerance = 0.35f;

        /// <summary>
        /// G3's collision height and the drawn relief height at <paramref name="p"/>'s XZ (the ground maps round the camera:
        /// 160 m at 0.31 m, then 511 m at 1 m; exact for the mesh as drawn). False: relief off, outside the maps, no ground.
        /// </summary>
        public static bool TryGround(Vector3 p, out float collisionY, out float visualY)
        {
            collisionY = visualY = p.y;
            if (!_active || _driver == null) return false;
            return _driver.SampleGround(p.x, p.y, p.z, out collisionY, out visualY);   // snow3: the layer nearest p.y (overhangs)
        }

        /// <summary>
        /// fx-1: loose open sand under a foot standing on the collision at <paramref name="p"/> (0..1; the relief's sand mask,
        /// <see cref="ReliefGroundMap.SandAt"/>): 0 on anything that is not the terrain's top surface there (carpets, floors,
        /// interiors, stone, wood) and on non-sand layers; 0 with the relief off or off its map.
        /// </summary>
        public static float LooseSandAt(Vector3 p)
        {
            if (!_active || _driver == null) return 0f;
            return Mathf.Max(0f, _driver.SandAt(p.x, p.y, p.z, 0.12f));
        }

        /// <summary>
        /// How far the drawn ground is above the collision ground (m, signed) under a thing standing on the collision at
        /// <paramref name="p"/>; faded to 0 when <paramref name="p"/> is not on the terrain (more than
        /// <see cref="OnGroundTolerance"/> from it). 0 with the relief off.
        /// </summary>
        public static float GroundDelta(Vector3 p)
        {
            if (!TryGround(p, out float c, out float v)) return 0f;
            return (v - c) * OnTerrain(p.y - c);
        }

        /// <summary>1 on the terrain (within 0.15 m of the collision ground), 0 beyond <see cref="OnGroundTolerance"/>.</summary>
        public static float OnTerrain(float aboveCollision)
        {
            float a = Mathf.Abs(aboveCollision);
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, OnGroundTolerance, a));
        }

        /// <summary>The source mesh of a relief copy (itself for any other mesh): ray hits report the collider's triangles.</summary>
        public static Mesh SourceOf(Mesh mesh) => mesh != null && _sourceOf.TryGetValue(mesh, out var s) ? s : mesh;

        /// <summary>
        /// fix-171 runner: the welded vertex with the most steep relief (detail amplitude >= <paramref name="minAmp"/> m)
        /// within <paramref name="radius"/> m around it, among the cells within <paramref name="range"/> m of
        /// <paramref name="around"/>: "x y z nx ny nz count" (a cliff face to film), or "none".
        /// </summary>
        public static string FindSteep(Vector3 around, float range = 200f, float radius = 15f, float minAmp = 1.0f)
        {
            if (!_active || _driver == null) return "inactive";
            return _driver.FindSteep(around, range, radius, minAmp);
        }

        /// <summary>fix-171 runner: open edges (cracks) of the drawn relief round <paramref name="around"/> (see Driver.AuditSeams).</summary>
        public static string AuditSeams(Vector3 around, float radius = 120f)
        {
            if (!_active || _driver == null) return "inactive";
            var cam = Camera.main;
            return _driver.AuditSeams(around, radius, cam != null ? cam.transform.position : around);
        }

        /// <summary>One line for the runner: cells, triangles, memory, near state.</summary>
        public static string Describe()
        {
            if (!_active || _driver == null) return $"relief {(Option ? "on" : "off")} (inactive)";
            return _driver.Describe();
        }

        struct Focus { public Vector3 position; public float spacing, slope, grid, max; }
        static readonly Dictionary<int, Focus> _focus = new Dictionary<int, Focus>();

        // ------------------------------------------------------------------ internals

        static ReliefSettings LoadSettings()
        {
            var s = ReliefSettings.Default;
            try
            {
                var ta = Resources.Load<TextAsset>(SettingsResource);
                if (ta != null) JsonUtility.FromJsonOverwrite(ta.text, s);
            }
            catch (Exception e) { Debug.LogWarning("[Relief] settings: " + e.Message); }
            if (s.dunes && DunesOption) s.Dunes = LoadDunes();
            return s.Clamped();
        }

        /// <summary>dune-1: the baked dune field, read once (main thread: Resources); null when missing.</summary>
        static DuneField LoadDunes()
        {
            if (_dunesLoaded) return _dunes;
            _dunesLoaded = true;
            try
            {
                var ta = Resources.Load<TextAsset>(DuneResource);
                if (ta != null) { _dunes = DuneField.Read(ta.bytes); Resources.UnloadAsset(ta); }
            }
            catch (Exception e) { Debug.LogWarning("[Relief] dune field: " + e.Message); }
            if (_dunes == null) Debug.LogWarning("[Relief] no dune field (Resources/G3DuneField.bytes, G3Cli dune bake): no dunes");
            else Debug.Log($"[Relief] dune field: {_dunes.TileCount} tiles, {_dunes.Bytes / 1048576f:F1} MB");
            return _dunes;
        }

        /// <summary>Loads the world-wide facts table once (main thread: Resources).</summary>
        static void EnsureFacts()
        {
            if (_factsIndex != null) return;
            _factsIndex = new Dictionary<string, (int, int)>();
            var ta = Resources.Load<TextAsset>(FactsResource);
            if (ta != null) { _facts = ta.bytes; _factsIndex = ReliefFacts.Index(_facts); Resources.UnloadAsset(ta); }
            if (_factsIndex.Count == 0) Debug.LogWarning("[Relief] no facts table (Resources/G3TerrainReliefFacts.bytes): cells use their own faces (borders may crack)");
        }

        /// <summary>One cell's facts (any thread, after <see cref="EnsureFacts"/>); null = not in the table or another vertex count.</summary>
        static ReliefShared FactsFor(string meshName, int weldCount)
        {
            var index = _factsIndex; var data = _facts;
            if (index != null && data != null && index.TryGetValue(meshName, out var e) && e.count == weldCount) return ReliefFacts.Decode(data, e.offset, e.count);
            return null;
        }

        static void SetActive(bool on)
        {
            _active = on;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (on)
            {
                EnsureFacts();
                if (_driver == null)
                {
                    var go = new GameObject("G3TerrainRelief") { hideFlags = HideFlags.HideInHierarchy };   // not DontSave: destroyed (meshes too) when Play stops
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _driver = go.AddComponent<Driver>();
                }
                _driver.enabled = true;
                _driver.Begin(Settings);
                SceneManager.sceneLoaded += OnSceneLoaded;
                for (int i = 0; i < SceneManager.sceneCount; i++) _driver.Scan(SceneManager.GetSceneAt(i));
                RegisterPatch(true);
            }
            else if (_driver != null)
            {
                RegisterPatch(false);
                _driver.End();
                _driver.enabled = false;
            }
            GroundVersion++;
            if (!on) { G3ReliefGroundGpu.Clear(); Shader.SetGlobalVector(EyeId, Vector4.zero); }
            _resetPending = on;
            if (!on) RaiseReset();
            Debug.Log($"[Relief] {(on ? "on" : "off")}");
        }

        static void RaiseReset()
        {
            try { GroundReset?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

        static readonly ProfilerMarker SceneMarker = new ProfilerMarker("G3TerrainRelief.SceneLoaded");

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            using var _ = SceneMarker.Auto();
            if (_active && _driver != null) _driver.Scan(scene);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _option = null; _active = false; _settings = null; _driver = null; _facts = null; _factsIndex = null;
            _sourceOf.Clear(); _focus.Clear(); _resetPending = false; GroundReset = null; HoldNear = false; NearSwaps = 0;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>The source of one cell: is it a landscape cell we can refine (identity transform, readable)?</summary>
        static bool IsCell(MeshFilter mf)
        {
            var m = mf.sharedMesh;
            if (m == null || m.name.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (m.name.EndsWith("_relief", StringComparison.Ordinal)) return false;
            if (!m.isReadable) return false;
            var t = mf.transform;
            if ((t.position - Vector3.zero).sqrMagnitude > 1e-6f || Quaternion.Angle(t.rotation, Quaternion.identity) > 0.01f ||
                (t.lossyScale - Vector3.one).sqrMagnitude > 1e-6f) return false;
            return mf.GetComponent<MeshRenderer>() != null;
        }

        /// <summary>A piece of the low-poly horizon (G3LowpolyBackdrop): Phong-smoothed, no detail, no near part.</summary>
        static bool IsBackdrop(MeshFilter mf)
        {
            var m = mf.sharedMesh;
            if (m == null || !m.name.StartsWith("LP_", StringComparison.Ordinal) || !m.isReadable) return false;
            if (mf.GetComponentInParent<G3LowpolyBackdrop>() == null) return false;
            var t = mf.transform;
            if ((t.position - Vector3.zero).sqrMagnitude > 1e-6f || Quaternion.Angle(t.rotation, Quaternion.identity) > 0.01f ||
                (t.lossyScale - Vector3.one).sqrMagnitude > 1e-6f) return false;
            return mf.GetComponent<MeshRenderer>() != null;
        }

        // ------------------------------------------------------------------ the driver

        sealed class Cell
        {
            public MeshFilter filter;
            public Mesh source, mesh;
            public ReliefMesh relief;
            public ReliefOutput far, near, nearPending;
            public ReliefPoint[] reach;   // fix-171: the refine points that reached the cell when its near part was built
            public int capacity;          // vertex buffer capacity of mesh
            public int nearVertices;      // near part in the buffer now
            public bool farQueued, farReady, nearBusy;
            public int version;           // bumped when the cell is released (a late job result is dropped)
            public bool light;            // mesh uploaded, CPU arrays dropped (far from the camera); rebuilt when it comes near
            public bool reheat;           // a light cell being rebuilt (its mesh is rewritten in place)
            public long tris;
            public Mesh retired;          // the previous mesh of a reheated cell until the swap
            public bool backdrop;         // a low-poly horizon piece (facts over all pieces, no near part)
            public Vector3 boundsMin, boundsMax;
            public List<int>[] combined;
            // perf-5: buffers reused by the near batches (a batch allocated a new output with a 150 KB vertex array and a
            // per-triangle flag array per chunk, ~16 chunks per cell: the remaster terrain was ~95 % of the GC load while moving)
            public List<ReliefOutput> parts;   // the chunk outputs of this cell's near build (job thread only)
            public ReliefOutput spareNear;     // the merge target of the next batch (the near part the last apply replaced)
            public object gpu;                 // the batch's GPU data of this cell (Driver.Prepared, reused)
            public int[] indexBuffer;
            public SubMeshDescriptor[] subs;
            public long bytes;
        }

        /// <summary>perf-5: relief mesh writes that had to pack vertices on the main thread (no worker data for them).</summary>
        public static int ReliefMainPacks;

        sealed class Driver : MonoBehaviour
        {
            readonly List<Cell> _cells = new List<Cell>();
            readonly HashSet<MeshFilter> _known = new HashSet<MeshFilter>();
            readonly Queue<Cell> _toRead = new Queue<Cell>();
            readonly List<Cell> _backdropRead = new List<Cell>();
            int _backdropNext;
            readonly ConcurrentQueue<Cell> _farDone = new ConcurrentQueue<Cell>();
            // perf-5: the far mesh's GPU data, built with the far job (UploadFar only copies it into the mesh)
            readonly ConcurrentDictionary<Cell, Prepared> _farGpu = new ConcurrentDictionary<Cell, Prepared>();
            readonly ConcurrentQueue<Action> _jobs = new ConcurrentQueue<Action>();
            readonly AutoResetEvent _wake = new AutoResetEvent(false);
            Thread _thread;
            volatile bool _run;
            volatile int _generation;
            ReliefSettings _s;

            // near batches
            ReliefPoint[] _points = new ReliefPoint[0];
            bool _force;
            volatile bool _batchBusy;
            List<(Cell cell, ReliefMesh relief, ReliefOutput output, ReliefPoint[] reach)> _batchResult;
            Dictionary<Cell, Prepared> _batchGpu;   // perf-5: the batch's GPU data per cell (built on the batch's worker)
            // ground maps (front: read on the main thread; back: written by the worker in a batch)
            const int MapN = 512;
            ReliefGroundMap _g0 = new ReliefGroundMap(MapN, 0.3125f), _g1 = new ReliefGroundMap(MapN, 1f);
            ReliefGroundMap _g0Back = new ReliefGroundMap(MapN, 0.3125f), _g1Back = new ReliefGroundMap(MapN, 1f);
            readonly ushort[] _delta0 = new ushort[2 * MapN * MapN], _delta1 = new ushort[2 * MapN * MapN];
            readonly byte[] _sand0 = new byte[MapN * MapN];   // fx-1: loose-sand mask of map 0 (sand streamers)
            float _base0, _base1;
            volatile bool _mapsReady;
            double _lastMapMs;

            public float SandAt(float x, float y, float z, float tol) => _g0.SandAt(x, y, z, tol);

            public bool SampleGround(float x, float y, float z, out float col, out float vis)
            {
                if (_g0.Sample(x, y, z, out col, out vis)) return true;
                return _g1.Sample(x, y, z, out col, out vis);
            }
            int _batches;
            double _lastBatchMs;

            static readonly VertexAttributeDescriptor[] Layout =
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm16, 4),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm16, 4),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float16, 4),   // fix-171: Ng xyz, strata amplitude
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float16, 4),   // gap, mask, focus, sigma (fix-171)
                new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float16, 4),   // fix-171: fine octaves 0-3
                new VertexAttributeDescriptor(VertexAttribute.TexCoord5, VertexAttributeFormat.Float16, 4),   // fix-171: fine octaves 4-6, marker 1
                new VertexAttributeDescriptor(VertexAttribute.TexCoord6, VertexAttributeFormat.SNorm16, 4),   // fix-171: coarse normal
            };
            /// <summary>
            /// The GPU vertex (88 bytes, <see cref="Layout"/>): normal/tangent as snorm16, uv2-uv5 as half, uv6 snorm16. fix-171: the
            /// position is the coarse point (CPU position minus the fine octaves and strata at the vertex's spacing); G3ReliefMorph.hlsl
            /// adds them back with the band of the live camera distance.
            /// </summary>
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            struct Packed
            {
                public float px, py, pz;
                public short nx, ny, nz, nw;
                public short tx, ty, tz, tw;
                public uint color;
                public float u0, v0, u1, v1;
                public ushort ga, gb, gc, gd;   // uv2
                public ushort a, b, c, d;       // uv3
                public ushort fa, fb, fc, fd;   // uv4
                public ushort ea, eb, ec, ed;   // uv5
                public short ca, cb, cc, cd;    // uv6
            }

            /// <summary>Bytes per GPU vertex (<see cref="Packed"/>).</summary>
            public const int VertexBytes = 88;

            [ThreadStatic] static Packed[] _packed;

            /// <summary>fix-185: second morph marker in uv6.w (G3ReliefMorph.hlsl G3ReliefIsMorphed): uv5.w = 1 alone also matched
            /// meshes without the relief channels.</summary>
            public const float MorphMarkerW = -0.5f;

            static short S(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767);

            /// <summary>
            /// perf-5: the GPU data of one <c>Write</c> (packed vertices, index lists) built on a worker thread from the given far /
            /// near output: the main thread only copies it into the mesh (packing 88-byte vertices with 14 half conversions each and
            /// combining the index lists of a big cell were 25-80 ms in one frame in the editor, the worst streaming hitch of the
            /// remaster terrain). Used only when the cell still has the same relief / far / near.
            /// </summary>
            sealed class Prepared
            {
                public ReliefMesh relief;
                public ReliefOutput far, near;
                public Packed[] farPacked;    // valid when hasFar (the far part is uploaded again only when the buffer grows)
                public bool hasFar;
                public Packed[] nearPacked;   // the first near.VertexCount entries
                public List<int>[] combined;  // swapped with the cell's lists by Write (so a reused Prepared never fills live lists)
            }

            /// <summary>Builds (into <paramref name="into"/> when given: its buffers are reused) the GPU data of far + near.</summary>
            static Prepared BuildGpu(ReliefMesh relief, ReliefOutput far, ReliefOutput near, bool packFar, Prepared into = null)
            {
                if (relief == null || far == null || relief.Sub == null) return null;
                var p = into ?? new Prepared();
                p.relief = relief; p.far = far; p.near = near;
                p.hasFar = packFar;
                if (packFar) p.farPacked = PackInto(far.Vertices, far.VertexCount, p.farPacked);
                if (near != null && near.VertexCount > 0) p.nearPacked = PackInto(near.Vertices, near.VertexCount, p.nearPacked);
                if (p.combined == null || p.combined.Length != relief.Sub.Length)
                {
                    p.combined = new List<int>[relief.Sub.Length];
                    for (int i = 0; i < p.combined.Length; i++) p.combined[i] = new List<int>();
                }
                TerrainRelief.Combine(relief, far, near, p.combined);
                return p;
            }

            /// <summary>Packs into <paramref name="buffer"/>, grown when too small (any thread).</summary>
            static Packed[] PackInto(ReliefVertex[] src, int n, Packed[] buffer)
            {
                if (buffer == null || buffer.Length < n) buffer = new Packed[Mathf.NextPowerOfTwo(Mathf.Max(n, 1024))];
                return Pack(src, n, buffer, true);
            }

            static Packed[] Pack(ReliefVertex[] src, int n)
            {
                var buf = _packed;
                if (buf == null || buf.Length < n) _packed = buf = new Packed[Mathf.NextPowerOfTwo(Mathf.Max(n, 1024))];
                return Pack(src, n, buf, true);
            }

            /// <summary>Packs the first <paramref name="n"/> vertices into <paramref name="dst"/> (large enough; any thread).</summary>
            static Packed[] Pack(ReliefVertex[] src, int n, Packed[] dst, bool _)
            {
                for (int i = 0; i < n; i++)
                {
                    ref var v = ref src[i];
                    var p = _legacy ? v.Position : v.Position - v.FineOffset * v.Ng;
                    dst[i] = new Packed
                    {
                        px = p.X, py = p.Y, pz = p.Z,
                        nx = S(v.Normal.X), ny = S(v.Normal.Y), nz = S(v.Normal.Z),
                        tx = S(v.Tangent.X), ty = S(v.Tangent.Y), tz = S(v.Tangent.Z), tw = S(v.Tangent.W),
                        color = v.Color, u0 = v.Uv0.X, v0 = v.Uv0.Y, u1 = v.Uv1.X, v1 = v.Uv1.Y,
                        ga = Mathf.FloatToHalf(v.Ng.X), gb = Mathf.FloatToHalf(v.Ng.Y), gc = Mathf.FloatToHalf(v.Ng.Z), gd = Mathf.FloatToHalf(v.StrataAmp),
                        a = Mathf.FloatToHalf(v.Uv3.X), b = Mathf.FloatToHalf(v.Uv3.Y), c = Mathf.FloatToHalf(v.Uv3.Z), d = Mathf.FloatToHalf(v.Sigma > 0 ? v.Sigma : 1000f),
                        fa = Mathf.FloatToHalf(v.Fine.A), fb = Mathf.FloatToHalf(v.Fine.B), fc = Mathf.FloatToHalf(v.Fine.C), fd = Mathf.FloatToHalf(v.Fine.D),
                        ea = Mathf.FloatToHalf(v.Fine.E), eb = Mathf.FloatToHalf(v.Fine.F), ec = Mathf.FloatToHalf(v.Fine.G), ed = Mathf.FloatToHalf(_legacy ? 0 : 1),
                        ca = S(v.NormalCoarse.X), cb = S(v.NormalCoarse.Y), cc = S(v.NormalCoarse.Z), cd = S(_legacy ? 0 : MorphMarkerW),
                    };
                }
                return dst;
            }

            const MeshUpdateFlags Quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

            public void Begin(ReliefSettings s)
            {
                if (_legacy)
                {
                    // build 21a: no macro octaves (all band-limited by the batch: the slots carry the first seven, the rest is
                    // folded in), no margin, a 6 m grid; Pack writes the full position without the morph marker
                    s = s.Clamped();
                    s.macroLambda = 1e6f; s.nearMargin = 0; s.recenter = 6f; s.skirt = 0;
                }
                _s = s;
                _generation++;
                if (_thread == null || !_thread.IsAlive)
                {
                    _run = true;
                    _thread = new Thread(Work) { IsBackground = true, Name = "G3TerrainRelief", Priority = System.Threading.ThreadPriority.BelowNormal };
                    _thread.Start();
                }
            }

            public void End()
            {
                _generation++;
                while (_jobs.TryDequeue(out _)) { }
                while (_farDone.TryDequeue(out _)) { }
                _toRead.Clear(); _backdropRead.Clear(); _backdropNext = 0;
                foreach (var c in _cells) Restore(c);
                _cells.Clear(); _known.Clear(); _sourceOf.Clear();
                _points = new ReliefPoint[0]; _force = true; _batchResult = null; _applyNext = 0; _hasCentre = false;
                _g0.Ready = _g1.Ready = false; _mapsReady = false;
            }

            void OnDestroy()
            {
                _run = false; _wake.Set();
                foreach (var c in _cells) Restore(c);
                _cells.Clear();
            }

            void Restore(Cell c)
            {
                if (c.filter != null && c.filter.sharedMesh != null && (c.filter.sharedMesh == c.mesh || c.filter.sharedMesh == c.retired))
                {
                    var f = c.filter; var src = c.source;
                    f.sharedMesh = src;
                }
                // dx12-3: meshes go a few frames later (G3GpuSafe), after the frames (and the GPU Resident Drawer) that drew them
                if (c.mesh != null) { _sourceOf.Remove(c.mesh); G3GpuSafe.Destroy(c.mesh); }
                if (c.retired != null) { _sourceOf.Remove(c.retired); G3GpuSafe.Destroy(c.retired); c.retired = null; }
                c.mesh = null;
            }

            public void Scan(Scene scene)
            {
                if (!scene.IsValid() || !scene.isLoaded) return;
                var filters = new List<MeshFilter>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    root.GetComponentsInChildren(true, filters);
                    foreach (var mf in filters)
                    {
                        if (_known.Contains(mf)) continue;
                        bool cell = IsCell(mf), back = !cell && _s.backdrop && IsBackdrop(mf);
                        if (!cell && !back) continue;
                        _known.Add(mf);
                        var c = new Cell { filter = mf, source = mf.sharedMesh, backdrop = back };
                        var mb = mf.sharedMesh.bounds;
                        c.boundsMin = mb.min; c.boundsMax = mb.max;
                        _cells.Add(c);
                        if (back) _backdropRead.Add(c);   // cells are queued by distance (Stream)
                    }
                }
            }

            void Work()
            {
                while (_run)
                {
                    if (_jobs.TryDequeue(out var job))
                    {
                        try { job(); } catch (Exception e) { Debug.LogException(e); }
                        continue;
                    }
                    _wake.WaitOne(200);
                }
            }

            void Queue(Action a) { _jobs.Enqueue(a); _wake.Set(); }

            static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("G3TerrainRelief.Update");   // snow3 perf

            void Update()
            {
                if (!_active) return;
                using var _pm = UpdateMarker.Auto();
                // drop cells whose scene went away
                for (int i = _cells.Count - 1; i >= 0; i--)
                    if (_cells[i].filter == null)
                    {
                        var c = _cells[i];
                        if (c.mesh != null) { _sourceOf.Remove(c.mesh); G3GpuSafe.Destroy(c.mesh); }
                        _cells.RemoveAt(i);
                    }
                _known.RemoveWhere(f => f == null);

                SetMorphGlobals();
                if (Time.unscaledTime >= _nextStream) { _nextStream = Time.unscaledTime + 0.5f; Stream(); }
                ReadSources(2.0);
                ReadBackdrop(1.0);
                UploadFar(2);
                UpdateNear();
            }

            float _nextStream;

            /// <summary>
            /// fix-171: the globals of G3ReliefMorph.hlsl: the live eye (the main camera; every pass of every camera fades by
            /// the same distance, so shadows and depth match the colour pass), the near band (spacing, slope, far spacing, strata
            /// wavelength) and the wavelengths of the seven fine slots.
            /// </summary>
            void SetMorphGlobals()
            {
                var cam = Camera.main;
                var e = cam != null ? cam.transform.position : Vector3.zero;
                Shader.SetGlobalVector(EyeId, new Vector4(e.x, e.y, e.z, cam != null ? 1 : 0));
                if (_globalsFor == _s) return;
                _globalsFor = _s;
                Shader.SetGlobalVector(BandId, new Vector4(_s.nearSpacing, _s.nearSlope, _s.farSpacing, _s.strata > 0 ? _s.strataScale : 0));
                Shader.SetGlobalVector(LambdaAId, new Vector4(TerrainRelief.FineLambda(0, _s), TerrainRelief.FineLambda(1, _s), TerrainRelief.FineLambda(2, _s), TerrainRelief.FineLambda(3, _s)));
                Shader.SetGlobalVector(LambdaBId, new Vector4(TerrainRelief.FineLambda(4, _s), TerrainRelief.FineLambda(5, _s), TerrainRelief.FineLambda(6, _s), 0));
            }
            ReliefSettings _globalsFor;
            readonly List<(float d, Cell c)> _wantRead = new List<(float, Cell)>();

            /// <summary>
            /// Cells within <see cref="ReliefSettings.farRadius"/> of the camera get a relief copy (nearest first); beyond the radius +
            /// 300 m the copy is dropped and G3's mesh comes back (memory: the editor loads almost every cell).
            /// </summary>
            void Stream()
            {
                var cam = Camera.main;
                if (cam == null) return;
                var p = cam.transform.position;
                _wantRead.Clear();
                foreach (var c in _cells)
                {
                    if (c.backdrop || c.filter == null) continue;
                    var q = Vector3.Max(c.boundsMin, Vector3.Min(c.boundsMax, p));
                    float d = (q - p).magnitude;
                    if (!c.farQueued && d < _s.farRadius) _wantRead.Add((d, c));
                    else if (c.farQueued && d > _s.farRadius + 300f && c.near == null) Release(c);
                    else if (c.farReady && c.light && !c.reheat && d < KeepRadius) { c.reheat = true; c.light = false; _toRead.Enqueue(c); }
                    else if (c.farReady && !c.light && !c.reheat && c.near == null && !_batchBusy && d > KeepRadius + 150f) MakeLight(c);
                }
                if (_wantRead.Count == 0) return;
                _wantRead.Sort((a, b) => a.d.CompareTo(b.d));
                foreach (var (_, c) in _wantRead) { c.farQueued = true; _toRead.Enqueue(c); }
            }

            /// <summary>Cells closer than this keep their CPU arrays (near parts need them): the camera's near radius + 150 m.</summary>
            float KeepRadius => _s.NearRadius + 150f;

            float DistanceTo(Cell c)
            {
                var cam = Camera.main;
                if (cam == null) return 0;
                var p = cam.transform.position;
                return (Vector3.Max(c.boundsMin, Vector3.Min(c.boundsMax, p)) - p).magnitude;
            }

            /// <summary>Drops the CPU copies of a cell's build (the mesh stays): ~2/3 of its memory.</summary>
            static void MakeLight(Cell c)
            {
                c.light = true; c.relief = null; c.far = null; c.combined = null; c.indexBuffer = null; c.subs = null;
                if (c.mesh != null) c.mesh.UploadMeshData(true);   // GPU copy only: half the memory; rebuilt into a new mesh when it comes near
            }



            void Release(Cell c)
            {
                Restore(c);
                c.version++;
                c.relief = null; c.far = null; c.near = null; c.reach = null; c.farReady = false; c.farQueued = false; c.combined = null; c.indexBuffer = null; c.bytes = 0;
            }

            /// <summary>Copies the source arrays of waiting cells (main thread, within <paramref name="budgetMs"/>) and queues their far build.</summary>
            void ReadSources(double budgetMs)
            {
                if (_toRead.Count == 0) return;
                using var _ = ReadMarker.Auto();
                var t0 = Time.realtimeSinceStartupAsDouble;
                while (_toRead.Count > 0 && (Time.realtimeSinceStartupAsDouble - t0) * 1000 < budgetMs)
                {
                    var c = _toRead.Dequeue();
                    if (c.filter == null || c.source == null) continue;
                    var r = ReadMesh(c);
                    int gen = _generation, ver = c.version;
                    string name = c.source.name;
                    var s0 = _s;
                    Queue(() =>
                    {
                        if (gen != _generation) return;
                        TerrainRelief.Weld(r);
                        var facts = FactsFor(name, r.WeldCount);
                        if (facts == null) Debug.LogWarning($"[Relief] {name}: not in the facts table (weld {r.WeldCount}), own faces");
                        TerrainRelief.Prepare(r, s0, facts);
                        var far = new ReliefOutput();
                        TerrainRelief.BuildFar(r, s0, far);
                        if (gen != _generation || ver != c.version) return;
                        Array.Resize(ref far.Vertices, far.VertexCount);   // the doubling slack would stay for the cell's life
                        var gpu = BuildGpu(r, far, null, packFar: true);
                        if (gpu != null) _farGpu[c] = gpu;
                        c.far = far;
                        _farDone.Enqueue(c);
                    });
                }
            }

            /// <summary>Copies a source mesh into a <see cref="ReliefMesh"/> (main thread).</summary>
            static ReliefMesh ReadMesh(Cell c)
            {
                var m = c.source;
                var r = new ReliefMesh();
                var v3 = new List<Vector3>(); var v2 = new List<Vector2>(); var v4 = new List<Vector4>(); var col = new List<Color32>();
                m.GetVertices(v3); r.Pos = Conv(v3);
                m.GetNormals(v3); r.Nrm = v3.Count == r.Pos.Length ? Conv(v3) : Fill(r.Pos.Length, NV3.UnitY);
                m.GetTangents(v4); r.Tan = v4.Count == r.Pos.Length ? Conv(v4) : null;
                m.GetUVs(0, v2); r.Uv0 = v2.Count == r.Pos.Length ? Conv(v2) : new NV2[r.Pos.Length];
                m.GetUVs(1, v2); r.Uv1 = v2.Count == r.Pos.Length ? Conv(v2) : null;
                m.GetColors(col);
                if (col.Count == r.Pos.Length)
                {
                    r.Col = new uint[col.Count];
                    for (int i = 0; i < col.Count; i++) r.Col[i] = col[i].r | (uint)col[i].g << 8 | (uint)col[i].b << 16 | (uint)col[i].a << 24;
                }
                var mats = c.filter.GetComponent<MeshRenderer>().sharedMaterials;
                r.Sub = new int[m.subMeshCount][]; r.SubAmp = new float[m.subMeshCount]; r.SubPhong = new float[m.subMeshCount];
                for (int s = 0; s < m.subMeshCount; s++)
                {
                    r.Sub[s] = m.GetTriangles(s);
                    // the material name is G3's (the remaster copies keep it as a prefix)
                    var (a, p) = ReliefMaterials.Mask(s < mats.Length && mats[s] != null ? mats[s].name : null);
                    r.SubAmp[s] = a; r.SubPhong[s] = p;
                    // fx-1: loose sand of the layers (streamer mask, ReliefGroundMap.Sand)
                    var (la, lb) = G3SandV2.LooseLayers(s < mats.Length ? mats[s] : null);
                    if (la > 0 || lb > 0)
                    {
                        r.SubLooseA ??= new float[m.subMeshCount]; r.SubLooseB ??= new float[m.subMeshCount];
                        r.SubLooseA[s] = la; r.SubLooseB[s] = lb;
                    }
                }
                c.relief = r;
                var b = m.bounds;
                c.boundsMin = b.min; c.boundsMax = b.max;
                return r;
            }

            /// <summary>
            /// The horizon pieces: read a few per frame; when all are read, one job accumulates their facts together (the pieces
            /// duplicate the triangles on their squares' borders) and builds every piece's Phong-only far mesh.
            /// </summary>
            void ReadBackdrop(double budgetMs)
            {
                if (_backdropNext >= _backdropRead.Count || _toRead.Count > 0) return;
                var t0 = Time.realtimeSinceStartupAsDouble;
                while (_backdropNext < _backdropRead.Count && (Time.realtimeSinceStartupAsDouble - t0) * 1000 < budgetMs)
                {
                    var c = _backdropRead[_backdropNext++];
                    if (c.filter != null && c.source != null) { ReadMesh(c); c.farQueued = true; }
                }
                if (_backdropNext < _backdropRead.Count) return;
                var pieces = new List<Cell>(_backdropRead);
                int gen = _generation;
                var sb = _s.ForBackdrop();
                Queue(() =>
                {
                    var facts = new ReliefFacts(sb.walkNy);
                    foreach (var c in pieces) if (c.relief != null) { TerrainRelief.Weld(c.relief); facts.Add(c.relief); }
                    foreach (var c in pieces)
                    {
                        if (gen != _generation) return;
                        if (c.relief == null) continue;
                        TerrainRelief.Prepare(c.relief, sb, facts.For(c.relief));
                        var far = new ReliefOutput();
                        TerrainRelief.BuildFar(c.relief, sb, far);
                        var gpu = BuildGpu(c.relief, far, null, packFar: true);
                        if (gpu != null) _farGpu[c] = gpu;
                        c.far = far;
                        _farDone.Enqueue(c);
                    }
                });
            }

            static NV3[] Conv(List<Vector3> l) { var a = new NV3[l.Count]; for (int i = 0; i < a.Length; i++) a[i] = new NV3(l[i].x, l[i].y, l[i].z); return a; }
            static NV2[] Conv(List<Vector2> l) { var a = new NV2[l.Count]; for (int i = 0; i < a.Length; i++) a[i] = new NV2(l[i].x, l[i].y); return a; }
            static NV4[] Conv(List<Vector4> l) { var a = new NV4[l.Count]; for (int i = 0; i < a.Length; i++) a[i] = new NV4(l[i].x, l[i].y, l[i].z, l[i].w); return a; }
            static NV3[] Fill(int n, NV3 v) { var a = new NV3[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

            /// <summary>Creates the relief mesh of finished far builds (a few per frame) and swaps it in.</summary>
            void UploadFar(int perFrame)
            {
                var t0 = Time.realtimeSinceStartupAsDouble;
                // fix-171: also a time budget (the 88-byte vertices of a big cell take a few ms to pack and upload)
                while (perFrame-- > 0 && (Time.realtimeSinceStartupAsDouble - t0) * 1000 < 3.0 && _farDone.TryDequeue(out var c))
                {
                    _farGpu.TryRemove(c, out var gpu);
                    if (c.filter == null || c.far == null || !c.farQueued) continue;
                    if (c.reheat)
                    {
                        // a light cell came near: a new readable mesh (the light one gave up its CPU copy), swapped like the first
                        c.reheat = false;
                        var old = c.mesh;
                        c.farReady = false;
                        c.mesh = null;
                        if (old != null) { if (c.retired != null) G3GpuSafe.Destroy(c.retired); c.retired = old; }   // destroyed by the swap below
                    }
                    if (c.farReady) continue;
                    using var _ = UploadMarker.Auto();
                    var mesh = new Mesh { name = c.source.name + "_relief" };
                    mesh.MarkDynamic();
                    c.mesh = mesh;
                    c.capacity = 0;
                    c.near = null; c.reach = null;   // fix-171: the new mesh starts far-only; the next batch gives it its near part
                    c.combined = new List<int>[c.relief.Sub.Length];
                    for (int i = 0; i < c.combined.Length; i++) c.combined[i] = new List<int>();
                    Write(c, null, gpu);
                    c.farReady = true;
                    _sourceOf[mesh] = c.source;
                    var target = c;
                    G3RendererChanges.Enqueue(c.filter.GetComponent<MeshRenderer>(), () =>
                    {
                        if (target.retired != null) { _sourceOf.Remove(target.retired); G3GpuSafe.Destroy(target.retired); target.retired = null; }
                        if (target.filter == null || target.mesh == null || !_active) return 0;
                        target.filter.sharedMesh = target.mesh;
                        G3GpuDiag.Event("relief swap " + target.mesh.name);
                        return 1;
                    }, "terrain relief");
                    if (!c.backdrop) _force = true;   // the near part of the new cell comes with the next batch
                    if (c.backdrop || DistanceTo(c) > KeepRadius) MakeLight(c);
                }
            }

            /// <summary>Writes far + <paramref name="near"/> (null = none) into the cell's mesh.</summary>
            static readonly ProfilerMarker WriteVerticesMarker = new ProfilerMarker("G3TerrainRelief.WriteVertices");
            static readonly ProfilerMarker WriteIndicesMarker = new ProfilerMarker("G3TerrainRelief.WriteIndices");

            void Write(Cell c, ReliefOutput near, Prepared pre = null)
            {
                var mesh = c.mesh;
                int nf = c.far.VertexCount, nn = near != null ? near.VertexCount : 0;
                // perf-5: data packed on a worker, if it was built from what the cell has now
                if (pre != null && (pre.relief != c.relief || pre.far != c.far || pre.near != near || c.combined == null ||
                                    pre.combined.Length != c.combined.Length)) pre = null;
                bool grow = nf + nn > c.capacity;
                G3GpuDiag.Count(grow ? "relief.write.grow" : "relief.write");
                if ((grow && (pre == null || !pre.hasFar)) || (nn > 0 && (pre == null || pre.nearPacked == null))) ReliefMainPacks++;
                WriteVerticesMarker.Begin();
                if (grow)
                {
                    c.capacity = nf + (nn > 0 ? Mathf.CeilToInt(nn * 1.5f) : 0);
                    mesh.SetVertexBufferParams(c.capacity, Layout);
                    mesh.SetVertexBufferData(pre != null && pre.hasFar ? pre.farPacked : Pack(c.far.Vertices, nf), 0, 0, nf, 0, Quiet);
                }
                if (nn > 0) mesh.SetVertexBufferData(pre != null && pre.nearPacked != null ? pre.nearPacked : Pack(near.Vertices, nn), 0, nf, nn, 0, Quiet);
                c.nearVertices = nn;
                WriteVerticesMarker.End();
                using var im = WriteIndicesMarker.Auto();
                if (pre != null) (c.combined, pre.combined) = (pre.combined, c.combined);
                else TerrainRelief.Combine(c.relief, c.far, near, c.combined);
                int total = 0;
                foreach (var l in c.combined)
                {
                    if (l.Count == 0) { l.Add(0); l.Add(0); l.Add(0); }   // a degenerate triangle: empty submeshes would share index ranges
                    total += l.Count;
                }
                if (c.indexBuffer == null || c.indexBuffer.Length < total) c.indexBuffer = new int[Mathf.Max(total, 3)];
                int k = 0;
                foreach (var l in c.combined) { l.CopyTo(c.indexBuffer, k); k += l.Count; }
                mesh.SetIndexBufferParams(total, IndexFormat.UInt32);
                mesh.SetIndexBufferData(c.indexBuffer, 0, 0, total, Quiet);
                int start = 0;
                var bounds = new Bounds();
                bounds.SetMinMax(c.boundsMin - Vector3.one * 3f, c.boundsMax + Vector3.one * 3f);
                if (c.subs == null || c.subs.Length != c.combined.Length) c.subs = new SubMeshDescriptor[c.combined.Length];
                for (int i = 0; i < c.combined.Length; i++)
                {
                    c.subs[i] = new SubMeshDescriptor(start, c.combined[i].Count) { bounds = bounds, firstVertex = 0, vertexCount = c.capacity };
                    start += c.combined[i].Count;
                }
                mesh.SetSubMeshes(c.subs, Quiet);   // all at once: one by one the new ranges overlap the old ones
                mesh.bounds = bounds;
                c.bytes = (long)c.capacity * VertexBytes + total * 4L;
                c.tris = total / 3;
            }

            /// <summary>Re-centres the refine points and, when they moved, rebuilds the near parts of every cell they reach in one batch.</summary>
            void UpdateNear()
            {
                if (_batchResult != null && !_batchBusy)
                {
                    using var _ = NearMarker.Auto();
                    // fix-171: the cells of a batch are written over a few frames (3 ms per frame; the skirts hide the short
                    // border mismatch); the ground maps change with the last one (a batch of 10 cells was one 10-30 ms frame)
                    var t0 = Time.realtimeSinceStartupAsDouble;
                    while (_applyNext < _batchResult.Count)
                    {
                        if (_applyNext > 0 && (Time.realtimeSinceStartupAsDouble - t0) * 1000 > ApplyBudgetMs) return;
                        var (cell, relief, o, reach) = _batchResult[_applyNext++];
                        if (cell.filter == null || cell.mesh == null || !cell.farReady || cell.light || cell.reheat || cell.far == null || cell.relief != relief)
                        {
                            // fix-171: a cell that cannot take its new part does not keep an old one (it would not match its neighbours)
                            if (cell.near != null && cell.mesh != null && cell.far != null && !cell.light && !cell.reheat && cell.relief != null && cell.farReady) { cell.near = null; cell.reach = null; Write(cell, null); }
                            if (o != null && o != cell.near) cell.spareNear = o;   // perf-5: its buffers serve the next batch
                            continue;
                        }
                        var oldNear = cell.near;
                        cell.near = o != null && o.NearTriangles > 0 ? o : null;
                        cell.reach = reach;
                        Write(cell, cell.near, _batchGpu != null && _batchGpu.TryGetValue(cell, out var gpu) ? gpu : null);
                        // perf-5: the replaced part (or an empty result) is the next batch's merge target
                        if (oldNear != null && oldNear != cell.near) cell.spareNear = oldNear;
                        else if (o != null && o != cell.near) cell.spareNear = o;
                    }
                    _batchResult = null;
                    _batchGpu = null;
                    _applyNext = 0;
                    NearSwaps++;
                    if (_mapsReady)
                    {
                        // the meshes and the ground under things change in the same frame
                        (_g0, _g0Back) = (_g0Back, _g0); (_g1, _g1Back) = (_g1Back, _g1);
                        _mapsReady = false;
                        G3ReliefGroundGpu.Upload(_g0, _delta0, _base0, _g1, _delta1, _base1, _sand0);
                        GroundVersion++;
                        if (_resetPending) { _resetPending = false; RaiseReset(); }
                    }
                }
                if (!_s.near || _batchBusy || HoldNear) return;
                if (!Points(out var pts)) return;
                _points = pts; _force = false;
                // every ready cell the points reach, plus every cell that has a near part now (to clear it); fix-171: a cell
                // whose reaching points did not change keeps its part (a moving snow focus rebuilds only the cells round it)
                var batch = new List<(Cell cell, ReliefPoint[] reach)>();
                var reachBuf = new List<ReliefPoint>(pts.Length);
                foreach (var c in _cells)
                {
                    if (!c.farReady || c.filter == null || c.backdrop || c.light || c.reheat) continue;
                    reachBuf.Clear();
                    foreach (var p in pts)
                    {
                        float r = p.Radius(_s.farSpacing);
                        var q = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
                        var cl = Vector3.Max(c.boundsMin, Vector3.Min(c.boundsMax, q));
                        if ((cl - q).magnitude < r) reachBuf.Add(p);
                    }
                    if (reachBuf.Count == 0 && c.near == null) continue;
                    if (c.near != null && c.reach != null && SameReach(c.reach, reachBuf)) continue;
                    batch.Add((c, reachBuf.ToArray()));
                }
                if (batch.Count == 0) return;
                _batchBusy = true;
                int gen = _generation;
                var s0 = _s;
                // each cell's prepared mesh is captured now: the main thread may drop or replace it meanwhile
                var work = new List<(Cell cell, ReliefMesh relief, ReliefPoint[] reach)>(batch.Count);
                foreach (var (c, reach) in batch) if (c.relief != null && c.relief.Prepared) work.Add((c, c.relief, reach));
                var farOf = new Dictionary<Cell, (ReliefOutput far, int capacity)>(work.Count);
                foreach (var w in work) farOf[w.cell] = (w.cell.far, w.cell.capacity);
                // the ground maps round the camera point: every ready cell they overlap (far meshes; near parts from this batch, else
                // the part the cell keeps)
                bool maps = pts.Length > 0 && pts[0].Shape;
                var centre = maps ? pts[0].Position : default;
                var mapCells = new List<(Cell cell, ReliefMesh relief, ReliefOutput far, ReliefOutput near)>();
                if (maps)
                {
                    float half = _g1Back.Size * 0.5f + 2f;
                    foreach (var c in _cells)
                    {
                        if (!c.farReady || c.backdrop || c.light || c.reheat || c.relief == null || !c.relief.Prepared || c.far == null || c.far.TriStart == null) continue;
                        if (c.boundsMax.x < centre.X - half || c.boundsMin.x > centre.X + half || c.boundsMax.z < centre.Z - half || c.boundsMin.z > centre.Z + half) continue;
                        mapCells.Add((c, c.relief, c.far, c.near));
                    }
                }
                var g0 = _g0Back; var g1 = _g1Back; var d0 = _delta0; var d1 = _delta1; var s0m = _sand0;
                Queue(() =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var result = new (Cell, ReliefMesh, ReliefOutput, ReliefPoint[])[work.Count];
                        // fix-171: chunks of source triangles of every cell in parallel (a few pool threads; the batch took up to
                        // 2.7 s on one thread), joined per cell in triangle order (the same output as one build)
                        var chunks = new List<(int wi, int from, int to)>();
                        for (int wi = 0; wi < work.Count; wi++)
                        {
                            var r = work[wi].relief;
                            if (!TerrainRelief.Reaches(r, pts, pts.Length, s0)) { chunks.Add((wi, 0, 0)); continue; }
                            int step = Math.Max(256, r.TriCount / 16);
                            for (int f = 0; f < r.TriCount; f += step) chunks.Add((wi, f, Math.Min(r.TriCount, f + step)));
                        }
                        var parts = new ReliefOutput[chunks.Count];
                        // perf-5: the j-th chunk of a cell reuses the cell's j-th output (batches run one at a time)
                        var pooled = new ReliefOutput[chunks.Count];
                        for (int k = 0, j = 0, prev = -1; k < chunks.Count; k++)
                        {
                            int wi = chunks[k].wi;
                            j = wi == prev ? j + 1 : 0;
                            prev = wi;
                            var cl = work[wi].cell;
                            cl.parts ??= new List<ReliefOutput>();
                            while (cl.parts.Count <= j) cl.parts.Add(new ReliefOutput());
                            pooled[k] = cl.parts[j];
                        }
                        Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = BatchThreads }, k =>
                        {
                            if (gen != _generation) return;
                            var (wi, from, to) = chunks[k];
                            var o = pooled[k];
                            TerrainRelief.BuildNear(work[wi].relief, pts, pts.Length, s0, o, from, to);
                            parts[k] = o;
                        });
                        if (gen == _generation)
                            for (int k = 0, wi = -1; k < chunks.Count; )
                            {
                                wi = chunks[k].wi;
                                var list = new List<ReliefOutput>();
                                while (k < chunks.Count && chunks[k].wi == wi) { if (parts[k] != null) list.Add(parts[k]); k++; }
                                var (c, r, reach) = work[wi];
                                var into = c.spareNear ?? new ReliefOutput();
                                c.spareNear = null;
                                result[wi] = (c, r, list.Count > 0 ? TerrainRelief.Merge(r, list, into) : into.Cleared(r), reach);
                            }
                        _lastBatchMs = sw.Elapsed.TotalMilliseconds;
                        _lastBatchCells = work.Count;
                        if (maps && gen == _generation)
                        {
                            var ms = System.Diagnostics.Stopwatch.StartNew();
                            var nearOf = new Dictionary<Cell, ReliefOutput>();
                            foreach (var (c, r, o, _) in result) if (c != null) nearOf[c] = o != null && o.NearTriangles > 0 ? o : null;
                            Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, gi =>
                            {
                                var g = gi == 0 ? g0 : g1;
                                g.Begin(centre.X, centre.Z);
                                foreach (var (c, r, far, keep) in mapCells) ReliefMapRaster.Collision(g, r);
                                foreach (var (c, r, far, keep) in mapCells)
                                    ReliefMapRaster.Visual(g, r, far, nearOf.TryGetValue(c, out var no) ? no : keep);
                                g.Ready = true;
                            });
                            _base0 = g0.BaseHeight(); _base1 = g1.BaseHeight();
                            g0.DeltaHalf(d0, _base0); g1.DeltaHalf(d1, _base1);
                            g0.SandBytes(s0m);
                            _lastMapMs = ms.Elapsed.TotalMilliseconds;
                            _mapsReady = true;
                        }
                        if (gen == _generation)
                        {
                            var list = new List<(Cell, ReliefMesh, ReliefOutput, ReliefPoint[])>(result.Length);
                            foreach (var x in result) if (x.Item1 != null) list.Add(x);
                            // perf-5: pack the vertices and combine the index lists here, not in the apply frames
                            var gpus = new Prepared[list.Count];
                            Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = BatchThreads }, k =>
                            {
                                var (c, r, o, _) = list[k];
                                if (!farOf.TryGetValue(c, out var f) || f.far == null) return;
                                var near = o != null && o.NearTriangles > 0 ? o : null;
                                int nn = near != null ? near.VertexCount : 0;
                                gpus[k] = BuildGpu(r, f.far, near, packFar: f.far.VertexCount + nn > f.capacity, c.gpu as Prepared);
                                c.gpu = gpus[k];
                            });
                            var byCell = new Dictionary<Cell, Prepared>(list.Count);
                            for (int k = 0; k < list.Count; k++) if (gpus[k] != null) byCell[list[k].Item1] = gpus[k];
                            _batchGpu = byCell;
                            _batchResult = list;
                        }
                        _batches++;
                    }
                    finally { _batchBusy = false; }
                });
            }

            int _applyNext;
            const double ApplyBudgetMs = 3.0;

            /// <summary>Worker threads of one near batch: a quarter of the cores (at least 1, at most 4): the job workers keep the rest.</summary>
            static readonly int BatchThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 4));

            int _lastBatchCells;

            static bool SameReach(ReliefPoint[] a, List<ReliefPoint> b)
            {
                if (a.Length != b.Count) return false;
                for (int i = 0; i < a.Length; i++)
                {
                    var x = a[i]; var y = b[i];
                    if (x.Position != y.Position || x.Spacing != y.Spacing || x.Slope != y.Slope || x.Margin != y.Margin || x.Shape != y.Shape || x.MaxSpacing != y.MaxSpacing) return false;
                }
                return true;
            }

            readonly List<ReliefPoint> _want = new List<ReliefPoint>();
            Vector3 _centre;
            bool _hasCentre;

            /// <summary>
            /// The refine points; true when they differ from the last batch's (no garbage per frame). fix-171: the camera point
            /// stays where it is until the camera is <see cref="ReliefSettings.recenter"/> m away (hysteresis, was a 6 m grid that
            /// flipped back and forth on a grid line), then moves to the camera; it carries the near margin (the mesh is dense
            /// enough for the camera anywhere within the margin until the next batch).
            /// </summary>
            bool Points(out ReliefPoint[] points)
            {
                _want.Clear();
                var cam = Camera.main;
                if (cam != null)
                {
                    var p = cam.transform.position;
                    if (_legacy) { float g = _s.recenter; _centre = new Vector3(Mathf.Round(p.x / g) * g, Mathf.Round(p.y / g) * g, Mathf.Round(p.z / g) * g); _hasCentre = true; }
                    else if (!_hasCentre || _force || (p - _centre).magnitude > _s.recenter) { _centre = p; _hasCentre = true; }
                    _want.Add(new ReliefPoint(new NV3(_centre.x, _centre.y, _centre.z), _s.nearSpacing, _s.nearSlope, true, float.MaxValue, _s.nearMargin));
                }
                foreach (var kv in _focus)
                {
                    var f = kv.Value;
                    _want.Add(new ReliefPoint(new NV3(Mathf.Round(f.position.x / f.grid) * f.grid, Mathf.Round(f.position.y / f.grid) * f.grid,
                                                      Mathf.Round(f.position.z / f.grid) * f.grid), f.spacing, f.slope, false, f.max));
                }
                bool same = _want.Count == _points.Length && !_force;
                for (int i = 0; same && i < _want.Count; i++)
                    same = _want[i].Position == _points[i].Position && _want[i].Spacing == _points[i].Spacing && _want[i].Slope == _points[i].Slope && _want[i].Margin == _points[i].Margin;
                points = same ? _points : _want.ToArray();
                return !same;
            }

            static long SourceTris(Mesh m)
            {
                long n = 0;
                for (int i = 0; i < m.subMeshCount; i++) n += m.GetSubMesh(i).indexCount / 3;
                return n;
            }

            /// <summary>
            /// fix-171 runner: the open edges of the drawn relief surface (every ready cell within <paramref name="radius"/> m of
            /// <paramref name="around"/>, far + near parts as written into the meshes, positions as the shader puts them for the
            /// eye at <paramref name="eye"/>): an edge whose reverse is in no triangle of any cell is a crack, unless it lies
            /// on the outer outline of the region. Skirt triangles are left out (they hide cracks, the audit looks for them).
            /// </summary>
            public string AuditSeams(Vector3 around, float radius, Vector3 eye)
            {
                var edges = new Dictionary<(NV3, NV3), int>();
                var where = new Dictionary<(NV3, NV3), string>();
                int cellsUsed = 0, nearCells = 0;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var c in _cells)
                {
                    if (!c.farReady || c.backdrop || c.light || c.far == null || c.relief == null || c.combined == null) continue;
                    var q = Vector3.Max(c.boundsMin, Vector3.Min(c.boundsMax, around));
                    if ((q - around).magnitude > radius) continue;
                    cellsUsed++;
                    if (c.near != null) nearCells++;
                    minX = Math.Min(minX, c.boundsMin.x); maxX = Math.Max(maxX, c.boundsMax.x); minZ = Math.Min(minZ, c.boundsMin.z); maxZ = Math.Max(maxZ, c.boundsMax.z);
                    var far = c.far; var near = c.near;
                    NV3 P(int i)
                    {
                        var v = i < far.VertexCount ? far.Vertices[i] : near.Vertices[i - far.VertexCount];
                        return GpuPosition(v, eye);
                    }
                    string state = $"{c.source.name}{(c.near != null ? " near" : " far")}";
                    foreach (var l in c.combined)
                        for (int i = 0; i + 2 < l.Count; i += 3)
                        {
                            var a = P(l[i]); var b = P(l[i + 1]); var d = P(l[i + 2]);
                            // skirts: vertical triangles (no XZ area) are not ground
                            float area = (b.X - a.X) * (d.Z - a.Z) - (d.X - a.X) * (b.Z - a.Z);
                            if (Math.Abs(area) < 1e-6f) continue;
                            foreach (var e in new[] { (a, b), (b, d), (d, a) })
                            {
                                edges.TryGetValue(e, out int n); edges[e] = n + 1;
                                if (n == 0) where[e] = state;
                            }
                        }
                }
                int open = 0; float openLen = 0;
                var worst = new List<(float len, string text)>();
                foreach (var kv in edges)
                {
                    var (a, b) = kv.Key;
                    if (edges.ContainsKey((b, a))) continue;
                    bool Edge(float v, float lo, float hi) => Math.Abs(v - lo) < 1.5f || Math.Abs(v - hi) < 1.5f;
                    if ((Edge(a.X, minX, maxX) && Edge(b.X, minX, maxX) && Math.Abs(a.X - b.X) < 1.5f) ||
                        (Edge(a.Z, minZ, maxZ) && Edge(b.Z, minZ, maxZ) && Math.Abs(a.Z - b.Z) < 1.5f)) continue;   // the region outline
                    float len = NV3.Distance(a, b);
                    open++; openLen += len;
                    worst.Add((len, $"({a.X:F1},{a.Y:F1},{a.Z:F1})-({b.X:F1},{b.Y:F1},{b.Z:F1}) {len:F2} m {where[kv.Key]}"));
                }
                worst.Sort((x, y) => y.len.CompareTo(x.len));
                var sb = new System.Text.StringBuilder();
                sb.Append($"seams: {cellsUsed} cells ({nearCells} with near parts), {edges.Count} edges, open {open} ({openLen:F1} m)");
                for (int i = 0; i < worst.Count && i < 8; i++) sb.Append("\n  ").Append(worst[i].text);
                return sb.ToString();
            }

            /// <summary>The shader's vertex position (G3ReliefMorph.hlsl) for the eye at <paramref name="eye"/>, in C#.</summary>
            NV3 GpuPosition(in ReliefVertex v, Vector3 eye)
            {
                var p0 = v.Position - v.FineOffset * v.Ng;
                if (v.Sigma >= _s.farSpacing - 1e-3f) return p0;
                float d = NV3.Distance(p0, new NV3(eye.x, eye.y, eye.z));
                float sigma = Math.Max(Math.Min(Math.Max(_s.nearSpacing + _s.nearSlope * d, _s.nearSpacing), _s.farSpacing), v.Sigma);
                float fine = 0;
                for (int k = 0; k < FineOctaves.Count; k++) fine += ReliefNoise.Band(TerrainRelief.FineLambda(k, _s), sigma) * v.Fine[k];
                float strata = v.StrataAmp > 0 && _s.strata > 0 ? v.StrataAmp * ReliefNoise.Band(_s.strataScale, sigma) * ReliefNoise.Strata(p0, _s.strataScale, 1e-3f) : 0;
                return p0 + (fine + strata) * v.Ng;
            }

            public string FindSteep(Vector3 around, float range, float radius, float minAmp)
            {
                var pts = new List<(Vector3 p, Vector3 n)>();
                foreach (var c in _cells)
                {
                    var r = c.relief;
                    if (r == null || !r.Prepared || r.WAmp == null) continue;
                    var q = Vector3.Max(c.boundsMin, Vector3.Min(c.boundsMax, around));
                    if ((q - around).magnitude > range) continue;
                    for (int w = 0; w < r.WeldCount; w++)
                    {
                        if (r.WAmp[w] < minAmp) continue;
                        var p = new Vector3(r.WPos[w].X, r.WPos[w].Y, r.WPos[w].Z);
                        if ((p - around).magnitude > range) continue;
                        pts.Add((p, new Vector3(r.WNrm[w].X, r.WNrm[w].Y, r.WNrm[w].Z)));
                    }
                }
                if (pts.Count == 0) return "none";
                int best = -1, bestN = 0;
                float r2 = radius * radius;
                for (int i = 0; i < pts.Count; i += Math.Max(1, pts.Count / 2000))
                {
                    int n = 0;
                    foreach (var o in pts) if ((o.p - pts[i].p).sqrMagnitude < r2) n++;
                    if (n > bestN) { bestN = n; best = i; }
                }
                var b = pts[best];
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                return string.Format(ic, "{0:F1} {1:F1} {2:F1} {3:F2} {4:F2} {5:F2} {6}", b.p.x, b.p.y, b.p.z, b.n.x, b.n.y, b.n.z, bestN);
            }

            public string Describe()
            {
                int ready = 0, near = 0; long bytes = 0, tris = 0, srcTris = 0;
                foreach (var c in _cells)
                {
                    if (!c.farReady) continue;
                    ready++; bytes += c.bytes;
                    if (c.near != null) near++;
                    tris += c.tris;
                    srcTris += c.source != null ? SourceTris(c.source) : 0;
                }
                return $"relief on: {ready}/{_cells.Count} cells, {srcTris} -> {tris} tris (x{(srcTris > 0 ? (double)tris / srcTris : 0):F2}), " +
                       $"{bytes / 1048576.0:F1} MB meshes (+ source copies), near parts {near}, batches {_batches} (last {_lastBatchMs:F0} ms worker), " +
                       $"cells per batch {_lastBatchCells}, waiting read {_toRead.Count} jobs {_jobs.Count}; ground maps {(_g0.Ready ? "ready" : "none")} (last {_lastMapMs:F0} ms worker), version {GroundVersion}";
            }
        }
    }
}
