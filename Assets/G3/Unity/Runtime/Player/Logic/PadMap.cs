using System;
using System.Collections.Generic;
using System.Text;

namespace G3.Player
{
    /// <summary>
    /// The inputs of a gamepad as the binding tables see them (Xbox names; the Input System's Gamepad layout is the same
    /// for PlayStation, Switch Pro and generic pads: South = A / Cross, East = B / Circle …). The stick halves (LsUp …)
    /// are buttons too: the resolver reads their deflection as an analog value and as a button with a threshold.
    /// Docs/gamepad.md §3.
    /// </summary>
    public enum PadButton : byte
    {
        None,
        A, B, X, Y, LB, RB, LT, RT, L3, R3, Start, View,
        DUp, DDown, DLeft, DRight,
        LsUp, LsDown, LsLeft, LsRight,
        RsUp, RsDown, RsLeft, RsRight,
    }

    /// <summary>How a binding reads its button.</summary>
    public enum PadGesture : byte
    {
        /// <summary>Held while the button is held.</summary>
        Press,
        /// <summary>A short press: one pulse on release (or on press when the button has no hold and is no modifier).</summary>
        Tap,
        /// <summary>Held from <see cref="PadResolver.HoldSeconds"/> on until the button is released.</summary>
        Hold,
    }

    /// <summary>Pad-only commands that are no G3 session key (Docs/gamepad.md §3).</summary>
    public enum PadAction : byte
    {
        None,
        /// <summary>Moves the highlight of the HUD quick bar one slot back.</summary>
        QuickSlotPrev,
        /// <summary>Moves the highlight of the HUD quick bar one slot on.</summary>
        QuickSlotNext,
        /// <summary>Uses the highlighted quick slot.</summary>
        QuickSlotUse,
        /// <summary>The port's quick loot: QuickLootModifier held + Use1 (Ctrl + left mouse; <see cref="PadMap.Expansion"/>).</summary>
        QuickLoot,
    }

    /// <summary>
    /// One pad binding: a button (with LB held when <see cref="Chord"/>), its gesture, and what it drives: a session key or
    /// a <see cref="PadAction"/> (<see cref="Action"/> ≠ None).
    /// </summary>
    public readonly struct PadBinding : IEquatable<PadBinding>
    {
        public PadBinding(PadButton button, bool chord, PadGesture gesture, SessionKey key, PadAction action = PadAction.None)
        {
            Button = button;
            Chord = chord && button != PadMap.Modifier;
            Gesture = gesture;
            Key = key;
            Action = action;
        }

        public PadButton Button { get; }
        /// <summary>Acts only with the chord modifier (<see cref="PadMap.Modifier"/>, LB) held.</summary>
        public bool Chord { get; }
        public PadGesture Gesture { get; }
        /// <summary>The session key (meaningless when <see cref="Action"/> ≠ None).</summary>
        public SessionKey Key { get; }
        public PadAction Action { get; }
        public bool IsAction => Action != PadAction.None;

        public static PadBinding ForKey(SessionKey key, PadButton button, PadGesture gesture = PadGesture.Press, bool chord = false) =>
            new PadBinding(button, chord, gesture, key);

        public static PadBinding ForAction(PadAction action, PadButton button, PadGesture gesture = PadGesture.Press, bool chord = false) =>
            new PadBinding(button, chord, gesture, default, action);

        /// <summary>The same command (key or action).</summary>
        public bool SameTarget(PadBinding other) => IsAction ? other.Action == Action : !other.IsAction && other.Key == Key;

        /// <summary>
        /// Two bindings that cannot both stay: the same button and chord with gestures that overlap (a press overlaps
        /// everything; tap and hold of one button can live side by side).
        /// </summary>
        public bool Conflicts(PadBinding other) =>
            Button == other.Button && Chord == other.Chord &&
            (Gesture == PadGesture.Press || other.Gesture == PadGesture.Press || Gesture == other.Gesture);

        public bool Equals(PadBinding o) => Button == o.Button && Chord == o.Chord && Gesture == o.Gesture && SameTarget(o);
        public override bool Equals(object obj) => obj is PadBinding b && Equals(b);
        public override int GetHashCode() => ((int)Button << 8) ^ ((Chord ? 1 : 0) << 7) ^ ((int)Gesture << 5) ^ (IsAction ? 1000 + (int)Action : (int)Key);

        /// <summary>The target's storage name: the session key's name or "@" + the action's.</summary>
        public string TargetName => IsAction ? "@" + Action : Key.ToString();

        /// <summary>"RT", "A:tap", "Y+LB" (button, chord, gesture).</summary>
        public string InputText
        {
            get
            {
                var s = Button.ToString();
                if (Chord) s += "+LB";
                if (Gesture == PadGesture.Tap) s += ":tap";
                else if (Gesture == PadGesture.Hold) s += ":hold";
                return s;
            }
        }

        public override string ToString() => TargetName + "=" + InputText;

        /// <summary>Reads <see cref="ToString"/>; false for anything malformed.</summary>
        public static bool TryParse(string s, out PadBinding binding)
        {
            binding = default;
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return false;
            int eq = s.IndexOf('=');
            if (eq <= 0) return false;
            string target = s.Substring(0, eq).Trim(), input = s.Substring(eq + 1).Trim();
            var gesture = PadGesture.Press;
            int colon = input.IndexOf(':');
            if (colon >= 0)
            {
                string g = input.Substring(colon + 1).Trim();
                if (g == "tap") gesture = PadGesture.Tap;
                else if (g == "hold") gesture = PadGesture.Hold;
                else return false;
                input = input.Substring(0, colon);
            }
            bool chord = input.EndsWith("+LB", StringComparison.Ordinal);
            if (chord) input = input.Substring(0, input.Length - 3);
            if (!Enum.TryParse(input, false, out PadButton button) || !Enum.IsDefined(typeof(PadButton), button) || button == PadButton.None)
                return false;
            if (target.StartsWith("@", StringComparison.Ordinal))
            {
                if (!Enum.TryParse(target.Substring(1), false, out PadAction action) || !Enum.IsDefined(typeof(PadAction), action) ||
                    action == PadAction.None) return false;
                binding = ForAction(action, button, gesture, chord);
                return true;
            }
            if (!Enum.TryParse(target, false, out SessionKey key) || !Enum.IsDefined(typeof(SessionKey), key)) return false;
            binding = ForKey(key, button, gesture, chord);
            return true;
        }
    }

