using System;

namespace G3.Player
{
    /// <summary>Which device family the prompts show (Docs/gamepad.md §5).</summary>
    public enum PadFamily : byte { Xbox, PlayStation, Nintendo, Generic }

    /// <summary>The device the player used last: prompts, hint bar and the menu pointer follow it.</summary>
    public enum ActiveInput : byte { KeyboardMouse, Pad }

    /// <summary>
    /// Last-used device: any keyboard/mouse activity makes it keyboard and mouse, any pad activity the pad; with both in
    /// one frame the pad wins (a mouse resting on a vibrating desk must not steal it back). Raises <see cref="Changed"/>.
    /// </summary>
    public sealed class InputDeviceTracker
    {
        public ActiveInput Current { get; private set; } = ActiveInput.KeyboardMouse;

        public event Action<ActiveInput> Changed;

        public void Note(bool keyboardMouse, bool pad)
        {
            var next = pad ? ActiveInput.Pad : keyboardMouse ? ActiveInput.KeyboardMouse : Current;
            if (next == Current) return;
            Current = next;
            Changed?.Invoke(next);
        }

        /// <summary>The pad left (unplugged): back to keyboard and mouse.</summary>
        public void PadLost() => Note(true, false);

        /// <summary>Mouse movement that counts as using the mouse (pixels per frame).</summary>
        public const float MouseMoveThreshold = 3f;
    }

    /// <summary>Button names of each pad family for text prompts and the drawn glyphs.</summary>
    public static class PadGlyphs
    {
        /// <summary>
        /// The family of a device from its Input System layout and product name: DualShock / DualSense → PlayStation,
        /// Switch → Nintendo, a plain joystick → Generic, everything else (XInput, Steam Deck, generic HID pads) → Xbox.
        /// </summary>
        public static PadFamily FamilyOf(string layout, string product, bool isJoystick = false)
        {
            string s = ((layout ?? "") + " " + (product ?? "")).ToLowerInvariant();
            if (s.Contains("dualshock") || s.Contains("dualsense") || s.Contains("playstation") || s.Contains("wireless controller") ||
                s.Contains("ps4") || s.Contains("ps5")) return PadFamily.PlayStation;
            if (s.Contains("switch") || s.Contains("nintendo") || s.Contains("joy-con")) return PadFamily.Nintendo;
            if (isJoystick) return PadFamily.Generic;
            return PadFamily.Xbox;
        }

        /// <summary>Short text name ("A", "Cross", "LB", "L1").</summary>
        public static string Name(PadButton b, PadFamily family)
        {
            switch (family)
            {
                case PadFamily.PlayStation:
                    switch (b)
                    {
                        case PadButton.A: return "Cross";
                        case PadButton.B: return "Circle";
                        case PadButton.X: return "Square";
                        case PadButton.Y: return "Triangle";
                        case PadButton.LB: return "L1";
                        case PadButton.RB: return "R1";
                        case PadButton.LT: return "L2";
                        case PadButton.RT: return "R2";
                        case PadButton.Start: return "Options";
                        case PadButton.View: return "Share";
                    }
                    break;
                case PadFamily.Nintendo:
                    switch (b)
                    {
                        // positions as on the Switch: south B, east A, west Y, north X
                        case PadButton.A: return "B";
                        case PadButton.B: return "A";
                        case PadButton.X: return "Y";
                        case PadButton.Y: return "X";
                        case PadButton.LB: return "L";
                        case PadButton.RB: return "R";
                        case PadButton.LT: return "ZL";
                        case PadButton.RT: return "ZR";
                        case PadButton.Start: return "+";
                        case PadButton.View: return "-";
                    }
                    break;
                case PadFamily.Generic:
                    switch (b)
                    {
                        case PadButton.A: return "1";
                        case PadButton.B: return "2";
                        case PadButton.X: return "3";
                        case PadButton.Y: return "4";
                        case PadButton.LB: return "5";
                        case PadButton.RB: return "6";
                        case PadButton.LT: return "7";
                        case PadButton.RT: return "8";
                        case PadButton.View: return "9";
                        case PadButton.Start: return "10";
                        case PadButton.L3: return "11";
                        case PadButton.R3: return "12";
                    }
                    break;
            }
            switch (b)
            {
                case PadButton.A: return "A";
                case PadButton.B: return "B";
                case PadButton.X: return "X";
                case PadButton.Y: return "Y";
                case PadButton.LB: return "LB";
                case PadButton.RB: return "RB";
                case PadButton.LT: return "LT";
                case PadButton.RT: return "RT";
                case PadButton.Start: return "Menu";
                case PadButton.View: return "View";
                case PadButton.L3: return family == PadFamily.Nintendo ? "LS" : family == PadFamily.PlayStation ? "L3" : "LS";
                case PadButton.R3: return family == PadFamily.Nintendo ? "RS" : family == PadFamily.PlayStation ? "R3" : "RS";
                case PadButton.DUp: return "D-Up";
                case PadButton.DDown: return "D-Down";
                case PadButton.DLeft: return "D-Left";
                case PadButton.DRight: return "D-Right";
                case PadButton.LsUp: return "LS-Up";
                case PadButton.LsDown: return "LS-Down";
                case PadButton.LsLeft: return "LS-Left";
                case PadButton.LsRight: return "LS-Right";
                case PadButton.RsUp: return "RS-Up";
                case PadButton.RsDown: return "RS-Down";
                case PadButton.RsLeft: return "RS-Left";
                case PadButton.RsRight: return "RS-Right";
                default: return "";
            }
        }

