using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>Where the program is between start-up and play (Docs/game-start.md "Flow").</summary>
    public enum GamePhase
    {
        /// <summary>The start-up logo videos (logo.ini).</summary>
        Logos,
        /// <summary>The title screen: the main menu without a game behind it.</summary>
        Title,
        /// <summary>The loading screen of a new or loaded game.</summary>
        Loading,
        /// <summary>The intro video of a new game (after the world is loaded, before play).</summary>
        Intro,
        Playing,
        /// <summary>The credit videos (from the main menu).</summary>
        Credits,
        Quit,
    }

    /// <summary>What the loading screen loads.</summary>
    public enum StartKind { None, NewGame, LoadGame }

    /// <summary>
    /// The G3 (CP 1.75) program flow as a state machine (Docs/game-start.md): logo videos → title (main menu) → New Game
    /// (asks with the Alternative Balancing remark) → loading screen at the start position → intro video
    /// (<c>G3_Intro.bik</c>, played by gCSession::Start after the world is ready) → play with the TUT_Start tip; Load goes
    /// through the loading screen straight into play; Credits plays <c>G3_Credits2.bik</c> then <c>G3_Credits.bik</c> and
    /// returns. The player's death keeps the game ("The End"); the menu then offers New Game in the Resume place.
    /// A developer quick start skips the logos and the title and loads a new game at once, without the intro.
    /// </summary>
    /// <summary>How a playing video is skipped.</summary>
    public enum VideoSkip
    {
        /// <summary>Any key or a click skips it at once (the start-up logos).</summary>
        AnyPress,
        /// <summary>Space or Esc held for a second (the intro, the credits).</summary>
        Hold,
    }

    /// <summary>
    /// fix-170: a video standing on its last frame has ended, even when the player never raises its end event (seen in the
    /// editor: "stalled at frame 164" of 165, then 3 s of black). <c>G3VideoScreen</c>'s watchdog finishes it after
    /// <see cref="EndHold"/> s on the last frame instead of the 3 s stall timeout.
    /// </summary>
    public static class VideoEnd
    {
        public const float EndHold = 0.2f;

        /// <summary>True when <paramref name="frame"/> is the clip's last frame (or beyond) and has stood for
        /// <see cref="EndHold"/> s. An unknown length (<paramref name="frameCount"/> 0) or no frame yet is never an end.</summary>
        public static bool Reached(long frame, ulong frameCount, float stillFor) =>
            frameCount > 0 && frame >= 0 && (ulong)frame + 1 >= frameCount && stillFor >= EndHold;
    }

    public sealed class GameFlow
    {
        public const string IntroVideo = "G3_Intro";
        /// <summary>fix-170: the port's own third logo "Gothic 3 Remaster" in CP's CPT slot, built from the G3 install at
        /// build time (Tools/RemasterLogo/remaster_logo.py, <c>RemasterLogoBuild</c>; Docs/game-start.md 2.2).</summary>
        public const string RemasterLogo = "G3R_Remaster";
        /// <summary>G3 Ini/logo.ini (CP 1.75) is G3_Logo_01, Publisher, CPT; fix-170 replaces CPT with <see cref="RemasterLogo"/>.</summary>
        public static readonly IReadOnlyList<string> DefaultLogos = new[] { "G3_Logo_01", "Publisher", RemasterLogo };
        /// <summary>gCHUDMenuMain2 Credits: G3_Credits2.bik, then G3_Credits.bik.</summary>
        public static readonly IReadOnlyList<string> CreditVideos = new[] { "G3_Credits2", "G3_Credits" };

        public static readonly TextId NewGameWithAB = new TextId("HUD_Confirm_NewGameABYes"),
                                      NewGameWithoutAB = new TextId("HUD_Confirm_NewGameABNo"),
                                      NewGameOverRunning = new TextId("HUD_Confirm_NewGame"),
                                      TutorialStart = new TextId("TUT_Start");

        readonly IReadOnlyList<string> _logos;
        int _videoIndex;
        GamePhase _creditsReturn;

        /// <param name="logos">Start-up videos (empty: straight to the title).</param>
        /// <param name="quickStart">Developer start: no logos, no title, a new game without the intro.</param>
        /// <param name="waitForContent">fix-115: the logos run while the content still loads (G3 shows them at once); the
        /// title comes after both, the last logo and <see cref="ContentReady"/>.</param>
        public GameFlow(IReadOnlyList<string> logos, bool quickStart, bool waitForContent = false)
        {
            _logos = logos ?? Array.Empty<string>();
            QuickStart = quickStart;
            _contentReady = !waitForContent;
            if (quickStart) EnterLoading(StartKind.NewGame);
            else if (_logos.Count > 0) Phase = GamePhase.Logos;
            else if (!_contentReady)
            {
                Phase = GamePhase.Logos; // no logo to show: black until the content is there
                WaitingForContent = true;
            }
            else Phase = GamePhase.Title;
        }

        public GamePhase Phase { get; private set; }
        public bool QuickStart { get; }
        bool _contentReady;
        /// <summary>The logos are over and the title waits for the content (black screen; <see cref="ContentReady"/>).</summary>
        public bool WaitingForContent { get; private set; }
        /// <summary>No logo is playing any more (or none was due): the game may be built without stalling a video.</summary>
        public bool LogosOver => Phase != GamePhase.Logos || WaitingForContent;
        /// <summary>What <see cref="GamePhase.Loading"/> is loading.</summary>
        public StartKind Starting { get; private set; }
        /// <summary>A game exists (in play, or paused behind the menu or the credits).</summary>
        public bool GameRunning { get; private set; }
        /// <summary>The hero died in the running game ("The End").</summary>
        public bool GameOver { get; private set; }
        /// <summary>The TUT_Start tip is due (once, after a new game's intro).</summary>
        public bool TutorialPending { get; private set; }
        /// <summary>The main menu stands alone (title) rather than over a running game.</summary>
        public bool AtTitle => Phase == GamePhase.Title || (Phase == GamePhase.Credits && _creditsReturn == GamePhase.Title);

        /// <summary>The video to play now, or null.</summary>
        public string CurrentVideo
        {
            get
            {
                switch (Phase)
                {
                    case GamePhase.Logos: return _videoIndex < _logos.Count ? _logos[_videoIndex] : null;
                    case GamePhase.Intro: return IntroVideo;
                    case GamePhase.Credits: return _videoIndex < CreditVideos.Count ? CreditVideos[_videoIndex] : null;
                    default: return null;
                }
            }
        }

        /// <summary>How the current video is skipped (<see cref="SkipFor"/>).</summary>
        public VideoSkip CurrentVideoSkip => SkipFor(Phase);

        /// <summary>
        /// The start-up logos (G3_Logo_01, Publisher, G3R_Remaster) go on a single key press or click, as in G3 (fix-68); the intro
        /// and the credits keep the port's hold-to-skip (owner's earlier request: a stray key must not skip a cutscene).
        /// </summary>
        public static VideoSkip SkipFor(GamePhase phase) => phase == GamePhase.Logos ? VideoSkip.AnyPress : VideoSkip.Hold;

        /// <summary>Raised after every change of phase or flag.</summary>
        public event Action Changed;

        /// <summary>The current video ended or was skipped (Esc), or could not be played.</summary>
        public void VideoFinished()
        {
            switch (Phase)
            {
                case GamePhase.Logos:
                    if (WaitingForContent) break;
                    if (++_videoIndex < _logos.Count) Changed?.Invoke();
                    else if (_contentReady) Enter(GamePhase.Title);
                    else
                    {
                        WaitingForContent = true;
                        Changed?.Invoke();
                    }
                    break;
                case GamePhase.Intro:
                    TutorialPending = true;
                    Enter(GamePhase.Playing);
                    break;
                case GamePhase.Credits:
                    if (++_videoIndex >= CreditVideos.Count) Enter(_creditsReturn);
                    else Changed?.Invoke();
                    break;
            }
        }

        /// <summary>The content is loaded and the game built: the title follows the logos (at once if they are over).</summary>
        public void ContentReady()
        {
            if (_contentReady) return;
            _contentReady = true;
            if (!WaitingForContent) return;
            WaitingForContent = false;
            Enter(GamePhase.Title);
        }

        /// <summary>The question New Game asks: the AB remark at the title (CP), "start over?" over a running game.</summary>
        public TextId NewGameQuestion(bool alternativeBalancing) =>
            GameRunning ? NewGameOverRunning : alternativeBalancing ? NewGameWithAB : NewGameWithoutAB;

        /// <summary>New Game confirmed: the loading screen for a new game.</summary>
        public void StartNewGame() => EnterLoading(StartKind.NewGame);

        /// <summary>A save is being loaded (from the title, the menu or quick-load).</summary>
        public void StartLoad() => EnterLoading(StartKind.LoadGame);

        /// <summary>The world around the start position is loaded; a new game shows its intro when it can be played.</summary>
        public void WorldReady(bool introAvailable)
        {
            if (Phase != GamePhase.Loading) return;
            var kind = Starting;
            Starting = StartKind.None;
            GameRunning = true;
            GameOver = false;
            if (kind == StartKind.NewGame && introAvailable && !QuickStart)
            {
                Enter(GamePhase.Intro);
                return;
            }
            TutorialPending = kind == StartKind.NewGame && !QuickStart;
            Enter(GamePhase.Playing);
        }

        public void TutorialShown()
        {
            if (!TutorialPending) return;
            TutorialPending = false;
            Changed?.Invoke();
        }

        /// <summary>Main menu → Credits; back to the title or the running game afterwards.</summary>
        public void ShowCredits()
        {
            if (Phase != GamePhase.Title && Phase != GamePhase.Playing) return;
            _creditsReturn = Phase;
            _videoIndex = 0;
            Enter(GamePhase.Credits);
        }

        /// <summary>
        /// G3 info command EndGame (the epilogue's last entry): the end credits (G3_Credits2, then G3_Credits), then the
        /// game ends — G3 terminates the application.
        /// </summary>
        public void EndGame()
        {
            if (Phase == GamePhase.Credits || Phase == GamePhase.Quit) return;
            _creditsReturn = GamePhase.Quit;
            _videoIndex = 0;
            Enter(GamePhase.Credits);
        }

        public void PlayerDied()
        {
            if (!GameRunning || GameOver) return;
            GameOver = true;
            Changed?.Invoke();
        }

        public void Quit() => Enter(GamePhase.Quit);

        void EnterLoading(StartKind kind)
        {
            Starting = kind;
            Enter(GamePhase.Loading);
        }

        void Enter(GamePhase phase)
        {
            Phase = phase;
            _videoIndex = phase == GamePhase.Credits ? _videoIndex : 0;
            Changed?.Invoke();
        }
    }

    /// <summary>Which buttons the main menu (<c>IDD_MENU_MAIN_2</c>) shows (Docs/formats-gui.md §6.12).</summary>
    public readonly struct MainMenuButtons
    {
        public MainMenuButtons(bool newGame, bool resume, bool save, bool saveAvailable, bool credits, bool alternativeBalancing,
                               bool @continue = false)
        {
            Continue = @continue;
            NewGame = newGame;
            Resume = resume;
            Save = save;
            SaveAvailable = saveAvailable;
            Credits = credits;
            AlternativeBalancing = alternativeBalancing;
        }

        /// <summary>
        /// fix-138 [owner]: the port's "Continue" above the first button (<see cref="ContinueGame"/>): at the title and after
        /// the hero's death, never in a running game (Resume is there). Greyed out while no save exists.
        /// </summary>
        public bool Continue { get; }
        /// <summary>"New Game" in the first place.</summary>
        public bool NewGame { get; }
        /// <summary>"Resume game" in the first place (instead of New Game).</summary>
        public bool Resume { get; }
        /// <summary>The Save button shows (always; at the title it is greyed out).</summary>
        public bool Save { get; }
        /// <summary>Save can be pressed: false at the title, where no game runs (greyed, takes no clicks).</summary>
        public bool SaveAvailable { get; }
        public bool Credits { get; }
        /// <summary>CP's Alternative Balancing switch (title only: it is fixed for a game).</summary>
        public bool AlternativeBalancing { get; }

        /// <summary>
        /// Title: New Game, Load, a greyed-out Save, Options, Credits, Quit and the AB switch. In game: Resume (New Game after the hero's
        /// death), Load, Save, Options, Credits, Quit. With <paramref name="continueOffered"/> (the port option, fix-138) Continue
        /// heads the title's and the dead hero's menu.
        /// </summary>
        public static MainMenuButtons For(bool atTitle, bool gameOver, bool continueOffered = false) =>
            atTitle
                ? new MainMenuButtons(newGame: true, resume: false, save: true, saveAvailable: false, credits: true, alternativeBalancing: true,
                                      @continue: continueOffered)
                : new MainMenuButtons(newGame: gameOver, resume: !gameOver, save: true, saveAvailable: true, credits: true, alternativeBalancing: false,
                                      @continue: continueOffered && gameOver);
    }
}
