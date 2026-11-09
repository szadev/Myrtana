using System;
using System.Collections.Generic;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The dialog in the original look (Docs/formats-gui.md §6.2): the choice page <c>IDD_PAGE_DIALOG_2</c>
    /// (bottom-centred marble panel) with a <see cref="G3ListView"/> that selects on hover and activates on one click,
    /// and the HUD subtitle box (<c>IDD_VIEW_TALK_2</c> at the top-centred <c>IDC_VIEW_TALK_2</c> of
    /// <c>IDD_PAGE_MAIN_2</c>) for spoken lines. Own panel above the HUD.
    /// </summary>
    public sealed class G3DialogScreen : IDialogScreen
    {
        public const string PageLayout = "IDD_PAGE_DIALOG_2", ListSlot = "IDC_LISTCTRL_DIALOG_2";
        const string HudLayout = "IDD_PAGE_MAIN_2", TalkSlot = "IDC_VIEW_TALK_2";
        /// <summary>Above the HUD document (sorting order 100).</summary>
        const int SortingOrder = 110;

        readonly VisualElement _page;
        readonly G3ListView _list;
        readonly G3TalkBox _talk;
        readonly G3Fade _talkFade;

        public event Action<int> ChoiceActivated;

        /// <summary>The skinned screen, or null when the G3 GUI is not imported (the caller keeps the plain look).</summary>
        public static G3DialogScreen TryCreate(Transform parent)
        {
            var skin = G3Skin.Load();
            return skin?.Catalog.Screen(PageLayout) == null ? null : new G3DialogScreen(skin, parent);
        }

        G3DialogScreen(G3Skin skin, Transform parent)
        {
            var root = G3PanelSettings.CreateDocument("Dialog", GuiPanelKind.Page, parent, SortingOrder).rootVisualElement;

            _page = G3LayoutBuilder.Build(skin, PageLayout, new G3LayoutBuilder.Options { ExpandViews = true });
            _page.AddToClassList("g3-fill-parent");
            _page.style.width = StyleKeyword.Null;
            _page.style.height = StyleKeyword.Null;
            root.Add(_page);
            _list = new G3ListView { HoverSelects = true, ActivateOnSingleClick = true };
            _list.AddToClassList("g3-fill-parent");
            _list.Activated += index => ChoiceActivated?.Invoke(index);
            _page.Q(ListSlot)?.Add(_list);

            _talk = new G3TalkBox(skin);
            var hud = skin.Catalog.Screen(HudLayout);
            var slot = hud?.Find(TalkSlot);
            if (slot != null) G3LayoutBuilder.Place(_talk, slot, hud.Width, hud.Height);
            root.Add(_talk);
            _talkFade = new G3Fade(_talk, G3FadeKind.Center);
            Hide();
        }

        public int SelectedChoice => _list.SelectedIndex;

        /// <summary>
        /// G3 StartSay/StopSay: every line grows the box out of its centre over 200 ms (AnimateWindow AW_CENTER,
        /// notes/gui_dialog_hud.md §1.6); the previous line's box is gone first (StopSay of the line before).
        /// </summary>
        public void ShowLine(string speaker, string text, bool newLine)
        {
            _page.style.display = DisplayStyle.None;
            _talk.Text = text;
            if (newLine) _talkFade.Show(false, instant: true);
            _talkFade.Show(true);
        }

        public void ShowChoices(IReadOnlyList<DialogChoiceText> choices, int selected)
        {
            _talkFade.Show(false, instant: true);
            var titles = new string[choices.Count];
            for (int i = 0; i < titles.Length; i++) titles[i] = choices[i].Title;
            _list.SetItems(titles);
            _list.Select(selected, notify: false);
            _page.style.display = DisplayStyle.Flex;
        }

        public void SelectChoice(int index) => _list.Select(index);

        public void Hide()
        {
            _page.style.display = DisplayStyle.None;
            _talkFade?.Show(false, instant: true);
        }
    }
}
