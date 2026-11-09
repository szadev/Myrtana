#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using Myrtana.Ui.Skin;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-126 ops and probes (Docs/dev-scenarios.md): the map views' filter bar (<see cref="MapFilterBar"/>), the place
    /// names' declutter and the quest talk marks on the documents map and the quest log's world map.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        readonly Dictionary<MapFilter, bool> _mapFiltersBefore = new Dictionary<MapFilter, bool>(); // restored at the end

        /// <summary>'chests' | 'names' | 'companions' | 'quests' (case-insensitive), or null.</summary>
        static MapFilter? ParseMapFilter(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (MapFilter f in Enum.GetValues(typeof(MapFilter)))
                if (string.Equals(f.ToString(), name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>
        /// 'mapfilter name=chests|names|companions|quests on=0|1': one map filter for this scenario (the stored value comes back
        /// at its end). With a map view open (documents map, quest log) it clicks the bar's button (the player's path).
        /// </summary>
        internal void SetMapFilter(MapFilter filter, bool on)
        {
            if (!_mapFiltersBefore.ContainsKey(filter)) _mapFiltersBefore[filter] = MapFilters.IsOn(filter);
            if (MapFilters.IsOn(filter) != on)
            {
                var current = FindAnyObjectByType<GameScreens>()?.Current;
                if (current is G3MapScreen map) map.ClickFilter(filter);
                else if (current is G3QuestLogScreen quests) quests.ClickFilter(filter);
                else MapFilters.Set(filter, on);
            }
            Log($"mapfilter {filter.ToString().ToLowerInvariant()} {(on ? "on" : "off")}");
        }

        void RestoreMapFilters()
        {
            foreach (var kv in _mapFiltersBefore) MapFilters.Set(kv.Key, kv.Value);
            _mapFiltersBefore.Clear();
        }

        /// <summary>'questlog': opens the quest log page (the L key's page) on the Running tab, all places.</summary>
        System.Collections.IEnumerator OpenQuestLog()
        {
            var screens = FindAnyObjectByType<GameScreens>();
            if (screens == null || !(screens.ByKey(UnityEngine.InputSystem.Key.L) is G3QuestLogScreen page))
            {
                Fail("questlog: no quest log page");
                yield break;
            }
            if (screens.Current != page) screens.Open(page);
            yield return new UnityEngine.WaitForSeconds(0.3f);
            Log($"questlog open: map fitted {page.MapFitted}, quest marks {page.QuestTalksShown}");
        }

        static bool IsFix126Probe(string kind) => kind == "mapfilter" || kind == "mapnames" || kind == "mapnamesnudged" || kind == "mapnamesoverlap"
                                                  || kind == "mapquestmarks" || kind == "mapquestselected" || kind == "mapfit"
                                                  || kind == "questtalks" || kind == "questtalk" || kind == "mapcompanions";

        string Fix126Probe(string kind, string arg)
        {
            var current = FindAnyObjectByType<GameScreens>()?.Current;
            var map = current as G3MapScreen;
            var quests = current as G3QuestLogScreen;
            switch (kind)
            {
                // the talks recorded (all / of quest arg)
                case "questtalks": return _session.QuestTalks.All.Count.ToString(CultureInfo.InvariantCulture);
                case "questtalk":
                    return _session.QuestTalks.Of(q => string.Equals(q.Name, arg, StringComparison.OrdinalIgnoreCase)).Count.ToString(CultureInfo.InvariantCulture);
                // mapfilter:name → on / off (button shown), hidden (not on this view's bar or no map), '-' without a map view
                case "mapfilter":
                {
                    var f = ParseMapFilter(arg);
                    if (f == null) return "unknown filter '" + arg + "'";
                    bool visible;
                    if (map != null) visible = map.FilterVisible(f.Value);
                    else if (quests != null) visible = quests.FilterVisible(f.Value);
                    else return "-";
                    return visible ? (MapFilters.IsOn(f.Value) ? "on" : "off") : "hidden";
                }
                case "mapnames": return map != null ? map.NamesShown.ToString(CultureInfo.InvariantCulture) : "-";
                case "mapnamesnudged": return map != null ? map.NamesNudged.ToString(CultureInfo.InvariantCulture) : "-";
                case "mapnamesoverlap": return map != null ? map.NamesOverlapping.ToString(CultureInfo.InvariantCulture) : "-";
                case "mapquestmarks":
                    return map != null ? map.QuestTalksShown.ToString(CultureInfo.InvariantCulture)
                        : quests != null ? quests.QuestTalksShown.ToString(CultureInfo.InvariantCulture) : "-";
                case "mapquestselected": return quests != null ? quests.QuestTalksSelected.ToString(CultureInfo.InvariantCulture) : "-";
                case "mapcompanions":
                    return map != null ? map.CompanionsShown.ToString(CultureInfo.InvariantCulture)
                        : quests != null ? quests.CompanionsShown.ToString(CultureInfo.InvariantCulture) : "-";
                case "mapfit": return quests != null ? quests.MapFitted.ToString() : "-";
                default: return "-";
            }
        }
    }
}
#endif
