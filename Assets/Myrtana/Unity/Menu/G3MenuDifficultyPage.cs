using System;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The options' Difficulty page (<c>IDD_MENU_DIFFICULTY_2</c>, Docs/game-start.md): Easy / Medium / Hard radio buttons
    /// and the CP "Alternative A.I." check box, filled into the layout's slots with the theme's radio and check boxes.
    /// The values are <see cref="NewGameOptions"/>; a running game keeps its rules until the next new or loaded game.
    /// </summary>
    public sealed class G3MenuDifficultyPage
    {
        const float BoxSize = 17f;
        static readonly (string slot, Difficulty value, string text)[] Radios =
        {
            ("IDC_RADIOBUTTON_MENU_DIFFICULTY_EASY_2", Difficulty.Easy, "HUD_Difficulty_Easy"),
            ("IDC_RADIOBUTTON_MENU_DIFFICULTY_MEDIUM_2", Difficulty.Medium, "HUD_Difficulty_Medium"),
            ("IDC_RADIOBUTTON_MENU_DIFFICULTY_HARD_2", Difficulty.Hard, "HUD_Difficulty_Hard"),
        };
        const string AiSlot = "IDC_CHECKBOX_MENU_DIFFICULTY_AIMODE_2";
        static readonly TextId AiText = new TextId("HUD_Difficulty_AIMode"), BackText = new TextId("HUD_Menu_Back");

        readonly VisualElement _page;
        readonly NewGameOptions _options;
        readonly VisualElement[] _radioMarks = new VisualElement[3];
        VisualElement _aiMark;

        /// <summary>The page, or null when the layout is missing.</summary>
        public static G3MenuDifficultyPage TryCreate(VisualElement frame, G3MenuView view, NewGameOptions options)
        {
            var skin = G3Skin.Load();
            if (skin == null || skin.Catalog.Screen("IDD_MENU_DIFFICULTY_2") == null) return null;
            return new G3MenuDifficultyPage(G3MenuView.Page(frame, "IDD_MENU_DIFFICULTY_2"), view, options);
        }

        G3MenuDifficultyPage(VisualElement page, G3MenuView view, NewGameOptions options)
        {
            _page = page;
            _options = options;
            for (int i = 0; i < Radios.Length; i++)
            {
                var value = Radios[i].value;
                _radioMarks[i] = Row(page, view, Radios[i].slot, new TextId(Radios[i].text), 24, "radio", () => _options.Difficulty = value);
            }
            _aiMark = Row(page, view, AiSlot, AiText, 18, "check", () => _options.AlternativeAI = !_options.AlternativeAI);
            view.Button(page, "IDC_BUTTON_MENU_DIFFICULTY_BACK_2", BackText, () => view.Menu.State.Back());
            _options.Changed += Refresh;
            Refresh();
        }

        /// <summary>The page (the port's gameplay rows go to the top strip, <see cref="G3MenuPortRows.AddGameRows"/>).</summary>
        internal VisualElement Page => _page;

        public void Show(bool visible)
        {
            G3MenuView.Show(_page, visible);
            if (visible) Refresh();
        }

        void Refresh()
        {
            for (int i = 0; i < Radios.Length; i++) G3MenuView.Show(_radioMarks[i], _options.Difficulty == Radios[i].value);
            G3MenuView.Show(_aiMark, _options.AlternativeAI);
        }

        /// <summary>
        /// Fills a radio/check slot: the theme box (<c>button_{kind}box_back</c>) with its mark (<c>button_{kind}_front</c>)
        /// and the text; the whole row takes the click. Returns the mark.
        /// </summary>
        internal static VisualElement Row(VisualElement page, G3MenuView view, string slotName, TextId text, float fontSize, string kind, Action clicked)
        {
            var slot = page.Q(slotName);
            if (slot == null) return null;
            var skin = G3Skin.Load();
            slot.pickingMode = PickingMode.Position;
            slot.style.flexDirection = FlexDirection.Row;
            slot.style.alignItems = Align.Center;
            var box = new VisualElement { pickingMode = PickingMode.Ignore, name = slotName + "_box" };
            box.style.width = box.style.height = BoxSize;
            box.style.flexShrink = 0;
            box.style.marginRight = 8;
            var back = skin?.Sprite($"theme:button_{kind}box_back") ?? skin?.Sprite($"theme:button_{kind}_back");
            if (back != null) box.style.backgroundImage = new StyleBackground(back);
            var mark = new VisualElement { pickingMode = PickingMode.Ignore, name = slotName + "_mark" };
            mark.style.position = Position.Absolute;
            mark.style.left = mark.style.top = mark.style.right = mark.style.bottom = 0;
            var front = skin?.Sprite($"theme:button_{kind}_front");
            if (front != null) mark.style.backgroundImage = new StyleBackground(front);
            else mark.style.backgroundColor = G3Palette.Label.ToColor();
            box.Add(mark);
            slot.Add(box);
            string labelName = slotName + "_text";
            var label = new Label { name = labelName, pickingMode = PickingMode.Ignore };
            G3Text.Font(label, fontSize, bold: true);
            label.style.color = skin != null ? skin.ThemeText("static_text", WidgetState.Normal, G3Palette.Label.ToColor()) : G3Palette.Label.ToColor();
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            slot.Add(label);
            view.Label(slot, labelName, text);
            slot.RegisterCallback<ClickEvent>(_ => clicked());
            G3PadFocus.MakeFocusable(slot, clicked); // gamepad-1: A picks the radio row
            return mark;
        }
    }
}
