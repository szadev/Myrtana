#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Linq;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-88 ops and probes (Docs/dev-scenarios.md): the port option "Cook on any fire" (<see cref="CookFires"/>), frying on
    /// the open fry page, the HUD focus name and the objects whose focus name has no text.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        bool? _cookFireBefore; // fix-88: the "Cook on any fire" option before a scenario's 'cookfire'

        /// <summary>'cookfire on=0|1': the option for this scenario only (the stored value comes back at its end).</summary>
        internal void SetCookFire(bool on)
        {
            _cookFireBefore ??= PlayerInteraction.CookOnAnyFire;
            PlayerInteraction.CookOnAnyFire = on;
            Log($"cookfire {(on ? "yes" : "no (original)")}");
        }

        void RestoreCookFire()
        {
            if (_cookFireBefore == null) return;
            PlayerInteraction.CookOnAnyFire = _cookFireBefore.Value;
            _cookFireBefore = null;
        }

        /// <summary>
        /// 'fry [item=It_Meat_Raw] [n=1]': on the open fry page (G3 OnPlayerFryStack) fries n of the item's stack (the amount
        /// dialog is confirmed with n when the stack holds more).
        /// </summary>
        System.Collections.IEnumerator FryStep(ScenarioStep st)
        {
            var page = G3TransformScreen.Current;
            var screens = FindAnyObjectByType<GameScreens>();
            if (page == null || screens == null || screens.Current != page) { Fail("fry: no fry page open"); yield break; }
            string item = st.Get("item", "It_Meat_Raw");
            var services = _session.Crafting;
            var candidates = services?.Crafting.FryCandidates();
            int index = candidates == null ? -1 : candidates.ToList().FindIndex(c => c.Item == item);
            if (index < 0) { Fail($"fry: {item} is not on the page"); yield break; }
            int n = Mathf.Max(1, st.Int("n", 1));
            int before = _session.Items.Inventories.Of(Myrtana.Core.ActorId.Player).Count(item);
            if (before > 1)
            {
                services.Crafting.Fry(item, Mathf.Min(n, before), out _); // the amount dialog's OK with n
            }
            else page.Transform(index);
            yield return null;
            Log($"fry {item}: {before} -> {_session.Items.Inventories.Of(Myrtana.Core.ActorId.Player).Count(item)}");
        }

        /// <summary>The HUD's focus name text ("-" = no focus).</summary>
        string FocusNameProbe() =>
            FindAnyObjectByType<G3Hud>() is G3Hud hud && hud.FocusTracker != null && hud.FocusTracker.HasFocus
                ? hud.FocusTracker.Describe(_session) is var t && string.IsNullOrEmpty(t.Name) ? "(none)" : t.Name
                : "-";

        /// <summary>The HUD focus name is a raw id: an entity name ("G3_Object_…") or the localizer's "[FO_…]".</summary>
        bool FocusNameRaw()
        {
            string name = FocusNameProbe();
            return name.Contains("G3_") || name.Contains("FO_") || name.StartsWith("[");
        }

        /// <summary>Interactive objects whose FO_ string is missing (they show no focus name, fix-88).</summary>
        string UnresolvedFocusNames() =>
            _session.Content.Database.All<InteractiveObjectDef>().Count(o => !_session.Localizer.TryText(o.FocusName, out _)).ToString();

        /// <summary>Cooking fires of the content (fix-88).</summary>
        string CookFireCount() => _session.Content.Database.All<InteractiveObjectDef>().Count(CookFires.IsCookFire).ToString();
    }
}
#endif
