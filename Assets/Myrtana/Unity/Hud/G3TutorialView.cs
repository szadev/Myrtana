using System;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Story;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The tutorial box (<c>IDD_PAGE_TUTORIAL_2</c> → <c>IDD_VIEW_TUTORIAL_2</c>, 450×379 in the middle of the screen):
    /// "Tip" (<c>HL_Tip</c>), the <c>TUT_*</c> text, the check box "Do not show tips" (<c>HUD_NoMoreTips</c>) and OK
    /// (<c>HUD_Ok</c>). It fades in and out like every G3 page and stays until OK, Enter or Esc; it has no timer.
    /// The occasions (G3 <c>gCGUIManager::OpenTutorial</c> callers): the new game's start, the first opening of the
    /// inventory, character, quest log, map and trade pages, the first drawn melee weapon, bow and spell, the talks with
    /// Diego, Milten and Gorn and with Lester, and Ardea's liberation. Which tips were shown and the check box are saved
    /// with the game (<see cref="TutorialTips"/>). The box waits for a conversation, the menu or a loading screen to end;
    /// the game stands still and the hero's controls rest while it is up (G3's script waits for the box to close; the
    /// first fight tip comes in Ardea's battle). Drawn above the pages and the HUD, below the dialog and the menu.
    /// </summary>
    [DefaultExecutionOrder(-60)] // before the game menu and the pages, which must not take the Esc that closes the box
    public sealed class G3TutorialView : MonoBehaviour, ISessionView
    {
        public const string Layout = "IDD_PAGE_TUTORIAL_2";
        public const int SortingOrder = 107;
        static readonly TextId TitleText = new TextId("HL_Tip"), NoMoreTipsText = new TextId("HUD_NoMoreTips"), OkText = new TextId("HUD_Ok");
        const float PollSeconds = 1f;

        TutorialTips _tips;
        HeroControlLock _controls;
        Func<bool> _blocked;
        Func<TutorialTip?> _stance;
        GameSession _session;
        IDisposable _questWatch;
        VisualElement _page;
        G3Fade _fade;
        Label _title, _message, _check;
        VisualElement _mark;
        G3Button _ok;
        TutorialTip? _shown;

        /// <summary>A tip is on screen (its OK button and check box take the mouse; fix-87).</summary>
        public bool IsOpen => _shown != null;
        float _nextPoll;

        public TutorialTips Tips => _tips;

        static int s_escapeFrame = -1;

        /// <summary>This frame's Esc closed a tip box: the menu and the pages ignore it.</summary>
        public static bool EscapeConsumed => s_escapeFrame == Time.frameCount;

        /// <summary>The view on its own document, or null when the G3 GUI lacks the layout.</summary>
        public static G3TutorialView TryCreate(Transform parent)
        {
            var skin = G3Skin.Load();
            if (skin?.Catalog.Screen(Layout) == null) return null;
            var doc = G3PanelSettings.CreateDocument("G3 tutorial", GuiPanelKind.Page, parent, SortingOrder);
            var view = doc.gameObject.AddComponent<G3TutorialView>();
            view.Build(doc.rootVisualElement);
            return view;
        }

        /// <param name="blocked">True while no box may appear (a conversation, the menu, a loading screen, not in play).</param>
        /// <param name="stance">The hero's drawn stance (Fight, Bow or Spell tip), or null.</param>
        public void Init(TutorialTips tips, HeroControlLock controls, Func<bool> blocked, Func<TutorialTip?> stance)
        {
            _tips = tips;
            _controls = controls;
            _blocked = blocked;
            _stance = stance;
        }

        void Build(VisualElement root)
        {
            _page = G3Pages.Build(Layout);
            root.Add(_page);
            _fade = new G3Fade(_page);
            _title = _page.Q<Label>("IDC_LABEL_TUTORIAL_2");
            var messageSlot = _page.Q("IDC_LABEL_TUTORIAL_MESSAGE_2");
            if (messageSlot != null)
            {
                var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
                scroll.AddToClassList("g3-fill-parent");
                _message = new Label { pickingMode = PickingMode.Ignore };
                G3Text.Font(_message, G3FontMap.ThemeText); // a Static without a res font: the theme's 16 px bold
                _message.style.whiteSpace = WhiteSpace.Normal;
                _message.style.unityTextAlign = TextAnchor.UpperLeft;
                _message.style.color = G3Palette.Text.ToColor();
                scroll.Add(_message);
                if (messageSlot is Label label) label.text = string.Empty;
                messageSlot.pickingMode = PickingMode.Position;
                messageSlot.Add(scroll);
            }
            BuildCheckBox(_page.Q("IDC_CHECKBOX_TUTORIAL_2"));
            _ok = _page.Q<G3Button>("IDC_BUTTON_TUTORIAL_OK_2");
            if (_ok != null) _ok.clicked += Close;
        }

        /// <summary>The theme's check box (as on the difficulty page) with its text; the whole row takes the click.</summary>
        void BuildCheckBox(VisualElement slot)
        {
            if (slot == null) return;
            var skin = G3Skin.Load();
            slot.pickingMode = PickingMode.Position;
            slot.style.flexDirection = FlexDirection.Row;
            slot.style.alignItems = Align.Center;
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.style.width = box.style.height = 17;
            box.style.flexShrink = 0;
            box.style.marginRight = 8;
            var back = skin?.Sprite("theme:button_checkbox_back") ?? skin?.Sprite("theme:button_check_back");
            if (back != null) box.style.backgroundImage = new StyleBackground(back);
            _mark = new VisualElement { pickingMode = PickingMode.Ignore };
            _mark.style.position = Position.Absolute;
            _mark.style.left = _mark.style.top = _mark.style.right = _mark.style.bottom = 0;
            var front = skin?.Sprite("theme:button_check_front");
            if (front != null) _mark.style.backgroundImage = new StyleBackground(front);
            else _mark.style.backgroundColor = G3Palette.Label.ToColor();
            box.Add(_mark);
            slot.Add(box);
            _check = new Label { pickingMode = PickingMode.Ignore };
            G3Text.Font(_check, G3FontMap.ThemeText);
            _check.style.color = skin != null ? skin.ThemeText("static_text", WidgetState.Normal, G3Palette.Label.ToColor()) : G3Palette.Label.ToColor();
            _check.style.unityTextAlign = TextAnchor.MiddleLeft;
            slot.Add(_check);
            slot.RegisterCallback<ClickEvent>(_ =>
            {
                if (_tips == null) return;
                _tips.HideTips = !_tips.HideTips; // gCPlayerMemory_PS::SetHideTips
                ShowMark();
            });
        }

        void ShowMark()
        {
            if (_mark != null) _mark.style.display = _tips != null && _tips.HideTips ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void Bind(GameSession session)
        {
            Hide();
            _questWatch?.Dispose();
            _session = session;
            _questWatch = session.Events.Subscribe<QuestStatusChanged>(e =>
            {
                if (e.Quest.Name == TutorialTips.ArdeaQuest && e.To.IsSuccess()) _tips?.Request(TutorialTip.AfterFight);
            });
        }

        void OnDestroy() => _questWatch?.Dispose();

        /// <summary>The first opening of a page asks for its tip.</summary>
        public void Request(TutorialTip tip) => _tips?.Request(tip);

        void Update()
        {
            if (_session == null || _tips == null) return;
            if (_stance?.Invoke() is TutorialTip stance) _tips.Request(stance);
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                PollTalks();
            }
            if (_shown != null)
            {
                var kb = global::G3.Unity.G3Input.GameKeyboard;
                // gamepad-1: A closes like Enter, B or Start like Esc
                bool padBack = global::G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Back) || global::G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Menu);
                bool padOk = global::G3.Unity.G3Pad.Ui(G3.Player.UiCommand.Accept);
                bool esc = kb != null && kb.escapeKey.wasPressedThisFrame || padBack;
                if (esc) s_escapeFrame = Time.frameCount;
                if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) || esc || padOk) Close();
                return;
            }
            if (_tips.Next is TutorialTip next && (_blocked == null || !_blocked())) Show(next);
        }

        /// <summary>PS_Normal: Diego, Milten and Gorn talked to → AfterFriends; Lester → AfterLester.</summary>
        void PollTalks()
        {
            var actors = _session.Actors;
            if (!_tips.HasSeen(TutorialTip.AfterFriends))
            {
                bool all = true;
                foreach (var friend in TutorialTips.Friends) all &= actors.HasTalkedToPlayer(friend);
                if (all) _tips.Request(TutorialTip.AfterFriends);
            }
            if (!_tips.HasSeen(TutorialTip.AfterLester) && actors.HasTalkedToPlayer(TutorialTips.Lester)) _tips.Request(TutorialTip.AfterLester);
        }

        void Show(TutorialTip tip)
        {
            _shown = tip;
            var text = _session.Localizer;
            if (_title != null) _title.text = text.Text(TitleText);
            if (_check != null) _check.text = text.Text(NoMoreTipsText);
            if (_ok != null) _ok.text = text.Text(OkText);
            // G3 writes the line breaks of its strings as the two characters \n
            if (_message != null)
                _message.text = G3Text.Paragraph(text.Text(TutorialTips.TextOf(tip)).Replace("\\n", "\n"), G3FontMap.ThemeText.PortSize);
            ShowMark();
            _page.BringToFront();
            _fade.Show(true);
            _controls?.Acquire(this);
            GamePause.Set(this, true);
        }

        /// <summary>Closes the open tip like its OK button (dev runner: `safe`, `notips`); releases its pause.</summary>
        public void CloseOpen() => Close();

        void Close()
        {
            if (_shown == null) return;
            _tips?.Dismiss();
            Hide();
        }

        void Hide()
        {
            if (_shown == null) return;
            _shown = null;
            _fade.Show(false);
            _controls?.Release(this);
            GamePause.Set(this, false);
        }

        void OnDisable() => GamePause.Set(this, false);
    }
}
