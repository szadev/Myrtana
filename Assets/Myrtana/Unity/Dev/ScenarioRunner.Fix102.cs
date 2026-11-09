#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-102 ops and probes (Docs/dev-scenarios.md): the map page's chest filter (fix-116, <see cref="LockedChestMapMarks.Shown"/>;
    /// fix-126: the chest button of the filter bar, <see cref="SetMapFilter"/>),
    /// a locked chest to try, and the marks recorded / drawn on the map page.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        string _fix102Chest;       // fix-102: the chest 'lockedchest' chose (object id)
        InteractiveObjects _pickObjects;                              // fix-121: where OnPicked is hooked
        readonly List<UnlockResult> _picks = new List<UnlockResult>(); // fix-121: the pick attempts on that chest

        /// <summary>
        /// 'chestfilter on=0|1': the map page's chest filter for this scenario (the stored value comes back at its end). With
        /// the map page open it clicks the page's toggle (the player's path) when the state differs.
        /// </summary>
        internal void SetChestFilter(bool on)
        {
            SetMapFilter(Myrtana.Ui.Skin.MapFilter.Chests, on); // fix-126: the chest button of the map views' filter bar
            Log($"chestfilter {(on ? "on (marks shown)" : "off (hidden)")}");
        }

        void RestoreChestMarkers()
        {
            if (_pickObjects != null) _pickObjects.Picked -= OnPicked;
            _pickObjects = null;
            _picks.Clear();
        }

        /// <summary>
        /// 'lockedchest [difficulty=1] [maxdist=3000]': the nearest chest in the world whose lock is still shut, has that
        /// picking difficulty and no key (so a lockpick and the perks open it); the hero is put 1.5 m from it, facing it.
        /// Sets $chest (its entity name, for obj: targets) and $chestid.
        /// </summary>
        System.Collections.IEnumerator LockedChestStep(ScenarioStep st)
        {
            int difficulty = st.Int("difficulty", 1);
            float maxDist = st.Float("maxdist", 3000f);
            var objects = _session.Interaction.Objects;
            var hp = Hero.transform.position;
            var chest = _session.Content.Database.All<InteractiveObjectDef>()
                                .Where(o => o.Kind == InteractiveKind.Chest && o.Container != null && o.Lock != null && o.Lock.Difficulty == difficulty &&
                                            string.IsNullOrEmpty(o.Lock.Key) && o.Position != null && objects.IsInWorld(o) && objects.IsLocked(o.Id))
                                .Select(o => (o, d: (new Vector3(o.Position[0], o.Position[1], o.Position[2]) - hp).magnitude))
                                .Where(x => x.d <= maxDist).OrderBy(x => x.d).Select(x => x.o).FirstOrDefault();
            if (chest == null) { Fail($"lockedchest: no locked chest of difficulty {difficulty} within {maxDist:0} m"); yield break; }
            // fix-105 (r40: the dev start hero knows Perk_LockPick_1 and carries 3 lockpicks, so the lock let him pick it: verdict
            // Pick, no mark): refused=1 (default) takes his lockpicks, the lock then refuses him (NeedLockpick)
            if (st.Bool("refused", true))
            {
                var locks = _session.Content.Database.TryGet(LockRules.DefinitionId, out LockRules lr) ? lr : LockRules.Default;
                var inv = _session.Items.Inventories;
                int had = inv.Of(ActorId.Player).Count(locks.LockpickItem);
                if (had > 0)
                {
                    inv.Consume(ActorId.Player, locks.LockpickItem, had);
                    Log($"lockedchest: took the hero's {had} {locks.LockpickItem}");
                }
            }
            _fix102Chest = chest.Id;
            if (_pickObjects != null) _pickObjects.Picked -= OnPicked;
            _pickObjects = objects;
            _pickObjects.Picked += OnPicked;
            _picks.Clear();
            _vars["chest"] = chest.Name;
            _vars["chestid"] = chest.Id;
            var at = new Vector3(chest.Position[0], chest.Position[1], chest.Position[2]);
            string Xyz(Vector3 v) => string.Join(",", new[] { v.x, v.y, v.z }.Select(f => f.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
            // to the chest first (streaming), then beside it: 'obj:' takes the nearest object of the name (this one: pinned, fix-105)
            yield return TeleportStep(new ScenarioStep("tp", new Dictionary<string, string> { ["target"] = Xyz(at) }, "tp " + Xyz(at)));
            _pinnedObject = chest;
            // fix-105 (r41: 'no free NavMesh spot 1.5 m from it' and the hero stood on the chest): the cell NavMesh streams in
            // after the first tp; the stand spot needs it
            for (float t0 = Time.time; Time.time - t0 < 5f && !UnityEngine.AI.NavMesh.SamplePosition(at, out _, 2f, UnityEngine.AI.NavMesh.AllAreas);)
                yield return new WaitForSeconds(0.25f);
            yield return TeleportStep(new ScenarioStep("tp", new Dictionary<string, string> { ["target"] = "obj:" + chest.Name, ["dist"] = "1.5" },
                                                       "tp obj:" + chest.Name));
            Face(at + Vector3.up * 0.5f);
            yield return null;
            Log($"lockedchest {chest.Name} ({chest.Id}) at {Xyz(at)}: {objects.CheckLock(chest.Id)}");
        }

        /// <summary>A loaded game has its own interactive objects: the pick hook moves to them (the counts stay).</summary>
        void RehookPicks()
        {
            if (_pickObjects == null) return;
            _pickObjects.Picked -= OnPicked;
            _pickObjects = _session.Interaction.Objects;
            _pickObjects.Picked += OnPicked;
        }

        void OnPicked(string id, UnlockResult result)
        {
            if (id == _fix102Chest) _picks.Add(result);
        }

        /// <summary>
        /// 'relockchest [clear=1]' (fix-121): the chosen chest's lock shut again, so the next 'use' is a fresh pick attempt;
        /// clear=1 forgets the attempts counted so far.
        /// </summary>
        void RelockChest(bool clear)
        {
            if (_fix102Chest == null) { Fail("relockchest: no chest ('lockedchest' first)"); return; }
            _session.Interaction.Objects.ResetLock(_fix102Chest);
            if (clear) _picks.Clear();
            Log($"relockchest {_fix102Chest}: locked {_session.Interaction.Objects.IsLocked(_fix102Chest)}{(clear ? ", attempts cleared" : "")}");
        }

        static bool IsFix102Probe(string kind) => kind == "chestmarks" || kind == "chestmarked" || kind == "chestlocked" || kind == "mapchestmarks"
                                                  || kind == "mapchestgroups" || kind == "mapchestfilter" || kind == "picks" || kind == "pickbreaks"
                                                  || kind == "pickopens" || kind == "pickrolls" || kind == "pickchance" || kind == "pickodd";

        string Fix102Probe(string kind)
        {
            var marks = _session.Interaction.ChestMarks;
            switch (kind)
            {
                case "chestmarks": return marks.Visible.Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "chestmarked": return (_fix102Chest != null && marks.Contains(_fix102Chest) && marks.Visible.Any(m => m.Id == _fix102Chest)).ToString();
                case "chestlocked": return (_fix102Chest != null && _session.Interaction.Objects.IsLocked(_fix102Chest)).ToString();
                // fix-121: the pick attempts on that chest since 'lockedchest' (all / broken / opened) and their rolls in order
                case "picks": return _picks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "pickbreaks": return _picks.Count(p => p.Broke).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "pickopens": return _picks.Count(p => p.Opened).ToString(System.Globalization.CultureInfo.InvariantCulture);
                // the break chances the attempts had (distinct, '.'-joined; CP 1.75 difficulty 1 with Perk_LockPick_1: 29)
                case "pickchance": return _picks.Count == 0 ? "-" : string.Join(".", _picks.Select(p => p.BreakChance).Distinct()
                                                                                   .Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                // a break that contradicts its roll (broke below the threshold or opened at / above it): must stay 0
                case "pickodd": return _picks.Count(p => p.Roll >= 0 && p.Broke != (p.Roll >= 100 - p.BreakChance))
                                             .ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "pickrolls": return _picks.Count == 0 ? "-" : string.Join(".", _picks.Select(p => p.Roll.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                case "mapchestmarks":
                case "mapchestgroups":
                case "mapchestfilter":
                    if (!(FindAnyObjectByType<GameScreens>() is GameScreens screens && screens.Current is G3MapScreen map)) return "-";
                    if (kind == "mapchestfilter") return map.ChestFilterVisible ? (LockedChestMapMarks.Shown ? "on" : "off") : "hidden";
                    return (kind == "mapchestmarks" ? map.LockedChestMarksShown : map.LockedChestGroupsShown)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: return "-";
            }
        }
    }
}
#endif
