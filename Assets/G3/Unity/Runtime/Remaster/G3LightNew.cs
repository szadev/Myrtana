using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// lighting-new (Docs/remaster-world.md section 21): runtime of the "New" value of the Modern lighting row (Options ->
    /// Remaster, "Современное освещение": Выкл / Мягкое / Новое). Three pieces, maths in <see cref="WorldLightNew"/>:
    /// <list type="bullet">
    /// <item>Sky light: the G3SkyReflection cube (G3's own sky, day colours, sun, v2 clouds) is read back at 16² per face every
    /// 0.5 s and integrated into an open-sky irradiance ambient cube (sun texels capped) for the light-ratio pass, which
    /// replaces G3's flat fill by it × the traced and screen-space visibility + the bounce.</item>
    /// <item>Auto-exposure: a histogram of the HDR frame after the transparents (G3RemasterExposure.compute, 1/4 res,
    /// centre weighted), percentile mean -> target EV, eye adaptation into a persistent 1×1 texture.</item>
    /// <item>Grade: the remaster grade pass multiplies by that exposure, applies the per-region / time-of-day white balance,
    /// the night (Purkinje) shift, and the filmic tonemapper (Khronos PBR Neutral) instead of the v2 shoulder.</item>
    /// </list>
    /// Nothing runs, nothing is allocated unless the row is on "New" (Lighting part on, v2).
    /// </summary>
    public static class G3LightNew
    {
        static readonly int NewLightId = Shader.PropertyToID("_G3NewLight");
        static readonly int NewLight2Id = Shader.PropertyToID("_G3NewLight2");
        static readonly int NewLight3Id = Shader.PropertyToID("_G3NewLight3");
        static readonly int NewSkyId = Shader.PropertyToID("_G3NewSky");
        static readonly int GradeId = Shader.PropertyToID("_G3NewGrade");
        static readonly int WbId = Shader.PropertyToID("_G3NewWb");
        static readonly int ExposureTexId = Shader.PropertyToID("_G3NewExposureTex");
        static readonly int HistId = Shader.PropertyToID("_ExpHist");
        static readonly int StateId = Shader.PropertyToID("_ExpState");
        static readonly int SourceId = Shader.PropertyToID("_ExpSource");
        static readonly int P1 = Shader.PropertyToID("_ExpParams"), P2 = Shader.PropertyToID("_ExpParams2"),
            P3 = Shader.PropertyToID("_ExpParams3"), P4 = Shader.PropertyToID("_ExpParams4");

        const int SkyFace = 16;   // texels per face edge read back from the sky cube

        static ComputeShader _cs;
        static int _kClear, _kBuild, _kAdapt;
        static GraphicsBuffer _hist;
        static RenderTexture _state;
        static EyePass _pass;
        static bool _reset = true;
        static float _lastFrameTime = -100;
        static Vector4 _exposure = new Vector4(1, 0, 0, 0);
        static bool _expPending;
        static int _expFrame;

        static readonly Vector4[] _sky = new Vector4[6];
        static readonly float[][] _faces = new float[6][];
        static int _facesPending, _facesGot;
        static float _nextSky;
        static bool _skyValid;
        static bool _skyBusy;

        static string _region = "Myrtana";
        static float _varant, _nordmar;   // fix-239: region shares of the weather mix (the grade)
        static float _sunrise = 6f, _sunset = 22f;
        static float Twilight => WorldLightNew.Twilight(_hour, _isDay, _fade, _sunrise, _sunset);
        static float _hour = 12, _fade = 1, _overcast;
        static bool _isDay = true, _envHooked;
        static float _nightW;
        static readonly int ToneId = Shader.PropertyToID("_G3NewTone");

        /// <summary>True when the New lighting is applied this frame (row on New, light pass with GI running).</summary>
        public static bool Active => WorldGi.New && G3WorldV2Light.Active && G3WorldGi.Active;

        /// <summary>Last read-back eye adaptation: x exposure multiplier, y metered mean log2 luminance, z EV, w target EV.</summary>
        public static Vector4 Exposure => _exposure;

        /// <summary>True when the sky irradiance comes from the sky cube (false: G3's fill × v2NewSkyFallback).</summary>
        public static bool SkyFromCube => _skyValid;

        /// <summary>The open-sky irradiance cube now used (+x −x +y −y +z −z).</summary>
        public static Vector4[] Sky => _sky;

        /// <summary>One line for the runner.</summary>
        public static string Describe()
        {
            if (!Active) return "new lighting off";
            var c = WorldRemasterLook.Current;
            return $"new lighting: sky {(_skyValid ? "cube" : "fallback")} up {_sky[2].x:0.###}/{_sky[2].y:0.###}/{_sky[2].z:0.###} side {_sky[0].y:0.###}, " +
                   $"exposure x{_exposure.x:0.##} (EV {_exposure.z:0.##} -> {_exposure.w:0.##}, mean log2 {_exposure.y:0.##}), key {c.v2NewKey:0.###}, " +
                   $"tonemap {(c.v2NewTonemap == 1 ? "PBR Neutral" : c.v2NewTonemap == 2 ? "ACES" : "none")}, region {_region} (Varant {_varant:0.##}, Nordmar {_nordmar:0.##}) {_hour:0.#} h, twilight {Twilight:0.##}";
        }

        static void HookEnv(bool on)
        {
            if (on == _envHooked) return;
            if (on) G3Environment.Applied += OnEnvironment; else G3Environment.Applied -= OnEnvironment;
            _envHooked = on;
        }

        static void OnEnvironment(G3Environment env, G3WeatherSample s, float overcast)
        {
            _region = env.weatherState;
            // fix-239: the region from the whole weather mix ("Sunny(Varant)" ...), not the state name's prefix
            if (env.StateMix != null && env.StateMix.Count > 0) (_varant, _nordmar) = WorldLightNew.RegionShares(env.StateMix);
            else
            {
                string reg = WorldLightNew.RegionOfState(env.weatherState);
                _varant = reg == "Varant" ? 1f : 0f;
                _nordmar = reg == "Nordmar" ? 1f : 0f;
            }
            _hour = env.timeOfDay;
            _isDay = env.IsDay;
            _fade = env.LightFade;
            _sunrise = env.sunriseHour;
            _sunset = env.sunsetHour;
            _overcast = overcast;
            // grade-1: night weight eased (no pop at G3's day/night switch)
            _nightW = Mathf.MoveTowards(_nightW, WorldLightNew.Night(_isDay), Time.deltaTime * 0.5f);
        }

        // ------------------------------------------------------------------ ratio pass (G3WorldV2Light.OnBeginCamera)

        /// <summary>Sets the New uniforms on the light-ratio material (zeros = off). <paramref name="g3Cube"/>: G3's ambient cube (fallback sky).</summary>
        public static void SetRatio(Camera cam, Material ratio, Vector4[] g3Cube)
        {
            bool on = Active;
            HookEnv(on);
            var g = WorldLightNew.RatioGlobal(Twilight, _nightW);   // fix-239: twilight floor; grade-1: night floor
            if (!on) { ratio.SetVector(NewLightId, Vector4.zero); return; }
            var c = WorldRemasterLook.Current;
            bool main = cam.cameraType == CameraType.Game && (cam == Camera.main || Camera.main == null) && !G3GpuSafe.Capturing;
            if (main) UpdateSky();
            if (!_skyValid)
                for (int i = 0; i < 6; i++)
                    _sky[i] = i == 3 ? Vector4.zero : g3Cube[i] * c.v2NewSkyFallback;
            ratio.SetVector(NewLightId, new Vector4(g[0], g[1], g[2], g[3]));
            var g2 = WorldLightNew.RatioGlobal2();
            ratio.SetVector(NewLight2Id, new Vector4(g2[0], g2[1], g2[2], g2[3]));
            ratio.SetVector(NewLight3Id, new Vector4(c.v2NewSun, 0, 0, 0));
            ratio.SetVectorArray(NewSkyId, _sky);
        }

        static void UpdateSky()
        {
            var cube = G3SkyReflection.Cube;
            if (cube == null || !G3SkyReflection.Active || !SystemInfo.supportsAsyncGPUReadback) { _skyValid = false; return; }
            if (_skyBusy || Time.unscaledTime < _nextSky) return;
            int mip = 0;
            while ((cube.width >> mip) > SkyFace && mip < cube.mipmapCount - 1) mip++;
            int n = Mathf.Max(1, cube.width >> mip);
            _skyBusy = true;
            _facesPending = 6; _facesGot = 0;
            _nextSky = Time.unscaledTime + 0.5f;
            for (int f = 0; f < 6; f++)
            {
                int face = f;
                AsyncGPUReadback.Request(cube, mip, 0, n, 0, n, face, 1, TextureFormat.RGBAFloat, r =>
                {
                    if (!r.hasError)
                    {
                        var d = r.GetData<Vector4>();
                        var arr = _faces[face];
                        if (arr == null || arr.Length != n * n * 3) arr = _faces[face] = new float[n * n * 3];
                        // readback rows run bottom-up (v up); the face maths take row 0 at the top
                        for (int j = 0; j < n; j++)
                            for (int i = 0; i < n; i++)
                            {
                                var t = d[(FlipRows ? n - 1 - j : j) * n + i];
                                int k = (j * n + i) * 3;
                                arr[k] = t.x; arr[k + 1] = t.y; arr[k + 2] = t.z;
                            }
                        _facesGot++;
                    }
                    if (--_facesPending == 0)
                    {
                        _skyBusy = false;
                        if (_facesGot == 6)
                        {
                            var irr = WorldLightNew.SkyIrradiance(_faces, n);
                            for (int c = 0; c < 6; c++) _sky[c] = new Vector4(irr[c][0], irr[c][1], irr[c][2], 0);
                            _skyValid = true;
                        }
                    }
                });
            }
        }

        /// <summary>Runner switch: flip the read-back rows of the sky cube faces (orientation check, section 21).</summary>
        public static bool FlipRows = true;

        /// <summary>Runner: per-face mean luminance of the last read-back sky texels and the top/bottom row means of face +x.</summary>
        public static string SkyProbe()
        {
            var sb = new System.Text.StringBuilder();
            for (int f = 0; f < 6; f++)
            {
                var a = _faces[f];
                if (a == null) { sb.Append("- "); continue; }
                double s = 0; for (int k = 0; k < a.Length; k += 3) s += WorldLightNew.Lum(a[k], a[k + 1], a[k + 2]);
                sb.Append((s / (a.Length / 3)).ToString("0.###")).Append(' ');
            }
            var x = _faces[0];
            if (x != null)
            {
                int n = (int)Mathf.Sqrt(x.Length / 3);
                double top = 0, bot = 0;
                for (int i = 0; i < n; i++) { int k = i * 3, kb = ((n - 1) * n + i) * 3; top += WorldLightNew.Lum(x[k], x[k + 1], x[k + 2]); bot += WorldLightNew.Lum(x[kb], x[kb + 1], x[kb + 2]); }
                sb.Append($"| +x top {top / n:0.###} bottom {bot / n:0.###}");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ grade pass (G3WorldRemasterLook.OnBeginCamera)

        /// <summary>
        /// Before the grade pass of a camera: enqueues the eye adaptation (main game camera) and sets the grade's New
        /// uniforms on <paramref name="atmos"/>. Returns true when the grade pass must run (New on).
        /// </summary>
        public static bool BeginGrade(Camera cam, ScriptableRenderer renderer, Material atmos)
        {
            bool on = Active && Ensure();
            if (!on)
            {
                atmos.SetVector(GradeId, Vector4.zero);
                if (!WorldGi.New) Release();
                return false;
            }
            var c = WorldRemasterLook.Current;
            // dx12-3: the save picture uses the current exposure (no second histogram / adapt dispatch per frame)
            bool main = cam.cameraType == CameraType.Game && (cam == Camera.main || Camera.main == null) && !G3GpuSafe.Capturing;
            if (main)
            {
                _pass ??= new EyePass();
                float now = Time.unscaledTime;
                if (now - _lastFrameTime > 2f) _reset = true;
                _pass.dt = Mathf.Clamp(now - _lastFrameTime, 0, 0.5f);
                _lastFrameTime = now;
                _pass.reset = _reset;
                _reset = false;
                renderer.EnqueuePass(_pass);
                G3GpuDiag.Count("exposure");
                ReadExposure();
                LogState(now);
            }
            var wb = WorldLightNew.Grade(_varant, _nordmar, _hour, _isDay, _fade, _overcast, Twilight);
            atmos.SetVector(GradeId, new Vector4(1, c.v2NewTonemap, c.v2NewContrast, WorldLightNew.PurkinjeStrength(Twilight, _nightW)));   // fix-255: night only
            atmos.SetVector(WbId, new Vector4(wb.r, wb.g, wb.b, wb.sat));
            atmos.SetVector(ToneId, new Vector4(c.v2NewToe, c.v2NewWhite, 0, 0));
            atmos.SetTexture(ExposureTexId, _state);
            return true;
        }

        static float _nextLog = 30;

        /// <summary>
        /// dx12-2: a Player.log line every 2 minutes in the world (graphics API, threading, sky irradiance, read-back sky
        /// texels, eye adaptation, SSGI, GI): the owner's DirectX 11 and 12 sessions become comparable from the logs alone.
        /// </summary>
        static void LogState(float now)
        {
            if (now < _nextLog || Application.isEditor) return;
            _nextLog = now + 120;
            Debug.Log($"[G3Lighting] {SystemInfo.graphicsDeviceType} {SystemInfo.renderingThreadingMode}: {Describe()}; " +
                      $"sky texels {SkyProbe()}; {G3WorldSsgi.Describe()}; {G3WorldGi.Describe()}");
        }

        static bool Ensure()
        {
            if (!SystemInfo.supportsComputeShaders) return false;
            if (_cs == null)
            {
                _cs = Resources.Load<ComputeShader>("G3RemasterExposure");
                if (_cs == null) { Debug.LogWarning("[G3LightNew] compute shader G3RemasterExposure missing"); return false; }
                _kClear = _cs.FindKernel("Clear"); _kBuild = _cs.FindKernel("Build"); _kAdapt = _cs.FindKernel("Adapt");
            }
            if (_state == null)
            {
                _state = new RenderTexture(1, 1, 0, GraphicsFormat.R32G32B32A32_SFloat)
                    { name = "G3 New Exposure", enableRandomWrite = true, filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
                _state.Create();
                _hist = new GraphicsBuffer(GraphicsBuffer.Target.Structured, WorldLightNew.Bins, 4) { name = "G3 New Exposure Histogram" };
                _reset = true;
            }
            _cs.SetBuffer(_kClear, HistId, _hist);
            _cs.SetBuffer(_kBuild, HistId, _hist);
            _cs.SetBuffer(_kAdapt, HistId, _hist);
            _cs.SetTexture(_kAdapt, StateId, _state);
            return true;
        }

        static void Release()
        {
            if (_state != null) { _state.Release(); Object.Destroy(_state); _state = null; }
            _hist?.Release(); _hist = null;
            _skyValid = false;
            HookEnv(false);
        }

        static void ReadExposure()
        {
            if (_state == null || _expPending || ++_expFrame % 15 != 0 || !SystemInfo.supportsAsyncGPUReadback) return;
            _expPending = true;
            AsyncGPUReadback.Request(_state, 0, r =>
            {
                _expPending = false;
                if (r.hasError) return;
                var d = r.GetData<Vector4>();
                if (d.Length > 0) _exposure = d[0];
            });
        }

        /// <summary>Histogram + adaptation of the frame after the transparents (before the grade at AfterRenderingTransparents).</summary>
        sealed class EyePass : ScriptableRenderPass
        {
            public float dt;
            public bool reset;

            sealed class EyeData { public TextureHandle source; public int w, h; public Vector4 p1, p2, p3, p4; }

            public EyePass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents - 1;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer || _cs == null || _state == null) return;
                var c = WorldRemasterLook.Current;
                var src = res.activeColorTexture;
                var desc = rg.GetTextureDesc(src);
                using (var builder = rg.AddComputePass<EyeData>("G3 Eye Adaptation", out var d))
                {
                    const int stride = 4;
                    d.source = src; d.w = desc.width; d.h = desc.height;
                    d.p1 = new Vector4(stride, desc.width, desc.height, dt);
                    d.p2 = new Vector4(c.v2NewLowPercent, c.v2NewHighPercent, c.v2NewAdapt, Mathf.Log(c.v2NewKey, 2));
                    d.p3 = new Vector4(c.v2NewMinEv, WorldLightNew.MaxEv(_nightW), c.v2NewBiasEv, reset ? 1 : 0);   // grade-1: night cap
                    d.p4 = new Vector4(c.v2NewSpeedUp, c.v2NewSpeedDown, c.v2NewCenter, 0);
                    builder.UseTexture(src);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((EyeData x, ComputeGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        cmd.SetComputeVectorParam(_cs, P1, x.p1);
                        cmd.SetComputeVectorParam(_cs, P2, x.p2);
                        cmd.SetComputeVectorParam(_cs, P3, x.p3);
                        cmd.SetComputeVectorParam(_cs, P4, x.p4);
                        cmd.DispatchCompute(_cs, _kClear, 1, 1, 1);
                        cmd.SetComputeTextureParam(_cs, _kBuild, SourceId, x.source);
                        int sx = (x.w + 3) / 4, sy = (x.h + 3) / 4;
                        cmd.DispatchCompute(_cs, _kBuild, (sx + 15) / 16, (sy + 15) / 16, 1);
                        cmd.DispatchCompute(_cs, _kAdapt, 1, 1, 1);
                    });
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Application.quitting -= Release;
            Application.quitting += Release;
            G3Environment.Applied -= OnEnvironment;
            _envHooked = false;
            _pass = null; _state = null; _hist = null; _cs = null;
            _reset = true; _lastFrameTime = -100; _exposure = new Vector4(1, 0, 0, 0); _expPending = false;
            _skyValid = false; _skyBusy = false; _nextSky = 0; _nextLog = 30;
        }
    }
}
