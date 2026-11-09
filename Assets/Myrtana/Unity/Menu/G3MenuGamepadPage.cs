using System;
using System.Collections.Generic;
using G3.Player;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// gamepad-1 (Docs/gamepad.md §5): the port's Gamepad page (no G3 page; reached by the "Gamepad" button on the Controls
    /// page, <see cref="MenuPage.Gamepad"/>), built on the Video layout like <see cref="G3MenuRemasterPage"/>: camera
    /// sensitivity X/Y and invert X/Y (the mouse rows' scales), stick dead zone, trigger press point, vibration, button
    /// layout Standard/Custom (Standard restores the default table), the "Gamepad buttons" list
    /// (<see cref="MenuPage.GamepadCustom"/>) and Back. The page's hint line names the connected pad. Changes apply and are
    /// stored at once (<see cref="OptionsPreferences"/>). Texts in Russian or English by the text language.
    /// </summary>
    public sealed class G3MenuGamepadPage
    {
        const string Layout = "IDD_MENU_OPTIONS_VIDEO_2";
        public const string ButtonsButtonName = "IDC_BUTTON_MENU_OPTIONS_GAMEPAD_BUTTONS_2";

        static readonly string[] RowControls =
        {
            "VIDEO_RESOLUTION", "VIDEO_VSYNC", "VIDEO_BRIGHTNESS", "VIDEO_CONTRAST", "VIDEO_GAMMA_RED", "VIDEO_GAMMA_GREEN", "VIDEO_GAMMA_BLUE",
        };

        readonly VisualElement _page;
        readonly G3MenuView _view;
        readonly PadOptions _pad;
        readonly LanguageSettings _languages;
        readonly List<Action> _renderers = new List<Action>();
        bool _visible;

        public G3MenuGamepadPage(VisualElement frame, G3MenuView view, ControlOptions controls, LanguageSettings languages)
        {
            _view = view;
            _pad = controls.Pad;
            _languages = languages;
            _page = G3MenuView.Page(frame, Layout);
            float top = G3MenuRemasterPage.FirstRowTop, pitch = Myrtana.Ui.Skin.VideoPortRowsLayout.Pitch;
            float Next() { float t = top; top += pitch; return t; }
            AddRow(Next(), "SENS_X", ControlOptions.SliderMin, ControlOptions.SliderMax, () => ControlOptions.ToSlider(_pad.SensitivityX),
                   p => _pad.SensitivityX = ControlOptions.FromSlider(p), () => T("Camera sensitivity X", "Чувствительность камеры X"),
                   VideoOptions.SliderText);
            AddRow(Next(), "SENS_Y", ControlOptions.SliderMin, ControlOptions.SliderMax, () => ControlOptions.ToSlider(_pad.SensitivityY),
                   p => _pad.SensitivityY = ControlOptions.FromSlider(p), () => T("Camera sensitivity Y", "Чувствительность камеры Y"),
                   VideoOptions.SliderText);
            AddOnOff(Next(), "INVERT_X", () => _pad.InvertX, on => _pad.InvertX = on, () => T("Invert X axis", "Инвертировать ось X"));
            AddOnOff(Next(), "INVERT_Y", () => _pad.InvertY, on => _pad.InvertY = on, () => T("Invert Y axis", "Инвертировать ось Y"));
            AddRow(Next(), "DEADZONE", 0, PadOptions.Steps, () => PadOptions.ToStep(_pad.DeadZone, PadOptions.DeadZoneMin, PadOptions.DeadZoneMax),
                   i => _pad.DeadZone = PadOptions.FromStep(i, PadOptions.DeadZoneMin, PadOptions.DeadZoneMax),
                   () => T("Stick dead zone", "Мёртвая зона стиков"),
                   i => Percent(PadOptions.FromStep(i, PadOptions.DeadZoneMin, PadOptions.DeadZoneMax)));
            AddRow(Next(), "TRIGGER", 0, PadOptions.Steps, () => PadOptions.ToStep(_pad.TriggerThreshold, PadOptions.TriggerMin, PadOptions.TriggerMax),
                   i => _pad.TriggerThreshold = PadOptions.FromStep(i, PadOptions.TriggerMin, PadOptions.TriggerMax),
                   () => T("Trigger press point", "Порог курков"),
                   i => Percent(PadOptions.FromStep(i, PadOptions.TriggerMin, PadOptions.TriggerMax)));
            AddOnOff(Next(), "VIBRATION", () => _pad.Vibration, on => _pad.Vibration = on, () => T("Vibration on hits", "Вибрация при ударах"));
            AddRow(Next(), "LAYOUT", 0, 1, () => _pad.Layout == ControlOptions.Custom ? 1 : 0,
                   i => { if (i == 0) _pad.UseStandardButtons(); else _pad.Layout = ControlOptions.Custom; },
                   () => T("Button layout", "Раскладка кнопок"),
                   i => i == 0 ? T("Standard", "Стандартная") : T("Custom", "Своя"));

            var buttons = new G3Button(T("Gamepad buttons", "Кнопки геймпада"), () => view.Menu.State.Show(MenuPage.GamepadCustom))
            {
                name = ButtonsButtonName,
            };
            G3Text.Font(buttons, G3FontMap.ThemeButton);
            buttons.style.position = Position.Absolute;
            buttons.style.left = 386;
            buttons.style.top = top + 12;
            buttons.style.width = 252;
            buttons.style.height = 32;
            _page.Add(buttons);
            _renderers.Add(() => buttons.text = T("Gamepad buttons", "Кнопки геймпада"));

            var hint = _page.Q<Label>("IDC_LABEL_VIDEO_RESET_WARNING_2");
            if (hint != null)
            {
                hint.style.whiteSpace = WhiteSpace.Normal;
                hint.style.unityTextAlign = TextAnchor.MiddleCenter;
                _renderers.Add(() => hint.text = Status());
            }
            view.Button(_page, "IDC_BUTTON_MENU_OPTIONS_VIDEO_BACK_2", G3MenuSettingsPages.BackText, () => view.Menu.State.Back());
            foreach (var c in RowControls)
                G3MenuView.Hide(_page, "IDC_LABEL_" + c + "_2", "IDC_SLIDER_" + c + "_2", "IDC_VALUE_" + c + "_2");
            _pad.Changed += () => { if (_visible) Render(); };
            languages.Changed += () => { if (_visible) Render(); };
        }

        string Language => _languages?.TextLanguage ?? "English";
        bool Russian => Language == "Russian";
        string T(string en, string ru) => Russian ? ru : en;

        static string Percent(float v) => Mathf.RoundToInt(v * 100f) + "%";

        string Status()
        {
            if (!G3Pad.Connected) return T("No gamepad connected", "Геймпад не подключён");
            string family;
            switch (G3Pad.Family)
            {
                case PadFamily.PlayStation: family = "PlayStation"; break;
                case PadFamily.Nintendo: family = "Nintendo Switch"; break;
                case PadFamily.Generic: family = T("generic joystick", "обычный джойстик"); break;
                default: family = "Xbox / XInput"; break;
            }
            return T("Connected: ", "Подключён: ") + family;
        }

        public void Show(bool visible)
        {
            _visible = visible;
            G3MenuView.Show(_page, visible);
            if (visible) Render();
        }

        public void Render()
        {
            foreach (var r in _renderers) r();
        }

        void AddOnOff(float top, string control, Func<bool> get, Action<bool> set, Func<string> name) =>
            AddRow(top, control, 0, 1, () => get() ? 1 : 0, i => set(i == 1), name,
                   i => _view.Text(i == 1 ? G3MenuSettingsPages.OnText : G3MenuSettingsPages.OffText));

        void AddRow(float top, string control, int min, int max, Func<int> get, Action<int> set, Func<string> name, Func<int, string> valueText)
        {
            string slotName = "IDC_SLIDER_GAMEPAD_" + control + "_2";
            var model = _page.Q<Label>("IDC_LABEL_VIDEO_RESOLUTION_2");
            var label = G3MenuSettingsPages.PlacedLabel(_page, 20f, top, 364f);
            if (model != null)
            {
                label.style.left = model.style.left;
                label.style.right = model.style.right;
                label.style.width = model.style.width;
                label.style.marginLeft = model.style.marginLeft;
                label.style.unityTextAlign = model.style.unityTextAlign;
            }
            var slot = new VisualElement { name = slotName, pickingMode = PickingMode.Position };
            slot.style.position = Position.Absolute;
            slot.style.left = 408f;
            slot.style.top = top + 4;
            slot.style.width = 200;
            slot.style.height = 25;
            _page.Add(slot);
            var slider = G3MenuSettingsPages.Slider(_page, slotName, min, max, i => { set(i); Render(); });
            var value = G3MenuSettingsPages.PlacedLabel(_page, 632f, top, 348);
            _renderers.Add(() =>
            {
                int v = get();
                label.text = name();
                slider?.SetValueWithoutNotify(v);
                value.text = valueText(v);
            });
        }
    }
}
