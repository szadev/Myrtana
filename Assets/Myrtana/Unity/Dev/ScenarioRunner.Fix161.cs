#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-161 op (Docs/dev-scenarios.md): the [owner] option "Freeing by force counts"
    /// (<see cref="Myrtana.Story.ForcedReleases.Enabled"/>) for one scenario.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        bool? _forcedReleaseBefore; // fix-161: the session's option before a scenario's 'forcedrelease'

        /// <summary>'forcedrelease on=0|1': the option for this scenario only (the stored value comes back at its end).</summary>
        internal void SetForcedRelease(bool on)
        {
            var rules = _session?.ForcedReleases;
            if (rules == null) { Fail("forcedrelease: no session"); return; }
            _forcedReleaseBefore ??= rules.Enabled;
            rules.Enabled = on;
            Log($"forcedrelease {(on ? "yes" : "no (original)")}");
        }

        void RestoreForcedRelease()
        {
            if (_forcedReleaseBefore == null) return;
            var rules = _session?.ForcedReleases;
            if (rules != null) rules.Enabled = _forcedReleaseBefore.Value;
            _forcedReleaseBefore = null;
        }
    }
}
#endif
