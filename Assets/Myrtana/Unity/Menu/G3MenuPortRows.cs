using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The port's own option rows (<see cref="PortOptions"/>, player feedback items 5, 12 and 15; Docs/controls-options.md
    /// section 7) in the original pages' look: label, theme trackbar and value text in the options' font (TNR 18, the
    /// theme's static text colour), placed in the free rows above each page's first G3 row (28 px pitch):
    /// <list type="bullet">
    /// <item>Video: Show helmet (84, fix-232, [owner] default Yes), Torch glow (112, fix-182, [owner] default No), Keep wounds after load (140, fix-175, [owner] default Yes), Show FPS (168, fix-172, [owner] default No), Forgive accidental hits (196, fix-162, [owner] default Yes), Freeing by force counts (224, fix-161, [owner] default Yes), Crash recovery (252, fix-155, [owner] default Yes), Continue button (280, fix-138, [owner] default Yes), Camera in windows (308, fix-122, [owner] default As in play = no zoom), Shield protects from beasts (336, fix-109, [owner] default No = original), Upscaling (364) and Field of
    /// view (392) above Resolution (420); fix-116 removed fix-102's "Locked chests on map" row (the map page has its own
    /// filter);</item>
    /// <item>Audio: Music transitions (312) and Combat music (340) above the voice language row (368); NPC shout subtitles
    /// (536, fix-156, [owner] default Yes) in G3's empty row between Ambient Volume and Text Language;</item>
    /// <item>Difficulty: Companions on teleport (36) and Companions on map (64) in the free strip at the top of the page
    /// (fix-74: at 344/372 they sat over the "Gothic 3" logo; fix-72: at 380/408 they touched the difficulty box).</item>
    /// <item>Controls: three rows centred in the band between the keyboard layout radios (Standard 532, Custom 568..593) and
    /// Back (682), 28 px apart (<see cref="ControlsPortRowsLayout"/>): the turn keys (595, fix-73; the label names the keys
    /// bound to Camera rotation left / right, "Q / E"), "Focus decorative objects" (623, fix-79: Yes = original / No) and
    /// "Take all: Ctrl or RMB + click" (651, fix-205, [owner] default Yes).
    /// fix-107 removed fix-82's quicksave rotation row. Q / E uses the mouse rows' columns; the longer decorative label
    /// gets a wider right-aligned box ending at the same edge.
    /// "Late trophies" (79, fix-143, [owner] default Yes) and "Cook on any fire" (107, fix-88, [owner] default Yes) sit in
    /// the free band at the top of the page, above G3's Mouse heading (141).</item>
    /// </list>
    /// Position 0 of every row is the original's behaviour. Texts
    /// in Russian or English by the text language (G3's string tables have none). Changes apply and are stored at once.
    /// </summary>
    public sealed class G3MenuPortRows
    {
        readonly PortOptions _port;
        readonly LanguageSettings _languages;
        readonly List<Action> _renderers = new List<Action>();
        readonly HashSet<VisualElement> _pages = new HashSet<VisualElement>();

        public G3MenuPortRows(PortOptions port, LanguageSettings languages)
        {
            _port = port;
            _languages = languages;
            port.Changed += Render;
            languages.Changed += Render;
        }

        string Language => _languages.TextLanguage;

        /// <summary>Top of the first Difficulty-page row (page px of 768; the second is 28 px lower), see <see cref="AddGameRows"/>.</summary>
        const float GameRowsTop = 36f;

        public void AddVideoRows(VisualElement video)
        {
            var model = video.Q<Label>("IDC_LABEL_VIDEO_RESOLUTION_2");
            var columns = (label: 20f, labelWidth: 364f, slider: 408f, value: 632f);
            // fix-109 [owner]: the rule row "Shield protects from beasts" one row above Upscaling (the game-rule pages are full);
            // fix-116: fix-102's "Locked chests on map" row went (the map page's own filter), Shield moved down into its place
            // fix-161 [owner]: "Freeing by force counts" (default Yes) one row above Crash recovery, in the free 224 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.ForcedReleaseTop, "FORCED_RELEASE", PortOptions.Row.ForcedRelease,
                        () => _port.ForcedReleaseCounts, on => _port.ForcedReleaseCounts = on);
            // fix-162 [owner]: "Forgive accidental hits" (default Yes) one row above Freeing by force counts, in the free 196 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.ForgiveHitsTop, "FORGIVE_HITS", PortOptions.Row.ForgiveHits,
                        () => _port.ForgiveAccidentalHits, on => _port.ForgiveAccidentalHits = on);
            // fix-172 [owner]: "Show FPS" (default No) one row above Forgive accidental hits, in the free 168 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.ShowFpsTop, "SHOW_FPS", PortOptions.Row.ShowFps,
                        () => _port.ShowFps, on => _port.ShowFps = on);
            // fix-175 [owner]: "Keep wounds after load" (default Yes) one row above Show FPS, in the free 140 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.KeepWoundsTop, "KEEP_WOUNDS", PortOptions.Row.KeepWounds,
                        () => _port.KeepWoundsAfterLoad, on => _port.KeepWoundsAfterLoad = on);
            // fix-182 [owner]: "Torch glow" (default No) one row above Keep wounds after load, in the free 112 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.TorchGlowTop, "TORCH_GLOW", PortOptions.Row.TorchGlow,
                        () => _port.TorchGlow, on => _port.TorchGlow = on);
            // fix-232 [owner]: "Show helmet" (default Yes = G3) in the free 84 row above Torch glow
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.ShowHelmetTop, "SHOW_HELMET", PortOptions.Row.ShowHelmet,
                        () => _port.ShowHelmet, on => _port.ShowHelmet = on);
            // fix-241 [owner]: "Arrows finish downed foes" (default Yes) in the free 56 row above Show helmet
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.RangedFinishTop, "RANGED_FINISH", PortOptions.Row.RangedFinishesDowned,
                        () => _port.RangedFinishesDowned, on => _port.RangedFinishesDowned = on);
            // fix-155 [owner]: "Crash recovery" (default Yes) one row above Continue button, in the free 252 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.CrashRecoveryTop, "CRASH_RECOVERY", PortOptions.Row.CrashRecovery,
                        () => _port.CrashRecovery, on => _port.CrashRecovery = on);
            // fix-138 [owner]: "Continue button" (default Yes) one row above Camera in windows, in the free 280 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.ContinueButtonTop, "CONTINUE_BUTTON", PortOptions.Row.ContinueButton,
                        () => _port.ContinueButton, on => _port.ContinueButton = on);
            // fix-122 [owner]: "Camera in windows" (default As in play, no zoom) above Shield, in the free 308 row
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.WindowCameraTop, "WINDOW_CAMERA", PortOptions.Row.WindowCamera,
                        () => _port.StillWindowCamera, on => _port.StillWindowCamera = on);
            AddOnOffRow(video, model, columns, VideoPortRowsLayout.ShieldCreaturesTop, "SHIELD_CREATURES", PortOptions.Row.ShieldCreatures,
                        () => _port.ShieldBlocksCreatures, on => _port.ShieldBlocksCreatures = on);
            AddRow(video, model, columns, VideoPortRowsLayout.UpscalingTop, "UPSCALING", PortOptions.Row.Upscaling, PortOptions.UpscalingSteps,
                   () => (int)_port.Upscaling, i => _port.Upscaling = (UpscalingMode)i,
                   i => PortOptions.UpscalingText((UpscalingMode)i, Language));
            AddRow(video, model, columns, VideoPortRowsLayout.FovTop, "FOV", PortOptions.Row.FieldOfView, PortOptions.FovSteps,
                   () => PortOptions.FovToStep(_port.FovOffset), i => _port.FovOffset = PortOptions.FovFromStep(i),
                   i => PortOptions.FovText(PortOptions.FovFromStep(i), Language));
        }

        public void AddAudioRows(VisualElement audio)
        {
            var model = audio.Q<Label>("IDC_LABEL_AUDIO_VOLUME_MASTER_2");
            var columns = (label: 24f, labelWidth: 366f, slider: 416f, value: 640f);
            AddOnOffRow(audio, model, columns, AudioPortRowsLayout.MusicTransitionsTop, "MUSIC_TRANSITIONS", PortOptions.Row.MusicTransitions,
                        () => _port.SmoothMusic, on => _port.SmoothMusic = on);
            AddOnOffRow(audio, model, columns, AudioPortRowsLayout.CombatMusicTop, "COMBAT_MUSIC", PortOptions.Row.CombatMusic,
                        () => _port.CombatMusicThreat, on => _port.CombatMusicThreat = on);
            // fix-156 [owner]: "NPC shout subtitles" (default Yes) in G3's empty row between Ambient Volume and Text Language
            AddOnOffRow(audio, model, columns, AudioPortRowsLayout.NpcShoutSubtitlesTop, "NPC_SHOUT_SUBTITLES", PortOptions.Row.NpcShoutSubtitles,
                        () => _port.NpcShoutSubtitles, on => _port.NpcShoutSubtitles = on);
        }

        /// <summary>The Controls page rows: what the Camera rotation keys do (fix-73, <see cref="G3.Player.KeyOrbitRules"/>), the decorative focus (fix-79) and Cook on any fire (fix-88).</summary>
        public void AddControlsRows(VisualElement controls)
        {
            if (controls == null) return;
            var model = controls.Q<Label>("IDC_LABEL_CONTROLS_ALT_CAMERA_2");
            var columns = (label: 112f, labelWidth: 282f, slider: 420f, value: 644f);
            AddOnOffRow(controls, model, columns, ControlsPortRowsLayout.TurnKeysTop, "TURN_KEYS", PortOptions.Row.TurnKeys,
                        () => _port.TurnKeysOrbit, on => _port.TurnKeysOrbit = on,
                        () => PortOptions.TurnKeysText(G3.Player.KeyOrbitRules.KeysText(G3.Unity.G3Input.Keys)));
            // fix-79: the label is longer than the mouse rows' 282 px column, so it gets G3's right-aligned option style in a
            // wider box ending at the same right edge (394).
            AddOnOffRow(controls, null, (label: 24f, labelWidth: 370f, slider: 420f, value: 644f), DecorativeFocusTop, "DECORATIVE_FOCUS",
                        PortOptions.Row.DecorativeFocus, () => _port.SkipDecorativeFocus, on => _port.SkipDecorativeFocus = on);
            // fix-205 [owner]: "Take all: Ctrl or RMB + click" (default Yes), the band's third row, same wide label box
            AddOnOffRow(controls, null, (label: 24f, labelWidth: 370f, slider: 420f, value: 644f), ControlsPortRowsLayout.QuickLootChordTop,
                        "QUICK_LOOT_CHORD", PortOptions.Row.QuickLootChord, () => _port.QuickLootChord, on => _port.QuickLootChord = on);
            // fix-88 [owner]: the free band above G3's Mouse heading (141), below the frame (~65)
            AddOnOffRow(controls, model, columns, ControlsPortRowsLayout.CookFireTop, "COOK_FIRE", PortOptions.Row.CookFire,
                        () => _port.CookOnAnyFire, on => _port.CookOnAnyFire = on);
            // fix-143 [owner]: "Late trophies" (default Yes) one row above Cook on any fire in the same top band
            AddOnOffRow(controls, model, columns, ControlsPortRowsLayout.LateTrophiesTop, "LATE_TROPHIES", PortOptions.Row.LateTrophies,
                        () => _port.LateTrophies, on => _port.LateTrophies = on);
        }

        /// <summary>Top of the Controls page's "Focus decorative objects" row (one 28 px row below the turn keys).</summary>
        public const float DecorativeFocusTop = ControlsPortRowsLayout.DecorativeFocusTop;

        /// <summary>
        /// The gameplay rows on the Difficulty page (G3's page of game rules). The page has no free row places: the "Gothic 3"
        /// logo fills its middle (y ~250..400 of 768, the "3" x ~768..940 from y ~60) and the difficulty box sits below it
        /// (x ~362..662, y ~418..653), so the rows go to the free strip at the top, between the frame (y ~28) and the banner
        /// rod (y ~90..115 at x 82..341), left of the "3" (labels right-aligned against the sliders, fix-74).
        /// </summary>
        public void AddGameRows(VisualElement difficulty)
        {
            if (difficulty == null) return;
            var columns = (label: 24f, labelWidth: 346f, slider: 386f, value: 598f);
            AddOnOffRow(difficulty, null, columns, GameRowsTop, "COMPANIONS_TELEPORT", PortOptions.Row.CompanionsTeleport,
                        () => _port.CompanionsFollowTeleport, on => _port.CompanionsFollowTeleport = on);
            AddOnOffRow(difficulty, null, columns, GameRowsTop + 28, "COMPANION_MARKERS", PortOptions.Row.CompanionMarkers,
                        () => _port.CompanionMarkers, on => _port.CompanionMarkers = on);
            Render();
        }

        void AddOnOffRow(VisualElement page, Label model, (float label, float labelWidth, float slider, float value) columns, float top,
                         string control, PortOptions.Row row, Func<bool> get, Action<bool> set, Func<string> labelText = null) =>
            AddRow(page, model, columns, top, control, row, 1, () => get() ? 1 : 0, i => set(i == 1),
                   i => PortOptions.ValueText(row, i == 1, Language), labelText);

        /// <summary>A row <c>IDC_SLIDER_PORT_{control}_2</c>: label, slider 200×25 and value, the label placed like the page's own.</summary>
        void AddRow(VisualElement page, Label model, (float label, float labelWidth, float slider, float value) columns, float top,
                    string control, PortOptions.Row row, int max, Func<int> get, Action<int> set, Func<int, string> valueText,
                    Func<string> labelText = null)
        {
            var label = G3MenuSettingsPages.PlacedLabel(page, columns.label, top, columns.labelWidth);
            LikeLayoutLabel(label, model);
            string slotName = "IDC_SLIDER_PORT_" + control + "_2";
            var slot = new VisualElement { name = slotName, pickingMode = PickingMode.Position };
            slot.style.position = Position.Absolute;
            slot.style.left = columns.slider;
            slot.style.top = top + 4;
            slot.style.width = 200;
            slot.style.height = 25;
            page.Add(slot);
            var slider = G3MenuSettingsPages.Slider(page, slotName, 0, max, set);
            var value = G3MenuSettingsPages.PlacedLabel(page, columns.value, top, 348);
            _renderers.Add(() =>
            {
                int position = get();
                label.text = labelText != null ? labelText() : PortOptions.RowText(row, Language);
                slider?.SetValueWithoutNotify(position);
                value.text = valueText(position);
            });
            // A page shown again (display none → flex) lays out anew: the texts follow a language change made meanwhile.
            if (_pages.Add(page)) page.RegisterCallback<GeometryChangedEvent>(_ => Render());
        }

        /// <summary>
        /// G3's option label style (IDC_LABEL_AUDIO_*/IDC_LABEL_VIDEO_* in g3.gui.res, 0x10000026): right-aligned against the
        /// sliders, vertically centred, single line.
        /// </summary>
        const uint OptionLabelStyle = 0x10000026;

        /// <summary>
        /// The page's own label column (G3's audio labels are right-aligned against the sliders); a page without option rows
        /// of its own (Difficulty) gets G3's option label style (fix-72: its labels sat left-aligned at the page edge).
        /// </summary>
        static void LikeLayoutLabel(Label label, Label model)
        {
            if (model == null)
            {
                G3Text.Align(label, new StaticTextFormat(OptionLabelStyle));
                return;
            }
            label.style.left = model.style.left;
            label.style.right = model.style.right;
            label.style.width = model.style.width;
            label.style.marginLeft = model.style.marginLeft;
            label.style.unityTextAlign = model.style.unityTextAlign;
        }

        public void Render()
        {
            foreach (var r in _renderers) r();
        }
    }
}
