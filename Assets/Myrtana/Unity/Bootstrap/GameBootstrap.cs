using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Builds the <see cref="GameSession"/> from the content packs in StreamingAssets, ticks it, and creates the
    /// presentation (HUD, dialog view, voice player, interaction, saves, language keys). It is added automatically when
    /// a scene with the <see cref="G3Player"/> starts (no scene edits; the world scenes are generated), and survives
    /// scene changes. Without content (pack not imported yet) it logs how to import it and stays inactive.
    /// Loading a save creates a new session; every <see cref="ISessionView"/> is bound to it again.
    /// The content is parsed on a worker thread (<see cref="ContentPreload"/>); the game starts in the first frame after
    /// it is done, and the loading screen (<see cref="G3StartupLoader"/>) stays up until then.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        /// <summary>Air time after which the hero counts as falling (short hops over bumps do not block saving).</summary>
        const float AirborneSeconds = 0.3f;

        readonly List<ISessionView> _views = new List<ISessionView>();
        GameContent _content;
        SessionPorts _ports;
        GameSettings _settings;
        PlayerPoseSaveSection _playerPose;
        /// <summary>Crafting services and the bookstand memory (Docs/interactive-objects.md §6).</summary>
        readonly CraftingHost _crafting = new CraftingHost();
        G3Player _player;
        UnityGameLog _log;
        Task<GameContent> _contentLoad;
        NewGameOptions _ruleChoices;
        LoadingHold _loadingHold;
        TitleStage _title;
        /// <summary>The full-screen video, made at start-up so the screen is black and the first logo prepares while the
        /// content loads (fix-98); handed to the program flow.</summary>
        G3VideoScreen _video;
        /// <summary>The program flow, begun at start-up with the logos (fix-115); null on the quick start until the game.</summary>
        GameStartFlow _startFlow;
        bool _contentLogged;
        /// <summary>fix-222: the game being built, a few steps per frame while a logo plays (<see cref="BuildSteps"/>).</summary>
        IEnumerator<string> _build;
        readonly System.Text.StringBuilder _buildSteps = new System.Text.StringBuilder();
        double _buildMs, _buildLongestMs;
        int _buildFrames;
        bool _built;
        /// <summary>fix-222: made at start-up, so the save index scans in the background behind the logos.</summary>
        SaveSlots _saveSlots;
        /// <summary>fix-222: the menu options, loaded and applied (Video / Performance) at start-up behind the first logo.</summary>
        PlayerOptions _options;
        /// <summary>fix-222: frame budget for build steps while a logo plays (one step at least per frame).</summary>
        const double BuildFrameBudgetMs = 6.0;
        /// <summary>The tutorial tips shown in this game (saved with it).</summary>
        readonly TutorialTips _tips = new TutorialTips();

        public GameSession Session { get; private set; }
        /// <summary>Suspends the hero's controls while a screen (or the dev console) needs the keyboard and mouse.</summary>
        public HeroControlLock Controls { get; private set; }
        /// <summary>The loaded content packs (null until the content load finished).</summary>
        public GameContent Content => _content;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (FindAnyObjectByType<GameBootstrap>() != null) return;
            var player = FindAnyObjectByType<G3Player>();
            if (player == null) return;
            var go = new GameObject("Myrtana Game");
            DontDestroyOnLoad(go);
            go.AddComponent<GameBootstrap>().AwaitContent(player);
        }

        CursorDirector _cursor;

        void AwaitContent(G3Player player)
        {
            _log = new UnityGameLog();
            _player = player;
            _loadingHold = gameObject.AddComponent<LoadingHold>(); // no game while a loading screen is up
            _loadingHold.Init(FindAnyObjectByType<G3WorldStreamer>(), () => Session);
            _contentLoad = ContentPreload.Take();
            if (_contentLoad == null)
            {
                _log.Warning($"No content pack in {ContentLocations.ContentRoot}. Import it with the menu Myrtana/Content/Import Gothic 3 pack.");
                enabled = false;
                return;
            }
            _cursor = gameObject.AddComponent<CursorDirector>(); // the one cursor rule (fix-87); hidden while content loads
            _cursor.Init(this, player);
            // fix-222: what the title needs, started now behind the logos instead of after them: the save index (Continue,
            // the Save/Load lists) on a worker thread, the item and object visual catalogues (739 assets) loading
            // asynchronously, and the Video / Performance options (the URP asset change re-creates the GPU Resident Drawer:
            // now behind the black of the first logo, not under the title)
            var early = System.Diagnostics.Stopwatch.StartNew();
            _saveSlots = new SaveSlots();
            _saveSlots.StartIndexing();
            StartupAssets.Begin();
            _options = OptionsPreferences.Load();
            new DisplayOptionsApplier(_options, transform); // Video and Performance pages (URP, render switches)
            StartupTimeline.Mark($"background loads started (save index, visual catalogues), display options applied ({early.Elapsed.TotalMilliseconds:0} ms)");
            if (!DevStart.QuickStart && G3Skin.Load() != null)
            {
                // Logos and title first, no world behind them (G3); the streamer has not started yet.
                _title = TitleStage.Begin(gameObject, FindAnyObjectByType<G3WorldStreamer>());
                _title.Attach(_loadingHold);
                _video = G3VideoScreen.Create(transform);
                _video.Preload(FirstLogo());
                // fix-115: the logos play from the end of the Unity splash while the content loads; the game is built
                // after them (its main-thread work would stall a playing logo), the title waits for both.
                _startFlow = GameStartFlow.BeginLogos(gameObject, _video, _title);
            }
            else G3StartupLoader.WaitFor(() => this == null || Session != null || !enabled);   // null: destroyed
        }

        /// <summary>The first start-up logo that has a converted video (null: none).</summary>
        static string FirstLogo()
        {
            foreach (var logo in GameFlow.DefaultLogos)
                if (G3VideoScreen.Exists(logo)) return logo;
            return null;
        }

        /// <summary>
        /// Builds the game once the content load has finished; false until it is built. fix-222 [owner: the title came a
        /// couple of seconds after the last logo]: the build no longer waits for the end of the logos; it runs in steps
        /// (<see cref="BuildSteps"/>), as many per frame as fit <see cref="BuildFrameBudgetMs"/> (one at least) while a logo
        /// plays, all at once when none does (black cover; the quick start), so the title follows the last logo at once.
        /// </summary>
        bool ContentReady()
        {
            if (_built || _contentLoad == null) return true;
            if (_build == null)
            {
                if (!_contentLoad.IsCompleted) return false;
                if (!_contentLogged)
                {
                    _contentLogged = true;
                    StartupTimeline.Mark($"content loaded (parse {ContentPreload.ParseSeconds:0.00} s on a worker thread)" +
                                         (_startFlow != null && !_startFlow.LogosOver ? "; the game is built in steps during the logos" : ""));
                }
                var load = _contentLoad;
                if (load.IsFaulted)
                {
                    _contentLoad = null;
                    Debug.LogException(load.Exception.GetBaseException());
                    _title?.End();
                    if (_startFlow != null) _startFlow.Abort();
                    if (_video != null) _video.Stop();
                    enabled = false;
                    return false;
                }
                StartupTimeline.Mark("building the game");
                _build = BuildSteps(_player, load.Result);
            }
            var frame = System.Diagnostics.Stopwatch.StartNew();
            _buildFrames++;
            while (true)
            {
                double before = frame.Elapsed.TotalMilliseconds;
                bool more = _build.MoveNext();
                double ms = frame.Elapsed.TotalMilliseconds - before;
                _buildMs += ms;
                if (ms > _buildLongestMs) _buildLongestMs = ms;
                if (more) _buildSteps.Append(_buildSteps.Length > 0 ? ", " : "").Append(_build.Current).Append(' ').Append(ms.ToString("0"));
                if (!more)
                {
                    _built = true;
                    _build = null;
                    _contentLoad = null;
                    Debug.Log($"[Myrtana] start-up: game built in {_buildMs:0} ms over {_buildFrames} frames (longest step {_buildLongestMs:0} ms): {_buildSteps} ms");
                    return true;
                }
                // spread over frames only while a logo plays; with none (black cover, quick start) finish now
                bool spread = _startFlow != null && !_startFlow.LogosOver;
                if (spread && frame.Elapsed.TotalMilliseconds >= BuildFrameBudgetMs) return false;
            }
        }

        /// <summary>
        /// The game's presentation, in steps (each yield names the step just done; fix-222 runs them over the logo frames).
        /// The order is the one of the former single-frame build; the program flow starts in the last step.
        /// </summary>
        IEnumerator<string> BuildSteps(G3Player player, GameContent content)
        {
            _content = content;
            foreach (var problem in _content.Problems) _log.Warning("content: " + problem);
            _ruleChoices = NewGameOptionsPreferences.Load(); // CP rule choices of the menus (Docs/game-start.md)
            _settings = new GameSettings(LanguagePreferences.Load(), PinnedSeed(), _ruleChoices.ToRules());

            yield return "settings";
            var hud = HudDocument.Create(transform);
            var root = hud.rootVisualElement;
            var g3Hud = G3Hud.TryCreate(transform); // the original HUD page; null without the GUI import
            yield return "hud";
            var messages = new HudMessages(g3Hud != null ? g3Hud.Messages : new PlainHudMessages(root));
            var voice = new GameObject("Voice").AddComponent<VoicePlayer>();
            voice.transform.SetParent(transform, false);
            var npcSpawner = new GameObject("NPCs").AddComponent<NpcViewManager>();
            npcSpawner.transform.SetParent(transform, false);
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            npcSpawner.Init(player.transform, streamer);
            npcSpawner.gameObject.AddComponent<CellNavMeshes>().Init(streamer, player.transform);
            var npcViews = new NpcViewDirectory { Spawned = npcSpawner };
            yield return "npcs";
            var environmentClock = gameObject.AddComponent<EnvironmentClock>();
            environmentClock.Init(FindAnyObjectByType<G3Environment>());

            var dialogView = gameObject.AddComponent<DialogView>();
            dialogView.Init(voice, new StreamingVoiceCatalog(ContentLocations.VoiceRoot), root, _settings.Languages);
            var controls = new HeroControlLock(player);
            Controls = controls;
            var staging = gameObject.AddComponent<ConversationStaging>();
            staging.Init(controls, npcViews);
            var presentation = gameObject.AddComponent<DialogPresentation>();
            presentation.Init(player, npcViews, dialogView, voice);
            yield return "dialog";
            var saves = gameObject.AddComponent<SaveGameController>();
            saves.Init(this, _saveSlots ?? new SaveSlots(), messages);
            gameObject.AddComponent<LanguageHotkeys>().Bind(_settings.Languages);
            gameObject.AddComponent<RemasterHotkeys>().Bind(_settings.Languages, this);   // F7 world remaster A/B (Docs/build.md)
            gameObject.AddComponent<FpsOverlay>();   // fix-172: option "Show FPS" / console fps, Ctrl+F
            global::G3.Unity.G3CharacterRemaster.Register(player.gameObject);              // Options → Remaster → Characters
            var heroModel = player.GetComponentInChildren<global::G3.Unity.Characters.G3HeroAnimator>(true);
            if (heroModel != null) G3ReliefFooting.Attach(heroModel.gameObject, hero: true);   // on the drawn relief (remaster-world 14)
            _views.AddRange(new ISessionView[] { npcSpawner, environmentClock, dialogView, staging, presentation, saves });
            yield return "saves";
            var sheets = AddItemsAndScreens(player, controls, npcViews, root, messages, g3Hud == null);
            _sheets = sheets;
            yield return "pages";
            AddCombat(player, controls, npcSpawner, root);
            yield return "combat";
            AddPerception(player, npcSpawner, messages);
            AddTeleports(player, streamer);
            AddEffects(player);
            if (g3Hud != null) AddG3Hud(g3Hud, player, npcViews, sheets);
            yield return "world";
            AddMenu(controls, saves, voice);
            yield return "menu";

            _playerPose = new PlayerPoseSaveSection(player);
            var airborne = SaveBlocker.When(() => player.IsActive && player.AirTime > AirborneSeconds, "falling");
            var snowTracks = new SnowTracksSaveSection();   // fix-178: persistent snow tracks (saved; applied / cleared on bind)
            var trackSampler = gameObject.AddComponent<SnowTrackSampler>();
            trackSampler.Init(snowTracks);
            _views.Add(trackSampler);
            yield return "tracks";
            _ports = new SessionPorts(dialogView, _log, new ISaveParticipant[] { _playerPose, _torch.SaveSection, new WeatherSaveSection(), _tips, snowTracks }, new[] { airborne });

            // Title, new game at the G3 start, intro, or the developer quick start (Docs/game-start.md).
            var flow = _startFlow != null ? _startFlow : gameObject.AddComponent<GameStartFlow>();
            flow.Init(this, player, GetComponent<GameMenu>(), controls, messages, _ruleChoices, _video);
            flow.Title = _title;
            saves.InPlay = () => flow.MenuAllowed && !flow.InVideo; // fix-155: recovery snapshots only in play
            AddTutorial(player, controls, flow);
            _views.Add(flow);
            var cutscene = gameObject.AddComponent<StoryCutsceneView>(); // epilogue slideshow, story music, effects, EndGame (story-2)
            cutscene.Init(flow, npcSpawner, player.transform);
            _views.Add(cutscene);
            _cursor.Wire(flow, GetComponent<GameMenu>(), GetComponent<GameScreens>(), dialogView, cutscene,
                         GetComponentInChildren<G3TutorialView>(true));
            _loadingHold.Attach(controls, flow);
            yield return "flow";
            GetComponent<GameMenu>()?.Prewarm(); // the title's first frame: Continue's list, the blurred side fill
            yield return "menu warm-up";
            flow.Run();
            _log.Info($"Game started: {string.Join(", ", _content.Database.Packs.Select(p => p.Id))}, " +
                      $"text {_settings.Languages.TextLanguage}, voice {_settings.Languages.VoiceLanguage}.");
        }

        /// <summary>
        /// World items, pickup and talk focus, the armour on the hero, the pages (C character, L quest log, I/Tab
        /// inventory, M map stub, trade after a trade topic), the quick-slot keys and the HUD notices. Returns the sidebar's
        /// pages in G3's order (inventory, character, spells, map, quests; null = none).
        /// </summary>
        GameScreen[] AddItemsAndScreens(G3Player player, HeroControlLock controls, NpcViewDirectory npcViews, VisualElement root,
                                        HudMessages messages, bool plainPrompt)
        {
            var catalog = ItemVisualCatalog.Load();
            if (catalog == null) _log.Warning("No item visuals yet (menu Myrtana/Content/Build item visuals): items are invisible, armour does not show.");
            var worldItems = new GameObject("World items").AddComponent<WorldItemViews>();
            worldItems.transform.SetParent(transform, false);
            worldItems.Init(player.transform, catalog);
            var interaction = gameObject.AddComponent<PlayerInteraction>();
            interaction.Init(npcViews, worldItems, controls, messages, plainPrompt ? root.Q<Label>("interact-prompt") : null);
            var screens = gameObject.AddComponent<GameScreens>();
            screens.Init(controls);
            var pages = G3Pages.TryCreateRoot(transform); // the original G3 look; null without the GUI import
            GameScreen inventory = pages != null ? new G3InventoryScreen(pages, messages) : new InventoryScreen(root, messages);
            TradePage trade = pages != null ? new G3TradeScreen(pages, messages, screens) : new TradeScreen(root, messages);
            GameScreen character = pages != null ? new G3CharacterScreen(pages) : new CharacterScreen(root);
            GameScreen quests = pages != null ? new G3QuestLogScreen(pages) : new QuestLogScreen(root);
            GameScreen map = pages != null ? new G3MapScreen(pages, player.transform) : null;
            _spellBook = pages != null ? new G3SpellBookScreen(pages, screens) : null; // no plain-look page: the tab stays empty
            // G3 page keys through the key map (session keys JournalModeCharScreen / MagBook / Log / Map, InventoryMode;
            // GameScreens.PageKey). Tab is no longer an inventory key (not in G3's table; par-controls).
            screens.Add(Key.C, character);
            if (_spellBook != null) screens.Add(G3.Player.SessionKey.JournalModeMagBook, _spellBook); // G3 "Magie Buch", default B (DIK 0x30)
            screens.Add(Key.L, quests);
            if (map != null) screens.Add(Key.M, map);
            screens.Add(Key.I, inventory);
            _views.Add(_crafting);
            AddInteractiveObjects(player, controls, messages, screens, pages, interaction);
            var quickSlots = gameObject.AddComponent<QuickSlotInput>();
            quickSlots.Init(controls, messages);
            _views.AddRange(new ISessionView[]
            {
                worldItems, interaction, screens, new TradeOpener(screens, trade), new HeroArmorView(player.transform, catalog),
                new HeroHelmetView(player.transform, catalog),
                new NoticeFeed(messages, () => trade.IsOpen), quickSlots,
            });
            return new[] { inventory, character, _spellBook, map, quests };
        }

        /// <summary>
        /// Chests, doors, beds and stations: their views, the hero's use of them, the loot page and the station pages
        /// (craft, sharpen/fry; bookstands are read aloud) (Docs/interactive-objects.md).
        /// </summary>
        void AddInteractiveObjects(G3Player player, HeroControlLock controls, HudMessages messages, GameScreens screens,
                                   VisualElement pages, PlayerInteraction interaction)
        {
            var catalog = InteractiveVisualCatalog.Load();
            if (catalog == null) _log.Warning("No interactive visuals yet (menu Myrtana/Content/Build interactive visuals): chests and doors are invisible.");
            var objects = new GameObject("Interactive objects").AddComponent<InteractiveObjectViews>();
            objects.transform.SetParent(transform, false);
            objects.Init(player.transform, catalog);
            var heroVoice = new GameObject("Hero voice").AddComponent<VoicePlayer>();
            heroVoice.transform.SetParent(transform, false);
            var loot = pages != null ? new G3LootScreen(pages, screens) : null;
            var use = gameObject.AddComponent<HeroObjectUse>();
            use.Init(controls, objects, screens, loot, messages, heroVoice, _settings.Languages);
            var lines = new HeroLines(heroVoice, _settings.Languages, messages);
            _heroLines = lines;
            use.InitStations(_crafting, pages != null ? new G3SynthScreen(pages, screens, _crafting, messages, lines) : null,
                             pages != null ? new G3TransformScreen(pages, screens, _crafting, messages) : null, lines);
            interaction.AddObjects(objects, use);
            // G3 PS_HUD / PS_Interact use CamModeInventory; NPC bodies block the camera cast (par-misc)
            var camera = player.playerCamera != null ? player.playerCamera.GetComponent<G3PlayerCamera>() : null;
            if (camera != null) camera.hudOrInteraction = () => screens.Current != null || use.Busy;
            G3PlayerCamera.IsCharacter = c => c.GetComponentInParent<CombatBody>() != null;
            // G3 characters collide (gCCharacterMovement_PS): standing bodies stop the hero's walk (fix-86)
            G3Player.BodyFootprint = c => c.TryGetComponent<CombatBody>(out var b) ? b.WalkBlock : 0f;
            // the ceiling-top guard waits for the cell NavMesh around the hero (fix-219)
            G3Player.NavMeshPending = p => CellNavMeshes.IsBuildingNear(p, 20f);
            var consumption = gameObject.AddComponent<HeroConsumption>(); // eat/drink motions, regen page gate (par-interact)
            consumption.Init(controls, screens);
            _views.AddRange(new ISessionView[] { objects, use, consumption });
        }

        /// <summary>Teleport stones move the hero (Docs/map-teleport.md).</summary>
        /// <summary>G3 effects and weather (Docs/formats-effects.md): weather director, world effects, level-up/teleport effects.</summary>
        HeroTorchView _torch;

        void AddEffects(G3Player player)
        {
            var weather = G3WeatherDirector.Ensure();
            G3WorldEffects.Ensure();
            var effects = gameObject.AddComponent<EffectsView>();
            effects.Init(player.transform, weather);
            _views.Add(effects);
            // The hero's torch (effects-2): take/put away, the flame and light in the left hand, saved.
            _torch = gameObject.AddComponent<HeroTorchView>();
            _torch.Init(player, ItemVisualCatalog.Load());
            _views.Add(_torch);
        }

        void AddTeleports(G3Player player, G3WorldStreamer streamer)
        {
            var teleports = gameObject.AddComponent<TeleportExecutor>();
            teleports.Init(player, streamer);
            _views.Add(teleports);
        }

        /// <summary>The original HUD page (compass, quick slots, sidebar, messages, focus name) over the vitals bars.</summary>
        void AddG3Hud(G3Hud hud, G3Player player, NpcViewDirectory npcViews, GameScreen[] sheets)
        {
            var focus = new HudFocusTracker(GetComponent<PlayerInteraction>(), player.GetComponent<PlayerCombat>(), npcViews,
                                            GetComponentInChildren<WorldItemViews>());
            focus.Objects = GetComponentInChildren<InteractiveObjectViews>();
            hud.Init(GetComponent<GameScreens>(), GetComponent<VitalsHud>(), focus, sheets);
            hud.Aim = player.GetComponent<PlayerCombat>(); // the aiming crosshair
            _views.Add(hud);
        }

        /// <summary>
        /// The menu options (volumes, camera; applied at once) and the Esc game menu with save/load lists, which needs
        /// the G3 GUI import.
        /// </summary>
        /// <summary>Melee combat views (Docs/combat.md §9): hits, NPC fighters, the player's input, HUD bars, game over.</summary>
        void AddCombat(G3Player player, HeroControlLock controls, NpcViewManager npcs, VisualElement root)
        {
            var catalog = ItemVisualCatalog.Load();
            var presenter = gameObject.AddComponent<CombatPresenter>();
            presenter.Init(npcs, catalog);
            var gameOver = new GameOverView(root);
            var playerCombat = player.gameObject.AddComponent<PlayerCombat>();
            playerCombat.Init(presenter, controls, catalog, gameOver);
            if (TryGetComponent<QuickSlotInput>(out var quickSlots)) quickSlots.Combat = playerCombat; // slots ready weapons and spells
            if (_spellBook != null) _spellBook.Combat = playerCombat; // the spell page readies spells
            // PS_Magic pre-check: a focus spell without a target or a spell whose condition fails → the hero's "doesn't work"
            playerCombat.CastRefused += refusal =>
            {
                if (refusal == Myrtana.Rules.CastRefusal.NoFocus || refusal == Myrtana.Rules.CastRefusal.DoesntWork) _heroLines?.SayAny(Session, Myrtana.Rules.HeroSpokenLines.DoesntWork);
            };
            if (TryGetComponent<PlayerInteraction>(out var interaction))
            {
                interaction.WeaponDrawn = () => playerCombat.StanceActive; // PS_Normal or a drawn stance (HeroUseInput)
                playerCombat.TakeAllChord = interaction.StanceChord;        // fix-226: Ctrl + click loots a body with a weapon drawn
            }
            var hud = gameObject.AddComponent<VitalsHud>();
            hud.Init(root, playerCombat);
            var arena = gameObject.AddComponent<ArenaFights>();
            arena.Init(presenter);
            var sounds = gameObject.AddComponent<CombatSounds>();
            sounds.Init(presenter, player, new StreamingVoiceCatalog(ContentLocations.VoiceRoot), _settings.Languages);
            _views.AddRange(new ISessionView[] { presenter, gameOver, playerCombat, hud, arena, sounds });
        }

        /// <summary>NPC perception and crime (Docs/perception-crime.md): the active scans, line of sight and the NPCs' calls.</summary>
        void AddPerception(G3Player player, NpcViewManager npcs, HudMessages messages)
        {
            var driver = gameObject.AddComponent<PerceptionDriver>();
            driver.Init(npcs, player, player.GetComponent<PlayerCombat>());
            var party = gameObject.AddComponent<PartyDriver>(); // companions and guides look out for enemies (Docs/party.md)
            party.Init(npcs);
            _views.Add(party);
            var reactions = gameObject.AddComponent<NpcReactionView>();
            reactions.Init(npcs, player.transform, messages, new StreamingVoiceCatalog(ContentLocations.VoiceRoot), _settings.Languages);
            _views.AddRange(new ISessionView[] { driver, reactions });
        }

        void AddMenu(HeroControlLock controls, SaveGameController saves, VoicePlayer voice)
        {
            var options = _options ?? OptionsPreferences.Load();
            new PlayerOptionsApplier(options, voice.GetComponent<AudioSource>());
            var dialogView = GetComponent<DialogView>();
            if (dialogView != null) dialogView.SubtitlesOn = () => options.Subtitles;
            var reactionView = GetComponent<NpcReactionView>();
            if (reactionView != null) reactionView.SubtitlesOn = () => options.Subtitles;
            if (_options == null) new DisplayOptionsApplier(options, transform); // Video and Performance pages (URP, render switches)
            _views.Add(new PortOptionsApplier(options.Port)); // the port's rows: FOV, upscaling, music, companions
            var controlOptions = OptionsPreferences.LoadControls();
            G3.Unity.G3Input.Options = controlOptions; // the session keys and mouse options every input reads
            var view = G3MenuView.TryCreate(transform, options, controlOptions, _settings.Languages, LanguageName);
            if (view == null)
            {
                _log.Warning("No game menu: the G3 GUI is not imported (menu G3/GUI/Import).");
                return;
            }
            view.Fallback = new Localizer(_settings.Languages, _content.Strings); // the title has no session
            view.AttachRuleChoices(_ruleChoices);
            var menu = gameObject.AddComponent<GameMenu>();
            menu.Init(saves, GetComponent<GameScreens>(), controls, view);
            _views.Add(menu);
        }

        /// <summary>A language's own name from its string table ("Русский", "English").</summary>
        string LanguageName(string language) =>
            _content.Strings.Load(language) is StringTable table && table.TryGetText("Language", out var name) ? name : language;

        /// <summary>
        /// The tutorial box (Docs/ui-skin.md "Tutorial"): the start tip after the intro, the first opening of each page,
        /// the first drawn weapon, bow and spell, the companions' talks and Ardea's liberation.
        /// </summary>
        void AddTutorial(G3Player player, HeroControlLock controls, GameStartFlow flow)
        {
            var view = G3TutorialView.TryCreate(transform);
            if (view == null) return;
            var menu = GetComponent<GameMenu>();
            var combat = player.GetComponent<PlayerCombat>();
            view.Init(_tips, controls,
                      blocked: () => Session == null || Session.Dialog.IsActive || LoadingHold.IsHeld || (menu != null && menu.State.IsOpen) ||
                                     flow.Flow == null || flow.Flow.Phase != GamePhase.Playing ||
                                     // r31 D1: not with the hero still landing (the pause would hold him in the air); 3 s at most
                                     (!player.IsGrounded && !player.IsFlying && player.AirTime < 3f),
                      stance: () => DrawnStance(combat));
            _views.Add(view);
            flow.RequestTip = tip => _tips.Request(tip);
            var screens = GetComponent<GameScreens>();
            var pages = _sheets;
            screens.Opened += page =>
            {
                if (page is TradePage) _tips.Request(TutorialTip.Trade);
                else if (page != null && pages != null)
                {
                    int i = System.Array.IndexOf(pages, page); // inventory, character, spells, map, quests
                    if (i >= 0) _tips.Request(SheetTips[i]);
                }
            };
        }

        static readonly TutorialTip[] SheetTips = { TutorialTip.Inventory, TutorialTip.Charscreen, TutorialTip.Spellbook, TutorialTip.Maps, TutorialTip.MissionLog };
        GameScreen[] _sheets;
        G3SpellBookScreen _spellBook;
        HeroLines _heroLines;

        /// <summary>PS_Melee, PS_Ranged or PS_Magic: the hero's drawn stance, or null.</summary>
        static TutorialTip? DrawnStance(PlayerCombat combat)
        {
            var animator = combat != null && combat.Fighter != null ? combat.Fighter.Animator : null;
            if (animator == null || !animator.Drawn) return null;
            return animator.IsCastStance ? TutorialTip.Spell : animator.IsRanged ? TutorialTip.Bow : TutorialTip.Fight;
        }

        /// <summary>Replaces the running game by the one stored in <paramref name="save"/>.</summary>
        public void LoadGame(byte[] save)
        {
            Session?.Dialog.Discard();
            _tips.Reset(); // an older save without the section starts with no tip seen
            _settings = WithRuleChoices();
            Begin(GameSession.Load(_content, _ports, _settings, new MemoryStream(save)));
            _playerPose.ApplyTo(_player);
        }

        /// <summary>Replaces the running game (if any) by a new one (Docs/game-start.md); the caller places the hero.</summary>
        public void NewGame()
        {
            Session?.Dialog.Discard();
            _tips.Reset();
            _settings = WithRuleChoices();
            Begin(GameSession.NewGame(_content, _ports, _settings));
        }

        /// <summary>The settings with the menus' current rule choices (a session keeps the rules it was made with).</summary>
        GameSettings WithRuleChoices() =>
            _ruleChoices == null ? _settings : new GameSettings(_settings.Languages, _settings.RandomSeed, _ruleChoices.ToRules(_settings.Rules));

        /// <summary>
        /// fix-119: the game's sessions roll with a fresh seed each (a new game and every load, like G3's unsaved rand());
        /// "-seed N" on the command line or the environment variable G3_SEED pins it (reproducible dev runs; scenario runs pin
        /// their own, <c>ScenarioSpec.Seed</c>).
        /// </summary>
        static ulong? PinnedSeed()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-seed");
            if (i >= 0 && i + 1 < args.Length) return SessionSeed.Parse(args[i + 1]);
            return SessionSeed.Parse(System.Environment.GetEnvironmentVariable("G3_SEED"));
        }

        void Begin(GameSession session)
        {
            Session = session;
            Debug.Log($"[Myrtana] session random seed {session.RandomSeed}{(session.RandomSeedPinned ? " (pinned)" : " (fresh)")}");
            ResetHero();
            foreach (var view in _views) view.Bind(session);
            Debug.Log($"[Myrtana] ground scatter keeps {ScatterKeepOutWiring.Apply(session)} NPC places clear");
            // the safety net after the views' own resets: nothing of the old game keeps the hero's controls
            string stale = Controls != null ? Controls.ReleaseSessionHolders() : string.Empty;
            if (stale.Length > 0) Debug.LogWarning($"[Myrtana] new session: control locks of the old game released ({stale})");
        }

        /// <summary>
        /// fix-105 (owner: a clean hero state on load and on a new game; G3 rebuilds the player from the save): the body's
        /// input and gait flags, the camera's orbit and smoothing, the motion overlays and their masks go back to rest before
        /// the views bind the new session. The views reset their own parts in Bind (PlayerCombat: the weapon-switch queue,
        /// melee input, aim and magic, fighter state; HeroObjectUse: the use or approach; HeroTorchView: the torch from the
        /// save; ConversationStaging, HeroConsumption …); then <see cref="HeroControlLock.ReleaseSessionHolders"/> catches
        /// any control lock of the old game still held. The hands follow the saved equipment: weapons sheathed, the torch as
        /// saved.
        /// </summary>
        void ResetHero()
        {
            if (_player == null) return;
            _player.ResetTransientState();
            foreach (var camera in _player.GetComponentsInChildren<G3PlayerCamera>(true)) camera.ResetTransient();
            foreach (var motions in _player.GetComponentsInChildren<G3.Unity.Characters.G3MotionPlayer>(true))
            {
                motions.StopOverlay(0f);
                motions.RigChanged();
            }
        }

        void Update()
        {
            GamePause.Guard();
            // Paused (menu, tutorial box, intro): the session clock, AI and timers stand still (G3 gCSession::Pause, r31 D1)
            if (ContentReady() && !_loadingHold.Refresh() && !GamePause.IsPaused) Session?.Tick(Time.deltaTime);
            // fix-201: a control lock whose action (motion, coroutine) is gone is ended and released (paused: dt 0, nothing expires)
            Controls?.Sweep(GamePause.IsPaused ? 0f : Time.deltaTime);
        }
    }
}
