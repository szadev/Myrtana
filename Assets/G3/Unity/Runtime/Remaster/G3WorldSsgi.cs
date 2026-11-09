using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// remaster-ssgi (Docs/remaster-world.md section 19): per-pixel screen-space indirect light of Modern lighting
    /// (visibility bitmask, Therrien et al. 2023; maths in <see cref="WorldSsgi"/>, shader Hidden/G3/Remaster/Ssgi).
    /// Recorded by the light-ratio pass before its ratio: trace (half res) -> temporal (persistent ping-pong history,
    /// camera reprojection) -> depth-aware filter; the ratio pass reads the result as <c>_G3SsgiTex</c>. Main game camera
    /// only; other cameras and Modern lighting off = nothing runs, nothing allocated.
    /// </summary>
    public static class G3WorldSsgi
    {
        static readonly int SsgiId = Shader.PropertyToID("_G3Ssgi");
        static readonly int Ssgi2Id = Shader.PropertyToID("_G3Ssgi2");
        static readonly int Ssgi3Id = Shader.PropertyToID("_G3Ssgi3");
        static readonly int SizeId = Shader.PropertyToID("_G3SsgiSize");
        static readonly int ColorId = Shader.PropertyToID("_G3SsgiColor");
        static readonly int HistId = Shader.PropertyToID("_G3SsgiHist");
        static readonly int HistDepthId = Shader.PropertyToID("_G3SsgiHistDepth");
        static readonly int DepthNowId = Shader.PropertyToID("_G3SsgiDepthNow");
        static readonly int PrevVpId = Shader.PropertyToID("_G3SsgiPrevVP");
        static readonly int TemporalId = Shader.PropertyToID("_G3SsgiTemporal");
        /// <summary>Runner debug (global): 1 visibility, 2 indirect x gain, 3 history age / 16 (shown through _G3LightRatioDebug 1).</summary>
        public static readonly int DebugId = Shader.PropertyToID("_G3SsgiDebug");
        const int PassTrace = 0, PassTemporal = 1, PassFilter = 2;

        static Material _mat;
        static readonly RenderTexture[] _hist = new RenderTexture[2], _histDepth = new RenderTexture[2];
        static readonly RTHandle[] _histH = new RTHandle[2], _histDepthH = new RTHandle[2];
        static int _idx;
        static bool _histValid;
        static Matrix4x4 _prevVp, _curVp;
        static bool _hasPrev;
        static float _lastTime;
        static int _frame;
        static float _proj11 = 1;
        static bool _active;

        /// <summary>True when the pass ran for the main camera this frame.</summary>
        public static bool Active => _active;

        /// <summary>One line for the runner.</summary>
        public static string Describe()
        {
            if (!WorldSsgi.Wanted) return "ssgi off";
            var c = WorldRemasterLook.Current;
            return $"ssgi {(_active ? "on" : "idle")} (radius {c.v2SsgiRadius:0.##} m/{c.v2SsgiMaxPixels:0} px, thickness {c.v2SsgiThickness:0.##} m, {c.v2SsgiSlices}x2x{c.v2SsgiSteps}, ao {c.v2SsgiAo:0.##}, gain {c.v2SsgiGain:0.##}, history {c.v2SsgiHistory:0.##}, ~{WorldSsgi.CostMs():0.##} ms)";
        }

        /// <summary>Per camera (G3WorldV2Light): true when this camera gets the screen-space pass this frame.</summary>
        public static bool BeginCamera(Camera cam, bool modern)
        {
            _active = false;
            bool main = cam.cameraType == CameraType.Game && (cam == Camera.main || Camera.main == null);
            if (main && G3GpuSafe.Capturing) return false;   // dx12-3: the save picture keeps the history as it is
            if (!modern || !main || !WorldSsgi.Wanted || !Application.isPlaying) { if (main) Release(); return false; }
            if (_mat == null)
            {
                var sh = Resources.Load<Shader>("G3RemasterSsgi");
                if (sh == null) { Debug.LogWarning("[G3WorldSsgi] shader G3RemasterSsgi missing"); return false; }
                _mat = new Material(sh) { name = "G3 Remaster SSGI", hideFlags = HideFlags.DontSave };
            }
            _curVp = GL.GetGPUProjectionMatrix(cam.nonJitteredProjectionMatrix, true) * cam.worldToCameraMatrix;
            _proj11 = cam.projectionMatrix.m11;
            float now = Time.unscaledTime;
            if (!_hasPrev || now - _lastTime > 1f) { _histValid = false; _prevVp = _curVp; }
            _lastTime = now;
            _hasPrev = true;
            _frame = (_frame + 1) & 1023;
            _active = true;
            return true;
        }

        static void Ensure(int w, int h)
        {
            if (_hist[0] != null && _hist[0].width == w && _hist[0].height == h) return;
            Release();
            for (int i = 0; i < 2; i++)
            {
                _hist[i] = new RenderTexture(w, h, 0, GraphicsFormat.R16G16B16A16_SFloat) { name = "G3 SSGI History " + i, hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _hist[i].Create();
                _histDepth[i] = new RenderTexture(w, h, 0, GraphicsFormat.R16G16_SFloat) { name = "G3 SSGI History Depth " + i, hideFlags = HideFlags.DontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _histDepth[i].Create();
                _histH[i] = RTHandles.Alloc(_hist[i]);
                _histDepthH[i] = RTHandles.Alloc(_histDepth[i]);
            }
            _histValid = false;
        }

        static void Release()
        {
            for (int i = 0; i < 2; i++)
            {
                _histH[i]?.Release(); _histH[i] = null;
                _histDepthH[i]?.Release(); _histDepthH[i] = null;
                if (_hist[i] != null) { _hist[i].Release(); Object.Destroy(_hist[i]); _hist[i] = null; }
                if (_histDepth[i] != null) { _histDepth[i].Release(); Object.Destroy(_histDepth[i]); _histDepth[i] = null; }
            }
            _histValid = false;
        }

        sealed class PassData
        {
            public TextureHandle source, color, hist, histDepth, depthNow;
            public Material material;
            public int pass;
            public Vector4 p1, p2, p3, size, temporal;
            public Matrix4x4 prevVp;
        }

        /// <summary>
        /// Records trace, temporal and filter at the size of <paramref name="small"/> (the ratio target); returns the
        /// filtered (rgb indirect, a visibility), or a null handle when not active.
        /// </summary>
        public static TextureHandle Record(RenderGraph rg, UniversalResourceData res, TextureHandle color4, TextureDesc small)
        {
            if (!_active || _mat == null || !color4.IsValid() || !res.cameraDepthTexture.IsValid()) return TextureHandle.nullHandle;
            Ensure(small.width, small.height);
            var c = WorldRemasterLook.Current;
            var g = WorldSsgi.Global();
            bool normals = res.cameraNormalsTexture.IsValid();
            var p1 = new Vector4(g[0], g[1], g[2], g[3]);
            var p2 = new Vector4(g[4], g[5], g[6], g[7]);
            var p3 = new Vector4(_frame, small.height * 0.5f * _proj11, normals ? 1 : 0, G3WorldGi.FlipY ? 1 : 0);
            var size = new Vector4(small.width, small.height, 1f / small.width, 1f / small.height);

            var d = small;
            d.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
            d.filterMode = FilterMode.Point;
            d.clearBuffer = false;
            d.name = "G3 SSGI Trace";
            var trace = rg.CreateTexture(d);
            d.name = "G3 SSGI";
            var filtered = rg.CreateTexture(d);

            int prev = _idx, cur = _idx ^ 1;
            _idx = cur;
            var histPrev = rg.ImportTexture(_histH[prev]);
            var histDepthPrev = rg.ImportTexture(_histDepthH[prev]);
            var histCur = rg.ImportTexture(_histH[cur]);
            var histDepthCur = rg.ImportTexture(_histDepthH[cur]);

            using (var b = rg.AddRasterRenderPass<PassData>("G3 SSGI Trace", out var pd))
            {
                pd.source = color4; pd.color = color4; pd.material = _mat; pd.pass = PassTrace;
                pd.p1 = p1; pd.p2 = p2; pd.p3 = p3; pd.size = size;
                b.UseTexture(color4);
                b.UseTexture(res.cameraDepthTexture);
                if (normals) b.UseTexture(res.cameraNormalsTexture);
                b.UseAllGlobalTextures(true);
                b.SetRenderAttachment(trace, 0);
                b.SetRenderFunc((PassData x, RasterGraphContext ctx) =>
                {
                    Values(x).SetTexture(ColorId, (RTHandle)x.color);
                    G3PassBlit.Draw(ctx.cmd, x.material, x.pass);
                });
            }

            using (var b = rg.AddRasterRenderPass<PassData>("G3 SSGI Temporal", out var pd))
            {
                pd.source = trace; pd.hist = histPrev; pd.histDepth = histDepthPrev; pd.material = _mat; pd.pass = PassTemporal;
                pd.p1 = p1; pd.p2 = p2; pd.p3 = p3; pd.size = size; pd.prevVp = _prevVp;
                pd.temporal = new Vector4(c.v2SsgiHistory, _histValid ? 1 : 0, 0, 0);
                b.UseTexture(trace);
                b.UseTexture(histPrev);
                b.UseTexture(histDepthPrev);
                b.UseTexture(res.cameraDepthTexture);
                b.UseAllGlobalTextures(true);
                b.SetRenderAttachment(histCur, 0);
                b.SetRenderAttachment(histDepthCur, 1);
                b.AllowPassCulling(false);
                b.SetRenderFunc((PassData x, RasterGraphContext ctx) =>
                {
                    var bl = Values(x);
                    bl.SetMatrix(PrevVpId, x.prevVp);
                    bl.SetVector(TemporalId, x.temporal);
                    bl.SetTexture(HistId, (RTHandle)x.hist);
                    bl.SetTexture(HistDepthId, (RTHandle)x.histDepth);
                    G3PassBlit.Draw(ctx.cmd, x.material, x.pass);
                });
            }

            using (var b = rg.AddRasterRenderPass<PassData>("G3 SSGI Filter", out var pd))
            {
                pd.source = histCur; pd.depthNow = histDepthCur; pd.material = _mat; pd.pass = PassFilter;
                pd.p1 = p1; pd.p2 = p2; pd.p3 = p3; pd.size = size;
                b.UseTexture(histCur);
                b.UseTexture(histDepthCur);
                b.UseAllGlobalTextures(true);
                b.SetRenderAttachment(filtered, 0);
                b.SetRenderFunc((PassData x, RasterGraphContext ctx) =>
                {
                    Values(x).SetTexture(DepthNowId, (RTHandle)x.depthNow);
                    G3PassBlit.Draw(ctx.cmd, x.material, x.pass);
                });
            }

            _prevVp = _curVp;
            _histValid = true;
            return filtered;
        }

        /// <summary>
        /// dx12-2: the shared parameters of all three passes on the draw's property block (they were set on the material
        /// inside the render functions, i.e. at once while the draws execute later: the filter read what the temporal
        /// pass left, and a draw still in flight could see the next frame's ping-pong history, its own target).
        /// </summary>
        static MaterialPropertyBlock Values(PassData x)
        {
            var b = G3PassBlit.Begin(x.source);
            b.SetVector(SsgiId, x.p1); b.SetVector(Ssgi2Id, x.p2);
            b.SetVector(Ssgi3Id, x.p3); b.SetVector(SizeId, x.size);
            return b;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Application.quitting -= Release;
            Application.quitting += Release;
            _active = false;
            _hasPrev = false;
            _histValid = false;
        }
    }
}
