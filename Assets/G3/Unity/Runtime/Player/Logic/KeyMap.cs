using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace G3.Player
{
    /// <summary>
    /// The G3 session keys (gESessionKey; "SessionKey.&lt;name&gt;" in the custom key list, text "HUD_SessionKey_&lt;name&gt;"),
    /// in the order of the custom key list (gCHUDMenuOptionsControlsCustom2). "Abbrechen" (cancel) has no row in the
    /// list and is not bound by default in a usable way (DIK 0xDF), so it is left out.
    /// </summary>
    public enum SessionKey
    {
        Forward, Backward, StrafeLeft, StrafeRight, TurnUp, TurnDown, RotateCamRight, RotateCamLeft,
        /// <summary>"Gehen" — hold: sprint while moving (text "Walk / Sprint", OnPlayerWalk).</summary>
        Walk,
        /// <summary>"Gehen Toggle" — permanent walk.</summary>
        WalkToggle,
        /// <summary>"Springen" — jump.</summary>
        Up,
        /// <summary>"Ducken" — sneak toggle.</summary>
        Down,
        /// <summary>"Aktion 1" — use / attack.</summary>
        Use1,
        /// <summary>"Aktion 2" — CP quick loot / parade.</summary>
        Use2,
        /// <summary>"Zoomen +" — raise the camera distance.</summary>
        Plus,
        /// <summary>"Zoomen -" — reduce the camera distance.</summary>
        Minus,
        QuickUse0, QuickUse1, QuickUse2, QuickUse3, QuickUse4, QuickUse5, QuickUse6, QuickUse7, QuickUse8, QuickUse9,
        /// <summary>"Bestätigen" — confirm (CP: change back from an animal form).</summary>
        Confirm,
        InventoryMode, JournalModeLog, JournalModeCharScreen, JournalModeMagBook, JournalModeMap,
        QuickLoad, QuickSave,
        /// <summary>"Waffe ziehen" — draw / holster.</summary>
        WeaponMode,
        /// <summary>"Target locken" (bound, but the player states never lock a target; Docs/combat.md §9.2).</summary>
        Lock,
        /// <summary>"Camera Look" — look behind while held.</summary>
        Look,
        FirstPerson, ResetCamera, ToggleHUD,
        /// <summary>CP 1.75: take everything on the loot page.</summary>
        TakeAll,
        /// <summary>CP 1.75: drink a healing / mana / endurance potion.</summary>
        UsePotionHP, UsePotionMP, UsePotionSP,
        /// <summary>
        /// Port key, not G3 (fix-205, [owner] QoL): held with "Aktion 1" it takes everything from the focused body, sleeper
        /// or chest without the loot page. A modifier (<see cref="KeyMap.IsModifier"/>): it may share its inputs with a
        /// normal key (default Ctrl, which is also Sneak).
        /// </summary>
        QuickLootModifier,
    }

    /// <summary>The device of a <see cref="KeyInput"/> (G3 "KeyN.Type": 0 keyboard, 1 mouse).</summary>
    public enum KeyDevice : byte { None, Keyboard, Mouse }

    /// <summary>
    /// One physical input as G3 stores it: a DirectInput keyboard scan code (DIK_*) or an engine mouse offset
    /// (<see cref="MouseOffset"/>). Engine-free; the Unity side maps the codes to the Input System.
    /// </summary>
    public readonly struct KeyInput : IEquatable<KeyInput>
    {
        public KeyInput(KeyDevice device, int code)
        {
            Device = code == 0 ? KeyDevice.None : device;
            Code = Device == KeyDevice.None ? 0 : code;
        }

        public KeyDevice Device { get; }
        public int Code { get; }
        public bool IsNone => Device == KeyDevice.None;
        public bool IsMouseAxis => Device == KeyDevice.Mouse && MouseOffset.IsAxis(Code);

        public static readonly KeyInput None = default;
        public static KeyInput Key(int dik) => new KeyInput(KeyDevice.Keyboard, dik);
        public static KeyInput Mouse(int offset) => new KeyInput(KeyDevice.Mouse, offset);

        /// <summary>Display name: the DirectInput key name or the engine's mouse name ("Mouse 0", "Mouse X+").</summary>
        public string Name => Device == KeyDevice.Keyboard ? Dik.Name(Code) : Device == KeyDevice.Mouse ? MouseOffset.Name(Code) : "";

        public bool Equals(KeyInput other) => Device == other.Device && Code == other.Code;
        public override bool Equals(object obj) => obj is KeyInput k && Equals(k);
        public override int GetHashCode() => ((int)Device << 16) ^ Code;
        public static bool operator ==(KeyInput a, KeyInput b) => a.Equals(b);
        public static bool operator !=(KeyInput a, KeyInput b) => !a.Equals(b);
        public override string ToString() => IsNone ? "-" : $"{(Device == KeyDevice.Mouse ? 1 : 0)}:{Code.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>Parses <see cref="ToString"/> ("0:57", "1:3", "-"); false for anything else.</summary>
        public static bool TryParse(string s, out KeyInput input)
        {
            input = None;
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return false;
            if (s == "-") return true;
            int colon = s.IndexOf(':');
            if (colon <= 0) return false;
            if (!int.TryParse(s.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int type) ||
                !int.TryParse(s.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) return false;
            if (type == 0 && code > 0 && code < 256) { input = Key(code); return true; }
            if (type == 1 && code >= MouseOffset.Left && code <= MouseOffset.WheelMinus) { input = Mouse(code); return true; }
            return false;
        }
    }

    /// <summary>
    /// eCInpShared::eEMouseOffset as used by the session keys (names from Engine.dll: "Mouse X", "Mouse Y", "Mouse Wheel",
    /// "Mouse 0".."Mouse 2", "Mouse X+" …). The axis halves turn mouse movement into a key (camera rotation).
    /// </summary>
    public static class MouseOffset
    {
        public const int Left = 3, Right = 4, Middle = 5, Button3 = 6, XPlus = 7, XMinus = 8, YPlus = 9, YMinus = 10,
                         WheelPlus = 11, WheelMinus = 12;

        public static bool IsAxis(int code) => code >= XPlus && code <= YMinus;
        public static bool IsWheel(int code) => code == WheelPlus || code == WheelMinus;

        public static string Name(int code)
        {
            switch (code)
            {
                case Left: return "Mouse 0";
                case Right: return "Mouse 1";
                case Middle: return "Mouse 2";
                case Button3: return "Mouse 3";
                case XPlus: return "Mouse X+";
                case XMinus: return "Mouse X-";
                case YPlus: return "Mouse Y+";
                case YMinus: return "Mouse Y-";
                case WheelPlus: return "Mouse Wheel+";
                case WheelMinus: return "Mouse Wheel-";
                default: return "Mouse ?" + code.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>DirectInput keyboard scan codes (dinput.h DIK_*) and their names in the style of G3's controls.ini.</summary>
    public static class Dik
    {
        public const int Escape = 0x01, D1 = 0x02, D0 = 0x0B, Backspace = 0x0E, Tab = 0x0F, Q = 0x10, W = 0x11, E = 0x12, R = 0x13,
                         T = 0x14, U = 0x16, I = 0x17, Return = 0x1C, LeftCtrl = 0x1D, A = 0x1E, S = 0x1F, D = 0x20, F = 0x21,
                         L = 0x26, Grave = 0x29, LeftShift = 0x2A, C = 0x2E, B = 0x30, M = 0x32, RightShift = 0x36,
                         LeftAlt = 0x38, Space = 0x39, CapsLock = 0x3A, F1 = 0x3B, F5 = 0x3F, F8 = 0x42, F9 = 0x43, F10 = 0x44,
                         ScrollLock = 0x46, Num7 = 0x47, Num8 = 0x48, Num9 = 0x49, NumMinus = 0x4A, Num4 = 0x4B, NumPlus = 0x4E,
                         Num2 = 0x50, Num0 = 0x52, NumDecimal = 0x53, F11 = 0x57, F12 = 0x58, NumEnter = 0x9C, RightCtrl = 0x9D,
                         RightAlt = 0xB8, Home = 0xC7, Up = 0xC8, PageUp = 0xC9, Left = 0xCB, Right = 0xCD, End = 0xCF, Down = 0xD0,
                         PageDown = 0xD1, Insert = 0xD2, Delete = 0xD3;

        static readonly Dictionary<int, string> Names = BuildNames();

        static Dictionary<int, string> BuildNames()
        {
            var n = new Dictionary<int, string>
            {
                [0x01] = "ESCAPE", [0x0C] = "-", [0x0D] = "=", [0x0E] = "BACKSPACE", [0x0F] = "TAB", [0x1A] = "[", [0x1B] = "]",
                [0x1C] = "RETURN", [0x1D] = "LEFT CTRL", [0x27] = ";", [0x28] = "'", [0x29] = "`", [0x2A] = "LEFT SHIFT",
                [0x2B] = "\\", [0x33] = ",", [0x34] = ".", [0x35] = "/", [0x36] = "RIGHT SHIFT", [0x37] = "NUM *",
                [0x38] = "LEFT ALT", [0x39] = "SPACE", [0x3A] = "CAPS LOCK", [0x45] = "NUM LOCK", [0x46] = "SCROLL",
                [0x47] = "NUM 7", [0x48] = "NUM 8", [0x49] = "NUM 9", [0x4A] = "NUM -", [0x4B] = "NUM 4", [0x4C] = "NUM 5",
                [0x4D] = "NUM 6", [0x4E] = "NUM +", [0x4F] = "NUM 1", [0x50] = "NUM 2", [0x51] = "NUM 3", [0x52] = "NUM 0",
                [0x53] = "NUM DEL", [0x56] = "<>", [0x57] = "F11", [0x58] = "F12", [0x9C] = "NUM ENTER", [0x9D] = "RIGHT CTRL",
                [0xB5] = "NUM /", [0xB7] = "PRINT", [0xB8] = "RIGHT ALT", [0xC5] = "PAUSE", [0xC7] = "HOME", [0xC8] = "CURSOR UP",
                [0xC9] = "PAGE UP", [0xCB] = "CURSOR LEFT", [0xCD] = "CURSOR RIGHT", [0xCF] = "END", [0xD0] = "CURSOR DOWN",
                [0xD1] = "PAGE DOWN", [0xD2] = "INSERT", [0xD3] = "DELETE", [0xDB] = "LEFT WIN", [0xDC] = "RIGHT WIN", [0xDD] = "APPS",
            };
            for (int i = 0; i < 9; i++) n[0x02 + i] = ((char)('1' + i)).ToString();
            n[0x0B] = "0";
            const string row1 = "QWERTYUIOP", row2 = "ASDFGHJKL", row3 = "ZXCVBNM";
            for (int i = 0; i < row1.Length; i++) n[0x10 + i] = row1[i].ToString();
            for (int i = 0; i < row2.Length; i++) n[0x1E + i] = row2[i].ToString();
            for (int i = 0; i < row3.Length; i++) n[0x2C + i] = row3[i].ToString();
            for (int i = 0; i < 10; i++) n[0x3B + i] = "F" + (i + 1).ToString(CultureInfo.InvariantCulture);
            return n;
        }

        public static string Name(int dik) =>
            Names.TryGetValue(dik, out var s) ? s : "KEY 0x" + dik.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>All codes with a name (the keys the Unity side can map).</summary>
        public static IEnumerable<int> Known => Names.Keys;
    }

    /// <summary>
    /// The session key bindings: up to two inputs per <see cref="SessionKey"/> (G3 Key1 / Key2). <see cref="Default"/> is
    /// gCSessionKeys::CreateDefaultKeys of CP 1.75 (Game.dll 0x1017f690; the original 1.12 table is the same without
    /// TakeAll and the potion keys and with a debug key). One input belongs to one key at a time: assigning it elsewhere
    /// takes it away from its old key (G3 asks first, "HUD_Controls_Custom_Request_B"). Port exception (fix-205): a
    /// modifier key (<see cref="IsModifier"/>) and a normal key may share an input; the uniqueness holds within each class.
    /// </summary>
    public sealed class KeyMap
    {
        public const int Slots = 2;
        public static readonly SessionKey[] All = (SessionKey[])Enum.GetValues(typeof(SessionKey));

        readonly KeyInput[] _inputs = new KeyInput[All.Length * Slots];

        /// <summary>Raised after every change.</summary>
        public event Action Changed;

        public KeyInput Get(SessionKey key, int slot) => _inputs[Index(key, slot)];

        /// <summary>
        /// The bound inputs of a key (none, one or two). A struct: <c>foreach</c> over it allocates nothing (perf-3: the input
        /// queries run dozens of times per frame; the iterator was ~40 B each).
        /// </summary>
        public BoundInputs Inputs(SessionKey key) => new BoundInputs(_inputs, Index(key, 0));

        /// <summary>The bound inputs of one key, in slot order, the empty slots left out.</summary>
        public readonly struct BoundInputs : IEnumerable<KeyInput>
        {
            readonly KeyInput[] _all;
            readonly int _first;

            internal BoundInputs(KeyInput[] all, int first)
            {
                _all = all;
                _first = first;
            }

            public Enumerator GetEnumerator() => new Enumerator(_all, _first);
            IEnumerator<KeyInput> IEnumerable<KeyInput>.GetEnumerator() => GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public struct Enumerator : IEnumerator<KeyInput>
            {
                readonly KeyInput[] _all;
                readonly int _first;
                int _slot;

                internal Enumerator(KeyInput[] all, int first)
                {
                    _all = all;
                    _first = first;
                    _slot = -1;
                }

                public KeyInput Current => _all[_first + _slot];
                object System.Collections.IEnumerator.Current => Current;

                public bool MoveNext()
                {
                    while (++_slot < Slots)
                        if (!_all[_first + _slot].IsNone) return true;
                    return false;
                }

                public void Reset() => _slot = -1;
                public void Dispose() { }
            }
        }

        public bool IsBound(SessionKey key, KeyInput input)
        {
            if (input.IsNone) return false;
            for (int s = 0; s < Slots; s++) if (_inputs[Index(key, s)] == input) return true;
            return false;
        }

        /// <summary>The key (and slot) an input is bound to, or null; a normal key before a modifier sharing the input.</summary>
        public SessionKey? Owner(KeyInput input, out int slot) =>
            OwnerIn(input, false, out slot) ?? OwnerIn(input, true, out slot);

        /// <summary>
        /// The key an assignment of <paramref name="input"/> to <paramref name="key"/> would take it from: its owner of the
        /// same class (normal or modifier, <see cref="IsModifier"/>), or null.
        /// </summary>
        public SessionKey? ConflictOwner(SessionKey key, KeyInput input, out int slot) => OwnerIn(input, IsModifier(key), out slot);

        SessionKey? OwnerIn(KeyInput input, bool modifier, out int slot)
        {
            slot = -1;
            if (input.IsNone) return null;
            for (int i = 0; i < _inputs.Length; i++)
                if (_inputs[i] == input && IsModifier(All[i / Slots]) == modifier)
                {
                    slot = i % Slots;
                    return All[i / Slots];
                }
            return null;
        }

        /// <summary>
        /// A port modifier key (fix-205): it is held together with another key, so it may share its inputs with a normal
        /// key (Ctrl: Sneak and the quick-loot modifier).
        /// </summary>
        public static bool IsModifier(SessionKey key) => key == SessionKey.QuickLootModifier;

        /// <summary>A port key that G3 does not have (its name is not in G3's string tables; <see cref="PortText"/>).</summary>
        public static bool IsPortKey(SessionKey key) => key == SessionKey.QuickLootModifier;

        /// <summary>The name of a port key in the menu's text language (Russian or English), or null for a G3 key.</summary>
        public static string PortText(SessionKey key, string language)
        {
            bool ru = string.Equals(language, "Russian", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case SessionKey.QuickLootModifier: return ru ? "Забрать всё (+ Действие 1)" : "Take all (+ Action 1)";
                default: return null;
            }
        }

        /// <summary>
        /// Binds <paramref name="input"/> to a slot; the input leaves any other key/slot it was bound to. Returns the key
        /// it was taken from (null when it was free or already in this slot).
        /// </summary>
        public SessionKey? Assign(SessionKey key, int slot, KeyInput input)
        {
            int target = Index(key, slot);
            SessionKey? previous = null;
            bool modifier = IsModifier(key);
            if (!input.IsNone)
                for (int i = 0; i < _inputs.Length; i++)
                    if (i != target && _inputs[i] == input && IsModifier(All[i / Slots]) == modifier)
                    {
                        _inputs[i] = KeyInput.None;
                        previous = All[i / Slots];
                    }
            _inputs[target] = input;
            Changed?.Invoke();
            return previous;
        }

        public void Clear(SessionKey key, int slot) => Assign(key, slot, KeyInput.None);

        /// <summary>The CP 1.75 default table ("Standard").</summary>
        public static KeyMap Default()
        {
            var m = new KeyMap();
            m.SetDefaults();
            return m;
        }

        /// <summary>Replaces every binding by the default table (G3 Standard radio: CreateDefaultKeys).</summary>
        public void ResetToDefault()
        {
            SetDefaults();
            Changed?.Invoke();
        }

        void SetDefaults()
        {
            Array.Clear(_inputs, 0, _inputs.Length);
            foreach (var (key, a, b) in DefaultTable) { Put(key, 0, a); Put(key, 1, b); }
        }

        public bool IsDefault
        {
            get
            {
                var d = Default();
                for (int i = 0; i < _inputs.Length; i++) if (_inputs[i] != d._inputs[i]) return false;
                return true;
            }
        }

        public void CopyFrom(KeyMap other)
        {
            Array.Copy(other._inputs, _inputs, _inputs.Length);
            Changed?.Invoke();
        }

        /// <summary>"Forward=0:200,0:17;Backward=…" (G3 Key1/Key2 Type:Offset, "-" = free).</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            foreach (var key in All)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(key).Append('=').Append(Get(key, 0)).Append(',').Append(Get(key, 1));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reads <see cref="Serialize"/>. Keys missing from the text (a newer build added them) keep their defaults; unknown
        /// names and malformed entries are skipped. An input claimed twice stays with the first key read.
        /// </summary>
        public static KeyMap Parse(string text)
        {
            var m = Default();
            if (string.IsNullOrEmpty(text)) return m;
            var seenNormal = new HashSet<KeyInput>();
            var seenModifier = new HashSet<KeyInput>();
            HashSet<KeyInput> Seen(SessionKey k) => IsModifier(k) ? seenModifier : seenNormal;
            var read = new List<(SessionKey key, KeyInput a, KeyInput b)>();
            foreach (var entry in text.Split(';'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0 || !Enum.TryParse(entry.Substring(0, eq).Trim(), false, out SessionKey key) || !Enum.IsDefined(typeof(SessionKey), key)) continue;
                var parts = entry.Substring(eq + 1).Split(',');
                if (parts.Length != Slots || !KeyInput.TryParse(parts[0], out var a) || !KeyInput.TryParse(parts[1], out var b)) continue;
                read.Add((key, a, b));
            }
            // Keys from the text first (they win), then defaults of keys not in the text lose inputs the text claims.
            var fromText = new HashSet<SessionKey>();
            foreach (var r in read) fromText.Add(r.key);
            foreach (var key in All) if (!fromText.Contains(key)) { m.Put(key, 0, KeyInput.None); m.Put(key, 1, KeyInput.None); }
            foreach (var (key, a, b) in read)
            {
                var seen = Seen(key);
                m.Put(key, 0, seen.Add(a) || a.IsNone ? a : KeyInput.None);
                m.Put(key, 1, seen.Add(b) || b.IsNone ? b : KeyInput.None);
            }
            var defaults = Default();
            foreach (var key in All)
            {
                if (fromText.Contains(key)) continue;
                for (int s = 0; s < Slots; s++)
                {
                    var d = defaults.Get(key, s);
                    m.Put(key, s, d.IsNone || Seen(key).Add(d) ? d : KeyInput.None);
                }
            }
            return m;
        }

        void Put(SessionKey key, int slot, KeyInput input) => _inputs[Index(key, slot)] = input;

        static int Index(SessionKey key, int slot)
        {
            if (slot < 0 || slot >= Slots) throw new ArgumentOutOfRangeException(nameof(slot));
            return (int)key * Slots + slot;
        }

        static KeyInput K(int dik) => KeyInput.Key(dik);
        static KeyInput Ms(int offset) => KeyInput.Mouse(offset);

        /// <summary>gCSessionKeys::CreateDefaultKeys, CP 1.75, Key1 then Key2.</summary>
        public static readonly IReadOnlyList<(SessionKey key, KeyInput key1, KeyInput key2)> DefaultTable = new[]
        {
            (SessionKey.Forward, K(Dik.Up), K(Dik.W)),
            (SessionKey.Backward, K(Dik.Down), K(Dik.S)),
            (SessionKey.StrafeLeft, K(Dik.Left), K(Dik.A)),
            (SessionKey.StrafeRight, K(Dik.Right), K(Dik.D)),
            (SessionKey.RotateCamRight, Ms(MouseOffset.XPlus), K(Dik.E)),
            (SessionKey.RotateCamLeft, Ms(MouseOffset.XMinus), K(Dik.Q)),
            (SessionKey.TurnUp, Ms(MouseOffset.YMinus), KeyInput.None),
            (SessionKey.TurnDown, Ms(MouseOffset.YPlus), KeyInput.None),
            (SessionKey.Walk, K(Dik.RightShift), K(Dik.LeftShift)),
            (SessionKey.WalkToggle, K(Dik.CapsLock), KeyInput.None),
            (SessionKey.Up, K(Dik.Num0), K(Dik.LeftAlt)),
            (SessionKey.Down, K(Dik.RightCtrl), K(Dik.LeftCtrl)),
            (SessionKey.Use1, Ms(MouseOffset.Left), KeyInput.None),
            (SessionKey.Use2, Ms(MouseOffset.Right), KeyInput.None),
            (SessionKey.Plus, K(Dik.NumPlus), Ms(MouseOffset.WheelPlus)),
            (SessionKey.Minus, K(Dik.NumMinus), Ms(MouseOffset.WheelMinus)),
            (SessionKey.ResetCamera, K(Dik.Num4), KeyInput.None),
            (SessionKey.FirstPerson, K(Dik.NumDecimal), K(Dik.T)),
            (SessionKey.Look, K(Dik.Num2), KeyInput.None),
            (SessionKey.QuickUse0, K(Dik.D0), KeyInput.None),
            (SessionKey.QuickUse1, K(0x02), KeyInput.None),
            (SessionKey.QuickUse2, K(0x03), KeyInput.None),
            (SessionKey.QuickUse3, K(0x04), KeyInput.None),
            (SessionKey.QuickUse4, K(0x05), KeyInput.None),
            (SessionKey.QuickUse5, K(0x06), KeyInput.None),
            (SessionKey.QuickUse6, K(0x07), KeyInput.None),
            (SessionKey.QuickUse7, K(0x08), KeyInput.None),
            (SessionKey.QuickUse8, K(0x09), KeyInput.None),
            (SessionKey.QuickUse9, K(0x0A), KeyInput.None),
            (SessionKey.InventoryMode, K(Dik.I), K(Dik.Backspace)),
            (SessionKey.WeaponMode, K(Dik.Space), Ms(MouseOffset.Middle)),
            (SessionKey.JournalModeLog, K(Dik.L), KeyInput.None),
            (SessionKey.JournalModeCharScreen, K(Dik.C), KeyInput.None),
            (SessionKey.JournalModeMagBook, K(Dik.B), KeyInput.None),
            (SessionKey.JournalModeMap, K(Dik.M), KeyInput.None),
            (SessionKey.Lock, K(Dik.R), K(Dik.End)),
            (SessionKey.ToggleHUD, K(Dik.ScrollLock), KeyInput.None),
            (SessionKey.TakeAll, K(Dik.U), KeyInput.None),
            (SessionKey.UsePotionHP, K(Dik.Num7), KeyInput.None),
            (SessionKey.UsePotionMP, K(Dik.Num8), KeyInput.None),
            (SessionKey.UsePotionSP, K(Dik.Num9), KeyInput.None),
            (SessionKey.Confirm, K(Dik.Return), KeyInput.None),
            (SessionKey.QuickSave, K(Dik.F5), KeyInput.None),
            (SessionKey.QuickLoad, K(Dik.F9), KeyInput.None),
            // fix-205 port modifier: shares Ctrl with Sneak (KeyMap.IsModifier)
            (SessionKey.QuickLootModifier, K(Dik.LeftCtrl), K(Dik.RightCtrl)),
        };

        /// <summary>The quick-use key of a slot digit 0…9.</summary>
        public static SessionKey QuickUse(int digit) => SessionKey.QuickUse0 + digit;

        /// <summary>The string-table id of a key's name ("HUD_SessionKey_Forward").</summary>
        public static string TextId(SessionKey key) => "HUD_SessionKey_" + key;
    }
}
