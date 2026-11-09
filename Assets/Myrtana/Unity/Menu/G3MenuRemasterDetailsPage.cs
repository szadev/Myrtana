using System;
using System.Collections.Generic;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// opts-1 (Docs/controls-options.md §9): the Remaster page's second page, "World details" (<see cref="MenuPage.RemasterDetails"/>,
    /// the button right of Back on <see cref="G3MenuRemasterPage"/>; Back returns there). The Remaster page holds 13 rows
    /// (252..588, every row place of the Video layout above the hint), so the desert rows got their own page on the same
    /// layout, same columns and trackbars: Desert sand (Off / Simple / Scans), Sand wind and dust, Dunes, Desert atmosphere,
    /// Sand on buildings after storms and (when grade-1's json field exists) Saturation. Every row applies live and is
    /// stored (<see cref="G3DesertOptions"/>, <see cref="G3TerrainRelief.SetDunes"/>). The pad focus takes the trackbars
    /// and Back like on every page (gamepad-1).
    /// </summary>
    public sealed class G3MenuRemasterDetailsPage
    {
        const string Layout = "IDD_MENU_OPTIONS_VIDEO_2";

        static readonly string[] RowControls =
        {
            "VIDEO_RESOLUTION", "VIDEO_VSYNC", "VIDEO_BRIGHTNESS", "VIDEO_CONTRAST", "VIDEO_GAMMA_RED", "VIDEO_GAMMA_GREEN", "VIDEO_GAMMA_BLUE",
        };

        readonly VisualElement _page;
        readonly LanguageSettings _languages;
        readonly List<Action> _renderers = new List<Action>();
        bool _visible;

        public G3MenuRemasterDetailsPage(VisualElement frame, G3MenuView view, LanguageSettings languages)
        {
            _languages = languages;
            _page = G3MenuView.Page(frame, Layout);
            float top = G3MenuRemasterPage.FirstRowTop, pitch = Myrtana.Ui.Skin.VideoPortRowsLayout.Pitch;
            float Next() { float t = top; top += pitch; return t; }

            Row(Next(), "SAND", 2, () => (int)G3DesertOptions.Sand, i => G3DesertOptions.SetSand(DesertOptions.ToLook(i)),
                () => DesertOptions.SandName(Language), i => DesertOptions.SandValue(DesertOptions.ToLook(i), Ground, Language));
            OnOff(Next(), "SANDFX", () => G3DesertOptions.SandFx, G3DesertOptions.SetSandFx,
                () => DesertOptions.SandFxName(Language),
                on => DesertOptions.SandFxValue(on, Ground && G3DesertOptions.Sand != SandLook.Original, Language));
            OnOff(Next(), "DUNES", () => G3TerrainRelief.DunesOption, on => G3TerrainRelief.SetDunes(on),
                () => DesertOptions.DunesName(Language), on => DesertOptions.DunesValue(on, Ground && G3TerrainRelief.Option, Language));
            OnOff(Next(), "ATMO", () => G3DesertOptions.Atmo, G3DesertOptions.SetAtmo,
                () => DesertOptions.AtmoName(Language), on => DesertOptions.AtmoValue(on, Lighting, Language));
            OnOff(Next(), "ARCHSAND", () => G3DesertOptions.ArchSand, G3DesertOptions.SetArchSand,
                () => DesertOptions.ArchSandName(Language),
                on => DesertOptions.ArchSandValue(on, (G3WorldRemaster.Parts & WorldParts.Buildings) != 0, Language));
            if (DesertOptions.HasSaturation)   // grade-1's global saturation (json `saturation`); no row on a build without it
                Row(Next(), "SATURATION", DesertOptions.SaturationMax - DesertOptions.SaturationMin,
                    () => G3DesertOptions.SaturationSteps - DesertOptions.SaturationMin,
                    i => G3DesertOptions.SetSaturation(i + DesertOptions.SaturationMin),
                    () => DesertOptions.SaturationName(Language),
                    i => DesertOptions.SaturationValue(i + DesertOptions.SaturationMin, Lighting, Language));

            var hint = _page.Q<Label>("IDC_LABEL_VIDEO_RESET_WARNING_2");
            if (hint != null)
            {
                hint.style.whiteSpace = WhiteSpace.Normal;
                hint.style.unityTextAlign = UnityEngine.TextAnchor.MiddleCenter;
                _renderers.Add(() => hint.text = DesertOptions.Hint(Language));
            }
            view.Button(_page, "IDC_BUTTON_MENU_OPTIONS_VIDEO_BACK_2", G3MenuSettingsPages.BackText, () => view.Menu.State.Back());
            languages.Changed += () => { if (_visible) Render(); };
            G3WorldRemaster.PartsChanged += () => { if (_visible && _page?.panel != null) Render(); };   // F7 while open
            foreach (var c in RowControls)
                G3MenuView.Hide(_page, "IDC_LABEL_" + c + "_2", "IDC_SLIDER_" + c + "_2", "IDC_VALUE_" + c + "_2");
        }

        string Language => _languages?.TextLanguage ?? "English";
        static bool Ground => (G3WorldRemaster.Parts & WorldParts.Terrain) != 0;
        static bool Lighting => (G3WorldRemaster.Parts & WorldParts.Lighting) != 0;

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

        void OnOff(float top, string control, Func<bool> get, Action<bool> set, Func<string> name, Func<bool, string> valueText) =>
            Row(top, control, 1, () => get() ? 1 : 0, i => set(i == 1), name, i => valueText(i == 1));

        void Row(float top, string control, int max, Func<int> get, Action<int> set, Func<string> name, Func<int, string> valueText) =>
            _renderers.Add(G3MenuRemasterPage.ChoiceRow(_page, top, "IDC_SLIDER_REMASTER_" + control + "_2", max, get, set, name, valueText, Render));
    }
}
