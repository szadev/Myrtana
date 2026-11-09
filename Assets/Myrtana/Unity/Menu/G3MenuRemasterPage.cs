using System;
using System.Collections.Generic;
using G3.Unity;
using Myrtana.Unity.Ui.Components;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The port's Options → Remaster page (Docs/build.md §3; G3 has no such page): the video page's layout
    /// (<c>IDD_MENU_OPTIONS_VIDEO_2</c>: same background, trackbars, fonts and Back) with the remaster's switches, each
    /// sub-switch under its part (fix-171, <see cref="FirstRowTop"/>): the six world parts — Lighting and atmosphere,
    /// Ground, Grass and plants, Rocks and mountains, Buildings and props, Trees (<see cref="G3WorldRemaster.SetPart"/>;
    /// F7 flips them all, <see cref="RemasterHotkeys.Set"/>; an open page re-reads them via
    /// <see cref="G3WorldRemaster.PartsChanged"/>), Lighting quality (<see cref="G3WorldRemasterLook.SetQuality"/>), Modern
    /// lighting, Terrain relief (<see cref="G3TerrainRelief.SetOption"/>), Snow and footprints, Characters
    /// (<see cref="G3CharacterRemaster.SetEnabled"/>), and a hint in the reset-warning place. Every row applies live and is
    /// stored; texts in the text language (<see cref="RemasterOptions"/>), Russian in the Gothic3-style Cyrillic like every page.
    /// </summary>
    public sealed class G3MenuRemasterPage
    {
        const string Layout = "IDD_MENU_OPTIONS_VIDEO_2";

        /// <summary>Row places of the video layout used here, top to bottom (all seven, 28 px apart: 420..588).</summary>
        static readonly string[] RowControls =
        {
            "VIDEO_RESOLUTION", "VIDEO_VSYNC", "VIDEO_BRIGHTNESS", "VIDEO_CONTRAST", "VIDEO_GAMMA_RED", "VIDEO_GAMMA_GREEN", "VIDEO_GAMMA_BLUE",
        };

        readonly VisualElement _page;
        readonly LanguageSettings _languages;
        readonly List<Action> _renderers = new List<Action>();
        bool _visible;

        /// <summary>
        /// fix-171 [owner]: the rows top to bottom, each sub-switch under its part (28 px apart from 280 to 588, the free place
        /// above the Video layout's Resolution row is used as on the Video page): Lighting and atmosphere, Lighting quality,
        /// Modern lighting, Ground, Terrain relief, Snow and footprints, Persistent tracks (fix-178), Grass and plants, Rocks
        /// and mountains, Buildings and props, Trees, Characters. G3's seven row controls are hidden; every row is placed like
        /// the Snow / Modern rows were.
        /// </summary>
        public const float FirstRowTop = Myrtana.Ui.Skin.VideoPortRowsLayout.ContinueButtonTop;   // 280 (12 rows, last at 588)

        /// <summary>The number of rows (<see cref="FirstRowTop"/> + (RowCount - 1) * Pitch = 588, G3's last row place).</summary>
        public const int RowCount = 12;

        /// <summary>weather-opt: the "Weather" row (Original / Fair, <see cref="G3WeatherDirector.SetModeOption"/>), one row
        /// above <see cref="FirstRowTop"/> (252, free on this page): not a world part, F7 leaves it.</summary>
        /// <summary>opts-1: the "World details" button (<see cref="MenuPage.RemasterDetails"/>, <see cref="G3MenuRemasterDetailsPage"/>).</summary>
        public const string DetailsButtonName = "IDC_BUTTON_MENU_OPTIONS_REMASTER_DETAILS_2";

        public const float WeatherRowTop = FirstRowTop - Myrtana.Ui.Skin.VideoPortRowsLayout.Pitch;

        public G3MenuRemasterPage(VisualElement frame, G3MenuView view, LanguageSettings languages)
        {
            _languages = languages;
            _page = G3MenuView.Page(frame, Layout);
            float top = FirstRowTop, pitch = Myrtana.Ui.Skin.VideoPortRowsLayout.Pitch;
            float Next() { float t = top; top += pitch; return t; }
            AddChoiceRow(WeatherRowTop, "IDC_SLIDER_REMASTER_WEATHER_2", 1, () => G3WeatherDirector.ModeOption,
                m => G3WeatherDirector.SetModeOption(m), () => RemasterOptions.WeatherName(Language),
                m => RemasterOptions.WeatherValue(m, Language));
            AddPartRow(Next(), RemasterFeature.Lighting);
            AddChoiceRow(Next(), "IDC_SLIDER_REMASTER_LIGHTQ_2", 3, () => (int)G3WorldRemasterLook.Quality,
                i => G3WorldRemasterLook.SetQuality((LightingQuality)i), () => LightingQualityLevels.RowName(Language),
                i => LightingQualityLevels.Value((LightingQuality)i, Language) +
                     ((G3WorldRemaster.Parts & WorldParts.Lighting) == 0 ? (Language == "Russian" ? " (с Освещением)" : " (with Lighting)") : ""));
            // lighting-new: three values, Off / Soft / New (Docs/remaster-world.md section 21)
            AddChoiceRow(Next(), "IDC_SLIDER_REMASTER_MODERN_2", 2, () => G3WorldGi.ModernMode, m => G3WorldGi.SetModernMode(m),
                () => RemasterOptions.ModernName(Language),
                m => RemasterOptions.ModernModeValue(m, (G3WorldRemaster.Parts & WorldParts.Lighting) != 0, Language,
                    WorldRemasterLook.Current.v2GiStrength > 0));   // Medium / Low quality: GI off (fix-171)
            AddPartRow(Next(), RemasterFeature.Terrain);
            AddExtraRow(Next(), "IDC_SLIDER_REMASTER_RELIEF_2", () => G3TerrainRelief.Option, on => G3TerrainRelief.SetOption(on),
                () => RemasterOptions.ReliefName(Language),
                on => RemasterOptions.ReliefValue(on, (G3WorldRemaster.Parts & WorldParts.Terrain) != 0, Language));
            AddExtraRow(Next(), "IDC_SLIDER_REMASTER_SNOW_2", () => G3SnowV2.Option, on => G3SnowV2.SetOption(on),
                () => RemasterOptions.RowName(RemasterFeature.Snow, Language),
                on => RemasterOptions.SnowValue(on, (G3WorldRemaster.Parts & WorldParts.Surfaces) != 0, Language));
            // fix-178: snow prints stay until snowfall covers them (find animals by their trails), a sub-switch under Snow
            AddExtraRow(Next(), "IDC_SLIDER_REMASTER_TRACKS_2", () => G3SnowV2.TracksOption, on => G3SnowV2.SetTracksOption(on),
                () => RemasterOptions.TracksName(Language),
                on => RemasterOptions.TracksValue(on, G3SnowV2.Option && (G3WorldRemaster.Parts & WorldParts.Surfaces) != 0, Language));
            foreach (var f in new[] { RemasterFeature.Grass, RemasterFeature.Rocks, RemasterFeature.Buildings, RemasterFeature.Trees, RemasterFeature.Characters })
                AddPartRow(Next(), f);

            var hint = _page.Q<Label>("IDC_LABEL_VIDEO_RESET_WARNING_2");
            if (hint != null)
            {
                hint.style.whiteSpace = WhiteSpace.Normal;
                hint.style.unityTextAlign = TextAnchor.MiddleCenter;
                _renderers.Add(() => hint.text = RemasterOptions.Hint(Language));
            }
            view.Button(_page, "IDC_BUTTON_MENU_OPTIONS_VIDEO_BACK_2", G3MenuSettingsPages.BackText, () => view.Menu.State.Back());
            // opts-1: the desert / colour rows did not fit (13 rows, 252..588): their own page, "World details", right of Back
            var details = BesideBack(_page, DetailsButtonName, DesertOptions.PageName(Language), () => view.Menu.State.Show(MenuPage.RemasterDetails));
            _renderers.Add(() => details.text = DesertOptions.PageName(Language));
            languages.Changed += () => { if (_visible) Render(); };
            G3WorldRemaster.PartsChanged += OnPartsChanged;   // F7 while the page is open
            foreach (var c in RowControls)   // G3's row places: every row is placed by the page now
                G3MenuView.Hide(_page, "IDC_LABEL_" + c + "_2", "IDC_SLIDER_" + c + "_2", "IDC_VALUE_" + c + "_2");
        }

        void AddPartRow(float top, RemasterFeature feature) =>
            AddExtraRow(top, "IDC_SLIDER_REMASTER_" + feature.ToString().ToUpperInvariant() + "_2", () => Get(feature), on => Set(feature, on),
                () => RemasterOptions.RowName(feature, Language), on => RemasterOptions.RowValue(feature, on, Available(feature), Language));

        string Language => _languages?.TextLanguage ?? "English";

        public void Show(bool visible)
        {
            _visible = visible;
            G3MenuView.Show(_page, visible);
            if (visible) Render();
        }

        /// <summary>Re-reads the rows when the parts change elsewhere (F7, <see cref="RemasterHotkeys.Set"/>) while the page
        /// is shown; hidden or detached pages re-read on <see cref="Show"/>.</summary>
        void OnPartsChanged()
        {
            if (_visible && _page?.panel != null) Render();
        }

        public void Render()
        {
            foreach (var r in _renderers) r();
        }

        /// <summary>
        /// A row above the seven, same columns and look as the page's own rows (<see cref="G3MenuPortRows"/> style), in the
        /// free rows of the video layout: remaster-snow "Snow and footprints" at 392 (where the Video page has Field of
        /// view), remaster-gi2 "Modern lighting" at 364 (Upscaling there); fix-171 places every row from <see cref="FirstRowTop"/>.
        /// </summary>
        void AddExtraRow(float top, string slotName, Func<bool> get, Action<bool> set, Func<string> name, Func<bool, string> valueText) =>
            AddChoiceRow(top, slotName, 1, () => get() ? 1 : 0, i => set(i == 1), name, i => valueText(i == 1));

        /// <summary>A row with <paramref name="max"/> + 1 positions (0..max), same columns and look as the on/off rows.</summary>
        void AddChoiceRow(float top, string slotName, int max, Func<int> get, Action<int> set, Func<string> name, Func<int, string> valueText) =>
            _renderers.Add(ChoiceRow(_page, top, slotName, max, get, set, name, valueText, Render));

        /// <summary>
        /// opts-1: one row on a Video-layout page (label in the Resolution row's column, theme trackbar 408-608, value at
        /// 632) with <paramref name="max"/> + 1 positions; a move sets the value and calls <paramref name="render"/>.
        /// Returns the row's renderer (also used by <see cref="G3MenuRemasterDetailsPage"/>).
        /// </summary>
        internal static Action ChoiceRow(VisualElement page, float top, string slotName, int max, Func<int> get, Action<int> set,
                                         Func<string> name, Func<int, string> valueText, Action render)
        {
            var model = page.Q<Label>("IDC_LABEL_VIDEO_RESOLUTION_2");
            var label = G3MenuSettingsPages.PlacedLabel(page, 20f, top, 364f);
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
            page.Add(slot);
            var slider = G3MenuSettingsPages.Slider(page, slotName, 0, max, i => { set(i); render?.Invoke(); });
            var value = G3MenuSettingsPages.PlacedLabel(page, 632f, top, 348);
            return () =>
            {
                int v = get();
                label.text = name();
                slider?.SetValueWithoutNotify(v);
                value.text = valueText(v);
            };
        }

        /// <summary>
        /// opts-1: a port button right of the layout's Back on its row (like gamepad-1's "Gamepad" on the Controls page;
        /// Back 462-562 × 682-714 when the layout's own place cannot be read). The pad focus takes it like any button.
        /// </summary>
        internal static G3Button BesideBack(VisualElement page, string name, string text, Action clicked)
        {
            var back = page.Q<G3Button>("IDC_BUTTON_MENU_OPTIONS_VIDEO_BACK_2");
            float left = 462, top = 682, width = 100;
            if (back != null)
            {
                if (back.style.left.keyword == StyleKeyword.Undefined && back.style.left.value.unit == LengthUnit.Pixel) left = back.style.left.value.value;
                if (back.style.top.keyword == StyleKeyword.Undefined && back.style.top.value.unit == LengthUnit.Pixel && back.style.top.value.value > 0) top = back.style.top.value.value;
                if (back.style.width.keyword == StyleKeyword.Undefined && back.style.width.value.unit == LengthUnit.Pixel && back.style.width.value.value > 0) width = back.style.width.value.value;
            }
            var b = new G3Button(text, clicked) { name = name };
            G3Text.Font(b, G3FontMap.ThemeButton);
            b.style.position = Position.Absolute;
            b.style.left = left + width + 28;
            b.style.top = top;
            b.style.width = 200;
            b.style.height = 32;
            page.Add(b);
            return b;
        }

        static bool Get(RemasterFeature f) =>
            RemasterOptions.IsWorld(f) ? G3WorldRemaster.IsOn(f) : G3CharacterRemaster.Enabled;

        static void Set(RemasterFeature f, bool on)
        {
            if (RemasterOptions.IsWorld(f)) G3WorldRemaster.SetPart(f, on, persist: true);
            else G3CharacterRemaster.SetEnabled(on);
        }

        static bool Available(RemasterFeature f) =>
            RemasterOptions.IsWorld(f) ? G3WorldRemaster.Available(f) : G3CharacterRemaster.Available;
    }
}