        /// <summary>A binding as text: "A", "LB + Y", "View (hold)".</summary>
        public static string Name(PadBinding binding, PadFamily family, bool russian = false)
        {
            if (binding.Button == PadButton.None) return "";
            string s = Name(binding.Button, family);
            if (binding.Chord) s = Name(PadMap.Modifier, family) + " + " + s;
            if (binding.Gesture == PadGesture.Hold) s += russian ? " (удерж.)" : " (hold)";
            return s;
        }

        /// <summary>
        /// The Xbox face colour of a face button (RGB 0…1) for the drawn glyph; PlayStation shapes use their own colours.
        /// </summary>
        public static (float r, float g, float b) FaceColour(PadButton b, PadFamily family)
        {
            if (family == PadFamily.PlayStation)
                switch (b)
                {
                    case PadButton.A: return (0.49f, 0.70f, 0.91f);  // cross: blue
                    case PadButton.B: return (1.00f, 0.40f, 0.40f);  // circle: red
                    case PadButton.X: return (0.84f, 0.57f, 0.80f);  // square: pink
                    case PadButton.Y: return (0.25f, 0.81f, 0.70f);  // triangle: green
                }
            else if (family == PadFamily.Xbox)
                switch (b)
                {
                    case PadButton.A: return (0.38f, 0.73f, 0.27f);
                    case PadButton.B: return (0.86f, 0.24f, 0.20f);
                    case PadButton.X: return (0.20f, 0.47f, 0.84f);
                    case PadButton.Y: return (0.95f, 0.75f, 0.15f);
                }
            return (0.85f, 0.85f, 0.85f);
        }
    }

    /// <summary>The pad's cycling quick bar (Docs/gamepad.md §3): a highlight over the slots that LB/RB move round.</summary>
    public sealed class QuickSlotCursor
    {
        readonly int _count;

        public QuickSlotCursor(int count) => _count = Math.Max(1, count);

        /// <summary>The highlighted slot (0…count−1).</summary>
        public int Slot { get; private set; }

        /// <summary>Moves by <paramref name="step"/> (±1) round the bar; with <paramref name="filled"/> it skips empty slots
        /// (stays put when all are empty).</summary>
        public int Move(int step, Func<int, bool> filled = null)
        {
            int s = Slot;
            for (int i = 0; i < _count; i++)
            {
                s = ((s + step) % _count + _count) % _count;
                if (filled == null || filled(s))
                {
                    Slot = s;
                    return Slot;
                }
            }
            return Slot;
        }

        public void Set(int slot) => Slot = Math.Max(0, Math.Min(_count - 1, slot));
    }

    /// <summary>
    /// The journal pages the pad's View button cycles on an open page (Docs/gamepad.md §4): Inventory → Character → Spell
    /// book → Quest log → Map → Inventory.
    /// </summary>
    public static class JournalCycle
    {
        public static readonly SessionKey[] Order =
        {
            SessionKey.InventoryMode, SessionKey.JournalModeCharScreen, SessionKey.JournalModeMagBook,
            SessionKey.JournalModeLog, SessionKey.JournalModeMap,
        };

        /// <summary>The page after <paramref name="open"/> (the first when the open page is none of them).</summary>
        public static SessionKey Next(SessionKey? open, int step = 1)
        {
            int i = open.HasValue ? Array.IndexOf(Order, open.Value) : -1;
            if (i < 0) return Order[step >= 0 ? 0 : Order.Length - 1];
            return Order[((i + step) % Order.Length + Order.Length) % Order.Length];
        }
    }
}
