using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>
    /// The in-game menu (G3's main menu during a game): Esc opens it unless a conversation runs (Esc ends the
    /// conversation there) or a page is open (Esc closes the page). While it is open the game is paused (time scale 0),
    /// the world's sounds are muted (the music plays on), the hero's controls, the page keys and the save keys rest. Esc steps back (<see cref="GameMenuState.Back"/>) and
    /// resumes from the main page. Saving and loading go through <see cref="SaveGameController"/>; saving is refused
    /// with G3's message while a save blocker holds. It runs before the other views so it sees the state Esc meets.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class GameMenu : MonoBehaviour, ISessionView
    {
        SaveGameController _saves;
        GameScreens _screens;
        HeroControlLock _controls;
        G3MenuView _view;
        GameSession _session;
        bool _paused;

        static readonly TextId QuitConfirmText = new TextId("HUD_Confirm_Quit");
        static readonly TextId LoadFailedText = new TextId("HUD_File_LoadFailed");

        /// <summary>The port option "Continue button" (fix-138, <see cref="PortOptionsApplier"/>): Continue heads the title's and the dead hero's menu.</summary>
        public static bool ContinueOffered { get; set; } = true;

        public GameMenuState State { get; } = new GameMenuState();
        /// <summary>fix-233: shut while the click or key that skipped a video could still reach the menu (<see cref="SwallowSkipInput"/>).</summary>
        public Myrtana.Ui.Skin.MenuInputGate InputGate { get; } = new Myrtana.Ui.Skin.MenuInputGate();
        /// <summary>The program flow (title, new game, credits; Docs/game-start.md); null = in-game menu only.</summary>
        public GameStartFlow StartFlow { get; set; }
        /// <summary>The menu stands alone on the title screen.</summary>
        public bool AtTitle => State.AtTitle;
        /// <summary>The hero died: the first button starts a new game instead of resuming.</summary>
        public bool GameOver => StartFlow != null && StartFlow.Flow != null && StartFlow.Flow.GameOver;
        /// <summary>The main page's buttons now (<see cref="MainMenuButtons.For"/>).</summary>
        public MainMenuButtons Buttons => MainMenuButtons.For(AtTitle, GameOver, ContinueOffered);
        public G3MenuView View => _view;
        public SaveGameController Saves => _saves;
        public GameSession Session => _session;

        public void Init(SaveGameController saves, GameScreens screens, HeroControlLock controls, G3MenuView view)
        {
            _saves = saves;
            _screens = screens;
            _controls = controls;
            _view = view;
            view.Attach(this);
            State.Changed += OnStateChanged;
        }

        public void Bind(GameSession session)
        {
            State.Close();
            _session = session;
        }

        /// <summary>
        /// fix-233: the menu appears under the input that skipped a video (the last logo, the credits): no click, key or
        /// pad button reaches it for <see cref="Myrtana.Ui.Skin.MenuInputGate.GraceSeconds"/> and until everything is released.
        /// </summary>
        public void SwallowSkipInput() => InputGate.Close(Time.unscaledTimeAsDouble);

        /// <summary>A mouse button, key or pad button is held now.</summary>
        static bool AnyInputHeld()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            return kb != null && kb.anyKey.isPressed ||
                   mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed) ||
                   global::G3.Unity.G3Pad.AnyDown;
        }

        void Update()
        {
            InputGate.Tick(Time.unscaledTimeAsDouble, InputGate.Blocks && AnyInputHeld());
            if (InputGate.Blocks)
            {
                _view?.PadFocus?.Tick(false); // no move, no press; the ring returns with the gate
                return;
            }
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && !G3TutorialView.EscapeConsumed) Escape();
            // gamepad-1: Start opens the menu / steps back; B steps back inside the menu (pages handle their own B)
            else if (!G3TutorialView.EscapeConsumed && (global::G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Menu) && !_screens.IsPageOpen ||
                                                        State.IsOpen && !(_view != null && _view.Settings.WaitingForPad) &&
                                                        global::G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Back)))
                Escape();
            if (State.IsOpen) _view?.Settings.Tick(); // the custom key list's capture
            // gamepad-1: the pad's focus over the open page (not while a key list cell waits for its input)
            _view?.PadFocus?.Tick(State.IsOpen && !_view.Settings.WaitingForKey);
            if (State.IsOpen) _view?.PadHints?.Tick(G3.Player.PadHintPage.Menu, _view.Languages?.TextLanguage);
        }

        /// <summary>What Esc does: open the menu, or one step back in it (also closes a confirm box).</summary>
        public void Escape()
        {
            if (_view != null && _view.Settings.CancelKeyWait()) return; // Esc ends the key list's wait, not the page
            if (State.IsOpen)
            {
                if (State.AtTitle && State.Page == MenuPage.Main && State.Request == null) AskQuit(); // G3: Esc at the title asks to quit
                else State.Back();
                return;
            }
            if (CanOpen) State.Open();
        }

        public bool CanOpen => _session != null && _view != null && !_session.Dialog.IsActive && !_screens.IsPageOpen &&
                               (StartFlow == null || StartFlow.MenuAllowed);

        public void Resume() => State.Close();

        /// <summary>The first button: New Game at the title and after the hero's death, else Resume.</summary>
        public void FirstButton()
        {
            if ((AtTitle || GameOver) && StartFlow != null) StartFlow.AskNewGame();
            else Resume();
        }

        public void ShowCredits() => StartFlow?.ShowCredits();

        /// <summary>A save exists for Continue (fix-138; fix-222: from the save index, no file opened; Continue itself rescans).</summary>
        public bool CanContinue => _saves != null && _saves.ContinueSlots().Count > 0;

        /// <summary>
        /// fix-222: the work of the title's first frame done ahead (during the logos): the save index is waited for and
        /// the main page's blurred side fill is made.
        /// </summary>
        public void Prewarm()
        {
            _ = CanContinue;
            _view?.Prewarm();
        }

        /// <summary>
        /// fix-138 [owner]: loads the newest save of any kind (<see cref="SaveGameController.LoadNewest"/>, which skips a corrupt
        /// one for the next newest); when none loads the menu comes back with G3's "could not be loaded" notice.
        /// </summary>
        public void Continue()
        {
            if (!CanContinue) return;
            Resume();
            if (_saves.LoadNewest()) return;
            State.Open();
            State.Ask(new MenuRequest(LoadFailedText, notice: true));
        }

        public void AskQuit() => State.Ask(new MenuRequest(QuitConfirmText, Quit));

        /// <summary>
        /// fix-155 [owner]: at the title after an unclean exit, when the hidden recovery save is newer than every normal save
        /// (<see cref="SaveGameController.RecoveryOffer"/>), G3's confirm box asks "Игра завершилась некорректно. Восстановить
        /// прогресс от …?": Yes loads it (as the visible slot "Восстановление"), No / Esc discards it. True when asked.
        /// </summary>
        public bool OfferRecovery()
        {
            if (_saves == null || State.Request != null) return false;
            var at = _saves.RecoveryOffer();
            if (at == null) return false;
            var local = at.Value.ToLocalTime();
            Debug.Log($"[Myrtana] crash recovery offered: snapshot of {local:dd.MM.yyyy HH:mm:ss}");
            State.Ask(new MenuRequest(() => CrashRecovery.PromptText(_view?.Languages?.TextLanguage ?? _session?.Settings.Languages.TextLanguage, local),
                                      RestoreRecovery, () => _saves.DiscardRecovery()));
            return true;
        }

        void RestoreRecovery()
        {
            Resume();
            if (_saves.RestoreRecovery()) return;
            State.Open();
            State.Ask(new MenuRequest(LoadFailedText, notice: true));
        }

        void OnStateChanged()
        {
            _view?.PadFocus?.Reset(); // a new page: the pad focus starts at its first element
            if (State.IsOpen && !_paused) Pause();
            else if (!State.IsOpen && _paused) Unpause();
            _view.Render(this);
        }

        void Pause()
        {
            _paused = true;
            GamePause.Set(this, true);
            WorldSounds.Mute(this, true); // G3 mutes everything but the music while the menu is open
            _controls.Acquire(this);
            _screens.Suspended = true;
            _saves.Suspended = true;
        }

        void Unpause()
        {
            _paused = false;
            GamePause.Set(this, false);
            WorldSounds.Mute(this, false);
            _controls.Release(this);
            _screens.Suspended = false;
            _saves.Suspended = false;
        }

        /// <summary>Saves to the slot and resumes; refused with G3's message while a blocker holds.</summary>
        public void SaveTo(string slot)
        {
            if (_saves.SaveBlockReason != null || !_saves.SaveTo(slot))
            {
                State.Ask(new MenuRequest(G3MenuView.SaveFailedText, notice: true));
                return;
            }
            Resume();
        }

        /// <summary>Loads the slot (a new session; <see cref="Bind"/> closes the menu).</summary>
        public void Load(string slot)
        {
            Resume();
            _saves.Load(slot);
        }

        public void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>Never leaves the game frozen (destroyed, or a script reload in the editor while paused).</summary>
        void OnDisable()
        {
            if (!_paused) return;
            GamePause.Set(this, false);
            WorldSounds.Mute(this, false);
        }
    }
}
