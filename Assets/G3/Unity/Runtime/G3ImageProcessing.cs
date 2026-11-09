using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>What <see cref="G3ImageProcessing"/> does: G3's own image filters, in G3's units (fix-55).</summary>
    public struct G3ImageSettings
    {
        /// <summary>Highlight mask multiplier (ge3.ini BloomIntensity 1.7 / HDRGlowIntensity 3.0); 0 = no bloom.</summary>
        public float BloomMultiplier;
        /// <summary>DoF far blur from <see cref="DofStart"/> to <see cref="DofEnd"/> (m), tap disc radius ≤ DofMaxBlur px.</summary>
        public bool DepthOfField;
        public float DofStart, DofEnd, DofMaxBlur;
        /// <summary>Additive noise in the darks: (noise − 0.5) · (1 − luma)^NoiseThreshold · NoiseIntensity.</summary>
        public bool Noise;
        public float NoiseThreshold, NoiseIntensity;

        public bool Any => BloomMultiplier > 0 || DepthOfField || Noise;
    }

    /// <summary>
    /// G3 CP 1.75's image filters (Materials.pak Effects/ImageProcessing IP_DOF, IP_MaskHighLights, IP_Blur, IP_HDRI bloom
    /// combine, IP_Noise) as one URP render-graph pass chain on the game camera, before URP's post-processing (which then
    /// only adds the options' colour lookup and FXAA). Ported rather than mapped onto URP's effects (fix-55): URP's bloom
    /// thresholds the brightest channel in linear light, its film grain multiplies the colour (nothing in the darks, where
    /// G3's noise lives), and its Gaussian DoF swaps in a half-resolution image from a coc of 0.16 on (the whole vista
    /// blurred). Steps (shader Hidden/G3/ImageProcessing, Resources/G3ImageProcessing.shader; the math is mirrored and tested
    /// in Myrtana.Session PostLook):
    /// <list type="number">
    /// <item>DoF (full resolution): 8 Poisson taps on a disc of coc·DofMaxBlur pixels, coc = (view depth − start) /
    /// (end − start) clamped, G3's leak reduction (a tap nearer and sharper than the centre weighs its own coc).</item>
    /// <item>Bloom: highlights c·max(luma − threshold, 0)·multiplier on display values at quarter resolution, G3's 13-tap
    /// blur horizontally then vertically, screen-blended on the display values.</item>
    /// <item>Noise: per pixel, a new pattern each frame (G3 cycles 32 offsets), weighted (1 − luma)^7 · 0.15.</item>
    /// </list>
    /// Runs only on base game cameras that render post-processing, and not while <see cref="G3CameraPost.Enabled"/> is
    /// off (the post A/B toggle). <see cref="BloomThreshold"/> follows the weather (Myrtana.Unity PostWeatherBloom).
    /// </summary>
    public static class G3ImageProcessing
    {
        static readonly int DofId = Shader.PropertyToID("_G3Dof");
        static readonly int BloomId = Shader.PropertyToID("_G3Bloom");
        static readonly int NoiseId = Shader.PropertyToID("_G3Noise");
        static readonly int BloomTexId = Shader.PropertyToID("_G3BloomTex");
        const int PassDof = 0, PassMask = 1, PassBlurH = 2, PassBlurV = 3, PassCombine = 4;
        public const string ShaderResource = "G3ImageProcessing";

        static G3ImageSettings _settings;
        static float _bloomThreshold = 0.6f;
        static Material _material;
        static ImagePass _pass;
        static bool _missingLogged;

        /// <summary>The filters the options asked for (Myrtana.Unity DisplayOptionsApplier).</summary>
        public static G3ImageSettings Settings
        {
            get => _settings;
            set => _settings = value;
        }

        /// <summary>G3's highlight threshold on display luma (1 − the weather's GlowIntensity; ge3.ini 0.59).</summary>
        public static float BloomThreshold
        {
            get => _bloomThreshold;
            set => _bloomThreshold = value;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            _settings = default;
            _bloomThreshold = 0.6f;
            _pass = null;
            _missingLogged = false;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        static void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            var s = _settings;
            if (!s.Any || !G3CameraPost.Enabled || camera.cameraType != CameraType.Game || camera.cullingMask == 0) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data == null || !data.renderPostProcessing || data.renderType != CameraRenderType.Base || data.scriptableRenderer == null)
                return;
            if (!EnsureMaterial()) return;

            float range = Mathf.Max(0.01f, s.DofEnd - s.DofStart);
            _material.SetVector(DofId, new Vector4(1f / range, -s.DofStart / range, s.DofMaxBlur, 0));
            _material.SetVector(BloomId, new Vector4(_bloomThreshold, s.BloomMultiplier, 0, 0));
            _material.SetVector(NoiseId, new Vector4(s.NoiseThreshold, s.Noise ? s.NoiseIntensity : 0f, Time.frameCount & 31, 0));
            _pass ??= new ImagePass(_material);
            _pass.Setup(s.DepthOfField, s.BloomMultiplier > 0, s.Noise);
            data.scriptableRenderer.EnqueuePass(_pass);
        }

        static bool EnsureMaterial()
        {
            if (_material != null) return true;
            var shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null || !shader.isSupported)
            {
                if (!_missingLogged) Debug.LogWarning("[G3ImageProcessing] shader Resources/" + ShaderResource + " missing or unsupported: no G3 bloom/DoF/noise");
                _missingLogged = true;
                return false;
            }
            _material = new Material(shader) { name = "G3 Image Processing", hideFlags = HideFlags.DontSave };
            return true;
        }

        sealed class ImagePass : ScriptableRenderPass
        {
            readonly Material _mat;
            bool _dof, _bloom, _noise;

            sealed class BlitData
            {
                public TextureHandle source, bloom;
                public Material material;
                public int pass;
            }

            public ImagePass(Material mat)
            {
                _mat = mat;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }

            public void Setup(bool dof, bool bloom, bool noise)
            {
                _dof = dof;
                _bloom = bloom;
                _noise = noise;
                ConfigureInput(dof ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer) return;
                var color = res.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(color);
                desc.clearBuffer = false;
                desc.msaaSamples = MSAASamples.None;
                desc.depthBufferBits = DepthBits.None;
                desc.filterMode = FilterMode.Bilinear;
                desc.wrapMode = TextureWrapMode.Clamp;

                if (_dof && res.cameraDepthTexture.IsValid())
                    color = Blit(renderGraph, res, color, TextureHandle.nullHandle, desc, "G3 DoF", PassDof, true);

                var bloom = TextureHandle.nullHandle;
                if (_bloom)
                {
                    var quarter = desc;
                    quarter.width = Mathf.Max(1, desc.width / 4);
                    quarter.height = Mathf.Max(1, desc.height / 4);
                    var mask = Blit(renderGraph, res, color, TextureHandle.nullHandle, quarter, "G3 Bloom Mask", PassMask, false);
                    var blurH = Blit(renderGraph, res, mask, TextureHandle.nullHandle, quarter, "G3 Bloom Blur H", PassBlurH, false);
                    bloom = Blit(renderGraph, res, blurH, TextureHandle.nullHandle, quarter, "G3 Bloom Blur V", PassBlurV, false);
                }

                if (_bloom || _noise)
                    color = Blit(renderGraph, res, color, bloom, desc, "G3 Bloom + Noise", PassCombine, false);
                res.cameraColor = color;
            }

            TextureHandle Blit(RenderGraph renderGraph, UniversalResourceData res, TextureHandle source, TextureHandle bloom,
                TextureDesc desc, string name, int pass, bool depth)
            {
                desc.name = name;
                var target = renderGraph.CreateTexture(desc);
                using (var builder = renderGraph.AddRasterRenderPass<BlitData>(name, out var d))
                {
                    d.source = source;
                    d.bloom = bloom;
                    d.material = _mat;
                    d.pass = pass;
                    builder.UseTexture(source);
                    if (bloom.IsValid()) builder.UseTexture(bloom);
                    if (depth) builder.UseTexture(res.cameraDepthTexture);
                    if (depth) builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(target, 0);
                    builder.SetRenderFunc((BlitData data, RasterGraphContext ctx) =>
                    {
                        var b = G3PassBlit.Begin(data.source);   // dx12-2: the bloom texture with the draw
                        if (data.pass == PassCombine)
                            b.SetTexture(BloomTexId, data.bloom.IsValid() ? (Texture)(RTHandle)data.bloom : Texture2D.blackTexture);
                        G3PassBlit.Draw(ctx.cmd, data.material, data.pass);
                    });
                }
                return target;
            }
        }
    }
}
