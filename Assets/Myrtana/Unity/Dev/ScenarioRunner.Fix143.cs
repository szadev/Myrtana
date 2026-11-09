#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-143 op (Docs/dev-scenarios.md): the [owner] option "Late trophies" (<see cref="Myrtana.Rules.NpcLoot.LateTrophies"/>)
    /// for one scenario.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        bool? _lateTrophiesBefore; // fix-143: the session's option before a scenario's 'latetrophies'

        /// <summary>'latetrophies on=0|1': the option for this scenario only (the stored value comes back at its end).</summary>
        internal void SetLateTrophies(bool on)
        {
            var loot = _session?.Items.NpcLoot;
            if (loot == null) { Fail("latetrophies: no session"); return; }
            _lateTrophiesBefore ??= loot.LateTrophies;
            loot.LateTrophies = on;
            Log($"latetrophies {(on ? "yes" : "no (original)")}");
        }

        void RestoreLateTrophies()
        {
            if (_lateTrophiesBefore == null) return;
            var loot = _session?.Items.NpcLoot;
            if (loot != null) loot.LateTrophies = _lateTrophiesBefore.Value;
            _lateTrophiesBefore = null;
        }
    }
}
#endif