    /// <summary>
    /// The pad's binding table (Docs/gamepad.md §3): any number of bindings per session key or pad action, no two
    /// conflicting ones (<see cref="PadBinding.Conflicts"/>). Start and the UI layer's buttons are fixed and not in here.
    /// </summary>
    public sealed class PadMap
    {
        /// <summary>The name of a pad action in the menu's text language (Russian or English).</summary>
        public static string ActionText(PadAction action, string language)
        {
            bool ru = string.Equals(language, "Russian", StringComparison.OrdinalIgnoreCase);
            switch (action)
            {
                case PadAction.QuickSlotPrev: return ru ? "Предыдущий быстрый слот" : "Previous quick slot";
                case PadAction.QuickSlotNext: return ru ? "Следующий быстрый слот" : "Next quick slot";
                case PadAction.QuickSlotUse: return ru ? "Использовать выбранный слот" : "Use selected quick slot";
                case PadAction.QuickLoot: return ru ? "Забрать всё (быстрый обыск)" : "Take all (quick loot)";
                default: return "";
            }
        }

        /// <summary>The chord modifier: other buttons have a second layer while it is held.</summary>
        public const PadButton Modifier = PadButton.LB;

        readonly List<PadBinding> _bindings = new List<PadBinding>();

        /// <summary>Raised after every change.</summary>
        public event Action Changed;

        public IReadOnlyList<PadBinding> Bindings => _bindings;

        /// <summary>Every command the table can bind, in the order of the pad key list.</summary>
        public static readonly IReadOnlyList<PadBinding> Commands = BuildCommands();

        static IReadOnlyList<PadBinding> BuildCommands()
        {
            var list = new List<PadBinding>();
            foreach (var key in KeyMap.All)
            {
                // the quick-loot modifier is reached through the QuickLoot action on the pad
                if (key == SessionKey.QuickLootModifier) continue;
                list.Add(PadBinding.ForKey(key, PadButton.None));
            }
            foreach (PadAction a in Enum.GetValues(typeof(PadAction)))
                if (a != PadAction.None) list.Add(PadBinding.ForAction(a, PadButton.None));
            return list;
        }

