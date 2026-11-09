using System;
using System.Globalization;

namespace Myrtana.Session
{
    /// <summary>
    /// The port's upscaling choices (URP render scale with FidelityFX Super Resolution 1 or Spatial-Temporal
    /// Post-processing). Off = native resolution, as the original.
    /// </summary>
    public enum UpscalingMode { Off, FsrQuality, FsrBalanced, FsrPerformance, StpQuality, StpBalanced, StpPerformance }

    /// <summary>
    /// The port's own options that the original has no row for (player feedback, Docs/research/player-feedback.md items
    /// 5, 12 and 15). Every default is the original's behaviour (CP 1.75), so an untouched options file plays like G3:
    /// <list type="bullet">
    /// <item><see cref="FovOffset"/>: degrees added to G3's camera field of view (0 = G3's mode FOVs).</item>
    /// <item><see cref="Upscaling"/>: render below the screen size and upscale (Off = native).</item>
    /// <item><see cref="CombatMusicThreat"/>: fight music only for a real threat, not for every scavenger (off = G3's
    /// HandleMusicSituation: any attacker within 30 m).</item>
    /// <item><see cref="SmoothMusic"/>: longer crossfades where G3 cuts with its 2 s crossfade (fight starts, location
    /// changes without a g3.mul transition).</item>
    /// <item><see cref="CompanionsFollowTeleport"/>: following companions come along on a teleport (off = G3
    /// MagicTeleport: they wait where they were).</item>
    /// <item><see cref="CompanionMarkers"/>: the companions' positions on the map pages (G3 shows only the hero).</item>
    /// <item><see cref="TurnKeysOrbit"/>: the Camera rotation keys (G3 Q / E) orbit the camera round the hero out of combat
    /// (off = G3: they turn the hero; fix-73).</item>
    /// <item><see cref="SkipDecorativeFocus"/>: the focus skips the purely decorative objects (seats, water pipes, logs,
    /// orc drums; <c>ObjectFocusRules</c>; off = G3: every interactive object is focused; fix-79).</item>
    /// <item><see cref="CookOnAnyFire"/>: lit fireplaces, fire cages and fire basins work like a campfire (fry page;
    /// <c>Myrtana.Rules.CookFires</c>; off = G3: only its campfires and cauldrons cook). Default on, [owner] (fix-88).</item>
    /// <item><see cref="ShieldBlocksCreatures"/>: a raised shield blocks animals and fist fighters as the Russian Update
    /// Pack 1.04 does (<c>Myrtana.Rules.ShieldCreatureParade</c>; off = CP 1.75: no parade stops a creature). Default off,
    /// [owner] (fix-109).</item>
    /// <item><see cref="StillWindowCamera"/>: pages (loot, inventory, trade, character, map, documents) and object uses keep
    /// the play camera as it is (off = G3: CamModeInventory, 1.5 m, FOV 45). Default on, [owner] (fix-122).</item>
    /// <item><see cref="ContinueButton"/>: the main menu offers "Continue" (the newest save, <see cref="ContinueGame"/>) at the
    /// title and after the hero's death (off = G3: no such button). Default on, [owner] (fix-138).</item>
    /// <item><see cref="LateTrophies"/>: a corpse rolled before the hero learnt a trophy perk gives that perk's trophies when
    /// looted later (<c>Myrtana.Rules.NpcLoot.LateTrophies</c>; off = G3: the trophies are decided once, at death). Default
    /// on, [owner] (fix-143).</item>
    /// <item><see cref="CrashRecovery"/>: a hidden recovery save every 2 min of play and after key moments, offered at the title
    /// after a crash (<c>Myrtana.Session.CrashRecovery</c>; off = G3: progress since the last save is lost). Default on,
    /// [owner] (fix-155).</item>
    /// <item><see cref="NpcShoutSubtitles"/>: NPCs' shared lines (warnings, cheers, comments, smalltalk) show their own voice
    /// set's text in the HUD log (off = G3: AI SVM lines are voice only, StartSaySVM(…, false)). Default on, [owner] (fix-156).</item>
    /// <item><see cref="ForcedReleaseCounts"/>: killing the keeper of a prisoner the hero is to free (Potros for Gorn, Zapotek for
    /// Lars) wins the release quest with its rewards (<c>Myrtana.Story.ForcedReleases</c>; off = G3: the Report quest fails).
    /// Default on, [owner] (fix-161).</item>
    /// <item><see cref="ForgiveAccidentalHits"/>: the hero's stray hits on an NPC that fights his enemy (up to 3 within 8 s), and
    /// on his party, start no fight and call nobody in (<c>Myrtana.Simulation.AccidentalHits</c>; off = G3 AssessAgressor and
    /// the fight sound). Default on, [owner] (fix-162).</item>
    /// <item><see cref="ShowFps"/>: a small FPS / frame-time overlay in the top-right corner (Ctrl+F hides it; the console's
    /// <c>fps</c>; off = G3: none). Default off, [owner] (fix-172).</item>
    /// <item><see cref="KeepWoundsAfterLoad"/>: NPCs wounded near the hero keep their saved hit points after a load
    /// (<c>NpcLifeServices.KeepWoundsAfterLoad</c>; off = G3: every NPC entering the processing range, which after a load is
    /// every NPC near the hero, is refilled by RefreshHitPoints). Default on, [owner] (fix-175).</item>
    /// <item><see cref="TorchGlow"/>: the big soft halo sprite around a carried torch's flame (<c>G3.Unity.G3EffectPlayer.TorchGlow</c>;
    /// on = G3: VFX_Env_Fire_Torch_Glow_01 of eff_item_torch_02). Default off, [owner] (fix-182).</item>
    /// <item><see cref="QuickLootChord"/>: "Aktion 1" with the quick-loot modifier (Ctrl) or the right button held takes
    /// everything from the focused body, sleeper or chest without the loot page (<c>Myrtana.Session.QuickLoot</c>; off = G3:
    /// Use1 always opens the loot page). Default on, [owner] (fix-205).</item>
    /// <item><see cref="ShowHelmet"/>: the hero's worn helmet is drawn on his head (<c>Myrtana.Unity.HeroHelmetView</c>; off: not
    /// drawn, his hair shows, the item stays equipped with its protection). Default on = G3, [owner] (fix-232).</item>
    /// <item><see cref="RangedFinishesDowned"/>: an arrow, bolt or damaging spell missile of the hero or his party on a knocked-out
    /// NPC that may be killed finishes it like the melee finishing move (<c>CombatResolver.FinishesDowned</c>; the blunt arrow never
    /// finishes a humanoid; off = G3: a downed victim takes no damage). Default on, [owner] (fix-241).</item>
    /// </list>
    /// Kept outside save games (<c>OptionsPreferences</c>).
    /// </summary>
    public sealed class PortOptions
    {
        public const int FovOffsetMin = -10, FovOffsetMax = 30, FovOffsetStep = 5;

