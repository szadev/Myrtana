using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// The quest log's location filter (<c>IDD_VIEW_WORLD_2</c>, gCHUDViewWorld): 25 18×18 icon buttons on the map of
    /// Myrtana — 23 places, Main (the main story) and World (everything). A quest belongs to a place when its G3 editor
    /// folder (<c>.quest</c> Folder, e.g. "Myrtana/Silden_City/"), lower-cased, contains the place's key; World shows all.
    /// The list shows the icon of the first place a quest belongs to. Picking a place shows its name
    /// (<c>GO_Enclave_*</c>) and the hero's fame with that enclave in the location box; Main and World show no fame.
    /// fix-223: Main and World sit in the map's scroll cartouche with captions (<see cref="CaptionControl"/>); a map
    /// place's name shows beside its icon while hovered or picked (<see cref="NamedOnMap"/>).
    /// </summary>
    public static class QuestLocations
    {
        public sealed class Location
        {
            internal Location(string control, string key, string icon, string name, string enclave)
            {
                Control = control;
                Key = key;
                Icon = icon;
                Name = new TextId(name);
                Enclave = enclave;
            }

            /// <summary>The button of <c>IDD_VIEW_WORLD_2</c>.</summary>
            public string Control { get; }
            /// <summary>Lower-case part of the quest folder ("faring"); null for World (all quests).</summary>
            public string Key { get; }
            /// <summary>G3_HUD_Icon_Location_&lt;Icon&gt;.tga.</summary>
            public string Icon { get; }
            public TextId Name { get; }
            /// <summary>The enclave whose fame the location box shows, or null.</summary>
            public string Enclave { get; }
            public bool IsAll => Key == null;
            public string IconImage => "G3_HUD_Icon_Location_" + Icon + ".tga";
        }

        static Location L(string control, string key, string icon, string enclave) =>
            new Location("IDC_BUTTON_LOCATION_" + control, key, icon, "GO_Enclave_" + enclave, enclave);

        /// <summary>G3's order (gCHUDViewWorld): the 23 places, then Main and World.</summary>
        public static readonly IReadOnlyList<Location> All = new[]
        {
            L("ARDEA", "ardea", "Ardea", "Ardea"),
            L("BAKARESH", "bakaresh", "Bakaresh", "Bakaresh"),
            L("BEN_ERAI", "benerai", "BenErai", "BenErai"),
            L("BEN_SALA", "bensala", "BenSala", "BenSala"),
            L("BRAGA", "braga", "Braga", "Braga"),
            L("FARING", "faring", "Faring", "Faring"),
            L("FIRE_CLAN", "fireclan", "Fireclan", "FireClan"),
            L("GELDERN", "geldern", "Geldern", "Geldern"),
            L("GOTHA", "gotha", "Gotha", "Gotha"),
            L("HAMMER_CLAN", "hammerclan", "Hammerclan", "HammerClan"),
            L("INNOS_MONASTERY", "innos", "InnosMonastery", "Monastery"),
            L("ISHTAR", "ishtar", "Ishtar", "Ishtar"),
            L("KAP_DUN", "kapdun", "KapDun", "KapDun"),
            L("LAGO", "lago", "Lago", "Lago"),
            L("MONTERA", "montera", "Montera", "Montera"),
            L("MORA_SUL", "morasul", "MoraSul", "MoraSul"),
            L("NEMORA", "nemora", "Nemora", "Nemora"),
            L("OKARA", "okara", "Okara", "Okara"),
            L("REDDOCK", "reddock", "Reddock", "Reddock"),
            L("SILDEN", "silden", "Silden", "Silden"),
            L("TRELIS", "trelis", "Trelis", "Trelis"),
            L("VANGARD", "vangard", "Vangard", "Vangard"),
            L("WOLF_CLAN", "wolfclan", "Wolfclan", "WolfClan"),
            new Location("IDC_BUTTON_LOCATION_MAIN", "haupt", "Main", "HUD_Main", null),
            new Location("IDC_BUTTON_LOCATION_WORLD", null, "World", "HUD_All", null),
        };

        /// <summary>World: every quest (the filter's start).</summary>
        public static int World => All.Count - 1;

        /// <summary>Main: the main story's quests.</summary>
        public static int Main => All.Count - 2;

        /// <summary>
        /// fix-223 [owner]: the caption of a cartouche entry — G3's map has a scroll cartouche (bottom right of
        /// <c>G3_HUD_View_World_Back</c>) holding the Main and World icons with the captions <c>IDC_LABEL_LOCATION_MAIN_2</c>
        /// (<c>HUD_Main</c>) and <c>IDC_LABEL_LOCATION_WORLD_2</c> (<c>HUD_All</c>) — or null for a map place.
        /// </summary>
        public static string CaptionControl(int index) =>
            index == Main ? "IDC_LABEL_LOCATION_MAIN_2" : index == World ? "IDC_LABEL_LOCATION_WORLD_2" : null;

        /// <summary>
        /// fix-223 [owner]: the map place named beside its icon — the hovered place, else the picked one — or −1 (the
        /// cartouche entries have their own captions).
        /// </summary>
        public static int NamedOnMap(int picked, int hovered)
        {
            if (IsMapPlace(hovered)) return hovered;
            return IsMapPlace(picked) ? picked : -1;
        }

        static bool IsMapPlace(int index) => index >= 0 && index < All.Count && CaptionControl(index) == null;

        /// <summary>The quest with editor folder <paramref name="folder"/> is listed under location <paramref name="index"/>.</summary>
        public static bool Matches(int index, string folder)
        {
            if (index < 0 || index >= All.Count) return true;
            var location = All[index];
            if (location.IsAll) return true;
            return !string.IsNullOrEmpty(folder) && folder.ToLowerInvariant().Contains(location.Key);
        }

        /// <summary>The first place (not World) the quest belongs to, or −1.</summary>
        public static int FirstOf(string folder)
        {
            for (int i = 0; i < All.Count; i++)
                if (!All[i].IsAll && Matches(i, folder)) return i;
            return -1;
        }

        /// <summary>The fame group of a location's enclave as the content names it (case-insensitive; Lago is "Enclave_Lago").</summary>
        public static string FameGroup(Location location, IEnumerable<string> groups)
        {
            if (location?.Enclave == null) return null;
            foreach (var g in groups)
                if (string.Equals(g, location.Enclave, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(g, "Enclave_" + location.Enclave, StringComparison.OrdinalIgnoreCase)) return g;
            return location.Enclave;
        }

        /// <summary>The world map item whose markers place the quest log's map (fix-126).</summary>
        public const string WorldMapItem = "It_Map_World";

        /// <summary>
        /// fix-126: the world map's marker of a place — a visible town marker (not a minor "$" one) whose label names the
        /// place's key ("MAP_World_Myrtana_Faring_01" for faring) — or null (Main, World, the monastery's "innos").
        /// </summary>
        public static MapMarkerDef MarkerOf(Location location, MapDef world)
        {
            if (location?.Key == null || world == null) return null;
            foreach (var m in world.VisibleMarkers)
            {
                string label = m.Label.Value;
                if (string.IsNullOrEmpty(label) || label.Contains("$")) continue;
                if (label.ToLowerInvariant().Contains(location.Key)) return m;
            }
            return null;
        }

        /// <summary>
        /// fix-126: the quest log map's projection (its picture has no stored world extent): the place buttons' centres
        /// (<paramref name="buttonCentre"/>, map view px; null for a missing button) against the world map's town markers,
        /// fitted by <see cref="MapProjection.TryFit"/>. False when fewer than two places match.
        /// </summary>
        public static bool TryProjection(MapDef world, Func<string, (float x, float y)?> buttonCentre, float width, float height,
                                         out MapProjection projection)
        {
            var pairs = new List<(float px, float py, float x, float z)>();
            foreach (var location in All)
            {
                var marker = MarkerOf(location, world);
                var centre = marker != null ? buttonCentre?.Invoke(location.Control) : null;
                if (centre != null) pairs.Add((centre.Value.x, centre.Value.y, marker.X, marker.Z));
            }
            return MapProjection.TryFit(pairs, width, height, out projection);
        }
    }
}