        /// <summary>The bindings of one command (key or action).</summary>
        public IEnumerable<PadBinding> Of(PadBinding command)
        {
            foreach (var b in _bindings) if (b.SameTarget(command)) yield return b;
        }

        public IEnumerable<PadBinding> Of(SessionKey key) => Of(PadBinding.ForKey(key, PadButton.None));

        /// <summary>The first binding of a command, or null (the pad key list's cell; the prompts).</summary>
        public PadBinding? First(PadBinding command)
        {
            foreach (var b in _bindings) if (b.SameTarget(command)) return b;
            return null;
        }

        public PadBinding? First(SessionKey key) => First(PadBinding.ForKey(key, PadButton.None));

        /// <summary>Cells per command in the pad key list (like G3's Key1 / Key2).</summary>
        public const int Slots = 2;

        /// <summary>The <paramref name="slot"/>-th binding of a command (in table order), or null.</summary>
        public PadBinding? Get(PadBinding command, int slot)
        {
            int n = 0;
            foreach (var b in _bindings)
                if (b.SameTarget(command) && n++ == slot) return b;
            return null;
        }

        /// <summary>
        /// Binds as the pad key list does: the command's <paramref name="slot"/>-th binding is replaced by
        /// <paramref name="binding"/> (added when the command has fewer), and every other binding that conflicts with it
        /// is removed. Returns the bindings taken from other commands.
        /// </summary>
        public List<PadBinding> Assign(PadBinding binding, int slot = 0)
        {
            var taken = new List<PadBinding>();
            int at = IndexOf(binding, slot);
            if (at >= 0) _bindings.RemoveAt(at);
            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                var b = _bindings[i];
                if (!b.Conflicts(binding)) continue;
                if (!b.SameTarget(binding)) taken.Add(b);
                _bindings.RemoveAt(i);
                if (i < at) at--;
            }
            if (binding.Button != PadButton.None)
            {
                if (at >= 0 && at <= _bindings.Count) _bindings.Insert(at, binding);
                else _bindings.Add(binding);
            }
            Changed?.Invoke();
            return taken;
        }

        int IndexOf(PadBinding command, int slot)
        {
            int n = 0;
            for (int i = 0; i < _bindings.Count; i++)
                if (_bindings[i].SameTarget(command) && n++ == slot) return i;
            return -1;
        }

        /// <summary>Adds a binding next to the command's others (conflicting bindings leave).</summary>
        public void Add(PadBinding binding)
        {
            if (binding.Button == PadButton.None) return;
            _bindings.RemoveAll(b => b.Conflicts(binding));
            _bindings.Add(binding);
            Changed?.Invoke();
        }

        /// <summary>Removes the command's <paramref name="slot"/>-th binding.</summary>
        public void Clear(PadBinding command, int slot)
        {
            int at = IndexOf(command, slot);
            if (at < 0) return;
            _bindings.RemoveAt(at);
            Changed?.Invoke();
        }

        /// <summary>The button has a chord (LB +) binding: its plain bindings rest while LB is held.</summary>
        public bool HasChord(PadButton button)
        {
            foreach (var b in _bindings) if (b.Chord && b.Button == button) return true;
            return false;
        }

        /// <summary>The button has a hold binding (with the given chord state): its taps wait for the release.</summary>
        public bool HasHold(PadButton button, bool chord)
        {
            foreach (var b in _bindings) if (b.Gesture == PadGesture.Hold && b.Button == button && b.Chord == chord) return true;
            return false;
        }

        /// <summary>The session keys a pad action stands for (QuickLoot = QuickLootModifier + Use1), else none.</summary>
        public static SessionKey[] Expansion(PadAction action) => action == PadAction.QuickLoot ? QuickLootKeys : NoKeys;

        static readonly SessionKey[] QuickLootKeys = { SessionKey.QuickLootModifier, SessionKey.Use1 }, NoKeys = new SessionKey[0];

        public static PadMap Default()
        {
            var m = new PadMap();
            m.SetDefaults();
            return m;
        }

        public void ResetToDefault()
        {
            SetDefaults();
            Changed?.Invoke();
        }

