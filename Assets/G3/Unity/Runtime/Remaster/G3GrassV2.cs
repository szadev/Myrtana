using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// World remaster v2 grass (Docs/remaster-world.md section 10.3): while on, every <see cref="G3VegetationRenderer"/>
    /// draws through here instead of its CPU instancing path: G3's own plants plus the <see cref="GrassDensify"/> copies
    /// (built on a worker thread on first use; the original path draws until they are ready) live in GPU buffers per mesh,
    /// G3GrassCull.compute culls them per camera (frustum, view range x v2GrassDistance, distance thinning of the copies)
    /// and Graphics.RenderMeshIndirect draws them with G3/Remaster/GrassV2 (same atlases, colours, wind and fade as
    /// G3/Vegetation, plus translucency). No GameObjects; buffers are released when switched off.
    /// crash-2 (D3D12 + split graphics jobs DEVICE_HUNG right after an upload): every buffer is created, filled, swapped
    /// and released in <see cref="Prepare"/> (the renderer's LateUpdate), never inside a camera callback; each camera has
    /// its own visible/args buffers (one camera no longer overwrites the culling another one's draws still read); the
    /// cull runs from a CommandBuffer executed in the camera's context (counter reset, cull, clamp, copy into the
    /// indirect args, all in GPU order before the draws); instance counts are clamped to the visible buffer's size on the
    /// GPU; instance data is written once per buffer (SetData only on fresh buffers).
    /// dx12-3: buffers and materials are released through <see cref="G3GpuSafe"/> (a few frames later), and nothing is
    /// culled or drawn here while a script captures the main camera (the save picture).
    /// </summary>
    public static class G3GrassV2
    {
        [StructLayout(LayoutKind.Sequential)]
        struct GpuInst
        {
            public Vector3 pos;
            public float width;
            public Quaternion rot;
            public float height;
            public uint color;
            public float extra;
            public float pad;
        }
        const int Stride = 48;
        /// <summary>Per-camera buffers not used for this many frames are released (a camera that went away).</summary>
        const int CameraIdleFrames = 300;

        sealed class CamBuffers
        {
            public GraphicsBuffer visible, argsRW, args;
            public MaterialPropertyBlock props;
            public int lastFrame;
        }

        sealed class MeshDraw
        {
            public Mesh mesh;
            public Material material;
            public GraphicsBuffer instances;
            public int count, subMeshes;
            public uint[] argsTemplate;   // subMeshes x IndirectDrawIndexedArgs (5 uints) + the visible counter
            public float viewRange, maxHeight, radius, top;
            public readonly Dictionary<Camera, CamBuffers> cams = new Dictionary<Camera, CamBuffers>();
        }

        sealed class State
        {
            public G3VegetationData data;
            public Task<GpuInst[][]> build;
            public List<MeshDraw> draws;
            public Bounds bounds;
            public int g3Count, extraCount;
            public float density;
            public int trees = -1;          // G3 trees in the region when built (canopy thinning; -1 = built without)
            public float checkAt;            // next canopy re-check (realtime)
            public State next;               // the rebuild in progress
            public bool failed;
            public readonly HashSet<Camera> wantCams = new HashSet<Camera>();   // cameras that need buffers (made in Prepare)
        }

        static readonly Dictionary<G3VegetationRenderer, State> _states = new Dictionary<G3VegetationRenderer, State>();
        static bool _enabled;
        static ComputeShader _cull;
        static int _kClear, _kCull, _kFinish;
        static Shader _shader;
        static readonly Plane[] _planes = new Plane[6];
        static readonly Vector4[] _planeVecs = new Vector4[6];
        static readonly List<Camera> _drop = new List<Camera>();
        static readonly int InstancesId = Shader.PropertyToID("_Instances"), VisibleId = Shader.PropertyToID("_Visible"),
            ArgsId = Shader.PropertyToID("_Args"), CapacityId = Shader.PropertyToID("_Capacity"),
            SubMeshesId = Shader.PropertyToID("_SubMeshes"), RowGroupsId = Shader.PropertyToID("_RowGroups"),
            CountId = Shader.PropertyToID("_Count"), PlanesId = Shader.PropertyToID("_Planes"), CamPosId = Shader.PropertyToID("_CamPos"),
            ParamsId = Shader.PropertyToID("_Params"), GrassInstancesId = Shader.PropertyToID("_G3GrassInstances"),
            GrassVisibleId = Shader.PropertyToID("_G3GrassVisible"), FadeId = Shader.PropertyToID("_G3GrassFade");

        public static bool Enabled => _enabled;

        public static void SetEnabled(bool on)
        {
            if (on == _enabled && on) { ReleaseAllIfDensityChanged(); return; }
            _enabled = on;
            if (!on) ReleaseAll();
        }

        public static string Describe()
        {
            int g3 = 0, extra = 0, building = 0;
            foreach (var s in _states.Values)
            {
                g3 += s.g3Count; extra += s.extraCount;
                if (s.draws == null && !s.failed) building++;
            }
            return $"{_states.Count} regions, {g3} G3 + {extra} extra plants{(building > 0 ? $", {building} building" : "")}";
        }

        static bool EnsureShaders()
        {
            if (_cull != null) return true;
            _cull = Resources.Load<ComputeShader>("G3GrassCull");
            // player builds: Resources/G3GrassV2ShaderRef.mat keeps the shader (no scene material uses it; Docs/build.md)
            _shader = Shader.Find("G3/Remaster/GrassV2") ?? Resources.Load<Material>("G3GrassV2ShaderRef")?.shader;
            if (_cull == null || _shader == null)
            {
                _cull = null;
                _enabled = false;
                Debug.LogWarning("[G3GrassV2] G3GrassCull.compute or G3/Remaster/GrassV2 missing: grass v2 off");
                return false;
            }
            _kClear = _cull.FindKernel("Clear");
            _kCull = _cull.FindKernel("Cull");
            _kFinish = _cull.FindKernel("Finish");
            return true;
        }

        /// <summary>
        /// Called by <see cref="G3VegetationRenderer"/> per camera; true = drawn here (the renderer skips its own path).
        /// Creates and releases nothing on the GPU (crash-2): what is missing is asked for and made in <see cref="Prepare"/>,
        /// and the original path draws meanwhile.
        /// </summary>
        public static bool TryDraw(G3VegetationRenderer r, Camera cam, ScriptableRenderContext ctx)
        {
            if (!_enabled || !Application.isPlaying || r.data == null || !SystemInfo.supportsComputeShaders) return false;
            if (!EnsureShaders()) return false;
            // dx12-3: a script rendering the main camera into a picture (save) gets the original path: no indirect draw
            // queued from inside that render can outlive it into the frame's own render
            if (G3GpuSafe.Capturing) return false;
            if (!_states.TryGetValue(r, out var st))
            {
                st = Start(r.data);   // CPU only: a worker task
                _states[r] = st;
            }
            if (st.data != r.data || st.failed || st.draws == null) return false;   // swapped / built in Prepare
            var id = cam;
            foreach (var d in st.draws)
                if (!d.cams.ContainsKey(id)) { st.wantCams.Add(id); return false; }
            Draw(r, st, cam, ctx);
            return true;
        }

        /// <summary>
        /// crash-2: once per frame per renderer, outside any camera callback (the renderer's LateUpdate): finished builds
        /// are uploaded, rebuilds swapped in, per-camera buffers made for the cameras that asked and released for the ones
        /// that went away.
        /// </summary>
        public static void Prepare(G3VegetationRenderer r)
        {
            if (!_enabled || !_states.TryGetValue(r, out var st)) return;
            if (st.data != r.data)
            {
                Release(st);
                if (st.next != null) Release(st.next);
                _states.Remove(r);
                return;   // TryDraw starts the new data's build
            }
            // pass 2: rebuilt in the background when the region's trees changed; the old buffers draw until it is ready
            if (st.next == null && CanopyChanged(st)) st.next = Start(r.data);
            if (st.next != null && st.next.build.IsCompleted)
            {
                var nx = st.next;
                st.next = null;
                if (!nx.build.IsFaulted)
                {
                    Upload(nx, nx.build.Result);
                    foreach (var id in st.wantCams) nx.wantCams.Add(id);
                    if (st.draws != null)
                        foreach (var d in st.draws) foreach (var id in d.cams.Keys) nx.wantCams.Add(id);   // keep drawing on those cameras
                    Release(st);
                    st = nx;
                    _states[r] = st;
                }
            }
            if (st.failed) return;
            if (st.draws == null)
            {
                if (!st.build.IsCompleted) return;   // the original path draws meanwhile
                if (st.build.IsFaulted) { st.failed = true; Debug.LogWarning("[G3GrassV2] build failed: " + st.build.Exception?.GetBaseException().Message); return; }
                Upload(st, st.build.Result);
            }
            int frame = Time.frameCount;
            foreach (var d in st.draws)
            {
                foreach (var id in st.wantCams)
                    if (id != null && !d.cams.ContainsKey(id)) d.cams[id] = MakeCamBuffers(d, frame);
                _drop.Clear();
                foreach (var kv in d.cams)
                    if (kv.Key == null || frame - kv.Value.lastFrame > CameraIdleFrames) _drop.Add(kv.Key);
                foreach (var id in _drop) { Release(d.cams[id]); d.cams.Remove(id); }
            }
            st.wantCams.Clear();
        }

        /// <summary>
        /// dx12-1: the renderer was disabled or destroyed (its region scene unloaded, travel): its buffers and materials are
        /// released now. Before, a destroyed renderer's state stayed in the table for the whole session (every region rebuild
        /// after travel kept up to ~80 MB of instance + visible buffers alive: VRAM pressure on D3D12). Main thread, outside
        /// the camera callbacks (OnDisable).
        /// </summary>
        public static void Forget(G3VegetationRenderer r)
        {
            if (ReferenceEquals(r, null) || !_states.TryGetValue(r, out var st)) return;
            Release(st);
            if (st.next != null) Release(st.next);
            _states.Remove(r);
            PruneDestroyed();
        }

        /// <summary>States whose renderer is gone without an OnDisable (scene teardown order): released.</summary>
        static void PruneDestroyed()
        {
            List<G3VegetationRenderer> dead = null;
            foreach (var kv in _states)
                if (kv.Key == null) (dead ??= new List<G3VegetationRenderer>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var k in dead)
            {
                var st = _states[k];
                Release(st);
                if (st.next != null) Release(st.next);
                _states.Remove(k);
            }
        }

        /// <summary>Live regions (tests / runner).</summary>
        public static int StateCount => _states.Count;

        // ------------------------------------------------------------------ build (worker thread: pure data)

        /// <summary>Pass 2 (remaster-veg): G3's trees round a region changed since its build (streaming brought them later):
        /// built again, at most every 8 s per region.</summary>
        static bool CanopyChanged(State st)
        {
            if (st == null || st.draws == null || Time.realtimeSinceStartup < st.checkAt) return false;
            st.checkAt = Time.realtimeSinceStartup + 8f;
            var cn = G3GroundScatter.Canopy;
            if (cn == null) return st.trees > 0;
            var b = st.bounds;
            int n = cn.CountIn(b.min.x, b.min.z, b.max.x, b.max.z);
            return n != Math.Max(st.trees, 0);
        }

        static State Start(G3VegetationData data)
        {
            var c = WorldRemasterLook.Current;
            var st = new State { data = data, density = c.v2GrassDensity, checkAt = Time.realtimeSinceStartup + 8f };
            var bytes = data.instances != null ? data.instances.bytes : null;
            int meshCount = data.meshes.Count;
            var batches = data.batches.ToArray();
            var kinds = new GrassDensify.Kind[meshCount];
            for (int m = 0; m < meshCount; m++) kinds[m] = GrassDensify.KindOf(data.meshes[m].name);
            st.bounds = new Bounds(data.center, new Vector3(data.halfSize * 2 + 10, 4000, data.halfSize * 2 + 10));
            float density = c.v2GrassDensity;
            // pass 2 (remaster-veg): G3's grass thins out under the crowns while the scatter lays litter there
            var canopy = G3GroundScatter.Canopy;
            float keepUnder = c.scatterGrassUnderCanopy;
            if (canopy != null && keepUnder < 0.999f)
                st.trees = canopy.CountIn(st.bounds.min.x, st.bounds.min.z, st.bounds.max.x, st.bounds.max.z);
            else canopy = null;
            st.build = Task.Run(() => Build(bytes, batches, kinds, density, canopy, keepUnder, out st.g3Count, out st.extraCount));
            return st;
        }

        static GpuInst[][] Build(byte[] bytes, G3VegetationData.Batch[] batches, GrassDensify.Kind[] kinds, float density, ScatterCanopy canopy,
                                 float keepUnder, out int g3Count, out int extraCount)
        {
            int meshCount = kinds.Length;
            g3Count = extraCount = 0;
            if (bytes == null) return new GpuInst[meshCount][];
            int n = bytes.Length / G3VegetationData.InstanceStride;
            var f = new float[n * 10];
            Buffer.BlockCopy(bytes, 0, f, 0, n * G3VegetationData.InstanceStride);
            var meshOf = new int[n];
            foreach (var b in batches) for (int i = 0; i < b.count; i++) meshOf[b.start + i] = b.mesh;

            // memory cap: at most ~1.5 M plants per region (72 MB of instance data)
            float dens = Math.Min(density, Math.Max(1f, 1_500_000f / Math.Max(n, 1)));
            var xyz = new float[n * 3];
            for (int i = 0; i < n; i++) { xyz[i * 3] = f[i * 10]; xyz[i * 3 + 1] = f[i * 10 + 1]; xyz[i * 3 + 2] = f[i * 10 + 2]; }
            var extras = GrassDensify.Generate(xyz, n, new GrassDensify.Options { Density = dens }, meshOf, kinds);

            var lists = new List<GpuInst>[meshCount];
            for (int m = 0; m < meshCount; m++) lists[m] = new List<GpuInst>();
            GpuInst Read(int i)
            {
                int o = i * 10;
                var rot = new Quaternion(f[o + 3], f[o + 4], f[o + 5], f[o + 6]);
                float len = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                if (len < 1e-6f) rot = Quaternion.identity;
                else if (Math.Abs(len - 1) > 1e-3f) { float k = 1 / (float)Math.Sqrt(len); rot = new Quaternion(rot.x * k, rot.y * k, rot.z * k, rot.w * k); }
                return new GpuInst
                {
                    pos = new Vector3(f[o], f[o + 1], f[o + 2]), rot = rot, width = f[o + 7], height = f[o + 8],
                    color = BitConverter.ToUInt32(bytes, i * G3VegetationData.InstanceStride + 36),
                };
            }
            bool Thinned(int mesh, float x, float z)
            {
                if (canopy == null) return false;
                var k = kinds[mesh];
                if (k != GrassDensify.Kind.Grass && k != GrassDensify.Kind.Dry) return false;
                return !ScatterCanopy.KeepGrass(canopy.Cover(x, z), keepUnder, x, z);
            }
            for (int i = 0; i < n; i++) { if (!Thinned(meshOf[i], f[i * 10], f[i * 10 + 2])) lists[meshOf[i]].Add(Read(i)); }
            foreach (var e in extras)
            {
                if (Thinned(meshOf[e.Source], e.X, e.Z)) continue;
                var g = Read(e.Source);
                // extra yaw around world up on top of the source's rotation
                float h = e.Yaw * 0.5f;
                var yaw = new Quaternion(0, (float)Math.Sin(h), 0, (float)Math.Cos(h));
                g.rot = yaw * g.rot;
                g.pos = new Vector3(e.X, e.Y, e.Z);
                g.width *= e.Scale; g.height *= e.Scale;
                g.color = Brighten(g.color, e.Bright);
                g.extra = 1;
                lists[meshOf[e.Source]].Add(g);
            }
            g3Count = n;
            extraCount = extras.Count;
            var result = new GpuInst[meshCount][];
            for (int m = 0; m < meshCount; m++) result[m] = lists[m].ToArray();
            return result;
        }

        /// <summary>0xAARRGGBB with rgb x k (clamped), alpha kept.</summary>
        static uint Brighten(uint c, float k)
        {
            uint Ch(int sh) => (uint)Math.Min(255, Math.Max(0, (int)Math.Round(((c >> sh) & 255) * k))) << sh;
            return (c & 0xFF000000u) | Ch(16) | Ch(8) | Ch(0);
        }

        // ------------------------------------------------------------------ GPU (buffers: Prepare only)

        static void Upload(State st, GpuInst[][] perMesh)
        {
            st.draws = new List<MeshDraw>();
            for (int m = 0; m < perMesh.Length; m++)
            {
                var me = st.data.meshes[m];
                var arr = perMesh[m];
                if (arr == null || arr.Length == 0 || me.mesh == null || me.material == null) continue;
                int sub = me.mesh.subMeshCount;
                if (sub <= 0) continue;
                var d = new MeshDraw
                {
                    mesh = me.mesh, count = arr.Length, subMeshes = sub, viewRange = Mathf.Max(1, me.viewRange), maxHeight = me.maxHeight,
                    radius = Mathf.Max(0.5f, me.mesh.bounds.extents.magnitude + me.mesh.bounds.center.magnitude),
                    top = Mathf.Max(0, me.mesh.bounds.max.y),
                    material = MakeMaterial(G3WorldRemaster.Resolve(me.material)),
                };
                d.instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, arr.Length, Stride) { name = "G3GrassV2 instances" };
                d.instances.SetData(arr);   // a fresh buffer: no frame in flight reads it yet
                d.argsTemplate = new uint[sub * 5 + 1];
                for (int s = 0; s < sub; s++)
                {
                    d.argsTemplate[s * 5] = me.mesh.GetIndexCount(s);
                    d.argsTemplate[s * 5 + 1] = 0;
                    d.argsTemplate[s * 5 + 2] = me.mesh.GetIndexStart(s);
                    d.argsTemplate[s * 5 + 3] = unchecked((uint)me.mesh.GetBaseVertex(s));
                    d.argsTemplate[s * 5 + 4] = 0;
                }
                st.draws.Add(d);
            }
            Debug.Log($"[G3GrassV2] {st.data.name}: {st.g3Count} G3 plants + {st.extraCount} copies in {st.draws.Count} indirect draws");
            G3GpuDiag.Event($"grass upload {st.data.name} {st.draws.Count} draws");
        }

        static CamBuffers MakeCamBuffers(MeshDraw d, int frame)
        {
            int n = d.argsTemplate.Length;
            var cb = new CamBuffers
            {
                visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, d.count), sizeof(uint)) { name = "G3GrassV2 visible" },
                // compute-written raw copy (an IndirectArguments buffer bound as a UAV did not take the writes on D3D12, scatter)
                argsRW = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, n, 4) { name = "G3GrassV2 argsRW" },
                args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.CopyDestination, n, 4) { name = "G3GrassV2 args" },
                props = new MaterialPropertyBlock(),
                lastFrame = frame,
            };
            cb.argsRW.SetData(d.argsTemplate);   // fresh buffers
            cb.args.SetData(d.argsTemplate);     // instance count 0 until the first cull
            cb.props.SetBuffer(GrassInstancesId, d.instances);
            cb.props.SetBuffer(GrassVisibleId, cb.visible);
            G3GpuDiag.Count("grass.cambuf");
            return cb;
        }

        static Material MakeMaterial(Material src)
        {
            var m = new Material(_shader) { name = src.name + " (grass v2)", hideFlags = HideFlags.DontSave };
            foreach (var p in new[] { "_BaseMap" })
                if (src.HasProperty(p)) { m.SetTexture(p, src.GetTexture(p)); m.SetTextureScale(p, src.GetTextureScale(p)); m.SetTextureOffset(p, src.GetTextureOffset(p)); }
            foreach (var p in new[] { "_Cutoff", "_Smoothness", "_InstColorStrength", "_InstColorScale", "_NormalUp", "_MipAlphaScale", "_G3WindScale", "_FoliageAO", "_FoliageAOHeight", "_Cull" })
                if (src.HasProperty(p) && m.HasProperty(p)) m.SetFloat(p, src.GetFloat(p));
            if (src.HasProperty("_BaseColor")) m.SetColor("_BaseColor", src.GetColor("_BaseColor"));
            m.renderQueue = src.renderQueue;
            return m;
        }


        static readonly global::Unity.Profiling.ProfilerMarker PmDraw = new global::Unity.Profiling.ProfilerMarker("G3GrassV2.Draw");
        static void Draw(G3VegetationRenderer r, State st, Camera cam, ScriptableRenderContext ctx)
        {
            using var _pm = PmDraw.Auto();   // snow3 perf
            var c = WorldRemasterLook.Current;
            GeometryUtility.CalculateFrustumPlanes(cam, _planes);
            for (int i = 0; i < 6; i++) _planeVecs[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
            if (!GeometryUtility.TestPlanesAABB(_planes, st.bounds)) return;   // whole region off screen: no dispatches, no draws
            var camPos = cam.transform.position;
            float scale = c.v2GrassDistance * G3VegetationRenderer.OptionViewScale * r.viewDistanceScale;
            var rp = new RenderParams(st.draws.Count > 0 ? st.draws[0].material : null)
            {
                camera = cam,
                layer = r.gameObject.layer,
                receiveShadows = r.receiveShadows,
                lightProbeUsage = LightProbeUsage.BlendProbes,
                motionVectorMode = G3WorldV2.PlantMotionMode,   // remaster TAA: wind motion vectors (11.7)
                worldBounds = st.bounds,
            };
            var id = cam; int frame = Time.frameCount;
            CommandBuffer cmd = null;
            foreach (var d in st.draws)
            {
                float end = d.viewRange * scale;
                if (st.bounds.SqrDistance(camPos) > end * end) continue;
                if (!d.cams.TryGetValue(id, out var cb)) continue;
                cb.lastFrame = frame;
                if (cmd == null)
                {
                    cmd = CommandBufferPool.Get("G3GrassV2.Cull");
                    G3GpuDiag.Begin(cmd, "G3GrassV2.Cull");
                    cmd.SetComputeVectorArrayParam(_cull, PlanesId, _planeVecs);
                }
                float start = end * (1 - r.fadeFraction);
                // the cull, in GPU order before this camera's draws: counter 0, cull (bounded writes), clamp, copy to args
                cmd.SetComputeIntParam(_cull, SubMeshesId, d.subMeshes);
                cmd.SetComputeIntParam(_cull, CapacityId, d.count);
                cmd.SetComputeBufferParam(_cull, _kClear, ArgsId, cb.argsRW);
                cmd.DispatchCompute(_cull, _kClear, 1, 1, 1);
                int groups = (d.count + 63) / 64;
                int gx = Math.Min(groups, 65535), gy = (groups + gx - 1) / gx;
                cmd.SetComputeIntParam(_cull, CountId, d.count);
                cmd.SetComputeIntParam(_cull, RowGroupsId, gx);
                cmd.SetComputeVectorParam(_cull, CamPosId, new Vector4(camPos.x, camPos.y, camPos.z, end));
                cmd.SetComputeVectorParam(_cull, ParamsId, new Vector4(d.radius, c.v2GrassThinStart, 0, 0));
                cmd.SetComputeBufferParam(_cull, _kCull, InstancesId, d.instances);
                cmd.SetComputeBufferParam(_cull, _kCull, VisibleId, cb.visible);
                cmd.SetComputeBufferParam(_cull, _kCull, ArgsId, cb.argsRW);
                cmd.DispatchCompute(_cull, _kCull, gx, gy, 1);
                cmd.SetComputeBufferParam(_cull, _kFinish, ArgsId, cb.argsRW);
                cmd.DispatchCompute(_cull, _kFinish, (d.subMeshes + 63) / 64, 1, 1);
                cmd.CopyBuffer(cb.argsRW, cb.args);
                cb.props.SetVector(FadeId, new Vector4(start, 1f / Mathf.Max(0.01f, end - start), end, d.top));
                rp.material = d.material;
                rp.matProps = cb.props;
                rp.shadowCastingMode = c.v2GrassShadows && G3VegetationRenderer.OptionShadows && d.maxHeight >= r.shadowMinHeight
                    ? ShadowCastingMode.On : ShadowCastingMode.Off;
                Graphics.RenderMeshIndirect(rp, d.mesh, cb.args, d.subMeshes, 0);   // one command per sub-mesh
                G3GpuDiag.Count("grass.cull");
            }
            if (cmd != null)
            {
                G3GpuDiag.End(cmd, "G3GrassV2.Cull");
                ctx.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        static void ReleaseAllIfDensityChanged()
        {
            var keys = new List<G3VegetationRenderer>();
            foreach (var kv in _states)
                if (Math.Abs(kv.Value.density - WorldRemasterLook.Current.v2GrassDensity) > 1e-3f) keys.Add(kv.Key);
            foreach (var k in keys)
            {
                var st = _states[k];
                Release(st);
                if (st.next != null) Release(st.next);
                _states.Remove(k);
            }
        }

        static void Release(CamBuffers cb)
        {
            // dx12-3: later (G3GpuSafe): a draw queued this frame or the frame in flight may still name them
            G3GpuSafe.Release(cb.visible); G3GpuSafe.Release(cb.argsRW); G3GpuSafe.Release(cb.args);
        }

        static void Release(State st)
        {
            if (st?.draws == null) return;
            G3GpuDiag.Event($"grass release {st.data?.name} {st.draws.Count} draws");
            foreach (var d in st.draws)
            {
                foreach (var cb in d.cams.Values) Release(cb);
                d.cams.Clear();
                G3GpuSafe.Release(d.instances);
                if (d.material != null) G3GpuSafe.Destroy(d.material);
            }
            st.draws = null;
        }

        static void ReleaseAll()
        {
            foreach (var st in _states.Values)
            {
                Release(st);
                if (st.next != null) Release(st.next);
            }
            _states.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            ReleaseAll();
            _enabled = false;
            _cull = null;
            _shader = null;
            Application.quitting -= ReleaseAll;
            Application.quitting += ReleaseAll;
        }
    }
}