        int _fovOffset;
        UpscalingMode _upscaling;
        bool _combatMusicThreat, _smoothMusic, _companionsFollowTeleport, _companionMarkers, _turnKeysOrbit, _skipDecorativeFocus;
        bool _cookOnAnyFire = true;  // [owner] default: cook at any lit fire (fix-88)
        bool _shieldBlocksCreatures;     // [owner] default No = CP 1.75 (fix-109)
        bool _stillWindowCamera = true;  // [owner] default: no zoom in windows (fix-122)
        bool _continueButton = true;     // [owner] default: Continue on the main menu (fix-138)
        bool _lateTrophies = true;       // [owner] default: trophies after learning the perk (fix-143)
        bool _crashRecovery = true;      // [owner] default: recovery snapshots + the title's question (fix-155)
        bool _npcShoutSubtitles = true;  // [owner] default: NPC shouts show their own text (fix-156)
        bool _forcedReleaseCounts = true; // [owner] default: freeing a prisoner by killing his keeper wins the quest (fix-161)
        bool _forgiveAccidentalHits = true; // [owner] default: stray hits on allies start no fight (fix-162)
        bool _showFps;                      // [owner] default off: no FPS overlay (fix-172)
        bool _keepWoundsAfterLoad = true;   // [owner] default: NPCs keep their wounds over a load (fix-175)
        bool _torchGlow;                    // [owner] default off: no halo around the torch flame (fix-182)
        bool _quickLootChord = true;        // [owner] default on: Ctrl / right button + click takes all (fix-205)
        bool _rangedFinishesDowned = true;  // [owner] default on: arrows and spell missiles finish downed foes (fix-241)
        bool _showHelmet = true;            // [owner] default on (G3): the worn helmet is drawn on the hero (fix-232)

