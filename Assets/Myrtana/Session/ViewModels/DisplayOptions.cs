using System;
using System.Collections.Generic;
using System.Globalization;

namespace Myrtana.Session
{
    /// <summary>The colour sliders of G3's Video page (gCHUDOptionVideoGamma, index 0…4).</summary>
    public enum VideoColor { Brightness, Contrast, GammaRed, GammaGreen, GammaBlue }

    /// <summary>
    /// G3's Video options (<c>IDD_MENU_OPTIONS_VIDEO_2</c>, [Options.Video]): resolution, V-Sync and the five colour sliders.
    /// A colour slider runs −100…100 (SetRange) and sets the gfx mixer's master value slider/100 × the scaling of
    /// ge3.ini [Options.Gamma] (FUN_100b01f0): brightness 0.2, contrast 0.075, red/green/blue gamma 0.3; 0 is neutral.
    /// The resolution is width × height @ refresh rate, 0 × 0 = the desktop's.
    /// </summary>
    public sealed class VideoOptions
    {
        public const int SliderMin = -100, SliderMax = 100;
        public static readonly VideoColor[] Colors = (VideoColor[])Enum.GetValues(typeof(VideoColor));
        /// <summary>ge3.ini [Options.Gamma] BrightnessScaling, ContrastScaling, Red/Green/BlueScaling.</summary>
        static readonly float[] Scaling = { 0.2f, 0.075f, 0.3f, 0.3f, 0.3f };

        readonly int[] _sliders = new int[5];
        /// <summary>perf-2: V-Sync is off by default (a 60 Hz V-Sync turned every 17-25 ms frame into 30 FPS, build 22d);
        /// with it off the frame rate is capped at <see cref="FrameCapVSyncOff"/>. A stored choice is kept.</summary>
        public const bool VSyncDefault = false;
        /// <summary>Frame-rate cap while V-Sync is off (menus and light scenes would otherwise run at hundreds of FPS).</summary>
        public const int FrameCapVSyncOff = 240;

        bool _vsync = VSyncDefault;
        int _width, _height, _refresh;

        public event Action Changed;

        public int Slider(VideoColor color) => _sliders[(int)color];

        public void SetSlider(VideoColor color, int position)
        {
            position = Math.Max(SliderMin, Math.Min(SliderMax, position));
            if (_sliders[(int)color] == position) return;
            _sliders[(int)color] = position;
            Changed?.Invoke();
        }

        /// <summary>The mixer value of a colour slider (G3 eSSetupEngine 0x1c0…0x1d0), e.g. brightness ±0.2.</summary>
        public float Value(VideoColor color) => Slider(color) * 0.01f * Scaling[(int)color];

        public bool VSync
        {
            get => _vsync;
            set
            {
                if (_vsync == value) return;
                _vsync = value;
                Changed?.Invoke();
            }
        }

        public int Width => _width;
        public int Height => _height;
        public int RefreshRate => _refresh;
        /// <summary>No resolution chosen: the screen keeps the desktop's (or the launcher's) mode.</summary>
        public bool NativeResolution => _width <= 0 || _height <= 0;

        public void SetResolution(int width, int height, int refreshRate)
        {
            if (width <= 0 || height <= 0) width = height = refreshRate = 0;
            if (_width == width && _height == height && _refresh == refreshRate) return;
            _width = width;
            _height = height;
            _refresh = Math.Max(0, refreshRate);
            Changed?.Invoke();
        }

        /// <summary>The value text of the resolution row ("1920 x 1080", G3's format).</summary>
        public static string ResolutionText(int width, int height) =>
            width.ToString(CultureInfo.InvariantCulture) + " x " + height.ToString(CultureInfo.InvariantCulture);

