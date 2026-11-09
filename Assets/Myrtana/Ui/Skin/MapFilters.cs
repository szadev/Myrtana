using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>What the map pages' filter bar switches (fix-126 [owner]; G3 has no map filter).</summary>
    public enum MapFilter
    {
        /// <summary>The port's locked-chest marks (fix-102/116; off by default).</summary>
        Chests,
        /// <summary>The map markers' place names (G3 shows them; off gives a clean map).</summary>
        Names,
        /// <summary>The companions' dots (fix-70; only while the option "Companions on map" is on).</summary>
        Companions,
        /// <summary>Where the hero talked to quest people (fix-126 [owner]).</summary>
        Quests,
    }

    /// <summary>
    /// The map pages' filter bar (fix-126 [owner]: "where is the chest filter button? can the place names be switched off
    /// too?"; Docs/map-teleport.md §2.4): a small row of G3 icon buttons inside the map's top-right corner on every map view
    /// (the documents page's map, the quest log's world map), on a dark plate so it reads on any parchment. A filter that
    /// does not apply to a view is not on its bar. Each state is kept per install (PlayerPrefs, <see cref="MapFilterState"/>).
    /// </summary>
    public static class MapFilterBarLayout
    {
        /// <summary>Button edge, gap between buttons, plate padding and the plate's inset from the map's corner (reference px).</summary>
        public const float ButtonSize = 24f, Gap = 3f, Padding = 3f, Inset = 6f;
        /// <summary>Icon opacity of a filter that is off (on: 1).</summary>
        public const float OffOpacity = 0.35f;
        /// <summary>Plate colour: G3's button brown #3C2819, α 0.7; 1.0 on hover.</summary>
        public const float PlateR = 0x3C / 255f, PlateG = 0x28 / 255f, PlateB = 0x19 / 255f, PlateAlpha = 0.7f;
        /// <summary>Caption under the bar while a button is hovered (the map page has no tooltips): font size.</summary>
        public const float CaptionSize = 13f;

        /// <summary>The bar's order, left to right.</summary>
        public static readonly IReadOnlyList<MapFilter> Order = new[] { MapFilter.Names, MapFilter.Quests, MapFilter.Companions, MapFilter.Chests };

        /// <summary>PlayerPrefs key (the chests keep fix-116's key, so a player's choice survives).</summary>
        public static string Key(MapFilter filter)
        {
            switch (filter)
            {
                case MapFilter.Chests: return "Myrtana.Map.LockedChests";
                case MapFilter.Names: return "Myrtana.Map.Names";
                case MapFilter.Companions: return "Myrtana.Map.Companions";
                default: return "Myrtana.Map.QuestMarks";
            }
        }

        /// <summary>On a new install: chests hidden ([owner] fix-116), names shown (as G3), companions and quest places shown.</summary>
        public static bool Default(MapFilter filter) => filter != MapFilter.Chests;

        /// <summary>The button's G3 icon.</summary>
        public static string Icon(MapFilter filter)
        {
            switch (filter)
            {
                case MapFilter.Chests: return "IT_Chest_Normal.tga";
                case MapFilter.Names: return "G3_HUD_Icon_Document_Letter.tga";
                case MapFilter.Companions: return "G3_HUD_Icon_Page_Character.tga";
                default: return "G3_HUD_Icon_Location_Main.tga";
            }
        }

        /// <summary>The caption's text id (patched strings, <c>Myrtana.Core.PatchStrings</c>).</summary>
        public static string TextId(MapFilter filter) => "MYR_MapFilter_" + filter;

        /// <summary>The bar's width for <paramref name="buttons"/> buttons (reference px).</summary>
        public static float Width(int buttons) => buttons <= 0 ? 0f : 2f * Padding + buttons * ButtonSize + (buttons - 1) * Gap;
    }

    /// <summary>
    /// The filters' states over a key/value store (PlayerPrefs in the game, a dictionary in tests): read once, written on
    /// every change; <see cref="Changed"/> tells the bars and the map layers.
    /// </summary>
    public sealed class MapFilterState
    {
        readonly Func<string, int, int> _read;
        readonly Action<string, int> _write;
        readonly Dictionary<MapFilter, bool> _cache = new Dictionary<MapFilter, bool>();

        public MapFilterState(Func<string, int, int> read, Action<string, int> write)
        {
            _read = read;
            _write = write;
        }

        public event Action<MapFilter> Changed;

        public bool IsOn(MapFilter filter)
        {
            if (_cache.TryGetValue(filter, out bool on)) return on;
            bool fallback = MapFilterBarLayout.Default(filter);
            try { on = _read == null ? fallback : _read(MapFilterBarLayout.Key(filter), fallback ? 1 : 0) != 0; }
            catch (Exception) { on = fallback; }
            _cache[filter] = on;
            return on;
        }

        public void Set(MapFilter filter, bool on)
        {
            if (IsOn(filter) == on) return;
            _cache[filter] = on;
            try { _write?.Invoke(MapFilterBarLayout.Key(filter), on ? 1 : 0); }
            catch (Exception) { }
            Changed?.Invoke(filter);
        }

        /// <summary>The button's click: the filter flips; returns the new state.</summary>
        public bool Toggle(MapFilter filter)
        {
            Set(filter, !IsOn(filter));
            return IsOn(filter);
        }
    }
}