        public event Action Changed;

        /// <summary>Degrees added to every camera mode's field of view (<see cref="FovOffsetMin"/>…<see cref="FovOffsetMax"/>).</summary>
        public int FovOffset
        {
            get => _fovOffset;
            set => Set(ref _fovOffset, Math.Max(FovOffsetMin, Math.Min(FovOffsetMax, value)));
        }

        public UpscalingMode Upscaling
        {
            get => _upscaling;
            set => Set(ref _upscaling, Enum.IsDefined(typeof(UpscalingMode), value) ? value : UpscalingMode.Off);
        }

        public bool CombatMusicThreat
        {
            get => _combatMusicThreat;
            set => Set(ref _combatMusicThreat, value);
        }

        public bool SmoothMusic
        {
            get => _smoothMusic;
            set => Set(ref _smoothMusic, value);
        }

        public bool CompanionsFollowTeleport
        {
            get => _companionsFollowTeleport;
            set => Set(ref _companionsFollowTeleport, value);
        }

        public bool CompanionMarkers
        {
            get => _companionMarkers;
            set => Set(ref _companionMarkers, value);
        }

        /// <summary>The Camera rotation keys (G3 Q / E) orbit the camera instead of turning the hero (G3PlayerCamera, KeyOrbitRules).</summary>
        public bool TurnKeysOrbit
        {
            get => _turnKeysOrbit;
            set => Set(ref _turnKeysOrbit, value);
        }

        /// <summary>
        /// The row "Focus decorative objects": false = Yes (original, every object), true = No (seats and the looping
        /// activities are not focused; <c>Myrtana.Rules.ObjectFocusRules</c>).
        /// </summary>
        public bool SkipDecorativeFocus
        {
            get => _skipDecorativeFocus;
            set => Set(ref _skipDecorativeFocus, value);
        }

        /// <summary>
        /// The row "Cook on any fire": true = Yes ([owner] default, fix-88: lit fireplaces, braziers and fire basins are
        /// cooking stations like the campfire), false = No (original).
        /// </summary>
        public bool CookOnAnyFire
        {
            get => _cookOnAnyFire;
            set => Set(ref _cookOnAnyFire, value);
        }

        /// <summary>
        /// The row "Shield protects from beasts": false = No (original CP 1.75), true = Yes (fix-109, [owner]: UP 1.04's
        /// shield block of animal and fist attacks, <c>Myrtana.Rules.ShieldCreatureParade</c>).
        /// </summary>
        public bool ShieldBlocksCreatures
        {
            get => _shieldBlocksCreatures;
            set => Set(ref _shieldBlocksCreatures, value);
        }

        /// <summary>
        /// The row "Camera in windows": true = "As in play (no zoom)" ([owner] default, fix-122: a page or an object use
        /// leaves the play camera's distance, FOV and pivot alone), false = Original (G3's CamModeInventory zoom).
        /// </summary>
        public bool StillWindowCamera
        {
            get => _stillWindowCamera;
            set => Set(ref _stillWindowCamera, value);
        }

        /// <summary>
        /// The row "Continue button": true = Yes ([owner] default, fix-138: the title's and the dead hero's main menu start
        /// with "Continue", loading the newest save), false = No (original menu).
        /// </summary>
        public bool ContinueButton
        {
            get => _continueButton;
            set => Set(ref _continueButton, value);
        }

        /// <summary>
        /// The row "Late trophies": true = Yes ([owner] default, fix-143: a corpse plundered before the hero learnt a trophy
        /// perk gives that perk's teeth, skin or fur when looted again, once per perk), false = No (original CP 1.75: the
        /// trophies are rolled once at death by the perks known then).
        /// </summary>
        public bool LateTrophies
        {
            get => _lateTrophies;
            set => Set(ref _lateTrophies, value);
        }

        /// <summary>
        /// The row "Crash recovery": true = Yes ([owner] default, fix-155: a hidden snapshot every 2 min of play and after key
        /// moments; after a crash the title offers it), false = No (original: what was not saved is lost).
        /// </summary>
        public bool CrashRecovery
        {
            get => _crashRecovery;
            set => Set(ref _crashRecovery, value);
        }

