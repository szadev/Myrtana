using System;
using System.Collections.Generic;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The in-game menu in the original look (Docs/formats-gui.md §6.12): <c>IDD_MENU_MAIN_2</c> with Resume (in the
    /// New Game place), Load, Save, Options and Quit, and the port's Continue above them at the title and after the hero's
    /// death (fix-138, <see cref="AddContinueButton"/>); the save/load list (<see cref="G3MenuFilesPage"/>); the options page
    /// with Performance, Video, the port's Remaster, Audio, Controls (and their custom pages; <see cref="G3MenuSettingsPages"/>) and Difficulty,
    /// and the confirm box <c>IDD_MENU_CONFIRM_2</c>.
    /// The 4:3 pages sit in their own document above every other UI, whose panel scales the 1024×768 frame to the screen
    /// height (<see cref="UiScaleRules.FitScale"/>, independent of the player's GUI size); wider screens show the visible
    /// page's background blurred and darkened at the sides with soft shadows along the frame, like the loading screen.
    /// Texts are G3's strings and follow the text language at once.
    /// </summary>
    public sealed class G3MenuView
    {
        /// <summary>Above the HUD (100), the pages (105) and the dialog (110).</summary>
        public const int SortingOrder = 120;

        public static readonly TextId SaveFailedText = new TextId("HUD_File_SaveFailed");
        static readonly TextId ResumeText = new TextId("HUD_Menu_Resume_Game"), LoadText = new TextId("HUD_Menu_Load_Game"),
                               SaveText = new TextId("HUD_Menu_Save_Game"), OptionsText = new TextId("HUD_Menu_Options"),
                               QuitText = new TextId("HUD_Menu_Quit_Game"), QuitConfirmText = new TextId("HUD_Confirm_Quit"),
                               BackText = new TextId("HUD_Menu_Back"), YesText = new TextId("HUD_Request_Yes"),
                               NoText = new TextId("HUD_Request_No"), OkText = new TextId("HUD_Request_Ok"),
                               NewGameText = new TextId("HUD_Menu_New_Game"), CreditsText = new TextId("HUD_Menu_Credits");
        static readonly (string control, string text)[] OptionButtons =
        {
            ("IDC_BUTTON_MENU_OPTIONS_PERFORMANCE_2", "HUD_Menu_Options_Performance"), ("IDC_BUTTON_MENU_OPTIONS_VIDEO_2", "HUD_Menu_Options_Video"),
            ("IDC_BUTTON_MENU_OPTIONS_AUDIO_2", "HUD_Menu_Options_Audio"), ("IDC_BUTTON_MENU_OPTIONS_CONTROLS_2", "HUD_Menu_Options_Controls"),
            ("IDC_BUTTON_MENU_OPTIONS_DIFFICULTY_2", "HUD_Menu_Options_Difficulty"),
        };

        readonly VisualElement _root;
        readonly VisualElement _main, _options, _confirmLayer;
        readonly G3MenuFilesPage _files;
        readonly G3MenuSettingsPages _settings;
        readonly List<(TextElement element, TextId id, float width)> _texts = new List<(TextElement, TextId, float)>();
        /// <summary>The port's own texts (no G3 string): element, text in the current language, fit width.</summary>
        readonly List<(TextElement element, Func<string> text, float width)> _ownTexts = new List<(TextElement, Func<string>, float)>();
        readonly G3Button _saveButton, _yes, _no, _ok, _firstButton, _creditsButton;
        G3Button _continueButton;
        /// <summary>The main column's buttons with G3's place and the place they take while Continue shows (fix-138).</summary>
        readonly List<(G3Button button, WindowData original, WindowData withContinue)> _mainColumn =
            new List<(G3Button, WindowData, WindowData)>();
        bool _continueLayout;
        readonly VisualElement _frame, _backdrop;
        string _backdropImage;
        G3MenuBalancing _balancing;
        G3MenuDifficultyPage _difficulty;
        readonly Label _confirmMessage;
        GameMenu _menu;
        GameSession _session;

        /// <summary>The menu view, or null when the G3 GUI is not imported.</summary>
        public static G3MenuView TryCreate(Transform parent, PlayerOptions options, G3.Player.ControlOptions controls,
                                           LanguageSettings languages, Func<string, string> languageName)
        {
            if (G3Skin.Load() == null) return null;
            var doc = G3PanelSettings.CreateDocument("G3 menu", GuiPanelKind.Screen, parent, SortingOrder);
            return new G3MenuView(doc.rootVisualElement, options, controls, languages, languageName);
        }

        G3MenuView(VisualElement documentRoot, PlayerOptions options, G3.Player.ControlOptions controls, LanguageSettings languages,
                   Func<string, string> languageName)
        {
            _root = new VisualElement { name = "G3 menu" };
            _root.AddToClassList("g3-fill-parent");
            _root.style.position = Position.Absolute;
            _root.style.left = _root.style.top = _root.style.right = _root.style.bottom = 0;
            _root.style.backgroundColor = Color.black;
            _root.style.justifyContent = Justify.Center;
            _root.style.alignItems = Align.Center;
            _root.style.display = DisplayStyle.None;
            documentRoot.Add(_root);
            GateInput(_root);
            _backdrop = new VisualElement { name = "backdrop", pickingMode = PickingMode.Ignore };
            _backdrop.style.position = Position.Absolute;
            _backdrop.style.left = _backdrop.style.top = _backdrop.style.right = _backdrop.style.bottom = 0;
            _backdrop.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            _root.Add(_backdrop);
            var frame = new VisualElement { name = "frame", pickingMode = PickingMode.Ignore };
            frame.style.width = G3PanelSettings.ReferenceWidth;
            frame.style.height = G3PanelSettings.ReferenceHeight;
            frame.style.flexShrink = 0;
            _root.Add(frame);
            _frame = frame;
            G3Backdrop.AddEdgeShadows(frame, G3PanelSettings.ReferenceWidth, G3PanelSettings.ReferenceHeight);
            Languages = languages;
            languages.Changed += Refresh; // also at the title, where no session exists yet

            _main = Page(frame, "IDD_MENU_MAIN_2");
            Hide(_main, "IDC_BUTTON_MENU_MAIN_BACK_2", "IDC_BUTTON_MENU_DEBUG_2",
                 "IDC_VIEW_MENU_MAIN_REQUEST_2", "IDC_BUTTON_MENU_ALTERNATIVE_BALANCING_OFF_2", "IDC_BUTTON_MENU_ALTERNATIVE_BALANCING_ON_2",
                 "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_IMAGE_OFF_2", "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_IMAGE_ON_2",
                 "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_OFF_2", "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_ON_2", "IDC_STATIC_MENU_ALTERNATIVE_BALANCING_2");
            if (_main.Q<Label>("IDC_STATIC_APP_VERSION2") is Label version)
            {
                // top centre, TNR -12 bold, #7F7F7F (gCHUDMenuMain2::OnInitDialog); rebrand: "Gothic 3 Remaster — demo <date>"
                // instead of CP's "pb/cpt/sb v1.75.14" (AppVersionText), language-dependent
                version.text = RemasterBrand.VersionLine(languages.TextLanguage, BuildStamp.Value);
                _ownTexts.Add((version, () => RemasterBrand.VersionLine(languages.TextLanguage, BuildStamp.Value), 0f));
                version.style.color = new Color(0x7F / 255f, 0x7F / 255f, 0x7F / 255f);
                version.style.unityTextAlign = TextAnchor.MiddleCenter;
                version.pickingMode = PickingMode.Ignore;
            }
            _firstButton = Button(_main, "IDC_BUTTON_MENU_NEW_GAME_2", ResumeText, () => _menu.FirstButton());
            _creditsButton = Button(_main, "IDC_BUTTON_MENU_CREDITS_2", CreditsText, () => _menu.ShowCredits());
            Button(_main, "IDC_BUTTON_MENU_LOAD_GAME_2", LoadText, () => _menu.State.Show(MenuPage.Load));
            _saveButton = Button(_main, "IDC_BUTTON_MENU_SAVE_GAME_2", SaveText, OnSave);
            Button(_main, "IDC_BUTTON_MENU_OPTIONS_2", OptionsText, () => _menu.State.Show(MenuPage.Options));
            Button(_main, "IDC_BUTTON_MENU_QUIT_2", QuitText, () => _menu.State.Ask(new MenuRequest(QuitConfirmText, () => _menu.Quit())));
            AddContinueButton(languages);

            _files = new G3MenuFilesPage(Page(frame, "IDD_MENU_FILES_2"), this);

            _options = Page(frame, "IDD_MENU_OPTIONS_2");
            Hide(_options, "IDC_VIEW_MENU_OPTIONS_REQUEST_2");
            foreach (var (control, text) in OptionButtons)
            {
                var b = Button(_options, control, new TextId(text), null);
                if (b == null) continue;
                if (control.Contains("_AUDIO_")) b.clicked += () => _menu.State.Show(MenuPage.Audio);
                else if (control.Contains("_CONTROLS_")) b.clicked += () => _menu.State.Show(MenuPage.Controls);
                else if (control.Contains("_VIDEO_")) b.clicked += () => _menu.State.Show(MenuPage.Video);
                else if (control.Contains("_PERFORMANCE_")) b.clicked += () => _menu.State.Show(MenuPage.Performance);
                else if (control.Contains("_DIFFICULTY_")) b.clicked += () => { if (_difficulty != null) _menu.State.Show(MenuPage.Difficulty); };
            }
            Button(_options, "IDC_BUTTON_MENU_OPTIONS_BACK_2", BackText, () => _menu.State.Back());
            AddRemasterButton(languages);

            _settings = new G3MenuSettingsPages(frame, this, options, controls, languages, languageName);
            PadFocus = new G3PadFocus(frame);
            PadHints = new G3PadHintBar();
            frame.Add(PadHints);

            _confirmLayer = new VisualElement { name = "confirm" };
            _confirmLayer.AddToClassList("g3-fill-parent");
            _confirmLayer.AddToClassList(G3PadFocus.ModalClass); // gamepad-1: the pad focus stays in the box
            _confirmLayer.style.position = Position.Absolute;
            _confirmLayer.style.left = _confirmLayer.style.top = _confirmLayer.style.right = _confirmLayer.style.bottom = 0;
            _confirmLayer.style.backgroundColor = new Color(0, 0, 0, 0.35f);
            _confirmLayer.style.display = DisplayStyle.None;
            frame.Add(_confirmLayer);
            var box = G3LayoutBuilder.Build(G3Skin.Load(), "IDD_MENU_CONFIRM_2") ?? new VisualElement();
            box.style.position = Position.Absolute;
            box.style.left = 271; // IDC_VIEW_MENU_MAIN_REQUEST_2
            box.style.top = 202;
            _confirmLayer.Add(box);
            _confirmMessage = box.Q<Label>("IDC_LABEL_MENU_REQUEST_2");
            if (_confirmMessage != null)
            {
                _confirmMessage.style.unityTextAlign = TextAnchor.MiddleCenter;
                _confirmMessage.style.whiteSpace = WhiteSpace.Normal;
            }
            _yes = Button(box, "IDC_BUTTON_MENU_REQUEST_YES_2", YesText, () => _menu.State.Answer(true));
            _no = Button(box, "IDC_BUTTON_MENU_REQUEST_NO_2", NoText, () => _menu.State.Answer(false));
            _ok = Button(box, "IDC_BUTTON_MENU_REQUEST_IGNORE_2", OkText, () => _menu.State.Answer(true));
        }

        internal GameMenu Menu => _menu;
        internal GameSession Session => _session;
        /// <summary>Texts without a session (the title screen).</summary>
        public ILocalizer Fallback { get; set; }
        internal ILocalizer Localizer => _session?.Localizer ?? Fallback;
        /// <summary>Text and voice language (port texts G3 has no string for, e.g. the autosave's name).</summary>
        internal LanguageSettings Languages { get; }

        /// <summary>The CP rule choices: the title's Alternative Balancing switch and the options' Difficulty page.</summary>
        public void AttachRuleChoices(NewGameOptions options)
        {
            if (options == null || _balancing != null) return;
            _balancing = new G3MenuBalancing(_main, this, options);
            _difficulty = G3MenuDifficultyPage.TryCreate(_frame, this, options);
            _settings.PortRows.AddGameRows(_difficulty?.Page);
            _confirmLayer.BringToFront();
        }

        public void Attach(GameMenu menu) => _menu = menu;

        /// <summary>
        /// fix-233: while the menu's input gate is shut (<see cref="GameMenu.InputGate"/>: the menu just appeared under the
        /// click or key that skipped a video) no press, release, click, Enter or Space reaches a button; a release counts only
        /// after a press taken while the gate was open, so no click begun before the menu appeared completes on it.
        /// </summary>
        void GateInput(VisualElement root)
        {
            root.RegisterCallback<PointerDownEvent>(e => { if (!(_menu?.InputGate.PointerDown(e.pointerId) ?? true)) e.StopImmediatePropagation(); },
                                                    TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(e => { if (!(_menu?.InputGate.PointerUp(e.pointerId) ?? true)) e.StopImmediatePropagation(); },
                                                  TrickleDown.TrickleDown);
            root.RegisterCallback<MouseDownEvent>(StopWhileShut, TrickleDown.TrickleDown);
            root.RegisterCallback<MouseUpEvent>(StopWhileShut, TrickleDown.TrickleDown);
            root.RegisterCallback<ClickEvent>(StopWhileShut, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(StopWhileShut, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationCancelEvent>(StopWhileShut, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(StopWhileShut, TrickleDown.TrickleDown);
        }

        void StopWhileShut(EventBase e)
        {
            if (_menu != null && _menu.InputGate.Blocks) e.StopImmediatePropagation();
        }

        public G3MenuFilesPage Files => _files;
        /// <summary>The port's Continue button (fix-138), or null when the layout lacks the main column.</summary>
        public G3Button ContinueButton => _continueButton;
        public G3MenuSettingsPages Settings => _settings;

        /// <summary>gamepad-1: the pad's focus ring over the menu's buttons and sliders (<see cref="G3PadFocus"/>).</summary>
        public G3PadFocus PadFocus { get; private set; }

        /// <summary>gamepad-1: "A Select  B Back" at the bottom of the menu while the pad is in use.</summary>
        public G3PadHintBar PadHints { get; private set; }
        public VisualElement Root => _root;

        /// <summary>Shows the state's page and confirm box with texts in the current language.</summary>
        public void Render(GameMenu menu)
        {
            _menu = menu;
            var state = menu.State;
            if (_session != menu.Session)
            {
                if (_session != null) _session.Settings.Languages.Changed -= Refresh;
                _session = menu.Session;
                if (_session != null) _session.Settings.Languages.Changed += Refresh;
            }
            _root.style.display = state.IsOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (!state.IsOpen || Localizer == null) return;
            Show(_main, state.Page == MenuPage.Main);
            var buttons = menu.Buttons;
            if (_firstButton != null) Rebind(_firstButton, buttons.Resume ? ResumeText : NewGameText);
            bool showContinue = buttons.Continue && _continueButton != null;
            ShowContinue(showContinue, showContinue && state.Page == MenuPage.Main && menu.CanContinue);
            Show(_saveButton, buttons.Save);
            Show(_creditsButton, buttons.Credits);
            _balancing?.Show(buttons.AlternativeBalancing);
            _difficulty?.Show(state.Page == MenuPage.Difficulty);
            Show(_options, state.Page == MenuPage.Options);
            _files.Show(state.Page == MenuPage.Save || state.Page == MenuPage.Load, state.Page == MenuPage.Save);
            _settings.Show(state.Page);
            UpdateBackdrop();
            // Greyed while saving is refused (dead hero: a click explains why) and at the title, where it takes no clicks.
            SetDisabled(_saveButton, !buttons.SaveAvailable || _session == null || menu.Saves.SaveBlockReason != null);
            _saveButton?.SetEnabled(buttons.SaveAvailable);

            var request = state.Request;
            _confirmLayer.style.display = request != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (request != null)
            {
                if (_confirmMessage != null) _confirmMessage.text = request.MessageText != null ? request.MessageText() : Text(request.Message);
                Show(_yes, !request.IsNotice);
                Show(_no, !request.IsNotice);
                Show(_ok, request.IsNotice);
            }
            Localize();
        }

        /// <summary>fix-222: makes the main page's blurred side fill ahead (a GPU read-back), so the title's first frame has it.</summary>
        public void Prewarm()
        {
            if (_main?.userData is WindowData main && !string.IsNullOrEmpty(main.Image))
                G3Backdrop.Blurred(G3Skin.Load()?.Sprite(main.Image), G3Backdrop.SideBrightness);
        }

        /// <summary>The sides show the visible page's background (or the main page's), blurred and darkened.</summary>
        void UpdateBackdrop()
        {
            string image = null;
            foreach (var child in _frame.Children())
                if (child.style.display.value != DisplayStyle.None && child.userData is WindowData w && !string.IsNullOrEmpty(w.Image))
                {
                    image = w.Image;
                    break;
                }
            if (image == null && _main.userData is WindowData main) image = main.Image;
            if (image == _backdropImage) return;
            _backdropImage = image;
            var blurred = string.IsNullOrEmpty(image) ? null : G3Backdrop.Blurred(G3Skin.Load()?.Sprite(image), G3Backdrop.SideBrightness);
            _backdrop.style.backgroundImage = blurred != null ? new StyleBackground(blurred) : new StyleBackground(StyleKeyword.None);
        }

        void Refresh()
        {
            if (_menu != null && _menu.State.IsOpen) Render(_menu);
        }

        void OnSave()
        {
            if (_menu.Saves.SaveBlockReason != null) _menu.State.Ask(new MenuRequest(SaveFailedText, notice: true));
            else _menu.State.Show(MenuPage.Save);
        }

        internal string Text(TextId id) => Localizer?.Text(id) ?? id.Value;

        void Localize()
        {
            foreach (var (element, id, width) in _texts)
            {
                element.text = Text(id);
                if (width > 0 && element.panel != null) G3Text.FitWidth(element, width, 19);
            }
            foreach (var (element, text, width) in _ownTexts)
            {
                element.text = text();
                if (width > 0 && element.panel != null) G3Text.FitWidth(element, width, 19);
            }
        }

        public const string ContinueButtonName = "IDC_BUTTON_MENU_CONTINUE_2";

        /// <summary>The main page's column, top to bottom (G3: 252×32 at x 386, y 368…564, no gaps; Docs/formats-gui.md §6.12).</summary>
        static readonly string[] MainColumn =
        {
            "IDC_BUTTON_MENU_NEW_GAME_2", "IDC_BUTTON_MENU_LOAD_GAME_2", "IDC_BUTTON_MENU_SAVE_GAME_2", "IDC_BUTTON_MENU_OPTIONS_2",
            "IDC_BUTTON_MENU_CREDITS_2", "IDC_BUTTON_MENU_QUIT_2",
        };

        /// <summary>
        /// fix-138 [owner]: the port's "Continue" (no G3 control; <see cref="ContinueGame"/>): a copy of the New Game button
        /// placed above it. The "Gothic" logo ends right above the column and CP's Alternative Balancing switch starts 24 px
        /// below Quit, so the seven buttons share the six original buttons' span (368-564) as touching 28 px buttons
        /// (<see cref="MenuButtonColumn.InsertBefore"/>, no gap like G3's column); without Continue (in play, option off) the
        /// column keeps G3's places.
        /// </summary>
        void AddContinueButton(LanguageSettings languages)
        {
            var column = new List<MenuButtonColumn.Slot>();
            WindowData newGame = null;
            foreach (var control in MainColumn)
            {
                if (!(_main.Q<G3Button>(control)?.userData is WindowData w) || w.Rect == null) return;
                column.Add(new MenuButtonColumn.Slot(control, w.Rect[1], w.Rect[3] - w.Rect[1]));
                if (control == MainColumn[0]) newGame = w;
            }
            var fitted = MenuButtonColumn.InsertBefore(column, MainColumn[0], ContinueButtonName, minGap: 0f);
            if (fitted == null || newGame == null) return;
            int height = (int)fitted.Height;
            foreach (var control in MainColumn)
            {
                var b = _main.Q<G3Button>(control);
                var w = (WindowData)b.userData;
                _mainColumn.Add((b, w, MovedTo(w, w.ResName, (int)fitted.Tops[control], height)));
            }
            var data = MovedTo(newGame, ContinueButtonName, (int)fitted.Tops[ContinueButtonName], height);
            _continueButton = new G3Button(ContinueGame.ButtonText(languages.TextLanguage), () => _menu.Continue())
            {
                name = ContinueButtonName, userData = data,
            };
            if (data.Font != null) G3Text.Font(_continueButton, data.Font);
            else G3Text.Font(_continueButton, G3FontMap.ThemeButton);
            G3LayoutBuilder.Place(_continueButton, data, G3PanelSettings.ReferenceWidth, G3PanelSettings.ReferenceHeight);
            _continueButton.style.display = DisplayStyle.None;
            _main.Q<G3Button>(MainColumn[0]).parent.Add(_continueButton);
            _ownTexts.Add((_continueButton, () => ContinueGame.ButtonText(languages.TextLanguage), data.Width - 12));
        }

        /// <summary>Shows or hides Continue and moves the column between G3's places and the seven-button places; greyed out
        /// (no clicks) while no save exists, like Save at the title.</summary>
        void ShowContinue(bool visible, bool available)
        {
            if (_continueButton == null) return;
            Show(_continueButton, visible);
            SetDisabled(_continueButton, !available);
            _continueButton.SetEnabled(available);
            if (visible == _continueLayout) return;
            _continueLayout = visible;
            foreach (var (button, original, withContinue) in _mainColumn)
            {
                var data = visible ? withContinue : original;
                button.userData = data;
                G3LayoutBuilder.Place(button, data, G3PanelSettings.ReferenceWidth, G3PanelSettings.ReferenceHeight);
            }
        }

        public const string RemasterButtonName = "IDC_BUTTON_MENU_OPTIONS_REMASTER_2";

        /// <summary>
        /// The port's "Remaster" button on the options page (no G3 control): a copy of the Video button placed right below
        /// it; the six buttons share the five original rows' span (444-624), so the column stays inside the button frame
        /// painted into the page's background (fix-72; <see cref="MenuButtonColumn.Insert"/>: 28 px buttons, about 30 px apart). Opens <see cref="MenuPage.Remaster"/> (<see cref="G3MenuRemasterPage"/>).
        /// </summary>
        void AddRemasterButton(LanguageSettings languages)
        {
            var column = new List<MenuButtonColumn.Slot>();
            WindowData video = null;
            foreach (var (control, _) in OptionButtons)
            {
                if (!(_options.Q<G3Button>(control)?.userData is WindowData w) || w.Rect == null) continue;
                column.Add(new MenuButtonColumn.Slot(control, w.Rect[1], w.Rect[3] - w.Rect[1]));
                if (control == "IDC_BUTTON_MENU_OPTIONS_VIDEO_2") video = w;
            }
            if (video == null) return;
            var fitted = MenuButtonColumn.Insert(column, "IDC_BUTTON_MENU_OPTIONS_VIDEO_2", RemasterButtonName);
            if (fitted == null) return;
            int height = (int)fitted.Height;
            foreach (var slot in column)
            {
                var b = _options.Q<G3Button>(slot.Name);
                var w = (WindowData)b.userData;
                int top = (int)fitted.Tops[slot.Name];
                if (top == w.Rect[1] && height == w.Rect[3] - w.Rect[1]) continue;
                var moved = MovedTo(w, w.ResName, top, height);   // a copy: the skin catalog's data stays as G3 has it
                b.userData = moved;
                G3LayoutBuilder.Place(b, moved, G3PanelSettings.ReferenceWidth, G3PanelSettings.ReferenceHeight);
            }
            var data = MovedTo(video, RemasterButtonName, (int)fitted.Tops[RemasterButtonName], height);
            var button = new G3Button(RemasterOptions.PageName(languages.TextLanguage), () => _menu.State.Show(MenuPage.Remaster))
            {
                name = RemasterButtonName, userData = data,
            };
            if (data.Font != null) G3Text.Font(button, data.Font);
            else G3Text.Font(button, G3FontMap.ThemeButton);
            G3LayoutBuilder.Place(button, data, G3PanelSettings.ReferenceWidth, G3PanelSettings.ReferenceHeight);
            _options.Q<G3Button>("IDC_BUTTON_MENU_OPTIONS_VIDEO_2").parent.Add(button);
            _ownTexts.Add((button, () => RemasterOptions.PageName(languages.TextLanguage), data.Width - 12));
        }

        static WindowData MovedTo(WindowData w, string name, int top, int height) => new WindowData
        {
            Class = w.Class, GfcClass = w.GfcClass, ResName = name, Text = w.Text, Anchor = w.Anchor, Style = w.Style, Font = w.Font,
            Image = w.Image, TextColor = w.TextColor, Attributes = w.Attributes,
            Rect = new[] { w.Rect[0], top, w.Rect[2], top + height },
        };

        /// <summary>A menu layout filling the 4:3 frame.</summary>
        internal static VisualElement Page(VisualElement frame, string layout)
        {
            var page = G3LayoutBuilder.Build(G3Skin.Load(), layout) ?? new VisualElement();
            page.style.position = Position.Absolute;
            page.style.left = page.style.top = 0;
            page.style.display = DisplayStyle.None;
            frame.Add(page);
            return page;
        }

        /// <summary>The layout's button <paramref name="name"/> with a G3 string; null when the layout lacks it.</summary>
        internal G3Button Button(VisualElement page, string name, TextId text, Action clicked)
        {
            var b = page.Q<G3Button>(name);
            if (b == null) return null;
            if (clicked != null) b.clicked += clicked;
            float width = b.userData is WindowData w ? w.Width - 12 : 0;
            _texts.Add((b, text, width));
            return b;
        }

        /// <summary>Changes the G3 string an element shows (Load/Save button of the files page).</summary>
        internal void Rebind(TextElement element, TextId text)
        {
            int i = _texts.FindIndex(t => t.element == element);
            if (i >= 0) _texts[i] = (element, text, _texts[i].width);
        }

        /// <summary>A static text of the layout shown with a G3 string.</summary>
        internal Label Label(VisualElement page, string name, TextId text)
        {
            var label = page.Q<Label>(name);
            if (label != null) _texts.Add((label, text, 0));
            return label;
        }

        internal static void Hide(VisualElement page, params string[] names)
        {
            foreach (var n in names)
            {
                var e = page.Q(n);
                if (e != null) e.style.display = DisplayStyle.None;
            }
        }

        internal static void Show(VisualElement e, bool visible)
        {
            if (e != null) e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>The theme's disabled look; the button still takes clicks (Save then explains why it is refused).</summary>
        internal static void SetDisabled(VisualElement e, bool disabled) => e?.EnableInClassList("g3--disabled", disabled);
    }
}
