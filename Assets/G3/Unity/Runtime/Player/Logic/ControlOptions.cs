using System;

namespace G3.Player
{
    /// <summary>
    /// The options of G3's Controls page (<c>IDD_MENU_OPTIONS_CONTROLS_2</c>, [Options.Controls] of the user options):
    /// mouse sensitivity, invert and smoothing per axis, "Camera Lookaround Inverse", the Standard/Custom keyboard layout
    /// and the <see cref="KeyMap"/>. Values are stored as G3 stores them (eSSetupEngine 0x1f8…0x210): sensitivity and
    /// smoothing as 0…1 (0.5 = the slider's middle), the layout as "Standard"/"Custom".
    /// The Alt. Camera row stays the port's first-person option (Myrtana PlayerOptions.FirstPerson).
    /// </summary>
    public sealed class ControlOptions
    {
        public const string Standard = "Standard", Custom = "Custom";
        /// <summary>Slider positions of the mouse rows (gCHUDOptionControlsMouseSensitivity/Smoothing SetRange −100…100).</summary>
        public const int SliderMin = -100, SliderMax = 100;

        float _sensX = 0.5f, _sensY = 0.5f, _smoothX = 0.5f, _smoothY = 0.5f;
        bool _invertX, _invertY, _lookaroundInverse;
        string _layout = Standard;

        public ControlOptions()
        {
            Keys.Changed += OnKeysChanged;
            Pad.Changed += OnKeysChanged;
        }

        /// <summary>The session key bindings (changes raise <see cref="Changed"/> too).</summary>
        public KeyMap Keys { get; } = KeyMap.Default();

        /// <summary>The gamepad options and pad bindings (port, Docs/gamepad.md; changes raise <see cref="Changed"/> too).</summary>
        public PadOptions Pad { get; } = new PadOptions();

        /// <summary>Raised after every change of an option or a binding.</summary>
        public event Action Changed;

        /// <summary>G3 MouseSensitivityX/Y (0…1).</summary>
        public float SensitivityX { get => _sensX; set => Set(ref _sensX, Clamp01(value)); }
        public float SensitivityY { get => _sensY; set => Set(ref _sensY, Clamp01(value)); }
        /// <summary>G3 MouseSmoothingX/Y (0.001…1).</summary>
        public float SmoothingX { get => _smoothX; set => Set(ref _smoothX, Math.Max(0.001f, Clamp01(value))); }
        public float SmoothingY { get => _smoothY; set => Set(ref _smoothY, Math.Max(0.001f, Clamp01(value))); }
        public bool InvertX { get => _invertX; set => Set(ref _invertX, value); }
        public bool InvertY { get => _invertY; set => Set(ref _invertY, value); }
        /// <summary>"Invert rear view Y-Axis": the mouse's Y axis is inverted while looking behind (<see cref="SessionKey.Look"/>).</summary>
        public bool LookaroundInverse { get => _lookaroundInverse; set => Set(ref _lookaroundInverse, value); }

        /// <summary>"Standard" or "Custom" (G3 Options.Controls "Controls").</summary>
        public string Layout
        {
            get => _layout;
            set
            {
                string v = value == Custom ? Custom : Standard;
                if (_layout == v) return;
                _layout = v;
                Changed?.Invoke();
            }
        }

        /// <summary>The Standard radio: the default keys (gCSessionKeys::CreateDefaultKeys) and the Standard layout.</summary>
        public void UseStandardKeys()
        {
            _layout = Standard;
            Keys.ResetToDefault(); // raises Changed
        }

        /// <summary>Stored value (0…1) of a slider position (FUN_100ab020: pos·0.01·0.5 + 0.5).</summary>
        public static float FromSlider(int position) =>
            Math.Max(SliderMin, Math.Min(SliderMax, position)) * 0.005f + 0.5f;

        /// <summary>Slider position of a stored value (the value text shows this number).</summary>
        public static int ToSlider(float value) =>
            Math.Max(SliderMin, Math.Min(SliderMax, (int)Math.Round((value - 0.5f) * 200f)));

        /// <summary>
        /// The camera's turn factor for a sensitivity (gCCameraAI_PS 0x548/0x54c = 2·f + 0.5), relative to the default
        /// (f = 0.5 → 1.5), so 1 = the port's base mouse speed; range 1/3…5/3.
        /// </summary>
        public static float SensitivityFactor(float value) => (2f * Clamp01(value) + 0.5f) / 1.5f;

        /// <summary>The camera's smoothing value (gCCameraAI_PS 0x550/0x554 = 2·f, 0.002…2; 1 at the default).</summary>
        public static float SmoothingValue(float value) => 2f * Math.Max(0.001f, Clamp01(value));

        void OnKeysChanged() => Changed?.Invoke();

        void Set<T>(ref T field, T value)
        {
            if (Equals(field, value)) return;
            field = value;
            Changed?.Invoke();
        }

        static float Clamp01(float v) => float.IsNaN(v) ? 0.5f : Math.Max(0f, Math.Min(1f, v));
    }

    /// <summary>
    /// Mouse look of the hero camera with the Controls options: sensitivity factor, invert and smoothing per axis.
    /// Smoothing is an exponential filter of the per-frame movement with the time constant
    /// <see cref="SmoothingSeconds"/> × the G3 smoothing value [interp: G3's camera filter was not traced; at the default
    /// the lag is 25 ms, at the slider's left end none, at the right end 50 ms].
    /// </summary>
    public sealed class MouseLookFilter
    {
        public const float SmoothingSeconds = 0.025f;

        float _x, _y;

        /// <summary>Turn (x, degrees right) and pitch (y, degrees up) of this frame for the raw mouse movement.</summary>
        public (float x, float y) Apply(float rawX, float rawY, float dt, ControlOptions options, bool lookingBehind = false)
        {
            float x = rawX * ControlOptions.SensitivityFactor(options.SensitivityX) * (options.InvertX ? -1 : 1);
            bool invY = options.InvertY ^ (lookingBehind && options.LookaroundInverse);
            float y = rawY * ControlOptions.SensitivityFactor(options.SensitivityY) * (invY ? -1 : 1);
            _x = Filter(_x, x, dt, ControlOptions.SmoothingValue(options.SmoothingX));
            _y = Filter(_y, y, dt, ControlOptions.SmoothingValue(options.SmoothingY));
            return (_x, _y);
        }

        public void Reset() => _x = _y = 0;

        /// <summary>One step of the filter (per-frame amounts; a constant input converges to itself).</summary>
        public static float Filter(float previous, float raw, float dt, float smoothing)
        {
            float tau = SmoothingSeconds * smoothing;
            if (tau <= 0.0001f || dt <= 0f) return raw;
            float a = 1f - (float)Math.Exp(-dt / tau);
            return previous + (raw - previous) * a;
        }
    }
}
