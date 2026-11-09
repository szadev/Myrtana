#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using Myrtana.Rules;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-109 op (Docs/dev-scenarios.md): the [owner] option "Shield protects from beasts" (<see cref="ShieldCreatureParade"/>)
    /// for one scenario.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        bool? _shieldBeastsBefore; // fix-109: the session's option before a scenario's 'shieldbeasts'

        /// <summary>'shieldbeasts on=0|1': the option for this scenario only (the stored value comes back at its end).</summary>
        internal void SetShieldBeasts(bool on)
        {
            var port = _session?.Combat.Port;
            if (port == null) { Fail("shieldbeasts: no session"); return; }
            _shieldBeastsBefore ??= port.ShieldBlocksCreatures;
            port.ShieldBlocksCreatures = on;
            Log($"shieldbeasts {(on ? "yes" : "no (original)")}");
        }

        void RestoreShieldBeasts()
        {
            if (_shieldBeastsBefore == null) return;
            var port = _session?.Combat.Port;
            if (port != null) port.ShieldBlocksCreatures = _shieldBeastsBefore.Value;
            _shieldBeastsBefore = null;
        }
    }
}
#endif
