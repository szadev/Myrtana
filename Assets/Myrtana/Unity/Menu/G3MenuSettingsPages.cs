using System;
using System.Collections.Generic;
using G3.Player;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The option pages in the original's layouts (Docs/controls-options.md):
    /// <list type="bullet">
    /// <item>Audio (<c>IDD_MENU_OPTIONS_AUDIO_2</c>): master, voice, music, FX and ambient volume, the text language,
    /// subtitles on/off (<c>HUD_Audio_Subtitle</c>), speaker setup (<c>HUD_Audio_Speaker_Setup</c>, Mono … Dolby ProLogic,
    /// with G3's <c>HUD_Menu_ResetWarning</c>: it applies on the next start) and, as the port's own row above master,
    /// the voice language (<c>HUD_Audio_Language_Voice</c>).</item>
    /// <item>Controls (<c>IDD_MENU_OPTIONS_CONTROLS_2</c>): mouse sensitivity, invert and smoothing per axis (sliders
    /// −100…100 as G3's), the Alt. Camera row as the port's first-person option, "Invert rear view Y-Axis", and the keyboard
    /// layout radios: Standard (G3 CreateDefaultKeys) / Custom (opens the key list, <see cref="G3MenuKeyMappingPage"/>).</item>
    /// <item>Video (<c>IDD_MENU_OPTIONS_VIDEO_2</c>): resolution, V-Sync, brightness, contrast, gamma red/green/blue; the
    /// port's GUI size takes the reset-warning row (changes apply at once, nothing needs a restart).</item>
    /// <item>Performance and its custom page: <see cref="G3MenuPerformancePages"/>.</item>
    /// <item>The port's Remaster page (world, trees v2, characters): <see cref="G3MenuRemasterPage"/>.</item>
    /// </list>
    /// Sliders are the theme's trackbar; changes apply and are stored at once.
    /// </summary>
    public sealed class G3MenuSettingsPages
    {
        /// <summary>Text and voice languages offered (the voices exist in these two).</summary>
        public static readonly string[] Languages = { "Russian", "English" };

        internal static readonly TextId BackText = new TextId("HUD_Menu_Back"), OnText = new TextId("HUD_Slider_Value_On"),
                                        OffText = new TextId("HUD_Slider_Value_Off");
        static readonly TextId TextLanguageText = new TextId("HUD_Audio_Language_Text"), VoiceLanguageText = new TextId("HUD_Audio_Language_Voice"),
                               FirstPersonText = new TextId("HUD_SessionKey_FirstPerson"),
                               SubtitleText = new TextId("HUD_Audio_Subtitle"), SpeakerText = new TextId("HUD_Audio_Speaker_Setup"),
                               ResetWarningText = new TextId("HUD_Menu_ResetWarning");
        static readonly (VolumeChannel channel, string control, string text)[] Volumes =
        {
            (VolumeChannel.Master, "MASTER", "HUD_Audio_Volume_Master"), (VolumeChannel.Voice, "VOICE", "HUD_Audio_Volume_Voice"),
            (VolumeChannel.Music, "MUSIC", "HUD_Audio_Volume_Music"), (VolumeChannel.Effects, "EFFECTS", "HUD_Audio_Volume_Effects"),
            (VolumeChannel.Ambient, "AMBIENT", "HUD_Audio_Volume_Ambient"),
        };
        static readonly (VideoColor color, string control, string text)[] ColorRows =
        {
            (VideoColor.Brightness, "VIDEO_BRIGHTNESS", "HUD_Video_Brightness"), (VideoColor.Contrast, "VIDEO_CONTRAST", "HUD_Video_Contrast"),
            (VideoColor.GammaRed, "VIDEO_GAMMA_RED", "HUD_Video_Gamma_Red"), (VideoColor.GammaGreen, "VIDEO_GAMMA_GREEN", "HUD_Video_Gamma_Green"),
            (VideoColor.GammaBlue, "VIDEO_GAMMA_BLUE", "HUD_Video_Gamma_Blue"),
        };

        readonly G3MenuView _view;
        readonly PlayerOptions _options;
        readonly LanguageSettings _languages;
        readonly Func<string, string> _languageName;
        readonly VisualElement _audio, _controlsPage, _video;
        readonly Dictionary<VolumeChannel, (SliderInt slider, Label value)> _volumes = new Dictionary<VolumeChannel, (SliderInt, Label)>();
        readonly SliderInt _textLanguage;
        readonly Label _textLanguageValue;
        SliderInt _voiceLanguage;
        Label _voiceLanguageValue;
        readonly List<Action> _renderers = new List<Action>();
        readonly VisualElement _standardMark, _customMark;
        readonly List<(int width, int height, int refresh)> _modes = new List<(int, int, int)>();
        readonly G3MenuKeyMappingPage _keyMapping;
        readonly G3MenuPerformancePages _performance;
        readonly G3MenuRemasterPage _remaster;
        readonly G3MenuRemasterDetailsPage _remasterDetails;
        readonly G3MenuGamepadPage _gamepad;

        /// <summary>The port's own rows (FOV, upscaling, music, companions; <see cref="G3MenuPortRows"/>).</summary>
        internal G3MenuPortRows PortRows { get; }

        public G3MenuSettingsPages(VisualElement frame, G3MenuView view, PlayerOptions options, ControlOptions controls,
                                   LanguageSettings languages, Func<string, string> languageName)
        {
            _view = view;
            _options = options;
            _languages = languages;
            _languageName = languageName;
            PortRows = new G3MenuPortRows(options.Port, languages);

            // ------------------------------------------------------------------------------------------------ audio
            _audio = G3MenuView.Page(frame, "IDD_MENU_OPTIONS_AUDIO_2");
            foreach (var (channel, control, text) in Volumes)
            {
                view.Label(_audio, "IDC_LABEL_AUDIO_VOLUME_" + control + "_2", new TextId(text));
                var slider = Slider(_audio, "IDC_SLIDER_AUDIO_VOLUME_" + control + "_2", 0, PlayerOptions.Steps,
                                    step => options.SetVolume(channel, PlayerOptions.FromStep(step)));
                _volumes[channel] = (slider, _audio.Q<Label>("IDC_VALUE_AUDIO_VOLUME_" + control + "_2"));
            }
            view.Label(_audio, "IDC_LABEL_AUDIO_LANGUAGE_SUBTITLES_2", TextLanguageText);
            _textLanguage = Slider(_audio, "IDC_SLIDER_AUDIO_LANGUAGE_SUBTITLES_2", 0, Languages.Length - 1, i => languages.TextLanguage = Languages[i]);
            _textLanguageValue = _audio.Q<Label>("IDC_VALUE_AUDIO_LANGUAGE_SUBTITLES_2");
            OnOffRow(_audio, "AUDIO_SUBTITLES_ONOFF", SubtitleText, () => options.Subtitles, on => options.Subtitles = on);
            SliderRow(_audio, "AUDIO_SPEAKER_SETUP", SpeakerText, PlayerOptions.SpeakerModeMin, PlayerOptions.SpeakerModeMax,
                      () => PlayerOptions.SpeakerSliderPosition(options.SpeakerMode, PlayerOptionsApplier.RunningSpeakerMode),
                      mode => options.SpeakerMode = mode, mode => PlayerOptions.SpeakerModeName(mode, _languages.TextLanguage));
            view.Label(_audio, "IDC_LABEL_AUDIO_RESET_WARNING_2", ResetWarningText);
            AddVoiceLanguageRow(_audio, languages);
            PortRows.AddAudioRows(_audio);
            view.Button(_audio, "IDC_BUTTON_MENU_OPTIONS_AUDIO_BACK_2", BackText, () => view.Menu.State.Back());

            // --------------------------------------------------------------------------------------------- controls
            _controlsPage = G3MenuView.Page(frame, "IDD_MENU_OPTIONS_CONTROLS_2");
            var c = _controlsPage;
            view.Label(c, "IDC_LABEL_CONTROLS_MOUSE_2", new TextId("HUD_Controls_Mouse"));
            view.Label(c, "IDC_LABEL_CONTROLS_KEYBOARD_2", new TextId("HUD_Controls_Keyboard"));
            MouseRow(c, "SENSITIVITY_XAXIS", "HUD_Controls_Sensitivity_Axis_X", () => controls.SensitivityX, x => controls.SensitivityX = x);
            MouseRow(c, "SENSITIVITY_YAXIS", "HUD_Controls_Sensitivity_Axis_Y", () => controls.SensitivityY, x => controls.SensitivityY = x);
            OnOffRow(c, "CONTROLS_INVERT_XAXIS", new TextId("HUD_Controls_Invert_Axis_X"), () => controls.InvertX, on => controls.InvertX = on);
            OnOffRow(c, "CONTROLS_INVERT_YAXIS", new TextId("HUD_Controls_Invert_Axis_Y"), () => controls.InvertY, on => controls.InvertY = on);
            MouseRow(c, "SMOOTHING_XAXIS", "HUD_Controls_Smoothing_Axis_X", () => controls.SmoothingX, x => controls.SmoothingX = x);
            MouseRow(c, "SMOOTHING_YAXIS", "HUD_Controls_Smoothing_Axis_Y", () => controls.SmoothingY, x => controls.SmoothingY = x);
            OnOffRow(c, "CONTROLS_ALT_CAMERA", FirstPersonText, () => options.FirstPerson, on => options.FirstPerson = on);
            OnOffRow(c, "CONTROLS_CAMLOOKAROUNDINVERSE", new TextId("HUD_Controls_CamLookaroundInverse"),
                     () => controls.LookaroundInverse, on => controls.LookaroundInverse = on);
            _standardMark = G3MenuDifficultyPage.Row(c, view, "IDC_RADIOBUTTON_CONTROLS_STANDARD_2", new TextId("HUD_Controls_Standard"), 24,
                                                     "radio", () => controls.UseStandardKeys());
            _customMark = G3MenuDifficultyPage.Row(c, view, "IDC_RADIOBUTTON_CONTROLS_CUSTOM_2", new TextId("HUD_Controls_Custom"), 24,
                                                   "radio", () =>
                                                   {
                                                       controls.Layout = ControlOptions.Custom;
                                                       view.Menu.State.Show(MenuPage.ControlsCustom); // G3: OpenMenu(9)
                                                   });
            _renderers.Add(() =>
            {
                G3MenuView.Show(_standardMark, controls.Layout != ControlOptions.Custom);
                G3MenuView.Show(_customMark, controls.Layout == ControlOptions.Custom);
            });
            PortRows.AddControlsRows(c); // fix-73: what the Camera rotation keys (Q / E) do
            view.Button(c, "IDC_BUTTON_MENU_OPTIONS_CONTROLS_BACK_2", BackText, () => view.Menu.State.Back());
            AddGamepadButton(c, languages);
            _keyMapping = new G3MenuKeyMappingPage(frame, view, controls);
            _gamepad = new G3MenuGamepadPage(frame, view, controls, languages);

            // ------------------------------------------------------------------------------------------------ video
            _video = G3MenuView.Page(frame, "IDD_MENU_OPTIONS_VIDEO_2");
            var v = _video;
            G3MenuView.Hide(v, "IDC_LABEL_VIDEO_RESET_WARNING_2");
            CollectModes();
            SliderRow(v, "VIDEO_RESOLUTION", new TextId("HUD_Video_Resolution"), 0, Math.Max(0, _modes.Count - 1),
                      ModeIndex, SetMode, _ => ResolutionText());
            OnOffRow(v, "VIDEO_VSYNC", new TextId("HUD_Video_VSync"), () => options.Video.VSync, on => options.Video.VSync = on);
            foreach (var (color, control, text) in ColorRows)
                SliderRow(v, control, new TextId(text), VideoOptions.SliderMin, VideoOptions.SliderMax,
                          () => options.Video.Slider(color), p => options.Video.SetSlider(color, p), VideoOptions.SliderText);
            AddGuiSizeRow(v);
            AddGraphicsApiRow(v); // fix-211 [owner]: DirectX 11 / 12 (experimental), applied by the launcher at the next start
            PortRows.AddVideoRows(v);
            view.Button(v, "IDC_BUTTON_MENU_OPTIONS_VIDEO_BACK_2", BackText, () => view.Menu.State.Back());

            // ------------------------------------------------------------------------------------------ performance
            _performance = new G3MenuPerformancePages(frame, view, options.Performance);

            // --------------------------------------------------------------------------------------------- remaster
            _remaster = new G3MenuRemasterPage(frame, view, languages);   // the port's own page (world, trees v2, characters)
            _remasterDetails = new G3MenuRemasterDetailsPage(frame, view, languages);   // opts-1: desert, dunes, colour

            options.Changed += Render;
            controls.Changed += Render;
            languages.Changed += Render;
        }

        /// <summary>The custom key list waits for a key (Esc cancels it instead of leaving the page).</summary>
        public bool WaitingForKey => _keyMapping.Waiting;

        /// <summary>A pad key list cell waits for a pad button (B binds then instead of going back).</summary>
        public bool WaitingForPad => _keyMapping.WaitingForPad;

        /// <summary>Cancels a key wait; true when there was one.</summary>
        public bool CancelKeyWait() => _keyMapping.Cancel();

        /// <summary>Per frame while the menu is open (the key list's capture).</summary>
        public void Tick() => _keyMapping.Tick();

        public void Show(MenuPage page)
        {
            G3MenuView.Show(_audio, page == MenuPage.Audio);
            G3MenuView.Show(_controlsPage, page == MenuPage.Controls);
            G3MenuView.Show(_video, page == MenuPage.Video);
            _keyMapping.Show(page == MenuPage.ControlsCustom || page == MenuPage.GamepadCustom, pad: page == MenuPage.GamepadCustom);
            _gamepad?.Show(page == MenuPage.Gamepad);
            _performance.Show(page);
            _remaster.Show(page == MenuPage.Remaster);
            _remasterDetails.Show(page == MenuPage.RemasterDetails);
            if (page == MenuPage.Audio || page == MenuPage.Controls || page == MenuPage.Video) Render();
        }

        void Render()
        {
            if (_view.Localizer == null) return;
            foreach (var kv in _volumes)
            {
                int percent = _options.Volume(kv.Key);
                kv.Value.slider?.SetValueWithoutNotify(PlayerOptions.ToStep(percent));
                if (kv.Value.value != null) kv.Value.value.text = PlayerOptions.Percent(percent);
            }
            SetLanguage(_textLanguage, _textLanguageValue, _languages.TextLanguage);
            foreach (var r in _renderers) r();
            PortRows.Render();
        }

        // ----------------------------------------------------------------------------------------------------- rows

        /// <summary>
        /// gamepad-1: the port's "Gamepad" button on the Controls page (no G3 control), right of Back on its row (Back is
        /// 462-562 × 682-714): opens <see cref="MenuPage.Gamepad"/> (<see cref="G3MenuGamepadPage"/>).
        /// </summary>
        void AddGamepadButton(VisualElement page, LanguageSettings languages)
        {
            string Text() => languages.TextLanguage == "Russian" ? "Геймпад" : "Gamepad";
            var b = new Myrtana.Unity.Ui.Components.G3Button(Text(), () => _view.Menu.State.Show(MenuPage.Gamepad))
            {
                name = "IDC_BUTTON_MENU_OPTIONS_CONTROLS_GAMEPAD_2",
            };
            G3Text.Font(b, G3FontMap.ThemeButton);
            b.style.position = Position.Absolute;
            b.style.left = 590;
            b.style.top = 682;
            b.style.width = 160;
            b.style.height = 32;
            page.Add(b);
            languages.Changed += () => b.text = Text();
        }

        /// <summary>A mouse row: slider −100…100 over G3's stored 0…1 (<see cref="ControlOptions.FromSlider"/>), value "N%".</summary>
        void MouseRow(VisualElement page, string control, string text, Func<float> get, Action<float> set) =>
            SliderRow(page, "CONTROLS_" + control, new TextId(text), ControlOptions.SliderMin, ControlOptions.SliderMax,
                      () => ControlOptions.ToSlider(get()), p => set(ControlOptions.FromSlider(p)), VideoOptions.SliderText);

        /// <summary>A two-position row with On/Off as its value (G3's on/off sliders).</summary>
        void OnOffRow(VisualElement page, string control, TextId text, Func<bool> get, Action<bool> set) =>
            SliderRow(page, control, text, 0, 1, () => get() ? 1 : 0, i => set(i == 1), i => _view.Text(i == 1 ? OnText : OffText));

        /// <summary>
        /// A layout row <c>IDC_LABEL_/SLIDER_/VALUE_{control}_2</c>: G3 string, theme slider, value text (a value label
        /// right of the slider when the layout has none).
        /// </summary>
        void SliderRow(VisualElement page, string control, TextId text, int min, int max, Func<int> get, Action<int> set,
                       Func<int, string> valueText)
        {
            _view.Label(page, "IDC_LABEL_" + control + "_2", text);
            string sliderName = "IDC_SLIDER_" + control + "_2";
            var slider = Slider(page, sliderName, min, max, set);
            var value = page.Q<Label>("IDC_VALUE_" + control + "_2") ?? ValueLabel(page, sliderName);
            _renderers.Add(() =>
            {
                int position = get();
                slider?.SetValueWithoutNotify(position);
                if (value != null) value.text = valueText(position);
            });
        }

        /// <summary>The port's GUI size (no G3 row): label, slider and value placed in the video page's reset-warning row.</summary>
        /// <summary>
        /// The port's voice language (G3 has one language for text and speech) as an extra row above master volume, in the
        /// page's 28 px row pitch.
        /// </summary>
        void AddVoiceLanguageRow(VisualElement page, LanguageSettings languages)
        {
            const int top = 368; // master volume is at 396
            var label = PlacedLabel(page, 24, top, 366);
            LikeLayoutLabel(label, page.Q<Label>("IDC_LABEL_AUDIO_VOLUME_MASTER_2"));
            var slot = new VisualElement { name = "IDC_SLIDER_AUDIO_LANGUAGE_VOICE_2", pickingMode = PickingMode.Position };
            slot.style.position = Position.Absolute;
            slot.style.left = 416;
            slot.style.top = top + 4;
            slot.style.width = 200;
            slot.style.height = 23;
            page.Add(slot);
            _voiceLanguage = AddSlider(slot, 0, Languages.Length - 1, i => languages.VoiceLanguage = Languages[i]);
            _voiceLanguageValue = PlacedLabel(page, 640, top, 304);
            _renderers.Add(() =>
            {
                label.text = _view.Text(VoiceLanguageText);
                SetLanguage(_voiceLanguage, _voiceLanguageValue, _languages.VoiceLanguage);
            });
        }

        /// <summary>
        /// An added row's label in the column of the page's own labels (r31: the voice row's 'Язык' sat at the left edge
        /// while G3's audio labels are right-aligned against the sliders): the model's horizontal place and alignment.
        /// </summary>
        static void LikeLayoutLabel(Label label, Label model)
        {
            if (model == null) return;
            label.style.left = model.style.left;
            label.style.right = model.style.right;
            label.style.width = model.style.width;
            label.style.marginLeft = model.style.marginLeft;
            label.style.unityTextAlign = model.style.unityTextAlign;
        }

        void AddGuiSizeRow(VisualElement page)
        {
            const int top = 616; // below gamma blue (588; rows 28 px apart), in IDC_LABEL_VIDEO_RESET_WARNING_2's place
            var label = PlacedLabel(page, 20, top, 364);
            LikeLayoutLabel(label, page.Q<Label>("IDC_LABEL_VIDEO_RESOLUTION_2")); // fix-72: right-aligned like the page's labels
            var slot = new VisualElement { name = "IDC_SLIDER_VIDEO_GUISIZE_2", pickingMode = PickingMode.Position };
            slot.style.position = Position.Absolute;
            slot.style.left = 408;
            slot.style.top = top + 4;
            slot.style.width = 200;
            slot.style.height = 25;
            page.Add(slot);
            var slider = AddSlider(slot, 0, PlayerOptions.UiScaleSteps, step => _options.UiScale = PlayerOptions.UiScaleFromStep(step));
            var value = PlacedLabel(page, 632, top, 348);
            _renderers.Add(() =>
            {
                label.text = UiScaleText(_languages.TextLanguage);
                slider.SetValueWithoutNotify(PlayerOptions.UiScaleToStep(_options.UiScale));
                value.text = PlayerOptions.Percent(_options.UiScale);
            });
        }

        /// <summary>
        /// fix-211 [owner]: "Graphics API (after restart)" DirectX 11 (default) / DirectX 12 (experimental) (<see cref="LaunchConfig"/>, stored in
        /// launch.cfg for the launcher, not in the prefs) one row below the GUI size (616; the page's last free row above
        /// Back). The value shows the stored choice; the running API changes at the next start.
        /// </summary>
        void AddGraphicsApiRow(VisualElement page)
        {
            const int top = 644;
            var label = PlacedLabel(page, 20, top, 364);
            LikeLayoutLabel(label, page.Q<Label>("IDC_LABEL_VIDEO_RESOLUTION_2"));
            var slot = new VisualElement { name = "IDC_SLIDER_VIDEO_GRAPHICSAPI_2", pickingMode = PickingMode.Position };
            slot.style.position = Position.Absolute;
            slot.style.left = 408;
            slot.style.top = top + 4;
            slot.style.width = 200;
            slot.style.height = 25;
            page.Add(slot);
            // dx12-3: 0 DirectX 11, 1 DirectX 12, 2 DirectX 12 without graphics jobs
            var slider = AddSlider(slot, 0, 2, step => LaunchConfigFile.Api = (GraphicsApi)step);
            var value = PlacedLabel(page, 632, top, 348);
            _renderers.Add(() =>
            {
                label.text = LaunchConfig.RowText(_languages.TextLanguage);
                slider.SetValueWithoutNotify((int)LaunchConfigFile.Api);
                value.text = LaunchConfig.ValueText(LaunchConfigFile.Api, _languages.TextLanguage);
            });
            LaunchConfigFile.Changed += Render;
        }

        /// <summary>G3 has no string for the GUI size; the two text languages of <see cref="Languages"/> get their own.</summary>
        static string UiScaleText(string language) => language == "Russian" ? "Размер интерфейса" : "Interface size";

        // ------------------------------------------------------------------------------------------------ resolution

        /// <summary>The screen's modes (width × height @ Hz, distinct); the current size when the list is empty.</summary>
        void CollectModes()
        {
            var seen = new HashSet<(int, int, int)>();
            foreach (var r in Screen.resolutions)
            {
                var m = (r.width, r.height, (int)Math.Round(r.refreshRateRatio.value));
                if (seen.Add(m)) _modes.Add(m);
            }
            if (_modes.Count == 0) _modes.Add((Screen.width, Screen.height, 0));
        }

        int ModeIndex()
        {
            var v = _options.Video;
            int w = v.NativeResolution ? Screen.width : v.Width, h = v.NativeResolution ? Screen.height : v.Height;
            int best = _modes.Count - 1;
            for (int i = 0; i < _modes.Count; i++)
                if (_modes[i].width == w && _modes[i].height == h && (v.RefreshRate == 0 || _modes[i].refresh == v.RefreshRate))
                    best = i;
            return Math.Max(0, best);
        }

        void SetMode(int index)
        {
            if (index < 0 || index >= _modes.Count) return;
            var m = _modes[index];
            _options.Video.SetResolution(m.width, m.height, m.refresh);
        }

        string ResolutionText()
        {
            var v = _options.Video;
            return v.NativeResolution ? VideoOptions.ResolutionText(Screen.width, Screen.height) : VideoOptions.ResolutionText(v.Width, v.Height);
        }

        void SetLanguage(SliderInt slider, Label value, string language)
        {
            slider?.SetValueWithoutNotify(Math.Max(0, Array.IndexOf(Languages, language)));
            if (value != null) value.text = _languageName(language);
        }

        // ------------------------------------------------------------------------------------------------- helpers

        /// <summary>A theme trackbar with the positions <paramref name="min"/>…<paramref name="max"/> in a slider slot of the layout.</summary>
        internal static SliderInt Slider(VisualElement page, string name, int min, int max, Action<int> changed)
        {
            var slot = page.Q(name);
            if (slot == null) return null;
            slot.pickingMode = PickingMode.Position;
            return AddSlider(slot, min, max, changed);
        }

        static SliderInt AddSlider(VisualElement slot, int min, int max, Action<int> changed)
        {
            var slider = new SliderInt(min, Math.Max(min, max)) { pageSize = Math.Max(1, (max - min) / 20), focusable = true };
            slider.AddToClassList("unity-slider"); // the theme's trackbar rules (G3Theme.uss)
            slider.style.position = Position.Absolute;
            slider.style.left = slider.style.top = slider.style.right = slider.style.bottom = 0;
            slider.style.marginLeft = slider.style.marginRight = slider.style.marginTop = slider.style.marginBottom = 0;
            var tracker = slider.Q(className: "unity-base-slider__tracker");
            if (tracker != null)
            {
                tracker.style.top = 2;
                tracker.style.bottom = 2;
                tracker.style.height = StyleKeyword.Auto;
                tracker.style.marginTop = 0;
            }
            var dragger = slider.Q(className: "unity-base-slider__dragger");
            if (dragger != null)
            {
                dragger.style.width = 44;
                dragger.style.height = 19;
                dragger.style.top = 3;
                dragger.style.marginTop = 0;
            }
            slider.RegisterValueChangedCallback(e => changed(e.newValue));
            slot.Add(slider);
            return slider;
        }

        /// <summary>A value text right of a slider that has none in the layout (like the audio page: 24 px gap, 18 px).</summary>
        internal static Label ValueLabel(VisualElement page, string sliderName)
        {
            var slot = page.Q(sliderName);
            if (!(slot?.userData is WindowData w)) return null;
            return PlacedLabel(slot.parent, w.Rect[2] + 24, w.Rect[1] - 4, 300);
        }

        /// <summary>A static text in the options' style (TNR 18, the theme's static text colour) at a page position.</summary>
        internal static Label PlacedLabel(VisualElement parent, float left, float top, float width)
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.style.position = Position.Absolute;
            label.style.left = left;
            label.style.top = top;
            label.style.width = width;
            label.style.height = 25;
            G3Text.Font(label, 18);
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.color = TextColor;
            parent.Add(label);
            return label;
        }

        internal static Color TextColor
        {
            get
            {
                var skin = G3Skin.Load();
                return skin != null ? skin.ThemeText("static_text", WidgetState.Normal, G3Palette.Label.ToColor()) : G3Palette.Label.ToColor();
            }
        }
    }
}