        void SetDefaults()
        {
            _bindings.Clear();
            _bindings.AddRange(DefaultTable);
        }

        public bool IsDefault
        {
            get
            {
                if (_bindings.Count != DefaultTable.Count) return false;
                foreach (var d in DefaultTable) if (!_bindings.Contains(d)) return false;
                return true;
            }
        }

        public void CopyFrom(PadMap other)
        {
            _bindings.Clear();
            _bindings.AddRange(other._bindings);
            Changed?.Invoke();
        }

        /// <summary>"Use1=RT;Use1=A:tap;@QuickLoot=A:hold;…"; an empty table is "-".</summary>
        public string Serialize()
        {
            if (_bindings.Count == 0) return "-";
            var sb = new StringBuilder();
            foreach (var b in _bindings)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(b);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reads <see cref="Serialize"/>; empty or null text = the defaults, "-" = no bindings. Malformed entries are skipped;
        /// of two conflicting entries the first read stays.
        /// </summary>
        public static PadMap Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Default();
            var m = new PadMap();
            if (text.Trim() == "-") return m;
            foreach (var entry in text.Split(';'))
            {
                if (!PadBinding.TryParse(entry, out var b)) continue;
                bool clash = false;
                foreach (var have in m._bindings) clash |= have.Conflicts(b) || have.Equals(b);
                if (!clash) m._bindings.Add(b);
            }
            return m;
        }

        static PadBinding K(SessionKey key, PadButton button, PadGesture g = PadGesture.Press, bool chord = false) =>
            PadBinding.ForKey(key, button, g, chord);

        static PadBinding A(PadAction action, PadButton button, PadGesture g = PadGesture.Press, bool chord = false) =>
            PadBinding.ForAction(action, button, g, chord);

        /// <summary>Docs/gamepad.md §3.</summary>
        public static readonly IReadOnlyList<PadBinding> DefaultTable = new[]
        {
            K(SessionKey.Forward, PadButton.LsUp),
            K(SessionKey.Backward, PadButton.LsDown),
            K(SessionKey.StrafeLeft, PadButton.LsLeft),
            K(SessionKey.StrafeRight, PadButton.LsRight),
            K(SessionKey.RotateCamRight, PadButton.RsRight),
            K(SessionKey.RotateCamLeft, PadButton.RsLeft),
            K(SessionKey.TurnUp, PadButton.RsUp),
            K(SessionKey.TurnDown, PadButton.RsDown),
            K(SessionKey.Walk, PadButton.L3),
            K(SessionKey.FirstPerson, PadButton.R3),
            K(SessionKey.Look, PadButton.R3, chord: true),
            K(SessionKey.Plus, PadButton.RsUp, chord: true),
            K(SessionKey.Minus, PadButton.RsDown, chord: true),
            K(SessionKey.ResetCamera, PadButton.L3, chord: true),
            K(SessionKey.Use1, PadButton.RT),
            K(SessionKey.Use2, PadButton.LT),
            K(SessionKey.Use1, PadButton.A, PadGesture.Tap),
            A(PadAction.QuickLoot, PadButton.A, PadGesture.Hold),
            K(SessionKey.Down, PadButton.B),
            K(SessionKey.WeaponMode, PadButton.X),
            K(SessionKey.Up, PadButton.Y),
            A(PadAction.QuickSlotNext, PadButton.RB, PadGesture.Tap),
            A(PadAction.QuickSlotPrev, PadButton.LB, PadGesture.Tap),
            A(PadAction.QuickSlotUse, PadButton.DUp),
            K(SessionKey.QuickUse1, PadButton.DLeft),
            K(SessionKey.QuickUse2, PadButton.DRight),
            K(SessionKey.InventoryMode, PadButton.DDown),
            K(SessionKey.UsePotionHP, PadButton.DLeft, chord: true),
            K(SessionKey.UsePotionMP, PadButton.DUp, chord: true),
            K(SessionKey.UsePotionSP, PadButton.DRight, chord: true),
            K(SessionKey.Confirm, PadButton.Y, chord: true),
            K(SessionKey.JournalModeMap, PadButton.View, PadGesture.Tap),
            K(SessionKey.JournalModeLog, PadButton.View, PadGesture.Hold),
        };
    }
}
