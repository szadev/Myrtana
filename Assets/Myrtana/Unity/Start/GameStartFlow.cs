using System;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Runs the program flow (<see cref="GameFlow"/>, Docs/game-start.md): the start-up logos, the title screen (the
    /// main menu with New Game, Load, Options, Credits, Quit and the AB switch over the menu background, title music),
    /// New Game (AB question → hero to the G3 start position in Ardea → loading screen → intro video → play with the
    /// TUT_Start tip; the session's start script then sends Ardea's orcs at the hero), loads from anywhere, the credits,
    /// and the hero's death (the menu offers New Game). The developer quick start (<see cref="DevStart"/>) starts a new
    /// game where the scene's hero stands, as before this flow existed.
    /// </summary>
    public sealed class GameStartFlow : MonoBehaviour, ISessionView
    {
        GameBootstrap _game;
        G3Player _player;
        GameMenu _menu;
        HeroControlLock _controls;
        HudMessages _messages;
        G3VideoScreen _video;
        readonly TitleMusic _music = new TitleMusic();
        GameFlow _flow;
        GameSession _session;
        GamePhase _entered = (GamePhase)(-1);
        string _videoShown;
        bool _paused;
        bool _recoveryAsked;
        readonly object _logoHold = new object();

        public GameFlow Flow => _flow;
        public NewGameOptions Options { get; private set; }
        /// <summary>Keeps the world out of the logos and the title until a game starts (null: quick start).</summary>
        public TitleStage Title { get; set; }
        /// <summary>Esc may open the game menu (only in play; the title keeps it open itself).</summary>
        public bool MenuAllowed => _flow == null || _flow.Phase == GamePhase.Playing;
        /// <summary>Asks for a tutorial tip (the tutorial box); null shows TUT_Start as a game message.</summary>
        public Action<TutorialTip> RequestTip { get; set; }

        /// <summary>
        /// fix-115: the start-up logos from the first frame, while the content packs still load and before the game is
        /// built (G3 shows them at once); <see cref="Init"/> and <see cref="Run"/> follow once the content is there, and the
        /// title waits for both (<see cref="GameFlow.ContentReady"/>).
        /// </summary>
        public static GameStartFlow BeginLogos(GameObject host, G3VideoScreen video, TitleStage title)
        {
            var start = host.AddComponent<GameStartFlow>();
            start._video = video;
            start.Title = title;
            start._flow = new GameFlow(GameFlow.DefaultLogos, quickStart: false, waitForContent: true);
            start._flow.Changed += start.Apply;
            StartupTimeline.Mark("logo sequence begins (content still loading)");
            start.Apply();
            return start;
        }

        /// <summary>The logos are over (or not shown): building the game now stalls no video.</summary>
        public bool LogosOver => _flow == null || _flow.LogosOver;

        /// <summary>The content load failed: the logos stop, nothing follows.</summary>
        public void Abort()
        {
            if (_flow != null) _flow.Changed -= Apply;
            _flow = null;
            _video?.Stop();
        }

        public void Init(GameBootstrap game, G3Player player, GameMenu menu, HeroControlLock controls, HudMessages messages,
                         NewGameOptions options, G3VideoScreen video = null)
        {
            _game = game;
            _player = player;
            _menu = menu;
            _controls = controls;
            _messages = messages;
            Options = options;
            if (_video == null) _video = video != null ? video : G3VideoScreen.Create(transform); // the start-up's (fix-98)
            if (_menu != null) _menu.StartFlow = this;
            // fix-171: the in-game lighting A/B (-g3perfhere) measures only while a game is played
            G3PerfHere.GameReady = () => this != null && _flow != null && _flow.Phase == GamePhase.Playing && !LoadingHold.IsHeld &&
                                         (_menu == null || !_menu.State.IsOpen);
            G3PerfHere.Notice = text => { if (this != null) _messages?.Show(text); };
        }

        /// <summary>Starts the flow: the quick start's new game at once, else the logos and the title.</summary>
        public void Run()
        {
            bool quick = DevStart.QuickStart || _menu == null; // without the G3 GUI there is no title to show
            if (_flow != null && !quick)
            {
                // the logos started at start-up (BeginLogos); the game is built now, the title may follow them
                if (_flow.Phase == GamePhase.Logos && !_flow.WaitingForContent) _controls?.Acquire(_logoHold);
                _flow.ContentReady();
                return;
            }
            if (_flow != null) _flow.Changed -= Apply;
            if (quick) _video.Stop(); // no logos: lift a start-up cover
            _entered = (GamePhase)(-1);
            _videoShown = null;
            _flow = new GameFlow(quick ? null : GameFlow.DefaultLogos, quick);
            _flow.Changed += Apply;
            Apply();
        }

        public void Bind(GameSession session)
        {
            _session = session;
            // A load started elsewhere (menu, quick-load): wait for its area like a new game.
            if (_flow != null && _flow.Phase != GamePhase.Loading) _flow.StartLoad();
        }

        /// <summary>Main menu, first button at the title or after the hero's death.</summary>
        public void AskNewGame()
        {
            if (_flow == null || _menu == null) return;
            _menu.State.Ask(new MenuRequest(_flow.NewGameQuestion(Options.AlternativeBalancing), () => _flow.StartNewGame()));
        }

        public void ShowCredits() => _flow?.ShowCredits();

        /// <summary>Ends the current video as a skip does (<see cref="SmokeRun"/>: the intro of the smoke test's new game).</summary>
        public void SkipVideo()
        {
            _video?.Stop();
            _flow?.VideoFinished();
        }

        /// <summary>Acts on entering a phase or a new video of it.</summary>
        void Apply()
        {
            if (_flow == null) return;
            var phase = _flow.Phase;
            bool entered = phase != _entered;
            var left = _entered;
            _entered = phase;
            if (entered && left == GamePhase.Intro) PauseForIntro(false);
            if (entered && left == GamePhase.Logos) _controls?.Release(_logoHold);
            if (entered && phase == GamePhase.Logos) _controls?.Acquire(_logoHold); // the hero waits behind the logos
            if (entered && left == GamePhase.Title) _music.Active = false;
            if (entered && IsSilentPhase(phase) != IsSilentPhase(left)) TitleMusic.Mute(IsSilentPhase(phase)); // G3 mutes the music under videos

            string video = _flow.CurrentVideo;
            if (video == null) _videoShown = null;
            if (entered)
            {
                switch (phase)
                {
                    case GamePhase.Title:
                        if (!_titleTimed)
                        {
                            _titleTimed = true;
                            StartupTimeline.Mark("title", last: true);
                            StartCoroutine(TitleFirstFrame(Time.realtimeSinceStartupAsDouble));
                        }
                        _music.Active = true;
                        OpenTitleMenu();
                        // fix-233: the click or key that skipped the last logo (or the credits) must not press Continue
                        if (_menu != null) _menu.SwallowSkipInput();
                        // fix-155 [owner]: after an unclean exit the title first asks whether to restore the recovery save
                        if (!_recoveryAsked && _menu != null)
                        {
                            _recoveryAsked = true;
                            _menu.OfferRecovery();
                        }
                        break;
                    case GamePhase.Loading:
                        Title?.End(); // the world loads at the new or loaded hero position
                        if (_menu != null)
                        {
                            _menu.State.AtTitle = false;
                            if (_menu.State.IsOpen) _menu.Resume();
                        }
                        if (_flow.Starting == StartKind.NewGame) BeginNewGame();
                        break;
                    case GamePhase.Intro:
                        PauseForIntro(true);
                        break;
                    case GamePhase.Quit:
                        if (_menu != null) _menu.Quit();
                        break;
                }
            }
            // fix-137: after the last logo (or with none) the game is built (long main-thread frames) before the title:
            // black covers it, never the camera's empty view; the title lifts the cover.
            if (phase == GamePhase.Logos && _flow.WaitingForContent && video == null && _video != null) _video.Cover();
            if (entered && phase == GamePhase.Title && _video != null) _video.Uncover();
            if (video != null && video != _videoShown)
            {
                _videoShown = video;
                var flow = _flow;
                _video.Play(video, () => { if (flow.CurrentVideo == video) flow.VideoFinished(); }, flow.CurrentVideoSkip);
                // rebrand: the remaster's own credits over the first credits video, then G3's (and CP's) as they are
                if (phase == GamePhase.Credits && video == GameFlow.CreditVideos[0])
                    _video.Caption(Myrtana.Ui.Skin.RemasterBrand.CreditsCaption(LanguagePreferences.Load().TextLanguage),
                                   Myrtana.Ui.Skin.RemasterBrand.CreditsCaptionSeconds);
            }
        }

        bool _titleTimed;

        /// <summary>fix-222: when the title's first frame (menu shown) has been drawn, for the start-up timeline.</summary>
        System.Collections.IEnumerator TitleFirstFrame(double entered)
        {
            yield return new WaitForEndOfFrame();
            double drawn = Time.realtimeSinceStartupAsDouble;
            Debug.Log($"[Myrtana] start-up {drawn:0.00} s: title first frame drawn ({(drawn - entered) * 1000:0} ms after the title began)");
        }

        static bool IsVideoPhase(GamePhase p) => p == GamePhase.Logos || p == GamePhase.Intro || p == GamePhase.Credits;

        /// <summary>No situation music: under the videos, and once the end credits hand over to Quit (the game ends; in the
        /// editor, or when quitting is held, the explore music must not come back after the credits).</summary>
        /// <summary>A full-screen video phase (logos, intro, credits) or a video still playing: no cursor (fix-87).</summary>
        public bool InVideo => _flow != null && IsVideoPhase(_flow.Phase) || _video != null && _video.IsPlaying;

        /// <summary>The loading phase of a new or loaded game: no cursor (fix-87).</summary>
        public bool InLoading => _flow != null && _flow.Phase == GamePhase.Loading;

        static bool IsSilentPhase(GamePhase p) => IsVideoPhase(p) || p == GamePhase.Quit;

        void OpenTitleMenu()
        {
            if (_menu == null) return;
            _menu.State.AtTitle = true;
            if (!_menu.State.IsOpen) _menu.State.Open();
        }

        /// <summary>The G3 start (or the quick start where the hero stands), then the session.</summary>
        void BeginNewGame()
        {
            GameStartDef start = null;
            bool real = !_flow.QuickStart && _game.Content != null &&
                        _game.Content.Database.TryGet(GameStartDef.DefinitionId, out start);
            if (real) PlaceHero(start.HeroPose);
            else if (!_flow.QuickStart)
            {
                CoverUntilReady(_player.transform.position);
                Debug.LogWarning("[Myrtana] no gameStart in the content pack (re-import it): new game where the hero stands.");
            }
            _game.NewGame();
            if (!real) _game.Session?.Start?.DropAlarms();
        }

        void PlaceHero(WorldPose pose)
        {
            var feet = new Vector3(pose.X, pose.Y, pose.Z);
            _player.Teleport(feet);
            _player.SetLook(pose.Yaw, _player.Pitch);
            CoverUntilReady(feet);
        }

        /// <summary>The loading screen until the cells under <paramref name="feet"/> are loaded; the hero waits.</summary>
        static void CoverUntilReady(Vector3 feet)
        {
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            if (streamer != null && !streamer.IsReady(feet))
            {
                streamer.Hold(true);
                G3LoadingOverlay.ShowUntilReady(streamer, feet);
            }
        }

        bool WorldReady()
        {
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            return streamer == null || (!streamer.IsHolding && streamer.IsReady(_player.transform.position));
        }

        void Update()
        {
            if (_flow == null) return;
            switch (_flow.Phase)
            {
                case GamePhase.Title:
                    if (_menu != null && !_menu.State.IsOpen) OpenTitleMenu(); // e.g. a load from the title failed
                    break;
                case GamePhase.Loading:
                    if (_session != null && WorldReady()) _flow.WorldReady(G3VideoScreen.Exists(GameFlow.IntroVideo));
                    break;
                case GamePhase.Playing:
                    if (_flow.TutorialPending && _session != null && (_menu == null || !_menu.State.IsOpen))
                    {
                        // G3 writes the line breaks of its strings as the two characters \n
                        if (RequestTip != null) RequestTip(TutorialTip.Start);
                        else _messages?.Show(_session.Localizer.Text(GameFlow.TutorialStart).Replace("\\n", "\n"));

                        _flow.TutorialShown();
                    }
                    if (_session != null && !_flow.GameOver && _session.Combat.Vitals.IsDead(ActorId.Player)) _flow.PlayerDied();
                    break;
            }
        }

        /// <summary>G3 plays the intro with the game stopped (gCSession::Start plays it before the session runs).</summary>
        void PauseForIntro(bool on)
        {
            if (on == _paused) return;
            _paused = on;
            if (on)
            {
                GamePause.Set(this, true);
                _controls?.Acquire(this);
            }
            else
            {
                GamePause.Set(this, false);
                _controls?.Release(this);
            }
        }

        void OnDisable()
        {
            if (_paused) GamePause.Set(this, false);
        }
    }
}
