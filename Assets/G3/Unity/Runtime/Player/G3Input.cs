using System.Collections.Generic;
using G3.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace G3.Unity
{
    /// <summary>
    /// The session keys (<see cref="SessionKey"/>) on the Input System: reads the bindings of <see cref="Options"/>
    /// (DirectInput scan codes and G3 mouse offsets, <see cref="KeyMap"/>) every frame, so a rebinding in the Controls
    /// menu applies at once. Mouse axis halves (camera rotation) are analog and only read through <see cref="MouseTurn"/>
    /// and <see cref="MousePitch"/>; the wheel halves count as a press in the frame the wheel turns.
    /// Gameplay code asks this class instead of naming keys, so the default table and the player's map rule everywhere.
    /// </summary>
    public static class G3Input
    {
        static ControlOptions _options = new ControlOptions();

        /// <summary>The player's control options (set by the game at start from the stored options).</summary>
        public static ControlOptions Options
        {
            get => _options;
            set => _options = value ?? new ControlOptions();
        }

        public static KeyMap Keys => _options.Keys;

        static object _keyboardHolder;
        static int _resumeFrame = -1;

        /// <summary>
        /// The keyboard as the game sees it: null while a text field holds it (<see cref="HoldKeyboard"/>, the dev
        /// console) and in the frame it lets go, so the key that closed the field (Esc, F10) does not also reach the game.
        /// The device itself stays enabled: text input and the holder's own close keys keep working (fix-40: a disabled
        /// keyboard starved IMGUI under the Input System only setting and the console could not be closed).
        /// </summary>
        public static Keyboard GameKeyboard => _keyboardHolder != null || Time.frameCount <= _resumeFrame ? null : Keyboard.current;

        /// <summary>A text field takes the keyboard from the game until <see cref="ReleaseKeyboard"/>.</summary>
        public static void HoldKeyboard(object holder) => _keyboardHolder = holder;

        public static void ReleaseKeyboard(object holder)
        {
            if (_keyboardHolder != holder) return;
            _keyboardHolder = null;
            _resumeFrame = Time.frameCount;
        }

        public static bool KeyboardHeld => _keyboardHolder != null;

        /// <summary>Any bound input of the key is held: keyboard / mouse (axes excluded) or the pad (<see cref="G3Pad"/>).</summary>
        public static bool IsDown(SessionKey key) => KeysDown(key) || G3Pad.Resolver.IsDown(key);

        /// <summary>A bound keyboard or mouse input of the key is held (the pad left out; axes excluded).</summary>
        public static bool KeysDown(SessionKey key)
        {
            foreach (var input in Keys.Inputs(key))
                if (IsDown(input)) return true;
            return false;
        }

        /// <summary>A bound input of the key went down this frame (keyboard, mouse buttons and wheel, pad).</summary>
        public static bool WasPressed(SessionKey key) => KeysPressed(key) || G3Pad.Resolver.WasPressed(key);

        /// <summary>A bound keyboard or mouse input of the key went down this frame (the pad left out).</summary>
        public static bool KeysPressed(SessionKey key)
        {
            foreach (var input in Keys.Inputs(key))
                if (WasPressed(input)) return true;
            return false;
        }

        /// <summary>The pad's binding of the key went down this frame (pages that check their keys per input).</summary>
        public static bool PadPressed(SessionKey key) => G3Pad.Resolver.WasPressed(key);

        /// <summary>The key was let go this frame — by the keyboard/mouse or the pad — and nothing holds it any more.</summary>
        public static bool WasReleased(SessionKey key)
        {
            var pad = G3Pad.Resolver;
            if (pad.IsDown(key)) return false;
            bool released = pad.WasReleased(key);
            foreach (var input in Keys.Inputs(key))
            {
                var b = Button(input);
                if (b == null) continue;
                if (b.isPressed) return false;
                released |= b.wasReleasedThisFrame;
            }
            return released;
        }

        /// <summary>
        /// Held by a keyboard key, mouse button or pad button (no wheel): continuous actions such as zooming with Num + or
        /// LB + right stick.
        /// </summary>
        public static bool IsHeldByButton(SessionKey key)
        {
            if (G3Pad.Resolver.IsDown(key)) return true;
            foreach (var input in Keys.Inputs(key))
                if (!input.IsMouseAxis && !(input.Device == KeyDevice.Mouse && MouseOffset.IsWheel(input.Code)) && IsDown(input)) return true;
            return false;
        }

        /// <summary>Wheel notches of this frame on the wheel halves bound to the key (0 when none is bound).</summary>
        public static float WheelNotches(SessionKey key)
        {
            var mouse = Mouse.current;
            if (mouse == null) return 0;
            float y = mouse.scroll.ReadValue().y;
            if (y == 0) return 0;
            float notches = Mathf.Max(1f, Mathf.Abs(y) / 120f);
            float sum = 0;
            foreach (var input in Keys.Inputs(key))
            {
                if (input.Device != KeyDevice.Mouse) continue;
                if (input.Code == MouseOffset.WheelPlus && y > 0) sum += notches;
                if (input.Code == MouseOffset.WheelMinus && y < 0) sum += notches;
            }
            return sum;
        }

        /// <summary>Raw mouse movement bound to RotateCamRight/Left (pixels, + = right; G3 default: Mouse X+/X-).</summary>
        public static float MouseTurn() => AxisSum(SessionKey.RotateCamRight) - AxisSum(SessionKey.RotateCamLeft);

        /// <summary>Raw mouse movement bound to TurnUp/TurnDown (pixels, + = up; G3 default: Mouse Y-/Y+).</summary>
        public static float MousePitch() => AxisSum(SessionKey.TurnUp) - AxisSum(SessionKey.TurnDown);

        /// <summary>Keyboard turning: +1 right, −1 left, 0 (RotateCamRight/Left held by a key; G3 default E/Q). The pad's
        /// camera stick is <see cref="PadLookRates"/>.</summary>
        public static float KeyTurn() => (KeysDown(SessionKey.RotateCamRight) ? 1f : 0f) - (KeysDown(SessionKey.RotateCamLeft) ? 1f : 0f);

        /// <summary>Keyboard pitching: +1 up, −1 down (TurnUp/TurnDown bound to keys; none by default).</summary>
        public static float KeyPitch() => (KeysDown(SessionKey.TurnUp) ? 1f : 0f) - (KeysDown(SessionKey.TurnDown) ? 1f : 0f);

        /// <summary>
        /// The pad's move input (x right, y forward; length 0…1): the analog values of the movement keys' pad bindings
        /// (default the left stick; a d-pad binding gives 1).
        /// </summary>
        public static Vector2 PadMove()
        {
            var r = G3Pad.Resolver;
            var v = new Vector2(r.Analog(SessionKey.StrafeRight) - r.Analog(SessionKey.StrafeLeft),
                                r.Analog(SessionKey.Forward) - r.Analog(SessionKey.Backward));
            return Vector2.ClampMagnitude(v, 1f);
        }

        /// <summary>
        /// The pad's camera rates (degrees per second; x right, y up) from the camera keys' pad bindings (default the
        /// right stick) with the Gamepad options; Y inverted while looking behind when "Invert rear view Y-Axis" is on.
        /// </summary>
        public static Vector2 PadLookRates(bool lookingBehind = false)
        {
            var r = G3Pad.Resolver;
            float x = r.Analog(SessionKey.RotateCamRight) - r.Analog(SessionKey.RotateCamLeft);
            float y = r.Analog(SessionKey.TurnUp) - r.Analog(SessionKey.TurnDown);
            if (x == 0f && y == 0f) return Vector2.zero;
            var (yaw, pitch) = PadStick.CameraRates(x, y, Options.Pad);
            if (lookingBehind && Options.LookaroundInverse) pitch = -pitch;
            return new Vector2(yaw, pitch);
        }

        static float AxisSum(SessionKey key)
        {
            var mouse = Mouse.current;
            if (mouse == null) return 0;
            var d = mouse.delta.ReadValue();
            float sum = 0;
            foreach (var input in Keys.Inputs(key))
            {
                if (!input.IsMouseAxis) continue;
                switch (input.Code)
                {
                    case MouseOffset.XPlus: sum += Mathf.Max(0, d.x); break;
                    case MouseOffset.XMinus: sum += Mathf.Max(0, -d.x); break;
                    case MouseOffset.YPlus: sum += Mathf.Max(0, -d.y); break;  // DirectInput Y grows downwards
                    case MouseOffset.YMinus: sum += Mathf.Max(0, d.y); break;
                }
            }
            return sum;
        }

        public static bool IsDown(KeyInput input)
        {
            if (input.Device == KeyDevice.Mouse && MouseOffset.IsWheel(input.Code)) return WheelMoved(input.Code);
            var b = Button(input);
            return b != null && b.isPressed;
        }

        public static bool WasPressed(KeyInput input)
        {
            if (input.Device == KeyDevice.Mouse && MouseOffset.IsWheel(input.Code)) return WheelMoved(input.Code);
            var b = Button(input);
            return b != null && b.wasPressedThisFrame;
        }

        static bool WheelMoved(int code)
        {
            var mouse = Mouse.current;
            if (mouse == null) return false;
            float y = mouse.scroll.ReadValue().y;
            return code == MouseOffset.WheelPlus ? y > 0 : y < 0;
        }

        /// <summary>The button control of a key or mouse button input, or null (axes, wheel, unmapped codes, no device).</summary>
        public static ButtonControl Button(KeyInput input)
        {
            if (input.Device == KeyDevice.Keyboard)
            {
                var kb = GameKeyboard;
                return kb != null && UnityKey(input.Code) is Key k ? kb[k] : null;
            }
            if (input.Device == KeyDevice.Mouse)
            {
                var mouse = Mouse.current;
                if (mouse == null) return null;
                switch (input.Code)
                {
                    case MouseOffset.Left: return mouse.leftButton;
                    case MouseOffset.Right: return mouse.rightButton;
                    case MouseOffset.Middle: return mouse.middleButton;
                    case MouseOffset.Button3: return mouse.backButton;
                }
            }
            return null;
        }

        /// <summary>Input System control path of a key or mouse button ("&lt;Keyboard&gt;/space"), or null.</summary>
        public static string ControlPath(KeyInput input)
        {
            if (input.Device == KeyDevice.Keyboard)
                return UnityKey(input.Code) is Key k ? "<Keyboard>/" + KeyPathName(k) : null;
            if (input.Device == KeyDevice.Mouse)
                switch (input.Code)
                {
                    case MouseOffset.Left: return "<Mouse>/leftButton";
                    case MouseOffset.Right: return "<Mouse>/rightButton";
                    case MouseOffset.Middle: return "<Mouse>/middleButton";
                    case MouseOffset.Button3: return "<Mouse>/backButton";
                }
            return null;
        }

        /// <summary>
        /// Input System control path of the key's first plain press binding on the pad ("&lt;Gamepad&gt;/rightTrigger"),
        /// or null (no such binding, stick halves, taps and holds: those act on their own frame, not on the button's).
        /// </summary>
        public static string PadControlPath(SessionKey key)
        {
            foreach (var b in Options.Pad.Buttons.Of(key))
            {
                if (b.Chord || b.Gesture != PadGesture.Press) continue;
                string p = PadControlPath(b.Button);
                if (p != null) return p;
            }
            return null;
        }

        public static string PadControlPath(PadButton b)
        {
            switch (b)
            {
                case PadButton.A: return "<Gamepad>/buttonSouth";
                case PadButton.B: return "<Gamepad>/buttonEast";
                case PadButton.X: return "<Gamepad>/buttonWest";
                case PadButton.Y: return "<Gamepad>/buttonNorth";
                case PadButton.LB: return "<Gamepad>/leftShoulder";
                case PadButton.RB: return "<Gamepad>/rightShoulder";
                case PadButton.LT: return "<Gamepad>/leftTrigger";
                case PadButton.RT: return "<Gamepad>/rightTrigger";
                case PadButton.L3: return "<Gamepad>/leftStickPress";
                case PadButton.R3: return "<Gamepad>/rightStickPress";
                case PadButton.Start: return "<Gamepad>/start";
                case PadButton.View: return "<Gamepad>/select";
                case PadButton.DUp: return "<Gamepad>/dpad/up";
                case PadButton.DDown: return "<Gamepad>/dpad/down";
                case PadButton.DLeft: return "<Gamepad>/dpad/left";
                case PadButton.DRight: return "<Gamepad>/dpad/right";
                default: return null;
            }
        }

        static string KeyPathName(Key k)
        {
            var kb = Keyboard.current;
            if (kb != null) return kb[k].name;
            string s = k.ToString();
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>
        /// The first key, mouse button or wheel half pressed this frame (for the custom key list), Escape excluded (it
        /// cancels the wait). False when nothing was pressed.
        /// </summary>
        public static bool TryCapture(out KeyInput input)
        {
            input = KeyInput.None;
            var kb = GameKeyboard;
            if (kb != null && kb.anyKey.wasPressedThisFrame)
                foreach (var kv in DikToKey)
                    if (kv.Value != Key.Escape && kb[kv.Value].wasPressedThisFrame)
                    {
                        input = KeyInput.Key(kv.Key);
                        return true;
                    }
            var mouse = Mouse.current;
            if (mouse == null) return false;
            if (mouse.leftButton.wasPressedThisFrame) input = KeyInput.Mouse(MouseOffset.Left);
            else if (mouse.rightButton.wasPressedThisFrame) input = KeyInput.Mouse(MouseOffset.Right);
            else if (mouse.middleButton.wasPressedThisFrame) input = KeyInput.Mouse(MouseOffset.Middle);
            else if (mouse.backButton.wasPressedThisFrame) input = KeyInput.Mouse(MouseOffset.Button3);
            else
            {
                float y = mouse.scroll.ReadValue().y;
                if (y > 0) input = KeyInput.Mouse(MouseOffset.WheelPlus);
                else if (y < 0) input = KeyInput.Mouse(MouseOffset.WheelMinus);
            }
            return !input.IsNone;
        }

        /// <summary>The Input System key of a DirectInput scan code (both are physical US-layout positions), or null.</summary>
        public static Key? UnityKey(int dik) => DikToKey.TryGetValue(dik, out var k) ? k : (Key?)null;

        /// <summary>The DirectInput scan code of an Input System key, or 0.</summary>
        public static int DikOf(Key key)
        {
            foreach (var kv in DikToKey) if (kv.Value == key) return kv.Key;
            return 0;
        }

        static readonly Dictionary<int, Key> DikToKey = BuildMap();

        static Dictionary<int, Key> BuildMap()
        {
            var m = new Dictionary<int, Key>
            {
                [0x01] = Key.Escape, [0x0C] = Key.Minus, [0x0D] = Key.Equals, [0x0E] = Key.Backspace, [0x0F] = Key.Tab,
                [0x1A] = Key.LeftBracket, [0x1B] = Key.RightBracket, [0x1C] = Key.Enter, [0x1D] = Key.LeftCtrl,
                [0x27] = Key.Semicolon, [0x28] = Key.Quote, [0x29] = Key.Backquote, [0x2A] = Key.LeftShift, [0x2B] = Key.Backslash,
                [0x33] = Key.Comma, [0x34] = Key.Period, [0x35] = Key.Slash, [0x36] = Key.RightShift, [0x37] = Key.NumpadMultiply,
                [0x38] = Key.LeftAlt, [0x39] = Key.Space, [0x3A] = Key.CapsLock, [0x45] = Key.NumLock, [0x46] = Key.ScrollLock,
                [0x47] = Key.Numpad7, [0x48] = Key.Numpad8, [0x49] = Key.Numpad9, [0x4A] = Key.NumpadMinus, [0x4B] = Key.Numpad4,
                [0x4C] = Key.Numpad5, [0x4D] = Key.Numpad6, [0x4E] = Key.NumpadPlus, [0x4F] = Key.Numpad1, [0x50] = Key.Numpad2,
                [0x51] = Key.Numpad3, [0x52] = Key.Numpad0, [0x53] = Key.NumpadPeriod, [0x57] = Key.F11, [0x58] = Key.F12,
                [0x9C] = Key.NumpadEnter, [0x9D] = Key.RightCtrl, [0xB5] = Key.NumpadDivide, [0xB7] = Key.PrintScreen,
                [0xB8] = Key.RightAlt, [0xC5] = Key.Pause, [0xC7] = Key.Home, [0xC8] = Key.UpArrow, [0xC9] = Key.PageUp,
                [0xCB] = Key.LeftArrow, [0xCD] = Key.RightArrow, [0xCF] = Key.End, [0xD0] = Key.DownArrow, [0xD1] = Key.PageDown,
                [0xD2] = Key.Insert, [0xD3] = Key.Delete, [0xDB] = Key.LeftMeta, [0xDC] = Key.RightMeta, [0xDD] = Key.ContextMenu,
            };
            Key[] digits = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0 };
            for (int i = 0; i < digits.Length; i++) m[0x02 + i] = digits[i];
            Key[] row1 = { Key.Q, Key.W, Key.E, Key.R, Key.T, Key.Y, Key.U, Key.I, Key.O, Key.P };
            Key[] row2 = { Key.A, Key.S, Key.D, Key.F, Key.G, Key.H, Key.J, Key.K, Key.L };
            Key[] row3 = { Key.Z, Key.X, Key.C, Key.V, Key.B, Key.N, Key.M };
            for (int i = 0; i < row1.Length; i++) m[0x10 + i] = row1[i];
            for (int i = 0; i < row2.Length; i++) m[0x1E + i] = row2[i];
            for (int i = 0; i < row3.Length; i++) m[0x2C + i] = row3[i];
            for (int i = 0; i < 10; i++) m[0x3B + i] = Key.F1 + i;
            return m;
        }
    }
}
