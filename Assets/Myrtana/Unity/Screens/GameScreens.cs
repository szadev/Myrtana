using System.Collections.Generic;
using G3.Player;
using G3.Unity;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// Opens and closes the full-screen pages by key (G3 session keys through <see cref="G3Input"/>, rebindable):
    /// InventoryMode I / Backspace, JournalModeLog L, JournalModeCharScreen C, JournalModeMagBook B, JournalModeMap M.
    /// One page at a time; the same key or Esc closes it; none opens during a conversation, except the page the
    /// conversation's script holds it for (fix-140: trade, <see cref="OpenForDialog"/>), which only its own close or Esc
    /// shuts and whose close lets the conversation go on. While a page is open the hero's controls are suspended. An
    /// open page is redrawn when the state it shows changes.
    /// </summary>
    public sealed class GameScreens : MonoBehaviour, ISessionView
    {
        readonly Dictionary<Key, GameScreen> _byKey = new Dictionary<Key, GameScreen>();
        readonly Dictionary<SessionKey, GameScreen> _byAction = new Dictionary<SessionKey, GameScreen>();
        HeroControlLock _controls;
        GameSession _session;
        GameScreen _open;
        bool _dirty;
        GameScreen _waiting;
        bool _waitingForDialog, _dialogPage;
        int _openedFrame = -1;

        public void Init(HeroControlLock controls) => _controls = controls;

        /// <summary>fix-140: the open page holds the conversation (trade from a trade topic).</summary>
        public bool IsDialogPage => _open != null && _dialogPage;

        /// <summary>A page is open (Esc belongs to it then, not to the game menu).</summary>
        public bool IsPageOpen => _open != null;

        /// <summary>The open page, or null.</summary>
        public GameScreen Current => _open;

        /// <summary>Set while the game menu is open: page keys and the open page's keys rest.</summary>
        public bool Suspended { get; set; }

        /// <summary>
        /// Adds a page under the key G3 opens it with by default (I inventory, L log, C character, B spell book, M map):
        /// such a key stands for its session key (<see cref="PageKey"/>), so the page follows the player's binding. Other
        /// keys open the page as they are.
        /// </summary>
        public void Add(Key key, GameScreen screen)
        {
            if (PageKey(key) is SessionKey action) _byAction[action] = screen;
            else _byKey.Add(key, screen);
        }

        /// <summary>Adds a page opened by a session key.</summary>
        public void Add(SessionKey action, GameScreen screen) => _byAction[action] = screen;

        /// <summary>The page a key opens (null = none); a G3 page key finds the page of its session key.</summary>
        public GameScreen ByKey(Key key) =>
            PageKey(key) is SessionKey action ? ByAction(action) : _byKey.TryGetValue(key, out var screen) ? screen : null;

        /// <summary>The page a session key opens (null = none).</summary>
        public GameScreen ByAction(SessionKey action) => _byAction.TryGetValue(action, out var screen) ? screen : null;

        /// <summary>The page session key whose default (Key1) is <paramref name="key"/>, or null.</summary>
        public static SessionKey? PageKey(Key key)
        {
            int dik = G3Input.DikOf(key);
            if (dik == 0) return null;
            foreach (var action in PageActions)
                if (KeyMap.Default().Get(action, 0) == KeyInput.Key(dik)) return action;
            return null;
        }

        static readonly SessionKey[] PageActions =
        {
            SessionKey.InventoryMode, SessionKey.JournalModeLog, SessionKey.JournalModeCharScreen, SessionKey.JournalModeMagBook,
            SessionKey.JournalModeMap,
        };
        /// <summary>A page was opened (the tutorial tips of the pages).</summary>
        public event System.Action<GameScreen> Opened;

        /// <summary>Opens a page from code (trade after a dialog …).</summary>
        public void Open(GameScreen screen)
        {
            if (_session == null || _session.Dialog.IsActive && !_session.Dialog.IsHeldByPage) return;
            CloseCurrent();
            _dialogPage = _session.Dialog.IsHeldByPage;
            _open = screen;
            _openedFrame = Time.frameCount;
            _controls.Acquire(this);
            screen.Open(_session);
            Opened?.Invoke(screen);
        }

        /// <summary>
        /// Opens a page as soon as no conversation runs (a dialog's last event fires while the dialog is still closing).
        /// </summary>
        public void OpenAfterDialog(GameScreen screen)
        {
            _waiting = screen;
            _waitingForDialog = false;
        }

        /// <summary>
        /// fix-140: opens a page the running conversation holds for (<see cref="Myrtana.Story.DialogService.HoldForPage"/>)
        /// on the next frame, once the script has paused; closing it resumes the conversation.
        /// </summary>
        public void OpenForDialog(GameScreen screen)
        {
            _waiting = screen;
            _waitingForDialog = true;
        }

        public void CloseCurrent()
        {
            if (_open == null) return;
            _open.Close();
            _open = null;
            _controls.Release(this);
            if (!_dialogPage) return;
            _dialogPage = false;
            _session?.Dialog.ResumeAfterPage(); // G3 IS_Idle_Trade: no page open any more -> ProcessInfo completes the line
        }

        public void Bind(GameSession session)
        {
            CloseCurrent();
            _waiting = null;
            _dialogPage = false;
            if (_session != null) Unwatch(_session);
            _session = session;
            Watch(session);
        }

        void Watch(GameSession session)
        {
            session.Hero.Changed += MarkDirty;
            session.Hero.Stats.Changed += MarkDirtyAttribute;
            session.QuestLog.Changed += MarkDirty;
            session.Fame.Changed += MarkDirty;
            session.Settings.Languages.Changed += MarkDirty;
            session.Items.Inventories.Of(Core.ActorId.Player).Changed += MarkDirty;
            session.Items.QuickSlots.Changed += MarkDirty;
        }

        void Unwatch(GameSession session)
        {
            session.Hero.Changed -= MarkDirty;
            session.Hero.Stats.Changed -= MarkDirtyAttribute;
            session.QuestLog.Changed -= MarkDirty;
            session.Fame.Changed -= MarkDirty;
            session.Settings.Languages.Changed -= MarkDirty;
            session.Items.Inventories.Of(Core.ActorId.Player).Changed -= MarkDirty;
            session.Items.QuickSlots.Changed -= MarkDirty;
        }

        void OnDestroy()
        {
            if (_session != null) Unwatch(_session);
        }

        /// <summary>Asks the open page to redraw on the next frame.</summary>
        public void MarkDirty() => _dirty = true;

        void MarkDirtyAttribute(string _) => _dirty = true;

        void Update()
        {
            if (_session == null || Suspended) return;
            if (_waiting != null)
            {
                if (_waitingForDialog ? _session.Dialog.IsHeldByPage : !_session.Dialog.IsActive)
                {
                    Open(_waiting);
                    _waiting = null;
                }
                else if (_waitingForDialog && !_session.Dialog.IsActive) _waiting = null; // the talk ended (aborted) first
            }
            if (_dialogPage && _open != null && !_session.Dialog.IsHeldByPage) CloseCurrent(); // the talk was cut off (Abort)
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            bool keys = !_session.Dialog.IsActive || IsDialogPage;
            if (kb != null && keys && Time.frameCount != _openedFrame) HandleKeys(kb);
            if (keys && Time.frameCount != _openedFrame && Time.frameCount != _keyFrame) HandlePad();
            if (_open == null) return;
            if (_dirty)
            {
                _dirty = false;
                _open.Render();
            }
            if (Time.frameCount != _openedFrame) _open.Tick(); // the key that opened a page must not act in it
            UpdatePadHints();
        }

        int _keyFrame = -1;
        G3PadHintBar _hints;

        /// <summary>gamepad-1: the pad hint bar sits at the bottom of the open page while the pad is in use.</summary>
        void UpdatePadHints()
        {
            var host = _open?.HintHost;
            if (host == null) return;
            if (_hints == null) _hints = new G3PadHintBar();
            if (_hints.parent != host) host.Add(_hints);
            _hints.Tick(_open.PadHints, _session?.Settings.Languages.TextLanguage);
        }

        /// <summary>
        /// gamepad-1 (Docs/gamepad.md §4): B or Start on a page = Esc; View on a journal page = the next journal page; the
        /// pad's page bindings (d-pad down = inventory, View = map / quest log) open pages in play — the resolver is
        /// inactive while a page has the controls, so these do not fire inside a page.
        /// </summary>
        void HandlePad()
        {
            if (_open != null && (G3Pad.Ui(UiCommand.Back) || G3Pad.Ui(UiCommand.Menu)) && !G3TutorialView.EscapeConsumed)
            {
                if (!_open.HandleEscape()) CloseCurrent();
                return;
            }
            if (IsDialogPage) return;
            if (_open != null && G3Pad.Ui(UiCommand.View))
            {
                SessionKey? current = null;
                foreach (var kv in _byAction) if (kv.Value == _open) current = kv.Key;
                if (current == null || System.Array.IndexOf(JournalCycle.Order, current.Value) < 0) return;
                var next = current.Value;
                for (int i = 0; i < JournalCycle.Order.Length; i++)
                {
                    next = JournalCycle.Next(next);
                    var page = ByAction(next);
                    if (page == null || page == _open) continue;
                    Open(page);
                    return;
                }
                return;
            }
            if (_open != null) return;
            foreach (var kv in _byAction)
            {
                if (!G3Input.PadPressed(kv.Key)) continue;
                Toggle(kv.Value);
                return;
            }
        }

        void HandleKeys(Keyboard kb)
        {
            _keyFrame = -1;
            if (_open != null && kb.escapeKey.wasPressedThisFrame && !G3TutorialView.EscapeConsumed)
            {
                _keyFrame = Time.frameCount;
                if (!_open.HandleEscape()) CloseCurrent();
                return;
            }
            if (IsDialogPage) return; // fix-140: no other page while the conversation waits for this one
            foreach (var kv in _byAction)
            {
                if (!PressedFor(kv.Key)) continue;
                Toggle(kv.Value);
                return;
            }
            foreach (var kv in _byKey)
            {
                if (!kb[kv.Key].wasPressedThisFrame || (_open != null && _open.ConsumesKey(kv.Key))) continue;
                Toggle(kv.Key);
                return;
            }
        }

        /// <summary>An input of the session key went down this frame and the open page does not use that key itself.</summary>
        bool PressedFor(SessionKey action)
        {
            foreach (var input in G3Input.Keys.Inputs(action))
            {
                if (!G3Input.WasPressed(input)) continue;
                if (_open != null && input.Device == KeyDevice.Keyboard && G3Input.UnityKey(input.Code) is Key k && _open.ConsumesKey(k)) continue;
                if (input.Device == KeyDevice.Mouse && _open == null && !global::G3.Unity.G3Cursor.Captured) continue;
                return true;
            }
            return false;
        }

        /// <summary>What a page key does: opens its page, or closes it when it is the open one.</summary>
        public void Toggle(Key key)
        {
            var screen = ByKey(key);
            if (screen != null) Toggle(screen);
        }

        /// <summary>What a session page key does (<see cref="Toggle(Key)"/>).</summary>
        public void Toggle(SessionKey action)
        {
            var screen = ByAction(action);
            if (screen != null) Toggle(screen);
        }

        void Toggle(GameScreen screen)
        {
            if (_open == screen) CloseCurrent();
            else if (!Blocked) Open(screen);
        }

        /// <summary>G3 blocks the HUD pages while the hero swims (Docs/gameplay-logic.md §6, Docs/hero.md).</summary>
        bool Blocked => _controls != null && _controls.Player != null && _controls.Player.Swimming ||
                        _session != null && _session.SpellEffects.Hero.Restricted; // animal form, ghost, Dusk to Dawn (magic-3)
    }
}
