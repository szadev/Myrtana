using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// Remaster GI (Docs/remaster-world.md section 16, agent remaster-gi; Lighting part, JSON <c>v2Gi</c>): real coloured
    /// bounce for a streamed open world without a bake and without ray tracing. A camera-centred radiance volume
    /// (128×64×128 voxels of <c>v2GiVoxel</c> m, toroidal) is filled from the frame each frame (screen injection), a grid of
    /// 64×32×64 ambient-cube probes traces rays through it (1/<c>v2GiCadence</c> of them per frame, temporal blend), and the
    /// light-ratio pass (<see cref="G3WorldV2Light"/>) replaces G3's flat ambient by the probes' irradiance: green under
    /// canopies, warm in sunlit valleys, orange from torches in houses and caves. Compute work runs in the ratio pass's
    /// render graph after its composite (the voxels see the frame after the ratio = multi-bounce over frames). Maths in
    /// <see cref="WorldGi"/>; G3RemasterGi.compute. Off = nothing allocated, the ratio pass as before.
    /// </summary>
    public static class G3WorldGi
    {
        static readonly int VoxId = Shader.PropertyToID("_GiVox");
        static readonly int ProbesId = Shader.PropertyToID("_GiProbes");
        static readonly int[] CubeIds =
        {
            Shader.PropertyToID("_GiCube0"), Shader.PropertyToID("_GiCube1"), Shader.PropertyToID("_GiCube2"),
            Shader.PropertyToID("_GiCube3"), Shader.PropertyToID("_GiCube4"), Shader.PropertyToID("_GiCube5"),
        };
        static readonly int[] SampleIds =
        {
            Shader.PropertyToID("_G3GiCube0"), Shader.PropertyToID("_G3GiCube1"), Shader.PropertyToID("_G3GiCube2"),
            Shader.PropertyToID("_G3GiCube3"), Shader.PropertyToID("_G3GiCube4"), Shader.PropertyToID("_G3GiCube5"),
        };
        static readonly int ConfId = Shader.PropertyToID("_GiConf");
        static readonly int SampleConfId = Shader.PropertyToID("_G3GiConf");
        static readonly int OriginId = Shader.PropertyToID("_GiOrigin");
        static readonly int OldOriginId = Shader.PropertyToID("_GiOldOrigin");
        static readonly int ParamsId = Shader.PropertyToID("_GiParams");
        static readonly int Params2Id = Shader.PropertyToID("_GiParams2");
        static readonly int Params3Id = Shader.PropertyToID("_GiParams3");
        static readonly int VpId = Shader.PropertyToID("_GiVP");
        static readonly int InvVpId = Shader.PropertyToID("_GiInvVP");
        static readonly int ScatterId = Shader.PropertyToID("_GiScatter");
        static readonly int TintId = Shader.PropertyToID("_G3GiTint");
        static readonly int ZParamsId = Shader.PropertyToID("_GiZParams");
        static readonly int ScreenId = Shader.PropertyToID("_GiScreen");
        static readonly int RotId = Shader.PropertyToID("_GiRot");
        static readonly int AmbId = Shader.PropertyToID("_GiAmb");
        static readonly int DepthId = Shader.PropertyToID("_GiDepth");
        static readonly int ColorId = Shader.PropertyToID("_GiColor");
        static readonly int RatioId = Shader.PropertyToID("_GiRatio");
        static readonly int MapId = Shader.PropertyToID("_G3SkyOccMap");
        static readonly int MapWindowId = Shader.PropertyToID("_G3SkyOccWindow");
        static readonly int GiParamsId = Shader.PropertyToID("_G3GiParams");
        static readonly int GiWindowId = Shader.PropertyToID("_G3GiWindow");
        static readonly int DbgVoxId = Shader.PropertyToID("_G3GiVox");
        static readonly int DbgOriginId = Shader.PropertyToID("_G3GiOrigin");
        static readonly int ModernId = Shader.PropertyToID("_G3GiModern");
        static readonly int Modern2Id = Shader.PropertyToID("_G3GiModern2");
        static readonly int ModernSkyId = Shader.PropertyToID("_G3GiModernSky");

        /// <summary>remaster-gi2: PlayerPrefs key of the "Modern lighting" row (default 0 = off).</summary>
        public const string ModernPrefKey = "G3.Remaster.ModernLighting";
        /// <summary>remaster-gi2: own environment override of the row (1/0); the shared remaster variables do not turn it on.</summary>
        public const string ModernEnvVar = "G3_REMASTER_MODERN";
        static int? _modernOption;

        static bool _on;
        static ComputeShader _cs;
        static int _kClearVox, _kClearProbes, _kScrollVox, _kScrollProbes, _kInject, _kScatter, _kUpdate, _kDilate;
        static RenderTexture _vox;
        static readonly RenderTexture[] _cubes = new RenderTexture[6];
        static RenderTexture _conf;
        static GraphicsBuffer _probes;
        static Vector3Int _origin, _oldOrigin;
        static bool _hasOrigin, _needClear, _needScroll;
        static int _frame;
        static Camera _cam;
        static System.Random _rng = new System.Random(7);
        static Texture _dummyMap;

        /// <summary>Flip of clip y to texture v in the injection (runner switch; D3D: the projection carries the flip).</summary>
        public static bool FlipY = SystemInfo.graphicsUVStartsAtTop;

        /// <summary>True while the GI runs.</summary>
        public static bool Active => _on;

        /// <summary>
        /// remaster-gi2 (Docs/remaster-world.md section 17): the stored "Modern lighting" row (Options -> Remaster, under
        /// Lighting; default off): G3's flat fill lowered, the GI supplies the light as traced sky visibility + coloured
        /// bounce, an exposure matched per frame keeps the mean brightness. Env G3_REMASTER_MODERN first.
        /// </summary>
        public static bool ModernOption => ModernMode != 0;

        /// <summary>
        /// lighting-new (section 21): the row's stored value, 0 off / 1 "Мягкое" (Soft, sections 17 + 19) / 2 "Новое" (New,
        /// <see cref="G3LightNew"/>). PlayerPrefs int (old 0/1 values keep their meaning); env G3_REMASTER_MODERN first
        /// (0/1/off/on/soft, 2/new).
        /// </summary>
        public static int ModernMode
        {
            get
            {
                if (_modernOption == null)
                {
                    G3WorldRemaster.EnsureMigrated(); // the demo preset (rebrand) writes this key first
                    int stored = 0;
                    try { stored = Mathf.Clamp(PlayerPrefs.GetInt(ModernPrefKey, 0), 0, 2); } catch (System.Exception) { }
                    _modernOption = ParseMode(System.Environment.GetEnvironmentVariable(ModernEnvVar)) ?? stored;
                    WorldGi.ModernMode = _modernOption.Value;
                }
                return _modernOption.Value;
            }
        }

        /// <summary>Env value of the row: 2/new/neu -> 2, soft/1/on/true -> 1, 0/off/false -> 0, anything else null.</summary>
        public static int? ParseMode(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim().ToLowerInvariant();
            if (v == "2" || v == "new" || v == "neu") return 2;
            if (v == "soft") return 1;
            var f = WorldRemasterSwitch.ParseFlag(v);
            return f == null ? (int?)null : f.Value ? 1 : 0;
        }

        /// <summary>True while Modern lighting is applied (the row on and the light pass with the GI running).</summary>
        public static bool ModernActive => _on && WorldGi.Modern && G3WorldV2Light.Active;

        /// <summary>The row / runner A|B: stores and re-applies the light pass.</summary>
        public static void SetModern(bool on, bool persist = true) => SetModernMode(on ? 1 : 0, persist);

        /// <summary>lighting-new: the three-way row / runner A|B|C|D (0 off, 1 Soft, 2 New): stores and re-applies the light pass.</summary>
        public static void SetModernMode(int mode, bool persist = true)
        {
            mode = Mathf.Clamp(mode, 0, 2);
            _modernOption = mode;
            WorldGi.ModernMode = mode;
            if (persist) { PlayerPrefs.SetInt(ModernPrefKey, mode); PlayerPrefs.Save(); }
            bool light = (G3WorldRemaster.Parts & WorldParts.Lighting) != 0 && WorldRemasterLook.Current.v2;
            G3WorldV2Light.Apply(light);
            Debug.Log($"[Remaster] modern lighting {(mode == 2 ? "new" : mode == 1 ? "soft" : "off")}: {Describe()}");
        }

        /// <summary>Allocates / frees the volumes (G3WorldV2Light.Apply).</summary>
        public static void Apply(bool on)
        {
            WorldGi.ModernMode = ModernMode;
            on &= WorldGi.Wanted && SystemInfo.supportsComputeShaders && SystemInfo.supports3DRenderTextures && Application.isPlaying;
            if (on && _cs == null)
            {
                _cs = Resources.Load<ComputeShader>("G3RemasterGi");
                if (_cs == null) { Debug.LogWarning("[G3WorldGi] compute shader G3RemasterGi missing"); on = false; }
                else
                {
                    _kClearVox = _cs.FindKernel("ClearVox"); _kClearProbes = _cs.FindKernel("ClearProbes");
                    _kScrollVox = _cs.FindKernel("ScrollVox"); _kScrollProbes = _cs.FindKernel("ScrollProbes");
                    _kInject = _cs.FindKernel("Inject"); _kScatter = _cs.FindKernel("InjectScatter"); _kUpdate = _cs.FindKernel("UpdateProbes"); _kDilate = _cs.FindKernel("Dilate");
                }
            }
            if (!on) { Release(); _on = false; return; }
            if (_vox == null)
            {
                _vox = Make3D("G3 GI Radiance", WorldGi.VoxX, WorldGi.VoxY, WorldGi.VoxZ, FilterMode.Point);
                for (int f = 0; f < 6; f++) _cubes[f] = Make3D("G3 GI Probes " + f, WorldGi.ProbeX, WorldGi.ProbeY, WorldGi.ProbeZ, FilterMode.Bilinear);
                _probes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, WorldGi.ProbeX * WorldGi.ProbeY * WorldGi.ProbeZ * 7, 16) { name = "G3 GI Probe State" };
                _conf = Make3D("G3 GI Probe Weight", WorldGi.ProbeX, WorldGi.ProbeY, WorldGi.ProbeZ, FilterMode.Bilinear, GraphicsFormat.R16_SFloat);
                _needClear = true;
                _hasOrigin = false;
            }
            BindPersistent();
            _on = true;
        }

        static RenderTexture Make3D(string name, int x, int y, int z, FilterMode filter, GraphicsFormat format = GraphicsFormat.R16G16B16A16_SFloat)
        {
            var rt = new RenderTexture(x, y, 0, format)
            {
                name = name, dimension = TextureDimension.Tex3D, volumeDepth = z, enableRandomWrite = true,
                filterMode = filter, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.DontSave,
            };
            rt.Create();
            return rt;
        }

        static void BindPersistent()
        {
            foreach (int k in new[] { _kClearVox, _kScrollVox, _kInject, _kScatter, _kUpdate })
                _cs.SetTexture(k, VoxId, _vox);
            foreach (int k in new[] { _kClearProbes, _kScrollProbes, _kUpdate, _kDilate })
            {
                _cs.SetBuffer(k, ProbesId, _probes);
                for (int f = 0; f < 6; f++) _cs.SetTexture(k, CubeIds[f], _cubes[f]);
                _cs.SetTexture(k, ConfId, _conf);
            }
        }

        static void Release()
        {
            if (_vox != null) { _vox.Release(); Object.Destroy(_vox); _vox = null; }
            for (int f = 0; f < 6; f++) if (_cubes[f] != null) { _cubes[f].Release(); Object.Destroy(_cubes[f]); _cubes[f] = null; }
            if (_conf != null) { _conf.Release(); Object.Destroy(_conf); _conf = null; }
            _probes?.Release(); _probes = null;
            _hasOrigin = false;
            _params.valid = false;
        }

        /// <summary>One line for the runner.</summary>
        public static string Describe()
        {
            if (!_on) return "gi off";
            var c = WorldRemasterLook.Current;
            string modern = WorldGi.New ? "new lighting, " : WorldGi.Modern ? $"modern lighting (fill x{c.v2ModernAmbient:0.##}, occlusion {c.v2ModernOcclusion:0.##}, gain {c.v2ModernGain:0.##}, exposure {c.v2ModernMinExposure:0.##}..{c.v2ModernMaxExposure:0.##}), " : "";
            return $"gi {c.v2GiStrength:0.##} {modern}(voxel {c.v2GiVoxel:0.##} m, {WorldGi.VoxX}x{WorldGi.VoxY}x{WorldGi.VoxZ}, probes {WorldGi.ProbeX}x{WorldGi.ProbeY}x{WorldGi.ProbeZ} / {c.v2GiCadence} frames, {c.v2GiRays} rays, origin {_origin})";
        }

        /// <summary>
        /// Per camera, before its render graph: the main game camera moves the window (scroll / clear pending) and sets
        /// the compute parameters; every camera gets the probe sampling globals on the ratio material (weight 0 for the
        /// others: scene view and reflection cameras keep the old estimate).
        /// </summary>
        public static bool BeginCamera(Camera cam, Material ratio, Vector4[] ambCube, Texture skyOccMap, Vector4 skyOccWindow)
        {
            var c = WorldRemasterLook.Current;
            // dx12-3: a capture of the main camera (save picture) neither scrolls nor dispatches the GI window
            bool main = _on && cam.cameraType == CameraType.Game && (cam == Camera.main || Camera.main == null) && !G3GpuSafe.Capturing;
            if (!main)
            {
                ratio.SetVector(GiParamsId, Vector4.zero);
                ratio.SetVector(ModernId, Vector4.zero);
                return false;
            }
            G3GpuDiag.Count("gi");
            _cam = cam;
            float voxel = c.v2GiVoxel;
            var p = cam.transform.position;
            var o = WorldGi.Origin(p.x, p.y, p.z, voxel);
            var no = new Vector3Int(o.x, o.y, o.z);
            if (!_hasOrigin) { _needClear = true; _origin = no; _hasOrigin = true; }
            else if (no != _origin)
            {
                // a jump (teleport, > half the window) clears everything; a walk scrolls the slabs that entered
                var d = no - _origin;
                if (Mathf.Abs(d.x) > WorldGi.VoxX / 2 || Mathf.Abs(d.y) > WorldGi.VoxY / 2 || Mathf.Abs(d.z) > WorldGi.VoxZ / 2) _needClear = true;
                else { _oldOrigin = _origin; _needScroll = true; }
                _origin = no;
            }
            _frame++;
            // dx12-1: this frame's compute parameters are kept and recorded into the GI pass's command buffer (Record), not
            // set on the shared ComputeShader object from the camera callback (with split graphics jobs the dispatches run
            // later on the render thread; every parameter now travels with its dispatches)
            var gp = _params;
            gp.p1 = new Vector4(voxel, c.v2GiInject, _frame, c.v2GiHysteresis);
            gp.p2 = new Vector4(c.v2GiRays, c.v2GiRayLength, c.v2GiCadence, c.v2GiUnseenAlbedo);
            gp.p3 = new Vector4(WorldGi.Gain, 0, 0, 0);
            var vp = GL.GetGPUProjectionMatrix(cam.nonJitteredProjectionMatrix, true) * cam.worldToCameraMatrix;
            gp.vp = vp;
            gp.invVp = vp.inverse;
            float n = cam.nearClipPlane, f = cam.farClipPlane;
            bool rev = SystemInfo.usesReversedZBuffer;
            float zx = rev ? -1 + f / n : 1 - f / n, zy = rev ? 1 : f / n;
            gp.z = new Vector4(zx, zy, zx / f, zy / f);
            gp.screen = new Vector4(rev ? 1 : 0, FlipY ? 1 : 0, 0, 0);
            var q = Quaternion.Euler((float)_rng.NextDouble() * 360f, (float)_rng.NextDouble() * 360f, (float)_rng.NextDouble() * 360f);
            gp.rot = Matrix4x4.Rotate(q);
            for (int i = 0; i < 6; i++) gp.amb[i] = i < ambCube.Length ? ambCube[i] : Vector4.zero;
            if (_dummyMap == null) _dummyMap = Texture2D.blackTexture;
            gp.map = skyOccMap != null ? skyOccMap : _dummyMap;
            _cs.SetTexture(_kUpdate, MapId, gp.map);   // a persistent texture (like BindPersistent's), not per-frame state
            gp.mapWindow = skyOccMap != null ? skyOccWindow : Vector4.zero;
            gp.valid = true;

            var g = WorldGi.GiGlobal(_frame);
            if (_needClear) g[0] = 0;   // the probes are cleared after this frame's ratio pass: not valid yet
            ratio.SetVector(GiParamsId, new Vector4(g[0], g[1], g[2], g[3]));
            ratio.SetFloat(TintId, WorldGi.Tint);
            var m = WorldGi.ModernGlobal();
            if (_needClear) m[0] = 0;
            ratio.SetVector(ModernId, new Vector4(m[0], m[1], m[2], m[3]));
            ratio.SetVector(Modern2Id, new Vector4(m[4], 0, 0, 0));
            ratio.SetFloat(ModernSkyId, m[0] > 0 ? c.v2ModernSky : 0);
            float sp = voxel * 2;
            ratio.SetVector(GiWindowId, new Vector4(_origin.x * voxel, _origin.y * voxel, _origin.z * voxel, sp));
            for (int i = 0; i < 6; i++) ratio.SetTexture(SampleIds[i], _cubes[i]);
            ratio.SetTexture(SampleConfId, _conf);
            ratio.SetTexture(DbgVoxId, _vox);
            ratio.SetVector(DbgOriginId, new Vector4(_origin.x, _origin.y, _origin.z, 0));
            return true;
        }

        /// <summary>dx12-1: the compute parameters of the main camera's frame (BeginCamera), recorded by Record.</summary>
        sealed class GiParams
        {
            public Vector4 p1, p2, p3, z, screen, mapWindow;
            public Matrix4x4 vp, invVp, rot;
            public readonly Vector4[] amb = new Vector4[6];
            public Texture map;
            public bool valid;

            public void CopyTo(GiParams o)
            {
                o.p1 = p1; o.p2 = p2; o.p3 = p3; o.z = z; o.screen = screen; o.mapWindow = mapWindow;
                o.vp = vp; o.invVp = invVp; o.rot = rot; o.map = map; o.valid = valid;
                System.Array.Copy(amb, o.amb, 6);
            }
        }
        static readonly GiParams _params = new GiParams();

        sealed class GiData
        {
            public TextureHandle depth, color, ratio;
            public bool clear, scroll;
            public int[] origin, oldOrigin;
            public int w, h;
            public readonly GiParams p = new GiParams();
        }

        /// <summary>The compute passes (after the ratio composite): clear / scroll, injection, probe update, dilate.</summary>
        public static void Record(RenderGraph rg, UniversalResourceData res, TextureHandle color4, TextureHandle ratio)
        {
            if (!_on || _cs == null || !_params.valid || !res.cameraDepthTexture.IsValid() || !color4.IsValid() || !ratio.IsValid()) return;
            using (var builder = rg.AddComputePass<GiData>("G3 GI", out var d))
            {
                d.depth = res.cameraDepthTexture; d.color = color4; d.ratio = ratio;
                var cd = rg.GetTextureDesc(color4); d.w = cd.width; d.h = cd.height;
                d.clear = _needClear; d.scroll = _needScroll && !_needClear;
                _params.CopyTo(d.p);
                d.origin = new[] { _origin.x, _origin.y, _origin.z, 0 };
                d.oldOrigin = new[] { _oldOrigin.x, _oldOrigin.y, _oldOrigin.z, 0 };
                _needClear = _needScroll = false;
                builder.UseTexture(d.depth);
                builder.UseTexture(d.color);
                builder.UseTexture(d.ratio);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((GiData x, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    int vx = WorldGi.VoxX / 4, vy = WorldGi.VoxY / 4, vz = WorldGi.VoxZ / 4;
                    int px = WorldGi.ProbeX / 4, py = WorldGi.ProbeY / 4, pz = WorldGi.ProbeZ / 4;
                    cmd.SetComputeIntParams(_cs, OriginId, x.origin);
                    var p = x.p;
                    cmd.SetComputeVectorParam(_cs, ParamsId, p.p1);
                    cmd.SetComputeVectorParam(_cs, Params2Id, p.p2);
                    cmd.SetComputeVectorParam(_cs, Params3Id, p.p3);
                    cmd.SetComputeMatrixParam(_cs, VpId, p.vp);
                    cmd.SetComputeMatrixParam(_cs, InvVpId, p.invVp);
                    cmd.SetComputeVectorParam(_cs, ZParamsId, p.z);
                    cmd.SetComputeVectorParam(_cs, ScreenId, p.screen);
                    cmd.SetComputeMatrixParam(_cs, RotId, p.rot);
                    cmd.SetComputeVectorArrayParam(_cs, AmbId, p.amb);
                    cmd.SetComputeVectorParam(_cs, MapWindowId, p.mapWindow);
                    if (x.clear)
                    {
                        cmd.DispatchCompute(_cs, _kClearVox, vx, vy, vz);
                        cmd.DispatchCompute(_cs, _kClearProbes, px, py, pz);
                    }
                    else if (x.scroll)
                    {
                        cmd.SetComputeIntParams(_cs, OldOriginId, x.oldOrigin);
                        cmd.DispatchCompute(_cs, _kScrollVox, vx, vy, vz);
                        cmd.DispatchCompute(_cs, _kScrollProbes, px, py, pz);
                    }
                    cmd.SetComputeTextureParam(_cs, _kInject, DepthId, x.depth);
                    cmd.SetComputeTextureParam(_cs, _kInject, ColorId, x.color);
                    cmd.SetComputeTextureParam(_cs, _kInject, RatioId, x.ratio);
                    cmd.DispatchCompute(_cs, _kInject, vx, vy, vz);
                    cmd.SetComputeTextureParam(_cs, _kScatter, DepthId, x.depth);
                    cmd.SetComputeTextureParam(_cs, _kScatter, ColorId, x.color);
                    cmd.SetComputeTextureParam(_cs, _kScatter, RatioId, x.ratio);
                    cmd.SetComputeVectorParam(_cs, ScatterId, new Vector4(x.w, x.h, 0, 0));
                    cmd.DispatchCompute(_cs, _kScatter, (x.w + 7) / 8, (x.h + 7) / 8, 1);
                    cmd.DispatchCompute(_cs, _kUpdate, px, py, pz);
                    cmd.DispatchCompute(_cs, _kDilate, px, py, pz);
                });
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Application.quitting -= Release;
            Application.quitting += Release;
            _on = false;
            _hasOrigin = false;
            _needClear = _needScroll = false;
            _modernOption = null;
            _params.valid = false;
        }
    }
}