        /// <summary>
        /// The row "NPC shout subtitles": true = Yes ([owner] default, fix-156: an NPC's shared line shows the text of its own
        /// voice set, nothing when that set has none), false = No (original CP 1.75: AI SVM lines are heard only).
        /// </summary>
        public bool NpcShoutSubtitles
        {
            get => _npcShoutSubtitles;
            set => Set(ref _npcShoutSubtitles, value);
        }

        /// <summary>
        /// The row "Freeing by force counts": true = Yes ([owner] default, fix-161: the hero's side killing the keeper of a
        /// release quest, Gorn_FreeHim / Lars_FreeHim, settles it as Success with its XP and fame; a save where it failed so is
        /// re-judged), false = No (original CP 1.75: the Report quest fails, the prisoner is freed anyway).
        /// </summary>
        public bool ForcedReleaseCounts
        {
            get => _forcedReleaseCounts;
            set => Set(ref _forcedReleaseCounts, value);
        }

        /// <summary>
        /// The row "Forgive accidental hits": true = Yes ([owner] default, fix-162: the hero's stray blow, arrow or spell on an NPC
        /// fighting his enemy, or on his party, only draws a complaint; the 4th within 8 s counts), false = No (original CP 1.75).
        /// </summary>
        public bool ForgiveAccidentalHits
        {
            get => _forgiveAccidentalHits;
            set => Set(ref _forgiveAccidentalHits, value);
        }

        /// <summary>
        /// The row "Show FPS": true = Yes (fix-172, [owner]: FPS, frame ms avg / 1 % low / max of 5 s, CPU / GPU ms and the
        /// remaster parts in the top-right corner; Ctrl+F hides / shows it), false = No (default, original: no overlay).
        /// </summary>
        public bool ShowFps
        {
            get => _showFps;
            set => Set(ref _showFps, value);
        }

        /// <summary>
        /// The row "Keep wounds after load": true = Yes ([owner] default, fix-175: an NPC wounded in the processing range at the
        /// save keeps its HP and SP when the loaded game brings it into the range again), false = No (original CP 1.75:
        /// OnEnterProcessingRange refills it, as it does any NPC entering the range).
        /// </summary>
        public bool KeepWoundsAfterLoad
        {
            get => _keepWoundsAfterLoad;
            set => Set(ref _keepWoundsAfterLoad, value);
        }

        /// <summary>
        /// The row "Torch glow": false = No ([owner] default, fix-182: "а можно вообще убрать эту ауру вокруг факела?" - the
        /// flame, sparks, smoke and the torch's light on the surroundings stay, no glow sphere in the air), true = Yes (original
        /// CP 1.75: eff_item_torch_02 also spawns VFX_Env_Fire_Torch_Glow_01, 6-8 m BigGlow sprites).
        /// </summary>
        public bool TorchGlow
        {
            get => _torchGlow;
            set => Set(ref _torchGlow, value);
        }

        /// <summary>Which text a spoken shared line shows (<see cref="SharedLineText"/>).</summary>
        public enum LineText { None, OwnVoiceSet, OwnOrAnotherVoiceSet }

        /// <summary>
        /// The HUD text of a shared SVM line (fix-156): none while G3's Subtitles option is off; the hero's lines his own text,
        /// else another voice set's text of the line [interp, unchanged]; an NPC's line only with <see cref="NpcShoutSubtitles"/>
        /// and only its own voice set's text (never borrowed: another set's recording may say something else).
        /// </summary>
        public static LineText SharedLineText(bool speakerIsHero, bool subtitlesOn, bool npcShoutSubtitles)
        {
            if (!subtitlesOn) return LineText.None;
            if (speakerIsHero) return LineText.OwnOrAnotherVoiceSet;
            return npcShoutSubtitles ? LineText.OwnVoiceSet : LineText.None;
        }

        /// <summary>
        /// The row "Take all: Ctrl / RMB + click": true = Yes ([owner] default, fix-205, not G3: Use1 with the quick-loot
        /// modifier or the right button held takes everything from the focus), false = No (original: Use1 opens the loot page).
        /// </summary>
        public bool QuickLootChord
        {
            get => _quickLootChord;
            set => Set(ref _quickLootChord, value);
        }

        /// <summary>
        /// The row "Show helmet" (fix-232, [owner]): true = Yes (default, G3: the worn helmet on the hero's head), false = No (the
        /// helmet is not drawn, his hair shows normally; equipment, protection and the inventory are unchanged).
        /// </summary>
        public bool ShowHelmet
        {
            get => _showHelmet;
            set => Set(ref _showHelmet, value);
        }

