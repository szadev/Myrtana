using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3's "Details" options: <c>IDD_MENU_OPTIONS_PERFORMANCE_2</c> with the radios Very High / High / Medium / Low
    /// (ge3.ini presets) and Custom (opens <c>IDD_MENU_OPTIONS_PERFORMANCE_CUSTOM_2</c>), whose sliders set each value; a
    /// slider change makes the preset Custom. Values: <see cref="PerformanceOptions"/>; what they do:
    /// <see cref="DisplayOptionsApplier"/>. The reset warnings are hidden (every change applies at once).
    /// </summary>
    public sealed class G3MenuPerformancePages
    {
        static readonly (string slot, DetailPreset preset, string text)[] Radios =
        {
            ("IDC_RADIOBUTTON_PERFORMANCE_VERYHIGH_2", DetailPreset.VeryHigh, "HUD_Performance_VeryHigh"),
            ("IDC_RADIOBUTTON_PERFORMANCE_HIGH_2", DetailPreset.High, "HUD_Performance_High"),
            ("IDC_RADIOBUTTON_PERFORMANCE_MEDIUM_2", DetailPreset.Medium, "HUD_Performance_Medium"),
            ("IDC_RADIOBUTTON_PERFORMANCE_LOW_2", DetailPreset.Low, "HUD_Performance_Low"),
            ("IDC_RADIOBUTTON_PERFORMANCE_CUSTOM_2", DetailPreset.Custom, "HUD_Performance_Custom"),
        };

        const string Off = "HUD_Slider_Value_Off", On = "HUD_Slider_Value_On", Low = "HUD_Slider_Value_Low", Medium = "HUD_Slider_Value_Medium",
                     High = "HUD_Slider_Value_High", VeryHigh = "HUD_Slider_Value_VeryHigh";

        /// <summary>The custom page's rows: layout control, setting, label string, value strings from the setting's minimum up.</summary>
        static readonly (string control, DetailSetting setting, string label, string[] values)[] Rows =
        {
            ("HIGH_DETAL_DISTANCE", DetailSetting.DistanceHigh, "HUD_Performance_HighDetalDistance", new[] { Low, Medium, High }),
            ("LOW_DETAL_DISTANCE", DetailSetting.DistanceLow, "HUD_Performance_LowDetalDistance", new[] { Off, Low, Medium, High }),
            ("OBJECT_DETAIL", DetailSetting.ObjectDetails, "HUD_Performance_ObjectDetail", new[] { Low, Medium, High, VeryHigh }),
            ("TEXTURE_QUALITY", DetailSetting.TextureQuality, "HUD_Performance_TextureQuality", new[] { Low, Medium, High }),
            ("TEXTURE_FILTERING", DetailSetting.TextureFilter, "HUD_Performance_TextureFiltering",
             new[] { "HUD_Slider_Value_Linear", "HUD_Slider_Value_Anisotropic", "HUD_Slider_Value_Anisotropic4", "HUD_Slider_Value_Anisotropic8",
                     "HUD_Slider_Value_Anisotropic16" }),
            ("VEGETATION_QUALITY", DetailSetting.VegetationQuality, "HUD_Performance_VegetationQuality", new[] { Off, Low, Medium, High }),
            ("VEGETATION_VIEW_RANGE", DetailSetting.VegetationViewRange, "HUD_Performance_VegetationViewRange", new[] { Low, Medium, High, VeryHigh }),
            ("SHADOWS_DETAIL", DetailSetting.ShadowQuality, "HUD_Performance_ShadowQuality", new[] { Off, Low, Medium, High, VeryHigh }),
            ("LENSFLARE", DetailSetting.LensFlare, "HUD_Performance_LensFlare", new[] { Off, On }),
            ("DEPTH_OF_FIELD", DetailSetting.DepthOfField, "HUD_Performance_DepthOfField", new[] { Off, On }),
            ("BLOOM", DetailSetting.Bloom, "HUD_Slider_Value_EffectBloom", new[] { Off, "HUD_Slider_Value_EffectBloom", "HUD_Slider_Value_EffectHDR" }),
            ("EDGEBLUR", DetailSetting.Antialiasing, "HUD_Performance_EdgeBlur", new[] { Off, "HUD_Slider_Value_FXAA" }),
            ("NOISE", DetailSetting.Noise, "HUD_Performance_Noise", new[] { Off, On }),
            ("FEEDBACK", DetailSetting.Feedback, "HUD_Performance_Feedback", new[] { Off, On }),
            ("COLORGRADING", DetailSetting.ColorGrading, "HUD_Performance_ColorGrading", new[] { Off, On }),
        };

        readonly G3MenuView _view;
        readonly PerformanceOptions _options;
        readonly VisualElement _presets, _custom;
        readonly VisualElement[] _marks = new VisualElement[Radios.Length];
        readonly List<Action> _renderers = new List<Action>();

        public G3MenuPerformancePages(VisualElement frame, G3MenuView view, PerformanceOptions options)
        {
            _view = view;
            _options = options;

            _presets = G3MenuView.Page(frame, "IDD_MENU_OPTIONS_PERFORMANCE_2");
            for (int i = 0; i < Radios.Length; i++)
            {
                var preset = Radios[i].preset;
                _marks[i] = G3MenuDifficultyPage.Row(_presets, view, Radios[i].slot, new TextId(Radios[i].text), 24, "radio", () =>
                {
                    options.SelectPreset(preset);
                    if (preset == DetailPreset.Custom) view.Menu.State.Show(MenuPage.PerformanceCustom);
                });
            }
            view.Button(_presets, "IDC_BUTTON_MENU_OPTIONS_PERFORMANCE_BACK_2", G3MenuSettingsPages.BackText, () => view.Menu.State.Back());

            _custom = G3MenuView.Page(frame, "IDD_MENU_OPTIONS_PERFORMANCE_CUSTOM_2");
            G3MenuView.Hide(_custom, "IDC_LABEL_PERFORMANCE_RESET_WARNING_2");
            foreach (var (control, setting, label, values) in Rows) AddRow(control, setting, label, values);
            view.Button(_custom, "IDC_BUTTON_MENU_OPTIONS_PERFORMANCE_CUSTOM_BACK_2", G3MenuSettingsPages.BackText, () => view.Menu.State.Back());

            options.Changed += Render;
        }

        public void Show(MenuPage page)
        {
            G3MenuView.Show(_presets, page == MenuPage.Performance);
            G3MenuView.Show(_custom, page == MenuPage.PerformanceCustom);
            if (page == MenuPage.Performance || page == MenuPage.PerformanceCustom) Render();
        }

        void AddRow(string control, DetailSetting setting, string label, string[] values)
        {
            string name = "IDC_" + "{0}" + "_PERFORMANCE_" + control + "_2";
            _view.Label(_custom, string.Format(name, "LABEL"), new TextId(label));
            int min = PerformanceOptions.Min(setting);
            var slider = G3MenuSettingsPages.Slider(_custom, string.Format(name, "SLIDER"), min, PerformanceOptions.Max(setting),
                                                    v => _options.Set(setting, v));
            // The colour grading value is named with a trailing blank in g3.gui.res ("IDC_VALUE_PERFORMANCE_COLORGRADING_2 ").
            var value = _custom.Q<Label>(string.Format(name, "VALUE")) ?? _custom.Q<Label>(string.Format(name, "VALUE") + " ") ??
                        G3MenuSettingsPages.ValueLabel(_custom, string.Format(name, "SLIDER"));
            _renderers.Add(() =>
            {
                int v = _options.Value(setting);
                slider?.SetValueWithoutNotify(v);
                if (value != null) value.text = _view.Text(new TextId(values[Math.Max(0, Math.Min(values.Length - 1, v - min))]));
            });
        }

        void Render()
        {
            if (_view.Localizer == null) return;
            for (int i = 0; i < Radios.Length; i++) G3MenuView.Show(_marks[i], _options.Preset == Radios[i].preset);
            foreach (var r in _renderers) r();
        }
    }
}
