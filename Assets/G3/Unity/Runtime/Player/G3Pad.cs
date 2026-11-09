using System;
using G3.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace G3.Unity
{
    /// <summary>
    /// The gamepad on the Input System (Docs/gamepad.md §2): once per frame (before every other script) it reads
    /// <see cref="Gamepad.current"/> — XInput, DualShock/DualSense, Switch Pro, generic HID pads, Steam Deck — or, without
    /// one, <see cref="Joystick.current"/> (plain DirectInput sticks: stick, hat, buttons by index) into a
    /// <see cref="PadSnapshot"/>, runs the <see cref="PadResolver"/> with the player's <see cref="PadMap"/>, tracks the
    /// last-used device and drives the UI commands and vibration. <see cref="G3Input"/> answers session keys from it.
    /// </summary>
    [DefaultExecutionOrder(-30000)]
    public sealed class G3Pad : MonoBehaviour
    {
        static readonly PadSnapshot _pad = new PadSnapshot(), _prev = new PadSnapshot();
        static readonly PadResolver _resolver = new PadResolver();
        static readonly InputDeviceTracker _tracker = new InputDeviceTracker();
        static readonly UiNavRepeat _repeat = new UiNavRepeat();
        static int _frame = -1;
        static NavDir _uiMove;
        static Vector2 _left, _right;
        static UnityEngine.InputSystem.InputDevice _device;
        static float _rumbleUntil;
        static bool _rumbling;
        static G3Pad _instance;

        /// <summary>The pad drives the hero now (else the pages have it). Set by the game; default: the game has the cursor.</summary>
        public static Func<bool> GameplayActive = () => G3Cursor.Captured;

        /// <summary>The resolver of this frame (session keys and pad actions).</summary>
        public static PadResolver Resolver
        {
            get
            {
                Poll();
                return _resolver;
            }
        }

        /// <summary>The device the player used last.</summary>
        public static ActiveInput LastInput
        {
            get
            {
                Poll();
                return _tracker.Current;
            }
        }

        public static bool PadIsLast => LastInput == ActiveInput.Pad;

        /// <summary>Raised when the last-used device changes (prompts, hint bar, pointer).</summary>
        public static event Action<ActiveInput> InputChanged;

        /// <summary>A pad or joystick is connected.</summary>
        public static bool Connected => Gamepad.current != null || Joystick.current != null;

        /// <summary>The family of the connected pad (glyphs).</summary>
        public static PadFamily Family { get; private set; } = PadFamily.Xbox;

        /// <summary>Dead-zoned sticks of this frame (x right, y up).</summary>
        public static Vector2 LeftStick
        {
            get
            {
                Poll();
                return _left;
            }
        }

        public static Vector2 RightStick
        {
            get
            {
                Poll();
                return _right;
            }
        }

        static PadOptions Options => G3Input.Options.Pad;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (_instance != null) return;
            var go = new GameObject("G3Pad") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<G3Pad>();
            _tracker.Changed += d => InputChanged?.Invoke(d);
        }

        void Update()
        {
            Poll();
            if (_rumbling && (Time.unscaledTime >= _rumbleUntil || Time.timeScale == 0f)) StopRumble();
        }

        void OnApplicationFocus(bool focus)
        {
            if (focus) return;
            StopRumble();
            _resolver.Reset();
        }

        void OnDestroy()
        {
            StopRumble();
            if (_instance == this) _instance = null;
        }

        /// <summary>Reads the pad for this frame (once; every query calls it).</summary>
        public static void Poll()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _prev.CopyFrom(_pad);
            _pad.Clear();
            var o = Options;
            var gp = Gamepad.current;
            var js = gp == null ? Joystick.current : null;
            UnityEngine.InputSystem.InputDevice device = gp != null ? gp : js;
            if (device != _device)
            {
                _device = device;
                _resolver.Reset();
                _repeat.Reset();
                if (device != null) Family = PadGlyphs.FamilyOf(device.layout, device.description.product, device is Joystick);
                else _tracker.PadLost();
            }
            _left = _right = Vector2.zero;
            if (gp != null) ReadGamepad(gp, o.DeadZone);
            else if (js != null) ReadJoystick(js, o.DeadZone);

            bool padActivity = false;
            for (int i = 1; i < PadSnapshot.Count; i++)
            {
                var b = (PadButton)i;
                if (_pad[b] >= 0.5f && _prev[b] < 0.5f) padActivity = true;
            }
            padActivity |= _left.sqrMagnitude > 0.09f || _right.sqrMagnitude > 0.09f;
            _tracker.Note(KeyboardMouseActivity(), padActivity);

            bool active = device != null && (GameplayActive == null || GameplayActive());
            _resolver.TriggerThreshold = o.TriggerThreshold;
            _resolver.Update(o.Buttons, _pad, Time.unscaledTimeAsDouble, active);

            var dir = Down(PadButton.DUp) ? NavDir.Up : Down(PadButton.DDown) ? NavDir.Down :
                      Down(PadButton.DLeft) ? NavDir.Left : Down(PadButton.DRight) ? NavDir.Right :
                      UiNavRepeat.StickDir(_left.x, _left.y);
            _uiMove = _repeat.Update(dir, Time.unscaledTimeAsDouble);
        }

        static void ReadGamepad(Gamepad gp, float deadZone)
        {
            var (lx, ly) = PadStick.Radial(gp.leftStick.x.ReadValue(), gp.leftStick.y.ReadValue(), deadZone);
            var (rx, ry) = PadStick.Radial(gp.rightStick.x.ReadValue(), gp.rightStick.y.ReadValue(), deadZone);
            _left = new Vector2(lx, ly);
            _right = new Vector2(rx, ry);
            _pad.SetSticks(lx, ly, rx, ry);
            _pad[PadButton.A] = gp.buttonSouth.ReadValue();
            _pad[PadButton.B] = gp.buttonEast.ReadValue();
            _pad[PadButton.X] = gp.buttonWest.ReadValue();
            _pad[PadButton.Y] = gp.buttonNorth.ReadValue();
            _pad[PadButton.LB] = gp.leftShoulder.ReadValue();
            _pad[PadButton.RB] = gp.rightShoulder.ReadValue();
            _pad[PadButton.LT] = PadStick.Trigger(gp.leftTrigger.ReadValue(), 0.05f);
            _pad[PadButton.RT] = PadStick.Trigger(gp.rightTrigger.ReadValue(), 0.05f);
            _pad[PadButton.L3] = gp.leftStickButton.ReadValue();
            _pad[PadButton.R3] = gp.rightStickButton.ReadValue();
            _pad[PadButton.Start] = gp.startButton.ReadValue();
            _pad[PadButton.View] = gp.selectButton.ReadValue();
            _pad[PadButton.DUp] = gp.dpad.up.ReadValue();
            _pad[PadButton.DDown] = gp.dpad.down.ReadValue();
            _pad[PadButton.DLeft] = gp.dpad.left.ReadValue();
            _pad[PadButton.DRight] = gp.dpad.right.ReadValue();
        }

        static readonly PadButton[] JoystickOrder =
        {
            PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LB, PadButton.RB, PadButton.LT, PadButton.RT,
            PadButton.View, PadButton.Start, PadButton.L3, PadButton.R3,
        };

        /// <summary>A DirectInput joystick without a gamepad layout: stick, hat, the buttons by index, z/rz as a right stick.</summary>
        static void ReadJoystick(Joystick js, float deadZone)
        {
            var stick = js.stick;
            float sx = stick != null ? stick.x.ReadValue() : 0f, sy = stick != null ? stick.y.ReadValue() : 0f;
            var (lx, ly) = PadStick.Radial(sx, sy, deadZone);
            float zx = Axis(js, "z"), zy = Axis(js, "rz");
            var (rx, ry) = PadStick.Radial(zx, -zy, deadZone);
            _left = new Vector2(lx, ly);
            _right = new Vector2(rx, ry);
            _pad.SetSticks(lx, ly, rx, ry);
            var hat = js.TryGetChildControl<DpadControl>("hat");
            if (hat != null)
            {
                _pad[PadButton.DUp] = hat.up.ReadValue();
                _pad[PadButton.DDown] = hat.down.ReadValue();
                _pad[PadButton.DLeft] = hat.left.ReadValue();
                _pad[PadButton.DRight] = hat.right.ReadValue();
            }
            int n = 0;
            foreach (var c in js.allControls)
            {
                if (!(c is ButtonControl b) || c.parent is DpadControl || c.parent is StickControl || c is DpadControl) continue;
                if (c.parent != js) continue;
                if (n >= JoystickOrder.Length) break;
                _pad[JoystickOrder[n++]] = b.ReadValue();
            }
        }

        static float Axis(Joystick js, string name)
        {
            var a = js.TryGetChildControl<AxisControl>(name);
            return a != null ? a.ReadValue() : 0f;
        }

        static bool KeyboardMouseActivity()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var mouse = Mouse.current;
            if (mouse == null) return false;
            if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame) return true;
            if (mouse.scroll.ReadValue().y != 0f) return true;
            return mouse.delta.ReadValue().sqrMagnitude > InputDeviceTracker.MouseMoveThreshold * InputDeviceTracker.MouseMoveThreshold;
        }

        // ------------------------------------------------------------------------------------------------ raw buttons

        /// <summary>The physical button is held (after the thresholds).</summary>
        public static bool Down(PadButton b)
        {
            Poll();
            return _pad[b] >= 0.5f;
        }

        /// <summary>The physical button went down this frame.</summary>
        public static bool Pressed(PadButton b)
        {
            Poll();
            return _pad[b] >= 0.5f && _prev[b] < 0.5f;
        }

        /// <summary>Any face, shoulder, trigger or menu button went down this frame (skip videos, recapture the window).</summary>
        public static bool AnyPressed
        {
            get
            {
                for (var b = PadButton.A; b <= PadButton.View; b++) if (Pressed(b)) return true;
                return false;
            }
        }

        /// <summary>Any face, shoulder, trigger or menu button is held (fix-233: the menu waits until the skip is let go).</summary>
        public static bool AnyDown
        {
            get
            {
                for (var b = PadButton.A; b <= PadButton.View; b++) if (Down(b)) return true;
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------------ UI commands

        /// <summary>A UI command of this frame (Docs/gamepad.md §4); directions repeat while held.</summary>
        public static bool Ui(UiCommand c)
        {
            Poll();
            if (_device == null) return false;
            switch (c)
            {
                case UiCommand.Up: return _uiMove == NavDir.Up;
                case UiCommand.Down: return _uiMove == NavDir.Down;
                case UiCommand.Left: return _uiMove == NavDir.Left;
                case UiCommand.Right: return _uiMove == NavDir.Right;
                case UiCommand.Accept: return Pressed(PadButton.A);
                case UiCommand.Back: return Pressed(PadButton.B);
                case UiCommand.Secondary: return Pressed(PadButton.X);
                case UiCommand.Tertiary: return Pressed(PadButton.Y);
                case UiCommand.TabPrev: return Pressed(PadButton.LB);
                case UiCommand.TabNext: return Pressed(PadButton.RB);
                case UiCommand.PanePrev: return Pressed(PadButton.LT);
                case UiCommand.PaneNext: return Pressed(PadButton.RT);
                case UiCommand.Menu: return Pressed(PadButton.Start);
                case UiCommand.View: return Pressed(PadButton.View);
                default: return false;
            }
        }

        /// <summary>The UI move of this frame (None when nothing is due).</summary>
        public static NavDir UiMove
        {
            get
            {
                Poll();
                return _device == null ? NavDir.None : _uiMove;
            }
        }

        // ------------------------------------------------------------------------------------------------ vibration

        /// <summary>
        /// A rumble pulse (motor speeds 0…1) for <paramref name="seconds"/> (unscaled) on the current pad, when the option
        /// is on and the pad is the device in use. A stronger pulse replaces a weaker running one.
        /// </summary>
        public static void Rumble(float low, float high, float seconds)
        {
            var gp = Gamepad.current;
            if (gp == null || !Options.Vibration || _tracker.Current != ActiveInput.Pad) return;
            gp.SetMotorSpeeds(Mathf.Clamp01(low), Mathf.Clamp01(high));
            _rumbling = true;
            _rumbleUntil = Mathf.Max(_rumbleUntil, Time.unscaledTime + Mathf.Max(0.02f, seconds));
        }

        public static void StopRumble()
        {
            if (!_rumbling) return;
            _rumbling = false;
            _rumbleUntil = 0f;
            Gamepad.current?.ResetHaptics();
        }
    }
}
