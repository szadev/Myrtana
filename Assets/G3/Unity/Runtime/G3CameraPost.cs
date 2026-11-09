using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Turns URP post-processing on for every game camera, in code (fix-50). The game renders with one camera, the
    /// generated <c>Main Camera</c> of G3World_Master (player view, dialog rig, title stage and save thumbnails all use it;
    /// videos, map and inventory are UI). That camera has no <see cref="UniversalAdditionalCameraData"/>, and the one URP
    /// adds at runtime has <c>renderPostProcessing</c> off, so no Volume ever reached the image. Here every base game camera
    /// gets its data with post-processing on (off while it draws nothing: the title's cullingMask 0) and the options'
    /// anti-aliasing, at scene load and again before each render, so new or reloaded scenes need no edits.
    /// <c>-g3nopost</c> on the command line, the runner op <c>post on=0</c> or <see cref="Enabled"/> = false turn the
    /// effects off (A/B): G3's filters (<see cref="G3ImageProcessing"/>), FXAA and the options' colour lookup
    /// (<see cref="Changed"/>), while the camera keeps URP's post path (fix-55: switching <c>renderPostProcessing</c> off
    /// swapped URP's whole frame end, UberPost → FinalBlit and a different render-graph setup, so r30's "post off" was
    /// ~3 ms slower and did not measure the effects' cost; no player option ever takes that path). Order on the camera: world remaster atmosphere (before transparents) → its grade (after transparents) →
    /// URP post (DoF, bloom, grain, the options' colour lookup) → FXAA → UI.
    /// </summary>
    public static class G3CameraPost
    {
        static bool _enabled = true;
        static AntialiasingMode _antialiasing = AntialiasingMode.None;

        static bool _remasterTaa;
        static TemporalAA.Settings _taa = TemporalAA.Settings.Create();

        /// <summary>
        /// World remaster TAA (Docs/remaster-world.md 11.7, G3WorldV2 with the Lighting part and the JSON's v2Taa): while on,
        /// the game cameras use URP's temporal anti-aliasing (with the given settings) instead of the options' FXAA / none.
        /// </summary>
        public static void SetRemasterTaa(bool on, TemporalAA.Settings settings)
        {
            if (_remasterTaa == on && (!on || _taa.Equals(settings))) return;
            _remasterTaa = on;
            _taa = settings;
            ConfigureAll();
        }

        /// <summary>True while the remaster's TAA replaces the options' anti-aliasing.</summary>
        public static bool RemasterTaa => _remasterTaa;

        /// <summary>Raised when <see cref="Enabled"/> changes (the options applier drops or restores its colour lookup).</summary>
        public static event System.Action Changed;

        /// <summary>The post effects on the game cameras (default on; <c>-g3nopost</c> starts with them off).</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                ConfigureAll();
                Changed?.Invoke();
            }
        }

        /// <summary>The anti-aliasing of the game cameras (the Performance page: FXAA or none).</summary>
        public static AntialiasingMode Antialiasing
        {
            get => _antialiasing;
            set
            {
                if (_antialiasing == value) return;
                _antialiasing = value;
                ConfigureAll();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            _enabled = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-g3nopost") < 0;
            _antialiasing = AntialiasingMode.None;
            _remasterTaa = false;
            Changed = null;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ConfigureAll();

        static void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType == CameraType.Game) Configure(camera);
        }

        /// <summary>Configures every enabled game camera now (URP reads the data at the start of the next render).</summary>
        public static void ConfigureAll()
        {
            foreach (var camera in Camera.allCameras)
                if (camera.cameraType == CameraType.Game) Configure(camera);
        }

        /// <summary>
        /// Whether a game camera renders through URP's post path: every camera that draws something, also with the effects
        /// off (<see cref="Enabled"/> only bypasses the effects, fix-55); the title's empty camera (cullingMask 0) does not.
        /// </summary>
        public static bool UsesPostPath(bool drawsSomething) => drawsSomething;

        /// <summary>Adds the camera's URP data when missing and sets post-processing and anti-aliasing.</summary>
        public static void Configure(Camera camera)
        {
            if (camera == null) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data == null || data.renderType != CameraRenderType.Base) return;
            bool post = UsesPostPath(camera.cullingMask != 0);
            if (data.renderPostProcessing != post) data.renderPostProcessing = post;
            var aa = post && _enabled ? (_remasterTaa ? AntialiasingMode.TemporalAntiAliasing : _antialiasing) : AntialiasingMode.None;
            if (data.antialiasing != aa) data.antialiasing = aa;
            if (aa == AntialiasingMode.TemporalAntiAliasing)
            {
                ref var t = ref data.taaSettings;
                t.quality = _taa.quality; t.baseBlendFactor = _taa.baseBlendFactor; t.varianceClampScale = _taa.varianceClampScale;
                t.contrastAdaptiveSharpening = _taa.contrastAdaptiveSharpening; t.mipBias = _taa.mipBias; t.jitterScale = _taa.jitterScale;
            }
        }
    }
}
