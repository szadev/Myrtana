using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Remaster-look light pass (Docs/remaster-world.md section 11), part of the Lighting world part while the JSON has
    /// <c>"v2": true</c>: sky occlusion and sun contact shadows applied by ratio (Hidden/G3/Remaster/LightRatio, maths in
    /// <see cref="WorldV2Light"/>). The sky occlusion reads a top-down occluder map around the camera: R = highest solid
    /// surface (one downward physics ray per texel, <c>RaycastCommand</c> batch on worker threads; characters and loose
    /// rigid bodies are looked through), G = highest tree crown (G3's SpeedTree LODGroups, splatted from their bounds when
    /// a cell scene loads). Rebuilt when the camera leaves the inner half of the window or cells stream in (at most every
    /// 2 s). Off = no pass, no rebuilds; nothing in the frame changes.
    /// </summary>
    public static class G3WorldV2Light
    {
        static readonly int RatioId = Shader.PropertyToID("_G3LightRatio");
        static readonly int Ratio2Id = Shader.PropertyToID("_G3LightRatio2");
        static readonly int Ratio3Id = Shader.PropertyToID("_G3LightRatio3");   // fix-258
        static readonly int WindowId = Shader.PropertyToID("_G3SkyOccWindow");
        static readonly int MapId = Shader.PropertyToID("_G3SkyOccMap");
        static readonly int AmbCubeId = Shader.PropertyToID("_G3AmbCube");
        static readonly int RatioTexId = Shader.PropertyToID("_G3LightRatioTex");
        static readonly int RatioTexelId = Shader.PropertyToID("_G3LightRatioTexel");
        static readonly int BounceId = Shader.PropertyToID("_G3Bounce");
        static readonly int NormalsValidId = Shader.PropertyToID("_G3LrNormals");   // fix-178

        /// <summary>fix-178: the ratio uses the camera normals texture (smooth vertex normals) instead of the depth-derived face
        /// normal; false = the old face normals (A/B shots only, not stored).</summary>
        public static bool SmoothNormals { get; set; } = true;
        static readonly int BounceTexId = Shader.PropertyToID("_G3BounceTex");
        static readonly int BounceSrcTexelId = Shader.PropertyToID("_G3BounceSrcTexel");
        static readonly int ExposureId = Shader.PropertyToID("_G3Exposure");
        static readonly int ExposureTexId = Shader.PropertyToID("_G3ExposureTex");
        static readonly int ExposureSrcId = Shader.PropertyToID("_G3ExposureSrc");
        const int PassExposureDown = 4, PassExposureFinal = 5;
        static readonly int SsgiTexId = Shader.PropertyToID("_G3SsgiTex");
        static readonly int SsgiRatioId = Shader.PropertyToID("_G3SsgiRatio");
        static readonly int SsgiRatio2Id = Shader.PropertyToID("_G3SsgiRatio2");

        // remaster-gi2 (section 17): the 1x1 exposure of Modern lighting (persistent: the runner reads it back)
        static readonly RenderTexture[] _expRts = new RenderTexture[2];   // ping-pong: the final pass blends with the previous frame's
        static readonly RTHandle[] _expHandles = new RTHandle[2];
        static RenderTexture _expRt;   // the one written last (composite, read-back)
        static int _expIdx;
        static bool _expValid;
        static float _expTime;
        static readonly int ExposurePrevId = Shader.PropertyToID("_G3ExposurePrev");
        static Vector4 _exposure = new Vector4(1, 0, 0, 0);
        static int _expFrame;
        static bool _expPending;

        static bool _on;
        static Material _mat;
        static RatioPass _pass;
        static int _frame;

        // occluder map
        static Texture2D _map;
        static ushort[] _pixels;
        static float _cx, _cz, _size;
        static int _res;
        static bool _built;
        static bool _dirty;
        static float _lastBuild = -100;
        static NativeArray<RaycastCommand> _cmds;
        static NativeArray<RaycastHit> _hits;
        static JobHandle _job;
        static bool _pending;
        static float _pcx, _pcz, _pSize; static int _pRes;
        static readonly Dictionary<EntityId, bool> _seeThrough = new Dictionary<EntityId, bool>();

        struct Crown { public float x, z, r, bottom, top; }
        static readonly Dictionary<SceneHandle, List<Crown>> _crowns = new Dictionary<SceneHandle, List<Crown>>();   // per scene handle

        // fix-171 perf markers
        static readonly global::Unity.Profiling.ProfilerMarker PmBegin = new global::Unity.Profiling.ProfilerMarker("G3WorldV2Light.BeginCamera");
        static readonly global::Unity.Profiling.ProfilerMarker PmSchedule = new global::Unity.Profiling.ProfilerMarker("G3WorldV2Light.MapSchedule");
        static readonly global::Unity.Profiling.ProfilerMarker PmFinish = new global::Unity.Profiling.ProfilerMarker("G3WorldV2Light.MapFinish");
        static readonly global::Unity.Profiling.ProfilerMarker PmCrowns = new global::Unity.Profiling.ProfilerMarker("G3WorldV2Light.Crowns");

        static readonly Vector3[] CubeDirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static readonly Color[] _cube = new Color[6];
        static readonly Vector4[] _cubeV = new Vector4[6];

        /// <summary>True while the pass is applied.</summary>
        public static bool Active => _on;

        /// <summary>remaster-gi2: last read-back exposure of Modern lighting (x exposure, y frame mean with, z without; every ~15 frames).</summary>
        public static Vector4 Exposure => _exposure;

        static RTHandle ExposureTarget(int i)
        {
            if (_expRts[i] == null)
            {
                _expRts[i] = new RenderTexture(1, 1, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat)
                    { name = "G3 Modern Exposure " + i, hideFlags = HideFlags.DontSave, filterMode = FilterMode.Point };
                _expRts[i].Create();
                _expHandles[i]?.Release();
                _expHandles[i] = RTHandles.Alloc(_expRts[i]);
                _expValid = false;
            }
            return _expHandles[i];
        }

        static void ReadExposure()
        {
            if (_expRt == null || _expPending || ++_expFrame % 15 != 0 || !SystemInfo.supportsAsyncGPUReadback) return;
            _expPending = true;
            AsyncGPUReadback.Request(_expRt, 0, r =>
            {
                _expPending = false;
                if (r.hasError) return;
                var d = r.GetData<Vector4>();
                if (d.Length > 0) _exposure = d[0];
            });
        }

        /// <summary>Turns the pass on or off (G3WorldV2.Apply: Lighting part and the JSON toggles).</summary>
        public static void Apply(bool on)
        {
            on &= WorldV2Light.PassWanted;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _on = on;
            G3WorldGi.Apply(on);   // remaster-gi (section 16): radiance volume + probes, read by this pass
            if (!on)
            {
                Shader.SetGlobalVector(WindowId, Vector4.zero);
                return;
            }
            if (_mat == null)
            {
                var sh = Resources.Load<Shader>("G3RemasterLightRatio");
                if (sh == null) { Debug.LogWarning("[G3WorldV2Light] shader G3RemasterLightRatio missing"); _on = false; return; }
                _mat = new Material(sh) { name = "G3 Remaster Light Ratio", hideFlags = HideFlags.DontSave };
            }
            _pass ??= new RatioPass(_mat);
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            if (WorldRemasterLook.Current.v2SkyOcclusion && _crowns.Count == 0)
                for (int i = 0; i < SceneManager.sceneCount; i++) CollectCrowns(SceneManager.GetSceneAt(i));
            _dirty = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        /// <summary>One line for the runner.</summary>
        public static string Describe()
        {
            if (!_on) return "light pass off";
            int crowns = 0;
            foreach (var l in _crowns.Values) crowns += l.Count;
            var c = WorldRemasterLook.Current;
            return $"light pass: sky occlusion {(c.v2SkyOcclusion ? $"{c.v2SkyOccStrength:0.##} (window {_size:0} m/{_res}, built {_built}, {crowns} crowns)" : "off")}, " +
                   $"contact {(c.v2Contact ? $"{c.v2ContactStrength:0.##}/{c.v2ContactLength:0.##} m/{c.v2ContactSteps}" : "off")}, 1/{c.v2LightDownsample} res";
        }

        static readonly global::Unity.Profiling.ProfilerMarker SceneMarker = new global::Unity.Profiling.ProfilerMarker("G3WorldV2Light.SceneLoaded");

        static void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            using var _ = SceneMarker.Auto();
            if (WorldRemasterLook.Current.v2SkyOcclusion) QueueCrowns(s);
            _dirty = true;
        }

        static void OnSceneUnloaded(Scene s)
        {
            _crowns.Remove(s.handle);
            _crownQueue.RemoveAll(q => q.scene == s.handle);
            _dirty = true;
        }

        // fix-171: the crowns of a loaded cell are collected a few roots per frame (a cell scene's LODGroup walk and
        // GetLODs cost 1-3 ms on the main thread, in the frame the cell streamed in)
        sealed class CrownJob { public SceneHandle scene; public GameObject[] roots; public int next; public List<Crown> list; }
        static readonly List<CrownJob> _crownQueue = new List<CrownJob>();
        static readonly List<LODGroup> _lodBuffer = new List<LODGroup>();

        static void QueueCrowns(Scene s)
        {
            if (!s.isLoaded) return;
            _crownQueue.RemoveAll(q => q.scene == s.handle);
            _crownQueue.Add(new CrownJob { scene = s.handle, roots = s.GetRootGameObjects(), list = new List<Crown>() });
        }

        static void CollectCrowns(Scene s)
        {
            if (!s.isLoaded) return;
            var job = new CrownJob { scene = s.handle, roots = s.GetRootGameObjects(), list = new List<Crown>() };
            while (job.next < job.roots.Length) CrownsOf(job.roots[job.next++], job.list);
            _crowns[s.handle] = job.list;
        }

        /// <summary>Works on the queued cells' crowns for up to <paramref name="budgetMs"/>; a finished cell marks the map dirty.</summary>
        static void StepCrowns(double budgetMs)
        {
            if (_crownQueue.Count == 0) return;
            using var _pm = PmCrowns.Auto();
            var t0 = Time.realtimeSinceStartupAsDouble;
            while (_crownQueue.Count > 0 && (Time.realtimeSinceStartupAsDouble - t0) * 1000 < budgetMs)
            {
                var job = _crownQueue[0];
                if (job.next < job.roots.Length)
                {
                    var root = job.roots[job.next++];
                    if (root != null) CrownsOf(root, job.list);
                    continue;
                }
                _crownQueue.RemoveAt(0);
                _crowns[job.scene] = job.list;
                _dirty = true;
            }
        }

        static void CrownsOf(GameObject root, List<Crown> list)
        {
            root.GetComponentsInChildren(true, _lodBuffer);
            foreach (var g in _lodBuffer)
            {
                if (!g.name.StartsWith("G3_Tree", System.StringComparison.Ordinal)) continue;
                var lods = g.GetLODs();
                if (lods.Length == 0) continue;
                bool any = false;
                Bounds b = default;
                foreach (var r in lods[0].renderers)
                {
                    if (r == null) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                if (!any || b.size.y < 1) continue;
                var cr = WorldV2Light.Crown(b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z);
                list.Add(new Crown { x = cr.x, z = cr.z, r = cr.radius, bottom = cr.bottom, top = cr.top });
            }
            _lodBuffer.Clear();
        }

        // ------------------------------------------------------------------ occluder map

        /// <summary>fix-171: fills the raycast commands on the job workers (65 536 commands were ~3 ms on the main thread).</summary>
        struct FillJob : IJobParallelFor
        {
            public NativeArray<RaycastCommand> cmds;
            public float minX, minZ, top, texel;
            public int res;
            public QueryParameters qp;
            public void Execute(int k)
            {
                int j = k / res, i = k - j * res;
                cmds[k] = new RaycastCommand(new Vector3(minX + (i + 0.5f) * texel, top, minZ + (j + 0.5f) * texel), Vector3.down, qp, 2000f);
            }
        }

        // fix-171: the finished batch is turned into the map over several frames (a main-thread budget per frame): the hits
        // (see-through lookups), the crown splat into a float buffer, the half conversion, the upload; the old map stays in
        // use until the new one is complete (build 21a: one 5-10 ms frame every 2 s while cells streamed in)
        static int _finishStage, _fk;
        static float[] _solid, _crownTop;
        static List<Crown>[] _crownLists;
        static int _crownList, _crownIndex;
        const double FinishBudgetMs = 0.6;

        static void UpdateMap(Camera cam)
        {
            var c = WorldRemasterLook.Current;
            if (!c.v2SkyOcclusion) return;
            StepCrowns(0.4);
            if (_finishStage > 0) { StepFinish(FinishBudgetMs); return; }
            if (_pending)
            {
                if (!_job.IsCompleted) return;
                _job.Complete();
                BeginFinish();
                StepFinish(FinishBudgetMs);
                return;
            }
            var p = cam.transform.position;
            float size = c.v2SkyOccRange;
            int res = c.v2SkyOccResolution;
            bool moved = WorldV2Light.NeedsRecenter(p.x, p.z, _cx, _cz, size, _built && _size == size && _res == res);
            if (!moved && !(_dirty && Time.unscaledTime - _lastBuild > c.v2SkyOccInterval)) return;
            using var _pm = PmSchedule.Auto();
            float texel = size / res;
            _pcx = WorldV2Light.Snap(p.x, texel); _pcz = WorldV2Light.Snap(p.z, texel);
            _pSize = size; _pRes = res;
            _dirty = false;
            _lastBuild = Time.unscaledTime;
            int n = res * res;
            if (!_cmds.IsCreated || _cmds.Length != n)
            {
                if (_cmds.IsCreated) _cmds.Dispose();
                if (_hits.IsCreated) _hits.Dispose();
                _cmds = new NativeArray<RaycastCommand>(n, Allocator.Persistent);
                _hits = new NativeArray<RaycastHit>(n, Allocator.Persistent);
            }
            var fill = new FillJob
            {
                cmds = _cmds, minX = _pcx - size * 0.5f, minZ = _pcz - size * 0.5f, top = p.y + 600, texel = texel, res = res,
                qp = new QueryParameters(~0, false, QueryTriggerInteraction.Ignore, false),
            };
            var filled = fill.Schedule(n, 1024);
            _job = RaycastCommand.ScheduleBatch(_cmds, _hits, 256, 1, filled);
            _pending = true;
        }

        static bool SeeThrough(RaycastHit h)
        {
            var id = h.colliderEntityId;
            if (_seeThrough.TryGetValue(id, out bool st)) return st;
            var col = h.collider;
            st = col == null || col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic
                 || col is CharacterController || col.GetComponentInParent<Animator>() != null;
            if (_seeThrough.Count > 20000) _seeThrough.Clear();
            _seeThrough[id] = st;
            return st;
        }

        static void BeginFinish()
        {
            _pending = false;
            int n = _pRes * _pRes;
            if (_solid == null || _solid.Length != n) { _solid = new float[n]; _crownTop = new float[n]; }
            for (int k = 0; k < n; k++) _crownTop[k] = -10000f;
            _crownLists = new List<Crown>[_crowns.Count];
            _crowns.Values.CopyTo(_crownLists, 0);
            _crownList = _crownIndex = 0;
            _fk = 0;
            _finishStage = 1;
        }

        static void StepFinish(double budgetMs)
        {
            using var _pm = PmFinish.Auto();
            var t0 = Time.realtimeSinceStartupAsDouble;
            bool Over() => (Time.realtimeSinceStartupAsDouble - t0) * 1000 >= budgetMs;
            int res = _pRes, n = res * res;
            float size = _pSize, texel = size / res;
            float minX = _pcx - size * 0.5f, minZ = _pcz - size * 0.5f;
            if (_finishStage == 1)
            {
                // the solid surface per texel, 2048 hits between budget checks
                while (_fk < n)
                {
                    int end = Mathf.Min(n, _fk + 2048);
                    for (int k = _fk; k < end; k++)
                    {
                        var h = _hits[k];
                        float y = -10000f;
                        if (h.colliderEntityId.IsValid())
                        {
                            y = h.point.y;
                            if (SeeThrough(h))   // look through characters / loose bodies: the surface below them
                                y = Physics.Raycast(h.point + Vector3.down * 0.02f, Vector3.down, out var h2, 2000f, ~0, QueryTriggerInteraction.Ignore) ? h2.point.y : -10000f;
                        }
                        _solid[k] = y;
                    }
                    _fk = end;
                    if (Over()) return;
                }
                _finishStage = 2;
            }
            if (_finishStage == 2)
            {
                // tree crowns: the highest crown top per texel
                float maxX = minX + size, maxZ = minZ + size;
                while (_crownList < _crownLists.Length)
                {
                    var list = _crownLists[_crownList];
                    while (_crownIndex < list.Count)
                    {
                        var cr = list[_crownIndex++];
                        if (cr.x + cr.r < minX || cr.x - cr.r > maxX || cr.z + cr.r < minZ || cr.z - cr.r > maxZ) continue;
                        int i0 = Mathf.Max(0, (int)((cr.x - cr.r - minX) / texel)), i1 = Mathf.Min(res - 1, (int)((cr.x + cr.r - minX) / texel));
                        int j0 = Mathf.Max(0, (int)((cr.z - cr.r - minZ) / texel)), j1 = Mathf.Min(res - 1, (int)((cr.z + cr.r - minZ) / texel));
                        float r2 = cr.r * cr.r;
                        for (int j = j0; j <= j1; j++)
                        {
                            float dz = minZ + (j + 0.5f) * texel - cr.z;
                            for (int i = i0; i <= i1; i++)
                            {
                                float dx = minX + (i + 0.5f) * texel - cr.x;
                                if (dx * dx + dz * dz > r2) continue;
                                int k = j * res + i;
                                if (_crownTop[k] < cr.top) _crownTop[k] = cr.top;
                            }
                        }
                        if ((_crownIndex & 63) == 0 && Over()) return;
                    }
                    _crownList++; _crownIndex = 0;
                }
                _finishStage = 3;
                _fk = 0;
                if (Over()) return;
            }
            if (_finishStage == 3)
            {
                if (_pixels == null || _pixels.Length != n * 2) _pixels = new ushort[n * 2];
                while (_fk < n)
                {
                    int end = Mathf.Min(n, _fk + 4096);
                    for (int k = _fk; k < end; k++)
                    {
                        _pixels[k * 2] = Mathf.FloatToHalf(_solid[k]);
                        _pixels[k * 2 + 1] = Mathf.FloatToHalf(_crownTop[k]);
                    }
                    _fk = end;
                    if (Over()) return;
                }
                _finishStage = 4;
            }
            if (_map == null || _map.width != res)
            {
                if (_map != null) Object.Destroy(_map);
                _map = new Texture2D(res, res, TextureFormat.RGHalf, false, true)
                {
                    name = "G3 Sky Occlusion Map", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
                };
            }
            _map.SetPixelData(_pixels, 0);
            _map.Apply(false, false);
            _cx = _pcx; _cz = _pcz; _size = size; _res = res;
            _built = true;
            _finishStage = 0;
            _crownLists = null;
        }

        static void ReleaseMap()
        {
            if (_pending) { _job.Complete(); _pending = false; }
            _finishStage = 0; _crownLists = null; _crownQueue.Clear();
            if (_cmds.IsCreated) _cmds.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            _built = false;
        }

        // ------------------------------------------------------------------ per camera

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_on || _mat == null) return;
            if (cam.cullingMask == 0) return;
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
            using var _pm = PmBegin.Auto();
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null || data.renderType != CameraRenderType.Base) return;
            var c = WorldRemasterLook.Current;
            if (cam.cameraType == CameraType.Game && Application.isPlaying) UpdateMap(cam);

            // ambient cube from G3's SH probe (as G3Environment / G3WorldV2 set it this frame)
            RenderSettings.ambientProbe.Evaluate(CubeDirs, _cube);
            for (int i = 0; i < 6; i++) _cubeV[i] = new Vector4(_cube[i].r, _cube[i].g, _cube[i].b, 0);
            _mat.SetVectorArray(AmbCubeId, _cubeV);
            var g = WorldV2Light.RatioGlobal;
            _mat.SetVector(RatioId, new Vector4(g[0], g[1], g[2], g[3]));
            _frame = (_frame + 1) & 63;
            var g2 = WorldV2Light.Ratio2Global;
            _mat.SetVector(Ratio2Id, new Vector4(g2[0], g2[1], g2[2], _frame));
            var g3 = WorldV2Light.Ratio3Global;
            _mat.SetVector(Ratio3Id, new Vector4(g3[0], g3[1], g3[2], g3[3]));
            bool map = _built && c.v2SkyOcclusion && _map != null;
            _mat.SetVector(WindowId, map ? new Vector4(_cx - _size * 0.5f, _cz - _size * 0.5f, _size, 1) : Vector4.zero);
            if (map) _mat.SetTexture(MapId, _map);
            bool gi = G3WorldGi.BeginCamera(cam, _mat, _cubeV, map ? _map : null, map ? new Vector4(_cx - _size * 0.5f, _cz - _size * 0.5f, _size, 1) : Vector4.zero);
            bool modern = gi && WorldGi.Modern;
            bool soft = modern && WorldGi.Soft;   // lighting-new: the matched exposure only for "Soft"; "New" has its own auto-exposure
            _mat.SetKeyword(new LocalKeyword(_mat.shader, "_G3LR_EXPOSURE"), soft);
            if (soft) ReadExposure();
            G3LightNew.SetRatio(cam, _mat, _cubeV);   // lighting-new (section 21): sky light + uniforms of the New branch
            // remaster-ssgi (section 19): per-pixel screen-space visibility + bounce under Modern lighting
            bool ssgi = G3WorldSsgi.BeginCamera(cam, modern);
            if (cam.cameraType == CameraType.Game) G3WorldRemasterLook.ScaleSsao(ssgi ? c.v2SsgiUrpSsao : 1f);
            _mat.SetVector(SsgiRatioId, ssgi ? new Vector4(1, c.v2SsgiAo, c.v2SsgiGain, c.v2SsgiFade) : Vector4.zero);
            var r2 = WorldSsgi.RatioGlobal2();
            _mat.SetVector(SsgiRatio2Id, new Vector4(r2[0], r2[1], 0, 0));
            _pass.Setup(c.v2LightDownsample, c.v2SkyOcclusion && map ? c.v2Bounce : 0, gi, soft, ssgi);
            data.scriptableRenderer.EnqueuePass(_pass);
        }

        /// <summary>Ratio at 1/1 or 1/2 resolution (pass 0), then depth-aware upsample and multiply (pass 1).</summary>
        sealed class RatioPass : ScriptableRenderPass
        {
            readonly Material _mat;
            int _down = 2;
            float _bounce;
            bool _gi, _modern, _ssgi;

            sealed class RatioData { public TextureHandle source, bounce, ssgi; public Material material; public Vector4 bounceParams; public bool normals; }
            sealed class DownData { public TextureHandle source; public Material material; public Vector4 texel; public int pass; }
            sealed class CompositeData { public TextureHandle source, ratio, exposure; public Material material; public Vector4 texel; }
            sealed class ExpData { public TextureHandle source, prev; public Material material; public Vector4 size; public int pass; }

            public RatioPass(Material mat)
            {
                _mat = mat;
                // after the opaques (SSAO applied) and the sky, before the v2 fog/shafts and the aerial pass (both at BeforeRenderingTransparents)
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 10;
                requiresIntermediateTexture = true;
                // fix-178: + the normals (smooth vertex normals for the ratio; SSAO already asks for the depth-normals prepass)
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            }

            public void Setup(int down, float bounce, bool gi, bool modern, bool ssgi) { _down = Mathf.Clamp(down, 1, 2); _bounce = bounce; _gi = gi; _modern = modern; _ssgi = ssgi; }

            // remaster-gi2 exposure: 4x4 means down to <= 16 x 16, then one texel (WorldGi.Exposure)
            TextureHandle ExposureChain(RenderGraph rg, TextureHandle sums, int w, int h)
            {
                var src = sums;
                while (w > 16 || h > 16)
                {
                    int nw = Mathf.Max(1, (w + 3) / 4), nh = Mathf.Max(1, (h + 3) / 4);
                    var d = new TextureDesc(nw, nh)
                    {
                        name = "G3 Exposure Down", colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32_SFloat,
                        depthBufferBits = DepthBits.None, msaaSamples = MSAASamples.None, clearBuffer = false, filterMode = FilterMode.Point,
                    };
                    var dst = rg.CreateTexture(d);
                    ExpBlit(rg, src, dst, new Vector4(w, h, 0, 0), PassExposureDown, "G3 Exposure Down");
                    src = dst; w = nw; h = nh;
                }
                // eye adaptation: blend with the previous frame's exposure (time constant ~0.4 s; a jump > 3 s apart restarts)
                float now = Time.unscaledTime, dt = now - _expTime;
                _expTime = now;
                bool valid = _expValid && dt < 3f;
                float rate = valid ? 1f - Mathf.Exp(-Mathf.Max(dt, 1e-3f) / 0.4f) : 1f;
                var prev = rg.ImportTexture(ExposureTarget(_expIdx));
                _expIdx ^= 1;
                var target = rg.ImportTexture(ExposureTarget(_expIdx));
                _expRt = _expRts[_expIdx];
                _expValid = true;
                using (var builder = rg.AddRasterRenderPass<ExpData>("G3 Exposure", out var ed))
                {
                    ed.source = src; ed.prev = prev; ed.material = _mat; ed.size = new Vector4(w, h, rate, 0); ed.pass = PassExposureFinal;
                    builder.UseTexture(src);
                    builder.UseTexture(prev);
                    builder.SetRenderAttachment(target, 0);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((ExpData x, RasterGraphContext ctx) =>
                    {
                        var b = G3PassBlit.Begin(x.source);   // dx12-2: values with the draw, not on the shared material
                        b.SetVector(ExposureSrcId, x.size);
                        b.SetTexture(ExposurePrevId, (RTHandle)x.prev);
                        G3PassBlit.Draw(ctx.cmd, x.material, x.pass);
                    });
                }
                return target;
            }

            void ExpBlit(RenderGraph rg, TextureHandle src, TextureHandle dst, Vector4 size, int pass, string name)
            {
                using (var builder = rg.AddRasterRenderPass<ExpData>(name, out var ed))
                {
                    ed.source = src; ed.material = _mat; ed.size = size; ed.pass = pass;
                    builder.UseTexture(src);
                    builder.SetRenderAttachment(dst, 0);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((ExpData x, RasterGraphContext ctx) =>
                    {
                        G3PassBlit.Begin(x.source).SetVector(ExposureSrcId, x.size);   // dx12-2: each level its own size
                        G3PassBlit.Draw(ctx.cmd, x.material, x.pass);
                    });
                }
            }

            TextureHandle Down(RenderGraph rg, UniversalResourceData res, TextureHandle src, TextureDesc srcDesc, int factor, int pass, string name, out TextureDesc outDesc)
            {
                var d = srcDesc;
                d.name = name;
                d.width = Mathf.Max(1, srcDesc.width / factor);
                d.height = Mathf.Max(1, srcDesc.height / factor);
                d.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                d.depthBufferBits = DepthBits.None;
                d.msaaSamples = MSAASamples.None;
                d.clearBuffer = false;
                d.filterMode = FilterMode.Bilinear;
                outDesc = d;
                var dst = rg.CreateTexture(d);
                using (var builder = rg.AddRasterRenderPass<DownData>(name, out var dd))
                {
                    dd.source = src; dd.material = _mat; dd.pass = pass;
                    dd.texel = new Vector4(1f / srcDesc.width, 1f / srcDesc.height, srcDesc.width, srcDesc.height);
                    builder.UseTexture(src);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(dst, 0);
                    builder.SetRenderFunc((DownData x, RasterGraphContext ctx) =>
                    {
                        G3PassBlit.Begin(x.source).SetVector(BounceSrcTexelId, x.texel);   // dx12-2: 1/4 and 1/16 each their texel
                        G3PassBlit.Draw(ctx.cmd, x.material, x.pass);
                    });
                }
                return dst;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer) return;
                var source = res.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(source);

                var small = desc;
                small.name = "G3 Light Ratio";
                small.width = Mathf.Max(1, desc.width / _down);
                small.height = Mathf.Max(1, desc.height / _down);
                small.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                small.depthBufferBits = DepthBits.None;
                small.msaaSamples = MSAASamples.None;
                small.clearBuffer = false;
                small.filterMode = FilterMode.Point;
                var ratio = renderGraph.CreateTexture(small);
                TextureHandle sums = TextureHandle.nullHandle;
                if (_modern)
                {
                    var sd = small;
                    sd.name = "G3 Exposure Sums";
                    sd.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32_SFloat;
                    sums = renderGraph.CreateTexture(sd);
                }
                var texel = new Vector4(1f / small.width, 1f / small.height, small.width, small.height);

                // one-bounce colour (11.8): the lit frame blurred to 1/16 (sky dropped, mean depth in a)
                TextureHandle bounce = TextureHandle.nullHandle, b4 = TextureHandle.nullHandle;
                var bounceParams = Vector4.zero;
                if (_bounce > 0 || _gi) b4 = Down(renderGraph, res, source, desc, 4, 2, "G3 Bounce 1/4", out var d4);   // also the GI injection's colour
                if (_bounce > 0)
                {
                    var d4b = renderGraph.GetTextureDesc(b4);
                    bounce = Down(renderGraph, res, b4, d4b, 4, 3, "G3 Bounce 1/16", out var d16);
                    bounceParams = new Vector4(_bounce, 1f / d16.width, 1f / d16.height, 0);
                }

                var ssgi = _ssgi && b4.IsValid() ? G3WorldSsgi.Record(renderGraph, res, b4, small) : TextureHandle.nullHandle;

                using (var builder = renderGraph.AddRasterRenderPass<RatioData>("G3 Light Ratio", out var rd))
                {
                    rd.source = source;
                    rd.ssgi = ssgi;
                    if (ssgi.IsValid()) builder.UseTexture(ssgi);
                    rd.material = _mat;
                    rd.bounce = bounce;
                    rd.bounceParams = bounceParams;
                    rd.normals = SmoothNormals && res.cameraNormalsTexture.IsValid();
                    builder.UseTexture(source);
                    if (bounce.IsValid()) builder.UseTexture(bounce);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    if (rd.normals) builder.UseTexture(res.cameraNormalsTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachment(ratio, 0);
                    if (sums.IsValid()) builder.SetRenderAttachment(sums, 1);
                    builder.SetRenderFunc((RatioData d, RasterGraphContext ctx) =>
                    {
                        // dx12-2: this frame's values with the draw (the SSGI strength stays the material's, set per frame
                        // in OnBeginCamera; a frame without the SSGI texture zeroes it for this draw only)
                        var b = G3PassBlit.Begin(d.source);
                        b.SetVector(BounceId, d.bounceParams);
                        b.SetFloat(NormalsValidId, d.normals ? 1f : 0f);
                        if (d.bounce.IsValid()) b.SetTexture(BounceTexId, (RTHandle)d.bounce);
                        if (d.ssgi.IsValid()) b.SetTexture(SsgiTexId, (RTHandle)d.ssgi);
                        else b.SetVector(SsgiRatioId, Vector4.zero);
                        G3PassBlit.Draw(ctx.cmd, d.material, 0);
                    });
                }

                var exposure = sums.IsValid() ? ExposureChain(renderGraph, sums, small.width, small.height) : TextureHandle.nullHandle;

                var full = desc;
                full.name = "G3 Light Ratio Composite";
                full.clearBuffer = false;
                var target = renderGraph.CreateTexture(full);
                using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("G3 Light Ratio Composite", out var cd))
                {
                    cd.source = source;
                    cd.ratio = ratio;
                    cd.material = _mat;
                    cd.texel = texel;
                    cd.exposure = exposure;
                    builder.UseTexture(source);
                    builder.UseTexture(ratio);
                    if (exposure.IsValid()) builder.UseTexture(exposure);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(target, 0);
                    builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
                    {
                        var b = G3PassBlit.Begin(d.source);   // dx12-2: values with the draw
                        b.SetTexture(RatioTexId, (RTHandle)d.ratio);
                        b.SetVector(RatioTexelId, d.texel);
                        b.SetVector(ExposureId, d.exposure.IsValid() ? Vector4.one : Vector4.zero);
                        if (d.exposure.IsValid()) b.SetTexture(ExposureTexId, (RTHandle)d.exposure);
                        G3PassBlit.Draw(ctx.cmd, d.material, 1);
                    });
                }
                res.cameraColor = target;
                if (_gi) G3WorldGi.Record(renderGraph, res, b4, ratio);   // remaster-gi: the voxels see this frame after the ratio
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SmoothNormals = true;   // fix-178 A/B switch
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            Application.quitting -= ReleaseMap;
            Application.quitting += ReleaseMap;
            _on = false;
            _pass = null;
            _crowns.Clear();
            _seeThrough.Clear();
            ReleaseMap();
            _cx = _cz = 0; _size = 0; _res = 0;
            _exposure = new Vector4(1, 0, 0, 0); _expPending = false;
            Shader.SetGlobalVector(WindowId, Vector4.zero);
        }
    }
}