        /// <summary>
        /// The row "Arrows finish downed foes" (fix-241, [owner]): true = Yes (default: an arrow, bolt or spell missile of the hero
        /// or his party on a knocked-out NPC that may be killed kills it, with a melee finishing move's consequences), false = No
        /// (original: a downed victim takes no damage from them).
        /// </summary>
        public bool RangedFinishesDowned
        {
            get => _rangedFinishesDowned;
            set => Set(ref _rangedFinishesDowned, value);
        }

        void Set<T>(ref T field, T value)
        {
            if (Equals(field, value)) return;
            field = value;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------------------------------------ sliders

        /// <summary>Slider positions of the FOV offset (0…FovSteps).</summary>
        public static int FovSteps => (FovOffsetMax - FovOffsetMin) / FovOffsetStep;

        public static int FovToStep(int offset) => (int)Math.Round((offset - FovOffsetMin) / (double)FovOffsetStep);

        public static int FovFromStep(int step) => Math.Max(FovOffsetMin, Math.Min(FovOffsetMax, FovOffsetMin + step * FovOffsetStep));

        /// <summary>"Original" at 0, else the signed offset ("+10°").</summary>
        public static string FovText(int offset, string language) =>
            offset == 0 ? OriginalText(language) : offset.ToString("+0;-0", CultureInfo.InvariantCulture) + "°";

        public static int UpscalingSteps => Enum.GetValues(typeof(UpscalingMode)).Length - 1;

        /// <summary>The fraction of the screen size rendered (FSR 1 / STP presets: 1/1.5, 1/1.7, 1/2).</summary>
        public static float RenderScale(UpscalingMode mode)
        {
            switch (mode)
            {
                case UpscalingMode.FsrQuality:
                case UpscalingMode.StpQuality: return 1f / 1.5f;
                case UpscalingMode.FsrBalanced:
                case UpscalingMode.StpBalanced: return 1f / 1.7f;
                case UpscalingMode.FsrPerformance:
                case UpscalingMode.StpPerformance: return 0.5f;
                default: return 1f;
            }
        }

        public static bool IsStp(UpscalingMode mode) =>
            mode == UpscalingMode.StpQuality || mode == UpscalingMode.StpBalanced || mode == UpscalingMode.StpPerformance;

        public static string UpscalingText(UpscalingMode mode, string language)
        {
            bool ru = IsRussian(language);
            switch (mode)
            {
                case UpscalingMode.FsrQuality: return ru ? "FSR: качество" : "FSR Quality";
                case UpscalingMode.FsrBalanced: return ru ? "FSR: баланс" : "FSR Balanced";
                case UpscalingMode.FsrPerformance: return ru ? "FSR: скорость" : "FSR Performance";
                case UpscalingMode.StpQuality: return ru ? "STP: качество" : "STP Quality";
                case UpscalingMode.StpBalanced: return ru ? "STP: баланс" : "STP Balanced";
                case UpscalingMode.StpPerformance: return ru ? "STP: скорость" : "STP Performance";
                default: return ru ? "Выкл" : "Off";
            }
        }

        // ---------------------------------------------------------------------------------------------- row texts

        public enum Row { FieldOfView, Upscaling, CombatMusic, MusicTransitions, CompanionsTeleport, CompanionMarkers, TurnKeys, DecorativeFocus, CookFire, ShieldCreatures, WindowCamera, ContinueButton, LateTrophies, CrashRecovery, NpcShoutSubtitles, ForcedRelease, ForgiveHits, ShowFps, KeepWounds, TorchGlow, QuickLootChord, ShowHelmet, RangedFinishesDowned }

        /// <summary>Row labels in the two text languages of the menu (G3's string tables have none; other languages: English).</summary>
        public static string RowText(Row row, string language)
        {
            bool ru = IsRussian(language);
            switch (row)
            {
                case Row.FieldOfView: return ru ? "Поле зрения" : "Field of view";
                case Row.Upscaling: return ru ? "Масштабирование" : "Upscaling";
                case Row.CombatMusic: return ru ? "Боевая музыка" : "Combat music";
                case Row.MusicTransitions: return ru ? "Переходы музыки" : "Music transitions";
                case Row.CompanionsTeleport: return ru ? "Спутники при телепорте" : "Companions on teleport";
                case Row.TurnKeys: return TurnKeysText("Q / E");
                case Row.DecorativeFocus: return ru ? "Подсвечивать бесполезные объекты" : "Focus decorative objects";
                case Row.CookFire: return ru ? "Готовить на любом огне" : "Cook on any fire";
                case Row.ShieldCreatures: return ru ? "Щит защищает от зверей" : "Shield protects from beasts";
                case Row.WindowCamera: return ru ? "Камера в окнах" : "Camera in windows";
                case Row.ContinueButton: return ru ? "Кнопка «Продолжить»" : "Continue button";
                case Row.LateTrophies: return ru ? "Трофеи после обучения" : "Late trophies";
                case Row.CrashRecovery: return ru ? "Восстановление после вылета" : "Crash recovery";
                case Row.NpcShoutSubtitles: return ru ? "Субтитры реплик NPC" : "NPC shout subtitles";
                case Row.ForcedRelease: return ru ? "Освобождение силой засчитывается" : "Freeing by force counts";
                case Row.ForgiveHits: return ru ? "Прощать случайные удары" : "Forgive accidental hits";
                case Row.ShowFps: return ru ? "Показывать FPS" : "Show FPS";
                case Row.KeepWounds: return ru ? "Сохранять раны после загрузки" : "Keep wounds after load";
                case Row.TorchGlow: return ru ? "Ореол факела" : "Torch glow";
                case Row.QuickLootChord: return ru ? "Забрать всё: Ctrl или ПКМ + ЛКМ" : "Take all: Ctrl or RMB + click";
                case Row.ShowHelmet: return ru ? "Показывать шлем" : "Show helmet";
                case Row.RangedFinishesDowned: return ru ? "Стрелы добивают лежачих" : "Arrows finish downed foes";
                default: return ru ? "Спутники на карте" : "Companions on map";
            }
        }

        /// <summary>The two values of a two-position row: position 0 is always the original behaviour.</summary>
        public static string ValueText(Row row, bool on, string language)
        {
            bool ru = IsRussian(language);
            switch (row)
            {
                case Row.CombatMusic: return on ? (ru ? "Только при угрозе" : "Real threats only") : OriginalText(language);
                case Row.MusicTransitions: return on ? (ru ? "Плавные" : "Smooth") : OriginalText(language);
                case Row.CompanionsTeleport: return on ? (ru ? "Идут с героем" : "Come along") : (ru ? "Ждут (оригинал)" : "Wait (original)");
                case Row.TurnKeys: return on ? (ru ? "Обзор камерой" : "Orbit camera") : (ru ? "Поворот героя (оригинал)" : "Turn hero (original)");
                case Row.DecorativeFocus: return on ? (ru ? "Нет" : "No") : (ru ? "Да (оригинал)" : "Yes (original)");
                case Row.TorchGlow:
                case Row.ShowHelmet: return on ? (ru ? "Да (оригинал)" : "Yes (original)") : (ru ? "Нет" : "No");
                case Row.CookFire: return on ? (ru ? "Да" : "Yes") : (ru ? "Нет (оригинал)" : "No (original)");
                case Row.ShieldCreatures:
                case Row.LateTrophies:
                case Row.CrashRecovery:
                case Row.NpcShoutSubtitles:
                case Row.ForcedRelease:
                case Row.ForgiveHits:
                case Row.ShowFps:
                case Row.KeepWounds:
                case Row.QuickLootChord:
                case Row.RangedFinishesDowned:
                case Row.ContinueButton: return on ? (ru ? "Да" : "Yes") : (ru ? "Нет (оригинал)" : "No (original)");
                case Row.WindowCamera: return on ? (ru ? "Как в игре (без приближения)" : "As in play (no zoom)") : OriginalText(language);
                default: return on ? (ru ? "Вкл" : "On") : (ru ? "Выкл" : "Off");
            }
        }

        /// <summary>The turn-keys row's label: the bound keys ("Q / E" by default, a rebinding's keys otherwise).</summary>
        public static string TurnKeysText(string keys) => string.IsNullOrEmpty(keys) ? "Q / E" : keys;

        static string OriginalText(string language) => IsRussian(language) ? "Оригинал" : "Original";

        static bool IsRussian(string language) => string.Equals(language, "Russian", StringComparison.OrdinalIgnoreCase);
    }
}
