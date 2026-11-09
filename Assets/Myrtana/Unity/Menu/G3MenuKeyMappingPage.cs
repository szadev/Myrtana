using System.Collections.Generic;
using G3.Player;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3's custom key list (<c>IDD_MENU_OPTIONS_CONTROLS_CUSTOM_2</c>, gCHUDMenuOptionsControlsCustom2): header
    /// <c>HUD_Menu_Keymapping</c>, one row per session key in G3's order (<see cref="KeyMap.All"/>) with the key's name
    /// (<c>HUD_SessionKey_*</c>) and its two inputs (Key1 / Key2) in the list slot, Back. Clicking an input cell waits for a
    /// key (the request view shows <c>HUD_Controls_Custom_Request_A</c>; Esc cancels); a key or mouse button that already
    /// belongs to another session key asks <c>HUD_Controls_Custom_Request_B</c> first and then moves over (G3's
    /// SetControlsCustomWaitForKeyCallback). A right click clears a cell (port addition: G3 had no way to unbind).
    /// Every change is stored at once (<see cref="OptionsPreferences"/>).
    /// gamepad-1 (Docs/gamepad.md §5): the same page in pad mode (<see cref="MenuPage.GamepadCustom"/>) lists the pad's
    /// commands (<see cref="PadMap.Commands"/>: the session keys and the pad actions) with two pad cells each; a cell waits
    /// for a pad button (LB held = an LB chord; Start or Esc cancels), X / right click clears it, Y steps its gesture
    /// (press → tap → hold). The pad focus reaches every cell (A = wait, X = clear).
    /// </summary>
    public sealed class G3MenuKeyMappingPage
    {
        const float RowHeight = 26f, NameWidth = 300f, FontSize = 16f;
        static readonly TextId HeaderText = new TextId("HUD_Menu_Keymapping"), WaitText = new TextId("HUD_Controls_Custom_Request_A"),
                               AssignedText = new TextId("HUD_Controls_Custom_Request_B");

        readonly G3MenuView _view;
        readonly ControlOptions _controls;
        readonly VisualElement _page, _request;
        readonly Label _requestText;
        readonly ScrollView _keyList, _padList;
        readonly List<(SessionKey key, Label name, Label[] cells)> _rows = new List<(SessionKey, Label, Label[])>();
        readonly List<(PadBinding command, Label name, Label[] cells)> _padRows = new List<(PadBinding, Label, Label[])>();
        readonly PadCapture _capture = new PadCapture();
        (SessionKey key, int slot)? _waiting;
        (PadBinding command, int slot)? _padWaiting;
        int _waitFrame;
        bool _padMode;

        public G3MenuKeyMappingPage(VisualElement frame, G3MenuView view, ControlOptions controls)
        {
            _view = view;
            _controls = controls;
            _page = G3MenuView.Page(frame, "IDD_MENU_OPTIONS_CONTROLS_CUSTOM_2");
            view.Label(_page, "IDC_STATIC_CONTROLS_CUSTOM_KEYMAPPING_2", HeaderText);
            view.Button(_page, "IDC_BUTTON_MENU_OPTIONS_CONTROLS_CUSTOM_BACK_2", G3MenuSettingsPages.BackText, () => view.Menu.State.Back());

            var slot = _page.Q("IDC_LISTCTRL_CONTROLS_CUSTOM_KEYMAPPING_2");
            if (slot != null)
            {
                slot.pickingMode = PickingMode.Position;
                _keyList = List(slot);
                foreach (var key in KeyMap.All) AddRow(_keyList, key);
                _padList = List(slot);
                foreach (var command in PadMap.Commands) AddPadRow(_padList, command);
                _padList.style.display = DisplayStyle.None;
            }

            _request = _page.Q("IDC_VIEW_MENU_CONTROLS_CUSTOM_REQUEST_2");
            if (_request != null)
            {
                _request.style.display = DisplayStyle.None;
                _request.style.backgroundColor = new Color(0, 0, 0, 0.85f);
                _request.BringToFront();
                _requestText = _request as Label ?? new Label();
                if (_requestText.parent == null) _request.Add(_requestText);
                _requestText.style.unityTextAlign = TextAnchor.MiddleCenter;
                _requestText.style.whiteSpace = WhiteSpace.Normal;
                G3Text.Font(_requestText, 20, bold: true);
                _requestText.style.color = G3MenuSettingsPages.TextColor;
            }
            controls.Changed += Render;
        }

        static ScrollView List(VisualElement slot)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.style.position = Position.Absolute;
            scroll.style.left = scroll.style.top = scroll.style.right = scroll.style.bottom = 0;
            slot.Add(scroll);
            return scroll;
        }

        /// <summary>A cell waits for its key or pad button.</summary>
        public bool Waiting => _waiting != null || _padWaiting != null;

        /// <summary>A pad cell waits (B is a bindable button then, not Back).</summary>
        public bool WaitingForPad => _padWaiting != null;

        string Language => _view.Languages?.TextLanguage;
        bool Russian => string.Equals(Language, "Russian", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Shows the page: the keyboard list or, with <paramref name="pad"/>, the pad list.</summary>
        public void Show(bool visible, bool pad = false)
        {
            if (!visible || pad != _padMode) Cancel();
            _padMode = pad;
            if (_keyList != null) _keyList.style.display = pad ? DisplayStyle.None : DisplayStyle.Flex;
            if (_padList != null) _padList.style.display = pad ? DisplayStyle.Flex : DisplayStyle.None;
            G3MenuView.Show(_page, visible);
            if (visible) Render();
        }

        /// <summary>Stops waiting for a key; true when a wait was running (Esc on the page).</summary>
        public bool Cancel()
        {
            if (_waiting == null && _padWaiting == null) return false;
            _waiting = null;
            _padWaiting = null;
            ShowRequest(false);
            Render();
            return true;
        }

        /// <summary>Per frame: takes the first key or mouse button (or pad button) pressed while a cell waits.</summary>
        public void Tick()
        {
            if (_padWaiting != null)
            {
                PadTick();
                return;
            }
            if (_waiting == null || Time.frameCount == _waitFrame) return; // the click that started the wait
            if (!G3Input.TryCapture(out var input)) return;
            var (key, slot) = _waiting.Value;
            _waiting = null;
            ShowRequest(false);
            // fix-205: a modifier key (quick loot) may share its input with a normal key; only its own class conflicts
            var owner = _controls.Keys.ConflictOwner(key, input, out int ownerSlot);
            if (owner == null || (owner == key && ownerSlot == slot))
            {
                Assign(key, slot, input);
                return;
            }
            // G3: "Key is already assigned to an action. Do you want to re-assign this key?"
            _view.Menu.State.Ask(new MenuRequest(AssignedText, () => Assign(key, slot, input)));
        }

        void PadTick()
        {
            if (Time.frameCount == _waitFrame) return; // the A that started the wait
            if (G3Pad.Pressed(PadButton.Start))
            {
                Cancel();
                return;
            }
            if (!_capture.Update(G3Pad.Down, G3Pad.Pressed)) return;
            var (command, slot) = _padWaiting.Value;
            _padWaiting = null;
            ShowRequest(false);
            var old = _controls.Pad.Buttons.Get(command, slot);
            var gesture = old?.Gesture ?? PadGesture.Press;
            var binding = Bind(command, _capture.Button, _capture.Chord, gesture);
            if (!TakesFromOthers(binding))
            {
                AssignPad(binding, slot);
                return;
            }
            _view.Menu.State.Ask(new MenuRequest(AssignedText, () => AssignPad(binding, slot)));
        }

        static PadBinding Bind(PadBinding command, PadButton button, bool chord, PadGesture gesture) =>
            command.IsAction ? PadBinding.ForAction(command.Action, button, gesture, chord) : PadBinding.ForKey(command.Key, button, gesture, chord);

        bool TakesFromOthers(PadBinding binding)
        {
            foreach (var b in _controls.Pad.Buttons.Bindings)
                if (!b.SameTarget(binding) && b.Conflicts(binding)) return true;
            return false;
        }

        void Assign(SessionKey key, int slot, KeyInput input)
        {
            _controls.Layout = ControlOptions.Custom;
            _controls.Keys.Assign(key, slot, input); // raises Changed: stored and redrawn
        }

        void AssignPad(PadBinding binding, int slot)
        {
            _controls.Pad.Layout = ControlOptions.Custom;
            _controls.Pad.Buttons.Assign(binding, slot); // raises Changed: stored and redrawn
        }

        void StartKeyWait(SessionKey key, int slot)
        {
            if (_view.Menu.State.Request != null) return;
            _waiting = (key, slot);
            _waitFrame = Time.frameCount;
            ShowRequest(true);
            Render();
        }

        void ClearKey(SessionKey key, int slot)
        {
            if (_view.Menu.State.Request != null) return;
            _controls.Layout = ControlOptions.Custom;
            _controls.Keys.Clear(key, slot);
        }

        void StartPadWait(PadBinding command, int slot)
        {
            if (_view.Menu.State.Request != null) return;
            _padWaiting = (command, slot);
            _waitFrame = Time.frameCount;
            _capture.Reset();
            ShowRequest(true);
            Render();
        }

        void ClearPad(PadBinding command, int slot)
        {
            if (_view.Menu.State.Request != null || _controls.Pad.Buttons.Get(command, slot) == null) return;
            _controls.Pad.Layout = ControlOptions.Custom;
            _controls.Pad.Buttons.Clear(command, slot);
        }

        /// <summary>Y on a pad cell: its gesture steps press → tap → hold (a button of another command may move over).</summary>
        void StepGesture(PadBinding command, int slot)
        {
            if (_view.Menu.State.Request != null || !(_controls.Pad.Buttons.Get(command, slot) is PadBinding b)) return;
            AssignPad(Bind(command, b.Button, b.Chord, PadCapture.NextGesture(b.Gesture)), slot);
        }

        void AddRow(ScrollView list, SessionKey key)
        {
            var row = Row(out var name);
            var cells = new Label[KeyMap.Slots];
            for (int s = 0; s < KeyMap.Slots; s++)
            {
                int slot = s;
                var cell = InputCell(() => StartKeyWait(key, slot), () => ClearKey(key, slot), null);
                cells[s] = cell;
                row.Add(cell);
            }
            list.Add(row);
            _rows.Add((key, name, cells));
        }

        void AddPadRow(ScrollView list, PadBinding command)
        {
            var row = Row(out var name);
            var cells = new Label[PadMap.Slots];
            for (int s = 0; s < PadMap.Slots; s++)
            {
                int slot = s;
                var cell = InputCell(() => StartPadWait(command, slot), () => ClearPad(command, slot), () => StepGesture(command, slot));
                cells[s] = cell;
                row.Add(cell);
            }
            list.Add(row);
            _padRows.Add((command, name, cells));
        }

        static VisualElement Row(out Label name)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.height = RowHeight;
            row.style.flexShrink = 0;
            name = Cell(NameWidth, false);
            row.Add(name);
            return row;
        }

        /// <summary>An input cell: left click / A = wait, right click / X = clear, Y = <paramref name="tertiary"/>.</summary>
        Label InputCell(System.Action wait, System.Action clear, System.Action tertiary)
        {
            var cell = Cell(0, true);
            cell.style.flexGrow = 1;
            cell.RegisterCallback<PointerDownEvent>(e =>
            {
                if (_view.Menu.State.Request != null) return;
                if (e.button == 1) clear();
                else if (e.button == 0) wait();
            });
            cell.RegisterCallback<PointerEnterEvent>(_ => cell.style.color = G3Palette.TextHighlight.ToColor());
            cell.RegisterCallback<PointerLeaveEvent>(_ => cell.style.color = G3Palette.Text.ToColor());
            G3PadFocus.MakeFocusable(cell, wait, clear, tertiary);
            return cell;
        }

        static Label Cell(float width, bool input)
        {
            var label = new Label { pickingMode = input ? PickingMode.Position : PickingMode.Ignore };
            if (width > 0)
            {
                label.style.width = width;
                label.style.flexShrink = 0;
            }
            label.style.height = RowHeight;
            label.style.paddingLeft = 6;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            G3Text.Font(label, FontSize, bold: true);
            label.style.color = input ? G3Palette.Text.ToColor() : G3MenuSettingsPages.TextColor;
            return label;
        }

        void ShowRequest(bool on)
        {
            if (_request == null) return;
            _request.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (!on || _requestText == null) return;
            _requestText.text = _view.Text(WaitText) +
                                (_padWaiting != null ? "\n" + (Russian ? "(Start — отмена; LB + кнопка — сочетание)" : "(Start cancels; LB + button = chord)") : "");
        }

        void Render()
        {
            if (_view.Localizer == null) return;
            foreach (var (key, name, cells) in _rows)
            {
                name.text = KeyName(key);
                for (int s = 0; s < cells.Length; s++)
                {
                    bool waiting = _waiting.HasValue && _waiting.Value.key == key && _waiting.Value.slot == s;
                    cells[s].text = waiting ? "..." : _controls.Keys.Get(key, s).Name;
                }
            }
            var family = G3Pad.Family;
            foreach (var (command, name, cells) in _padRows)
            {
                name.text = command.IsAction ? PadMap.ActionText(command.Action, Language) : KeyName(command.Key);
                for (int s = 0; s < cells.Length; s++)
                {
                    bool waiting = _padWaiting.HasValue && _padWaiting.Value.command.SameTarget(command) && _padWaiting.Value.slot == s;
                    var b = _controls.Pad.Buttons.Get(command, s);
                    cells[s].text = waiting ? "..." : b.HasValue ? PadGlyphs.Name(b.Value, family, Russian) + GestureSuffix(b.Value) : "";
                }
            }
        }

        string GestureSuffix(PadBinding b) => b.Gesture == PadGesture.Tap ? (Russian ? " (нажать)" : " (tap)") : "";

        // fix-205: a port key has no G3 string; its own RU/EN name
        string KeyName(SessionKey key) =>
            KeyMap.IsPortKey(key) ? KeyMap.PortText(key, Language) : _view.Text(new TextId(KeyMap.TextId(key)));
    }
}