        /// <summary>The value text of a colour slider ("-35%").</summary>
        public static string SliderText(int position) => position.ToString(CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>The detail presets of G3's Performance page ("Details"); Custom opens the slider page.</summary>
    public enum DetailPreset { Low, Medium, High, VeryHigh, Custom }

    /// <summary>The sliders of G3's custom Performance page (<c>IDD_MENU_OPTIONS_PERFORMANCE_CUSTOM_2</c>), in [Options.*] names.</summary>
    public enum DetailSetting
    {
        DistanceHigh, DistanceLow, ObjectDetails, TextureQuality, TextureFilter, VegetationQuality, VegetationViewRange,
        ShadowQuality, LensFlare, DepthOfField, Bloom, Antialiasing, Noise, Feedback, ColorGrading,
    }

    /// <summary>
    /// G3's Performance options ("Details": <c>IDD_MENU_OPTIONS_PERFORMANCE_2</c> presets Very High / High / Medium / Low /
    /// Custom, and the custom page's sliders). The presets are ge3.ini [Options.Low] … [Options.VeryHigh] of CP 1.75; the
    /// ranges are the ini's comments (Antialiasing: CP shows Off / FXAA). Colour grading is in no preset: a preset keeps
    /// it. Changing a slider makes the preset Custom. What each value does in the port: Myrtana.Unity PerformanceApplier.
    /// </summary>
    public sealed class PerformanceOptions
    {
        public static readonly DetailSetting[] Settings = (DetailSetting[])Enum.GetValues(typeof(DetailSetting));

        /// <summary>(min, max) of each setting.</summary>
        static readonly (int min, int max)[] Ranges =
        {
            (1, 3), (0, 3), (1, 4), (1, 3), (0, 4), (0, 3), (1, 4), (0, 4), (0, 1), (0, 1), (0, 2), (0, 1), (0, 1), (0, 1), (0, 1),
        };

        /// <summary>ge3.ini [Options.Low/Medium/High/VeryHigh], in <see cref="DetailSetting"/> order (ColorGrading = −1: kept).</summary>
        static readonly int[][] Presets =
        {
            new[] { 1, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, -1 },
            new[] { 2, 2, 2, 2, 1, 1, 2, 1, 0, 0, 1, 0, 0, 0, -1 },
            new[] { 3, 3, 3, 2, 2, 3, 3, 3, 0, 1, 1, 0, 0, 0, -1 },
            new[] { 3, 3, 4, 3, 4, 3, 4, 4, 0, 1, 2, 1, 1, 1, -1 },
        };

        readonly int[] _values = new int[Settings.Length];
        DetailPreset _preset;

        public PerformanceOptions()
        {
            _values[(int)DetailSetting.ColorGrading] = 1;
            ApplyPreset(DetailPreset.High);
        }

        public event Action Changed;

        public DetailPreset Preset => _preset;

        public int Value(DetailSetting setting) => _values[(int)setting];

        public static int Min(DetailSetting setting) => Ranges[(int)setting].min;
        public static int Max(DetailSetting setting) => Ranges[(int)setting].max;

        /// <summary>A preset radio button: its values (Custom keeps the current ones and only marks them custom).</summary>
        public void SelectPreset(DetailPreset preset)
        {
            if (preset == _preset && preset == DetailPreset.Custom) return;
            ApplyPreset(preset);
            Changed?.Invoke();
        }

        /// <summary>A slider of the custom page: sets the value and the preset to Custom.</summary>
        public void Set(DetailSetting setting, int value)
        {
            value = Math.Max(Min(setting), Math.Min(Max(setting), value));
            if (_values[(int)setting] == value && _preset == DetailPreset.Custom) return;
            _values[(int)setting] = value;
            _preset = DetailPreset.Custom;
            Changed?.Invoke();
        }

        /// <summary>Restores stored values (no event): the preset, then each stored value within its range.</summary>
        public void Load(DetailPreset preset, IReadOnlyDictionary<DetailSetting, int> values)
        {
            ApplyPreset(preset == DetailPreset.Custom ? DetailPreset.High : preset);
            _preset = preset;
            if (values == null) return;
            foreach (var kv in values)
                if (preset == DetailPreset.Custom || PresetValue(preset, kv.Key) < 0)
                    _values[(int)kv.Key] = Math.Max(Min(kv.Key), Math.Min(Max(kv.Key), kv.Value));
        }

        /// <summary>The value a preset gives a setting (−1: the preset keeps it).</summary>
        public static int PresetValue(DetailPreset preset, DetailSetting setting) =>
            preset == DetailPreset.Custom ? -1 : Presets[(int)preset][(int)setting];

        void ApplyPreset(DetailPreset preset)
        {
            _preset = preset;
            if (preset == DetailPreset.Custom) return;
            var p = Presets[(int)preset];
            for (int i = 0; i < p.Length; i++)
                if (p[i] >= 0) _values[i] = p[i];
        }
    }
}
