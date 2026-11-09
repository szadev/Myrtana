using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using G3.Player;
using G3.Unity.Characters;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Ground cover scatter (Docs/remaster-world.md section 18, remaster-veg), with the Grass remaster part (json
    /// <c>scatter</c>): ferns, flowers, herbs, underbrush, leaf litter, needles, moss, twigs, mushrooms, pebbles, rocks, reeds,
    /// dry grass, shrubs and cacti on 16 m tiles round the camera, placed by <see cref="ScatterSampler"/> from G3's own
    /// landscape (layer textures and vertex blend → <see cref="GroundClass"/>, slope, water) with the land's palette
    /// (<see cref="ScatterCatalog"/>), kept off buildings/rocks/bridges/overhangs by straight-down rays (occupancy), tinted by
    /// G3's local vegetation colours. GPU-driven: every resident instance lives in one buffer (pages per tile and layer);
    /// G3ScatterCull.compute culls per camera (range, frustum, far thinning, LOD) into per-(type, LOD) ranges and
    /// Graphics.RenderMeshIndirect draws them with G3/Remaster/Scatter (wind = G3Wind, bending = the trail map stamped by the
    /// characters, motion vectors for STP, the relief's drawn ground). Sampling and mesh reading of the ground run on one worker
    /// thread; the main thread reads cell meshes within a time budget, schedules the ray batches (jobs) and uploads pages.
    /// Density and range follow the options' vegetation quality (<see cref="ScatterQuality"/>). Off = nothing drawn, every
    /// buffer released, the worker stopped.
    /// </summary>
    public static class G3GroundScatter
    {
        static Driver _driver;
        static bool _wanted;

        /// <summary>Options page vegetation quality 0..3 (DisplayOptionsApplier); 0 = no scatter.</summary>
        public static int VegetationQuality = 3;

        public static bool Active => _driver != null && _driver.enabled;

        /// <summary>A/B and perf: false = keep everything resident but cull and draw nothing (and no bending).</summary>
        public static bool Draw = true;

        /// <summary>Debug: every scatter instance drawn flat magenta (where it is, at a glance).</summary>
        public static bool DebugTint;

        /// <summary>Called by <see cref="G3WorldV2.Apply(WorldParts)"/>: on with the Grass part and json <c>scatter</c>.</summary>
        public static void Apply(bool on)
        {
            on &= Application.isPlaying && SystemInfo.supportsComputeShaders;
            _wanted = on;
            if (on)
            {
                if (_driver == null)
                {
                    var go = new GameObject("G3GroundScatter") { hideFlags = HideFlags.HideInHierarchy };
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _driver = go.AddComponent<Driver>();
                }
                if (!_driver.enabled) _driver.enabled = true;
                else _driver.SettingsChanged();
            }
            else if (_driver != null && _driver.enabled) _driver.enabled = false;
        }

        /// <summary>Re-reads the knobs (density, ranges: every tile regenerated).</summary>
        public static void Reload()
        {
            if (Active) _driver.SettingsChanged();
        }

        /// <summary>One line for the runner.</summary>
        public static string Describe() => Active ? _driver.Describe() : $"scatter off{(_wanted ? " (wanted)" : "")}";

        /// <summary>Debug: the ground the worker sees at (x, z) (classes, blend, height, slope), the tile's occupancy there and a
        /// histogram of the ground classes over a 64 m square round it (waits up to 3 s for the worker).</summary>
        public static string Probe(float x, float z) => Active ? _driver.Probe(x, z) : "scatter off";

        static ScatterKeepOut _keepOut;

        /// <summary>Places the scatter keeps clear for NPCs (x, y, z, radius m): routine stations and the objects they use;
        /// set by the game session (Myrtana's ScatterKeepOutWiring). Replaces the previous set; every tile is placed again.</summary>
        public static int SetKeepOut(IEnumerable<Vector4> discs)
        {
            var k = new ScatterKeepOut();
            if (discs != null) foreach (var d in discs) k.Add(d.x, d.z, d.w);
            _keepOut = k.Count > 0 ? k : null;
            if (Active) _driver.KeepOutChanged();
            return k.Count;
        }

        /// <summary>G3's trees as the scatter indexed them (null while off): the v2 grass thins G3's grass under the crowns.</summary>
        public static ScatterCanopy Canopy => Active ? _driver.Canopy : null;

        /// <summary>Main-thread CPU ms of the last frame (update + per-camera cull/draw submission).</summary>
        public static float LastCpuMs => Active ? _driver.lastCpuMs : 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _driver = null;
            _wanted = false;
            _keepOut = null;
            Draw = true;
            VegetationQuality = 3;
        }

        // =====================================================================================================================

        [StructLayout(LayoutKind.Sequential)]
        struct GpuInst
        {
            public Vector3 pos;
            public float scale;
            public Vector4 rot;
            public uint color;
            public uint type;
            public Vector2 pad;
        }
        const int InstStride = 48;

        [StructLayout(LayoutKind.Sequential)]
        struct GpuType
        {
            public Vector4 range, look, cull;
            public uint slot0, slot1, flags, pad;
        }
        const int TypeStride = 64;
        const int PageSize = 256;
        const uint Invalid = 0xFFFFFFFFu;

        /// <summary>One drawable (item, variant): mesh(es), material, its two draw slots.</summary>
        sealed class DrawType
        {
            public int item, variant;
            public ScatterItem def;
            public Mesh mesh, lod1;
            public Material material;
            public int slot0, slot1;
            public int resident;
            public float radius, top;
            public string key;            // material settings: types with the same key share one material and one draw call
            public int slice;             // generated cards: slice of the card array
            public int start0, count0, base0, start1, count1, base1;   // index ranges in the group's combined mesh
        }

        /// <summary>Types sharing a material: one combined mesh, one multi-command RenderMeshIndirect (one command per type and LOD).</summary>
        sealed class DrawGroup
        {
            public bool animated;          // a type with wind or bending: draws the motion vector pass
            public Material material;
            public Mesh mesh;
            public bool shadows, combined;
            public int firstCmd, cmdCount;
            public readonly List<DrawType> types = new List<DrawType>();
        }

        sealed class Unit
        {
            public readonly List<int> pages = new List<int>();
            public int count;
            public int[] typeCounts;
            public bool pending;
            public int version = -1;
        }

        sealed class Tile
        {
            public int tx, tz;
            public float[] occ;           // occupancy heights (null = not yet traced)
            public bool occPending;
            public readonly Unit[] units = new Unit[3];
            public int occVersion;         // bumped when its ground changed (rays again)
            public int version;            // bumped when its ground changed (a cell came or went): everything regenerated
            public ScatterBiome biome;
        }

        sealed class Result
        {
            public long key;
            public int layer, version;
            public ScatterInstance[] instances;
        }

        sealed class RayBatch
        {
            public long key;
            public int version, n;
            public NativeArray<RaycastCommand> cmds;
            public NativeArray<RaycastHit> hits;
            public JobHandle handle;
        }

        sealed class WaterAdapter : IScatterWater
        {
            public WaterMap map;
            public bool Surface(float x, float y, float z, out float surfaceY)
            {
                surfaceY = float.NegativeInfinity;
                return map != null && map.Sample(x, y - 1.5f, z, out surfaceY, out _, 11.5f);
            }
        }

        sealed class Driver : MonoBehaviour
        {
            static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("G3GroundScatter.Update");
            static readonly ProfilerMarker CullMarker = new ProfilerMarker("G3GroundScatter.Cull");
            const int OccN = 32;

            ScatterCatalog _catalog;
            ScatterSampler.Prepared _prepared;
            WorldLookSettings _s;
            float _densityApplied, _rangeQ;
            float[] _ranges = new float[3];
            float _tileSize;

            // assets
            G3ScatterSet _set;

            readonly Dictionary<string, (Mesh mesh, Material mat)> _found = new Dictionary<string, (Mesh, Material)>();
            readonly Dictionary<string, Texture> _terrainTex = new Dictionary<string, Texture>();
            readonly HashSet<string> _wantedNames = new HashSet<string>();
            readonly List<DrawType> _types = new List<DrawType>();
            readonly List<DrawGroup> _groups = new List<DrawGroup>();
            readonly Dictionary<string, Material> _sharedMats = new Dictionary<string, Material>();
            int[][] _typeOf;   // [item][variant] -> type index, -1 unresolved
            bool _typesDirty = true, _slotsDirty = true;
            readonly List<Mesh> _ownMeshes = new List<Mesh>();
            readonly List<Material> _ownMaterials = new List<Material>();
            Shader _shader;
            ComputeShader _cs;
            int _kClear, _kCull, _kClamp, _kBend;

            // GPU
            GraphicsBuffer _instances, _visible, _slots, _args, _argsRW, _typeBuf;   // _argsRW: compute-written copy (an IndirectArguments buffer bound as a UAV did not take the writes, D3D12)
            int _capacity, _highWater, _slotCount, _visibleCount;
            readonly Stack<int> _freePages = new Stack<int>();
            GpuInst[] _mirror = new GpuInst[0];   // CPU copy of the instance buffer (re-upload on growth)

            // tiles
            readonly Dictionary<long, Tile> _tiles = new Dictionary<long, Tile>();
            readonly List<RayBatch> _rays = new List<RayBatch>();
            Vector3 _lastWantPos = new Vector3(float.MaxValue, 0, 0);
            float _nextWant;
            List<(int tx, int tz, int layers, float dist)> _want = new List<(int, int, int, float)>();

            // worker
            Thread _thread;
            BlockingCollection<Action> _jobs;
            readonly ConcurrentQueue<Result> _results = new ConcurrentQueue<Result>();
            ScatterTerrainIndex _index;          // worker-owned
            ScatterTintGrid _tint;               // worker-owned
            ScatterCanopy _canopy;               // filled by the worker (locked inside: the v2 grass build reads it too)
            public ScatterCanopy Canopy => _canopy;
            int _trees;
            readonly WaterAdapter _water = new WaterAdapter();
            int _jobsInFlight;

            // cells
            // perf-5: with its scene; the filters of an unloaded scene leave the queue on sceneUnloaded, untouched (a null check
            // of a destroyed scene object asks the persistent manager, which the loading thread holds while a cell streams in:
            // 100+ such checks per frame were 340 ms stalls in the editor)
            readonly Queue<(MeshFilter mf, Scene scene)> _scan = new Queue<(MeshFilter, Scene)>();
            readonly List<(MeshFilter, Scene)> _scanKeep = new List<(MeshFilter, Scene)>();
            readonly Dictionary<int, (MeshFilter mf, Bounds b)> _cells = new Dictionary<int, (MeshFilter, Bounds)>();
            readonly Dictionary<MeshFilter, int> _cellIds = new Dictionary<MeshFilter, int>();
            int _nextCellId;
            readonly HashSet<G3VegetationData> _tintData = new HashSet<G3VegetationData>();
            float _nextCellCheck;

            // bending
            RenderTexture _trail;
            GraphicsBuffer _benders;
            readonly Vector4[] _benderData = new Vector4[64];
            readonly Dictionary<G3Character, Vector3> _lastPos = new Dictionary<G3Character, Vector3>();
            Vector2 _trailPrev;
            bool _trailValid;
            const int TrailN = 256;
            const float TrailExtent = 64f;

            // camera
            Camera _cam;
            G3WorldStreamer _streamer;
            readonly Plane[] _planes = new Plane[6];
            readonly Vector4[] _planeVecs = new Vector4[6];
            public float lastCpuMs;
            float _cpuAcc;
            readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
            int _generated, _uploadedInstances, _drawCalls;

            static readonly int InstancesId = Shader.PropertyToID("_Instances"), TypesId = Shader.PropertyToID("_Types"),
                SlotsId = Shader.PropertyToID("_Slots"), VisibleId = Shader.PropertyToID("_Visible"), ArgsId = Shader.PropertyToID("_Args"),
                CountId = Shader.PropertyToID("_Count"), SlotCountId = Shader.PropertyToID("_SlotCount"), PlanesId = Shader.PropertyToID("_Planes"),
                CamPosId = Shader.PropertyToID("_CamPos"), ParamsId = Shader.PropertyToID("_Params"),
                TrailId = Shader.PropertyToID("_Trail"), TrailNId = Shader.PropertyToID("_TrailN"), TrailPId = Shader.PropertyToID("_TrailP"),
                TrailPrevId = Shader.PropertyToID("_TrailPrev"), BendersId = Shader.PropertyToID("_Benders"), BenderCountId = Shader.PropertyToID("_BenderCount"),
                GInstancesId = Shader.PropertyToID("_G3ScatterInstances"), GVisibleId = Shader.PropertyToID("_G3ScatterVisible"),
                GTypesId = Shader.PropertyToID("_G3ScatterTypes"),
                GParamsId = Shader.PropertyToID("_G3ScatterParams"), BendTrailId = Shader.PropertyToID("_G3BendTrail"),
                BendParamsId = Shader.PropertyToID("_G3BendParams"), BendCamId = Shader.PropertyToID("_G3BendCam");

            // ---------------------------------------------------------------------------------------------- lifecycle

            void OnEnable()
            {
                _cs = Resources.Load<ComputeShader>("G3ScatterCull");
                _shader = Shader.Find("G3/Remaster/Scatter") ?? Resources.Load<Material>("G3ScatterShaderRef")?.shader;
                if (_cs == null || _shader == null)
                {
                    Debug.LogWarning("[Scatter] G3ScatterCull.compute or G3/Remaster/Scatter missing: scatter off");
                    enabled = false;
                    return;
                }
                _kClear = _cs.FindKernel("Clear"); _kCull = _cs.FindKernel("Cull"); _kClamp = _cs.FindKernel("Clamp"); _kBend = _cs.FindKernel("Bend");
                _catalog = ScatterCatalog.Default();
                _prepared = ScatterSampler.Prepare(_catalog);
                _set = Resources.Load<G3ScatterSet>(G3ScatterSet.ResourceName);
                foreach (var it in _catalog.Items) foreach (var src in it.Sources) if (!src.StartsWith("gen:", StringComparison.Ordinal)) _wantedNames.Add(src);
                _water.map = G3Water.Map;
                ReadSettings();
                StartWorker();
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                for (int i = 0; i < SceneManager.sceneCount; i++) QueueScene(SceneManager.GetSceneAt(i));
                RenderPipelineManager.beginCameraRendering += OnBeginCamera;
                Shader.SetGlobalVector(BendParamsId, Vector4.zero);
                Debug.Log($"[Scatter] on: {_catalog.Items.Count} items, {_catalog.Rules.Count} rules, set {(_set != null ? _set.entries.Count + " entries" : "not built")}, quality {VegetationQuality}");
            }

            void OnDisable()
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                StopWorker();
                foreach (var r in _rays) { r.handle.Complete(); r.cmds.Dispose(); r.hits.Dispose(); }
                _rays.Clear();
                _tiles.Clear();
                _cells.Clear();
                _cellIds.Clear();
                _scan.Clear();
                _tintData.Clear();
                _found.Clear();
                _terrainTex.Clear();
                _freePages.Clear();
                _highWater = 0;
                ReleaseGpu();
                foreach (var m in _ownMeshes) if (m != null) G3GpuSafe.Destroy(m);   // dx12-3: after the frames that draw them
                foreach (var m in _ownMaterials) if (m != null) G3GpuSafe.Destroy(m);
                _ownMeshes.Clear(); _ownMaterials.Clear(); _types.Clear(); _typeOf = null; _groups.Clear(); _sharedMats.Clear();
                _typesDirty = _slotsDirty = true;
                if (_trail != null) { G3GpuSafe.Release(_trail); _trail = null; }
                G3GpuSafe.Release(_benders); _benders = null;
                _trailValid = false;
                _lastPos.Clear();
                Shader.SetGlobalVector(BendParamsId, Vector4.zero);
                _lastWantPos = new Vector3(float.MaxValue, 0, 0);
                Debug.Log("[Scatter] off");
            }

            void OnDestroy() { if (enabled) OnDisable(); }

            void ReleaseGpu()
            {
                // dx12-3: released a few frames later (G3GpuSafe): queued indirect draws and the global bindings name them
                G3GpuSafe.Release(_instances); G3GpuSafe.Release(_visible); G3GpuSafe.Release(_slots);
                G3GpuSafe.Release(_args); G3GpuSafe.Release(_argsRW); G3GpuSafe.Release(_typeBuf);
                G3GpuDiag.Event("scatter release gpu");
                _instances = _visible = _slots = _args = _argsRW = _typeBuf = null;
                _capacity = 0; _slotCount = 0; _visibleCount = 0; _mirror = new GpuInst[0];
            }

            void ReadSettings()
            {
                _s = WorldRemasterLook.Current;
                _tileSize = _s.scatterTile;
                _densityApplied = _s.scatterDensity * ScatterQuality.Density(VegetationQuality);
                _rangeQ = _s.scatterDistance * ScatterQuality.Range(VegetationQuality);
                for (int l = 0; l < 3; l++) _ranges[l] = _catalog.LayerRange((ScatterLayer)l) * _rangeQ;
            }

            public void SettingsChanged()
            {
                float tile = _tileSize, dens = _densityApplied;
                ReadSettings();
                _typesDirty = true;
                if (Math.Abs(tile - _tileSize) > 1e-3f || Math.Abs(dens - _densityApplied) > 1e-3f)
                {
                    foreach (var t in _tiles.Values) for (int l = 0; l < 3; l++) FreeUnit(t, l);
                    _tiles.Clear();
                    _slotsDirty = true;
                }
                _lastWantPos = new Vector3(float.MaxValue, 0, 0);
            }

            // ---------------------------------------------------------------------------------------------- worker

            void StartWorker()
            {
                _jobs = new BlockingCollection<Action>();
                var jobs = _jobs;
                _index = new ScatterTerrainIndex();
                _tint = new ScatterTintGrid();
                _canopy = new ScatterCanopy();
                _thread = new Thread(() =>
                {
                    try
                    {
                        foreach (var job in jobs.GetConsumingEnumerable())
                        {
                            try { job(); }
                            catch (Exception e) { Debug.LogWarning("[Scatter] worker: " + e); }
                        }
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }) { IsBackground = true, Name = "G3GroundScatter", Priority = System.Threading.ThreadPriority.BelowNormal };
                _thread.Start();
            }

            void StopWorker()
            {
                if (_jobs != null) { _jobs.CompleteAdding(); }
                _thread?.Join(2000);
                _thread = null;
                _jobs?.Dispose();
                _jobs = null;
                while (_results.TryDequeue(out _)) { }
                _jobsInFlight = 0;
                _index = null; _tint = null; _canopy = null; _trees = 0;
            }

            void Post(Action a)
            {
                try { _jobs?.Add(a); } catch (InvalidOperationException) { }
            }

            // ---------------------------------------------------------------------------------------------- scenes, cells, assets

            static readonly ProfilerMarker SceneMarker = new ProfilerMarker("G3GroundScatter.SceneLoaded");

            void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                using var _ = SceneMarker.Auto();
                QueueScene(scene);
            }

            void OnSceneUnloaded(Scene scene)
            {
                if (_scan.Count == 0) return;
                _scanKeep.Clear();
                foreach (var e in _scan) if (e.Item2 != scene) _scanKeep.Add(e);
                if (_scanKeep.Count == _scan.Count) { _scanKeep.Clear(); return; }
                _scan.Clear();
                foreach (var e in _scanKeep) _scan.Enqueue(e);
                _scanKeep.Clear();
            }

            void QueueScene(Scene scene)
            {
                if (!scene.isLoaded) return;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true)) _scan.Enqueue((mf, scene));
                    foreach (var vr in root.GetComponentsInChildren<G3VegetationRenderer>(true)) AddVegetation(vr.data);
                    foreach (var lg in root.GetComponentsInChildren<LODGroup>(true)) AddTree(lg);
                }
            }

            /// <summary>G3's SpeedTrees (LODGroup roots named G3_Tree* / G3_Speedtree*, as G3TreeV2) feed the canopy.</summary>
            void AddTree(LODGroup lg)
            {
                var go = lg.gameObject;
                if (!go.name.StartsWith("G3_Tree", StringComparison.OrdinalIgnoreCase) && !go.name.StartsWith("G3_Speedtree", StringComparison.OrdinalIgnoreCase)) return;
                var p = lg.transform.position;
                float s = Mathf.Abs(lg.transform.lossyScale.x);
                float r = Mathf.Clamp(lg.size * s * 0.32f, 1.5f, 9f);
                bool conifer = ScatterCanopy.IsConifer(go.name);
                int id = (int)ScatterSampler.Hash((int)Mathf.Round(p.x * 10), (int)Mathf.Round(p.z * 10), 77u);
                _trees++;
                Post(() => _canopy?.Add(id, p.x, p.z, r, conifer));
            }

            void AddVegetation(G3VegetationData data)
            {
                if (data == null || !_tintData.Add(data)) return;
                foreach (var me in data.meshes)
                {
                    if (me.mesh == null || me.material == null) continue;
                    string n = me.name ?? me.mesh.name;
                    foreach (var w in _wantedNames)
                        if (n.StartsWith(w, StringComparison.OrdinalIgnoreCase) && !_found.ContainsKey(w)) { _found[w] = (me.mesh, me.material); _typesDirty = true; }
                }
                var bytes = data.instances != null ? data.instances.bytes : null;
                if (bytes == null) return;
                Post(() =>
                {
                    int n = bytes.Length / G3VegetationData.InstanceStride;
                    var tint = _tint;
                    if (tint == null) return;
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * G3VegetationData.InstanceStride;
                        float x = BitConverter.ToSingle(bytes, o), z = BitConverter.ToSingle(bytes, o + 8);
                        tint.Add(x, z, BitConverter.ToUInt32(bytes, o + 36));
                    }
                });
            }

            /// <summary>Landscape cell: G3's "_Landscape_Cell_" render meshes.</summary>
            static bool IsCellName(string n) => n != null && n.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase) >= 0;

            void ScanCells(double budgetMs)
            {
                var t0 = Time.realtimeSinceStartupAsDouble;
                while (_scan.Count > 0 && (Time.realtimeSinceStartupAsDouble - t0) * 1000 < budgetMs)
                {
                    var (mf, _) = _scan.Dequeue();
                    if (mf == null) continue;
                    var mesh = G3TerrainRelief.SourceOf(mf.sharedMesh);
                    if (mesh == null) continue;
                    string name = mesh.name;
                    if (IsCellName(name)) { ReadCell(mf, mesh); continue; }
                    if (_wantedNames.Contains(name) && !_found.ContainsKey(name))
                    {
                        var mr = mf.GetComponent<MeshRenderer>();
                        var mat = mr != null ? G3WorldRemaster.OriginalOf(mr.sharedMaterial) : null;
                        if (mat != null) { _found[name] = (mesh, mat); _typesDirty = true; }
                    }
                }
            }

            void ReadCell(MeshFilter mf, Mesh mesh)
            {
                // G3's render mesh (the relief copy maps back to it): its submeshes match the renderer's materials. The collider
                // may hold a split/simplified collision mesh ("_2_2s") with other submeshes - not used.
                if (!mesh.isReadable) return;
                if (_cellIds.ContainsKey(mf)) return;
                int id = ++_nextCellId;
                _cellIds[mf] = id;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) return;
                var l2w = mf.transform.localToWorldMatrix;
                var v = new List<Vector3>(); mesh.GetVertices(v);
                var c = new List<Color32>(); mesh.GetColors(c);
                var pos = new float[v.Count * 3];
                for (int i = 0; i < v.Count; i++)
                {
                    var p = l2w.MultiplyPoint3x4(v[i]);
                    pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z;
                }
                float[] alpha = null;
                if (c.Count == v.Count) { alpha = new float[c.Count]; for (int i = 0; i < c.Count; i++) alpha[i] = c[i].a / 255f; }
                var mats = mr.sharedMaterials;
                int sub = mesh.subMeshCount;
                var tris = new int[sub][];
                var cls = new (GroundClass, GroundClass)[sub];
                for (int s = 0; s < sub; s++)
                {
                    tris[s] = mesh.GetTriangles(s);
                    var m = s < mats.Length ? G3WorldRemaster.OriginalOf(mats[s]) : null;
                    cls[s] = Classify(m);
                }
                var b = mr.bounds;
                _cells[id] = (mf, b);
                Post(() => _index?.Add(id, pos, alpha, tris, cls));
                DirtyTiles(b);
            }

            (GroundClass, GroundClass) Classify(Material m)
            {
                if (m == null) return (GroundClass.None, GroundClass.None);
                Texture ta = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                Texture tb = m.HasProperty("_BaseMap2") ? m.GetTexture("_BaseMap2") : null;
                bool single = tb == null || m.IsKeywordEnabled("_SINGLE_LAYER");
                if (ta != null && !_terrainTex.ContainsKey(ta.name)) { _terrainTex[ta.name] = ta; _typesDirty = true; }
                if (tb != null && !_terrainTex.ContainsKey(tb.name)) { _terrainTex[tb.name] = tb; _typesDirty = true; }
                return ScatterGround.Layers(m.name, ta != null ? ta.name : null, tb != null ? tb.name : null, single);
            }

            void CheckCells()
            {
                if (Time.realtimeSinceStartup < _nextCellCheck) return;
                _nextCellCheck = Time.realtimeSinceStartup + 1f;
                List<int> gone = null;
                foreach (var kv in _cells) if (kv.Value.mf == null) (gone ??= new List<int>()).Add(kv.Key);
                if (gone == null) return;
                foreach (int id in gone)
                {
                    DirtyTiles(_cells[id].b);
                    _cells.Remove(id);
                    var dead = new List<MeshFilter>();
                    foreach (var kv in _cellIds) if (kv.Value == id) dead.Add(kv.Key);
                    foreach (var k in dead) _cellIds.Remove(k);
                    Post(() => _index?.Remove(id));
                }
            }

            /// <summary>A cell came or went: the resident tiles over it are regenerated (rays and placement).</summary>
            void DirtyTiles(Bounds b)
            {
                foreach (var t in _tiles.Values)
                {
                    float x0 = t.tx * _tileSize, z0 = t.tz * _tileSize;
                    if (x0 > b.max.x || x0 + _tileSize < b.min.x || z0 > b.max.z || z0 + _tileSize < b.min.z) continue;
                    t.version++;
                    t.occVersion++;
                    t.occ = null;
                }
            }

            // ---------------------------------------------------------------------------------------------- draw types

            void BuildTypes()
            {
                if (!_typesDirty) return;
                _typesDirty = false;
                int before = _types.Count;
                var known = new Dictionary<(int, int), DrawType>();
                foreach (var t in _types) known[(t.item, t.variant)] = t;
                _typeOf = new int[_catalog.Items.Count][];
                for (int i = 0; i < _catalog.Items.Count; i++)
                {
                    var it = _catalog.Items[i];
                    _typeOf[i] = new int[it.Sources.Length];
                    for (int v = 0; v < it.Sources.Length; v++)
                    {
                        _typeOf[i][v] = -1;
                        if (!known.TryGetValue((i, v), out var dt))
                        {
                            dt = Resolve(i, v, it);
                            if (dt == null) continue;
                            _types.Add(dt);
                            known[(i, v)] = dt;
                        }
                        _typeOf[i][v] = _types.IndexOf(dt);
                    }
                    // a missing variant borrows a resolved one of the same item
                    int any = -1;
                    foreach (int k in _typeOf[i]) if (k >= 0) { any = k; break; }
                    for (int v = 0; v < _typeOf[i].Length; v++) if (_typeOf[i][v] < 0) _typeOf[i][v] = any;
                }
                _slotsDirty = true;
                if (_types.Count != before) BuildGroups();
                if (_types.Count != before)
                    foreach (var t in _tiles.Values) t.version++;   // instances of items that were not drawable yet: place again
            }

            DrawType Resolve(int item, int variant, ScatterItem it)
            {
                string src = it.Sources[variant];
                Mesh mesh = null, lod1 = null;
                Material srcMat = null;
                Texture tex = null;
                int slice1 = 0;
                bool gen = src.StartsWith("gen:", StringComparison.Ordinal);
                if (_set != null && _set.TryGet(src, out var e)) { mesh = e.mesh; lod1 = e.lod1; srcMat = e.material; tex = e.texture; slice1 = e.arraySlice1; }
                if (!gen && mesh == null && _found.TryGetValue(src, out var f)) { mesh = f.mesh; srcMat = f.mat; }
                if (!gen && (mesh == null || srcMat == null)) return null;
                if (it.Kind == ScatterKind.Solid)
                {
                    if (it.Texture == null || !_terrainTex.TryGetValue(it.Texture, out tex)) return null;   // the land's rock image not loaded yet
                    if (mesh == null)
                    {
                        uint seed = ScatterSampler.StringSalt(src);
                        mesh = OwnMesh(ScatterMeshes.Rock(seed, 2), src + " (rock)");
                        lod1 = OwnMesh(ScatterMeshes.Rock(seed, 1), src + " (rock lod1)");
                    }
                }
                else if (gen)
                {
                    if (tex == null) return null;   // generated card not built (G3ScatterSetBuilder)
                    if (mesh == null)
                        mesh = it.Kind == ScatterKind.Litter ? OwnMesh(ScatterMeshes.Patch(it.SizeMax > 1.5f ? 4 : 2), src) : OwnMesh(ScatterMeshes.Cross(3), src);
                }
                var mat = new Material(_shader) { name = src + " (scatter)", hideFlags = HideFlags.DontSave };
                _ownMaterials.Add(mat);
                bool vegAtlas = srcMat != null && srcMat.shader != null && srcMat.shader.name.IndexOf("Vegetation", StringComparison.OrdinalIgnoreCase) >= 0;
                if (srcMat != null)
                {
                    var look = vegAtlas ? G3WorldRemaster.Resolve(srcMat) : srcMat;   // the Grass part's sharper atlas while on
                    if (look.HasProperty("_BaseMap")) { mat.SetTexture("_BaseMap", look.GetTexture("_BaseMap")); mat.SetTextureScale("_BaseMap", look.GetTextureScale("_BaseMap")); mat.SetTextureOffset("_BaseMap", look.GetTextureOffset("_BaseMap")); }
                    else if (look.HasProperty("_MainTex")) mat.SetTexture("_BaseMap", look.GetTexture("_MainTex"));
                    foreach (var p in new[] { "_Cutoff", "_InstColorStrength", "_InstColorScale", "_NormalUp", "_MipAlphaScale", "_FoliageAO", "_FoliageAOHeight" })
                        if (look.HasProperty(p)) mat.SetFloat(p, look.GetFloat(p));
                    if (look.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", look.GetColor("_BaseColor"));
                }
                int slice = Math.Max(0, slice1 - 1);
                if (gen && it.Kind != ScatterKind.Solid && slice1 > 0 && _set.cardArray != null)
                {
                    mat.SetTexture("_BaseArray", _set.cardArray);
                    mat.SetFloat("_UseArray", 1);
                    mat.SetTexture("_BaseMap", null);   // the key below: every card type with the same settings shares this material
                }
                else if (tex != null) mat.SetTexture("_BaseMap", tex);
                mat.SetFloat("_TintMode", vegAtlas ? 0 : 1);
                mat.SetFloat("_Solid", it.Kind == ScatterKind.Solid ? 1 : 0);
                mat.SetFloat("_Cull", it.Kind == ScatterKind.Solid ? (float)CullMode.Back : (float)CullMode.Off);
                if (it.Kind == ScatterKind.Litter) { mat.SetFloat("_NormalUp", 0.6f); mat.SetFloat("_FoliageAO", 0.35f); }
                if (it.Kind == ScatterKind.Solid) { mat.SetFloat("_Smoothness", 0.1f); mat.SetFloat("_FoliageAO", 0.2f); mat.SetFloat("_TriScale", 0.5f); }
                if (gen && it.Kind != ScatterKind.Solid) mat.SetFloat("_Cutoff", 0.42f);
                if (it.Kind == ScatterKind.Debris && !vegAtlas) mat.SetFloat("_Translucency", 0);
                // G3's opaque materials keep a specular mask in the diffuse alpha: no alpha test for them
                if (srcMat != null && !vegAtlas && !gen && !(srcMat.IsKeywordEnabled("_ALPHATEST_ON") || srcMat.renderQueue >= 2450 ||
                                                            (srcMat.HasProperty("_AlphaClip") && srcMat.GetFloat("_AlphaClip") > 0.5f)))
                    mat.SetFloat("_Cutoff", -1);
                mat.renderQueue = it.Kind == ScatterKind.Solid ? (int)RenderQueue.Geometry : (int)RenderQueue.AlphaTest;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var bt = mat.GetTexture("_BaseMap");
                string key = (bt != null ? bt.GetHashCode().ToString(ci) : "-") + "|" + (it.Shadows ? 1 : 0) + "|" + mat.renderQueue;
                foreach (var pn in new[] { "_Cutoff", "_InstColorStrength", "_InstColorScale", "_TintMode", "_NormalUp", "_MipAlphaScale", "_FoliageAO", "_FoliageAOHeight", "_Translucency", "_Solid", "_TriScale", "_Cull", "_Smoothness", "_UseArray" })
                    key += "|" + mat.GetFloat(pn).ToString("R", ci);
                key += "|" + mat.GetColor("_BaseColor") + "|" + mat.GetTextureScale("_BaseMap") + mat.GetTextureOffset("_BaseMap");
                if (_sharedMats.TryGetValue(key, out var shared)) { _ownMaterials.Remove(mat); Destroy(mat); mat = shared; }
                else _sharedMats[key] = mat;
                var b = mesh.bounds;
                return new DrawType
                {
                    item = item, variant = variant, def = it, mesh = mesh, lod1 = lod1, material = mat,
                    radius = Mathf.Max(0.1f, b.extents.magnitude + b.center.magnitude), top = Mathf.Max(0.01f, b.max.y), key = key, slice = slice,
                };
            }

            Mesh OwnMesh(ScatterMeshes.Data d, string name)
            {
                var m = new Mesh { name = name, hideFlags = HideFlags.DontSave };
                var v = new Vector3[d.VertexCount]; var n = new Vector3[d.VertexCount]; var uv = new Vector2[d.VertexCount];
                for (int i = 0; i < v.Length; i++)
                {
                    v[i] = new Vector3(d.Pos[i * 3], d.Pos[i * 3 + 1], d.Pos[i * 3 + 2]);
                    n[i] = new Vector3(d.Nrm[i * 3], d.Nrm[i * 3 + 1], d.Nrm[i * 3 + 2]);
                    uv[i] = new Vector2(d.Uv[i * 2], d.Uv[i * 2 + 1]);
                }
                m.vertices = v; m.normals = n; m.uv = uv; m.triangles = d.Tri;
                m.RecalculateBounds();
                m.UploadMeshData(false);
                _ownMeshes.Add(m);
                return m;
            }

            /// <summary>Groups by material key; each group's meshes combined (position, normal, uv0) into one mesh.</summary>
            void BuildGroups()
            {
                foreach (var g in _groups)
                    if (g.mesh != null && g.combined) { _ownMeshes.Remove(g.mesh); Destroy(g.mesh); }
                _groups.Clear();
                var byKey = new Dictionary<string, DrawGroup>();
                foreach (var dt in _types)
                {
                    bool combinable = dt.mesh.isReadable && (dt.lod1 == null || dt.lod1.isReadable);
                    if (!combinable || !byKey.TryGetValue(dt.key, out var g))
                    {
                        g = new DrawGroup { material = dt.material, shadows = dt.def.Shadows, combined = combinable };
                        g.animated = false;
                        _groups.Add(g);
                        if (combinable) byKey[dt.key] = g;
                    }
                    g.types.Add(dt);
                    if (dt.def.Kind == ScatterKind.Plant && (dt.def.Wind > 0 || dt.def.Bend > 0)) g.animated = true;
                }
                var tv = new List<Vector3>(); var tn = new List<Vector3>(); var tuv = new List<Vector2>();
                foreach (var g in _groups)
                {
                    if (!g.combined)
                    {
                        var dt = g.types[0];
                        g.mesh = dt.mesh;
                        dt.start0 = (int)dt.mesh.GetIndexStart(0); dt.count0 = (int)dt.mesh.GetIndexCount(0); dt.base0 = (int)dt.mesh.GetBaseVertex(0);
                        dt.lod1 = null;   // a separate LOD mesh cannot share the call
                        continue;
                    }
                    var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
                    void Add(Mesh m, out int start, out int count, out int baseV)
                    {
                        m.GetVertices(tv); m.GetNormals(tn); m.GetUVs(0, tuv);
                        baseV = v.Count;
                        v.AddRange(tv);
                        for (int i = 0; i < tv.Count; i++) n.Add(i < tn.Count ? tn[i] : Vector3.up);
                        for (int i = 0; i < tv.Count; i++) uv.Add(i < tuv.Count ? tuv[i] : Vector2.zero);
                        var t = m.GetIndices(0);
                        start = idx.Count; count = t.Length;
                        idx.AddRange(t);   // local indices: the command's baseVertex offsets them
                    }
                    foreach (var dt in g.types)
                    {
                        Add(dt.mesh, out dt.start0, out dt.count0, out dt.base0);
                        if (dt.lod1 != null) Add(dt.lod1, out dt.start1, out dt.count1, out dt.base1);
                    }
                    var cm = new Mesh { name = "scatter group " + g.material.name, hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
                    cm.SetVertices(v); cm.SetNormals(n); cm.SetUVs(0, uv);
                    cm.SetIndices(idx, MeshTopology.Triangles, 0, false);
                    cm.bounds = new Bounds(Vector3.zero, Vector3.one * 1000);
                    cm.UploadMeshData(true);
                    _ownMeshes.Add(cm);
                    g.mesh = cm;
                }
                _slotsDirty = true;
            }

            /// <summary>Command layout: per group its types' LOD0 (and LOD1) commands in a row; capacity = resident rounded up;
            /// startInstance of a command = its first visible index (the shader adds it to SV_InstanceID, G3ScatterIndirect.hlsl).</summary>
            void BuildSlots()
            {
                if (!_slotsDirty) return;
                _slotsDirty = false;
                int cmd = 0;
                foreach (var g in _groups)
                {
                    // types with resident instances first: the call issues only their commands (a land's other items -
                    // Varant's in Myrtana - cost no draws)
                    var order = new List<DrawType>(g.types);
                    order.Sort((x, y) => (y.resident > 0).CompareTo(x.resident > 0));
                    g.types.Clear(); g.types.AddRange(order);
                    g.firstCmd = cmd;
                    g.cmdCount = 0;
                    foreach (var dt in g.types)
                    {
                        dt.slot0 = cmd++;
                        dt.slot1 = dt.lod1 != null ? cmd++ : dt.slot0;
                        if (dt.resident > 0) g.cmdCount = cmd - g.firstCmd;
                    }
                }
                int slotCount = cmd;
                var slots = new uint[Math.Max(1, slotCount) * 2];
                var args = new uint[Math.Max(1, slotCount) * 5];
                int offset = 0;
                foreach (var g in _groups)
                    foreach (var dt in g.types)
                    {
                        int cap = dt.resident <= 0 ? 0 : (dt.resident + 1023) / 1024 * 1024;
                        void S(int slot, int start, int count, int baseV)
                        {
                            slots[slot * 2] = (uint)offset; slots[slot * 2 + 1] = (uint)cap;
                            args[slot * 5] = (uint)count; args[slot * 5 + 2] = (uint)start; args[slot * 5 + 3] = (uint)baseV; args[slot * 5 + 4] = (uint)offset;
                            offset += cap;
                        }
                        S(dt.slot0, dt.start0, dt.count0, dt.base0);
                        if (dt.slot1 != dt.slot0) S(dt.slot1, dt.start1, dt.count1, dt.base1);
                    }
                if (_slots == null || _slotCount != slotCount)
                {
                    G3GpuSafe.Release(_slots); G3GpuSafe.Release(_args); G3GpuSafe.Release(_argsRW);
                    G3GpuDiag.Event($"scatter slots {_slotCount} -> {slotCount}");
                    _slotCount = slotCount;
                    _slots = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, slotCount), 8);
                    _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.CopyDestination, Math.Max(1, slotCount) * 5, 4);
                    _argsRW = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, Math.Max(1, slotCount) * 5, 4);
                }
                _slots.SetData(slots);
                _args.SetData(args);
                _argsRW.SetData(args);
                if (_visible == null || _visibleCount < Math.Max(1, offset))
                {
                    G3GpuSafe.Release(_visible);
                    G3GpuDiag.Event($"scatter visible {_visibleCount} -> {Math.Max(1024, offset + offset / 4)}");
                    _visibleCount = Math.Max(1024, offset + offset / 4);
                    _visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _visibleCount, 4);
                }
                UploadTypes();
            }

            void UploadTypes()
            {
                if (_types.Count == 0) return;
                if (_typeBuf == null || _typeBuf.count < _types.Count)
                {
                    G3GpuSafe.Release(_typeBuf);
                    _typeBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(16, _types.Count * 2), TypeStride);
                }
                var a = new GpuType[_types.Count];
                bool shadows = _s.scatterShadows && ScatterQuality.Shadows(VegetationQuality) && G3VegetationRenderer.OptionShadows;
                for (int t = 0; t < _types.Count; t++)
                {
                    var dt = _types[t];
                    float range = dt.def.ViewRange * _rangeQ;
                    a[t] = new GpuType
                    {
                        range = new Vector4(range, range * 0.6f, range * _s.scatterLodDistance, range * _s.scatterThinStart),
                        look = new Vector4(dt.def.Kind == ScatterKind.Plant ? dt.def.Wind : 0, _s.scatterBend ? dt.def.Bend : 0, KindCode(dt.def.Kind), dt.top),
                        cull = new Vector4(dt.radius, _s.scatterShadowDistance, dt.slice, 0),
                        slot0 = (uint)dt.slot0, slot1 = (uint)dt.slot1, flags = dt.def.Shadows && shadows ? 1u : 0u,
                    };
                }
                _typeBuf.SetData(a);
            }

            static float KindCode(ScatterKind k) => k == ScatterKind.Plant ? 0 : k == ScatterKind.Litter ? 1 : k == ScatterKind.Solid ? 2 : 3;

            // ---------------------------------------------------------------------------------------------- update

            void Update()
            {
                _sw.Restart();
                using (UpdateMarker.Auto())
                {
                    if (_jobs == null) return;
                    if (_cam == null || !_cam.isActiveAndEnabled) _cam = Camera.main;
                    float q = _s.scatterDensity * ScatterQuality.Density(VegetationQuality);
                    if (Math.Abs(q - _densityApplied) > 1e-3f || Math.Abs(_s.scatterDistance * ScatterQuality.Range(VegetationQuality) - _rangeQ) > 1e-3f || _s != WorldRemasterLook.Current)
                        SettingsChanged();
                    if (VegetationQuality <= 0 || !G3VegetationRenderer.OptionEnabled) { lastCpuMs = 0; return; }
                    ScanCells(1.5);
                    CheckCells();
                    BuildTypes();
                    CompleteRays();
                    if (_cam != null) Stream(_cam.transform.position);
                    Collect(1.0);
                    BuildSlots();
                    if (_cam != null) UpdateBend(_cam.transform.position);
                }
                _cpuAcc = (float)_sw.Elapsed.TotalMilliseconds;
            }

            void LateUpdate()
            {
                lastCpuMs = _cpuAcc;
                _cpuAcc = 0;
            }

            string Region(Vector3 p)
            {
                if (_streamer == null) _streamer = FindAnyObjectByType<G3WorldStreamer>();
                return _streamer != null && _streamer.manifest != null ? _streamer.manifest.RegionAt(p) : null;
            }

            void Stream(Vector3 cam)
            {
                if (Time.unscaledTime >= _nextWant || (cam - _lastWantPos).sqrMagnitude > 4)
                {
                    _nextWant = Time.unscaledTime + 0.3f;
                    _lastWantPos = cam;
                    _want = ScatterTiles.Wanted(cam.x, cam.z, _tileSize, _ranges);
                    // drop what is out of range
                    List<long> empty = null;
                    foreach (var kv in _tiles)
                    {
                        var t = kv.Value;
                        bool any = false;
                        for (int l = 0; l < 3; l++)
                        {
                            if (t.units[l] == null) continue;
                            if (ScatterTiles.Drop(t.tx, t.tz, _tileSize, cam.x, cam.z, _ranges[l], _tileSize * 0.5f)) FreeUnit(t, l);
                            else any = true;
                        }
                        if (!any && !t.occPending && ScatterTiles.Drop(t.tx, t.tz, _tileSize, cam.x, cam.z, Mathf.Max(_ranges[0], Mathf.Max(_ranges[1], _ranges[2])), _tileSize))
                            (empty ??= new List<long>()).Add(kv.Key);
                    }
                    if (empty != null) foreach (var k in empty) _tiles.Remove(k);
                }
                // request: occupancy rays (a few tiles per frame), then placement jobs
                int rayTiles = 0;
                foreach (var (tx, tz, layers, _) in _want)
                {
                    long key = ScatterTiles.Key(tx, tz);
                    if (!_tiles.TryGetValue(key, out var t))
                    {
                        t = new Tile { tx = tx, tz = tz };
                        t.biome = ScatterGround.Biome(Region(new Vector3((tx + 0.5f) * _tileSize, 0, (tz + 0.5f) * _tileSize)));
                        _tiles[key] = t;
                    }
                    if (t.occ == null)
                    {
                        if (!t.occPending && rayTiles < 6 && _rays.Count < 12) { ScheduleRays(key, t); rayTiles++; }
                        continue;
                    }
                    for (int l = 0; l < 3; l++)
                    {
                        if ((layers & (1 << l)) == 0) continue;
                        var u = t.units[l];
                        if (u != null && (u.pending || u.version == t.version)) continue;
                        if (_jobsInFlight > 24) return;
                        RequestUnit(key, t, l);
                    }
                }
            }

            void ScheduleRays(long key, Tile t)
            {
                int n = OccN * OccN;
                var b = new RayBatch { key = key, version = t.occVersion, n = n, cmds = new NativeArray<RaycastCommand>(n, Allocator.Persistent), hits = new NativeArray<RaycastHit>(n, Allocator.Persistent) };
                float cell = _tileSize / OccN;
                var qp = new QueryParameters(Physics.DefaultRaycastLayers & ~(1 << 4) & ~(1 << 2), false, QueryTriggerInteraction.Ignore, false);
                for (int j = 0, k = 0; j < OccN; j++)
                    for (int i = 0; i < OccN; i++, k++)
                    {
                        var o = new Vector3(t.tx * _tileSize + (i + 0.5f) * cell, 1600f, t.tz * _tileSize + (j + 0.5f) * cell);
                        b.cmds[k] = new RaycastCommand(o, Vector3.down, qp, 3600f);
                    }
                b.handle = RaycastCommand.ScheduleBatch(b.cmds, b.hits, 64, 1);
                t.occPending = true;
                _rays.Add(b);
            }

            void CompleteRays()
            {
                for (int r = _rays.Count - 1; r >= 0; r--)
                {
                    var b = _rays[r];
                    if (!b.handle.IsCompleted) continue;
                    b.handle.Complete();
                    if (_tiles.TryGetValue(b.key, out var t) && t.occVersion == b.version)
                    {
                        var occ = new float[b.n];
                        for (int i = 0; i < b.n; i++)
                        {
                            var h = b.hits[i];
                            // nothing hit, or a character's capsule (it walks away): no occupancy
                            var hc = h.collider;
                            occ[i] = hc == null || hc is CharacterController ? float.NaN : h.point.y;
                        }
                        t.occ = occ;
                    }
                    if (t != null) t.occPending = false;
                    b.cmds.Dispose(); b.hits.Dispose();
                    _rays.RemoveAt(r);
                }
            }

            public void KeepOutChanged()
            {
                foreach (var t in _tiles.Values) t.version++;   // placed again with the new keep-out set
            }

            void RequestUnit(long key, Tile t, int layer)
            {
                var u = t.units[layer] ??= new Unit();
                u.pending = true;
                int version = t.version;
                var input = new ScatterTileInput
                {
                    Tx = t.tx, Tz = t.tz, TileSize = _tileSize, Biome = t.biome, Density = _densityApplied, OccTop = t.occ, OccN = OccN,
                    Water = _water.map != null ? _water : null, KeepOut = _keepOut,
                };
                var prepared = _prepared;
                _jobsInFlight++;
                Post(() =>
                {
                    var list = new List<ScatterInstance>();
                    var index = _index; var tint = _tint;
                    if (index != null)
                    {
                        input.Ground = index;
                        input.Tint = tint != null ? (Func<float, float, uint>)tint.At : null;
                        input.Canopy = _canopy;
                        ScatterSampler.Tile(prepared, input, (ScatterLayer)layer, list);
                    }
                    _results.Enqueue(new Result { key = key, layer = layer, version = version, instances = list.ToArray() });
                });
            }

            void Collect(double budgetMs)
            {
                var t0 = Time.realtimeSinceStartupAsDouble;
                while ((Time.realtimeSinceStartupAsDouble - t0) * 1000 < budgetMs && _results.TryDequeue(out var r))
                {
                    _jobsInFlight = Math.Max(0, _jobsInFlight - 1);
                    if (!_tiles.TryGetValue(r.key, out var t) || t.units[r.layer] == null) continue;
                    var u = t.units[r.layer];
                    u.pending = false;
                    if (r.version != t.version) continue;   // ground changed meanwhile: requested again
                    FreeUnit(t, r.layer, keep: true);
                    u.version = t.version;
                    Upload(u, r.instances);
                    _generated++;
                }
            }

            void Upload(Unit u, ScatterInstance[] inst)
            {
                if (_typeOf == null) return;
                u.typeCounts ??= new int[Math.Max(1, _types.Count)];
                if (u.typeCounts.Length < _types.Count) Array.Resize(ref u.typeCounts, _types.Count);
                int fill = 0, page = -1;
                for (int i = 0; i < inst.Length; i++)
                {
                    var s = inst[i];
                    if (s.Item >= _typeOf.Length) continue;
                    var map = _typeOf[s.Item];
                    int type = s.Variant < map.Length ? map[s.Variant] : -1;
                    if (type < 0) continue;
                    if (fill == 0) { page = AllocPage(); u.pages.Add(page); }
                    _mirror[page * PageSize + fill++] = new GpuInst
                    {
                        pos = new Vector3(s.X, s.Y, s.Z), scale = s.Scale, rot = new Vector4(s.Qx, s.Qy, s.Qz, s.Qw),
                        color = s.Color, type = (uint)type,
                    };
                    u.typeCounts[type]++;
                    _types[type].resident++;
                    u.count++;
                    if (fill == PageSize) { _instances.SetData(_mirror, page * PageSize, page * PageSize, PageSize); fill = 0; }
                }
                if (fill > 0)
                {
                    for (int k = fill; k < PageSize; k++) _mirror[page * PageSize + k].type = Invalid;
                    _instances.SetData(_mirror, page * PageSize, page * PageSize, PageSize);
                }
                _uploadedInstances += u.count;
                if (u.count > 0) _slotsDirty = true;
            }

            int AllocPage()
            {
                if (_freePages.Count > 0) return _freePages.Pop();
                int page = _highWater++;
                if ((page + 1) * PageSize > _capacity) Grow((page + 1) * PageSize);
                return page;
            }

            void Grow(int need)
            {
                int cap = Math.Max(65536, _capacity);
                while (cap < need) cap *= 2;
                int old = _mirror.Length;
                Array.Resize(ref _mirror, cap);
                for (int i = old; i < cap; i++) _mirror[i].type = Invalid;   // fresh pages are invalid until written
                G3GpuSafe.Release(_instances);   // dx12-3: still bound globally until the next cull rebinds
                G3GpuDiag.Event($"scatter grow {_capacity} -> {cap}");
                _instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, InstStride);
                _instances.SetData(_mirror);
                _capacity = cap;
            }

            void FreeUnit(Tile t, int layer, bool keep = false)
            {
                var u = t.units[layer];
                if (u == null) return;
                foreach (int p in u.pages)
                {
                    for (int k = 0; k < PageSize; k++) _mirror[p * PageSize + k].type = Invalid;
                    _instances?.SetData(_mirror, p * PageSize, p * PageSize, PageSize);
                    _freePages.Push(p);
                }
                if (u.typeCounts != null)
                    for (int k = 0; k < u.typeCounts.Length && k < _types.Count; k++) _types[k].resident -= u.typeCounts[k];
                if (u.count > 0) _slotsDirty = true;
                u.pages.Clear(); u.count = 0;
                if (u.typeCounts != null) Array.Clear(u.typeCounts, 0, u.typeCounts.Length);
                if (!keep && !u.pending) t.units[layer] = null;
                else if (!keep) u.version = -1;
            }

            // ---------------------------------------------------------------------------------------------- bending

            void UpdateBend(Vector3 cam)
            {
                if (!_s.scatterBend || !G3GroundScatter.Draw)
                {
                    Shader.SetGlobalVector(BendParamsId, Vector4.zero);
                    return;
                }
                if (_trail == null)
                {
                    _trail = new RenderTexture(TrailN, TrailN, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear)
                    {
                        enableRandomWrite = true, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "G3ScatterBendTrail", hideFlags = HideFlags.DontSave,
                    };
                    _trail.Create();
                    var prev = RenderTexture.active;
                    RenderTexture.active = _trail; GL.Clear(false, true, Color.clear); RenderTexture.active = prev;
                    _benders = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 64, 16);
                    _trailValid = false;
                }
                int n = 0;
                float r2 = 30f * 30f, dt = Mathf.Max(Time.deltaTime, 1e-4f);
                foreach (var ch in G3Character.Live)
                {
                    if (n >= 32) break;
                    if (ch == null || !ch.isActiveAndEnabled) continue;
                    var p = ch.transform.position;
                    if ((p - cam).sqrMagnitude > r2) continue;
                    Vector3 v = Vector3.zero;
                    if (_lastPos.TryGetValue(ch, out var lp)) v = (p - lp) / dt;
                    if (v.sqrMagnitude > 400) v = Vector3.zero;   // teleport
                    _lastPos[ch] = p;
                    float size = Mathf.Clamp(ch.transform.lossyScale.y, 0.5f, 3f);
                    _benderData[n * 2] = new Vector4(p.x, p.z, _s.scatterBendRadius * size, 1);
                    _benderData[n * 2 + 1] = new Vector4(v.x, v.z, 0, 0);
                    n++;
                }
                if (_lastPos.Count > 256) _lastPos.Clear();
                _benders.SetData(_benderData, 0, 0, Math.Max(1, n * 2));
                if (!_trailValid) { _trailPrev = new Vector2(cam.x + 1e4f, cam.z + 1e4f); _trailValid = true; }   // first frame: every texel re-anchored (cleared)
                _cs.SetTexture(_kBend, TrailId, _trail);
                _cs.SetBuffer(_kBend, BendersId, _benders);
                _cs.SetInt(TrailNId, TrailN);
                _cs.SetInt(BenderCountId, n);
                _cs.SetVector(TrailPId, new Vector4(TrailExtent, Mathf.Exp(-dt / ScatterBend.Recover), cam.x, cam.z));
                _cs.SetVector(TrailPrevId, new Vector4(_trailPrev.x, _trailPrev.y, 0, 0));
                _cs.Dispatch(_kBend, TrailN / 8, TrailN / 8, 1);
                G3GpuDiag.Count("scatter.bend");
                _trailPrev = new Vector2(cam.x, cam.z);
                Shader.SetGlobalTexture(BendTrailId, _trail);
                Shader.SetGlobalVector(BendParamsId, new Vector4(TrailExtent, 1f / TrailExtent, _s.scatterBendStrength, 1));
                Shader.SetGlobalVector(BendCamId, new Vector4(cam.x, 0, cam.z, 0));
            }

            // ---------------------------------------------------------------------------------------------- per camera

            void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
            {
                if (!enabled || !G3GroundScatter.Draw || cam.cameraType != CameraType.Game || cam != _cam) return;
                if (G3GpuSafe.Capturing) return;   // dx12-3: the save picture renders without the scatter (no indirect draws outliving it)
                if (_instances == null || _typeBuf == null || _args == null || _visible == null || _highWater == 0 || _types.Count == 0) return;
                if (VegetationQuality <= 0 || !G3VegetationRenderer.OptionEnabled) return;
                _sw.Restart();
                using (CullMarker.Auto())
                {
                    GeometryUtility.CalculateFrustumPlanes(cam, _planes);
                    for (int i = 0; i < 6; i++) _planeVecs[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
                    var camPos = cam.transform.position;
                    int count = _highWater * PageSize;
                    bool shadows = _s.scatterShadows && ScatterQuality.Shadows(VegetationQuality) && G3VegetationRenderer.OptionShadows;
                    // crash-2: recorded into the camera's context (GPU order before its draws, no immediate compute from the
                    // camera callback; D3D12 + split graphics jobs)
                    var cmd = CommandBufferPool.Get("G3Scatter.Cull");
                    G3GpuDiag.Begin(cmd, "G3Scatter.Cull");
                    cmd.SetComputeIntParam(_cs, SlotCountId, _slotCount);
                    cmd.SetComputeBufferParam(_cs, _kClear, ArgsId, _argsRW);
                    cmd.DispatchCompute(_cs, _kClear, (_slotCount + 63) / 64, 1, 1);
                    cmd.SetComputeBufferParam(_cs, _kCull, InstancesId, _instances);
                    cmd.SetComputeBufferParam(_cs, _kCull, TypesId, _typeBuf);
                    cmd.SetComputeBufferParam(_cs, _kCull, SlotsId, _slots);
                    cmd.SetComputeBufferParam(_cs, _kCull, VisibleId, _visible);
                    cmd.SetComputeBufferParam(_cs, _kCull, ArgsId, _argsRW);
                    cmd.SetComputeIntParam(_cs, CountId, count);
                    cmd.SetComputeVectorArrayParam(_cs, PlanesId, _planeVecs);
                    cmd.SetComputeVectorParam(_cs, CamPosId, new Vector4(camPos.x, camPos.y, camPos.z, 3f));
                    var gp = new Vector4(0.3f, 0.6f, G3GroundScatter.DebugTint ? 1 : 0, shadows ? 1 : 0);   // thin floor 30 % at the range end, survivors grow 0.6 x 1/sqrt
                    cmd.SetComputeVectorParam(_cs, ParamsId, gp);
                    cmd.DispatchCompute(_cs, _kCull, Math.Min((count + 63) / 64, 65535), 1, 1);
                    cmd.SetComputeBufferParam(_cs, _kClamp, SlotsId, _slots);
                    cmd.SetComputeBufferParam(_cs, _kClamp, ArgsId, _argsRW);
                    cmd.DispatchCompute(_cs, _kClamp, (_slotCount + 63) / 64, 1, 1);
                    cmd.CopyBuffer(_argsRW, _args);
                    G3GpuDiag.End(cmd, "G3Scatter.Cull");
                    G3GpuDiag.Count("scatter.cull");
                    ctx.ExecuteCommandBuffer(cmd);
                    CommandBufferPool.Release(cmd);
                    Shader.SetGlobalVector(GParamsId, gp);

                    Shader.SetGlobalBuffer(GInstancesId, _instances);
                    Shader.SetGlobalBuffer(GVisibleId, _visible);
                    Shader.SetGlobalBuffer(GTypesId, _typeBuf);
                    var rp = new RenderParams(_types[0].material)
                    {
                        camera = cam,
                        layer = 0,
                        receiveShadows = true,
                        lightProbeUsage = LightProbeUsage.BlendProbes,
                        motionVectorMode = G3WorldV2.PlantMotionMode,
                        worldBounds = new Bounds(camPos, Vector3.one * (2 * Mathf.Max(_ranges[2], 1) + 50)),
                    };
                    foreach (var g in _groups)
                    {
                        if (g.cmdCount == 0) continue;
                        rp.material = g.material;
                        rp.shadowCastingMode = g.shadows && shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                        // pass 2 (CPU): rigid groups (litter, debris, rocks) skip the per-object motion vector pass - URP's
                        // camera motion covers them; only plants with wind/bending draw it
                        rp.motionVectorMode = g.animated ? G3WorldV2.PlantMotionMode : MotionVectorGenerationMode.Camera;
                        Graphics.RenderMeshIndirect(rp, g.mesh, _args, g.cmdCount, g.firstCmd);
                        _drawCalls++;
                    }
                }
                _cpuAcc += (float)_sw.Elapsed.TotalMilliseconds;
            }

            // ---------------------------------------------------------------------------------------------- report

            public string Probe(float x, float z)
            {
                string res = "worker timeout";
                var done = new ManualResetEventSlim(false);
                Post(() =>
                {
                    var idx = _index;
                    if (idx == null) { res = "no index"; done.Set(); return; }
                    var sb = new System.Text.StringBuilder();
                    sb.Append(idx.Sample(x, z, out var g) ? $"ground y {g.Y:0.00} n.y {g.Ny:0.00} {g.A}|{g.B} a {g.Alpha:0.00}" : "no ground");
                    if (_canopy != null) { _canopy.At(x, z, out float cb, out float cc); sb.Append($" canopy broad {cb:0.00} conifer {cc:0.00} ({_canopy.Count} trees)"); }
                    sb.Append($"; index {idx.PartCount} cells {idx.TriangleCount} tris {idx.MemoryBytes / 1048576f:0.0} MB, instance mirror {_mirror.Length * InstStride / 1048576f:0.0} MB; classes 64 m:");
                    var hist = new int[(int)GroundClass.Count + 1];
                    int none = 0;
                    for (int j = -32; j < 32; j += 2)
                        for (int i = -32; i < 32; i += 2)
                            if (idx.Sample(x + i, z + j, out var s2)) { hist[(int)(s2.Alpha < 0.5f ? s2.A : s2.B)]++; } else none++;
                    for (int k = 0; k < hist.Length; k++) if (hist[k] > 0) sb.Append($" {(GroundClass)k} {hist[k]}");
                    sb.Append($" nohit {none}");
                    res = sb.ToString();
                    done.Set();
                });
                done.Wait(3000);
                long key = ScatterTiles.Key((int)Math.Floor(x / _tileSize), (int)Math.Floor(z / _tileSize));
                if (_tiles.TryGetValue(key, out var t))
                {
                    res += $"; tile {t.tx},{t.tz} biome {t.biome} v{t.version} occ {(t.occ == null ? "none" : "ok")}";
                    if (t.occ != null)
                    {
                        float cell = _tileSize / OccN;
                        int cx = Mathf.Clamp((int)((x - t.tx * _tileSize) / cell), 0, OccN - 1), cz = Mathf.Clamp((int)((z - t.tz * _tileSize) / cell), 0, OccN - 1);
                        int nan = 0; foreach (var o in t.occ) if (float.IsNaN(o)) nan++;
                        res += $" here {t.occ[cz * OccN + cx]:0.00} (NaN {nan}/{t.occ.Length})";
                    }
                    for (int l = 0; l < 3; l++) if (t.units[l] != null) res += $"; L{l} {t.units[l].count} v{t.units[l].version}{(t.units[l].pending ? " pending" : "")}";
                }
                return res;
            }

            public string Describe()
            {
                int units = 0, inst = 0, pending = 0;
                foreach (var t in _tiles.Values)
                    foreach (var u in t.units)
                        if (u != null) { units++; inst += u.count; if (u.pending) pending++; }
                int resolved = 0;
                foreach (var dt in _types) if (dt.resident > 0) resolved++;
                var missing = new List<string>();
                if (_typeOf != null)
                    for (int i = 0; i < _typeOf.Length; i++)
                        if (_typeOf[i].Length == 0 || _typeOf[i][0] < 0) missing.Add(_catalog.Items[i].Id);
                long gpuBytes = (long)_capacity * InstStride + (long)_visibleCount * 4 + _slotCount * 28L + (_trail != null ? TrailN * TrailN * 4 : 0);
                return $"scatter on: quality {VegetationQuality} density x{_densityApplied:0.##} range x{_rangeQ:0.##}; tiles {_tiles.Count}, units {units} ({pending} pending), " +
                       $"{inst} instances in {_highWater} pages ({_freePages.Count} free), {_types.Count} types ({resolved} drawn) in {_groups.Count} calls, cells {_cells.Count}, trees {_trees}, " +
                       $"GPU {gpuBytes / (1024f * 1024f):0.0} MB, cpu {lastCpuMs:0.00} ms; missing [{string.Join(",", missing)}]";
            }
        }
    }
}
