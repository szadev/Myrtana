using System;

namespace G3.Player
{
    /// <summary>
    /// One frame of a pad: the value of every <see cref="PadButton"/> (buttons 0/1, triggers 0…1, stick halves the
    /// positive part of the dead-zoned axis, <see cref="PadStick.Radial"/>). Filled by the Unity side.
    /// </summary>
    public sealed class PadSnapshot
    {
        public static readonly int Count = Enum.GetValues(typeof(PadButton)).Length;

        readonly float[] _values = new float[Count];

        public float this[PadButton b]
        {
            get => _values[(int)b];
            set => _values[(int)b] = b == PadButton.None || float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));
        }

        /// <summary>Sticks as vectors (x right, y up), already dead-zoned.</summary>
        public void SetSticks(float lx, float ly, float rx, float ry)
        {
            this[PadButton.LsRight] = Math.Max(0, lx);
            this[PadButton.LsLeft] = Math.Max(0, -lx);
            this[PadButton.LsUp] = Math.Max(0, ly);
            this[PadButton.LsDown] = Math.Max(0, -ly);
            this[PadButton.RsRight] = Math.Max(0, rx);
            this[PadButton.RsLeft] = Math.Max(0, -rx);
            this[PadButton.RsUp] = Math.Max(0, ry);
            this[PadButton.RsDown] = Math.Max(0, -ry);
        }

        public void Clear() => Array.Clear(_values, 0, _values.Length);

        public void CopyFrom(PadSnapshot other) => Array.Copy(other._values, _values, _values.Length);

        public static bool IsStick(PadButton b) => b >= PadButton.LsUp && b <= PadButton.RsRight;
        public static bool IsTrigger(PadButton b) => b == PadButton.LT || b == PadButton.RT;
    }

    /// <summary>
    /// The pad's binding logic, engine-free (Docs/gamepad.md §2/§3): each frame a <see cref="PadSnapshot"/> and the
    /// time go in, and every session key and <see cref="PadAction"/> has a held / pressed / released state and an analog
    /// value out. Buttons count as down at <see cref="PressThreshold"/> and up again below <see cref="ReleaseThreshold"/>
    /// (stick halves and triggers get the hysteresis). Gestures: press = the button; tap = a pulse (one frame down) on a
    /// release before <see cref="HoldSeconds"/>, or at once when the button has no hold binding and is no modifier;
    /// hold = down from <see cref="HoldSeconds"/> until release. Chords: a binding with <see cref="PadBinding.Chord"/>
    /// acts while <see cref="PadMap.Modifier"/> is held; a plain binding of a button that has a chord rests while it is
    /// held; the modifier's own tap is cancelled by any chord used meanwhile. With <c>active</c> false (a page or menu has
    /// the controls) everything is up.
    /// </summary>
    public sealed class PadResolver
    {
        public const float HoldSeconds = 0.4f;
        public const float ReleaseThreshold = 0.35f;
        /// <summary>Default press point of buttons, stick halves and triggers (the trigger option overrides it for LT/RT).</summary>
        public const float PressThreshold = 0.5f;

        static readonly int Keys = KeyMap.All.Length;
        static readonly int Actions = Enum.GetValues(typeof(PadAction)).Length;
        static readonly int Buttons = PadSnapshot.Count;

        readonly bool[] _buttonDown = new bool[Buttons];
        readonly double[] _downAt = new double[Buttons];
        readonly bool[] _holdFired = new bool[Buttons];
        readonly bool[] _chordAtPress = new bool[Buttons];
        readonly bool[] _blocked = new bool[Buttons];
        readonly bool[] _pressed = new bool[Buttons], _released = new bool[Buttons];
        bool _modifierUsed;

        readonly bool[] _keyDown = new bool[Keys], _keyWas = new bool[Keys];
        readonly float[] _keyAnalog = new float[Keys];
        readonly bool[] _actDown = new bool[Actions], _actWas = new bool[Actions];

        /// <summary>The press point of LT/RT (Gamepad options, 0.1…0.9).</summary>
        public float TriggerThreshold { get; set; } = PressThreshold;

        /// <summary>
        /// One frame. <paramref name="now"/> in seconds (unscaled), <paramref name="active"/> = the pad drives the hero now.
        /// </summary>
        public void Update(PadMap map, PadSnapshot pad, double now, bool active)
        {
            Array.Copy(_keyDown, _keyWas, Keys);
            Array.Copy(_actDown, _actWas, Actions);
            Array.Clear(_keyDown, 0, Keys);
            Array.Clear(_keyAnalog, 0, Keys);
            Array.Clear(_actDown, 0, Actions);

            // physical button edges (with hysteresis)
            var pressed = _pressed;
            var released = _released;
            Array.Clear(pressed, 0, Buttons);
            Array.Clear(released, 0, Buttons);
            for (int i = 1; i < Buttons; i++)
            {
                var b = (PadButton)i;
                float v = pad[b];
                float on = PadSnapshot.IsTrigger(b) ? TriggerThreshold : PressThreshold;
                float off = Math.Min(on, ReleaseThreshold);
                bool down = _buttonDown[i] ? v >= off : v >= on;
                if (down && !_buttonDown[i])
                {
                    pressed[i] = true;
                    _downAt[i] = now;
                    _holdFired[i] = false;
                }
                else if (!down && _buttonDown[i]) released[i] = true;
                _buttonDown[i] = down;
                if (!down) _blocked[i] = false;
            }
            bool modifier = _buttonDown[(int)PadMap.Modifier];
            int mod = (int)PadMap.Modifier;
            if (pressed[mod]) _modifierUsed = false;
            for (int i = 1; i < Buttons; i++)
            {
                if (!pressed[i] || i == mod) continue;
                _chordAtPress[i] = modifier;
                // another button with LB held is no LB tap (moving the left stick is, unless that half has a chord)
                if (modifier && (!PadSnapshot.IsStick((PadButton)i) || map.HasChord((PadButton)i))) _modifierUsed = true;
            }

            if (!active)
            {
                // a button held while the pages had the pad does nothing in play until released (B closing a page is no sneak)
                for (int i = 1; i < Buttons; i++) if (_buttonDown[i] && !PadSnapshot.IsStick((PadButton)i)) _blocked[i] = true;
                _modifierUsed = true;
                return;
            }

            foreach (var bind in map.Bindings)
            {
                int i = (int)bind.Button;
                if (i <= 0 || i >= Buttons || _blocked[i]) continue;
                bool isMod = i == mod;
                // the layer this binding lives on must be the one held (a press decides its layer when it starts)
                bool layerOk;
                if (isMod) layerOk = true;
                else if (bind.Chord) layerOk = bind.Gesture == PadGesture.Press ? modifier : _chordAtPress[i];
                else layerOk = !map.HasChord(bind.Button) ? true : bind.Gesture == PadGesture.Press ? !modifier : !_chordAtPress[i];
                if (!layerOk) continue;

                bool on = false;
                float analog = 0f;
                switch (bind.Gesture)
                {
                    case PadGesture.Press:
                        on = _buttonDown[i];
                        analog = on || PadSnapshot.IsStick(bind.Button) ? pad[bind.Button] : 0f;
                        break;
                    case PadGesture.Hold:
                        on = _buttonDown[i] && (_holdFired[i] || now - _downAt[i] >= HoldSeconds);
                        if (on) _holdFired[i] = true;
                        analog = on ? 1f : 0f;
                        break;
                    case PadGesture.Tap:
                        bool waits = isMod || map.HasHold(bind.Button, bind.Chord);
                        if (!waits) on = pressed[i];
                        else on = released[i] && !_holdFired[i] && now - _downAt[i] < HoldSeconds && !(isMod && _modifierUsed);
                        analog = on ? 1f : 0f;
                        break;
                }
                if (bind.Chord && (on || _buttonDown[i])) _modifierUsed = true;
                if (!on && analog <= 0f) continue;
                if (bind.IsAction)
                {
                    if (on) _actDown[(int)bind.Action] = true;
                    if (on) foreach (var k in PadMap.Expansion(bind.Action)) Set(k, true, 1f);
                }
                else Set(bind.Key, on, analog);
            }
            // a hold that fired keeps the tap of the same press from firing on release
            for (int i = 1; i < Buttons; i++)
                if (_buttonDown[i] && !_holdFired[i] && map.HasHold((PadButton)i, _chordAtPress[i]) && now - _downAt[i] >= HoldSeconds)
                    _holdFired[i] = true;
        }

        void Set(SessionKey key, bool on, float analog)
        {
            int k = (int)key;
            if (on) _keyDown[k] = true;
            if (analog > _keyAnalog[k]) _keyAnalog[k] = analog;
        }

        public bool IsDown(SessionKey key) => _keyDown[(int)key];
        public bool WasPressed(SessionKey key) => _keyDown[(int)key] && !_keyWas[(int)key];
        public bool WasReleased(SessionKey key) => !_keyDown[(int)key] && _keyWas[(int)key];

        /// <summary>The key's analog value 0…1 (stick deflection, trigger travel; 1 for a held button).</summary>
        public float Analog(SessionKey key) => _keyAnalog[(int)key];

        public bool IsDown(PadAction action) => _actDown[(int)action];
        public bool WasPressed(PadAction action) => _actDown[(int)action] && !_actWas[(int)action];

        /// <summary>A physical button is down (after the thresholds) — for the UI layer and the device tracker.</summary>
        public bool ButtonDown(PadButton b) => b != PadButton.None && _buttonDown[(int)b];

        /// <summary>Everything up at once (device lost, focus lost).</summary>
        public void Reset()
        {
            Array.Clear(_buttonDown, 0, _buttonDown.Length);
            Array.Clear(_blocked, 0, _blocked.Length);
            Array.Clear(_keyDown, 0, Keys);
            Array.Clear(_keyWas, 0, Keys);
            Array.Clear(_keyAnalog, 0, Keys);
            Array.Clear(_actDown, 0, Actions);
            Array.Clear(_actWas, 0, Actions);
            _modifierUsed = false;
        }
    }
}
