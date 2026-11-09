using System;

namespace G3.Player
{
    /// <summary>
    /// The gamepad options (port, Docs/gamepad.md §5): camera sensitivity and invert per axis, stick dead zone, trigger
    /// press point, vibration, the Standard/Custom layout and the <see cref="PadMap"/>. Sensitivities use G3's slider
    /// storage (0…1, 0.5 = the middle) so the same slider rows and <see cref="ControlOptions.SensitivityFactor"/> apply.
    /// </summary>
    public sealed class PadOptions
    {
        public const float DeadZoneMin = 0.05f, DeadZoneMax = 0.4f, DefaultDeadZone = 0.15f;
        public const float TriggerMin = 0.1f, TriggerMax = 0.9f, DefaultTrigger = 0.5f;
        /// <summary>Slider steps of the dead-zone and trigger rows (0…Steps).</summary>
        public const int Steps = 20;

        float _sensX = 0.5f, _sensY = 0.5f, _deadZone = DefaultDeadZone, _trigger = DefaultTrigger;
        bool _invertX, _invertY, _vibration = true;
        string _layout = ControlOptions.Standard;

        public PadOptions() => Buttons.Changed += () => Changed?.Invoke();

        public PadMap Buttons { get; } = PadMap.Default();

        public event Action Changed;

        public float SensitivityX { get => _sensX; set => Set(ref _sensX, Clamp(value, 0f, 1f, 0.5f)); }
        public float SensitivityY { get => _sensY; set => Set(ref _sensY, Clamp(value, 0f, 1f, 0.5f)); }
        public bool InvertX { get => _invertX; set => Set(ref _invertX, value); }
        public bool InvertY { get => _invertY; set => Set(ref _invertY, value); }
        /// <summary>Radial dead zone of both sticks (<see cref="PadStick.Radial"/>).</summary>
        public float DeadZone { get => _deadZone; set => Set(ref _deadZone, Clamp(value, DeadZoneMin, DeadZoneMax, DefaultDeadZone)); }
        /// <summary>Trigger travel at which LT/RT count as pressed.</summary>
        public float TriggerThreshold { get => _trigger; set => Set(ref _trigger, Clamp(value, TriggerMin, TriggerMax, DefaultTrigger)); }
        public bool Vibration { get => _vibration; set => Set(ref _vibration, value); }

        public string Layout
        {
            get => _layout;
            set
            {
                string v = value == ControlOptions.Custom ? ControlOptions.Custom : ControlOptions.Standard;
                if (_layout == v) return;
                _layout = v;
                Changed?.Invoke();
            }
        }

        public void UseStandardButtons()
        {
            _layout = ControlOptions.Standard;
            Buttons.ResetToDefault();
        }

        /// <summary>Slider step (0…<see cref="Steps"/>) of a value in [min, max] and back.</summary>
        public static int ToStep(float value, float min, float max) =>
            Math.Max(0, Math.Min(Steps, (int)Math.Round((value - min) / (max - min) * Steps)));

        public static float FromStep(int step, float min, float max) =>
            min + (max - min) * Math.Max(0, Math.Min(Steps, step)) / Steps;

        void Set<T>(ref T field, T value)
        {
            if (Equals(field, value)) return;
            field = value;
            Changed?.Invoke();
        }

        static float Clamp(float v, float min, float max, float fallback) => float.IsNaN(v) ? fallback : Math.Max(min, Math.Min(max, v));
    }
}
