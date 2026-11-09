using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Cloud shadows of the v2 cloud slab (Docs/remaster-world.md 16.4, Lighting part, JSON <c>v2CloudShadows</c>): a
    /// 256² top-down transmittance map of the sun through the slab around the camera
    /// (Hidden/G3/Remaster/CloudShadow, re-drawn every frame: the clouds drift), global <c>_G3CloudShadowTex</c> +
    /// <c>_G3CloudShadow</c> (min x, min z, size, strength; strength 0 = off). The light-ratio pass multiplies the sun term
    /// with it, the fog shafts their sun in-scatter. Off = global zero, nothing drawn.
    /// </summary>
    public static class G3CloudShadow
    {
        static readonly int TexId = Shader.PropertyToID("_G3CloudShadowTex");
        static readonly int ParamsId = Shader.PropertyToID("_G3CloudShadow");
        static readonly int WindowId = Shader.PropertyToID("_G3CloudShadowWindow");
        static readonly int SunId = Shader.PropertyToID("_G3CloudShadowSun");
        static readonly int OvercastId = Shader.PropertyToID("_Overcast");
        static readonly int SunDirId = Shader.PropertyToID("_SunDir");

        static bool _on;
        static RenderTexture _rt;
        static Material _mat;
        static CommandBuffer _cb;
        static Vector4 _window;
        static int _frame;
        static bool _drawn;

        /// <summary>True while the map is drawn and used.</summary>
        public static bool Active => _on;

        /// <summary>Turns the cloud shadows on or off (G3WorldV2.Apply: Lighting part, the JSON clouds + cloud shadow toggles).</summary>
        public static void Apply(bool on)
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            var c = WorldRemasterLook.Current;
            on &= c.v2 && c.v2Clouds && c.v2CloudShadows && c.v2CloudShadowStrength > 0 && Application.isPlaying;
            _on = false;
            _drawn = false;
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            if (!on) return;
            if (_mat == null)
            {
                var sh = Resources.Load<Shader>("G3RemasterCloudShadow");
                if (sh == null) { Debug.LogWarning("[G3CloudShadow] shader G3RemasterCloudShadow missing"); return; }
                _mat = new Material(sh) { name = "G3 Cloud Shadow", hideFlags = HideFlags.DontSave };
            }
            if (_rt == null)
            {
                _rt = new RenderTexture(256, 256, 0, RenderTextureFormat.R16, RenderTextureReadWrite.Linear)
                {
                    name = "G3 Cloud Shadow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
                };
                if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R16)) _rt.format = RenderTextureFormat.ARGBHalf;
                _rt.Create();
            }
            _cb ??= new CommandBuffer { name = "G3 Cloud Shadow" };
            _on = true;
            RenderPipelineManager.beginContextRendering += OnBeginContext;
        }

        /// <summary>One line for the runner.</summary>
        public static string Describe() => _on ? $"cloud shadows {WorldRemasterLook.Current.v2CloudShadowStrength:0.##} ({_window.z:0} m)" : "cloud shadows off";

        static void OnBeginContext(ScriptableRenderContext ctx, System.Collections.Generic.List<Camera> cams)
        {
            if (!_on || _rt == null || _mat == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            var c = WorldRemasterLook.Current;
            // fix-171 (lighting quality): the slab drifts a few m/s over a 6 km window: every n-th frame is enough
            if (c.v2CloudShadowEvery > 1 && _drawn && (++_frame % c.v2CloudShadowEvery) != 0) return;
            _drawn = true;
            var sky = RenderSettings.skybox;
            float overcast = sky != null && sky.HasProperty(OvercastId) ? sky.GetFloat(OvercastId) : 0;
            Vector3 sd = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward
                : sky != null && sky.HasProperty(SunDirId) ? (Vector3)sky.GetVector(SunDirId) : Vector3.up;
            float cover = Mathf.Clamp01(c.v2CloudCoverage + overcast * 0.55f);
            var p = cam.transform.position;
            float size = c.v2CloudShadowRange, texel = size / 256f;
            float minX = Mathf.Floor((p.x - size * 0.5f) / texel) * texel, minZ = Mathf.Floor((p.z - size * 0.5f) / texel) * texel;
            _window = new Vector4(minX, minZ, size, p.y);
            _mat.SetVector(WindowId, _window);
            _mat.SetVector(SunId, new Vector4(sd.x, sd.y, sd.z, cover));
            _cb.Clear();
            _cb.SetRenderTarget(_rt);
            _cb.DrawProcedural(Matrix4x4.identity, _mat, 0, MeshTopology.Triangles, 3);
            _cb.SetGlobalTexture(TexId, _rt);
            // below the horizon (night, moon) the shadows fade out
            float k = Mathf.Clamp01((sd.y - 0.02f) / 0.1f);
            // G3 already dims the sun with its overcast (CloudThickness): the patches fade out as the sky closes, else rain
            // and fog would darken the ground twice (first overcast A|B|C)
            k *= 1 - Mathf.Clamp01(overcast);
            _cb.SetGlobalVector(ParamsId, new Vector4(minX, minZ, size, c.v2CloudShadowStrength * k));
            // dx12-1: queued on the frame's context (GPU order before its cameras) instead of an immediate execute from inside
            // the render-pipeline callback (D3D12 + split graphics jobs: no out-of-band GPU work in the SRP callbacks, crash-2)
            ctx.ExecuteCommandBuffer(_cb);
            _cb.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            _on = false;
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
        }
    }
}
