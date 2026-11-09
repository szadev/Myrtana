using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// remaster-sand (sand-2, Docs/remaster-world.md 22.7): daytime heat haze over Varant - a cheap screen-space shimmer of the
    /// distant ground near the horizon (one full-screen copy through Resources/G3SandHaze.shader before the grade: rising
    /// noise offsets of ~1-2 px, faded in from 25 m and out by ~900 m, only on views close to the horizontal). Strength =
    /// json <c>sandHeatHaze</c> x desert at the camera x sun height (none below ~8 degrees: off at night, dawn and dusk) x clear
    /// weather (a sandstorm hides it). No pass at all when the strength is 0.
    /// </summary>
    public static class G3SandHaze
    {
        static readonly int HazeId = Shader.PropertyToID("_G3SandHaze");
        static Material _mat;
        static bool _missing, _hooked;
        static HazePass _pass;
        static float _strength;

        public static string Describe() => _strength > 0.001f ? $"haze {_strength:0.00}" : "haze off";

        /// <summary>Haze strength for a sun elevation (degrees), desert weight and sandstorm share; json scale applied.</summary>
        public static float Strength(float sunElevationDeg, float desert, float storm) =>
            SandV2.HeatHaze(sunElevationDeg, desert, storm);

        /// <summary>Per frame from <see cref="G3SandV2.Tick"/> (sand on).</summary>
        internal static void Tick(Camera cam)
        {
            float target = 0;
            if (cam != null)
            {
                var sun = RenderSettings.sun;
                float el = sun != null && sun.isActiveAndEnabled ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1, 1)) * Mathf.Rad2Deg : -90;
                var eye = cam.transform.position;
                target = Strength(el, G3SnowV2.DesertAt(eye.x, eye.z), G3SandV2.Storm);
            }
            _strength = Mathf.MoveTowards(_strength, target, Time.deltaTime * 0.3f);
            bool on = _strength > 0.001f;
            if (on && !_hooked) { RenderPipelineManager.beginCameraRendering += OnBeginCamera; _hooked = true; }
            else if (!on && _hooked) { RenderPipelineManager.beginCameraRendering -= OnBeginCamera; _hooked = false; }
        }

        internal static void Release()
        {
            if (_hooked) RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            _hooked = false;
            _strength = 0;
            if (_mat != null) G3GpuSafe.Destroy(_mat);   // dx12-4: after the frames that may still draw with it
            _mat = null;
            _pass = null;
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_strength <= 0.001f || cam.cameraType != CameraType.Game || cam.cullingMask == 0) return;
            if (G3GpuSafe.Capturing) return;   // dx12-4: the save picture renders without the haze (dx12-3 capture rule)
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null || data.renderType != CameraRenderType.Base) return;
            if (_mat == null && !_missing)
            {
                var sh = Resources.Load<Shader>("G3SandHaze");
                if (sh == null || !sh.isSupported) { _missing = true; Debug.LogWarning("[G3SandHaze] shader G3SandHaze missing/unsupported: no heat haze"); return; }
                _mat = new Material(sh) { name = "G3 Sand Haze", hideFlags = HideFlags.DontSave };
            }
            if (_mat == null) return;
            _mat.SetVector(HazeId, new Vector4(_strength, Time.time, 0, 0));
            _pass ??= new HazePass(_mat);
            data.scriptableRenderer.EnqueuePass(_pass);
        }

        /// <summary>Copy of the camera colour through the haze shader (render graph), just before the remaster grade.</summary>
        sealed class HazePass : ScriptableRenderPass
        {
            readonly Material _mat;

            sealed class PassData
            {
                public TextureHandle source;
                public Material material;
            }

            public HazePass(Material mat)
            {
                _mat = mat;
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents - 1;
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (_mat == null || res.isActiveTargetBackBuffer) return;   // dx12-4: skipped cleanly without its material
                var source = res.activeColorTexture;
                if (!source.IsValid()) return;
                var desc = renderGraph.GetTextureDesc(source);
                desc.name = "G3 Sand Haze";
                desc.clearBuffer = false;
                var target = renderGraph.CreateTexture(desc);
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("G3 Sand Haze", out var pd))
                {
                    pd.source = source;
                    pd.material = _mat;
                    builder.UseTexture(source);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(target, 0);
                    builder.SetRenderFunc((PassData d, RasterGraphContext c) =>
                        Blitter.BlitTexture(c.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, 0));
                }
                res.cameraColor = target;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            _hooked = false; _mat = null; _missing = false; _pass = null; _strength = 0;
        }
    }
}
