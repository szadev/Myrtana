using G3.Unity;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Myrtana.Unity
{
    /// <summary>
    /// Applies the Video and Performance pages (<see cref="VideoOptions"/>, <see cref="PerformanceOptions"/>) to Unity and
    /// URP, at start, after every change and when a scene loads (a new camera). G3's options map as follows
    /// ([interp] where the port picks the engine counterpart; Docs/controls-options.md):
    /// <list type="bullet">
    /// <item>Resolution / V-Sync → Screen.SetResolution (player only) / QualitySettings.vSyncCount (off: frame cap
    /// <see cref="VideoOptions.FrameCapVSyncOff"/>, perf-2).</item>
    /// <item>Brightness, contrast, gamma R/G/B (G3's gamma ramp, slider × [Options.Gamma] scaling) → a colour-lookup texture
    /// (URP ColorLookup, display space, the last colour step) on a runtime global "Options Volume" (priority 100, above the
    /// scene's "G3 Post Volume"); neutral sliders = no lookup (<see cref="PostLook"/>).</item>
    /// <item>High detail distance 1-3 → world light cull distance 40 / 50 / 60 m (<c>-g3lightdist</c>).</item>
    /// <item>Low detail distance 0-3 → LOD bias × 0.4 / 0.55 / 0.75 / 1 of the quality level's own.</item>
    /// <item>Object detail 1-4 → GPU Resident Drawer small-mesh culling 4.5 / 2 / 0.8 / 0.2 % of the screen (G3
    /// fScreenObjectDistanceCulling 0.045 … 0.002, <c>-g3smallmesh</c>) and NPC update LOD on below Very High
    /// (<c>-g3npclod</c>).</item>
    /// <item>Texture quality 1-3 → global mipmap limit 2 / 1 / 0; texture filtering 0-4 → linear, anisotropic 2× … 16×.</item>
    /// <item>Vegetation quality 0-3 → off / on / on / on with shadows; vegetation view range 1-4 → G3's 40 / 50 / 65 / 75 m
    /// (scale on the 50 m ViewRange of the data).</item>
    /// <item>Shadow quality 0-4 → off (shadow distance 0) / 1 cascade / 2 / 4 / 4 cascades (<c>-g3cascades</c>), shadow
    /// small-mesh culling 2 / 2 / 1 / 0.5 / 0 % (<c>-g3shadowsmall</c>), shadowed point lights 0 / 0 / 1 / 2 / 4
    /// (<c>-g3pointshadows</c>).</item>
    /// <item>Bloom 0-2, depth of field, noise → G3's own filters (<see cref="G3ImageProcessing"/>, <see cref="PostLook"/>:
    /// highlight mask 1.7 / 3.0 over 1 − the weather's GlowIntensity, far blur 20–120 m of at most 2 px, additive noise in
    /// the darks); colour grading off = G3's LUT removed (the colour lookup); anti-aliasing → FXAA on the camera. The volume
    /// pins everything the port must not add: no tonemapping, no exposure, no vignette, no motion blur, and none of URP's
    /// own bloom, depth of field or film grain (the URP asset's template profile and the scene's volume have some). Lens flare →
    /// LensFlareComponentSRP components (none in the world yet); feedback (low-HP screen effect) is stored only.</item>
    /// <item>Post-processing itself is on for every game camera (<see cref="G3CameraPost"/>).</item>
    /// </list>
    /// The <c>-g3…</c> command-line switches still win (<see cref="G3RenderTuning.SetMenuOptions"/>).
    /// </summary>
    public sealed class DisplayOptionsApplier
    {
        public const int VolumePriority = 100;
        static readonly float[] LightDistances = { 40f, 50f, 60f };
        static readonly float[] LodBiasFactors = { 0.4f, 0.55f, 0.75f, 1f };
        static readonly float[] SmallMesh = { 4.5f, 2f, 0.8f, 0.2f };
        static readonly float[] VegetationRanges = { 0.8f, 1f, 1.3f, 1.5f };
        static readonly int[] Cascades = { 1, 1, 2, 4, 4 };
        static readonly float[] ShadowSmall = { 2f, 2f, 1f, 0.5f, 0f };
        // perf-22d: Very high 4 -> 2. Every shadowed point light is six more culling views over all world instances
        // (~14 ms job time and ~1.6 ms main thread each, Faring); a 3rd / 4th torch shadow within 20 m is rarely seen.
        static readonly int[] PointShadows = { 0, 0, 1, 2, 2 };

        readonly PlayerOptions _options;
        readonly VolumeProfile _profile;
        readonly ColorAdjustments _color;
        readonly Tonemapping _tonemapping;
        readonly Bloom _bloom;
        readonly DepthOfField _dof;
        readonly FilmGrain _grain;
        readonly ColorLookup _lookup;
        PostLook _post;
        Texture2D _lut;
        readonly float _baseLodBias;
        float _baseShadowDistance = -1;
        UniversalRenderPipelineAsset _urp;
        int _appliedWidth, _appliedHeight, _appliedRefresh;

        public DisplayOptionsApplier(PlayerOptions options, Transform parent)
        {
            _options = options;
            _baseLodBias = QualitySettings.lodBias;
            var go = new GameObject("Options Volume");
            if (parent != null) go.transform.SetParent(parent, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = VolumePriority;
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "Options";
            volume.sharedProfile = _profile;
            _color = _profile.Add<ColorAdjustments>();
            _tonemapping = _profile.Add<Tonemapping>();
            _bloom = _profile.Add<Bloom>();
            _dof = _profile.Add<DepthOfField>();
            _grain = _profile.Add<FilmGrain>();
            _lookup = _profile.Add<ColorLookup>();
            PinNeutral();
            go.AddComponent<PostWeatherBloom>();
            options.Changed += Apply;
            G3CameraPost.Changed += OnPostToggled;
            Application.quitting += RestoreAsset;
            Apply();
        }

        public void Apply()
        {
            ApplyVideo(_options.Video);
            ApplyPerformance(_options.Performance);
            ApplyPost(PostLook.From(_options.Video, _options.Performance));
        }

        void ApplyVideo(VideoOptions v)
        {
            QualitySettings.vSyncCount = v.VSync ? 1 : 0;
            Application.targetFrameRate = v.VSync ? -1 : VideoOptions.FrameCapVSyncOff;   // perf-2
            if (!Application.isEditor && !v.NativeResolution &&
                (v.Width != _appliedWidth || v.Height != _appliedHeight || v.RefreshRate != _appliedRefresh))
            {
                var rate = new RefreshRate { numerator = (uint)Mathf.Max(0, v.RefreshRate), denominator = 1 };
                if (v.RefreshRate > 0) Screen.SetResolution(v.Width, v.Height, Screen.fullScreenMode, rate);
                else Screen.SetResolution(v.Width, v.Height, Screen.fullScreenMode);
                _appliedWidth = v.Width;
                _appliedHeight = v.Height;
                _appliedRefresh = v.RefreshRate;
            }
        }

        void ApplyPerformance(PerformanceOptions p)
        {
            int Get(DetailSetting s) => p.Value(s);
            // G3RenderTuning switches (the command line wins)
            int objects = Get(DetailSetting.ObjectDetails), shadows = Get(DetailSetting.ShadowQuality);
            var menu = new RenderTuningOptions
            {
                LightDistance = LightDistances[Get(DetailSetting.DistanceHigh) - 1],
                SmallMesh = SmallMesh[objects - 1],
                NpcLod = objects < 4,
                Cascades = Cascades[shadows],
                ShadowSmallMesh = ShadowSmall[shadows],
                PointShadows = PointShadows[shadows],
            };
            G3RenderTuning.SetMenuOptions(menu);
            ApplyShadowDistance(shadows > 0);
            // fix-171: the remaster lighting quality follows the preset until the player picks one on the Remaster page
            G3WorldRemasterLook.SetDefaultQuality(LightingQualityLevels.FromPreset((int)p.Preset, shadows));

            QualitySettings.lodBias = _baseLodBias * LodBiasFactors[Get(DetailSetting.DistanceLow)];
            QualitySettings.globalTextureMipmapLimit = 3 - Get(DetailSetting.TextureQuality);
            int filter = Get(DetailSetting.TextureFilter);
            QualitySettings.anisotropicFiltering = filter == 0 ? AnisotropicFiltering.Disable : AnisotropicFiltering.ForceEnable;
            if (filter > 0) Texture.SetGlobalAnisotropicFilteringLimits(1 << filter, 1 << filter);

            int vegetation = Get(DetailSetting.VegetationQuality);
            G3VegetationRenderer.OptionEnabled = vegetation > 0;
            G3VegetationRenderer.OptionShadows = vegetation >= 3;
            G3VegetationRenderer.OptionQuality = vegetation;   // remaster ground cover density (low/medium/high)
            G3VegetationRenderer.OptionViewScale = VegetationRanges[Get(DetailSetting.VegetationViewRange) - 1];
        }

        /// <summary>Shadow quality Off: the URP asset's shadow distance 0 (restored when shadows come back and on quit).</summary>
        void ApplyShadowDistance(bool on)
        {
            _urp ??= GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (_urp == null) return;
            // fix-187: only the Off switch touches the asset (base taken when switching off, written back when on again):
            // writing the base on every apply overwrote G3WorldV2's remaster shadow distance, and a base taken while
            // the remaster's value was applied wrote that value back after the remaster had restored its own
            if (!on)
            {
                if (_baseShadowDistance < 0) _baseShadowDistance = _urp.shadowDistance;
                _urp.shadowDistance = 0f;
            }
            else if (_baseShadowDistance >= 0)
            {
                if (_urp.shadowDistance <= 0f) _urp.shadowDistance = _baseShadowDistance;
                _baseShadowDistance = -1;
            }
        }

        void RestoreAsset()
        {
            if (_urp != null && _baseShadowDistance >= 0 && _urp.shadowDistance <= 0f) _urp.shadowDistance = _baseShadowDistance;
            QualitySettings.lodBias = _baseLodBias;
        }

        /// <summary>
        /// Overrides, once, everything of the scene's "G3 Post Volume", the URP asset's template profile (Neutral
        /// tonemapping, vignette 0.2, bloom 0.25) and the defaults that would change G3's image: no tonemapping, no exposure
        /// or colour adjustment, no vignette, no motion blur, and URP's bloom, depth of field and film grain off (G3's own
        /// are <see cref="G3ImageProcessing"/>: URP's bloom thresholds linear max-channel, its grain is multiplicative and its
        /// Gaussian DoF blurs every pixel past coc 0.16 at half resolution, fix-55).
        /// </summary>
        void PinNeutral()
        {
            SetOverride(_tonemapping.mode, TonemappingMode.None, true);
            SetOverride(_color.postExposure, 0f, true);
            SetOverride(_color.contrast, 0f, true);
            SetOverride(_color.saturation, 0f, true);
            SetOverride(_color.hueShift, 0f, true);
            SetOverride(_color.colorFilter, Color.white, true);
            var vignette = _profile.Add<Vignette>();
            SetOverride(vignette.intensity, 0f, true);
            var motion = _profile.Add<MotionBlur>();
            SetOverride(motion.intensity, 0f, true);
            SetOverride(_bloom.intensity, 0f, true);
            SetOverride(_bloom.dirtIntensity, 0f, true);
            SetOverride(_dof.mode, DepthOfFieldMode.Off, true);
            SetOverride(_grain.intensity, 0f, true);
        }

        /// <summary>The post-processing part of the options (<see cref="PostLook"/>) onto G3's filters, the volume and the cameras.</summary>
        void ApplyPost(PostLook post)
        {
            G3ImageProcessing.Settings = new G3ImageSettings
            {
                BloomMultiplier = post.BloomMultiplier,
                DepthOfField = post.DepthOfField,
                DofStart = PostLook.DofStart,
                DofEnd = PostLook.DofEnd,
                DofMaxBlur = PostLook.DofMaxBlur,
                Noise = post.Noise,
                NoiseThreshold = PostLook.NoiseThreshold,
                NoiseIntensity = PostLook.NoiseIntensity,
            };
            G3CameraPost.Antialiasing = post.Fxaa ? AntialiasingMode.FastApproximateAntialiasing : AntialiasingMode.None;
            foreach (var f in Object.FindObjectsByType<LensFlareComponentSRP>(FindObjectsSortMode.None)) f.enabled = post.LensFlare;
            ApplyLookup(post);
            _post = post;
            G3CameraPost.ConfigureAll();
        }

        /// <summary>The post A/B toggle (<see cref="G3CameraPost.Enabled"/>): the colour lookup off with the effects, back after.</summary>
        void OnPostToggled()
        {
            _post = null; // rebuild the lookup
            ApplyPost(PostLook.From(_options.Video, _options.Performance));
        }

        /// <summary>The colour lookup: rebuilt only when the sliders or colour grading change; none when neutral or post is off.</summary>
        void ApplyLookup(PostLook post)
        {
            if (_post != null && _post.ColorGrading == post.ColorGrading && _post.Ramp.Equals(post.Ramp)) return;
            if (post.ColorNeutral || !G3CameraPost.Enabled)
            {
                SetOverride(_lookup.contribution, 0f, true);
                return;
            }
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int size = urp != null ? urp.colorGradingLutSize : 32;
            if (_lut == null || _lut.height != size)
            {
                if (_lut != null) Object.Destroy(_lut);
                _lut = new Texture2D(size * size, size, TextureFormat.RGBAHalf, false, true)
                {
                    name = "G3 Options LUT", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
            }
            var rgb = post.BuildLut(size);
            var pixels = new Color[rgb.Length / 3];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 1f);
            _lut.SetPixels(pixels);
            _lut.Apply(false);
            SetOverride(_lookup.texture, (Texture)_lut, true);
            SetOverride(_lookup.contribution, 1f, true);
        }

        static void SetOverride<T>(VolumeParameter<T> parameter, T value, bool on)
        {
            parameter.overrideState = on;
            if (on) parameter.value = value;
        }
    }
}
