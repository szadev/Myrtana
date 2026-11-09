using System;

namespace G3.Player
{
    /// <summary>
    /// Stick shaping for the pad (Docs/gamepad.md §3/§5): radial dead zone, response curve, analog walk / run, camera
    /// rates. Engine-free.
    /// </summary>
    public static class PadStick
    {
        /// <summary>Deflection at and above which the outer edge counts as full (worn sticks rarely reach 1).</summary>
        public const float OuterEdge = 0.95f;
        /// <summary>Left-stick deflection below which the hero walks instead of running.</summary>
        public const float WalkThreshold = 0.55f;
        /// <summary>Camera turn rate at full deflection and default sensitivity (degrees per second).</summary>
        public const float YawRate = 180f, PitchRate = 110f;
        /// <summary>Exponent of the camera response curve (fine aim near the centre, full speed at the edge).</summary>
        public const float CameraCurve = 1.8f;

        /// <summary>
        /// Radial dead zone: inside <paramref name="inner"/> nothing; beyond, the length is rescaled so it starts at 0
        /// and reaches 1 at <see cref="OuterEdge"/>; the direction is kept (no axis snapping).
        /// </summary>
        public static (float x, float y) Radial(float x, float y, float inner)
        {
            if (float.IsNaN(x) || float.IsNaN(y)) return (0f, 0f);
            float len = (float)Math.Sqrt(x * x + y * y);
            inner = Math.Max(0f, Math.Min(0.9f, inner));
            if (len <= inner || len < 1e-6f) return (0f, 0f);
            float scaled = Math.Min(1f, (len - inner) / (OuterEdge - inner));
            return (x / len * scaled, y / len * scaled);
        }

        /// <summary>A trigger with a dead zone at its rest end (0…1 rescaled).</summary>
        public static float Trigger(float v, float inner)
        {
            if (float.IsNaN(v) || v <= inner) return 0f;
            return Math.Min(1f, (v - inner) / Math.Max(1e-3f, 1f - inner));
        }

        /// <summary>The hero walks at this move-stick deflection (0 = no move input).</summary>
        public static bool Walks(float deflection) => deflection > 0f && deflection < WalkThreshold;

        /// <summary>The move direction of a stick: full length (the gait sets the speed, G3 has no in-between speeds).</summary>
        public static (float x, float y) MoveDirection(float x, float y)
        {
            float len = (float)Math.Sqrt(x * x + y * y);
            if (len < 1e-4f) return (0f, 0f);
            return (x / len, y / len);
        }

        /// <summary>Sign-keeping response curve.</summary>
        public static float Curve(float v, float exponent = CameraCurve)
        {
            float a = Math.Min(1f, Math.Abs(v));
            return Math.Sign(v) * (float)Math.Pow(a, exponent);
        }

        /// <summary>
        /// Camera turn (degrees right per second) and pitch (degrees up per second) for a dead-zoned right stick with the
        /// pad options: response curve, sensitivity (G3 slider semantics: <see cref="ControlOptions.SensitivityFactor"/>),
        /// invert per axis.
        /// </summary>
        public static (float yaw, float pitch) CameraRates(float x, float y, PadOptions options)
        {
            float yaw = Curve(x) * YawRate * ControlOptions.SensitivityFactor(options.SensitivityX) * (options.InvertX ? -1 : 1);
            float pitch = Curve(y) * PitchRate * ControlOptions.SensitivityFactor(options.SensitivityY) * (options.InvertY ? -1 : 1);
            return (yaw, pitch);
        }
    }
}

namespace G3.Player
{
    /// <summary>
    /// Vibration on hits (Docs/gamepad.md §5, optional): a light short pulse when the hero lands a hit, a stronger one when
    /// he is hit, the strongest for a knock-down or death; a parried hit is a short buzz on the high motor.
    /// </summary>
    public static class HitRumble
    {
        public readonly struct Pulse
        {
            public Pulse(float low, float high, float seconds)
            {
                Low = low;
                High = high;
                Seconds = seconds;
            }

            public float Low { get; }
            public float High { get; }
            public float Seconds { get; }
            public bool IsNone => Seconds <= 0f;
        }

        public static Pulse For(bool heroIsVictim, bool blocked, bool heavy, bool killed)
        {
            if (heroIsVictim)
            {
                if (killed || heavy) return new Pulse(0.9f, 0.7f, 0.35f);
                if (blocked) return new Pulse(0.15f, 0.45f, 0.10f);
                return new Pulse(0.55f, 0.35f, 0.18f);
            }
            if (blocked) return new Pulse(0.05f, 0.30f, 0.06f);
            if (killed) return new Pulse(0.35f, 0.45f, 0.14f);
            return new Pulse(0.15f, 0.30f, 0.08f);
        }
    }
}
